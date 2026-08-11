using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ArenaGuard.Arenas;
using ArenaGuard.Challenges;
using ArenaGuard.Config;
using ArenaGuard.Domain;
using ArenaGuard.Networking;
using ArenaGuard.Persistence;
using ArenaGuard.Rules;
using ArenaGuard.Sessions;
using ArenaGuard.UI;
using ArenaGuard.World;
using HarmonyLib;
using Jotunn.Managers;
using UnityEngine;

namespace ArenaGuard.Runtime
{
    /// <summary>
    /// Main-thread bridge between the deterministic session engine and live Valheim objects.
    /// Network and engine callbacks are composed after their modules initialize.
    /// </summary>
    internal static class ArenaServerRuntime
    {
        internal const string EnemyArenaZdoKey = "arenaguard.arena_id";
        internal const string EnemySessionZdoKey = "arenaguard.session_id";
        internal const string EnemyIdZdoKey = "arenaguard.enemy_id";

        private static readonly MethodInfo MonsterAiWakeupMethod =
            AccessTools.Method(typeof(MonsterAI), "Wakeup", Type.EmptyTypes);
        private static readonly MethodInfo MonsterAiSetTargetMethod =
            AccessTools.Method(typeof(MonsterAI), "SetTarget", new[] { typeof(Character) });
        private static readonly MethodInfo PlayerUpdateFoodMethod =
            AccessTools.Method(typeof(Player), "UpdateFood", new[] { typeof(float), typeof(bool) });
        private static readonly FieldInfo TerminalCheatField =
            AccessTools.Field(typeof(Terminal), "m_cheat");

        private static readonly Dictionary<long, ArenaRole> Roles = new Dictionary<long, ArenaRole>();
        private static readonly Dictionary<string, ArenaEnemyHandle> Enemies =
            new Dictionary<string, ArenaEnemyHandle>(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> LastSpawnMarkerBySessionId =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly System.Random SpawnRandom = new System.Random();
        private static readonly Dictionary<long, PlayerResourceSnapshot> ResourceSnapshots =
            new Dictionary<long, PlayerResourceSnapshot>();
        private static readonly Dictionary<long, PendingRestore> PendingRestores =
            new Dictionary<long, PendingRestore>();
        private static readonly HashSet<string> ClientFoodPrompts =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, ClientPreparationState> ClientPreparations =
            new Dictionary<string, ClientPreparationState>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> ClientQueuePromptSessions =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly HashSet<string> ClientRestoredSnapshots =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, ArenaClientSnapshot> ClientArenaSnapshots =
            new Dictionary<string, ArenaClientSnapshot>(StringComparer.Ordinal);
        private static readonly Dictionary<string, DateTime> ClientCombatStartArrivalRetryUtc =
            new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private static readonly List<string> StaleClientCombatStartArrivalArenaIds = new List<string>();
        private static readonly Dictionary<long, DateTime> CombatStartMoveRetryUtc =
            new Dictionary<long, DateTime>();
        private static readonly Dictionary<long, CombatBoundaryState> CombatBoundaryStates =
            new Dictionary<long, CombatBoundaryState>();
        private static readonly HashSet<long> ActiveCombatantIds = new HashSet<long>();
        private static readonly HashSet<long> AwaitingCombatStartIds = new HashSet<long>();
        private static readonly List<long> StaleCombatantIds = new List<long>();
        private static readonly TimeSpan CombatBoundaryPopupInterval = TimeSpan.FromSeconds(1);
        private static bool _initialized;
        private static long _loadedWorldUid;
        private static DateTime _nextCollisionRefreshUtc;
        private static DateTime _nextStateBroadcastUtc;
        private static float _nextClientCombatStartArrivalCheckTime;
        private static bool _clientAdminsMayModifyTerrain;
        private static bool _clientAdminsMayBuild;
        private static bool _clientAdminsMayPickupDroppedItems;
        private static ChallengeCatalog _catalog;
        private static ArenaSessionEngine _engine;

        private static bool AdminsMayModifyTerrain()
        {
            return ZNet.instance != null && ZNet.instance.IsServer()
                ? ArenaConfig.AllowAdminTerrainEditing?.Value == true
                : _clientAdminsMayModifyTerrain;
        }

        private static bool AdminsMayBuild()
        {
            return ZNet.instance != null && ZNet.instance.IsServer()
                ? ArenaConfig.AllowAdminBuilding?.Value == true
                : _clientAdminsMayBuild;
        }

        private static bool AdminsMayPickupDroppedItems()
        {
            return ZNet.instance != null && ZNet.instance.IsServer()
                ? ArenaConfig.AllowAdminDroppedItemPickup?.Value == true
                : _clientAdminsMayPickupDroppedItems;
        }

        private static bool AreLocalDevcommandsEnabled()
        {
            try
            {
                return TerminalCheatField != null && TerminalCheatField.GetValue(null) is bool enabled && enabled;
            }
            catch (Exception error)
            {
                Plugin.Debug("Could not read the local devcommands state: " + error.Message);
                return false;
            }
        }

        private static bool ResolveAdminPermission(ArenaAdminPermissionKind permission)
        {
            switch (permission)
            {
                case ArenaAdminPermissionKind.Terrain:
                    return AdminsMayModifyTerrain();
                case ArenaAdminPermissionKind.Building:
                    return AdminsMayBuild();
                case ArenaAdminPermissionKind.DroppedItemPickup:
                    return AdminsMayPickupDroppedItems();
                default:
                    return false;
            }
        }

        internal static bool RequestAdminCommand(string operation, string target, out string response)
        {
            ArenaAdminMutationKind kind;
            switch ((operation ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "status":
                    kind = ArenaAdminMutationKind.RequestAdminStatus;
                    break;
                case "abort":
                    kind = string.Equals(target?.Trim(), "all", StringComparison.OrdinalIgnoreCase)
                        ? ArenaAdminMutationKind.AbortAllArenas
                        : ArenaAdminMutationKind.AbortArena;
                    break;
                case "clearqueue":
                    kind = ArenaAdminMutationKind.ClearArenaQueue;
                    break;
                default:
                    response = "unknown operation. Use status, abort, or clearqueue.";
                    return false;
            }

            if ((kind == ArenaAdminMutationKind.AbortArena ||
                 kind == ArenaAdminMutationKind.ClearArenaQueue) && string.IsNullOrWhiteSpace(target))
            {
                response = "an arena ID/name or 'all' is required.";
                return false;
            }

            var mutation = new ArenaAdminMutation
            {
                Kind = kind,
                TargetId = target?.Trim() ?? string.Empty
            };
            bool localServer = ZNet.instance != null && ZNet.instance.IsServer();
            if (localServer)
            {
                Player local = Player.m_localPlayer;
                var context = new ArenaRequestContext
                {
                    PlayerId = local?.GetPlayerID() ?? 0L,
                    PlayerName = local?.GetPlayerName() ?? "Dedicated server console",
                    IsAdmin = local == null || ZNet.instance.LocalPlayerIsAdminOrHost(),
                    Position = local == null ? default(PositionData) : ToPositionData(local.transform.position)
                };
                ArenaRpcResult result = OnAdminMutationRequested(context, mutation);
                response = result.Message;
                return result.Success;
            }

            ArenaRpc.RequestAdminMutation(mutation);
            response = "request sent; the authenticated server result will appear on screen.";
            return true;
        }

        internal static void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            _catalog = new ChallengeCatalog();
            _engine = new ArenaSessionEngine(
                request => _catalog.BuildPlan(request, GetWorldUnlockedBiome()),
                () => DateTime.UtcNow,
                new ArenaSessionOptions
                {
                    QueueAcceptanceTimeout = TimeSpan.FromSeconds(Math.Max(1, ArenaConfig.QueueAcceptSeconds.Value)),
                    StagingTimeout = TimeSpan.FromSeconds(Math.Max(1, ArenaConfig.StagingTimeoutSeconds.Value)),
                    BoundaryGracePeriod = TimeSpan.FromSeconds(Math.Max(1, ArenaConfig.BoundaryGraceSeconds.Value)),
                    VictoryDisplayDuration = TimeSpan.FromSeconds(Math.Max(0, ArenaConfig.ResultsSeconds.Value))
                });

            ArenaRuleContext.IsProtectedPoint = position =>
                ArenaRegistry.FindProtectedArena(ToPositionData(position)) != null;
            ArenaRuleContext.IsActiveProtectedPoint = position =>
            {
                ArenaDefinition arena = ArenaRegistry.FindProtectedArena(ToPositionData(position));
                return arena != null && ArenaRegistry.TryGetActiveSession(arena.ArenaId, out _);
            };
            ArenaRuleContext.IsAdministrator = IsAdministrator;
            ArenaRuleContext.AdminsMayModifyTerrain = AdminsMayModifyTerrain;
            ArenaRuleContext.AdminsMayBuild = AdminsMayBuild;
            ArenaRuleContext.AdminsMayPickupDroppedItems = AdminsMayPickupDroppedItems;
            ArenaRuleContext.AreLocalDevcommandsEnabled = AreLocalDevcommandsEnabled;
            ArenaRuleContext.ResolveRole = ResolveRole;
            ArenaRuleContext.ResolveEnemyArenaId = ResolveEnemyArenaId;
            ArenaRuleContext.ResolveCombatant = ResolveCombatant;
            ArenaRuleContext.ReportLethalDamage = ReportLethalDamage;
            ArenaRuleContext.ReportArenaEnemyDeath = ReportArenaEnemyDeath;
            ArenaRuleContext.RegisterArenaSummon = RegisterArenaSummon;

            ArenaWorldObjects.LocalAdminResolver = IsLocalAdministrator;
            ArenaWorldObjects.ArenaAtPositionResolver = position =>
                ArenaRegistry.FindProtectedArena(ToPositionData(position))?.ArenaId;
            ArenaWorldObjects.ArenaDefinitionResolver = arenaId =>
                ArenaRegistry.TryGetArena(arenaId, out ArenaDefinition definition) ? definition : null;
            ArenaWorldObjects.PlacementRequested = RequestPlacementMutation;
            ArenaWorldObjects.CoreRemovalRequested = RequestCoreRemoval;
            ArenaWorldObjects.CoreActivated = RequestStaleCoreCleanup;
            ArenaWorldObjects.AdminMarkerRequested = RequestMarkerMutation;
            ArenaWorldObjects.AdminMarkerRemovalRequested = RequestMarkerRemoval;
            ArenaTeleporters.GateTravelRequested = (_, gateId, returning) =>
                ArenaRpc.RequestGateTravel(gateId, returning);
            ArenaTeleporters.GateConfigurationRequested = RequestGateConfiguration;

            ArenaUi.ChallengeRequested = ArenaRpc.RequestChallenge;
            ArenaUi.QueueAccepted = ArenaRpc.AcceptQueueCall;
            ArenaUi.ForfeitRequested = ArenaRpc.RequestForfeit;
            ArenaUi.LeaderboardRequested = (_, key) => ArenaRpc.RequestLeaderboard(key);
            ArenaUi.AdminMutationRequested = RequestUiAdminMutation;
            ArenaUi.AdminPermissionResolver = ResolveAdminPermission;
            ArenaUi.CreatureListResolver = () => _catalog.GetRoster();
            ArenaUi.FoodPreparationConfirmed = ConfirmLocalFoodPreparation;
            ArenaUi.FoodPreparationCancelled = ArenaRpc.RequestForfeit;

            ArenaRpc.Configure(new ArenaRpcCallbacks
            {
                ChallengeRequested = OnChallengeRequested,
                QueueCallAccepted = OnQueueCallAccepted,
                ForfeitRequested = OnForfeitRequested,
                LethalDamageReported = OnLethalDamageReported,
                CombatStartArrived = OnCombatStartArrived,
                AdminMutationRequested = OnAdminMutationRequested,
                GateTravelAuthorizing = OnGateTravelAuthorizing,
                GateTravelCompleted = OnGateTravelCompleted,
                ClientGateTravelAuthorized = OnClientGateTravelAuthorized,
                ResourceSnapshotReceived = OnResourceSnapshotReceived,
                ClientResourceRestoreRequested = OnClientResourceRestoreRequested,
                ResourceRestoreCompleted = OnResourceRestoreCompleted,
                ClientMoveRequested = OnClientMoveRequested,
                SnapshotRequested = BuildClientSnapshot,
                LeaderboardRequested = GetCurrentRosterLeaderboard,
                ClientSnapshotReceived = OnClientSnapshotReceived,
                ClientArenaRemoved = OnClientArenaRemoved,
                ClientGateConfigurationReceived = gate => ArenaTeleporters.ApplyGateConfiguration(
                    gate.GateId,
                    gate.ArenaId,
                    gate.DisplayName,
                    gate.IsFallbackEntrance),
                ClientLeaderboardReceived = OnClientLeaderboardReceived,
                ClientActionResultReceived = OnClientActionResultReceived,
                ServerPeerConnected = _ => BroadcastAllArenaStates()
            });

            _initialized = true;
        }

        private static void LoadWorld(long worldUid)
        {
            if (worldUid == 0L)
            {
                return;
            }

            if (_loadedWorldUid != 0L && ZNet.instance != null && ZNet.instance.IsServer())
            {
                SaveWorldState();
            }

            _loadedWorldUid = worldUid;
            Roles.Clear();
            Enemies.Clear();
            LastSpawnMarkerBySessionId.Clear();
            ResourceSnapshots.Clear();
            PendingRestores.Clear();
            ClientFoodPrompts.Clear();
            ClientPreparations.Clear();
            ClientQueuePromptSessions.Clear();
            ClientRestoredSnapshots.Clear();
            ClientArenaSnapshots.Clear();
            _clientAdminsMayModifyTerrain = false;
            _clientAdminsMayBuild = false;
            _clientAdminsMayPickupDroppedItems = false;
            ClientCombatStartArrivalRetryUtc.Clear();
            StaleClientCombatStartArrivalArenaIds.Clear();
            CombatStartMoveRetryUtc.Clear();
            CombatBoundaryStates.Clear();
            ActiveCombatantIds.Clear();
            AwaitingCombatStartIds.Clear();
            StaleCombatantIds.Clear();
            _nextClientCombatStartArrivalCheckTime = 0f;

            if (!ZNet.instance.IsServer())
            {
                ArenaRegistry.Clear();
                Plugin.Debug("Prepared client arena state for world " + worldUid + ".");
                return;
            }

            PersistedWorldState state = ArenaStore.Load(worldUid);
            ArenaRegistry.Replace(state);
            _engine.RecoverAfterRestart(state);
            SyncRegistryFromEngine();
            ProcessEngineEffects();
            SaveWorldState();
            BroadcastAllArenaStates();
            Plugin.Log?.LogInfo(Plugin.PluginName + " is ready for world " + worldUid + ".");
        }

        private static ArenaRpcResult OnChallengeRequested(ArenaRequestContext context, ChallengeRequest request)
        {
            try
            {
                if (!_initialized || _engine == null ||
                    !ArenaRegistry.TryGetArena(request.ArenaId, out ArenaDefinition arena) ||
                    !arena.Enabled || arena.Markers?.EnemySpawnPositions?.Count != 4)
                {
                    return ArenaRpcResult.Rejected("That arena is not fully configured.");
                }
                if (!ArenaRegistry.IsWithinCombatRadius(arena, arena.Markers.CombatantStartPosition))
                {
                    return ArenaRpcResult.Rejected(
                        "Combat Start is outside the combat radius. An admin must move it inside the blue ring.");
                }

                string originGateId = ArenaRegistry.TryGetRoute(context.PlayerId, out PlayerArenaRoute route)
                    ? route.OriginGateId
                    : string.Empty;
                _engine.Enqueue(new QueueEntry
                {
                    Request = request,
                    OriginGateId = originGateId,
                    EnqueuedUtc = DateTime.UtcNow
                });
                ProcessEngineEffects();
                return ArenaRpcResult.Accepted("Joined the " + arena.DisplayName + " queue.");
            }
            catch (Exception error)
            {
                Plugin.Log?.LogWarning("Challenge request rejected: " + error.Message);
                return ArenaRpcResult.Rejected(error.Message);
            }
        }

        private static ArenaRpcResult OnQueueCallAccepted(ArenaRequestContext context)
        {
            bool accepted = _engine != null && _engine.AcceptTurn(context.PlayerId);
            ProcessEngineEffects();
            return accepted
                ? ArenaRpcResult.Accepted("Arena turn accepted.")
                : ArenaRpcResult.Rejected("Your arena call is no longer active.");
        }

        private static ArenaRpcResult OnForfeitRequested(ArenaRequestContext context)
        {
            bool accepted = _engine != null && _engine.Forfeit(context.PlayerId);
            ProcessEngineEffects();
            return accepted
                ? ArenaRpcResult.Accepted("Arena challenge forfeited.")
                : ArenaRpcResult.Rejected("You do not have an active arena challenge.");
        }

        private static ArenaRpcResult OnLethalDamageReported(ArenaRequestContext context)
        {
            bool accepted = _engine != null && _engine.ReportLethalDamage(context.PlayerId);
            ProcessEngineEffects();
            return accepted
                ? ArenaRpcResult.Accepted("Arena defeat recorded.")
                : ArenaRpcResult.Rejected("You do not have an active arena challenge.");
        }

        private static ArenaRpcResult OnCombatStartArrived(ArenaRequestContext context)
        {
            ArenaSession session = _engine?.GetActiveSessions().FirstOrDefault(candidate =>
                candidate?.Request?.PlayerId == context.PlayerId);
            if (session == null)
            {
                return ArenaRpcResult.Rejected("No staged arena challenge is awaiting arrival.");
            }

            if (session.Phase == SessionPhase.Countdown ||
                session.Phase == SessionPhase.Fighting ||
                session.Phase == SessionPhase.Intermission)
            {
                return ArenaRpcResult.Accepted();
            }

            if (session.Phase != SessionPhase.Staging || session.ResourceSnapshot == null ||
                !ArenaRegistry.TryGetArena(session.ArenaId, out ArenaDefinition arena) || arena.Markers == null)
            {
                return ArenaRpcResult.Rejected("The staged challenge is not ready for Combat Start.");
            }

            Player player = FindPlayer(context.PlayerId);
            if (player == null)
            {
                return ArenaRpcResult.Rejected("The staged combatant is unavailable.");
            }

            // The client chooses no destination. It acknowledges the exact marker
            // previously commanded by the server after Valheim finishes TeleportTo.
            // Mirror that known point into the server representation so a stationary
            // remote player does not need to move to publish a transform update.
            player.transform.position = ToVector3(arena.Markers.CombatantStartPosition);
            CombatStartMoveRetryUtc.Remove(context.PlayerId);
            bool entered = _engine.EnterCombatFloor(context.PlayerId, session.ResourceSnapshot);
            ProcessEngineEffects();
            return entered
                ? ArenaRpcResult.Accepted()
                : ArenaRpcResult.Rejected("Combat Start confirmation expired.");
        }

        private static ArenaRpcResult OnAdminMutationRequested(
            ArenaRequestContext context,
            ArenaAdminMutation mutation)
        {
            if (!context.IsAdmin || mutation == null)
            {
                return ArenaRpcResult.Rejected("Only a server administrator may change an arena.");
            }

            try
            {
                bool changed;
                string arenaId = mutation.TargetId;
                switch (mutation.Kind)
                {
                    case ArenaAdminMutationKind.RegisterArena:
                        ArenaDefinition definition = MergeArenaMutation(mutation.Arena);
                        if (!Near(context.Position, definition.CorePosition, 12f) &&
                            !ArenaRegistry.TryGetArena(definition.ArenaId, out _))
                        {
                            return ArenaRpcResult.Rejected("Stand near the Arena Core to register it.");
                        }
                        if (definition.Markers != null &&
                            ArenaRegistry.MarkerPositionIsSet(definition.Markers.CombatantStartPosition) &&
                            !ArenaRegistry.IsWithinCombatRadius(definition, definition.Markers.CombatantStartPosition))
                        {
                            return ArenaRpcResult.Rejected(
                                "Combat Start must remain inside the blue combat-radius ring.");
                        }
                        changed = ArenaRegistry.RegisterArena(definition);
                        arenaId = definition.ArenaId;
                        break;
                    case ArenaAdminMutationKind.RemoveArena:
                        string removalArenaId = ArenaRegistry.NormalizeId(mutation.TargetId);
                        if (_engine.TryGetActiveSession(removalArenaId, out _) ||
                            _engine.GetQueueSnapshot().Any(entry => entry.Request.ArenaId == removalArenaId))
                        {
                            return ArenaRpcResult.Rejected("Finish the active challenge and empty its queue before removing the arena.");
                        }
                        if (ArenaRegistry.GetRouteSnapshot().Any(route => route.ArenaId == removalArenaId))
                        {
                            return ArenaRpcResult.Rejected("Return all arena visitors before removing the arena.");
                        }
                        changed = ArenaRegistry.RemoveArena(removalArenaId);
                        if (changed)
                        {
                            ArenaRpc.BroadcastArenaRemoval(removalArenaId);
                            SaveWorldState();
                            return ArenaRpcResult.Accepted("Arena removed.");
                        }
                        break;
                    case ArenaAdminMutationKind.SetMarkers:
                        if (!ArenaRegistry.TryGetArena(mutation.TargetId, out ArenaDefinition markerSetArena) ||
                            mutation.Markers == null ||
                            !ArenaRegistry.IsWithinCombatRadius(
                                markerSetArena,
                                mutation.Markers.CombatantStartPosition))
                        {
                            return ArenaRpcResult.Rejected(
                                "Combat Start must be inside the blue combat-radius ring.");
                        }
                        changed = ArenaRegistry.SetMarkers(mutation.TargetId, mutation.Markers);
                        arenaId = mutation.TargetId;
                        break;
                    case ArenaAdminMutationKind.SetMarker:
                        if (!Near(context.Position, mutation.MarkerPosition, 12f))
                        {
                            return ArenaRpcResult.Rejected("Stand near the marker to configure it.");
                        }
                        arenaId = ArenaRegistry.NormalizeId(mutation.TargetId);
                        if (arenaId == null || !ArenaRegistry.TryGetArena(arenaId, out ArenaDefinition markerArena))
                        {
                            return ArenaRpcResult.Rejected("Select a registered arena before placing markers.");
                        }
                        ArenaMarkerKind markerKind = (ArenaMarkerKind)mutation.MarkerKind;
                        if (markerKind == ArenaMarkerKind.CombatantStart &&
                            !ArenaRegistry.IsWithinCombatRadius(markerArena, mutation.MarkerPosition))
                        {
                            return ArenaRpcResult.Rejected(
                                "Combat Start must be inside the blue combat-radius ring.");
                        }
                        if (markerKind == ArenaMarkerKind.CombatantStart &&
                            ArenaRegistry.TryGetMarkerPosition(
                                arenaId,
                                ArenaMarkerKind.CombatantStart,
                                -1,
                                out PositionData existingCombatStart) &&
                            !SamePosition(existingCombatStart, mutation.MarkerPosition))
                        {
                            return ArenaRpcResult.Rejected(
                                "This arena already has a Combat Start. Remove it before placing another.");
                        }
                        changed = ArenaRegistry.SetMarker(
                            arenaId,
                            markerKind,
                            mutation.MarkerSlot,
                            mutation.MarkerPosition);
                        break;
                    case ArenaAdminMutationKind.RemoveMarker:
                        if (!Near(context.Position, mutation.MarkerPosition, 12f))
                        {
                            return ArenaRpcResult.Rejected("Stand near the marker to remove it.");
                        }
                        arenaId = ArenaRegistry.NormalizeId(mutation.TargetId);
                        if (arenaId == null || !ArenaRegistry.TryGetArena(arenaId, out _))
                        {
                            return ArenaRpcResult.Rejected("The marker's arena no longer exists.");
                        }
                        ArenaMarkerKind removedMarkerKind = (ArenaMarkerKind)mutation.MarkerKind;
                        if (ArenaRegistry.TryGetMarkerPosition(
                                arenaId,
                                removedMarkerKind,
                                mutation.MarkerSlot,
                                out PositionData savedMarkerPosition) &&
                            !SamePosition(savedMarkerPosition, mutation.MarkerPosition))
                        {
                            return ArenaRpcResult.Accepted(
                                "Duplicate beacon removed; the saved arena position was left unchanged.");
                        }
                        changed = ArenaRegistry.RemoveMarker(
                            arenaId,
                            removedMarkerKind,
                            mutation.MarkerSlot);
                        break;
                    case ArenaAdminMutationKind.RegisterGate:
                        ArenaGateDefinition gate = mutation.Gate;
                        if (gate == null || !Near(context.Position, gate.Position, 12f))
                        {
                            return ArenaRpcResult.Rejected("Stand near the Arena Gate to configure it.");
                        }
                        if (ArenaRegistry.GetGatesForArena(gate.ArenaId).Count == 0)
                        {
                            gate.IsFallbackEntrance = true;
                        }
                        changed = ArenaRegistry.RegisterGate(gate);
                        arenaId = gate.ArenaId;
                        if (changed)
                        {
                            foreach (ArenaGateDefinition configuredGate in ArenaRegistry.GetGatesForArena(gate.ArenaId))
                            {
                                ArenaRpc.BroadcastGateConfiguration(configuredGate);
                            }
                        }
                        break;
                    case ArenaAdminMutationKind.RemoveGate:
                        ArenaRegistry.TryGetGate(mutation.TargetId, out ArenaGateDefinition removedGate);
                        changed = ArenaRegistry.RemoveGate(mutation.TargetId);
                        arenaId = removedGate?.ArenaId;
                        break;
                    case ArenaAdminMutationKind.SetAdminTerrainPermission:
                        ArenaConfig.AllowAdminTerrainEditing.Value = mutation.Enabled;
                        ArenaConfig.Save();
                        BroadcastAllArenaStates();
                        return ArenaRpcResult.Accepted(
                            "Admin terrain editing " + (mutation.Enabled ? "enabled." : "disabled."));
                    case ArenaAdminMutationKind.SetAdminBuildingPermission:
                        ArenaConfig.AllowAdminBuilding.Value = mutation.Enabled;
                        ArenaConfig.Save();
                        BroadcastAllArenaStates();
                        return ArenaRpcResult.Accepted(
                            "Admin building and demolition " + (mutation.Enabled ? "enabled." : "disabled."));
                    case ArenaAdminMutationKind.SetAdminPickupPermission:
                        ArenaConfig.AllowAdminDroppedItemPickup.Value = mutation.Enabled;
                        ArenaConfig.Save();
                        BroadcastAllArenaStates();
                        return ArenaRpcResult.Accepted(
                            "Admin dropped-item pickup " + (mutation.Enabled ? "enabled." : "disabled."));
                    case ArenaAdminMutationKind.RequestAdminStatus:
                        return ArenaRpcResult.Accepted(BuildAdminStatus(mutation.TargetId));
                    case ArenaAdminMutationKind.AbortArena:
                        return AbortArenaForAdministration(mutation.TargetId, false);
                    case ArenaAdminMutationKind.AbortAllArenas:
                        return AbortArenaForAdministration("all", true);
                    case ArenaAdminMutationKind.ClearArenaQueue:
                        return ClearArenaQueueForAdministration(mutation.TargetId);
                    default:
                        return ArenaRpcResult.Rejected("Unknown arena administration request.");
                }

                if (!changed)
                {
                    return ArenaRpcResult.Rejected("The change conflicts with an existing arena or gate name.");
                }

                SaveWorldState();
                if (!string.IsNullOrWhiteSpace(arenaId))
                {
                    ArenaRpc.BroadcastArenaState(arenaId);
                }
                return ArenaRpcResult.Accepted("Arena configuration saved.");
            }
            catch (Exception error)
            {
                Plugin.Log?.LogWarning("Admin mutation rejected: " + error.Message);
                return ArenaRpcResult.Rejected(error.Message);
            }
        }

        private static ArenaRpcResult AbortArenaForAdministration(string target, bool all)
        {
            if (_engine == null)
            {
                return ArenaRpcResult.Rejected("Arena session service is not ready.");
            }

            List<ArenaDefinition> arenas = all
                ? ArenaRegistry.GetArenaSnapshot().ToList()
                : TryResolveAdminArena(target, out ArenaDefinition arena)
                    ? new List<ArenaDefinition> { arena }
                    : null;
            if (arenas == null || arenas.Count == 0)
            {
                return ArenaRpcResult.Rejected("No matching arena was found.");
            }

            int aborted = 0;
            int cleared = 0;
            foreach (ArenaDefinition candidate in arenas)
            {
                cleared += _engine.ClearQueue(candidate.ArenaId);
                if (_engine.AbortArena(candidate.ArenaId))
                {
                    aborted++;
                }
            }
            ProcessEngineEffects();
            SaveWorldState();
            return ArenaRpcResult.Accepted(
                "Emergency recovery: " + aborted + " challenge(s) stopped, " +
                cleared + " queued player(s) cleared.");
        }

        private static ArenaRpcResult ClearArenaQueueForAdministration(string target)
        {
            if (_engine == null)
            {
                return ArenaRpcResult.Rejected("Arena session service is not ready.");
            }

            int cleared = 0;
            if (string.Equals(target?.Trim(), "all", StringComparison.OrdinalIgnoreCase))
            {
                foreach (ArenaDefinition arena in ArenaRegistry.GetArenaSnapshot())
                {
                    cleared += _engine.ClearQueue(arena.ArenaId);
                }
            }
            else
            {
                if (!TryResolveAdminArena(target, out ArenaDefinition arena))
                {
                    return ArenaRpcResult.Rejected("No matching arena was found.");
                }
                cleared = _engine.ClearQueue(arena.ArenaId);
            }
            ProcessEngineEffects();
            SaveWorldState();
            return ArenaRpcResult.Accepted("Cleared " + cleared + " queued player(s).");
        }

        private static string BuildAdminStatus(string target)
        {
            if (_engine == null)
            {
                return "Arena session service is not ready.";
            }

            if (!string.IsNullOrWhiteSpace(target) &&
                !string.Equals(target.Trim(), "all", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryResolveAdminArena(target, out ArenaDefinition arena))
                {
                    return "No matching arena was found.";
                }
                _engine.TryGetActiveSession(arena.ArenaId, out ArenaSession active);
                int queued = _engine.GetQueueSnapshot().Count(entry =>
                    string.Equals(entry.Request.ArenaId, arena.ArenaId, StringComparison.OrdinalIgnoreCase));
                int enemies = Enemies.Values.Count(enemy =>
                    string.Equals(enemy.ArenaId, arena.ArenaId, StringComparison.OrdinalIgnoreCase));
                return arena.DisplayName + ": " + (arena.Enabled ? "enabled" : "disabled") +
                       ", phase=" + (active?.Phase.ToString() ?? "idle") +
                       ", fighter=" + (active?.Request?.PlayerName ?? "none") +
                       ", queue=" + queued + ", enemies=" + enemies + ".";
            }

            List<ArenaDefinition> arenas = ArenaRegistry.GetArenaSnapshot().ToList();
            int activeCount = _engine.GetActiveSessions().Count;
            int queuedCount = _engine.GetQueueSnapshot().Count;
            return Plugin.PluginName + " " + Plugin.PluginVersion + ": arenas=" + arenas.Count +
                   ", enabled=" + arenas.Count(arena => arena.Enabled) +
                   ", active=" + activeCount + ", queued=" + queuedCount +
                   ", enemies=" + Enemies.Count + ", pending restores=" + PendingRestores.Count + ".";
        }

        private static bool TryResolveAdminArena(string target, out ArenaDefinition arena)
        {
            arena = null;
            string clean = target?.Trim();
            if (string.IsNullOrWhiteSpace(clean))
            {
                return false;
            }
            string normalized = ArenaRegistry.NormalizeId(clean);
            if (normalized != null && ArenaRegistry.TryGetArena(normalized, out arena))
            {
                return true;
            }
            arena = ArenaRegistry.GetArenaSnapshot().FirstOrDefault(candidate =>
                candidate != null && string.Equals(
                    candidate.DisplayName?.Trim(), clean, StringComparison.OrdinalIgnoreCase));
            return arena != null;
        }

        private static ArenaRpcResult OnGateTravelAuthorizing(
            ArenaRequestContext context,
            ArenaGateDefinition gate,
            bool returning)
        {
            if (returning)
            {
                return ArenaRpcResult.Accepted();
            }

            bool recorded = ArenaRegistry.RecordRoute(new PlayerArenaRoute
            {
                PlayerId = context.PlayerId,
                ArenaId = gate.ArenaId,
                OriginGateId = gate.GateId,
                EnteredUtc = DateTime.UtcNow
            });
            if (!recorded || !SaveWorldState())
            {
                ArenaRegistry.ClearRoute(context.PlayerId);
                return ArenaRpcResult.Rejected("The arena route could not be saved.");
            }
            return ArenaRpcResult.Accepted();
        }

        private static ArenaRpcResult OnGateTravelCompleted(
            ArenaRequestContext context,
            ArenaGateTravelAuthorization authorization,
            bool accepted)
        {
            if (!accepted)
            {
                if (!authorization.Returning)
                {
                    ArenaRegistry.ClearRoute(context.PlayerId);
                    SaveWorldState();
                }
                return ArenaRpcResult.Rejected("Arena travel was cancelled.");
            }

            if (authorization.Returning)
            {
                _engine?.Disconnect(context.PlayerId);
                ProcessEngineEffects();
                ArenaRegistry.ClearRoute(context.PlayerId);
                SetRole(context.PlayerId, ArenaRole.Visitor);
            }
            else
            {
                SetRole(context.PlayerId, ArenaRole.Spectator);
            }

            SaveWorldState();
            ArenaRpc.BroadcastArenaState(authorization.ArenaId);
            return ArenaRpcResult.Accepted();
        }

        private static bool OnClientGateTravelAuthorized(ArenaGateTravelAuthorization authorization)
        {
            bool accepted = authorization != null &&
                ArenaTeleporters.ApplyAuthorizedGateTravel(authorization.Destination, authorization.RotationY);
            if (accepted && Player.m_localPlayer != null)
            {
                SetRole(Player.m_localPlayer.GetPlayerID(),
                    authorization.Returning ? ArenaRole.Visitor : ArenaRole.Spectator);
            }
            return accepted;
        }

        private static ArenaRpcResult OnResourceSnapshotReceived(
            ArenaRequestContext context,
            PlayerResourceSnapshot snapshot)
        {
            if (snapshot == null || snapshot.PlayerId != context.PlayerId || _engine == null)
            {
                return ArenaRpcResult.Rejected("The resource snapshot is invalid.");
            }

            IList<ArenaFoodDefinition> legalFoods = GetArenaFoods(null, false);
            var legalPrefabNames = new HashSet<string>(legalFoods.Select(food => food.PrefabName), StringComparer.Ordinal);
            if (!ArenaFoodSelectionPolicy.IsValid(snapshot.ArenaFoodPrefabNames, legalPrefabNames))
            {
                return ArenaRpcResult.Rejected("Choose exactly three valid arena foods before starting.");
            }

            bool accepted = _engine.SetResourceSnapshot(context.PlayerId, snapshot);
            if (accepted)
            {
                ResourceSnapshots[context.PlayerId] = snapshot;
            }
            ProcessEngineEffects();
            return accepted
                ? ArenaRpcResult.Accepted("Arena food prepared and pre-challenge resources saved.")
                : ArenaRpcResult.Rejected("The challenge is not waiting for a resource snapshot.");
        }

        private static bool OnClientResourceRestoreRequested(
            PlayerResourceSnapshot snapshot,
            SessionOutcome outcome)
        {
            if (snapshot == null || Player.m_localPlayer == null ||
                snapshot.PlayerId != Player.m_localPlayer.GetPlayerID())
            {
                return false;
            }

            string restoreKey = snapshot.PlayerId + ":" + snapshot.CapturedUtc.Ticks + ":" + (int)outcome;
            if (ClientRestoredSnapshots.Contains(restoreKey))
            {
                return true;
            }
            ResourceSnapshots[snapshot.PlayerId] = snapshot;
            bool restored = RestoreResources(snapshot.PlayerId, outcome);
            if (restored)
            {
                foreach (string arenaId in ClientPreparations.Where(pair =>
                             pair.Value?.Snapshot?.PlayerId == snapshot.PlayerId).Select(pair => pair.Key).ToList())
                {
                    ClientPreparations.Remove(arenaId);
                    ClientFoodPrompts.Remove(arenaId);
                    ArenaUi.CloseFoodPreparation(arenaId);
                }
                ClientRestoredSnapshots.Add(restoreKey);
            }
            return restored;
        }

        private static void OnResourceRestoreCompleted(
            ArenaRequestContext context,
            string token,
            bool success)
        {
            if (success)
            {
                PendingRestores.TryGetValue(context.PlayerId, out PendingRestore completed);
                PendingRestores.Remove(context.PlayerId);
                ResourceSnapshots.Remove(context.PlayerId);
                SaveWorldState();
                if (completed?.Session != null)
                {
                    ArenaRpc.BroadcastArenaState(completed.Session.ArenaId);
                }
                return;
            }
            Plugin.Log?.LogWarning("Resource restoration was rejected for player " + context.PlayerId + ".");
        }

        private static bool OnClientMoveRequested(PositionData destination, float rotationY)
        {
            Player player = Player.m_localPlayer;
            return player != null && player.TeleportTo(
                ToVector3(destination),
                Quaternion.Euler(0f, rotationY, 0f),
                false);
        }

        private static void OnClientSnapshotReceived(ArenaClientSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            ArenaDefinition localArena = new ArenaDefinition
            {
                ArenaId = snapshot.ArenaId,
                DisplayName = string.IsNullOrWhiteSpace(snapshot.ArenaName) ? "Arena" : snapshot.ArenaName,
                CorePosition = snapshot.CorePosition,
                CombatRadius = snapshot.CombatRadius,
                ProtectedRadius = snapshot.ProtectedRadius,
                Markers = CopyMarkers(snapshot.Markers),
                Enabled = snapshot.Markers?.EnemySpawnPositions?.Count == 4,
                Revision = 1
            };
            try
            {
                ArenaRegistry.RegisterArena(localArena);
                ArenaWorldObjects.ApplyArenaConfiguration(snapshot.ArenaId, snapshot.ArenaName);
            }
            catch (Exception error)
            {
                Plugin.Log?.LogWarning("Ignored malformed arena snapshot: " + error.Message);
                return;
            }

            ClientArenaSnapshots[snapshot.ArenaId] = snapshot;
            _clientAdminsMayModifyTerrain = snapshot.AdminsMayModifyTerrain;
            _clientAdminsMayBuild = snapshot.AdminsMayBuild;
            _clientAdminsMayPickupDroppedItems = snapshot.AdminsMayPickupDroppedItems;

            Player local = Player.m_localPlayer;
            if (local != null)
            {
                long playerId = local.GetPlayerID();
                bool isCalledForLocal = snapshot.CombatantPlayerId == playerId &&
                                        snapshot.Phase == SessionPhase.Called;
                bool isCombatant = snapshot.CombatantPlayerId == playerId &&
                                   (snapshot.Phase == SessionPhase.Staging ||
                                    snapshot.Phase == SessionPhase.Countdown ||
                                    snapshot.Phase == SessionPhase.Fighting ||
                                    snapshot.Phase == SessionPhase.Intermission);
                RecomputeClientRole();

                ClientQueuePromptSessions.TryGetValue(snapshot.ArenaId, out string displayedSessionId);
                if (ArenaQueuePromptPolicy.ShouldOpen(
                        isCalledForLocal, displayedSessionId, snapshot.SessionId) &&
                    ArenaUi.ShowQueueCall(
                        snapshot.ArenaId, snapshot.SessionId, Math.Max(1, snapshot.CountdownSeconds)))
                {
                    ClientQueuePromptSessions[snapshot.ArenaId] = snapshot.SessionId;
                }
                else if (!isCalledForLocal)
                {
                    ClientQueuePromptSessions.Remove(snapshot.ArenaId);
                    ArenaUi.CloseQueuePromptForArena(snapshot.ArenaId);
                }

                if (snapshot.CombatantPlayerId == playerId && snapshot.PreparationComplete &&
                    ClientPreparations.TryGetValue(snapshot.ArenaId, out ClientPreparationState completedPreparation))
                {
                    completedPreparation.Accepted = true;
                    ClientFoodPrompts.Remove(snapshot.ArenaId);
                    ArenaUi.CloseFoodPreparation(snapshot.ArenaId);
                }

                if (isCombatant && snapshot.Phase == SessionPhase.Staging && !snapshot.PreparationComplete)
                {
                    if (ClientPreparations.TryGetValue(snapshot.ArenaId, out ClientPreparationState pending))
                    {
                        if (!pending.Accepted && DateTime.UtcNow >= pending.SubmittedUtc.AddSeconds(3))
                        {
                            RollBackLocalPreparation(snapshot.ArenaId, "Arena preparation was not accepted. Choose your food again.");
                        }
                    }
                    if (!ClientPreparations.ContainsKey(snapshot.ArenaId) &&
                        !ClientFoodPrompts.Contains(snapshot.ArenaId) &&
                        OpenLocalFoodPreparation(snapshot))
                    {
                        ClientFoodPrompts.Add(snapshot.ArenaId);
                    }
                }
                else if (!isCombatant)
                {
                    ClientFoodPrompts.Remove(snapshot.ArenaId);
                    ArenaUi.CloseFoodPreparation(snapshot.ArenaId);
                    if (snapshot.Phase == SessionPhase.Closed &&
                        ClientPreparations.TryGetValue(snapshot.ArenaId, out ClientPreparationState abandoned) &&
                        !abandoned.Accepted)
                    {
                        RollBackLocalPreparation(snapshot.ArenaId, null);
                    }
                }
            }

            ArenaUi.RenderSnapshot(snapshot);
        }

        private static void OnClientArenaRemoved(string arenaId)
        {
            arenaId = ArenaRegistry.NormalizeId(arenaId);
            if (arenaId == null)
            {
                return;
            }

            ArenaRegistry.RemoveArena(arenaId);
            ClientArenaSnapshots.Remove(arenaId);
            ClientCombatStartArrivalRetryUtc.Remove(arenaId);
            ClientFoodPrompts.Remove(arenaId);
            if (ClientPreparations.TryGetValue(arenaId, out ClientPreparationState preparation) && !preparation.Accepted)
            {
                RollBackLocalPreparation(arenaId, null);
            }
            ClientPreparations.Remove(arenaId);
            ClientQueuePromptSessions.Remove(arenaId);
            RecomputeClientRole();
            ArenaUi.OnArenaRemoved(arenaId);
        }

        private static void RecomputeClientRole()
        {
            Player local = Player.m_localPlayer;
            if (local == null)
            {
                return;
            }

            long playerId = local.GetPlayerID();
            IEnumerable<ArenaClientSnapshot> snapshots = ClientArenaSnapshots.Values;
            bool combatant = snapshots.Any(candidate => candidate.CombatantPlayerId == playerId &&
                (candidate.Phase == SessionPhase.Staging ||
                 candidate.Phase == SessionPhase.Countdown ||
                 candidate.Phase == SessionPhase.Fighting ||
                 candidate.Phase == SessionPhase.Intermission));
            if (combatant)
            {
                SetRole(playerId, ArenaRole.Combatant);
                return;
            }

            bool queued = snapshots.Any(candidate => candidate.QueuePosition > 0 ||
                candidate.CombatantPlayerId == playerId && candidate.Phase == SessionPhase.Called);
            if (queued)
            {
                SetRole(playerId, ArenaRole.Queued);
                return;
            }

            PositionData position = ToPositionData(local.transform.position);
            bool inside = snapshots.Any(candidate =>
                Near(position, candidate.CorePosition, candidate.ProtectedRadius));
            SetRole(playerId, inside ? ArenaRole.Spectator : ArenaRole.Visitor);
        }

        private static void ReportClientCombatStartArrival()
        {
            Player local = Player.m_localPlayer;
            if (local == null || local.IsTeleporting())
            {
                return;
            }

            float currentTime = Time.unscaledTime;
            if (currentTime < _nextClientCombatStartArrivalCheckTime)
            {
                return;
            }
            _nextClientCombatStartArrivalCheckTime = currentTime + 0.1f;

            long playerId = local.GetPlayerID();
            DateTime now = DateTime.UtcNow;
            PositionData localPosition = ToPositionData(local.transform.position);
            foreach (ArenaClientSnapshot snapshot in ClientArenaSnapshots.Values)
            {
                if (snapshot == null || snapshot.CombatantPlayerId != playerId ||
                    snapshot.Phase != SessionPhase.Staging || snapshot.Markers == null)
                {
                    continue;
                }

                bool retryPending = ClientCombatStartArrivalRetryUtc.TryGetValue(
                    snapshot.ArenaId,
                    out DateTime retryUtc) && now < retryUtc;
                if (retryPending || !Near3D(localPosition, snapshot.Markers.CombatantStartPosition, 2f))
                {
                    continue;
                }

                ClientCombatStartArrivalRetryUtc[snapshot.ArenaId] = now.AddSeconds(1);
                ArenaRpc.ReportCombatStartArrival();
            }

            StaleClientCombatStartArrivalArenaIds.Clear();
            foreach (KeyValuePair<string, DateTime> retry in ClientCombatStartArrivalRetryUtc)
            {
                if (!ClientArenaSnapshots.TryGetValue(retry.Key, out ArenaClientSnapshot snapshot) ||
                    snapshot == null || snapshot.CombatantPlayerId != playerId ||
                    snapshot.Phase != SessionPhase.Staging)
                {
                    StaleClientCombatStartArrivalArenaIds.Add(retry.Key);
                }
            }
            foreach (string arenaId in StaleClientCombatStartArrivalArenaIds)
            {
                ClientCombatStartArrivalRetryUtc.Remove(arenaId);
            }
        }

        private static void OnClientLeaderboardReceived(
            LeaderboardKey key,
            List<LeaderboardEntry> entries)
        {
            ArenaUi.ReceiveLeaderboard(key, entries ?? new List<LeaderboardEntry>());
        }

        private static List<LeaderboardEntry> GetCurrentRosterLeaderboard(LeaderboardKey key)
        {
            long revision = _catalog?.RosterRevision ?? 0L;
            return ArenaStore.GetLeaderboard(key)
                .Where(entry => entry != null && entry.RosterRevision == revision)
                .ToList();
        }

        private static void OnClientActionResultReceived(bool success, string message)
        {
            if (!string.IsNullOrWhiteSpace(message) && Player.m_localPlayer != null)
            {
                Player.m_localPlayer.Message(
                    success ? MessageHud.MessageType.TopLeft : MessageHud.MessageType.Center,
                    message);
            }
        }

        private static void RequestPlacementMutation(ArenaWorldObjectPlacement placement)
        {
            if (placement == null || !ArenaWorldObjects.IsLocalAdmin())
            {
                return;
            }

            switch (placement.Kind)
            {
                case ArenaWorldObjectKind.Core:
                    ArenaRpc.RequestAdminMutation(new ArenaAdminMutation
                    {
                        Kind = ArenaAdminMutationKind.RegisterArena,
                        Arena = new ArenaDefinition
                        {
                            ArenaId = placement.ObjectId,
                            DisplayName = "Arena " + ShortId(placement.ObjectId),
                            CorePosition = placement.Position,
                            CombatRadius = ArenaConfig.DefaultCombatRadius.Value,
                            ProtectedRadius = ArenaConfig.DefaultProtectedRadius.Value,
                            Markers = null,
                            Enabled = false,
                            Revision = 1
                        }
                    });
                    ArenaWorldObjects.SelectAdminArena(placement.ObjectId);
                    break;
                case ArenaWorldObjectKind.EntranceGate:
                    if (string.IsNullOrWhiteSpace(placement.ArenaId))
                    {
                        return;
                    }
                    ArenaRpc.RequestAdminMutation(new ArenaAdminMutation
                    {
                        Kind = ArenaAdminMutationKind.RegisterGate,
                        Gate = new ArenaGateDefinition
                        {
                            GateId = placement.ObjectId,
                            ArenaId = placement.ArenaId,
                            DisplayName = "Arena Gate " + ShortId(placement.ObjectId),
                            Position = placement.Position,
                            RotationY = placement.RotationY,
                            IsFallbackEntrance = false
                        }
                    });
                    break;
                case ArenaWorldObjectKind.HubGate:
                    RequestMarkerMutation(placement.ArenaId, ArenaMarkerKind.HubGate, placement.Position, -1);
                    break;
                case ArenaWorldObjectKind.StagingMarker:
                case ArenaWorldObjectKind.CombatantStartMarker:
                case ArenaWorldObjectKind.EnemySpawnMarker:
                case ArenaWorldObjectKind.HubGateMarker:
                    // ArenaMarkerBehaviour reports these once with their precise marker kind.
                    break;
            }
        }

        private static void RequestCoreRemoval(string arenaId)
        {
            arenaId = ArenaRegistry.NormalizeId(arenaId);
            if (arenaId == null || !ArenaWorldObjects.IsLocalAdmin())
            {
                return;
            }

            ArenaRpc.RequestAdminMutation(new ArenaAdminMutation
            {
                Kind = ArenaAdminMutationKind.RemoveArena,
                TargetId = arenaId
            });
        }

        private static void RequestStaleCoreCleanup(string arenaId, PositionData position)
        {
            arenaId = ArenaRegistry.NormalizeId(arenaId);
            if (arenaId == null || !ArenaWorldObjects.IsLocalAdmin())
            {
                return;
            }

            // Replacing a demolished Core used to leave its persisted arena behind.
            // Remove only overlapping definitions; normal nearby arenas remain intact.
            const float replacementDistanceSquared = 4f;
            foreach (ArenaDefinition candidate in ArenaRegistry.GetArenaSnapshot())
            {
                if (candidate == null ||
                    string.Equals(candidate.ArenaId, arenaId, StringComparison.Ordinal))
                {
                    continue;
                }

                float dx = candidate.CorePosition.X - position.X;
                float dy = candidate.CorePosition.Y - position.Y;
                float dz = candidate.CorePosition.Z - position.Z;
                if (dx * dx + dy * dy + dz * dz > replacementDistanceSquared)
                {
                    continue;
                }

                Plugin.Log?.LogInfo("Removing stale arena definition " + candidate.ArenaId +
                                    " replaced by Core " + arenaId + ".");
                ArenaRpc.RequestAdminMutation(new ArenaAdminMutation
                {
                    Kind = ArenaAdminMutationKind.RemoveArena,
                    TargetId = candidate.ArenaId
                });
            }
        }

        private static int RequestMarkerMutation(
            string arenaId,
            ArenaMarkerKind kind,
            PositionData position,
            int slot)
        {
            arenaId = ArenaRegistry.NormalizeId(arenaId);
            if (arenaId == null || !ArenaWorldObjects.IsLocalAdmin())
            {
                return ArenaWorldObjects.MarkerRequestRejected;
            }

            if (kind == ArenaMarkerKind.CombatantStart &&
                ArenaRegistry.TryGetArena(arenaId, out ArenaDefinition markerArena) &&
                !ArenaRegistry.IsWithinCombatRadius(markerArena, position))
            {
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    "Combat Start must be inside the blue combat-radius ring.");
                return ArenaWorldObjects.MarkerRequestRejected;
            }

            if (kind == ArenaMarkerKind.CombatantStart &&
                ArenaRegistry.TryGetMarkerPosition(
                    arenaId,
                    ArenaMarkerKind.CombatantStart,
                    -1,
                    out PositionData existingCombatStart) &&
                !SamePosition(existingCombatStart, position))
            {
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    "This arena already has a Combat Start. Remove it before placing another.");
                return ArenaWorldObjects.MarkerRequestRejected;
            }

            int resolvedSlot = kind == ArenaMarkerKind.EnemySpawn && slot < 0
                ? ArenaRegistry.NextAvailableEnemyMarkerSlot(arenaId)
                : slot;
            if (kind == ArenaMarkerKind.EnemySpawn && resolvedSlot < 0)
            {
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    "This arena already has four Enemy Spawn beacons. Remove one before placing another.");
                return ArenaWorldObjects.MarkerRequestRejected;
            }
            ArenaRpc.RequestAdminMutation(new ArenaAdminMutation
            {
                Kind = ArenaAdminMutationKind.SetMarker,
                TargetId = arenaId,
                MarkerKind = (int)kind,
                MarkerSlot = resolvedSlot,
                MarkerPosition = position
            });
            return resolvedSlot;
        }

        private static void RequestMarkerRemoval(
            string arenaId,
            ArenaMarkerKind kind,
            PositionData position,
            int slot)
        {
            arenaId = ArenaRegistry.NormalizeId(arenaId);
            if (arenaId == null || !ArenaWorldObjects.IsLocalAdmin())
            {
                return;
            }

            ArenaRpc.RequestAdminMutation(new ArenaAdminMutation
            {
                Kind = ArenaAdminMutationKind.RemoveMarker,
                TargetId = arenaId,
                MarkerKind = (int)kind,
                MarkerSlot = slot,
                MarkerPosition = position
            });
        }

        private static void RequestGateConfiguration(
            string objectId,
            string arenaId,
            string displayName,
            bool isFallback)
        {
            ArenaGateBehaviour behaviour = UnityEngine.Object
                .FindObjectsByType<ArenaGateBehaviour>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .FirstOrDefault(candidate => candidate != null && candidate.ObjectId == objectId);
            if (behaviour == null || behaviour.IsHubGate)
            {
                return;
            }

            ArenaRpc.RequestAdminMutation(new ArenaAdminMutation
            {
                Kind = ArenaAdminMutationKind.RegisterGate,
                Gate = new ArenaGateDefinition
                {
                    GateId = objectId,
                    ArenaId = arenaId,
                    DisplayName = displayName,
                    Position = ToPositionData(behaviour.transform.position),
                    RotationY = behaviour.transform.eulerAngles.y,
                    IsFallbackEntrance = isFallback
                }
            });
        }

        private static void RequestUiAdminMutation(ArenaUiAdminMutation mutation)
        {
            if (mutation == null)
            {
                return;
            }

            if (!ArenaWorldObjects.IsLocalAdmin())
            {
                Player.m_localPlayer?.Message(
                    MessageHud.MessageType.Center,
                    "Only an authenticated administrator may change arena settings.");
                return;
            }

            if (mutation.Kind == ArenaUiAdminMutationKind.SetTerrainPermission ||
                mutation.Kind == ArenaUiAdminMutationKind.SetBuildingPermission ||
                mutation.Kind == ArenaUiAdminMutationKind.SetPickupPermission)
            {
                string targetId = ArenaRegistry.NormalizeId(mutation.ArenaId);
                if (targetId == null)
                {
                    Player.m_localPlayer?.Message(
                        MessageHud.MessageType.Center,
                        "This Arena Core does not have a valid server ID yet.");
                    return;
                }
                ArenaAdminMutationKind kind = mutation.Kind == ArenaUiAdminMutationKind.SetTerrainPermission
                    ? ArenaAdminMutationKind.SetAdminTerrainPermission
                    : mutation.Kind == ArenaUiAdminMutationKind.SetBuildingPermission
                        ? ArenaAdminMutationKind.SetAdminBuildingPermission
                        : ArenaAdminMutationKind.SetAdminPickupPermission;
                ArenaRpc.RequestAdminMutation(new ArenaAdminMutation
                {
                    Kind = kind,
                    TargetId = targetId,
                    Enabled = mutation.Enabled
                });
                return;
            }

            if (!ArenaRegistry.TryGetArena(mutation.ArenaId, out ArenaDefinition arena))
            {
                Player.m_localPlayer?.Message(
                    MessageHud.MessageType.Center,
                    "The selected arena is not synchronized with the server yet.");
                return;
            }

            if (mutation.Kind == ArenaUiAdminMutationKind.SaveDefinition)
            {
                arena.DisplayName = mutation.DisplayName;
                arena.CombatRadius = mutation.CombatRadius;
                arena.ProtectedRadius = mutation.ProtectedRadius;
            }
            else
            {
                arena.Enabled = mutation.Kind == ArenaUiAdminMutationKind.EnableArena;
            }

            ArenaRpc.RequestAdminMutation(new ArenaAdminMutation
            {
                Kind = ArenaAdminMutationKind.RegisterArena,
                Arena = arena
            });
        }

        private static ArenaDefinition MergeArenaMutation(ArenaDefinition requested)
        {
            if (requested == null)
            {
                throw new ArgumentNullException(nameof(requested));
            }

            if (!ArenaRegistry.TryGetArena(requested.ArenaId, out ArenaDefinition existing))
            {
                return requested;
            }

            requested.CorePosition = existing.CorePosition;
            requested.Markers = requested.Markers ?? existing.Markers;
            requested.Revision = Math.Max(requested.Revision, existing.Revision + 1);
            return requested;
        }

        private static void DetectFloorEntryAndBoundary()
        {
            if (_engine == null)
            {
                return;
            }

            ActiveCombatantIds.Clear();
            AwaitingCombatStartIds.Clear();
            foreach (ArenaSession session in _engine.GetActiveSessions())
            {
                if (session?.Request == null || !ArenaRegistry.TryGetArena(session.ArenaId, out ArenaDefinition arena))
                {
                    continue;
                }

                Player player = FindPlayer(session.Request.PlayerId);
                if (player == null)
                {
                    if (session.Phase == SessionPhase.Countdown ||
                        session.Phase == SessionPhase.Fighting ||
                        session.Phase == SessionPhase.Intermission)
                    {
                        _engine.Disconnect(session.Request.PlayerId);
                    }
                    continue;
                }

                bool outside = !Near(ToPositionData(player.transform.position), arena.CorePosition, arena.CombatRadius);
                if (ArenaStartPolicy.ShouldTeleportToCombatStart(
                        session.Phase,
                        session.ResourceSnapshot != null))
                {
                    long playerId = session.Request.PlayerId;
                    AwaitingCombatStartIds.Add(playerId);
                    bool arrivalConfirmed = Near3D(
                        ToPositionData(player.transform.position),
                        arena.Markers.CombatantStartPosition,
                        2f);
                    if (ArenaStartPolicy.ShouldBeginCountdown(
                            session.Phase,
                            session.ResourceSnapshot != null,
                            arrivalConfirmed))
                    {
                        CombatStartMoveRetryUtc.Remove(playerId);
                        _engine.EnterCombatFloor(playerId, session.ResourceSnapshot);
                        continue;
                    }

                    // Accepting a queue call may already be teleporting the player
                    // to the Arena Master. Valheim rejects a second TeleportTo while
                    // that move is active, so retry at a controlled rate and keep the
                    // session in staging until the server observes the destination.
                    DateTime now = DateTime.UtcNow;
                    if (!CombatStartMoveRetryUtc.TryGetValue(playerId, out DateTime retryUtc) || now >= retryUtc)
                    {
                        ArenaRpc.SendPlayerMove(playerId, arena.Markers.CombatantStartPosition, 0f);
                        CombatStartMoveRetryUtc[playerId] = now.AddSeconds(1);
                    }
                    continue;
                }

                if (session.Phase == SessionPhase.Countdown ||
                    session.Phase == SessionPhase.Fighting ||
                    session.Phase == SessionPhase.Intermission)
                {
                    long playerId = session.Request.PlayerId;
                    ActiveCombatantIds.Add(playerId);
                    HandleCombatBoundary(session, arena, outside);
                    _engine.ReportBoundaryState(playerId, outside);
                    if (session.Phase == SessionPhase.Fighting)
                    {
                        RepositionEscapedOrStuckEnemies(session.SessionId);
                    }
                }
            }

            StaleCombatantIds.Clear();
            foreach (long playerId in CombatStartMoveRetryUtc.Keys)
            {
                if (!AwaitingCombatStartIds.Contains(playerId))
                {
                    StaleCombatantIds.Add(playerId);
                }
            }
            foreach (long playerId in StaleCombatantIds)
            {
                CombatStartMoveRetryUtc.Remove(playerId);
            }

            StaleCombatantIds.Clear();
            foreach (long playerId in CombatBoundaryStates.Keys)
            {
                if (!ActiveCombatantIds.Contains(playerId))
                {
                    StaleCombatantIds.Add(playerId);
                }
            }
            foreach (long playerId in StaleCombatantIds)
            {
                CombatBoundaryStates.Remove(playerId);
            }
        }

        private static void HandleCombatBoundary(ArenaSession session, ArenaDefinition arena, bool outside)
        {
            if (session?.Request == null || arena == null)
            {
                return;
            }

            long playerId = session.Request.PlayerId;
            if (!CombatBoundaryStates.TryGetValue(playerId, out CombatBoundaryState boundary) ||
                !string.Equals(boundary.ArenaId, arena.ArenaId, StringComparison.Ordinal))
            {
                boundary = new CombatBoundaryState { ArenaId = arena.ArenaId };
                CombatBoundaryStates[playerId] = boundary;
            }

            if (!outside)
            {
                if (boundary.OutsideSinceUtc != DateTime.MinValue)
                {
                    SendPlayerPopup(playerId, "ARENA BOUNDARY CLEARED\nChallenge continuing.");
                }
                boundary.OutsideSinceUtc = DateTime.MinValue;
                boundary.NextPopupUtc = DateTime.MinValue;
                return;
            }

            DateTime now = DateTime.UtcNow;
            if (boundary.OutsideSinceUtc == DateTime.MinValue)
            {
                boundary.OutsideSinceUtc = now;
                boundary.NextPopupUtc = DateTime.MinValue;
            }
            if (now < boundary.NextPopupUtc)
            {
                return;
            }

            int graceSeconds = Math.Max(1, ArenaConfig.BoundaryGraceSeconds.Value);
            int remainingSeconds = Math.Max(1,
                (int)Math.Ceiling(graceSeconds - (now - boundary.OutsideSinceUtc).TotalSeconds));
            SendPlayerPopup(playerId,
                "RETURN TO THE ARENA\n" + remainingSeconds +
                (remainingSeconds == 1 ? " second" : " seconds") + " remaining or challenge forfeited.");
            boundary.NextPopupUtc = now.Add(CombatBoundaryPopupInterval);
        }

        private static void ReconcileArenaEnemies()
        {
            var affectedSessions = new HashSet<string>(StringComparer.Ordinal);
            foreach (ArenaEnemyHandle handle in Enemies.Values.ToList())
            {
                if (handle.Character != null && !handle.Character.IsDead() && handle.Character.GetHealth() > 0f)
                {
                    continue;
                }
                Enemies.Remove(handle.EnemyId);
                affectedSessions.Add(handle.SessionId);
            }
            foreach (string sessionId in affectedSessions)
            {
                if (!Enemies.Values.Any(handle => string.Equals(handle.SessionId, sessionId, StringComparison.Ordinal)))
                {
                    _engine.ReportEncounterCleared(sessionId);
                }
            }
        }

        private static void ProcessEngineEffects()
        {
            if (_engine == null)
            {
                return;
            }

            bool stateChanged = false;
            int safety = 0;
            while (safety++ < 16)
            {
                IList<ArenaEffect> effects = _engine.DrainEffects();
                if (effects.Count == 0)
                {
                    break;
                }

                stateChanged = true;
                foreach (ArenaEffect effect in effects)
                {
                    bool succeeded = ApplyEngineEffect(effect);
                    _engine.ApplyEffectResult(effect, succeeded);
                }
            }

            DateTime now = DateTime.UtcNow;
            if (!stateChanged && now < _nextStateBroadcastUtc)
            {
                return;
            }

            _nextStateBroadcastUtc = now.AddSeconds(1);
            SyncRegistryFromEngine();
            BroadcastAllArenaStates();
        }

        private static bool ApplyEngineEffect(ArenaEffect effect)
        {
            if (effect == null)
            {
                return false;
            }

            switch (effect.Type)
            {
                case ArenaEffectType.CallQueuedPlayer:
                case ArenaEffectType.StartCountdown:
                    return true;
                case ArenaEffectType.MoveToStaging:
                    return TrySendArenaMove(
                        effect.PlayerId,
                        effect.ArenaId,
                        true,
                        IsTerminalMove(effect));
                case ArenaEffectType.SpawnEncounter:
                    if (!_engine.TryGetSession(effect.SessionId, out ArenaSession spawning))
                    {
                        return false;
                    }
                    return SpawnEncounter(spawning, effect.Encounter).Count == effect.Encounter.Quantity;
                case ArenaEffectType.DespawnSessionEnemies:
                    DespawnSessionEnemies(effect.SessionId, effect.ArenaId);
                    return true;
                case ArenaEffectType.RestorePlayerState:
                    return QueueResourceRestore(effect);
                case ArenaEffectType.MoveToSpectatorArea:
                    SetRole(effect.PlayerId, ArenaRole.Spectator);
                    return TrySendArenaMove(effect.PlayerId, effect.ArenaId, true, true);
                case ArenaEffectType.SetArenaRole:
                    if (Enum.TryParse(effect.Message, true, out ArenaRole role))
                    {
                        SetRole(effect.PlayerId, role);
                        return true;
                    }
                    return false;
                case ArenaEffectType.PersistState:
                    return SaveWorldState();
                case ArenaEffectType.UpdateLeaderboard:
                    return RecordLeaderboard(effect.SessionId);
                case ArenaEffectType.SendLocalMessage:
                    SendPlayerMessage(effect.PlayerId, effect.Message);
                    return true;
                case ArenaEffectType.SendGlobalMessage:
                    SendGlobalMessage(effect.Message);
                    return true;
                default:
                    return false;
            }
        }

        private static bool QueueResourceRestore(ArenaEffect effect)
        {
            if (!_engine.TryGetSession(effect.SessionId, out ArenaSession session) || session.ResourceSnapshot == null)
            {
                return false;
            }

            var restore = new PendingRestore
            {
                PlayerId = effect.PlayerId,
                Snapshot = session.ResourceSnapshot,
                Outcome = session.Outcome == SessionOutcome.None ? SessionOutcome.ServerRestart : session.Outcome,
                Session = session
            };
            PendingRestores[effect.PlayerId] = restore;
            if (!SaveWorldState())
            {
                return false;
            }
            return TrySendRestore(restore);
        }

        private static void RetryPendingRestores()
        {
            foreach (PendingRestore restore in PendingRestores.Values.ToList())
            {
                if (DateTime.UtcNow >= restore.NextAttemptUtc)
                {
                    TrySendRestore(restore);
                }
            }
        }

        private static bool TrySendRestore(PendingRestore restore)
        {
            restore.NextAttemptUtc = DateTime.UtcNow.AddSeconds(35);
            return ArenaRpc.SendResourceRestore(restore.PlayerId, restore.Snapshot, restore.Outcome);
        }

        private static bool TrySendArenaMove(
            long playerId,
            string arenaId,
            bool staging,
            bool useArenaMasterExit = false)
        {
            if (!ArenaRegistry.TryGetArena(arenaId, out ArenaDefinition arena) || arena.Markers == null)
            {
                return false;
            }
            PositionData destination = staging ? arena.Markers.StagingPosition : arena.Markers.CombatantStartPosition;
            float rotationY = 0f;
            if (staging && useArenaMasterExit)
            {
                TryResolveArenaMasterExit(arena, out destination, out rotationY);
            }
            Player player = FindPlayer(playerId);
            if (staging && !useArenaMasterExit && player != null &&
                Near3D(ToPositionData(player.transform.position), destination, 4f))
            {
                return true;
            }
            return ArenaRpc.SendPlayerMove(playerId, destination, rotationY);
        }

        private static bool IsTerminalMove(ArenaEffect effect)
        {
            return effect != null && _engine != null &&
                   _engine.TryGetSession(effect.SessionId, out ArenaSession session) &&
                   session.Outcome != SessionOutcome.None;
        }

        private static bool TryResolveArenaMasterExit(
            ArenaDefinition arena,
            out PositionData destination,
            out float rotationY)
        {
            destination = arena.Markers.StagingPosition;
            rotationY = 0f;
            Character closest = null;
            float closestDistanceSquared = 36f;
            Vector3 staging = ToVector3(arena.Markers.StagingPosition);
            foreach (Character character in Character.GetAllCharacters())
            {
                if (!ArenaWorldObjects.IsChallengeHost(character))
                {
                    continue;
                }

                Vector3 offset = character.transform.position - staging;
                float distanceSquared = offset.sqrMagnitude;
                if (distanceSquared <= closestDistanceSquared)
                {
                    closest = character;
                    closestDistanceSquared = distanceSquared;
                }
            }
            if (closest == null)
            {
                Plugin.Log?.LogWarning("Arena Master was not loaded while resolving the completion exit for " +
                                       arena.ArenaId + "; using the saved staging position.");
                return false;
            }

            Transform master = closest.transform;
            Vector3 exit = master.position + master.forward * 3f + Vector3.up * 0.2f;
            destination = ToPositionData(exit);
            rotationY = master.eulerAngles.y;
            return true;
        }

        private static bool RecordLeaderboard(string sessionId)
        {
            if (!_engine.TryGetSession(sessionId, out ArenaSession session) || session?.Plan == null || session.Request == null)
            {
                return false;
            }

            LeaderboardKey key = BuildLeaderboardKey(session);
            return ArenaStore.RecordResult(new LeaderboardEntry
            {
                Key = key,
                PlayerId = session.Request.PlayerId,
                PlayerName = session.Request.PlayerName,
                Completed = session.Outcome == SessionOutcome.Victory,
                FurthestEncounterIndex = Math.Max(0, session.EncounterIndex),
                ElapsedMilliseconds = Math.Max(0L, session.ElapsedMilliseconds),
                RosterRevision = session.Plan.RosterRevision,
                RecordedUtc = DateTime.UtcNow
            });
        }

        private static LeaderboardKey BuildLeaderboardKey(ArenaSession session)
        {
            CustomEncounterSelection custom = session.Request.CustomSelection;
            return new LeaderboardKey
            {
                Mode = session.Plan.Mode,
                CapMode = session.Plan.CapMode,
                SelectedBiome = session.Request.SelectedBiome,
                CustomCreatureKey = custom?.CreatureKey ?? string.Empty,
                CustomStars = custom?.Stars ?? StarLevel.Base,
                CustomQuantity = custom?.Quantity ?? 0
            };
        }

        private static void SyncRegistryFromEngine()
        {
            if (_engine == null)
            {
                return;
            }

            ArenaRegistry.ReplaceQueue(_engine.GetQueueSnapshot());
            foreach (ArenaDefinition arena in ArenaRegistry.GetArenaSnapshot())
            {
                ArenaRegistry.ClearActiveSession(arena.ArenaId);
            }
            foreach (ArenaSession session in _engine.GetActiveSessions())
            {
                if (ArenaRegistry.TryGetArena(session.ArenaId, out _))
                {
                    ArenaRegistry.SetActiveSession(session);
                }
            }
        }

        private static bool SaveWorldState()
        {
            if (_loadedWorldUid == 0L || ZNet.instance == null || !ZNet.instance.IsServer() || _engine == null)
            {
                return false;
            }

            PersistedWorldState current = ArenaStore.Snapshot();
            var queue = _engine.GetQueueSnapshot().ToList();
            var interrupted = new List<ArenaSession>();
            foreach (ArenaSession active in _engine.GetActiveSessions())
            {
                interrupted.Add(active);
            }
            interrupted.AddRange(PendingRestores.Values
                .Where(restore => restore.Session != null && restore.Session.ResourceSnapshot != null)
                .Select(restore => restore.Session));

            ArenaRegistry.ReplaceQueue(queue);
            PersistedWorldState state = ArenaRegistry.Snapshot(
                _loadedWorldUid,
                _catalog.RosterRevision,
                interrupted.GroupBy(session => session.SessionId).Select(group => group.First()),
                current.Leaderboard);
            return ArenaStore.Save(state);
        }

        private static ArenaClientSnapshot BuildClientSnapshot(string arenaId, long viewerPlayerId)
        {
            if (!ArenaRegistry.TryGetArena(arenaId, out ArenaDefinition arena))
            {
                return null;
            }

            ArenaSession active = null;
            _engine?.TryGetActiveSession(arenaId, out active);
            bool awaitingRestore = viewerPlayerId > 0 && PendingRestores.ContainsKey(viewerPlayerId);
            IList<QueueEntry> queue = _engine?.GetQueueSnapshot() ?? new List<QueueEntry>();
            List<QueueEntry> arenaQueue = queue.Where(entry => entry.Request.ArenaId == arenaId)
                .OrderBy(entry => entry.QueueSequence).ToList();
            int queueIndex = arenaQueue.FindIndex(entry => entry.Request.PlayerId == viewerPlayerId);
            EncounterDefinition encounter = active?.Plan?.Encounters != null &&
                                            active.EncounterIndex >= 0 &&
                                            active.EncounterIndex < active.Plan.Encounters.Count
                ? active.Plan.Encounters[active.EncounterIndex]
                : null;
            int countdown = active == null || active.PhaseDeadlineUtc == DateTime.MinValue
                ? 0
                : Math.Max(0, (int)Math.Ceiling((active.PhaseDeadlineUtc - DateTime.UtcNow).TotalSeconds));
            return new ArenaClientSnapshot
            {
                ArenaId = arena.ArenaId,
                SessionId = active?.SessionId ?? string.Empty,
                ArenaName = arena.DisplayName,
                CorePosition = arena.CorePosition,
                CombatRadius = arena.CombatRadius,
                ProtectedRadius = arena.ProtectedRadius,
                Markers = CopyMarkers(arena.Markers),
                Phase = awaitingRestore && active?.Request?.PlayerId == viewerPlayerId
                    ? SessionPhase.Recovering
                    : active?.Phase ?? SessionPhase.Closed,
                CombatantPlayerId = awaitingRestore && active?.Request?.PlayerId == viewerPlayerId
                    ? 0L
                    : active?.Request?.PlayerId ?? 0L,
                CombatantName = awaitingRestore && active?.Request?.PlayerId == viewerPlayerId
                    ? string.Empty
                    : active?.Request?.PlayerName ?? string.Empty,
                QueuePosition = queueIndex < 0 ? 0 : queueIndex + 1,
                QueueLength = arenaQueue.Count,
                EncounterIndex = active?.EncounterIndex ?? 0,
                EncounterCount = active?.Plan?.Encounters?.Count ?? 0,
                CurrentEncounter = encounter,
                CountdownSeconds = awaitingRestore ? 0 : countdown,
                ElapsedMilliseconds = active?.ElapsedMilliseconds ?? 0L,
                PreparationComplete = active?.ResourceSnapshot != null,
                AdminsMayModifyTerrain = ArenaConfig.AllowAdminTerrainEditing?.Value == true,
                AdminsMayBuild = ArenaConfig.AllowAdminBuilding?.Value == true,
                AdminsMayPickupDroppedItems = ArenaConfig.AllowAdminDroppedItemPickup?.Value == true
            };
        }

        private static void BroadcastAllArenaStates()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer())
            {
                return;
            }
            foreach (ArenaDefinition arena in ArenaRegistry.GetArenaSnapshot())
            {
                ArenaRpc.BroadcastArenaState(arena.ArenaId);
            }
        }

        private static void SendPlayerMessage(long playerId, string message)
        {
            if (string.IsNullOrWhiteSpace(message) || ZNet.instance == null || ZRoutedRpc.instance == null)
            {
                return;
            }
            if (Player.m_localPlayer != null && Player.m_localPlayer.GetPlayerID() == playerId)
            {
                Player.m_localPlayer.Message(MessageHud.MessageType.TopLeft, message);
                return;
            }
            foreach (ZNetPeer peer in ZNet.instance.GetConnectedPeers())
            {
                Player player = FindPlayer(playerId);
                if (player != null && peer.m_characterID == player.GetZDOID())
                {
                    ZRoutedRpc.instance.InvokeRoutedRPC(
                        peer.m_uid,
                        "ShowMessage",
                        (int)MessageHud.MessageType.TopLeft,
                        message);
                    return;
                }
            }
        }

        private static void SendPlayerPopup(long playerId, string message)
        {
            if (string.IsNullOrWhiteSpace(message) || ZNet.instance == null || ZRoutedRpc.instance == null)
            {
                return;
            }
            if (Player.m_localPlayer != null && Player.m_localPlayer.GetPlayerID() == playerId)
            {
                try
                {
                    MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, message);
                }
                catch (Exception error)
                {
                    Plugin.Debug("Could not display the arena boundary popup: " + error.Message);
                }
                return;
            }

            Player player = FindPlayer(playerId);
            if (player == null)
            {
                return;
            }
            foreach (ZNetPeer peer in ZNet.instance.GetConnectedPeers())
            {
                if (peer.m_characterID == player.GetZDOID())
                {
                    ZRoutedRpc.instance.InvokeRoutedRPC(
                        peer.m_uid,
                        "ShowMessage",
                        (int)MessageHud.MessageType.Center,
                        message);
                    return;
                }
            }
        }

        private static void SendGlobalMessage(string message)
        {
            if (!string.IsNullOrWhiteSpace(message) && ZRoutedRpc.instance != null)
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(
                    ZRoutedRpc.Everybody,
                    "ShowMessage",
                    (int)MessageHud.MessageType.TopLeft,
                    message);
            }
        }

        private static BiomeTier GetWorldUnlockedBiome()
        {
            ZoneSystem zones = ZoneSystem.instance;
            if (zones == null)
            {
                return BiomeTier.Meadows;
            }
            if (zones.GetGlobalKey("defeated_fader")) return BiomeTier.DeepNorth;
            if (zones.GetGlobalKey("defeated_queen")) return BiomeTier.Ashlands;
            if (zones.GetGlobalKey(GlobalKeys.defeated_goblinking)) return BiomeTier.Mistlands;
            if (zones.GetGlobalKey(GlobalKeys.defeated_dragon)) return BiomeTier.Plains;
            if (zones.GetGlobalKey(GlobalKeys.defeated_bonemass)) return BiomeTier.Mountain;
            if (zones.GetGlobalKey(GlobalKeys.defeated_gdking)) return BiomeTier.Swamp;
            if (zones.GetGlobalKey(GlobalKeys.defeated_eikthyr)) return BiomeTier.BlackForest;
            return BiomeTier.Meadows;
        }

        internal static void TickOnMainThread()
        {
            if (!_initialized || ZNet.instance == null)
            {
                return;
            }

            ArenaRpc.Register();

            long worldUid = ZNet.instance.GetWorldUID();
            if (worldUid != 0L && worldUid != _loadedWorldUid)
            {
                LoadWorld(worldUid);
            }

            if (ZNet.instance.IsServer() && _loadedWorldUid != 0L)
            {
                // Observe a last-moment return before evaluating the grace
                // deadline so a player who crossed back in is never forfeited.
                DetectFloorEntryAndBoundary();
                _engine.Tick(DateTime.UtcNow);
                ReconcileArenaEnemies();
                ProcessEngineEffects();
                RetryPendingRestores();

            }

            if (_loadedWorldUid != 0L)
            {
                ReportClientCombatStartArrival();
            }

            if (DateTime.UtcNow >= _nextCollisionRefreshUtc)
            {
                _nextCollisionRefreshUtc = DateTime.UtcNow.AddSeconds(1);
                RefreshParticipantCollisions();
            }
        }

        internal static IList<string> SpawnEncounter(EncounterDefinition encounter)
        {
            ArenaSession session = ArenaRegistry.GetArenaSnapshot()
                .Select(arena =>
                {
                    ArenaRegistry.TryGetActiveSession(arena.ArenaId, out ArenaSession active);
                    return active;
                })
                .FirstOrDefault(active => active != null &&
                                          active.Phase == SessionPhase.Fighting &&
                                          active.EncounterIndex == encounter?.Sequence);
            return SpawnEncounter(session, encounter);
        }

        internal static IList<string> SpawnEncounter(ArenaSession session, EncounterDefinition encounter)
        {
            var spawnedIds = new List<string>();
            if (!IsAuthoritativeServer() || session == null || encounter == null ||
                !ArenaRegistry.TryGetArena(session.ArenaId, out ArenaDefinition arena) ||
                arena.Markers?.EnemySpawnPositions == null || arena.Markers.EnemySpawnPositions.Count == 0 ||
                ZNetScene.instance == null)
            {
                return spawnedIds;
            }

            Challenges.ChallengeCatalog catalog = new Challenges.ChallengeCatalog();
            CreatureDefinition creature = catalog.GetRoster().FirstOrDefault(candidate =>
                string.Equals(candidate.CreatureKey, encounter.CreatureKey, StringComparison.OrdinalIgnoreCase));
            GameObject primaryPrefab = creature == null ? null : ZNetScene.instance.GetPrefab(creature.PrefabName);
            GameObject secondaryPrefab = creature == null || string.IsNullOrWhiteSpace(creature.SecondaryPrefabName)
                ? null
                : ZNetScene.instance.GetPrefab(creature.SecondaryPrefabName);
            if (primaryPrefab == null ||
                !string.IsNullOrWhiteSpace(creature?.SecondaryPrefabName) && secondaryPrefab == null)
            {
                Plugin.Log?.LogError("Arena creature prefab is unavailable: " + encounter.CreatureKey);
                return spawnedIds;
            }

            GameObject[] encounterPrefabs = secondaryPrefab == null
                ? new[] { primaryPrefab }
                : new[] { primaryPrefab, secondaryPrefab };
            int quantity = Math.Max(1, Math.Min(10, encounter.Quantity));
            int previousMarker = LastSpawnMarkerBySessionId.TryGetValue(session.SessionId, out int rememberedMarker)
                ? rememberedMarker
                : -1;
            IList<int> markerOrder = ArenaSpawnSelectionPolicy.BuildOrder(
                arena.Markers.EnemySpawnPositions.Count,
                quantity * encounterPrefabs.Length,
                NextSpawnSeed(),
                previousMarker);
            int markerCursor = 0;
            for (int index = 0; index < quantity; index++)
            {
                var unitEnemyIds = new List<string>(encounterPrefabs.Length);
                bool completeUnit = true;
                for (int member = 0; member < encounterPrefabs.Length; member++)
                {
                    int markerIndex = markerOrder[markerCursor++];
                    PositionData marker = arena.Markers.EnemySpawnPositions[markerIndex];
                    Vector3 position = ToVector3(marker);
                    GameObject spawned = UnityEngine.Object.Instantiate(
                        encounterPrefabs[member], position, Quaternion.identity);
                    Character character = spawned == null ? null : spawned.GetComponent<Character>();
                    ZNetView view = spawned == null ? null : spawned.GetComponent<ZNetView>();
                    if (character == null || view == null || !view.IsValid())
                    {
                        if (spawned != null)
                        {
                            UnityEngine.Object.Destroy(spawned);
                        }
                        completeUnit = false;
                        break;
                    }

                    string enemyId = Guid.NewGuid().ToString("D").ToLowerInvariant();
                    character.SetLevel((int)encounter.Stars + 1);
                    CharacterDrop drop = spawned.GetComponent<CharacterDrop>();
                    drop?.SetDropsEnabled(false);

                    ZDO zdo = view.GetZDO();
                    zdo.Set(EnemyArenaZdoKey, session.ArenaId);
                    zdo.Set(EnemySessionZdoKey, session.SessionId);
                    zdo.Set(EnemyIdZdoKey, enemyId);

                    Enemies[enemyId] = new ArenaEnemyHandle
                    {
                        EnemyId = enemyId,
                        ArenaId = session.ArenaId,
                        SessionId = session.SessionId,
                        Character = character,
                        SpawnPosition = position,
                        SpawnMarkerIndex = markerIndex
                    };
                    unitEnemyIds.Add(enemyId);
                    ArenaRuleContext.CacheEnemyArenaId(character, session.ArenaId);
                    AggroArenaEnemy(character, session.Request?.PlayerId ?? 0L);
                }

                if (!completeUnit)
                {
                    foreach (string enemyId in unitEnemyIds)
                    {
                        if (Enemies.TryGetValue(enemyId, out ArenaEnemyHandle handle))
                        {
                            DestroyEnemy(handle);
                        }
                    }
                    continue;
                }

                // One returned ID represents one selected encounter unit. For
                // Zil & Thungr, the unit owns two independently tracked enemies.
                spawnedIds.Add(unitEnemyIds[0]);
            }

            if (markerCursor > 0)
            {
                LastSpawnMarkerBySessionId[session.SessionId] = markerOrder[markerCursor - 1];
            }

            return spawnedIds;
        }

        private static int NextSpawnSeed()
        {
            lock (SpawnRandom)
            {
                return SpawnRandom.Next();
            }
        }

        internal static void DespawnSessionEnemies(string sessionId)
        {
            DespawnSessionEnemies(sessionId, null);
        }

        private static void DespawnSessionEnemies(string sessionId, string arenaId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return;
            }
            LastSpawnMarkerBySessionId.Remove(sessionId);

            var characters = new HashSet<Character>();
            foreach (ArenaEnemyHandle handle in Enemies.Values
                         .Where(candidate => string.Equals(candidate.SessionId, sessionId, StringComparison.Ordinal))
                         .ToList())
            {
                Enemies.Remove(handle.EnemyId);
                if (handle.Character != null)
                {
                    characters.Add(handle.Character);
                }
            }

            // The ZDO scan covers summons and restart/reconciliation gaps even if
            // an in-memory handle was lost before terminal cleanup.
            foreach (Character character in Character.GetAllCharacters().ToList())
            {
                if (character != null &&
                    string.Equals(ReadZdo(character, EnemySessionZdoKey), sessionId, StringComparison.Ordinal))
                {
                    characters.Add(character);
                }
            }

            // A clean arena reset is the final safety net for indirect spawners
            // other than SpawnAbility. Never remove players, tamed creatures, or
            // the Arena Master; all other creatures inside this arena's protected
            // area belong to the concluded combat state and must not escape it.
            if (!string.IsNullOrWhiteSpace(arenaId) &&
                ArenaRegistry.TryGetArena(arenaId, out ArenaDefinition arena))
            {
                foreach (Character character in Character.GetAllCharacters().ToList())
                {
                    if (character == null || character is Player || character.IsTamed() ||
                        ArenaWorldObjects.IsChallengeHost(character))
                    {
                        continue;
                    }

                    if (Near(ToPositionData(character.transform.position),
                            arena.CorePosition,
                            arena.ProtectedRadius))
                    {
                        characters.Add(character);
                    }
                }
            }

            foreach (Character character in characters)
            {
                DestroyArenaCharacter(character);
            }
        }

        internal static bool MoveToStaging(long playerId)
        {
            return MovePlayerToArenaMarker(playerId, arena => arena.Markers?.StagingPosition);
        }

        internal static bool MoveToSpectatorArea(long playerId)
        {
            SetRole(playerId, ArenaRole.Spectator);
            return MovePlayerToArenaMarker(playerId, arena => arena.Markers?.StagingPosition);
        }

        private static bool OpenLocalFoodPreparation(ArenaClientSnapshot arena)
        {
            Player player = Player.m_localPlayer;
            if (player == null || arena == null)
            {
                return false;
            }

            List<string> preferred = LoadPreferredFoodKeys(player.GetPlayerID());
            foreach (Player.Food activeFood in player.GetFoods())
            {
                if (!string.IsNullOrWhiteSpace(activeFood?.m_name) && !preferred.Contains(activeFood.m_name))
                {
                    preferred.Add(activeFood.m_name);
                }
            }
            return ArenaUi.OpenFoodPreparation(
                arena.ArenaId,
                GetArenaFoods(player, true),
                preferred,
                Math.Max(1, arena.CountdownSeconds));
        }

        private static bool ConfirmLocalFoodPreparation(IList<string> selectedPrefabNames)
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                return false;
            }
            long playerId = player.GetPlayerID();
            ArenaClientSnapshot arena = ClientArenaSnapshots.Values.FirstOrDefault(snapshot =>
                snapshot != null && snapshot.CombatantPlayerId == playerId &&
                snapshot.Phase == SessionPhase.Staging && !snapshot.PreparationComplete);
            if (arena == null || ClientPreparations.ContainsKey(arena.ArenaId))
            {
                player.Message(MessageHud.MessageType.Center, "The arena is not waiting for food preparation.");
                return false;
            }

            List<ArenaFoodDefinition> knownFoods = GetArenaFoods(player, true).ToList();
            var eligible = new HashSet<string>(knownFoods.Select(food => food.PrefabName), StringComparer.Ordinal);
            if (!ArenaFoodSelectionPolicy.IsValid(selectedPrefabNames, eligible))
            {
                player.Message(MessageHud.MessageType.Center,
                    "Choose exactly three distinct foods your character has discovered.");
                return false;
            }

            PlayerResourceSnapshot resources = CaptureResources(playerId);
            if (resources == null)
            {
                return false;
            }
            resources.ArenaFoodPrefabNames = selectedPrefabNames.Select(value => value.Trim()).ToList();
            if (!ApplyTemporaryArenaFood(player, resources.ArenaFoodPrefabNames, knownFoods))
            {
                RestoreResources(playerId, SessionOutcome.RuntimeError);
                player.Message(MessageHud.MessageType.Center, "Arena food could not be applied. Try again.");
                return false;
            }

            SavePreferredFoodKeys(playerId, resources.ArenaFoodPrefabNames);
            ClientPreparations[arena.ArenaId] = new ClientPreparationState
            {
                Snapshot = resources,
                SubmittedUtc = DateTime.UtcNow,
                Accepted = false
            };
            ArenaRpc.SubmitResourceSnapshot(resources);
            return true;
        }

        private static void RollBackLocalPreparation(string arenaId, string message)
        {
            if (!ClientPreparations.TryGetValue(arenaId, out ClientPreparationState preparation))
            {
                return;
            }
            ClientPreparations.Remove(arenaId);
            ClientFoodPrompts.Remove(arenaId);
            if (preparation?.Snapshot != null)
            {
                ResourceSnapshots[preparation.Snapshot.PlayerId] = preparation.Snapshot;
                RestoreResources(preparation.Snapshot.PlayerId, SessionOutcome.RuntimeError);
            }
            ArenaUi.CloseFoodPreparation(arenaId);
            if (!string.IsNullOrWhiteSpace(message))
            {
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, message);
            }
        }

        private static IList<ArenaFoodDefinition> GetArenaFoods(Player player, bool requireKnown)
        {
            if (ObjectDB.instance == null)
            {
                return new List<ArenaFoodDefinition>();
            }

            var foods = new Dictionary<string, ArenaFoodDefinition>(StringComparer.Ordinal);
            foreach (GameObject prefab in ObjectDB.instance.m_items)
            {
                ItemDrop itemDrop = prefab?.GetComponent<ItemDrop>();
                ItemDrop.ItemData item = itemDrop?.m_itemData;
                if (item?.m_shared == null ||
                    item.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Consumable ||
                    item.m_shared.m_foodBurnTime <= 0f ||
                    item.m_shared.m_food <= 0f && item.m_shared.m_foodStamina <= 0f && item.m_shared.m_foodEitr <= 0f ||
                    requireKnown && (player == null || !player.IsKnownMaterial(item.m_shared.m_name)))
                {
                    continue;
                }

                string prefabName = prefab.name;
                if (string.IsNullOrWhiteSpace(prefabName) || foods.ContainsKey(prefabName))
                {
                    continue;
                }
                string displayName = Localization.instance == null
                    ? item.m_shared.m_name
                    : Localization.instance.Localize(item.m_shared.m_name);
                foods[prefabName] = new ArenaFoodDefinition
                {
                    PrefabName = prefabName,
                    DisplayName = string.IsNullOrWhiteSpace(displayName) ? prefabName : displayName,
                    Health = item.m_shared.m_food,
                    Stamina = item.m_shared.m_foodStamina,
                    Eitr = item.m_shared.m_foodEitr,
                    DurationSeconds = item.m_shared.m_foodBurnTime
                };
            }
            return foods.Values.OrderBy(food => food.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool ApplyTemporaryArenaFood(
            Player player,
            IList<string> selectedPrefabNames,
            IList<ArenaFoodDefinition> knownFoods)
        {
            if (player == null || selectedPrefabNames == null || knownFoods == null)
            {
                return false;
            }
            var definitions = knownFoods.ToDictionary(food => food.PrefabName, StringComparer.Ordinal);
            var arenaFoods = new List<FoodStateSnapshot>();
            foreach (string prefabName in selectedPrefabNames)
            {
                if (!definitions.TryGetValue(prefabName, out ArenaFoodDefinition food))
                {
                    return false;
                }
                arenaFoods.Add(new FoodStateSnapshot
                {
                    ItemPrefabName = food.PrefabName,
                    RemainingSeconds = food.DurationSeconds,
                    Health = food.Health,
                    Stamina = food.Stamina,
                    Eitr = food.Eitr
                });
            }

            RestoreFoods(player, arenaFoods);
            RefreshFoodStats(player);
            SEMan statusEffects = player.GetSEMan();
            statusEffects.RemoveStatusEffect(SEMan.s_statusEffectRested, true);
            StatusEffect rested = statusEffects.AddStatusEffect(SEMan.s_statusEffectRested, true);
            rested = rested ?? statusEffects.GetStatusEffect(SEMan.s_statusEffectRested);
            if (rested == null)
            {
                return false;
            }
            // SE_Rested.ResetTime recalculates a normal comfort-based TTL, so it
            // must run before the arena's non-expiring TTL is applied. The
            // effect is removed when the original pre-arena state is restored.
            rested.ResetTime();
            rested.m_ttl = 0f;
            player.SetHealth(player.GetMaxHealth());
            player.AddStamina(player.GetMaxStamina());
            player.AddEitr(player.GetMaxEitr());
            return true;
        }

        private static List<string> LoadPreferredFoodKeys(long playerId)
        {
            string value = PlayerPrefs.GetString("ArenaGuard.FoodLoadout." + playerId, string.Empty);
            return value.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim()).Where(item => item.Length > 0).Distinct(StringComparer.Ordinal).Take(3).ToList();
        }

        private static void SavePreferredFoodKeys(long playerId, IEnumerable<string> prefabNames)
        {
            PlayerPrefs.SetString("ArenaGuard.FoodLoadout." + playerId,
                string.Join("|", prefabNames ?? Enumerable.Empty<string>()));
            PlayerPrefs.Save();
        }

        internal static PlayerResourceSnapshot CaptureResources(long playerId)
        {
            Player player = FindPlayer(playerId);
            if (player == null)
            {
                return null;
            }

            var snapshot = new PlayerResourceSnapshot
            {
                PlayerId = playerId,
                Foods = player.GetFoods().Select(food => new FoodStateSnapshot
                {
                    ItemPrefabName = food.m_name,
                    RemainingSeconds = food.m_time,
                    Health = food.m_health,
                    Stamina = food.m_stamina,
                    Eitr = food.m_eitr
                }).ToList(),
                RestedRemainingSeconds = GetRestedRemainingSeconds(player),
                ArenaFoodPrefabNames = new List<string>(),
                AllowedConsumableCounts = new Dictionary<string, int>(StringComparer.Ordinal),
                AmmunitionCounts = new Dictionary<string, int>(StringComparer.Ordinal),
                EquipmentDurabilityBySlot = new Dictionary<string, float>(StringComparer.Ordinal),
                CapturedUtc = DateTime.UtcNow
            };

            int slot = 0;
            foreach (ItemDrop.ItemData item in player.GetInventory().GetAllItems())
            {
                if (item?.m_shared == null)
                {
                    continue;
                }

                string prefabName = item.m_dropPrefab == null ? item.m_shared.m_name : item.m_dropPrefab.name;
                if (ArenaRuleContext.IsRecoveryConsumable(item))
                {
                    AddCount(snapshot.AllowedConsumableCounts, prefabName, item.m_stack);
                }

                if (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Ammo ||
                    item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.AmmoNonEquipable)
                {
                    AddCount(snapshot.AmmunitionCounts, prefabName, item.m_stack);
                }

                if (item.m_equipped && item.m_shared.m_useDurability)
                {
                    snapshot.EquipmentDurabilityBySlot[(slot++).ToString() + ":" + prefabName] = item.m_durability;
                }
            }

            ResourceSnapshots[playerId] = snapshot;
            return snapshot;
        }

        internal static bool RestoreResources(long playerId, SessionOutcome outcome)
        {
            Player player = FindPlayer(playerId);
            if (player == null || !ResourceSnapshots.TryGetValue(playerId, out PlayerResourceSnapshot snapshot))
            {
                return false;
            }

            RestoreStackCounts(player.GetInventory(), snapshot.AllowedConsumableCounts);
            RestoreStackCounts(player.GetInventory(), snapshot.AmmunitionCounts);
            RestoreDurability(player.GetInventory(), snapshot.EquipmentDurabilityBySlot);
            RestoreFoods(player, snapshot.Foods);
            RefreshFoodStats(player);

            player.GetSEMan().RemoveAllStatusEffects();
            RestoreRested(player, snapshot.RestedRemainingSeconds);
            player.SetHealth(player.GetMaxHealth());
            player.AddStamina(player.GetMaxStamina());
            player.AddEitr(player.GetMaxEitr());
            ResourceSnapshots.Remove(playerId);
            return true;
        }

        internal static void RepositionEscapedOrStuckEnemies(string sessionId)
        {
            foreach (ArenaEnemyHandle handle in Enemies.Values.Where(candidate =>
                string.Equals(candidate.SessionId, sessionId, StringComparison.Ordinal)).ToList())
            {
                Character character = handle.Character;
                if (character == null || character.IsDead())
                {
                    continue;
                }

                if (!ArenaRegistry.TryGetArena(handle.ArenaId, out ArenaDefinition arena) ||
                    arena.Markers?.EnemySpawnPositions == null ||
                    arena.Markers.EnemySpawnPositions.Count == 0)
                {
                    continue;
                }

                Vector3 position = character.transform.position;
                float dx = position.x - arena.CorePosition.X;
                float dz = position.z - arena.CorePosition.Z;
                if (dx * dx + dz * dz > arena.CombatRadius * arena.CombatRadius)
                {
                    IList<int> markerOrder = ArenaSpawnSelectionPolicy.BuildOrder(
                        arena.Markers.EnemySpawnPositions.Count,
                        1,
                        NextSpawnSeed(),
                        handle.SpawnMarkerIndex);
                    int markerIndex = markerOrder[0];
                    Vector3 respawnPosition = ToVector3(arena.Markers.EnemySpawnPositions[markerIndex]);
                    character.transform.position = respawnPosition;
                    handle.SpawnPosition = respawnPosition;
                    handle.SpawnMarkerIndex = markerIndex;
                    if (ArenaRegistry.TryGetActiveSession(handle.ArenaId, out ArenaSession active))
                    {
                        AggroArenaEnemy(character, active.Request?.PlayerId ?? 0L);
                    }
                }
            }
        }

        private static void AggroArenaEnemy(Character enemy, long combatantPlayerId)
        {
            Player combatant = FindPlayer(combatantPlayerId);
            MonsterAI monsterAi = enemy?.GetBaseAI() as MonsterAI;
            if (combatant == null || monsterAi == null)
            {
                return;
            }

            try
            {
                MonsterAiWakeupMethod?.Invoke(monsterAi, null);
                monsterAi.SetHuntPlayer(true);
                monsterAi.Alert();
                MonsterAiSetTargetMethod?.Invoke(monsterAi, new object[] { combatant });
            }
            catch (Exception exception)
            {
                Plugin.Log?.LogWarning("Could not immediately aggro arena enemy " + enemy.name +
                                       " on combatant " + combatantPlayerId + ": " + exception.Message);
            }
        }

        internal static void SetRole(long playerId, ArenaRole role)
        {
            if (role == ArenaRole.Visitor)
            {
                Roles.Remove(playerId);
            }
            else
            {
                Roles[playerId] = role;
            }
        }

        internal static ArenaRole ResolveRole(long playerId)
        {
            return Roles.TryGetValue(playerId, out ArenaRole role) ? role : ArenaRole.Visitor;
        }

        internal static void Shutdown()
        {
            PrepareForWorldShutdown();
            foreach (ArenaEnemyHandle handle in Enemies.Values.ToList())
            {
                DestroyEnemy(handle);
            }
            Enemies.Clear();
            LastSpawnMarkerBySessionId.Clear();
            Roles.Clear();
            ResourceSnapshots.Clear();
            PendingRestores.Clear();
            ClientFoodPrompts.Clear();
            ClientPreparations.Clear();
            ClientQueuePromptSessions.Clear();
            ClientRestoredSnapshots.Clear();
            ClientArenaSnapshots.Clear();
            _clientAdminsMayModifyTerrain = false;
            _clientAdminsMayBuild = false;
            _clientAdminsMayPickupDroppedItems = false;
            ClientCombatStartArrivalRetryUtc.Clear();
            StaleClientCombatStartArrivalArenaIds.Clear();
            CombatStartMoveRetryUtc.Clear();
            CombatBoundaryStates.Clear();
            ActiveCombatantIds.Clear();
            AwaitingCombatStartIds.Clear();
            StaleCombatantIds.Clear();
            _nextClientCombatStartArrivalCheckTime = 0f;
            ArenaRpc.Shutdown();
            ArenaUi.CloseArenaUi();
            ArenaWorldObjects.PlacementRequested = null;
            ArenaWorldObjects.CoreRemovalRequested = null;
            ArenaWorldObjects.CoreActivated = null;
            ArenaWorldObjects.AdminMarkerRequested = null;
            ArenaWorldObjects.AdminMarkerRemovalRequested = null;
            ArenaWorldObjects.ArenaAtPositionResolver = null;
            ArenaWorldObjects.ArenaDefinitionResolver = null;
            ArenaTeleporters.GateTravelRequested = null;
            ArenaTeleporters.GateConfigurationRequested = null;
            ArenaUi.ChallengeRequested = null;
            ArenaUi.QueueAccepted = null;
            ArenaUi.ForfeitRequested = null;
            ArenaUi.LeaderboardRequested = null;
            ArenaUi.AdminMutationRequested = null;
            ArenaUi.AdminPermissionResolver = null;
            ArenaUi.CreatureListResolver = null;
            ArenaUi.FoodPreparationConfirmed = null;
            ArenaUi.FoodPreparationCancelled = null;
            ArenaRegistry.Clear();
            ArenaStore.Clear();
            _engine = null;
            _catalog = null;
            _loadedWorldUid = 0L;
            _initialized = false;
            ArenaRuleContext.Reset();
        }

        internal static void PrepareForWorldShutdown()
        {
            if (_initialized && ZNet.instance != null && ZNet.instance.IsServer() && _loadedWorldUid != 0L)
            {
                SaveWorldState();
            }
        }

        private static void ReportLethalDamage(long playerId)
        {
            if (ZNet.instance != null && ZNet.instance.IsServer())
            {
                _engine?.ReportLethalDamage(playerId);
                ProcessEngineEffects();
                return;
            }
            ArenaRpc.ReportLethalDamage();
        }

        private static void ReportArenaEnemyDeath(Character character)
        {
            string enemyId = ReadZdo(character, EnemyIdZdoKey);
            string sessionId = ReadZdo(character, EnemySessionZdoKey);
            ArenaRuleContext.ForgetEnemy(character);
            if (!string.IsNullOrEmpty(enemyId))
            {
                Enemies.Remove(enemyId);
            }
            if (!string.IsNullOrEmpty(sessionId) &&
                !Enemies.Values.Any(handle => string.Equals(handle.SessionId, sessionId, StringComparison.Ordinal)))
            {
                _engine?.ReportEncounterCleared(sessionId);
                ProcessEngineEffects();
            }
        }

        private static void RegisterArenaSummon(Character owner, GameObject spawned)
        {
            if (!IsAuthoritativeServer() || owner == null || spawned == null)
            {
                return;
            }

            string arenaId = ArenaRuleContext.GetEnemyArenaId(owner);
            string sessionId = ReadZdo(owner, EnemySessionZdoKey);
            if (string.IsNullOrWhiteSpace(arenaId) || string.IsNullOrWhiteSpace(sessionId))
            {
                return;
            }

            Character summoned = spawned.GetComponent<Character>() ?? spawned.GetComponentInChildren<Character>();
            ZNetView view = summoned == null ? null : summoned.GetComponent<ZNetView>();
            if (summoned == null || view == null || !view.IsValid())
            {
                return;
            }

            ArenaSession active = null;
            bool activeFight = _engine != null &&
                               _engine.TryGetActiveSession(arenaId, out active) &&
                               string.Equals(active.SessionId, sessionId, StringComparison.Ordinal) &&
                               active.Phase == SessionPhase.Fighting;
            if (!activeFight)
            {
                DestroyArenaCharacter(summoned);
                return;
            }

            ZDO zdo = view.GetZDO();
            string enemyId = zdo.GetString(EnemyIdZdoKey, string.Empty);
            if (string.IsNullOrWhiteSpace(enemyId))
            {
                enemyId = Guid.NewGuid().ToString("D").ToLowerInvariant();
                zdo.Set(EnemyArenaZdoKey, arenaId);
                zdo.Set(EnemySessionZdoKey, sessionId);
                zdo.Set(EnemyIdZdoKey, enemyId);
            }

            CharacterDrop drop = summoned.GetComponent<CharacterDrop>();
            drop?.SetDropsEnabled(false);
            Enemies[enemyId] = new ArenaEnemyHandle
            {
                EnemyId = enemyId,
                ArenaId = arenaId,
                SessionId = sessionId,
                Character = summoned,
                SpawnPosition = summoned.transform.position,
                SpawnMarkerIndex = -1
            };
            ArenaRuleContext.CacheEnemyArenaId(summoned, arenaId);
            AggroArenaEnemy(summoned, active.Request?.PlayerId ?? 0L);
            Plugin.Debug("Registered arena summon " + summoned.name + " for session " + sessionId + ".");
        }

        private static string ResolveEnemyArenaId(Character character)
        {
            return ReadZdo(character, EnemyArenaZdoKey);
        }

        private static long ResolveCombatant(string arenaId)
        {
            return ArenaRegistry.TryGetActiveSession(arenaId, out ArenaSession session) && session.Request != null
                ? session.Request.PlayerId
                : 0L;
        }

        private static bool MovePlayerToArenaMarker(long playerId, Func<ArenaDefinition, PositionData?> marker)
        {
            Player player = FindPlayer(playerId);
            if (player == null)
            {
                return false;
            }

            ArenaDefinition arena = ArenaRegistry.GetArenaSnapshot().FirstOrDefault(candidate =>
                candidate != null && (candidate.ArenaId == ArenaRegistry.FindProtectedArena(ToPositionData(player.transform.position))?.ArenaId ||
                                      ArenaRegistry.TryGetActiveSession(candidate.ArenaId, out ArenaSession session) &&
                                      session.Request?.PlayerId == playerId));
            PositionData? destination = arena == null ? null : marker(arena);
            return destination.HasValue && player.TeleportTo(ToVector3(destination.Value), player.transform.rotation, false);
        }

        private static Player FindPlayer(long playerId)
        {
            return Player.GetAllPlayers().FirstOrDefault(player => player != null && player.GetPlayerID() == playerId);
        }

        private static bool IsLocalAdministrator()
        {
            bool synchronizedAdmin = SynchronizationManager.Instance != null &&
                                     SynchronizationManager.Instance.PlayerIsAdmin;
            return synchronizedAdmin ||
                   ZNet.instance != null && ZNet.instance.LocalPlayerIsAdminOrHost();
        }

        private static bool IsAdministrator(Player player)
        {
            if (player == null || ZNet.instance == null)
            {
                return false;
            }

            if (player == Player.m_localPlayer)
            {
                return ZNet.instance.LocalPlayerIsAdminOrHost();
            }

            ZDOID characterId = player.GetZDOID();
            foreach (ZNet.PlayerInfo info in ZNet.instance.GetPlayerList())
            {
                if (info.m_characterID == characterId)
                {
                    return ZNet.instance.PlayerIsAdmin(info.m_userInfo.m_id);
                }
            }

            return false;
        }

        private static bool IsAuthoritativeServer()
        {
            return ZNet.instance != null && ZNet.instance.IsServer();
        }

        private static string ReadZdo(Character character, string key)
        {
            ZNetView view = character == null ? null : character.GetComponent<ZNetView>();
            return view != null && view.IsValid() ? view.GetZDO().GetString(key, string.Empty) : string.Empty;
        }

        private static void DestroyEnemy(ArenaEnemyHandle handle)
        {
            Enemies.Remove(handle.EnemyId);
            DestroyArenaCharacter(handle.Character);
        }

        private static void DestroyArenaCharacter(Character character)
        {
            if (character == null)
            {
                return;
            }

            ArenaRuleContext.ForgetEnemy(character);

            if (ZNetScene.instance != null)
            {
                ZNetScene.instance.Destroy(character.gameObject);
            }
            else
            {
                UnityEngine.Object.Destroy(character.gameObject);
            }
        }

        private static void RefreshParticipantCollisions()
        {
            List<Player> spectators = Player.GetAllPlayers()
                .Where(player => player != null &&
                                 (ResolveRole(player.GetPlayerID()) == ArenaRole.Spectator ||
                                  ResolveRole(player.GetPlayerID()) == ArenaRole.Queued))
                .ToList();
            if (spectators.Count == 0)
            {
                return;
            }

            List<Character> combatObjects = Enemies.Values.Select(handle => handle.Character)
                .Where(character => character != null).ToList();
            combatObjects.AddRange(Player.GetAllPlayers().Where(player =>
                player != null && ResolveRole(player.GetPlayerID()) == ArenaRole.Combatant));

            foreach (Player spectator in spectators)
            {
                Collider[] spectatorColliders = spectator.GetComponentsInChildren<Collider>(true);
                foreach (Character combatObject in combatObjects)
                {
                    if (combatObject == spectator)
                    {
                        continue;
                    }

                    Collider[] combatColliders = combatObject.GetComponentsInChildren<Collider>(true);
                    foreach (Collider spectatorCollider in spectatorColliders)
                    foreach (Collider combatCollider in combatColliders)
                    {
                        if (spectatorCollider != null && combatCollider != null)
                        {
                            Physics.IgnoreCollision(spectatorCollider, combatCollider, true);
                        }
                    }
                }
            }
        }

        private static void RestoreFoods(Player player, IList<FoodStateSnapshot> foods)
        {
            List<Player.Food> target = player.GetFoods();
            target.Clear();
            if (foods == null || ObjectDB.instance == null)
            {
                return;
            }

            foreach (FoodStateSnapshot food in foods)
            {
                GameObject prefab = ObjectDB.instance.GetItemPrefab(food.ItemPrefabName);
                ItemDrop itemDrop = prefab == null ? null : prefab.GetComponent<ItemDrop>();
                if (itemDrop == null)
                {
                    continue;
                }

                target.Add(new Player.Food
                {
                    m_name = food.ItemPrefabName,
                    m_item = itemDrop.m_itemData,
                    m_time = food.RemainingSeconds,
                    m_health = food.Health,
                    m_stamina = food.Stamina,
                    m_eitr = food.Eitr
                });
            }
        }

        private static void RefreshFoodStats(Player player)
        {
            if (player == null || PlayerUpdateFoodMethod == null)
            {
                return;
            }
            try
            {
                PlayerUpdateFoodMethod.Invoke(player, new object[] { 0f, true });
            }
            catch (Exception error)
            {
                Plugin.Log?.LogWarning("Could not refresh arena food totals: " + error.Message);
            }
        }

        private static float GetRestedRemainingSeconds(Player player)
        {
            StatusEffect rested = player?.GetSEMan()?.GetStatusEffect(SEMan.s_statusEffectRested);
            return rested == null ? 0f : Math.Max(0f, rested.GetRemaningTime());
        }

        private static void RestoreRested(Player player, float remainingSeconds)
        {
            if (player == null || remainingSeconds <= 0f)
            {
                return;
            }
            SEMan statusEffects = player.GetSEMan();
            StatusEffect rested = statusEffects.AddStatusEffect(SEMan.s_statusEffectRested, false);
            rested = rested ?? statusEffects.GetStatusEffect(SEMan.s_statusEffectRested);
            if (rested != null)
            {
                rested.m_ttl = remainingSeconds;
                rested.ResetTime();
            }
        }

        private static void RestoreStackCounts(Inventory inventory, IDictionary<string, int> expected)
        {
            if (inventory == null || expected == null || ObjectDB.instance == null)
            {
                return;
            }

            foreach (KeyValuePair<string, int> pair in expected)
            {
                int current = inventory.GetAllItems().Where(item =>
                    item?.m_dropPrefab != null && item.m_dropPrefab.name == pair.Key).Sum(item => item.m_stack);
                int missing = pair.Value - current;
                if (missing <= 0)
                {
                    continue;
                }

                GameObject prefab = ObjectDB.instance.GetItemPrefab(pair.Key);
                if (prefab != null)
                {
                    inventory.AddItem(prefab, missing);
                }
            }
        }

        private static void RestoreDurability(Inventory inventory, IDictionary<string, float> expected)
        {
            if (inventory == null || expected == null)
            {
                return;
            }

            var queues = expected.GroupBy(pair => pair.Key.Substring(pair.Key.IndexOf(':') + 1))
                .ToDictionary(group => group.Key, group => new Queue<float>(group.Select(pair => pair.Value)), StringComparer.Ordinal);
            foreach (ItemDrop.ItemData item in inventory.GetAllItems().Where(item => item != null && item.m_equipped))
            {
                string prefabName = item.m_dropPrefab == null ? item.m_shared.m_name : item.m_dropPrefab.name;
                if (queues.TryGetValue(prefabName, out Queue<float> values) && values.Count > 0)
                {
                    item.m_durability = values.Dequeue();
                }
            }
        }

        private static void AddCount(IDictionary<string, int> counts, string key, int amount)
        {
            counts[key] = counts.TryGetValue(key, out int current) ? current + amount : amount;
        }

        private static PositionData ToPositionData(Vector3 position)
        {
            return new PositionData { X = position.x, Y = position.y, Z = position.z };
        }

        private static Vector3 ToVector3(PositionData position)
        {
            return new Vector3(position.X, position.Y, position.Z);
        }

        private static bool Near(PositionData first, PositionData second, float radius)
        {
            float dx = first.X - second.X;
            float dz = first.Z - second.Z;
            return dx * dx + dz * dz <= radius * radius;
        }

        private static bool Near3D(PositionData first, PositionData second, float radius)
        {
            float dx = first.X - second.X;
            float dy = first.Y - second.Y;
            float dz = first.Z - second.Z;
            return dx * dx + dy * dy + dz * dz <= radius * radius;
        }

        private static string ShortId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "new";
            }
            string clean = value.Replace("-", string.Empty);
            return clean.Substring(0, Math.Min(6, clean.Length));
        }

        private static ArenaMarkerSet CopyMarkers(ArenaMarkerSet source)
        {
            if (source == null)
            {
                return null;
            }
            return new ArenaMarkerSet
            {
                StagingPosition = source.StagingPosition,
                CombatantStartPosition = source.CombatantStartPosition,
                HubGatePosition = source.HubGatePosition,
                EnemySpawnPositions = source.EnemySpawnPositions == null
                    ? new List<PositionData>()
                    : new List<PositionData>(source.EnemySpawnPositions)
            };
        }

        private static bool MarkersEqual(ArenaMarkerSet first, ArenaMarkerSet second)
        {
            if (first == null || second == null || first.EnemySpawnPositions == null ||
                second.EnemySpawnPositions == null || first.EnemySpawnPositions.Count != second.EnemySpawnPositions.Count)
            {
                return false;
            }
            if (!SamePosition(first.StagingPosition, second.StagingPosition) ||
                !SamePosition(first.CombatantStartPosition, second.CombatantStartPosition) ||
                !SamePosition(first.HubGatePosition, second.HubGatePosition))
            {
                return false;
            }
            for (int index = 0; index < first.EnemySpawnPositions.Count; index++)
            {
                if (!SamePosition(first.EnemySpawnPositions[index], second.EnemySpawnPositions[index]))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool SamePosition(PositionData first, PositionData second)
        {
            return Math.Abs(first.X - second.X) < 0.001f &&
                   Math.Abs(first.Y - second.Y) < 0.001f &&
                   Math.Abs(first.Z - second.Z) < 0.001f;
        }

        private sealed class ArenaEnemyHandle
        {
            internal string EnemyId;
            internal string ArenaId;
            internal string SessionId;
            internal Character Character;
            internal Vector3 SpawnPosition;
            internal int SpawnMarkerIndex;
        }

        private sealed class PendingRestore
        {
            internal long PlayerId;
            internal PlayerResourceSnapshot Snapshot;
            internal SessionOutcome Outcome;
            internal ArenaSession Session;
            internal DateTime NextAttemptUtc;
        }

        private sealed class ClientPreparationState
        {
            internal PlayerResourceSnapshot Snapshot;
            internal DateTime SubmittedUtc;
            internal bool Accepted;
        }

        private sealed class CombatBoundaryState
        {
            internal string ArenaId;
            internal DateTime OutsideSinceUtc;
            internal DateTime NextPopupUtc;
        }

    }
}
