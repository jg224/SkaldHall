using System;
using System.Collections.Generic;
using System.Linq;
using ArenaGuard.Arenas;
using ArenaGuard.Domain;
using ArenaGuard.Rules;
using UnityEngine;

namespace ArenaGuard.Networking
{
    internal enum ArenaAdminMutationKind
    {
        RegisterArena = 1,
        RemoveArena = 2,
        SetMarkers = 3,
        RegisterGate = 4,
        RemoveGate = 5,
        SetMarker = 6,
        RemoveMarker = 7,
        SetAdminTerrainPermission = 8,
        SetAdminBuildingPermission = 9,
        SetAdminPickupPermission = 10,
        RequestAdminStatus = 11,
        AbortArena = 12,
        AbortAllArenas = 13,
        ClearArenaQueue = 14
    }

    internal sealed class ArenaAdminMutation
    {
        internal ArenaAdminMutationKind Kind;
        internal string TargetId;
        internal ArenaDefinition Arena;
        internal ArenaMarkerSet Markers;
        internal ArenaGateDefinition Gate;
        internal int MarkerKind;
        internal int MarkerSlot;
        internal PositionData MarkerPosition;
        internal bool Enabled;
    }

    internal sealed class ArenaRequestContext
    {
        internal long PeerUid;
        internal long PlayerId;
        internal string PlayerName;
        internal bool IsAdmin;
        internal PositionData Position;
    }

    internal sealed class ArenaRpcResult
    {
        internal bool Success;
        internal string Message;

        internal static ArenaRpcResult Accepted(string message = "")
        {
            return new ArenaRpcResult { Success = true, Message = message ?? string.Empty };
        }

        internal static ArenaRpcResult Rejected(string message)
        {
            return new ArenaRpcResult { Success = false, Message = message ?? "Request rejected." };
        }
    }

    /// <summary>Composition hooks. Server mutations remain outside the transport layer.</summary>
    internal sealed class ArenaRpcCallbacks
    {
        internal Func<ArenaRequestContext, ChallengeRequest, ArenaRpcResult> ChallengeRequested { get; set; }
        internal Func<ArenaRequestContext, ArenaRpcResult> QueueCallAccepted { get; set; }
        internal Func<ArenaRequestContext, ArenaRpcResult> ForfeitRequested { get; set; }
        internal Func<ArenaRequestContext, ArenaRpcResult> LethalDamageReported { get; set; }
        internal Func<ArenaRequestContext, ArenaRpcResult> CombatStartArrived { get; set; }
        internal Func<ArenaRequestContext, ArenaAdminMutation, ArenaRpcResult> AdminMutationRequested { get; set; }
        internal Func<ArenaRequestContext, ArenaGateDefinition, bool, ArenaRpcResult> GateTravelAuthorizing { get; set; }
        internal Func<ArenaRequestContext, ArenaGateTravelAuthorization, bool, ArenaRpcResult> GateTravelCompleted { get; set; }
        internal Func<ArenaGateTravelAuthorization, bool> ClientGateTravelAuthorized { get; set; }
        internal Func<ArenaRequestContext, PlayerResourceSnapshot, ArenaRpcResult> ResourceSnapshotReceived { get; set; }
        internal Func<PlayerResourceSnapshot, SessionOutcome, bool> ClientResourceRestoreRequested { get; set; }
        internal Action<ArenaRequestContext, string, bool> ResourceRestoreCompleted { get; set; }
        internal Func<PositionData, float, bool> ClientMoveRequested { get; set; }
        internal Func<string, long, ArenaClientSnapshot> SnapshotRequested { get; set; }
        internal Func<LeaderboardKey, List<LeaderboardEntry>> LeaderboardRequested { get; set; }
        internal Action<ArenaClientSnapshot> ClientSnapshotReceived { get; set; }
        internal Action<string> ClientArenaRemoved { get; set; }
        internal Action<ArenaGateDefinition> ClientGateConfigurationReceived { get; set; }
        internal Action<LeaderboardKey, List<LeaderboardEntry>> ClientLeaderboardReceived { get; set; }
        internal Action<bool, string> ClientActionResultReceived { get; set; }
        internal Action<long> ServerPeerConnected { get; set; }
    }

    internal sealed class ArenaGateTravelAuthorization
    {
        internal string TravelToken;
        internal string GateId;
        internal string ArenaId;
        internal PositionData Destination;
        internal float RotationY;
        internal bool Returning;
    }

    /// <summary>
    /// Versioned routed RPC transport. Every server mutation derives character identity and
    /// administrator authority from the sending peer instead of trusting client payload fields.
    /// </summary>
    internal static class ArenaRpc
    {
        private sealed class PendingGateTravel
        {
            internal ArenaRequestContext Context;
            internal ArenaGateTravelAuthorization Authorization;
            internal DateTime ExpiresUtc;
        }

        private sealed class PendingResourceRestore
        {
            internal ArenaRequestContext Context;
            internal DateTime ExpiresUtc;
        }

        private const int ProtocolVersion = 8;
        private const int MaximumPackageBytes = 1024 * 1024;
        private const int MaximumLeaderboardEntries = 500;
        private const int MaximumStringLength = 256;
        private const int MaximumResourceEntries = 1024;
        private const int MaximumAppliedRestoreKeys = 128;
        private const float MaximumGateUseDistance = 10f;
        private static readonly TimeSpan GateTravelTimeout = TimeSpan.FromSeconds(30);
        private const string ChallengeRpc = Plugin.PluginGuid + ".RequestChallenge";
        private const string AcceptRpc = Plugin.PluginGuid + ".AcceptQueueCall";
        private const string ForfeitRpc = Plugin.PluginGuid + ".RequestForfeit";
        private const string LethalDamageRpc = Plugin.PluginGuid + ".ReportLethalDamage";
        private const string CombatStartArrivalRpc = Plugin.PluginGuid + ".CombatStartArrived";
        private const string AdminRpc = Plugin.PluginGuid + ".RequestAdminMutation";
        private const string SnapshotRpc = Plugin.PluginGuid + ".ArenaSnapshot";
        private const string ArenaRemovedRpc = Plugin.PluginGuid + ".ArenaRemoved";
        private const string GateConfigurationRpc = Plugin.PluginGuid + ".GateConfiguration";
        private const string LeaderboardRequestRpc = Plugin.PluginGuid + ".RequestLeaderboard";
        private const string LeaderboardResponseRpc = Plugin.PluginGuid + ".Leaderboard";
        private const string ResultRpc = Plugin.PluginGuid + ".ActionResult";
        private const string GateTravelRequestRpc = Plugin.PluginGuid + ".RequestGateTravel";
        private const string GateTravelAuthorizationRpc = Plugin.PluginGuid + ".AuthorizeGateTravel";
        private const string GateTravelAcknowledgementRpc = Plugin.PluginGuid + ".AcknowledgeGateTravel";
        private const string ResourceSnapshotRpc = Plugin.PluginGuid + ".SubmitResourceSnapshot";
        private const string ResourceRestoreRpc = Plugin.PluginGuid + ".RestoreResources";
        private const string ResourceRestoreAcknowledgementRpc = Plugin.PluginGuid + ".AcknowledgeResourceRestore";
        private const string MovePlayerRpc = Plugin.PluginGuid + ".MovePlayer";

        private static ZRoutedRpc _registeredInstance;
        private static Action<long> _newPeerHandler;
        private static ArenaRpcCallbacks _callbacks = new ArenaRpcCallbacks();
        private static readonly Dictionary<string, PendingGateTravel> PendingGateTravels =
            new Dictionary<string, PendingGateTravel>(StringComparer.Ordinal);
        private static readonly Dictionary<string, PendingResourceRestore> PendingResourceRestores =
            new Dictionary<string, PendingResourceRestore>(StringComparer.Ordinal);
        private static readonly HashSet<string> AppliedResourceRestoreKeys =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly Queue<string> AppliedResourceRestoreOrder = new Queue<string>();

        internal static void Configure(ArenaRpcCallbacks callbacks)
        {
            _callbacks = callbacks ?? new ArenaRpcCallbacks();
        }

        internal static void Register()
        {
            Register(ZRoutedRpc.instance);
        }

        internal static void Register(ZRoutedRpc rpc)
        {
            if (rpc == null) return;
            if (!ReferenceEquals(_registeredInstance, rpc))
            {
                DetachPeerHook();
                rpc.Register<ZPackage>(ChallengeRpc, OnChallengeRequest);
                rpc.Register<ZPackage>(AcceptRpc, OnAcceptQueueCall);
                rpc.Register<ZPackage>(ForfeitRpc, OnForfeitRequest);
                rpc.Register<ZPackage>(LethalDamageRpc, OnLethalDamageReport);
                rpc.Register<ZPackage>(CombatStartArrivalRpc, OnCombatStartArrival);
                rpc.Register<ZPackage>(AdminRpc, OnAdminMutation);
                rpc.Register<ZPackage>(SnapshotRpc, OnArenaSnapshot);
                rpc.Register<ZPackage>(ArenaRemovedRpc, OnArenaRemoved);
                rpc.Register<ZPackage>(GateConfigurationRpc, OnGateConfiguration);
                rpc.Register<ZPackage>(LeaderboardRequestRpc, OnLeaderboardRequest);
                rpc.Register<ZPackage>(LeaderboardResponseRpc, OnLeaderboardResponse);
                rpc.Register<ZPackage>(ResultRpc, OnActionResult);
                rpc.Register<ZPackage>(GateTravelRequestRpc, OnGateTravelRequest);
                rpc.Register<ZPackage>(GateTravelAuthorizationRpc, OnGateTravelAuthorization);
                rpc.Register<ZPackage>(GateTravelAcknowledgementRpc, OnGateTravelAcknowledgement);
                rpc.Register<ZPackage>(ResourceSnapshotRpc, OnResourceSnapshot);
                rpc.Register<ZPackage>(ResourceRestoreRpc, OnResourceRestore);
                rpc.Register<ZPackage>(ResourceRestoreAcknowledgementRpc, OnResourceRestoreAcknowledgement);
                rpc.Register<ZPackage>(MovePlayerRpc, OnMovePlayer);
                _registeredInstance = rpc;
                Plugin.Log?.LogInfo($"Registered ArenaGuard routed RPC protocol {ProtocolVersion}.");
            }

            DetachPeerHook();
            if (ZNet.instance != null && ZNet.instance.IsServer())
            {
                _newPeerHandler = OnNewPeer;
                rpc.m_onNewPeer += _newPeerHandler;
            }
        }

        internal static void RequestChallenge(ChallengeRequest request)
        {
            if (!TryPrepareLocalRequest(out ZNet znet, out ZRoutedRpc rpc)) return;
            ChallengeRequest wire = SanitizeClientChallenge(request);
            if (znet.IsServer())
            {
                if (TryResolveLocalContext(out ArenaRequestContext context))
                {
                    wire = StampIdentity(wire, context);
                    ReceiveLocalResult(ValidateChallengeLocation(context, wire, out string rejection)
                        ? Invoke(_callbacks.ChallengeRequested, context, wire)
                        : ArenaRpcResult.Rejected(rejection));
                }
                return;
            }

            var package = NewPackage();
            WriteChallengeRequest(package, wire, false);
            rpc.InvokeRoutedRPC(ChallengeRpc, package);
        }

        internal static void AcceptQueueCall(string requestId)
        {
            AcceptQueueCall();
        }

        internal static void AcceptQueueCall()
        {
            if (!TryPrepareLocalRequest(out ZNet znet, out ZRoutedRpc rpc)) return;
            if (znet.IsServer())
            {
                if (TryResolveLocalContext(out ArenaRequestContext context))
                    ReceiveLocalResult(Invoke(_callbacks.QueueCallAccepted, context));
                return;
            }
            rpc.InvokeRoutedRPC(AcceptRpc, NewPackage());
        }

        internal static void RequestForfeit(string sessionId)
        {
            RequestForfeit();
        }

        internal static void RequestForfeit()
        {
            if (!TryPrepareLocalRequest(out ZNet znet, out ZRoutedRpc rpc)) return;
            if (znet.IsServer())
            {
                if (TryResolveLocalContext(out ArenaRequestContext context))
                    ReceiveLocalResult(Invoke(_callbacks.ForfeitRequested, context));
                return;
            }
            rpc.InvokeRoutedRPC(ForfeitRpc, NewPackage());
        }

        /// <summary>
        /// Reports an intercepted lethal hit. The server derives the player identity from the
        /// authenticated routed-RPC sender; no client-selected player or session ID is accepted.
        /// </summary>
        internal static void ReportLethalDamage()
        {
            if (!TryPrepareLocalRequest(out ZNet znet, out ZRoutedRpc rpc)) return;
            if (znet.IsServer())
            {
                if (TryResolveLocalContext(out ArenaRequestContext context))
                    ReceiveLocalResult(Invoke(_callbacks.LethalDamageReported, context));
                return;
            }
            rpc.InvokeRoutedRPC(LethalDamageRpc, NewPackage());
        }

        /// <summary>
        /// Confirms completion of the server-commanded Combat Start move. The
        /// request carries no player, arena, session, or destination; the server
        /// resolves all authority from the authenticated routed sender.
        /// </summary>
        internal static void ReportCombatStartArrival()
        {
            if (!TryPrepareLocalRequest(out ZNet znet, out ZRoutedRpc rpc)) return;
            if (znet.IsServer())
            {
                if (TryResolveLocalContext(out ArenaRequestContext context))
                    ReceiveLocalResult(Invoke(_callbacks.CombatStartArrived, context));
                return;
            }
            rpc.InvokeRoutedRPC(CombatStartArrivalRpc, NewPackage());
        }

        internal static void RequestAdminMutation(ArenaAdminMutation mutation)
        {
            if (mutation == null) throw new ArgumentNullException(nameof(mutation));
            if (!TryPrepareLocalRequest(out ZNet znet, out ZRoutedRpc rpc)) return;
            if (znet.IsServer())
            {
                if (!TryResolveLocalContext(out ArenaRequestContext context))
                {
                    ReceiveLocalResult(ArenaRpcResult.Rejected(
                        "ArenaGuard could not resolve the local administrator identity."));
                    return;
                }
                ValidateAdminMutation(mutation);
                ReceiveLocalResult(InvokeAdmin(context, mutation));
                return;
            }

            var package = NewPackage();
            WriteAdminMutation(package, mutation);
            rpc.InvokeRoutedRPC(AdminRpc, package);
        }

        /// <summary>
        /// Begins a two-phase portal move. The server validates the sending character and persists
        /// entrance routing before returning a targeted destination. Completion is acknowledged only
        /// after the owning client accepts TeleportTo.
        /// </summary>
        internal static bool RequestGateTravel(string gateId, bool returning)
        {
            if (!returning) gateId = RequireId(gateId, "gate ID");
            else gateId = string.Empty;
            if (!TryPrepareLocalRequest(out ZNet znet, out ZRoutedRpc rpc)) return false;
            if (znet.IsServer())
            {
                if (!TryResolveLocalContext(out ArenaRequestContext context)) return false;
                ArenaRpcResult result = TryAuthorizeGateTravel(context, gateId, returning,
                    out ArenaGateTravelAuthorization authorization);
                if (!result.Success)
                {
                    ReceiveLocalResult(result);
                    return false;
                }
                CompleteLocalGateTravel(context, authorization);
                return true;
            }

            var package = NewPackage();
            package.Write(returning);
            package.Write(gateId);
            rpc.InvokeRoutedRPC(GateTravelRequestRpc, package);
            return true;
        }

        internal static void SubmitResourceSnapshot(PlayerResourceSnapshot snapshot)
        {
            PlayerResourceSnapshot wire = ValidateAndCopyResourceSnapshot(snapshot, 0, false);
            if (!TryPrepareLocalRequest(out ZNet znet, out ZRoutedRpc rpc)) return;
            if (znet.IsServer())
            {
                if (!TryResolveLocalContext(out ArenaRequestContext context)) return;
                wire.PlayerId = context.PlayerId;
                wire.CapturedUtc = DateTime.UtcNow;
                ReceiveLocalResult(_callbacks.ResourceSnapshotReceived?.Invoke(context, wire) ??
                                   ArenaRpcResult.Rejected("Arena resource capture is not ready."));
                return;
            }
            var package = NewPackage();
            WriteResourceSnapshot(package, wire, false);
            rpc.InvokeRoutedRPC(ResourceSnapshotRpc, package);
        }

        internal static bool SendResourceRestore(long playerId, PlayerResourceSnapshot snapshot, SessionOutcome outcome)
        {
            if (playerId <= 0 || outcome <= SessionOutcome.None || outcome > SessionOutcome.RuntimeError) return false;
            PlayerResourceSnapshot wire = ValidateAndCopyResourceSnapshot(snapshot, playerId, true);
            RemoveExpiredResourceRestores();
            ZNet znet = ZNet.instance;
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (znet == null || rpc == null || !znet.IsServer()) return false;
            string token = Guid.NewGuid().ToString("N").ToLowerInvariant();
            if (Player.m_localPlayer != null && Player.m_localPlayer.GetPlayerID() == playerId)
            {
                bool success = _callbacks.ClientResourceRestoreRequested?.Invoke(wire, outcome) == true;
                if (TryResolveLocalContext(out ArenaRequestContext context))
                    _callbacks.ResourceRestoreCompleted?.Invoke(context, token, success);
                return success;
            }
            foreach (ZNetPeer peer in znet.GetConnectedPeers())
            {
                if (!TryResolvePeer(peer, out ArenaRequestContext context) || context.PlayerId != playerId) continue;
                if (PendingResourceRestores.Values.Any(pending =>
                    pending.Context.PlayerId == context.PlayerId && pending.Context.PeerUid == context.PeerUid))
                    return true;
                PendingResourceRestores[token] = new PendingResourceRestore
                {
                    Context = context,
                    ExpiresUtc = DateTime.UtcNow.Add(GateTravelTimeout)
                };
                var package = NewPackage();
                package.Write(token);
                package.Write((int)outcome);
                WriteResourceSnapshot(package, wire, true);
                rpc.InvokeRoutedRPC(peer.m_uid, ResourceRestoreRpc, package);
                return true;
            }
            return false;
        }

        internal static bool SendPlayerMove(long playerId, PositionData destination, float rotationY = 0f)
        {
            if (playerId <= 0 || !Finite(destination.X) || !Finite(destination.Y) || !Finite(destination.Z) ||
                !Finite(rotationY)) return false;
            ZNet znet = ZNet.instance;
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (znet == null || rpc == null || !znet.IsServer()) return false;
            if (Player.m_localPlayer != null && Player.m_localPlayer.GetPlayerID() == playerId)
                return _callbacks.ClientMoveRequested?.Invoke(destination, rotationY) == true;
            foreach (ZNetPeer peer in znet.GetConnectedPeers())
            {
                if (!TryResolvePeer(peer, out ArenaRequestContext context) || context.PlayerId != playerId) continue;
                var package = NewPackage();
                WritePosition(package, destination);
                package.Write(rotationY);
                rpc.InvokeRoutedRPC(peer.m_uid, MovePlayerRpc, package);
                return true;
            }
            return false;
        }

        internal static void RequestLeaderboard(LeaderboardKey key)
        {
            LeaderboardKey normalized = NormalizeLeaderboardKey(key);
            if (!TryPrepareLocalRequest(out ZNet znet, out ZRoutedRpc rpc)) return;
            if (znet.IsServer())
            {
                List<LeaderboardEntry> entries = _callbacks.LeaderboardRequested?.Invoke(normalized) ?? new List<LeaderboardEntry>();
                _callbacks.ClientLeaderboardReceived?.Invoke(normalized, entries);
                return;
            }

            var package = NewPackage();
            WriteLeaderboardKey(package, normalized);
            rpc.InvokeRoutedRPC(LeaderboardRequestRpc, package);
        }

        internal static void BroadcastArenaState(string arenaId)
        {
            arenaId = ArenaRegistry.NormalizeId(arenaId);
            ZNet znet = ZNet.instance;
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (arenaId == null || znet == null || rpc == null || !znet.IsServer()) return;

            Player local = Player.m_localPlayer;
            if (local != null)
            {
                ArenaClientSnapshot localSnapshot = _callbacks.SnapshotRequested?.Invoke(arenaId, local.GetPlayerID());
                if (localSnapshot != null) _callbacks.ClientSnapshotReceived?.Invoke(localSnapshot);
            }

            foreach (ZNetPeer peer in znet.GetConnectedPeers())
            {
                if (peer != null) SendArenaStateToPeer(peer.m_uid, arenaId);
            }
        }

        internal static void BroadcastArenaRemoval(string arenaId)
        {
            arenaId = RequireId(arenaId, "arena ID");
            ZNet znet = ZNet.instance;
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (znet == null || rpc == null || !znet.IsServer()) return;
            _callbacks.ClientArenaRemoved?.Invoke(arenaId);
            foreach (ZNetPeer peer in znet.GetConnectedPeers())
            {
                if (peer == null) continue;
                var package = NewPackage();
                package.Write(arenaId);
                rpc.InvokeRoutedRPC(peer.m_uid, ArenaRemovedRpc, package);
            }
        }

        internal static void BroadcastGateConfiguration(ArenaGateDefinition gate)
        {
            ArenaGateDefinition wire = ValidateAndCopyGate(gate);
            ZNet znet = ZNet.instance;
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (znet == null || rpc == null || !znet.IsServer()) return;

            _callbacks.ClientGateConfigurationReceived?.Invoke(wire);
            foreach (ZNetPeer peer in znet.GetConnectedPeers())
            {
                if (peer == null) continue;
                var package = NewPackage();
                WriteGate(package, wire);
                rpc.InvokeRoutedRPC(peer.m_uid, GateConfigurationRpc, package);
            }
        }

        private static void SendArenaStateToPeer(long peerUid, string arenaId)
        {
            ZNet znet = ZNet.instance;
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (znet == null || rpc == null || !znet.IsServer()) return;
            ZNetPeer peer = znet.GetPeer(peerUid);
            long playerId = TryResolvePeer(peer, out ArenaRequestContext context) ? context.PlayerId : 0L;
            ArenaClientSnapshot snapshot = _callbacks.SnapshotRequested?.Invoke(arenaId, playerId);
            if (snapshot == null) return;
            var package = NewPackage();
            WriteArenaSnapshot(package, snapshot);
            rpc.InvokeRoutedRPC(peerUid, SnapshotRpc, package);
        }

        internal static void SendLeaderboard(long playerId, LeaderboardKey key)
        {
            if (playerId <= 0) return;
            LeaderboardKey normalized = NormalizeLeaderboardKey(key);
            List<LeaderboardEntry> entries = (_callbacks.LeaderboardRequested?.Invoke(normalized) ??
                                              new List<LeaderboardEntry>()).Take(MaximumLeaderboardEntries).ToList();
            ZNet znet = ZNet.instance;
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (znet == null || rpc == null || !znet.IsServer()) return;

            if (Player.m_localPlayer != null && Player.m_localPlayer.GetPlayerID() == playerId)
            {
                _callbacks.ClientLeaderboardReceived?.Invoke(normalized, entries);
                return;
            }

            foreach (ZNetPeer peer in znet.GetConnectedPeers())
            {
                if (!TryResolvePeer(peer, out ArenaRequestContext context) || context.PlayerId != playerId) continue;
                var package = NewPackage();
                WriteLeaderboardKey(package, normalized);
                package.Write(entries.Count);
                foreach (LeaderboardEntry entry in entries) WriteLeaderboardEntry(package, entry);
                rpc.InvokeRoutedRPC(peer.m_uid, LeaderboardResponseRpc, package);
                return;
            }
        }

        internal static void Shutdown()
        {
            foreach (PendingGateTravel pending in PendingGateTravels.Values.ToList())
            {
                try { CompleteGateTravel(pending.Context, pending.Authorization, false); }
                catch (Exception exception)
                {
                    Plugin.Log?.LogWarning("Could not cancel pending gate travel during shutdown: " + exception.Message);
                }
            }
            PendingGateTravels.Clear();
            PendingResourceRestores.Clear();
            AppliedResourceRestoreKeys.Clear();
            AppliedResourceRestoreOrder.Clear();
            DetachPeerHook();
            _registeredInstance = null;
            _callbacks = new ArenaRpcCallbacks();
        }

        private static void OnNewPeer(long peerUid)
        {
            if (_callbacks.ServerPeerConnected != null)
            {
                _callbacks.ServerPeerConnected(peerUid);
                return;
            }
            foreach (ArenaDefinition arena in ArenaRegistry.GetArenaSnapshot())
                SendArenaStateToPeer(peerUid, arena.ArenaId);
        }

        private static void OnChallengeRequest(long sender, ZPackage package)
        {
            if (!TryBeginServerRequest(sender, package, out ArenaRequestContext context)) return;
            try
            {
                ChallengeRequest request = ReadChallengeRequest(package, false);
                RequireConsumed(package);
                request = StampIdentity(request, context);

                if (!ValidateChallengeLocation(context, request, out string rejection))
                    throw new InvalidOperationException(rejection);

                SendResult(sender, Invoke(_callbacks.ChallengeRequested, context, request));
            }
            catch (Exception exception)
            {
                Reject(sender, "challenge request", exception);
            }
        }

        private static void OnAcceptQueueCall(long sender, ZPackage package)
        {
            if (!TryBeginServerRequest(sender, package, out ArenaRequestContext context)) return;
            try
            {
                RequireConsumed(package);
                SendResult(sender, Invoke(_callbacks.QueueCallAccepted, context));
            }
            catch (Exception exception)
            {
                Reject(sender, "queue acceptance", exception);
            }
        }

        private static void OnForfeitRequest(long sender, ZPackage package)
        {
            if (!TryBeginServerRequest(sender, package, out ArenaRequestContext context)) return;
            try
            {
                RequireConsumed(package);
                SendResult(sender, Invoke(_callbacks.ForfeitRequested, context));
            }
            catch (Exception exception)
            {
                Reject(sender, "forfeit request", exception);
            }
        }

        private static void OnLethalDamageReport(long sender, ZPackage package)
        {
            if (!TryBeginServerRequest(sender, package, out ArenaRequestContext context)) return;
            try
            {
                RequireConsumed(package);
                SendResult(sender, Invoke(_callbacks.LethalDamageReported, context));
            }
            catch (Exception exception)
            {
                Reject(sender, "lethal damage report", exception);
            }
        }

        private static void OnCombatStartArrival(long sender, ZPackage package)
        {
            if (!TryBeginServerRequest(sender, package, out ArenaRequestContext context)) return;
            try
            {
                RequireConsumed(package);
                SendResult(sender, Invoke(_callbacks.CombatStartArrived, context));
            }
            catch (Exception exception)
            {
                Reject(sender, "combat start arrival", exception);
            }
        }

        private static void OnAdminMutation(long sender, ZPackage package)
        {
            if (!TryBeginServerRequest(sender, package, out ArenaRequestContext context)) return;
            try
            {
                if (!context.IsAdmin) throw new UnauthorizedAccessException("Only a server administrator may change an arena.");
                ArenaAdminMutation mutation = ReadAdminMutation(package);
                RequireConsumed(package);
                ValidateAdminMutation(mutation);
                SendResult(sender, InvokeAdmin(context, mutation));
            }
            catch (Exception exception)
            {
                Reject(sender, "administrator mutation", exception);
            }
        }

        private static void OnLeaderboardRequest(long sender, ZPackage package)
        {
            if (!TryBeginServerRequest(sender, package, out ArenaRequestContext context)) return;
            try
            {
                LeaderboardKey key = ReadLeaderboardKey(package);
                RequireConsumed(package);
                SendLeaderboard(context.PlayerId, key);
            }
            catch (Exception exception)
            {
                Reject(sender, "leaderboard request", exception);
            }
        }

        private static void OnArenaSnapshot(long sender, ZPackage package)
        {
            if (!TryBeginClientResponse(sender, package)) return;
            try
            {
                ArenaClientSnapshot snapshot = ReadArenaSnapshot(package);
                RequireConsumed(package);
                _callbacks.ClientSnapshotReceived?.Invoke(snapshot);
            }
            catch (Exception exception)
            {
                Plugin.Log?.LogError($"Rejected invalid arena snapshot: {exception}");
            }
        }

        private static void OnArenaRemoved(long sender, ZPackage package)
        {
            if (!TryBeginClientResponse(sender, package)) return;
            try
            {
                string arenaId = RequireId(ReadString(package, 64), "arena ID");
                RequireConsumed(package);
                _callbacks.ClientArenaRemoved?.Invoke(arenaId);
            }
            catch (Exception exception)
            {
                Plugin.Log?.LogError($"Rejected invalid arena removal: {exception}");
            }
        }

        private static void OnGateConfiguration(long sender, ZPackage package)
        {
            if (!TryBeginClientResponse(sender, package)) return;
            try
            {
                ArenaGateDefinition gate = ReadGate(package);
                RequireConsumed(package);
                _callbacks.ClientGateConfigurationReceived?.Invoke(gate);
            }
            catch (Exception exception)
            {
                Plugin.Log?.LogError($"Rejected invalid gate configuration: {exception}");
            }
        }

        private static void OnLeaderboardResponse(long sender, ZPackage package)
        {
            if (!TryBeginClientResponse(sender, package)) return;
            try
            {
                LeaderboardKey key = ReadLeaderboardKey(package);
                int count = package.ReadInt();
                if (count < 0 || count > MaximumLeaderboardEntries)
                    throw new InvalidOperationException("Leaderboard result count is invalid.");
                var entries = new List<LeaderboardEntry>(count);
                for (int i = 0; i < count; i++) entries.Add(ReadLeaderboardEntry(package, key));
                RequireConsumed(package);
                _callbacks.ClientLeaderboardReceived?.Invoke(key, entries);
            }
            catch (Exception exception)
            {
                Plugin.Log?.LogError($"Rejected invalid leaderboard response: {exception}");
            }
        }

        private static void OnActionResult(long sender, ZPackage package)
        {
            if (!TryBeginClientResponse(sender, package)) return;
            try
            {
                bool success = package.ReadBool();
                string message = ReadString(package, MaximumStringLength);
                RequireConsumed(package);
                _callbacks.ClientActionResultReceived?.Invoke(success, message);
            }
            catch (Exception exception)
            {
                Plugin.Log?.LogError($"Rejected invalid arena action result: {exception}");
            }
        }

        private static void OnGateTravelRequest(long sender, ZPackage package)
        {
            if (!TryBeginServerRequest(sender, package, out ArenaRequestContext context)) return;
            try
            {
                bool returning = package.ReadBool();
                string gateId = ReadString(package, 64);
                RequireConsumed(package);
                if (!returning) gateId = RequireId(gateId, "gate ID");
                else if (gateId.Length != 0) throw new InvalidOperationException("A return request cannot select a destination gate.");

                ArenaRpcResult result = TryAuthorizeGateTravel(context, gateId, returning,
                    out ArenaGateTravelAuthorization authorization);
                if (!result.Success)
                {
                    SendResult(sender, result);
                    return;
                }

                var response = NewPackage();
                WriteGateTravelAuthorization(response, authorization);
                ZRoutedRpc.instance?.InvokeRoutedRPC(sender, GateTravelAuthorizationRpc, response);
            }
            catch (Exception exception)
            {
                Reject(sender, "gate travel request", exception);
            }
        }

        private static void OnGateTravelAuthorization(long sender, ZPackage package)
        {
            if (!TryBeginClientResponse(sender, package)) return;
            ArenaGateTravelAuthorization authorization = null;
            bool accepted = false;
            try
            {
                authorization = ReadGateTravelAuthorization(package);
                RequireConsumed(package);
                accepted = _callbacks.ClientGateTravelAuthorized?.Invoke(authorization) == true;
            }
            catch (Exception exception)
            {
                Plugin.Log?.LogError($"Could not apply ArenaGuard gate destination: {exception}");
            }

            if (authorization == null) return;
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (rpc == null) return;
            var acknowledgement = NewPackage();
            acknowledgement.Write(authorization.TravelToken);
            acknowledgement.Write(accepted);
            rpc.InvokeRoutedRPC(GateTravelAcknowledgementRpc, acknowledgement);
        }

        private static void OnGateTravelAcknowledgement(long sender, ZPackage package)
        {
            if (!TryBeginServerRequest(sender, package, out ArenaRequestContext context)) return;
            try
            {
                string token = RequireId(ReadString(package, 64), "travel token");
                bool accepted = package.ReadBool();
                RequireConsumed(package);
                RemoveExpiredGateTravels();
                if (!PendingGateTravels.TryGetValue(token, out PendingGateTravel pending) ||
                    pending.Context.PlayerId != context.PlayerId || pending.Context.PeerUid != context.PeerUid)
                    throw new InvalidOperationException("Gate travel authorization is missing, expired, or belongs to another player.");
                PendingGateTravels.Remove(token);
                ArenaRpcResult result = CompleteGateTravel(context, pending.Authorization, accepted);
                SendResult(sender, result);
            }
            catch (Exception exception)
            {
                Reject(sender, "gate travel acknowledgement", exception);
            }
        }

        private static void OnResourceSnapshot(long sender, ZPackage package)
        {
            if (!TryBeginServerRequest(sender, package, out ArenaRequestContext context)) return;
            try
            {
                PlayerResourceSnapshot snapshot = ReadResourceSnapshot(package, false);
                RequireConsumed(package);
                snapshot.PlayerId = context.PlayerId;
                snapshot.CapturedUtc = DateTime.UtcNow;
                ArenaRpcResult result = _callbacks.ResourceSnapshotReceived?.Invoke(context, snapshot) ??
                                        ArenaRpcResult.Rejected("Arena resource capture is not ready.");
                SendResult(sender, result);
            }
            catch (Exception exception)
            {
                Reject(sender, "resource snapshot", exception);
            }
        }

        private static void OnResourceRestore(long sender, ZPackage package)
        {
            if (!TryBeginClientResponse(sender, package)) return;
            string token = null;
            bool success = false;
            try
            {
                token = RequireId(ReadString(package, 64), "resource restore token");
                SessionOutcome outcome = (SessionOutcome)package.ReadInt();
                if (outcome <= SessionOutcome.None || outcome > SessionOutcome.RuntimeError)
                    throw new InvalidOperationException("Resource restore outcome is invalid.");
                PlayerResourceSnapshot snapshot = ReadResourceSnapshot(package, true);
                RequireConsumed(package);
                Player local = Player.m_localPlayer;
                if (local == null || local.GetPlayerID() != snapshot.PlayerId)
                    throw new InvalidOperationException("Resource restore targets another character.");
                string restoreKey = ResourceRestoreKey(snapshot);
                success = AppliedResourceRestoreKeys.Contains(restoreKey) ||
                          _callbacks.ClientResourceRestoreRequested?.Invoke(snapshot, outcome) == true;
                if (success) RememberAppliedResourceRestore(restoreKey);
            }
            catch (Exception exception)
            {
                Plugin.Log?.LogError($"Could not apply ArenaGuard resource restore: {exception}");
            }
            if (token == null) return;
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (rpc == null) return;
            var acknowledgement = NewPackage();
            acknowledgement.Write(token);
            acknowledgement.Write(success);
            rpc.InvokeRoutedRPC(ResourceRestoreAcknowledgementRpc, acknowledgement);
        }

        private static void OnResourceRestoreAcknowledgement(long sender, ZPackage package)
        {
            if (!TryBeginServerRequest(sender, package, out ArenaRequestContext context)) return;
            try
            {
                string token = RequireId(ReadString(package, 64), "resource restore token");
                bool success = package.ReadBool();
                RequireConsumed(package);
                RemoveExpiredResourceRestores();
                if (!PendingResourceRestores.TryGetValue(token, out PendingResourceRestore pending) ||
                    pending.Context.PlayerId != context.PlayerId || pending.Context.PeerUid != context.PeerUid)
                    throw new InvalidOperationException("Resource restore is missing, expired, or belongs to another character.");
                PendingResourceRestores.Remove(token);
                _callbacks.ResourceRestoreCompleted?.Invoke(context, token, success);
            }
            catch (Exception exception)
            {
                Reject(sender, "resource restore acknowledgement", exception);
            }
        }

        private static void OnMovePlayer(long sender, ZPackage package)
        {
            if (!TryBeginClientResponse(sender, package)) return;
            try
            {
                PositionData destination = ReadPosition(package);
                float rotationY = package.ReadSingle();
                if (!Finite(rotationY)) throw new InvalidOperationException("Move rotation is invalid.");
                RequireConsumed(package);
                if (_callbacks.ClientMoveRequested?.Invoke(destination, rotationY) != true)
                    Plugin.Log?.LogWarning("The local player rejected an ArenaGuard move instruction.");
            }
            catch (Exception exception)
            {
                Plugin.Log?.LogError($"Could not apply ArenaGuard move instruction: {exception}");
            }
        }

        private static ArenaRpcResult TryAuthorizeGateTravel(ArenaRequestContext context, string gateId,
            bool returning, out ArenaGateTravelAuthorization authorization)
        {
            authorization = null;
            RemoveExpiredGateTravels();
            if (!ServerPortalRulesAllowTravel(out string portalRejection))
                return ArenaRpcResult.Rejected(portalRejection);
            ArenaGateDefinition destinationGate;
            ArenaDefinition arena;
            string arenaId;
            PositionData destination;
            float rotationY;

            if (returning)
            {
                if (!ArenaRegistry.TryGetRoute(context.PlayerId, out PlayerArenaRoute route) ||
                    !ArenaRegistry.TryGetArena(route.ArenaId, out arena))
                    return ArenaRpcResult.Rejected("No saved arena entrance is available for your character.");
                if (arena.Markers == null || !WithinDistance(context.Position, arena.Markers.HubGatePosition,
                    MaximumGateUseDistance))
                    return ArenaRpcResult.Rejected("Stand next to the arena return gate to leave.");
                destinationGate = ArenaRegistry.ResolveReturnGate(context.PlayerId);
                if (destinationGate == null)
                    return ArenaRpcResult.Rejected("Your entrance gate and the arena fallback gate are unavailable.");
                arenaId = arena.ArenaId;
                gateId = destinationGate.GateId;
                destination = destinationGate.Position;
                rotationY = destinationGate.RotationY;
            }
            else
            {
                if (!ArenaRegistry.TryGetGate(gateId, out ArenaGateDefinition entrance) ||
                    !ArenaRegistry.TryGetArena(entrance.ArenaId, out arena) || !arena.Enabled || arena.Markers == null)
                    return ArenaRpcResult.Rejected("That arena entrance is unavailable.");
                if (!WithinDistance(context.Position, entrance.Position, MaximumGateUseDistance))
                    return ArenaRpcResult.Rejected("Stand next to the Arena Gate to use it.");
                destinationGate = entrance;
                arenaId = arena.ArenaId;
                // Gates are optional travel infrastructure, not challenge state.
                // Every entrance arrives at the Arena Master staging point.
                destination = arena.Markers.StagingPosition;
                rotationY = 0f;
            }

            ArenaRpcResult preparation = _callbacks.GateTravelAuthorizing == null
                ? ArenaRpcResult.Rejected("Arena gate routing is not ready.")
                : _callbacks.GateTravelAuthorizing(context, destinationGate, returning) ??
                  ArenaRpcResult.Rejected("Arena server returned no gate result.");
            if (!preparation.Success) return preparation;

            authorization = new ArenaGateTravelAuthorization
            {
                TravelToken = Guid.NewGuid().ToString("N").ToLowerInvariant(),
                GateId = gateId,
                ArenaId = arenaId,
                Destination = destination,
                RotationY = rotationY,
                Returning = returning
            };
            PendingGateTravels[authorization.TravelToken] = new PendingGateTravel
            {
                Context = context,
                Authorization = authorization,
                ExpiresUtc = DateTime.UtcNow.Add(GateTravelTimeout)
            };
            return ArenaRpcResult.Accepted();
        }

        private static bool ServerPortalRulesAllowTravel(out string rejection)
        {
            ZoneSystem zones = ZoneSystem.instance;
            if (zones != null && zones.GetGlobalKey(GlobalKeys.NoPortals))
            {
                rejection = "Portals are disabled in this world.";
                return false;
            }
            if (zones != null && zones.GetGlobalKey(GlobalKeys.NoBossPortals))
            {
                float activeBosses;
                bool bossEvent = RandEventSystem.instance != null &&
                                 !string.IsNullOrEmpty(RandEventSystem.instance.GetBossEvent());
                bool bossKey = zones.GetGlobalKey(GlobalKeys.activeBosses, out activeBosses) && activeBosses > 0f;
                if (bossEvent || bossKey)
                {
                    rejection = "Portals are blocked while a boss is active.";
                    return false;
                }
            }
            rejection = string.Empty;
            return true;
        }

        private static void CompleteLocalGateTravel(ArenaRequestContext context,
            ArenaGateTravelAuthorization authorization)
        {
            bool accepted = false;
            try { accepted = _callbacks.ClientGateTravelAuthorized?.Invoke(authorization) == true; }
            catch (Exception exception) { Plugin.Log?.LogError($"Could not apply local ArenaGuard gate destination: {exception}"); }
            PendingGateTravels.Remove(authorization.TravelToken);
            ReceiveLocalResult(CompleteGateTravel(context, authorization, accepted));
        }

        private static ArenaRpcResult CompleteGateTravel(ArenaRequestContext context,
            ArenaGateTravelAuthorization authorization, bool accepted)
        {
            return _callbacks.GateTravelCompleted == null
                ? ArenaRpcResult.Rejected("Arena gate completion is not ready.")
                : _callbacks.GateTravelCompleted(context, authorization, accepted) ??
                  ArenaRpcResult.Rejected("Arena server returned no gate completion result.");
        }

        private static void RemoveExpiredGateTravels()
        {
            DateTime now = DateTime.UtcNow;
            foreach (string token in PendingGateTravels.Where(pair => pair.Value.ExpiresUtc <= now)
                .Select(pair => pair.Key).ToList())
            {
                PendingGateTravel pending = PendingGateTravels[token];
                PendingGateTravels.Remove(token);
                // An entrance route may already be persisted. Completion=false gives composition
                // a deterministic opportunity to roll it back; return requests have no mutation yet.
                try { CompleteGateTravel(pending.Context, pending.Authorization, false); }
                catch (Exception exception)
                {
                    Plugin.Log?.LogWarning($"Could not expire gate travel '{token}': {exception.Message}");
                }
            }
        }

        private static void RemoveExpiredResourceRestores()
        {
            DateTime now = DateTime.UtcNow;
            foreach (string token in PendingResourceRestores.Where(pair => pair.Value.ExpiresUtc <= now)
                .Select(pair => pair.Key).ToList())
            {
                PendingResourceRestore pending = PendingResourceRestores[token];
                PendingResourceRestores.Remove(token);
                _callbacks.ResourceRestoreCompleted?.Invoke(pending.Context, token, false);
            }
        }

        private static string ResourceRestoreKey(PlayerResourceSnapshot snapshot)
        {
            return snapshot.PlayerId.ToString() + ":" + snapshot.CapturedUtc.ToBinary().ToString();
        }

        private static void RememberAppliedResourceRestore(string key)
        {
            if (!AppliedResourceRestoreKeys.Add(key)) return;
            AppliedResourceRestoreOrder.Enqueue(key);
            while (AppliedResourceRestoreOrder.Count > MaximumAppliedRestoreKeys)
                AppliedResourceRestoreKeys.Remove(AppliedResourceRestoreOrder.Dequeue());
        }

        private static ArenaRpcResult Invoke(Func<ArenaRequestContext, ChallengeRequest, ArenaRpcResult> handler,
            ArenaRequestContext context, ChallengeRequest request)
        {
            return handler == null ? ArenaRpcResult.Rejected("Arena server is not ready.") :
                handler(context, request) ?? ArenaRpcResult.Rejected("Arena server returned no result.");
        }

        private static ArenaRpcResult Invoke(Func<ArenaRequestContext, ArenaRpcResult> handler,
            ArenaRequestContext context)
        {
            return handler == null ? ArenaRpcResult.Rejected("Arena server is not ready.") :
                handler(context) ?? ArenaRpcResult.Rejected("Arena server returned no result.");
        }

        private static ArenaRpcResult InvokeAdmin(ArenaRequestContext context, ArenaAdminMutation mutation)
        {
            if (!context.IsAdmin) return ArenaRpcResult.Rejected("Only a server administrator may change an arena.");
            return _callbacks.AdminMutationRequested == null ? ArenaRpcResult.Rejected("Arena administration is not ready.") :
                _callbacks.AdminMutationRequested(context, mutation) ?? ArenaRpcResult.Rejected("Arena server returned no result.");
        }

        private static void ValidateAdminMutation(ArenaAdminMutation mutation)
        {
            if (mutation == null || mutation.Kind < ArenaAdminMutationKind.RegisterArena ||
                mutation.Kind > ArenaAdminMutationKind.ClearArenaQueue)
                throw new ArgumentException("Unknown administrator mutation type.", nameof(mutation));
            switch (mutation.Kind)
            {
                case ArenaAdminMutationKind.RegisterArena:
                    if (mutation.Arena == null) throw new ArgumentException("Arena definition is required.");
                    break;
                case ArenaAdminMutationKind.RemoveArena:
                case ArenaAdminMutationKind.RemoveGate:
                    mutation.TargetId = RequireId(mutation.TargetId, "mutation target ID");
                    break;
                case ArenaAdminMutationKind.SetMarkers:
                    mutation.TargetId = RequireId(mutation.TargetId, "arena ID");
                    if (mutation.Markers == null || mutation.Markers.EnemySpawnPositions == null ||
                        mutation.Markers.EnemySpawnPositions.Count != 4)
                        throw new ArgumentException("A complete marker set requires exactly four enemy positions.");
                    break;
                case ArenaAdminMutationKind.RegisterGate:
                    if (mutation.Gate == null) throw new ArgumentException("Gate definition is required.");
                    break;
                case ArenaAdminMutationKind.SetMarker:
                case ArenaAdminMutationKind.RemoveMarker:
                    mutation.TargetId = RequireId(mutation.TargetId, "arena ID");
                    bool enemySpawn = mutation.MarkerKind == 2;
                    if (mutation.MarkerKind < 0 || mutation.MarkerKind > 3 ||
                        (enemySpawn && (mutation.MarkerSlot < 0 || mutation.MarkerSlot > 3)) ||
                        (!enemySpawn && mutation.MarkerSlot != -1) ||
                        !Finite(mutation.MarkerPosition.X) || !Finite(mutation.MarkerPosition.Y) ||
                        !Finite(mutation.MarkerPosition.Z))
                        throw new ArgumentException("Incremental marker kind, slot, or position is invalid.");
                    break;
                case ArenaAdminMutationKind.SetAdminTerrainPermission:
                case ArenaAdminMutationKind.SetAdminBuildingPermission:
                case ArenaAdminMutationKind.SetAdminPickupPermission:
                    mutation.TargetId = RequireId(mutation.TargetId, "arena ID");
                    break;
                case ArenaAdminMutationKind.RequestAdminStatus:
                case ArenaAdminMutationKind.AbortAllArenas:
                    mutation.TargetId = Trim(mutation.TargetId, 64);
                    break;
                case ArenaAdminMutationKind.AbortArena:
                case ArenaAdminMutationKind.ClearArenaQueue:
                    mutation.TargetId = Trim(mutation.TargetId, 64);
                    if (string.IsNullOrWhiteSpace(mutation.TargetId))
                        throw new ArgumentException("An arena ID or name is required.");
                    break;
            }
        }

        private static bool TryBeginServerRequest(long sender, ZPackage package, out ArenaRequestContext context)
        {
            context = null;
            ZNet znet = ZNet.instance;
            if (znet == null || !znet.IsServer()) return false;
            if (!ValidatePackageHeader(package, "client request")) return false;
            if (!TryResolvePeer(znet.GetPeer(sender), out context))
            {
                Plugin.Log?.LogWarning($"Rejected ArenaGuard request from unresolved peer {sender}.");
                SendResult(sender, ArenaRpcResult.Rejected("Your player character is not ready."));
                return false;
            }
            return true;
        }

        private static bool TryBeginClientResponse(long sender, ZPackage package)
        {
            ZNet znet = ZNet.instance;
            if (znet == null || znet.IsServer()) return false;
            ZNetPeer server = znet.GetServerPeer();
            if (server == null || sender != server.m_uid)
            {
                Plugin.Log?.LogWarning($"Ignored ArenaGuard response from non-server peer {sender}.");
                return false;
            }
            return ValidatePackageHeader(package, "server response");
        }

        private static bool ValidatePackageHeader(ZPackage package, string direction)
        {
            try
            {
                if (package == null || package.Size() <= 0 || package.Size() > MaximumPackageBytes)
                    throw new InvalidOperationException("Package size is invalid.");
                int protocol = package.ReadInt();
                string version = ReadString(package, 32);
                if (protocol != ProtocolVersion || !string.Equals(version, Plugin.PluginVersion, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Protocol/version mismatch ({protocol}, '{version}'). " +
                                                        $"Expected ({ProtocolVersion}, '{Plugin.PluginVersion}').");
                return true;
            }
            catch (Exception exception)
            {
                Plugin.Log?.LogWarning($"Rejected ArenaGuard {direction}: {exception.Message}");
                return false;
            }
        }

        private static ZPackage NewPackage()
        {
            var package = new ZPackage();
            package.Write(ProtocolVersion);
            package.Write(Plugin.PluginVersion);
            return package;
        }

        private static bool TryPrepareLocalRequest(out ZNet znet, out ZRoutedRpc rpc)
        {
            znet = ZNet.instance;
            rpc = ZRoutedRpc.instance;
            if (znet != null && rpc != null) return true;
            ReceiveLocalResult(ArenaRpcResult.Rejected("Arena networking is not ready."));
            return false;
        }

        private static bool TryResolveLocalContext(out ArenaRequestContext context)
        {
            context = null;
            Player player = Player.m_localPlayer;
            ZNet znet = ZNet.instance;
            if (player == null || znet == null || !znet.IsServer()) return false;
            Vector3 position = player.transform.position;
            context = new ArenaRequestContext
            {
                PeerUid = 0,
                PlayerId = player.GetPlayerID(),
                PlayerName = player.GetPlayerName() ?? string.Empty,
                IsAdmin = znet.LocalPlayerIsAdminOrHost(),
                Position = new PositionData { X = position.x, Y = position.y, Z = position.z }
            };
            return context.PlayerId > 0;
        }

        private static bool TryResolvePeer(ZNetPeer peer, out ArenaRequestContext context)
        {
            context = null;
            if (peer == null || peer.m_characterID == ZDOID.None || ZDOMan.instance == null || ZNetScene.instance == null)
                return false;
            ZDO character = ZDOMan.instance.GetZDO(peer.m_characterID);
            if (character == null || character.GetOwner() != peer.m_uid) return false;
            GameObject prefab = ZNetScene.instance.GetPrefab(character.GetPrefab());
            if (prefab == null || prefab.GetComponent<Player>() == null) return false;

            long playerId = character.GetLong(ZDOVars.s_playerID, 0L);
            if (playerId <= 0) return false;
            string playerName = character.GetString(ZDOVars.s_playerName, peer.m_playerName ?? string.Empty);
            Vector3 position = character.GetPosition();
            ZNet znet = ZNet.instance;
            bool admin = znet != null && peer.m_socket != null && znet.IsAdmin(peer.m_socket.GetHostName());
            context = new ArenaRequestContext
            {
                PeerUid = peer.m_uid,
                PlayerId = playerId,
                PlayerName = playerName ?? string.Empty,
                IsAdmin = admin,
                Position = new PositionData { X = position.x, Y = position.y, Z = position.z }
            };
            return true;
        }

        private static ChallengeRequest SanitizeClientChallenge(ChallengeRequest source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            string arenaId = RequireId(source.ArenaId, "arena ID");
            if (source.Mode < ChallengeMode.BiomeLadder || source.Mode > ChallengeMode.CustomEncounter)
                throw new ArgumentOutOfRangeException(nameof(source.Mode));
            if (source.CapMode < ProgressionCapMode.Gauntlet || source.CapMode > ProgressionCapMode.Biome)
                throw new ArgumentOutOfRangeException(nameof(source.CapMode));
            if (source.CapMode == ProgressionCapMode.Biome &&
                (source.SelectedBiome < BiomeTier.Meadows || source.SelectedBiome > BiomeTier.DeepNorth))
                throw new ArgumentOutOfRangeException(nameof(source.SelectedBiome));
            CustomEncounterSelection custom = null;
            if (source.Mode == ChallengeMode.CustomEncounter)
            {
                if (source.CustomSelection == null || string.IsNullOrWhiteSpace(source.CustomSelection.CreatureKey) ||
                    source.CustomSelection.CreatureKey.Length > 128 || source.CustomSelection.Quantity < 1 ||
                    source.CustomSelection.Quantity > 10 || source.CustomSelection.Stars < StarLevel.Base ||
                    source.CustomSelection.Stars > StarLevel.TwoStar)
                    throw new ArgumentException("Custom encounter selection is invalid.", nameof(source));
                custom = new CustomEncounterSelection
                {
                    CreatureKey = source.CustomSelection.CreatureKey.Trim(),
                    Stars = source.CustomSelection.Stars,
                    Quantity = source.CustomSelection.Quantity
                };
            }
            return new ChallengeRequest
            {
                ArenaId = arenaId,
                Mode = source.Mode,
                CapMode = source.Mode == ChallengeMode.CustomEncounter
                    ? ProgressionCapMode.Gauntlet
                    : source.CapMode,
                SelectedBiome = source.CapMode == ProgressionCapMode.Biome
                    ? source.SelectedBiome
                    : BiomeTier.BlackForest,
                CustomSelection = custom
            };
        }

        private static ChallengeRequest StampIdentity(ChallengeRequest request, ArenaRequestContext context)
        {
            request.RequestId = Guid.NewGuid().ToString("N").ToLowerInvariant();
            request.PlayerId = context.PlayerId;
            request.PlayerName = context.PlayerName;
            request.RequestedUtc = DateTime.UtcNow;
            return request;
        }

        private static bool ValidateChallengeLocation(ArenaRequestContext context, ChallengeRequest request,
            out string rejection)
        {
            if (!ArenaRegistry.TryGetArena(request.ArenaId, out ArenaDefinition arena) || !arena.Enabled)
            {
                rejection = "The selected arena is unavailable.";
                return false;
            }
            float dx = arena.CorePosition.X - context.Position.X;
            float dz = arena.CorePosition.Z - context.Position.Z;
            if (dx * dx + dz * dz > arena.ProtectedRadius * arena.ProtectedRadius)
            {
                rejection = "Use the challenge sign from inside the selected arena.";
                return false;
            }
            rejection = string.Empty;
            return true;
        }

        private static void SendResult(long peerUid, ArenaRpcResult result)
        {
            result = result ?? ArenaRpcResult.Rejected("Arena server returned no result.");
            if (peerUid == 0)
            {
                ReceiveLocalResult(result);
                return;
            }
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (rpc == null) return;
            var package = NewPackage();
            package.Write(result.Success);
            package.Write(Trim(result.Message, MaximumStringLength));
            rpc.InvokeRoutedRPC(peerUid, ResultRpc, package);
        }

        private static void ReceiveLocalResult(ArenaRpcResult result)
        {
            if (result != null) _callbacks.ClientActionResultReceived?.Invoke(result.Success, result.Message ?? string.Empty);
        }

        private static void Reject(long peerUid, string operation, Exception exception)
        {
            Plugin.Log?.LogWarning($"Rejected ArenaGuard {operation} from peer {peerUid}: {exception.Message}");
            SendResult(peerUid, ArenaRpcResult.Rejected(exception.Message));
        }

        private static void WriteChallengeRequest(ZPackage package, ChallengeRequest request, bool includeIdentity)
        {
            package.Write(request.ArenaId ?? string.Empty);
            package.Write((int)request.Mode);
            package.Write((int)request.CapMode);
            package.Write((int)request.SelectedBiome);
            package.Write(request.CustomSelection != null);
            if (request.CustomSelection != null)
            {
                package.Write(request.CustomSelection.CreatureKey ?? string.Empty);
                package.Write((int)request.CustomSelection.Stars);
                package.Write(request.CustomSelection.Quantity);
            }
            if (includeIdentity)
            {
                package.Write(request.RequestId ?? string.Empty);
                package.Write(request.PlayerId);
                package.Write(request.PlayerName ?? string.Empty);
                package.Write(request.RequestedUtc.ToBinary());
            }
        }

        private static ChallengeRequest ReadChallengeRequest(ZPackage package, bool includeIdentity)
        {
            var request = new ChallengeRequest
            {
                ArenaId = ReadString(package, 64),
                Mode = (ChallengeMode)package.ReadInt(),
                CapMode = (ProgressionCapMode)package.ReadInt(),
                SelectedBiome = (BiomeTier)package.ReadInt()
            };
            bool hasCustom = package.ReadBool();
            if (hasCustom)
            {
                request.CustomSelection = new CustomEncounterSelection
                {
                    CreatureKey = ReadString(package, 128),
                    Stars = (StarLevel)package.ReadInt(),
                    Quantity = package.ReadInt()
                };
            }
            if (includeIdentity)
            {
                request.RequestId = ReadString(package, 64);
                request.PlayerId = package.ReadLong();
                request.PlayerName = ReadString(package, 64);
                request.RequestedUtc = DateTime.FromBinary(package.ReadLong());
            }
            return SanitizeClientChallenge(request);
        }

        private static void WriteAdminMutation(ZPackage package, ArenaAdminMutation mutation)
        {
            package.Write((int)mutation.Kind);
            package.Write(mutation.TargetId ?? string.Empty);
            package.Write(mutation.Arena != null);
            if (mutation.Arena != null) WriteArena(package, mutation.Arena);
            package.Write(mutation.Markers != null);
            if (mutation.Markers != null) WriteMarkers(package, mutation.Markers);
            package.Write(mutation.Gate != null);
            if (mutation.Gate != null) WriteGate(package, mutation.Gate);
            if (mutation.Kind == ArenaAdminMutationKind.SetMarker ||
                mutation.Kind == ArenaAdminMutationKind.RemoveMarker)
            {
                package.Write(mutation.MarkerKind);
                package.Write(mutation.MarkerSlot);
                WritePosition(package, mutation.MarkerPosition);
            }
            if (mutation.Kind == ArenaAdminMutationKind.SetAdminTerrainPermission ||
                mutation.Kind == ArenaAdminMutationKind.SetAdminBuildingPermission ||
                mutation.Kind == ArenaAdminMutationKind.SetAdminPickupPermission)
            {
                package.Write(mutation.Enabled);
            }
        }

        private static void WriteGateTravelAuthorization(ZPackage package,
            ArenaGateTravelAuthorization authorization)
        {
            package.Write(authorization.TravelToken ?? string.Empty);
            package.Write(authorization.GateId ?? string.Empty);
            package.Write(authorization.ArenaId ?? string.Empty);
            WritePosition(package, authorization.Destination);
            package.Write(authorization.RotationY);
            package.Write(authorization.Returning);
        }

        private static ArenaGateTravelAuthorization ReadGateTravelAuthorization(ZPackage package)
        {
            var authorization = new ArenaGateTravelAuthorization
            {
                TravelToken = RequireId(ReadString(package, 64), "travel token"),
                GateId = RequireId(ReadString(package, 64), "gate ID"),
                ArenaId = RequireId(ReadString(package, 64), "arena ID"),
                Destination = ReadPosition(package),
                RotationY = package.ReadSingle(),
                Returning = package.ReadBool()
            };
            if (!Finite(authorization.RotationY))
                throw new InvalidOperationException("Gate destination rotation is invalid.");
            return authorization;
        }

        private static ArenaAdminMutation ReadAdminMutation(ZPackage package)
        {
            var mutation = new ArenaAdminMutation
            {
                Kind = (ArenaAdminMutationKind)package.ReadInt(),
                TargetId = ReadString(package, 64)
            };
            if (mutation.Kind < ArenaAdminMutationKind.RegisterArena ||
                mutation.Kind > ArenaAdminMutationKind.ClearArenaQueue)
                throw new InvalidOperationException("Unknown administrator mutation type.");
            if (package.ReadBool()) mutation.Arena = ReadArena(package);
            if (package.ReadBool()) mutation.Markers = ReadMarkers(package);
            if (package.ReadBool()) mutation.Gate = ReadGate(package);
            if (mutation.Kind == ArenaAdminMutationKind.SetMarker ||
                mutation.Kind == ArenaAdminMutationKind.RemoveMarker)
            {
                mutation.MarkerKind = package.ReadInt();
                mutation.MarkerSlot = package.ReadInt();
                mutation.MarkerPosition = ReadPosition(package);
                if (mutation.MarkerKind < 0 || mutation.MarkerKind > 3 || mutation.MarkerSlot < -1 ||
                    mutation.MarkerSlot > 3 || string.IsNullOrWhiteSpace(mutation.TargetId))
                    throw new InvalidOperationException("Incremental arena marker mutation is invalid.");
                mutation.TargetId = RequireId(mutation.TargetId, "arena ID");
            }
            if (mutation.Kind == ArenaAdminMutationKind.SetAdminTerrainPermission ||
                mutation.Kind == ArenaAdminMutationKind.SetAdminBuildingPermission ||
                mutation.Kind == ArenaAdminMutationKind.SetAdminPickupPermission)
            {
                mutation.Enabled = package.ReadBool();
                mutation.TargetId = RequireId(mutation.TargetId, "arena ID");
            }
            return mutation;
        }

        private static void WriteArena(ZPackage package, ArenaDefinition arena)
        {
            package.Write(arena.ArenaId ?? string.Empty);
            package.Write(arena.DisplayName ?? string.Empty);
            WritePosition(package, arena.CorePosition);
            package.Write(arena.CombatRadius);
            package.Write(arena.ProtectedRadius);
            package.Write(arena.Enabled);
            package.Write(arena.Revision);
            package.Write(arena.Markers != null);
            if (arena.Markers != null) WriteMarkers(package, arena.Markers);
        }

        private static ArenaDefinition ReadArena(ZPackage package)
        {
            var arena = new ArenaDefinition
            {
                ArenaId = ReadString(package, 64),
                DisplayName = ReadString(package, 64),
                CorePosition = ReadPosition(package),
                CombatRadius = package.ReadSingle(),
                ProtectedRadius = package.ReadSingle(),
                Enabled = package.ReadBool(),
                Revision = package.ReadLong()
            };
            if (package.ReadBool()) arena.Markers = ReadMarkers(package);
            return arena;
        }

        private static void WriteMarkers(ZPackage package, ArenaMarkerSet markers)
        {
            WritePosition(package, markers.StagingPosition);
            WritePosition(package, markers.CombatantStartPosition);
            WritePosition(package, markers.HubGatePosition);
            int count = markers.EnemySpawnPositions?.Count ?? 0;
            package.Write(count);
            for (int i = 0; i < count; i++) WritePosition(package, markers.EnemySpawnPositions[i]);
        }

        private static ArenaMarkerSet ReadMarkers(ZPackage package)
        {
            var markers = new ArenaMarkerSet
            {
                StagingPosition = ReadPosition(package),
                CombatantStartPosition = ReadPosition(package),
                HubGatePosition = ReadPosition(package),
                EnemySpawnPositions = new List<PositionData>()
            };
            int count = package.ReadInt();
            if (count < 0 || count > 4) throw new InvalidOperationException("Enemy marker count is invalid.");
            for (int i = 0; i < count; i++) markers.EnemySpawnPositions.Add(ReadPosition(package));
            return markers;
        }

        private static void WriteGate(ZPackage package, ArenaGateDefinition gate)
        {
            package.Write(gate.GateId ?? string.Empty);
            package.Write(gate.ArenaId ?? string.Empty);
            package.Write(gate.DisplayName ?? string.Empty);
            WritePosition(package, gate.Position);
            package.Write(gate.RotationY);
            package.Write(gate.IsFallbackEntrance);
        }

        private static ArenaGateDefinition ReadGate(ZPackage package)
        {
            return ValidateAndCopyGate(new ArenaGateDefinition
            {
                GateId = ReadString(package, 64),
                ArenaId = ReadString(package, 64),
                DisplayName = ReadString(package, 64),
                Position = ReadPosition(package),
                RotationY = package.ReadSingle(),
                IsFallbackEntrance = package.ReadBool()
            });
        }

        private static ArenaGateDefinition ValidateAndCopyGate(ArenaGateDefinition source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            string name = (source.DisplayName ?? string.Empty).Trim();
            if (name.Length == 0 || name.Length > 64 || !Finite(source.RotationY) ||
                !Finite(source.Position.X) || !Finite(source.Position.Y) || !Finite(source.Position.Z))
                throw new ArgumentException("Gate configuration is invalid.", nameof(source));
            return new ArenaGateDefinition
            {
                GateId = RequireId(source.GateId, "gate ID"),
                ArenaId = RequireId(source.ArenaId, "arena ID"),
                DisplayName = name,
                Position = source.Position,
                RotationY = source.RotationY,
                IsFallbackEntrance = source.IsFallbackEntrance
            };
        }

        private static void WriteArenaSnapshot(ZPackage package, ArenaClientSnapshot snapshot)
        {
            package.Write(snapshot.ArenaId ?? string.Empty);
            package.Write(snapshot.SessionId ?? string.Empty);
            package.Write(snapshot.ArenaName ?? string.Empty);
            WritePosition(package, snapshot.CorePosition);
            package.Write(snapshot.CombatRadius);
            package.Write(snapshot.ProtectedRadius);
            package.Write(snapshot.Markers != null);
            if (snapshot.Markers != null) WriteMarkers(package, snapshot.Markers);
            package.Write((int)snapshot.Phase);
            package.Write(snapshot.CombatantPlayerId);
            package.Write(snapshot.CombatantName ?? string.Empty);
            package.Write(snapshot.QueuePosition);
            package.Write(snapshot.QueueLength);
            package.Write(snapshot.EncounterIndex);
            package.Write(snapshot.EncounterCount);
            package.Write(snapshot.CurrentEncounter != null);
            if (snapshot.CurrentEncounter != null) WriteEncounter(package, snapshot.CurrentEncounter);
            package.Write(snapshot.CountdownSeconds);
            package.Write(snapshot.ElapsedMilliseconds);
            package.Write(snapshot.PreparationComplete);
            package.Write(snapshot.AdminsMayModifyTerrain);
            package.Write(snapshot.AdminsMayBuild);
            package.Write(snapshot.AdminsMayPickupDroppedItems);
        }

        private static ArenaClientSnapshot ReadArenaSnapshot(ZPackage package)
        {
            var snapshot = new ArenaClientSnapshot
            {
                ArenaId = RequireId(ReadString(package, 64), "arena ID"),
                SessionId = ReadString(package, 64),
                ArenaName = ReadString(package, 64),
                CorePosition = ReadPosition(package),
                CombatRadius = package.ReadSingle(),
                ProtectedRadius = package.ReadSingle()
            };
            if (!Finite(snapshot.CombatRadius) || !Finite(snapshot.ProtectedRadius) || snapshot.CombatRadius <= 0f ||
                snapshot.ProtectedRadius <= snapshot.CombatRadius)
                throw new InvalidOperationException("Arena snapshot bounds are invalid.");
            if (package.ReadBool()) snapshot.Markers = ReadMarkers(package);
            snapshot.Phase = (SessionPhase)package.ReadInt();
            snapshot.CombatantPlayerId = package.ReadLong();
            snapshot.CombatantName = ReadString(package, 64);
            snapshot.QueuePosition = package.ReadInt();
            snapshot.QueueLength = package.ReadInt();
            snapshot.EncounterIndex = package.ReadInt();
            snapshot.EncounterCount = package.ReadInt();
            if (snapshot.Phase < SessionPhase.Queued || snapshot.Phase > SessionPhase.Closed ||
                snapshot.QueuePosition < 0 || snapshot.QueueLength < 0 || snapshot.EncounterIndex < 0 ||
                snapshot.EncounterCount < 0)
                throw new InvalidOperationException("Arena snapshot values are invalid.");
            if (!string.IsNullOrWhiteSpace(snapshot.SessionId))
                snapshot.SessionId = RequireId(snapshot.SessionId, "session ID");
            if (snapshot.Phase != SessionPhase.Closed && string.IsNullOrWhiteSpace(snapshot.SessionId))
                throw new InvalidOperationException("An active arena snapshot requires a session ID.");
            if (package.ReadBool()) snapshot.CurrentEncounter = ReadEncounter(package);
            snapshot.CountdownSeconds = package.ReadInt();
            snapshot.ElapsedMilliseconds = package.ReadLong();
            snapshot.PreparationComplete = package.ReadBool();
            snapshot.AdminsMayModifyTerrain = package.ReadBool();
            snapshot.AdminsMayBuild = package.ReadBool();
            snapshot.AdminsMayPickupDroppedItems = package.ReadBool();
            if (snapshot.CountdownSeconds < 0 || snapshot.ElapsedMilliseconds < 0)
                throw new InvalidOperationException("Arena snapshot timing is invalid.");
            return snapshot;
        }

        private static void WriteEncounter(ZPackage package, EncounterDefinition encounter)
        {
            package.Write(encounter.Sequence);
            package.Write(encounter.CreatureKey ?? string.Empty);
            package.Write((int)encounter.Biome);
            package.Write((int)encounter.Stars);
            package.Write(encounter.Quantity);
            package.Write(encounter.IsMiniboss);
            package.Write(encounter.PreparationSeconds);
        }

        private static EncounterDefinition ReadEncounter(ZPackage package)
        {
            var encounter = new EncounterDefinition
            {
                Sequence = package.ReadInt(),
                CreatureKey = ReadString(package, 128),
                Biome = (BiomeTier)package.ReadInt(),
                Stars = (StarLevel)package.ReadInt(),
                Quantity = package.ReadInt(),
                IsMiniboss = package.ReadBool(),
                PreparationSeconds = package.ReadInt()
            };
            if (encounter.Sequence < 0 || string.IsNullOrWhiteSpace(encounter.CreatureKey) ||
                encounter.Biome < BiomeTier.Meadows || encounter.Biome > BiomeTier.DeepNorth ||
                encounter.Stars < StarLevel.Base || encounter.Stars > StarLevel.TwoStar ||
                encounter.Quantity < 1 || encounter.Quantity > 10 || encounter.PreparationSeconds < 0 ||
                encounter.PreparationSeconds > 300)
                throw new InvalidOperationException("Encounter definition is invalid.");
            return encounter;
        }

        private static void WriteLeaderboardKey(ZPackage package, LeaderboardKey key)
        {
            package.Write((int)key.Mode);
            package.Write((int)key.CapMode);
            package.Write((int)key.SelectedBiome);
            package.Write(key.CustomCreatureKey ?? string.Empty);
            package.Write((int)key.CustomStars);
            package.Write(key.CustomQuantity);
        }

        private static LeaderboardKey ReadLeaderboardKey(ZPackage package)
        {
            return NormalizeLeaderboardKey(new LeaderboardKey
            {
                Mode = (ChallengeMode)package.ReadInt(),
                CapMode = (ProgressionCapMode)package.ReadInt(),
                SelectedBiome = (BiomeTier)package.ReadInt(),
                CustomCreatureKey = ReadString(package, 128),
                CustomStars = (StarLevel)package.ReadInt(),
                CustomQuantity = package.ReadInt()
            });
        }

        private static LeaderboardKey NormalizeLeaderboardKey(LeaderboardKey key)
        {
            if (key == null || key.Mode < ChallengeMode.BiomeLadder || key.Mode > ChallengeMode.CustomEncounter ||
                key.CapMode < ProgressionCapMode.Gauntlet || key.CapMode > ProgressionCapMode.Biome)
                throw new ArgumentException("Leaderboard key is invalid.", nameof(key));
            if (key.Mode != ChallengeMode.CustomEncounter)
            {
                if (key.CapMode == ProgressionCapMode.Biome &&
                    (key.SelectedBiome < BiomeTier.Meadows || key.SelectedBiome > BiomeTier.DeepNorth))
                    throw new ArgumentException("Leaderboard biome is invalid.", nameof(key));
                return new LeaderboardKey
                {
                    Mode = key.Mode,
                    CapMode = key.CapMode,
                    SelectedBiome = key.CapMode == ProgressionCapMode.Biome
                        ? key.SelectedBiome
                        : BiomeTier.BlackForest,
                    CustomCreatureKey = string.Empty
                };
            }
            if (string.IsNullOrWhiteSpace(key.CustomCreatureKey) || key.CustomCreatureKey.Length > 128 ||
                key.CustomStars < StarLevel.Base || key.CustomStars > StarLevel.TwoStar ||
                key.CustomQuantity < 1 || key.CustomQuantity > 10)
                throw new ArgumentException("Custom leaderboard key is invalid.", nameof(key));
            return new LeaderboardKey
            {
                Mode = key.Mode,
                CapMode = ProgressionCapMode.Gauntlet,
                SelectedBiome = BiomeTier.BlackForest,
                CustomCreatureKey = key.CustomCreatureKey.Trim().ToLowerInvariant(),
                CustomStars = key.CustomStars,
                CustomQuantity = key.CustomQuantity
            };
        }

        private static void WriteLeaderboardEntry(ZPackage package, LeaderboardEntry entry)
        {
            package.Write(entry.PlayerId);
            package.Write(entry.PlayerName ?? string.Empty);
            package.Write(entry.Completed);
            package.Write(entry.FurthestEncounterIndex);
            package.Write(entry.ElapsedMilliseconds);
            package.Write(entry.RosterRevision);
            package.Write(entry.RecordedUtc.ToBinary());
        }

        private static LeaderboardEntry ReadLeaderboardEntry(ZPackage package, LeaderboardKey key)
        {
            var entry = new LeaderboardEntry
            {
                Key = key,
                PlayerId = package.ReadLong(),
                PlayerName = ReadString(package, 64),
                Completed = package.ReadBool(),
                FurthestEncounterIndex = package.ReadInt(),
                ElapsedMilliseconds = package.ReadLong(),
                RosterRevision = package.ReadLong(),
                RecordedUtc = DateTime.FromBinary(package.ReadLong())
            };
            if (entry.PlayerId <= 0 || string.IsNullOrWhiteSpace(entry.PlayerName) ||
                entry.FurthestEncounterIndex < 0 || entry.ElapsedMilliseconds < 0 || entry.RosterRevision < 0)
                throw new InvalidOperationException("Leaderboard entry is invalid.");
            return entry;
        }

        private static void WriteResourceSnapshot(ZPackage package, PlayerResourceSnapshot snapshot, bool includeIdentity)
        {
            if (includeIdentity)
            {
                package.Write(snapshot.PlayerId);
                package.Write(snapshot.CapturedUtc.ToBinary());
            }
            package.Write(snapshot.Foods.Count);
            foreach (FoodStateSnapshot food in snapshot.Foods)
            {
                package.Write(food.ItemPrefabName ?? string.Empty);
                package.Write(food.RemainingSeconds);
                package.Write(food.Health);
                package.Write(food.Stamina);
                package.Write(food.Eitr);
            }
            package.Write(snapshot.RestedRemainingSeconds);
            package.Write(snapshot.ArenaFoodPrefabNames.Count);
            foreach (string prefabName in snapshot.ArenaFoodPrefabNames)
            {
                package.Write(prefabName ?? string.Empty);
            }
            WriteIntDictionary(package, snapshot.AllowedConsumableCounts);
            WriteIntDictionary(package, snapshot.AmmunitionCounts);
            package.Write(snapshot.EquipmentDurabilityBySlot.Count);
            foreach (KeyValuePair<string, float> pair in snapshot.EquipmentDurabilityBySlot.OrderBy(pair => pair.Key,
                StringComparer.Ordinal))
            {
                package.Write(pair.Key);
                package.Write(pair.Value);
            }
        }

        private static PlayerResourceSnapshot ReadResourceSnapshot(ZPackage package, bool includeIdentity)
        {
            var snapshot = new PlayerResourceSnapshot
            {
                PlayerId = includeIdentity ? package.ReadLong() : 0,
                CapturedUtc = includeIdentity ? DateTime.FromBinary(package.ReadLong()) : DateTime.UtcNow,
                Foods = new List<FoodStateSnapshot>(),
                ArenaFoodPrefabNames = new List<string>(),
                AllowedConsumableCounts = new Dictionary<string, int>(StringComparer.Ordinal),
                AmmunitionCounts = new Dictionary<string, int>(StringComparer.Ordinal),
                EquipmentDurabilityBySlot = new Dictionary<string, float>(StringComparer.Ordinal)
            };
            int foodCount = ReadCount(package, 3, "food");
            for (int i = 0; i < foodCount; i++)
            {
                var food = new FoodStateSnapshot
                {
                    ItemPrefabName = ReadString(package, 128),
                    RemainingSeconds = package.ReadSingle(),
                    Health = package.ReadSingle(),
                    Stamina = package.ReadSingle(),
                    Eitr = package.ReadSingle()
                };
                if (string.IsNullOrWhiteSpace(food.ItemPrefabName) || !FiniteNonNegative(food.RemainingSeconds) ||
                    !FiniteNonNegative(food.Health) || !FiniteNonNegative(food.Stamina) || !FiniteNonNegative(food.Eitr))
                    throw new InvalidOperationException("Food snapshot entry is invalid.");
                snapshot.Foods.Add(food);
            }
            snapshot.RestedRemainingSeconds = package.ReadSingle();
            if (!FiniteNonNegative(snapshot.RestedRemainingSeconds))
                throw new InvalidOperationException("Rested snapshot duration is invalid.");
            int arenaFoodCount = ReadCount(package, ArenaFoodSelectionPolicy.RequiredFoodCount, "arena food");
            for (int i = 0; i < arenaFoodCount; i++)
            {
                string prefabName = ReadString(package, 128).Trim();
                if (prefabName.Length == 0 || snapshot.ArenaFoodPrefabNames.Contains(prefabName))
                    throw new InvalidOperationException("Arena food snapshot entry is invalid or duplicated.");
                snapshot.ArenaFoodPrefabNames.Add(prefabName);
            }
            ReadIntDictionary(package, snapshot.AllowedConsumableCounts);
            ReadIntDictionary(package, snapshot.AmmunitionCounts);
            int durabilityCount = ReadCount(package, MaximumResourceEntries, "equipment durability");
            for (int i = 0; i < durabilityCount; i++)
            {
                string slot = ReadString(package, 128);
                float durability = package.ReadSingle();
                if (string.IsNullOrWhiteSpace(slot) || !FiniteNonNegative(durability) ||
                    snapshot.EquipmentDurabilityBySlot.ContainsKey(slot))
                    throw new InvalidOperationException("Equipment durability entry is invalid or duplicated.");
                snapshot.EquipmentDurabilityBySlot.Add(slot, durability);
            }
            return ValidateAndCopyResourceSnapshot(snapshot, snapshot.PlayerId, includeIdentity);
        }

        private static PlayerResourceSnapshot ValidateAndCopyResourceSnapshot(PlayerResourceSnapshot source,
            long expectedPlayerId, bool requireIdentity)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (requireIdentity && (expectedPlayerId <= 0 || source.PlayerId != expectedPlayerId))
                throw new ArgumentException("Resource snapshot targets another character.", nameof(source));
            if ((source.Foods?.Count ?? 0) > 3 ||
                (source.ArenaFoodPrefabNames?.Count ?? 0) > ArenaFoodSelectionPolicy.RequiredFoodCount ||
                (source.AllowedConsumableCounts?.Count ?? 0) > MaximumResourceEntries ||
                (source.AmmunitionCounts?.Count ?? 0) > MaximumResourceEntries ||
                (source.EquipmentDurabilityBySlot?.Count ?? 0) > MaximumResourceEntries)
                throw new ArgumentException("Resource snapshot collection limit exceeded.", nameof(source));

            var copy = new PlayerResourceSnapshot
            {
                PlayerId = requireIdentity ? expectedPlayerId : 0,
                CapturedUtc = requireIdentity ? source.CapturedUtc : DateTime.UtcNow,
                Foods = new List<FoodStateSnapshot>(),
                RestedRemainingSeconds = source.RestedRemainingSeconds,
                ArenaFoodPrefabNames = new List<string>(),
                AllowedConsumableCounts = CopyIntDictionary(source.AllowedConsumableCounts, "consumable"),
                AmmunitionCounts = CopyIntDictionary(source.AmmunitionCounts, "ammunition"),
                EquipmentDurabilityBySlot = new Dictionary<string, float>(StringComparer.Ordinal)
            };
            foreach (FoodStateSnapshot food in source.Foods ?? new List<FoodStateSnapshot>())
            {
                if (food == null || string.IsNullOrWhiteSpace(food.ItemPrefabName) || food.ItemPrefabName.Length > 128 ||
                    !FiniteNonNegative(food.RemainingSeconds) || !FiniteNonNegative(food.Health) ||
                    !FiniteNonNegative(food.Stamina) || !FiniteNonNegative(food.Eitr))
                    throw new ArgumentException("Food snapshot entry is invalid.", nameof(source));
                copy.Foods.Add(new FoodStateSnapshot
                {
                    ItemPrefabName = food.ItemPrefabName.Trim(),
                    RemainingSeconds = food.RemainingSeconds,
                    Health = food.Health,
                    Stamina = food.Stamina,
                    Eitr = food.Eitr
                });
            }
            if (!FiniteNonNegative(source.RestedRemainingSeconds))
                throw new ArgumentException("Rested snapshot duration is invalid.", nameof(source));
            foreach (string value in source.ArenaFoodPrefabNames ?? new List<string>())
            {
                string prefabName = (value ?? string.Empty).Trim();
                if (prefabName.Length == 0 || prefabName.Length > 128 ||
                    copy.ArenaFoodPrefabNames.Contains(prefabName))
                    throw new ArgumentException("Arena food snapshot entry is invalid or duplicated.", nameof(source));
                copy.ArenaFoodPrefabNames.Add(prefabName);
            }
            foreach (KeyValuePair<string, float> pair in source.EquipmentDurabilityBySlot ??
                     new Dictionary<string, float>())
            {
                string key = (pair.Key ?? string.Empty).Trim();
                if (key.Length == 0 || key.Length > 128 || !FiniteNonNegative(pair.Value) ||
                    copy.EquipmentDurabilityBySlot.ContainsKey(key))
                    throw new ArgumentException("Equipment durability entry is invalid.", nameof(source));
                copy.EquipmentDurabilityBySlot.Add(key, pair.Value);
            }
            return copy;
        }

        private static Dictionary<string, int> CopyIntDictionary(Dictionary<string, int> source, string label)
        {
            var copy = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, int> pair in source ?? new Dictionary<string, int>())
            {
                string key = (pair.Key ?? string.Empty).Trim();
                if (key.Length == 0 || key.Length > 128 || pair.Value < 0 || copy.ContainsKey(key))
                    throw new ArgumentException($"{label} snapshot entry is invalid.");
                copy.Add(key, pair.Value);
            }
            return copy;
        }

        private static void WriteIntDictionary(ZPackage package, Dictionary<string, int> values)
        {
            package.Write(values.Count);
            foreach (KeyValuePair<string, int> pair in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                package.Write(pair.Key);
                package.Write(pair.Value);
            }
        }

        private static void ReadIntDictionary(ZPackage package, Dictionary<string, int> destination)
        {
            int count = ReadCount(package, MaximumResourceEntries, "resource");
            for (int i = 0; i < count; i++)
            {
                string key = ReadString(package, 128);
                int value = package.ReadInt();
                if (string.IsNullOrWhiteSpace(key) || value < 0 || destination.ContainsKey(key))
                    throw new InvalidOperationException("Resource entry is invalid or duplicated.");
                destination.Add(key, value);
            }
        }

        private static int ReadCount(ZPackage package, int maximum, string label)
        {
            int count = package.ReadInt();
            if (count < 0 || count > maximum) throw new InvalidOperationException($"{label} count is invalid.");
            return count;
        }

        private static void WritePosition(ZPackage package, PositionData position)
        {
            package.Write(position.X);
            package.Write(position.Y);
            package.Write(position.Z);
        }

        private static PositionData ReadPosition(ZPackage package)
        {
            var position = new PositionData { X = package.ReadSingle(), Y = package.ReadSingle(), Z = package.ReadSingle() };
            if (!Finite(position.X) || !Finite(position.Y) || !Finite(position.Z))
                throw new InvalidOperationException("Position contains a non-finite value.");
            return position;
        }

        private static string ReadString(ZPackage package, int maximumLength)
        {
            string value = package.ReadString() ?? string.Empty;
            if (value.Length > maximumLength) throw new InvalidOperationException("String exceeds protocol limit.");
            return value;
        }

        private static string Trim(string value, int maximumLength)
        {
            value = value ?? string.Empty;
            return value.Length <= maximumLength ? value : value.Substring(0, maximumLength);
        }

        private static string RequireId(string value, string label)
        {
            string id = ArenaRegistry.NormalizeId(value);
            if (id == null) throw new ArgumentException($"Invalid {label}.");
            return id;
        }

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool FiniteNonNegative(float value)
        {
            return Finite(value) && value >= 0f;
        }

        private static bool WithinDistance(PositionData first, PositionData second, float maximum)
        {
            float dx = first.X - second.X;
            float dy = first.Y - second.Y;
            float dz = first.Z - second.Z;
            return dx * dx + dy * dy + dz * dz <= maximum * maximum;
        }

        private static void RequireConsumed(ZPackage package)
        {
            if (package.GetPos() != package.Size())
                throw new InvalidOperationException("Package contains unexpected trailing data.");
        }

        private static void DetachPeerHook()
        {
            if (_registeredInstance != null && _newPeerHandler != null)
                _registeredInstance.m_onNewPeer -= _newPeerHandler;
            _newPeerHandler = null;
        }
    }
}
