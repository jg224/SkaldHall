using System;
using System.Collections.Generic;
using System.Linq;
using ArenaGuard.Domain;
using ArenaGuard.Config;
using ArenaGuard.Rules;
using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;

namespace ArenaGuard.World
{
    public enum ArenaWorldObjectKind
    {
        Core,
        Sign,
        EntranceGate,
        HubGate,
        StagingMarker,
        CombatantStartMarker,
        EnemySpawnMarker,
        HubGateMarker
    }

    public sealed class ArenaWorldObjectPlacement
    {
        public ArenaWorldObjectKind Kind;
        public string ObjectId;
        public string ArenaId;
        public PositionData Position;
        public float RotationY;
    }

    /// <summary>
    /// Registers the small set of vanilla-derived world objects used by ArenaGuard.
    /// All authoritative changes are passed to delegates which the networking layer
    /// binds during plugin composition.
    /// </summary>
    public static class ArenaWorldObjects
    {
        public const string AdminHammerPrefabName = "ArenaGuard_AdminHammer";
        public const string PieceTableName = "ArenaGuard_PieceTable";
        public const string CorePrefabName = "ArenaGuard_Core";
        public const string ChallengeHostPrefabName = "ArenaGuard_ChallengeHost";
        public const string SignPrefabName = "ArenaGuard_Sign";
        public const string EntranceGatePrefabName = "ArenaGuard_EntranceGate";
        public const string HubGatePrefabName = "ArenaGuard_HubGate";
        public const string StagingMarkerPrefabName = "ArenaGuard_StagingMarker";
        public const string CombatantStartMarkerPrefabName = "ArenaGuard_CombatantStartMarker";
        public const string EnemySpawnMarkerPrefabName = "ArenaGuard_EnemySpawnMarker";
        public const string HubGateMarkerPrefabName = "ArenaGuard_HubGateMarker";

        private const string SignBasePrefabName = "sign";
        private const string ChallengeHostBasePrefabName = "Dverger";
        private const string ChallengeHostIconPrefabName = "TrophyDvergr";

        internal const string ZdoObjectId = "arenaguard.object_id";
        internal const string ZdoArenaId = "arenaguard.arena_id";
        internal const string ZdoDisplayName = "arenaguard.display_name";
        internal const string ZdoPlacementReported = "arenaguard.placement_reported";
        internal const string ZdoMarkerKind = "arenaguard.marker_kind";
        internal const string ZdoMarkerSlot = "arenaguard.marker_slot";
        internal const int MarkerRequestRejected = -2;

        private static bool _registered;
        private static GameObject _adminHammerPrefab;
        private static Player _lastLocalPlayer;
        private static float _nextHammerAuditTime;
        private static float _nextGrantAttemptTime;
        private static bool _adminStatusRefreshSubscribed;
        private static readonly HashSet<int> ChallengeHostCharacterIds = new HashSet<int>();

        public static Func<bool> LocalAdminResolver;
        public static Action<ArenaWorldObjectPlacement> PlacementRequested;
        public static Action<string> CoreRemovalRequested;
        public static Action<string, PositionData> CoreActivated;
        public static Func<string, ArenaMarkerKind, PositionData, int, int> AdminMarkerRequested;
        public static Action<string, ArenaMarkerKind, PositionData, int> AdminMarkerRemovalRequested;
        public static Func<Vector3, string> ArenaAtPositionResolver;
        public static Func<string, ArenaDefinition> ArenaDefinitionResolver;

        /// <summary>The arena most recently opened by an admin. Newly placed pieces use it as their requested target.</summary>
        public static string SelectedAdminArenaId { get; private set; }

        public static void RegisterPrefabs()
        {
            if (_registered)
            {
                return;
            }

            // Resolve every vanilla source before registering anything. A renamed
            // source prefab must not leave an apparently valid but partial hammer
            // menu behind (the old "piece_sign" name did exactly that).
            RequirePieceBasePrefab("guard_stone");
            RequirePieceBasePrefab(SignBasePrefabName);
            RequirePieceBasePrefab("portal_wood");
            RequireChallengeHostSources();

            RegisterLocalization();

            var pieceTable = new CustomPieceTable(PieceTableName, new PieceTableConfig
            {
                UseCategories = true,
                UseCustomCategories = true,
                CustomCategories = new[] { "Arena" },
                CanRemovePieces = true
            });
            if (!PieceManager.Instance.AddPieceTable(pieceTable))
            {
                throw new InvalidOperationException("ArenaGuard could not register its admin piece table.");
            }

            var hammer = new CustomItem(AdminHammerPrefabName, "Hammer");
            ConfigureAdminHammer(hammer, pieceTable.PieceTable);
            if (!ItemManager.Instance.AddItem(hammer))
            {
                throw new InvalidOperationException("ArenaGuard could not register its admin hammer.");
            }
            _adminHammerPrefab = hammer.ItemPrefab;

            RegisterCore();
            RegisterChallengeHost();
            // Kept registered so existing worlds can load old sign ZDOs, but
            // hidden from the hammer. The Arena Master replaces this control.
            RegisterSign(false);
            RegisterGate(EntranceGatePrefabName, "$arenaguard_entrance_gate", false);
            RegisterGate(HubGatePrefabName, "$arenaguard_hub_gate", true);
            // Register the two admin-facing setup beacons before any hidden
            // compatibility prefab. A legacy-world issue must never suppress
            // the actual Combat Start and Enemy Spawn hammer entries.
            RegisterMarker(CombatantStartMarkerPrefabName, "$arenaguard_combat_marker", ArenaMarkerKind.CombatantStart);
            RegisterMarker(EnemySpawnMarkerPrefabName, "$arenaguard_enemy_marker", ArenaMarkerKind.EnemySpawn);

            // These names remain registered only so old world ZDOs can load.
            // Their failures are isolated because Arena Master/Return Gate now
            // provide the corresponding live positions.
            TryRegisterCompatibilityMarker(
                StagingMarkerPrefabName,
                "$arenaguard_staging_marker",
                ArenaMarkerKind.Staging);
            TryRegisterCompatibilityMarker(
                HubGateMarkerPrefabName,
                "$arenaguard_hub_marker",
                ArenaMarkerKind.HubGate);

            _registered = true;
            SubscribeAdminStatusRefresh();
            Plugin.Log?.LogInfo("Registered ArenaGuard admin hammer with Arena Core, Arena Master, two gates, and admin-only Combat/Enemy beacons.");
        }

        public static void SelectAdminArena(string arenaId)
        {
            SelectedAdminArenaId = arenaId ?? string.Empty;
        }

        public static bool IsLocalAdmin()
        {
            // The vanilla client admin list can be temporarily empty while a
            // reconnecting player is recovering their queue/session state.
            // Jotunn separately synchronizes the authenticated local admin bit,
            // so a stale resolver result must never suppress that authoritative
            // status once it has arrived.
            if (LocalAdminResolver != null && LocalAdminResolver())
            {
                return true;
            }

            return SynchronizationManager.Instance != null &&
                   SynchronizationManager.Instance.PlayerIsAdmin;
        }

        public static bool AdminSetupVisualsEnabled => ArenaConfig.ShowAdminSetupVisuals?.Value != false;

        public static string AdminSetupVisualShortcut =>
            ArenaConfig.ToggleAdminSetupVisualsShortcut == null
                ? "F7"
                : ArenaConfig.ToggleAdminSetupVisualsShortcut.Value.ToString();

        internal static bool ShouldShowAdminSetupVisuals()
        {
            return Player.m_localPlayer != null &&
                   ArenaAdminVisualPolicy.ShouldShow(IsLocalAdmin(), AdminSetupVisualsEnabled);
        }

        public static void HandleAdminVisualToggle()
        {
            if (ArenaConfig.ToggleAdminSetupVisualsShortcut != null &&
                ArenaConfig.ToggleAdminSetupVisualsShortcut.Value.IsDown())
            {
                ToggleAdminSetupVisuals();
            }
        }

        public static void ToggleAdminSetupVisuals()
        {
            Player player = Player.m_localPlayer;
            if (player == null || ArenaConfig.ShowAdminSetupVisuals == null)
            {
                return;
            }
            if (!IsLocalAdmin())
            {
                player.Message(
                    MessageHud.MessageType.Center,
                    "Arena setup visuals are waiting for server administrator synchronization.",
                    0,
                    null);
                return;
            }

            ArenaConfig.ShowAdminSetupVisuals.Value = !ArenaConfig.ShowAdminSetupVisuals.Value;
            RefreshAdminSetupVisuals();
            player.Message(
                MessageHud.MessageType.Center,
                ArenaConfig.ShowAdminSetupVisuals.Value
                    ? "Arena setup visuals shown."
                    : "Arena setup visuals hidden. Press " + AdminSetupVisualShortcut + " to show them again.",
                0,
                null);
        }

        private static void RefreshAdminSetupVisuals()
        {
            foreach (ArenaCoreBehaviour core in UnityEngine.Object
                         .FindObjectsByType<ArenaCoreBehaviour>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                core.RefreshAdminVisibility();
            }
            foreach (ArenaMarkerBehaviour marker in UnityEngine.Object
                         .FindObjectsByType<ArenaMarkerBehaviour>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                marker.RefreshAdminVisibility();
            }
        }

        private static void SubscribeAdminStatusRefresh()
        {
            if (_adminStatusRefreshSubscribed)
            {
                return;
            }

            SynchronizationManager.OnAdminStatusChanged += OnAdminStatusChanged;
            _adminStatusRefreshSubscribed = true;
        }

        private static void OnAdminStatusChanged()
        {
            // Force the hammer audit as well as the renderer refresh. This
            // covers initial login, reconnect, and recovered queue sessions.
            _nextHammerAuditTime = 0f;
            _nextGrantAttemptTime = 0f;
            RefreshAdminSetupVisuals();
        }

        internal static string UseKeyLabel()
        {
            string key = ZInput.instance?.GetBoundKeyString("Use", true);
            return string.IsNullOrWhiteSpace(key) ? "E" : key;
        }

        internal static string Localize(string text)
        {
            return Localization.instance == null ? text : Localization.instance.Localize(text);
        }

        /// <summary>
        /// Call from the plugin's main-thread update. It grants exactly one hammer
        /// to an authenticated admin and strips every copy from other clients.
        /// </summary>
        public static void GrantOrRemoveAdminHammer()
        {
            var player = Player.m_localPlayer;
            if (player == null || _adminHammerPrefab == null)
            {
                return;
            }

            float now = Time.unscaledTime;
            bool playerChanged = !ReferenceEquals(_lastLocalPlayer, player);
            if (!playerChanged && now < _nextHammerAuditTime)
            {
                return;
            }
            _lastLocalPlayer = player;
            _nextHammerAuditTime = now + 0.5f;
            if (!IsLocalAdmin())
            {
                RemoveAdminHammer(player);
                return;
            }

            var inventory = player.GetInventory();
            if (FindAdminHammer(inventory) != null || now < _nextGrantAttemptTime)
            {
                return;
            }

            _nextGrantAttemptTime = now + 5f;
            if (!inventory.AddItem(_adminHammerPrefab, 1))
            {
                player.Message(MessageHud.MessageType.Center, "$arenaguard_hammer_inventory_full", 0, null);
            }
        }

        public static void RemoveAdminHammer(Player player)
        {
            if (player == null)
            {
                return;
            }

            var inventory = player.GetInventory();
            ItemDrop.ItemData hammer;
            while ((hammer = FindAdminHammer(inventory)) != null)
            {
                if (player.IsItemEquiped(hammer))
                {
                    player.UnequipItem(hammer, false);
                }
                inventory.RemoveItem(hammer);
            }
        }

        /// <summary>Call before plugin shutdown while the local player still exists.</summary>
        public static void Shutdown()
        {
            if (_adminStatusRefreshSubscribed)
            {
                SynchronizationManager.OnAdminStatusChanged -= OnAdminStatusChanged;
                _adminStatusRefreshSubscribed = false;
            }
            RemoveAdminHammer(Player.m_localPlayer != null ? Player.m_localPlayer : _lastLocalPlayer);
            _lastLocalPlayer = null;
            _nextHammerAuditTime = 0f;
            _nextGrantAttemptTime = 0f;
            ChallengeHostCharacterIds.Clear();
            SelectedAdminArenaId = string.Empty;
        }

        internal static void TrackChallengeHost(Character character)
        {
            if (character != null)
            {
                ChallengeHostCharacterIds.Add(character.GetInstanceID());
            }
        }

        internal static void ForgetChallengeHost(Character character)
        {
            if (character != null)
            {
                ChallengeHostCharacterIds.Remove(character.GetInstanceID());
            }
        }

        internal static bool IsChallengeHost(Character character)
        {
            return character != null && ChallengeHostCharacterIds.Contains(character.GetInstanceID());
        }

        public static int ApplyAdminMarker(string arenaId, ArenaMarkerKind kind, Vector3 position, int slot = -1)
        {
            if (!IsLocalAdmin() || string.IsNullOrWhiteSpace(arenaId))
            {
                return MarkerRequestRejected;
            }

            return AdminMarkerRequested == null
                ? MarkerRequestRejected
                : AdminMarkerRequested(arenaId, kind, ToPositionData(position), slot);
        }

        internal static void RemoveAdminMarker(
            string arenaId,
            ArenaMarkerKind kind,
            Vector3 position,
            int slot)
        {
            if (IsLocalAdmin() && !string.IsNullOrWhiteSpace(arenaId))
            {
                AdminMarkerRemovalRequested?.Invoke(arenaId, kind, ToPositionData(position), slot);
            }
        }

        internal static int ResolveConfiguredEnemySlot(string arenaId, Vector3 position)
        {
            ArenaDefinition arena = ArenaDefinitionResolver?.Invoke(arenaId);
            if (arena?.Markers?.EnemySpawnPositions == null)
            {
                return MarkerRequestRejected;
            }

            for (int index = 0; index < arena.Markers.EnemySpawnPositions.Count && index < 4; index++)
            {
                PositionData candidate = arena.Markers.EnemySpawnPositions[index];
                if (candidate.X == float.MaxValue && candidate.Y == float.MaxValue && candidate.Z == float.MaxValue)
                {
                    continue;
                }
                float dx = candidate.X - position.x;
                float dy = candidate.Y - position.y;
                float dz = candidate.Z - position.z;
                if (dx * dx + dy * dy + dz * dz <= 0.25f)
                {
                    return index;
                }
            }
            return MarkerRequestRejected;
        }

        public static void OpenArenaSign(string arenaId)
        {
            if (!string.IsNullOrWhiteSpace(arenaId))
            {
                ArenaGuard.UI.ArenaUi.OpenChallengeMenu(arenaId);
            }
        }

        public static void ApplyArenaConfiguration(string arenaId, string displayName)
        {
            foreach (ArenaCoreBehaviour core in UnityEngine.Object
                .FindObjectsByType<ArenaCoreBehaviour>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (string.Equals(core.ObjectId, arenaId, StringComparison.Ordinal) ||
                    string.Equals(core.ArenaId, arenaId, StringComparison.Ordinal))
                {
                    core.ApplyOwnedDisplayName(displayName);
                }
            }
        }

        internal static string ResolveArenaId(Vector3 position, string current)
        {
            if (!string.IsNullOrWhiteSpace(current))
            {
                return current;
            }
            if (!string.IsNullOrWhiteSpace(SelectedAdminArenaId))
            {
                return SelectedAdminArenaId;
            }
            return ArenaAtPositionResolver?.Invoke(position) ?? string.Empty;
        }

        internal static void ReportPlacement(ArenaWorldObjectBehaviour worldObject)
        {
            if (PlacementRequested == null || worldObject == null || !worldObject.IsOwner)
            {
                return;
            }

            var view = worldObject.View;
            var zdo = view != null ? view.GetZDO() : null;
            if (zdo == null || zdo.GetBool(ZdoPlacementReported, false))
            {
                return;
            }

            // A Core is the identity anchor for a new arena. It must never inherit
            // the previously selected or nearby arena ID when replacing a Core.
            Vector3 worldPosition = worldObject.WorldPosition;
            var arenaId = worldObject.Kind == ArenaWorldObjectKind.Core
                ? worldObject.ObjectId
                : ResolveArenaId(worldPosition, worldObject.ArenaId);
            if (!string.IsNullOrWhiteSpace(arenaId) && string.IsNullOrWhiteSpace(worldObject.ArenaId))
            {
                zdo.Set(ZdoArenaId, arenaId);
            }

            PlacementRequested(new ArenaWorldObjectPlacement
            {
                Kind = worldObject.Kind,
                ObjectId = worldObject.ObjectId,
                ArenaId = arenaId,
                Position = ToPositionData(worldPosition),
                RotationY = worldObject.WorldRotationY
            });
            zdo.Set(ZdoPlacementReported, true);
        }

        internal static PositionData ToPositionData(Vector3 position)
        {
            return new PositionData { X = position.x, Y = position.y, Z = position.z };
        }

        internal static Vector3 ToVector3(PositionData position)
        {
            return new Vector3(position.X, position.Y, position.Z);
        }

        private static ItemDrop.ItemData FindAdminHammer(Inventory inventory)
        {
            if (inventory == null)
            {
                return null;
            }

            foreach (var item in inventory.GetAllItems())
            {
                if (item?.m_dropPrefab != null && item.m_dropPrefab.name == AdminHammerPrefabName)
                {
                    return item;
                }
            }
            return null;
        }

        private static void ConfigureAdminHammer(CustomItem hammer, PieceTable table)
        {
            if (hammer?.ItemDrop?.m_itemData?.m_shared == null)
            {
                throw new InvalidOperationException("The cloned vanilla hammer is missing ItemDrop shared data.");
            }

            var shared = hammer.ItemDrop.m_itemData.m_shared;
            shared.m_name = "$arenaguard_admin_hammer";
            shared.m_description = "$arenaguard_admin_hammer_description";
            shared.m_buildPieces = table;
            shared.m_questItem = true;
            shared.m_teleportable = true;
            shared.m_useDurability = false;
            shared.m_canBeReparied = false;
        }

        private static PieceConfig Piece(string name, string description)
        {
            return new PieceConfig
            {
                Name = name,
                Description = description,
                PieceTable = PieceTableName,
                Category = "Arena",
                Enabled = true,
                AllowedInDungeons = false,
                Requirements = Array.Empty<RequirementConfig>()
            };
        }

        private static void RegisterCore()
        {
            var custom = new CustomPiece(CorePrefabName, "guard_stone", Piece("$arenaguard_core", "$arenaguard_core_description"));
            RemoveComponent<PrivateArea>(custom.PiecePrefab);
            // guard_stone has EffectArea components on collider-bearing child
            // objects. EffectArea.Awake dereferences its same-object Collider
            // without a null check, so remove the inherited ward effects before
            // stripping their colliders or a placement preview can crash.
            RemoveComponent<EffectArea>(custom.PiecePrefab);
            RemoveComponent<Collider>(custom.PiecePrefab);
            CreateArenaRadiusRing(custom.PiecePrefab.transform, "CombatRadiusRing",
                new Color(0.05f, 0.75f, 1f, 1f));
            CreateArenaRadiusRing(custom.PiecePrefab.transform, "ProtectedRadiusRing",
                new Color(1f, 0.65f, 0f, 1f));
            var interaction = custom.PiecePrefab.AddComponent<SphereCollider>();
            interaction.center = new Vector3(0f, 1f, 0f);
            interaction.radius = 1.25f;
            interaction.isTrigger = true;
            var core = AddWorldBehaviour<ArenaCoreBehaviour>(custom.PiecePrefab, ArenaWorldObjectKind.Core);
            ValidateCoreInteractionPrefab(custom.PiecePrefab, core, interaction);
            AddPiece(custom);
        }

        private static void CreateArenaRadiusRing(Transform parent, string name, Color color)
        {
            int nonSolidLayer = LayerMask.NameToLayer("piece_nonsolid");
            Renderer sourceRenderer = PrefabManager.Instance.GetPrefab(SignBasePrefabName)?
                .GetComponentsInChildren<MeshRenderer>(true)
                .FirstOrDefault(candidate => candidate?.sharedMaterial?.shader != null);
            if (nonSolidLayer < 0 || sourceRenderer?.sharedMaterial == null)
            {
                throw new InvalidOperationException("ArenaGuard could not create the admin radius rings.");
            }

            var visual = new GameObject(name);
            visual.layer = nonSolidLayer;
            visual.transform.SetParent(parent, false);
            visual.transform.localPosition = new Vector3(0f, 0.2f, 0f);
            visual.transform.localRotation = Quaternion.identity;
            visual.transform.localScale = Vector3.one;

            var ring = visual.AddComponent<LineRenderer>();
            ring.sharedMaterial = sourceRenderer.sharedMaterial;
            ring.useWorldSpace = false;
            ring.loop = true;
            ring.positionCount = 96;
            ring.startWidth = 0.16f;
            ring.endWidth = 0.16f;
            ring.numCornerVertices = 2;
            ring.numCapVertices = 2;
            ring.startColor = color;
            ring.endColor = color;
            ring.enabled = false;
            for (int index = 0; index < ring.positionCount; index++)
            {
                float angle = index * Mathf.PI * 2f / ring.positionCount;
                ring.SetPosition(index, new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)));
            }
        }

        private static void ValidateCoreInteractionPrefab(
            GameObject prefab,
            ArenaCoreBehaviour expectedBehaviour,
            SphereCollider expectedCollider)
        {
            if (prefab.GetComponentInChildren<PrivateArea>(true) != null ||
                prefab.GetComponentInChildren<EffectArea>(true) != null)
            {
                throw new InvalidOperationException(
                    "Arena Core prefab retained inherited ward or EffectArea behavior.");
            }

            int hoverableCount = 0;
            int interactableCount = 0;
            foreach (MonoBehaviour component in prefab.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (component is Hoverable)
                {
                    hoverableCount++;
                    if (!ReferenceEquals(component, expectedBehaviour))
                    {
                        throw new InvalidOperationException("Arena Core prefab retained competing Hoverable component '" +
                                                            component.GetType().FullName + "'.");
                    }
                }
                if (component is Interactable)
                {
                    interactableCount++;
                    if (!ReferenceEquals(component, expectedBehaviour))
                    {
                        throw new InvalidOperationException("Arena Core prefab retained competing Interactable component '" +
                                                            component.GetType().FullName + "'.");
                    }
                }
            }

            Collider[] colliders = prefab.GetComponentsInChildren<Collider>(true);
            if (hoverableCount != 1 || interactableCount != 1 || colliders.Length != 1 ||
                !ReferenceEquals(colliders[0], expectedCollider) || !expectedCollider.isTrigger ||
                expectedCollider.gameObject != expectedBehaviour.gameObject)
            {
                throw new InvalidOperationException(
                    "Arena Core requires one root trigger collider and exactly one shared Hoverable/Interactable handler.");
            }

            Plugin.Log?.LogInfo(
                "Validated Arena Core interaction prefab: one root trigger collider and ArenaCoreBehaviour is the sole Hoverable/Interactable.");
        }

        private static void RegisterChallengeHost()
        {
            GameObject prefab = PrefabManager.Instance.CreateClonedPrefab(
                ChallengeHostPrefabName,
                ChallengeHostBasePrefabName);
            if (prefab == null)
            {
                throw new InvalidOperationException("ArenaGuard could not clone the installed Dverger rogue prefab.");
            }

            var character = prefab.GetComponent<Character>();
            var humanoid = prefab.GetComponent<Humanoid>();
            var view = prefab.GetComponent<ZNetView>();
            var body = prefab.GetComponent<Rigidbody>();
            var ai = prefab.GetComponent<BaseAI>();
            if (character == null || humanoid == null || view == null || body == null || ai == null)
            {
                throw new InvalidOperationException(
                    "The installed Dverger rogue prefab is missing its Character, Humanoid, ZNetView, Rigidbody, or AI component.");
            }

            character.m_name = "$arenaguard_challenge_host";
            character.m_aiSkipTarget = true;
            character.m_health = 1000000f;
            ai.enabled = false;
            body.isKinematic = true;
            body.useGravity = false;
            body.constraints = RigidbodyConstraints.FreezeAll;
            view.m_persistent = true;
            RemoveComponent<CharacterDrop>(prefab);

            int nonSolidLayer = LayerMask.NameToLayer("piece_nonsolid");
            if (nonSolidLayer < 0)
            {
                throw new InvalidOperationException("Valheim's piece_nonsolid layer is unavailable.");
            }
            SetLayerRecursively(prefab, nonSolidLayer);
            Collider[] bodyColliders = prefab.GetComponentsInChildren<Collider>(true);
            foreach (Collider collider in bodyColliders)
            {
                collider.enabled = true;
                collider.isTrigger = true;
            }

            Piece piece = prefab.GetComponent<Piece>() ?? prefab.AddComponent<Piece>();
            ItemDrop trophy = PrefabManager.Instance.GetPrefab(ChallengeHostIconPrefabName)?.GetComponent<ItemDrop>();
            piece.m_icon = trophy?.m_itemData?.GetIcon();
            piece.m_groundPiece = true;
            piece.m_groundOnly = true;
            piece.m_clipGround = true;
            piece.m_noInWater = true;

            var interactionObject = new GameObject("ArenaGuard_ChallengeInteraction");
            interactionObject.layer = nonSolidLayer;
            interactionObject.transform.SetParent(prefab.transform, false);
            interactionObject.transform.localPosition = new Vector3(0f, 1f, 0f);
            var interaction = interactionObject.AddComponent<SphereCollider>();
            interaction.radius = 1.15f;
            interaction.isTrigger = true;
            var host = AddWorldBehaviour<ArenaChallengeHostBehaviour>(
                interactionObject,
                ArenaWorldObjectKind.Sign);
            prefab.AddComponent<ArenaChallengeHostRemoval>();

            var custom = new CustomPiece(prefab, false,
                Piece("$arenaguard_challenge_host", "$arenaguard_challenge_host_description"));
            ValidateChallengeHostPrefab(prefab, host, interaction);
            AddPiece(custom);
        }

        private static void RegisterSign(bool enabled)
        {
            var custom = new CustomPiece(SignPrefabName, SignBasePrefabName,
                Piece("$arenaguard_sign", "$arenaguard_sign_description"));
            RemoveComponent<Sign>(custom.PiecePrefab);
            Collider[] interaction = MakeSignCollidersNonSolid(custom.PiecePrefab);
            var sign = AddWorldBehaviour<ArenaSignBehaviour>(custom.PiecePrefab, ArenaWorldObjectKind.Sign);
            ValidateSignDerivedPrefab(custom.PiecePrefab, sign, interaction, "Arena Challenge Sign");
            AddPiece(custom, enabled);
        }

        private static void ValidateChallengeHostPrefab(
            GameObject prefab,
            ArenaChallengeHostBehaviour host,
            SphereCollider interaction)
        {
            var character = prefab.GetComponent<Character>();
            var view = prefab.GetComponent<ZNetView>();
            var body = prefab.GetComponent<Rigidbody>();
            var ai = prefab.GetComponent<BaseAI>();
            var piece = prefab.GetComponent<Piece>();
            Renderer[] renderers = prefab.GetComponentsInChildren<Renderer>(true);
            Collider[] colliders = prefab.GetComponentsInChildren<Collider>(true);
            bool invalidRenderer = renderers.Length == 0;
            foreach (Renderer renderer in renderers)
            {
                invalidRenderer |= renderer == null || renderer.sharedMaterial == null;
            }
            bool invalidCollider = colliders.Length == 0;
            foreach (Collider collider in colliders)
            {
                invalidCollider |= collider == null || !collider.enabled || !collider.isTrigger;
            }
            if (character == null || view == null || !view.m_persistent || body == null ||
                !body.isKinematic || body.useGravity || ai == null || ai.enabled || piece == null ||
                piece.m_icon == null || host == null || interaction == null || !interaction.isTrigger ||
                interaction.GetComponent<Interactable>() == null || invalidRenderer || invalidCollider)
            {
                throw new InvalidOperationException(
                    "Arena Master requires a visible Dverger, persistent network state, frozen AI/body, non-solid colliders, and one interaction bubble.");
            }

            Plugin.Log?.LogInfo("Validated stationary Arena Master Dvergr: renderers=" + renderers.Length +
                                ", colliders=" + colliders.Length +
                                ", persistent=True, aiDisabled=True, nonSolid=True.");
        }

        private static void RegisterGate(string prefabName, string displayName, bool isHub)
        {
            var custom = new CustomPiece(prefabName, "portal_wood", Piece(displayName, "$arenaguard_gate_description"));
            var vanillaPortal = custom.PiecePrefab.GetComponent<TeleportWorld>();
            var gate = AddWorldBehaviour<ArenaGateBehaviour>(custom.PiecePrefab,
                isHub ? ArenaWorldObjectKind.HubGate : ArenaWorldObjectKind.EntranceGate);
            gate.IsHubGate = isHub;
            if (vanillaPortal != null)
            {
                gate.ActivationRange = vanillaPortal.m_activationRange;
                gate.ExitDistance = vanillaPortal.m_exitDistance;
                gate.ProximityRoot = vanillaPortal.m_proximityRoot;
                gate.UnconnectedColor = vanillaPortal.m_colorUnconnected;
                gate.ConnectedColor = vanillaPortal.m_colorTargetfound;
                gate.TargetFoundEffect = vanillaPortal.m_target_found;
                gate.Model = vanillaPortal.m_model;
                gate.ConnectedEffects = vanillaPortal.m_connected;
            }
            ReplacePortalTriggers(custom.PiecePrefab);
            RemoveComponent<TeleportWorld>(custom.PiecePrefab);
            AddPiece(custom);
        }

        private static void ReplacePortalTriggers(GameObject prefab)
        {
            foreach (var trigger in prefab.GetComponentsInChildren<TeleportWorldTrigger>(true))
            {
                if (trigger.GetComponent<ArenaGateTrigger>() == null)
                {
                    trigger.gameObject.AddComponent<ArenaGateTrigger>();
                }
                UnityEngine.Object.DestroyImmediate(trigger);
            }
        }

        private static void RegisterMarker(
            string prefabName,
            string displayName,
            ArenaMarkerKind markerKind,
            bool enabled = true)
        {
            string description = markerKind == ArenaMarkerKind.CombatantStart
                ? "$arenaguard_combat_marker_description"
                : markerKind == ArenaMarkerKind.EnemySpawn
                    ? "$arenaguard_enemy_marker_description"
                    : "$arenaguard_marker_description";
            var custom = new CustomPiece(prefabName, true,
                Piece(displayName, description));
            GameObject prefab = custom.PiecePrefab;
            if (prefab == null)
            {
                throw new InvalidOperationException("ArenaGuard could not create marker prefab " + prefabName + ".");
            }

            int nonSolidLayer = LayerMask.NameToLayer("piece_nonsolid");
            if (nonSolidLayer < 0)
            {
                throw new InvalidOperationException("Valheim's piece_nonsolid layer is unavailable.");
            }
            SetLayerRecursively(prefab, nonSolidLayer);

            Piece piece = prefab.GetComponent<Piece>();
            ItemDrop trophy = PrefabManager.Instance.GetPrefab(ChallengeHostIconPrefabName)?.GetComponent<ItemDrop>();
            if (piece == null)
            {
                throw new InvalidOperationException("ArenaGuard marker prefab has no Piece component.");
            }
            piece.m_icon = trophy?.m_itemData?.GetIcon();
            piece.m_groundPiece = true;
            piece.m_groundOnly = true;
            piece.m_clipGround = true;
            piece.m_noInWater = false;

            ZNetView view = prefab.GetComponent<ZNetView>();
            if (view != null)
            {
                view.m_persistent = true;
            }

            RemoveComponent<Collider>(prefab);
            CreateMarkerVisuals(prefab, markerKind, nonSolidLayer);
            var interaction = prefab.AddComponent<SphereCollider>();
            interaction.center = new Vector3(0f, 1f, 0f);
            interaction.radius = 0.85f;
            interaction.isTrigger = true;
            var marker = AddWorldBehaviour<ArenaMarkerBehaviour>(custom.PiecePrefab, MarkerObjectKind(markerKind));
            marker.MarkerKind = markerKind;
            ValidateMarkerPrefab(prefab, marker, interaction, displayName);
            AddPiece(custom, enabled);
        }

        private static void TryRegisterCompatibilityMarker(
            string prefabName,
            string displayName,
            ArenaMarkerKind markerKind)
        {
            try
            {
                RegisterMarker(prefabName, displayName, markerKind, false);
            }
            catch (Exception exception)
            {
                Plugin.Log?.LogWarning("Could not register hidden compatibility marker '" + prefabName +
                                       "'. Combat Start and Enemy Spawn remain available. " + exception);
            }
        }

        private static void CreateMarkerVisuals(GameObject prefab, ArenaMarkerKind markerKind, int layer)
        {
            CreateMarkerPrimitive(prefab.transform, PrimitiveType.Cylinder, "FloorRing",
                new Vector3(0f, 0.04f, 0f), new Vector3(1.35f, 0.04f, 1.35f), layer);
            CreateMarkerPrimitive(prefab.transform, PrimitiveType.Cylinder, "BeaconPost",
                new Vector3(0f, 0.9f, 0f), new Vector3(0.08f, 0.9f, 0.08f), layer);
            CreateMarkerPrimitive(prefab.transform, PrimitiveType.Sphere, "BeaconTop",
                new Vector3(0f, 1.85f, 0f), new Vector3(0.32f, 0.32f, 0.32f), layer);
        }

        internal static Color MarkerColor(ArenaMarkerKind markerKind)
        {
            switch (markerKind)
            {
                case ArenaMarkerKind.CombatantStart: return new Color(0.05f, 0.75f, 1f, 1f);
                case ArenaMarkerKind.EnemySpawn: return new Color(1f, 0f, 0f, 1f);
                case ArenaMarkerKind.HubGate: return new Color(1f, 0.7f, 0f, 1f);
                default: return new Color(0.2f, 1f, 0.25f, 1f);
            }
        }

        private static void CreateMarkerPrimitive(
            Transform parent,
            PrimitiveType type,
            string name,
            Vector3 localPosition,
            Vector3 localScale,
            int layer)
        {
            GameObject visual = GameObject.CreatePrimitive(type);
            visual.name = name;
            visual.layer = layer;
            visual.transform.SetParent(parent, false);
            visual.transform.localPosition = localPosition;
            visual.transform.localRotation = Quaternion.identity;
            visual.transform.localScale = localScale;
            Collider collider = visual.GetComponent<Collider>();
            if (collider != null)
            {
                UnityEngine.Object.DestroyImmediate(collider);
            }
            Renderer renderer = visual.GetComponent<Renderer>();
            Renderer sourceRenderer = PrefabManager.Instance.GetPrefab(SignBasePrefabName)?
                .GetComponentsInChildren<MeshRenderer>(true)
                .FirstOrDefault(candidate => candidate?.sharedMaterial?.shader != null);
            if (renderer == null || sourceRenderer?.sharedMaterial == null)
            {
                throw new InvalidOperationException("ArenaGuard could not resolve a Valheim marker material.");
            }
            // Unity's cylinder mesh has separate side/cap material slots. Setting
            // only sharedMaterial colors the pole sides but leaves the flat cap on
            // Jotunn's unsupported default, which renders bright purple. Reuse the
            // exact game-owned material in every slot so the base and pole match.
            int materialSlotCount = Math.Max(1, renderer.sharedMaterials.Length);
            renderer.sharedMaterials = Enumerable
                .Repeat(sourceRenderer.sharedMaterial, materialSlotCount)
                .ToArray();
        }

        private static void ValidateMarkerPrefab(
            GameObject prefab,
            ArenaMarkerBehaviour expectedBehaviour,
            SphereCollider expectedCollider,
            string displayName)
        {
            Collider[] colliders = prefab.GetComponentsInChildren<Collider>(true);
            Renderer[] renderers = prefab.GetComponentsInChildren<Renderer>(true);
            Piece piece = prefab.GetComponent<Piece>();
            ZNetView view = prefab.GetComponent<ZNetView>();
            int hoverableCount = prefab.GetComponentsInChildren<MonoBehaviour>(true).Count(component => component is Hoverable);
            int interactableCount = prefab.GetComponentsInChildren<MonoBehaviour>(true).Count(component => component is Interactable);
            var failures = new List<string>();
            if (piece == null || piece.m_icon == null) failures.Add("build Piece/icon");
            if (view == null || !view.m_persistent) failures.Add("persistent ZNetView");
            if (expectedBehaviour == null) failures.Add("ArenaMarkerBehaviour");
            if (colliders.Length != 1 || !ReferenceEquals(colliders.FirstOrDefault(), expectedCollider) ||
                expectedCollider == null || !expectedCollider.isTrigger)
                failures.Add("one trigger-only selection collider");
            if (renderers.Length != 3 || renderers.Any(renderer => renderer == null || renderer.sharedMaterial == null))
                failures.Add("three initialized beacon renderers");
            if (hoverableCount != 1 || interactableCount != 1)
                failures.Add("one Hoverable/Interactable handler");
            if (failures.Count != 0)
            {
                // This is a diagnostic, not an availability gate. Jotunn can
                // still register the Piece, and suppressing the hammer entry is
                // worse than allowing an admin to test a degraded beacon.
                Plugin.Log?.LogWarning(displayName + " marker validation warning: " +
                                       string.Join(", ", failures) + ". Registration will continue.");
                return;
            }

            Plugin.Log?.LogInfo("Validated admin-only non-solid " + displayName +
                                ": renderers=" + renderers.Length + ", colliders=1.");
        }

        private static Collider[] MakeSignCollidersNonSolid(GameObject prefab)
        {
            // Preserve the exact visible 0.1.5 sign prefab hierarchy. Deleting
            // the authored collider and adding a new root collider made the
            // placement/live sign mesh disappear. The only required mutation
            // for a non-solid sign is converting its existing collider to a
            // trigger in place.
            Collider[] colliders = prefab.GetComponentsInChildren<Collider>(true);
            if (colliders.Length == 0)
            {
                throw new InvalidOperationException("The Valheim sign source has no selection collider.");
            }
            foreach (Collider collider in colliders)
            {
                collider.enabled = true;
                collider.isTrigger = true;
            }
            return colliders;
        }

        private static void ValidateSignDerivedPrefab(
            GameObject prefab,
            ArenaWorldObjectBehaviour expectedBehaviour,
            Collider[] expectedColliders,
            string displayName)
        {
            int hoverableCount = 0;
            int interactableCount = 0;
            foreach (MonoBehaviour component in prefab.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (component is Hoverable)
                {
                    hoverableCount++;
                    if (!ReferenceEquals(component, expectedBehaviour))
                    {
                        throw new InvalidOperationException(displayName + " retained competing Hoverable component '" +
                                                            component.GetType().FullName + "'.");
                    }
                }
                if (component is Interactable)
                {
                    interactableCount++;
                    if (!ReferenceEquals(component, expectedBehaviour))
                    {
                        throw new InvalidOperationException(displayName + " retained competing Interactable component '" +
                                                            component.GetType().FullName + "'.");
                    }
                }
            }

            Collider[] colliders = prefab.GetComponentsInChildren<Collider>(true);
            Renderer[] renderers = prefab.GetComponentsInChildren<Renderer>(true);
            MeshFilter[] meshes = prefab.GetComponentsInChildren<MeshFilter>(true);
            bool invalidCollider = colliders.Length == 0 || expectedColliders == null ||
                colliders.Length != expectedColliders.Length;
            foreach (Collider collider in colliders)
            {
                invalidCollider |= collider == null || !collider.enabled || !collider.isTrigger;
            }
            bool invalidVisual = renderers.Length == 0 || meshes.Length == 0;
            foreach (Renderer renderer in renderers)
            {
                invalidVisual |= renderer == null || !renderer.enabled || renderer.forceRenderingOff ||
                    !renderer.gameObject.activeSelf || renderer.sharedMaterial == null;
            }
            foreach (MeshFilter mesh in meshes)
            {
                invalidVisual |= mesh == null || mesh.sharedMesh == null ||
                    mesh.sharedMesh.bounds.size.sqrMagnitude <= 0.0001f;
            }
            if (hoverableCount != 1 || interactableCount != 1 || invalidCollider || invalidVisual)
            {
                throw new InvalidOperationException(displayName +
                    " requires visible mesh/material data, preserved trigger colliders, and one interaction handler.");
            }

            Plugin.Log?.LogInfo("Validated visible non-solid " + displayName +
                                ": renderers=" + renderers.Length +
                                ", meshes=" + meshes.Length +
                                ", colliders=" + colliders.Length + ".");
        }

        private static T AddWorldBehaviour<T>(GameObject prefab, ArenaWorldObjectKind kind) where T : ArenaWorldObjectBehaviour
        {
            var behaviour = prefab.GetComponent<T>() ?? prefab.AddComponent<T>();
            behaviour.Kind = kind;
            return behaviour;
        }

        private static void SetLayerRecursively(GameObject root, int layer)
        {
            root.layer = layer;
            foreach (Transform child in root.transform)
            {
                SetLayerRecursively(child.gameObject, layer);
            }
        }

        private static ArenaWorldObjectKind MarkerObjectKind(ArenaMarkerKind markerKind)
        {
            switch (markerKind)
            {
                case ArenaMarkerKind.Staging: return ArenaWorldObjectKind.StagingMarker;
                case ArenaMarkerKind.CombatantStart: return ArenaWorldObjectKind.CombatantStartMarker;
                case ArenaMarkerKind.EnemySpawn: return ArenaWorldObjectKind.EnemySpawnMarker;
                default: return ArenaWorldObjectKind.HubGateMarker;
            }
        }

        private static void AddPiece(CustomPiece piece, bool enabled = true)
        {
            if (piece?.PiecePrefab == null)
            {
                throw new InvalidOperationException("ArenaGuard received an empty custom piece prefab during registration.");
            }

            if (piece.Piece == null)
            {
                throw new InvalidOperationException("ArenaGuard piece '" + piece.PiecePrefab.name + "' has no Piece component.");
            }

            if (piece.Piece.m_icon == null)
            {
                throw new InvalidOperationException("ArenaGuard piece '" + piece.PiecePrefab.name + "' has no build-menu icon.");
            }

            if (string.IsNullOrWhiteSpace(piece.Piece.m_name) || string.IsNullOrWhiteSpace(piece.Piece.m_description))
            {
                throw new InvalidOperationException("ArenaGuard piece '" + piece.PiecePrefab.name +
                                                    "' has incomplete build-menu name or description metadata.");
            }

            piece.Piece.m_enabled = enabled;
            piece.Piece.m_canBeRemoved = true;
            piece.Piece.m_resources = Array.Empty<Piece.Requirement>();
            piece.Piece.m_craftingStation = null;
            piece.Piece.m_onlyInTeleportArea = false;
            piece.Piece.m_cultivatedGroundOnly = false;
            piece.Piece.m_notOnFloor = false;
            piece.Piece.m_inCeilingOnly = false;
            piece.Piece.m_noClipping = false;
            piece.Piece.m_spaceRequirement = 0f;
            piece.Piece.m_mustConnectTo = null;
            piece.Piece.m_blockingPieces?.Clear();
            if (!PieceManager.Instance.AddPiece(piece))
            {
                throw new InvalidOperationException("ArenaGuard could not register piece " + piece.PiecePrefab.name + ".");
            }
            piece.Piece.m_enabled = enabled;

            Plugin.Log?.LogInfo("Registered ArenaGuard build piece '" + piece.PiecePrefab.name + "' (" +
                                piece.Piece.m_name + ").");
        }

        private static void RequirePieceBasePrefab(string prefabName)
        {
            GameObject prefab = PrefabManager.Instance.GetPrefab(prefabName);
            Piece piece = prefab == null ? null : prefab.GetComponent<Piece>();
            if (prefab == null || piece == null)
            {
                throw new InvalidOperationException("ArenaGuard requires the installed Valheim piece prefab '" +
                                                    prefabName + "', but it was not available.");
            }
            if (piece.m_icon == null)
            {
                throw new InvalidOperationException("Valheim piece prefab '" + prefabName +
                                                    "' has no icon for ArenaGuard to inherit.");
            }
        }

        private static void RequireChallengeHostSources()
        {
            GameObject dverger = PrefabManager.Instance.GetPrefab(ChallengeHostBasePrefabName);
            GameObject trophy = PrefabManager.Instance.GetPrefab(ChallengeHostIconPrefabName);
            if (dverger == null || dverger.GetComponent<Character>() == null ||
                dverger.GetComponent<Humanoid>() == null || dverger.GetComponent<ZNetView>() == null ||
                dverger.GetComponent<Rigidbody>() == null || dverger.GetComponent<BaseAI>() == null)
            {
                throw new InvalidOperationException(
                    "ArenaGuard requires the installed Valheim Dverger rogue prefab with its standard character components.");
            }
            if (trophy?.GetComponent<ItemDrop>()?.m_itemData?.GetIcon() == null)
            {
                throw new InvalidOperationException(
                    "ArenaGuard requires the installed TrophyDvergr prefab for the Arena Master build icon.");
            }
        }

        private static void RemoveComponent<T>(GameObject prefab) where T : Component
        {
            foreach (var component in prefab.GetComponentsInChildren<T>(true))
            {
                UnityEngine.Object.DestroyImmediate(component);
            }
        }

        private static void RegisterLocalization()
        {
            var tokens = new Dictionary<string, string>
            {
                ["arenaguard_admin_hammer"] = "Arena Admin Hammer",
                ["arenaguard_admin_hammer_description"] = "Admin-only tool for placing and configuring arenas.",
                ["arenaguard_hammer_inventory_full"] = "Make one inventory space for the Arena Admin Hammer.",
                ["arenaguard_core"] = "Arena Core",
                ["arenaguard_core_description"] = "Defines an administrator-managed protected arena.",
                ["arenaguard_sign"] = "Arena Challenge Sign",
                ["arenaguard_sign_description"] = "Choose a challenge or view the server leaderboard.",
                ["arenaguard_challenge_host"] = "Arena Master",
                ["arenaguard_challenge_host_description"] = "Talk to this named Dvergr rogue to choose a challenge or view the server leaderboard.",
                ["arenaguard_entrance_gate"] = "Arena Gate",
                ["arenaguard_hub_gate"] = "Arena Return Gate",
                ["arenaguard_gate_description"] = "A named SkaldHall portal which preserves vanilla teleport restrictions.",
                ["arenaguard_staging_marker"] = "Staging Position",
                ["arenaguard_combat_marker"] = "Combat Start Position",
                ["arenaguard_enemy_marker"] = "Enemy Spawn Position",
                ["arenaguard_hub_marker"] = "Hub Gate Position",
                ["arenaguard_marker_description"] = "Place this marker with the Arena Admin Hammer.",
                ["arenaguard_combat_marker_description"] = "Admin-only cyan beacon for the combatant's starting point. Place exactly one.",
                ["arenaguard_enemy_marker_description"] = "Admin-only red enemy spawn beacon. Place four; they number themselves automatically.",
                ["arenaguard_admin_only"] = "Only a server administrator can use this.",
                ["arenaguard_admin_build_only"] = "Only an authorized arena administrator may build or demolish here.",
                ["arenaguard_unconfigured"] = "This arena object has not been configured yet.",
                ["arenaguard_gate_name_topic"] = "Unique gate name"
            };
            var language = "English";
            LocalizationManager.Instance.GetLocalization().AddTranslation(in language, tokens);
        }
    }

    public abstract class ArenaWorldObjectBehaviour : MonoBehaviour
    {
        public ArenaWorldObjectKind Kind;
        protected ZNetView NView;

        public ZNetView View => NView;
        public bool IsOwner => NView != null && NView.IsValid() && NView.IsOwner();
        public string ObjectId => GetOrCreateObjectId();
        public string ArenaId => Read(ZdoArenaId);
        public virtual Vector3 WorldPosition => transform.position;
        public virtual float WorldRotationY => transform.eulerAngles.y;
        protected static string ZdoArenaId => ArenaWorldObjects.ZdoArenaId;

        protected virtual void Awake()
        {
            NView = GetComponent<ZNetView>() ?? GetComponentInParent<ZNetView>();
            GetOrCreateObjectId();
        }

        protected virtual void Start()
        {
            ArenaWorldObjects.ReportPlacement(this);
        }

        protected string Read(string key)
        {
            var zdo = NView != null ? NView.GetZDO() : null;
            return zdo?.GetString(key, string.Empty) ?? string.Empty;
        }

        protected int ReadInt(string key, int fallback)
        {
            var zdo = NView != null ? NView.GetZDO() : null;
            return zdo?.GetInt(key, fallback) ?? fallback;
        }

        protected bool ReadBool(string key, bool fallback)
        {
            var zdo = NView != null ? NView.GetZDO() : null;
            return zdo?.GetBool(key, fallback) ?? fallback;
        }

        protected void WriteOwned(string key, string value)
        {
            if (IsOwner)
            {
                NView.GetZDO().Set(key, value ?? string.Empty);
            }
        }

        protected void WriteOwned(string key, int value)
        {
            if (IsOwner)
            {
                NView.GetZDO().Set(key, value);
            }
        }

        private string GetOrCreateObjectId()
        {
            var id = Read(ArenaWorldObjects.ZdoObjectId);
            if (string.IsNullOrWhiteSpace(id) && IsOwner)
            {
                id = Guid.NewGuid().ToString("N").ToLowerInvariant();
                WriteOwned(ArenaWorldObjects.ZdoObjectId, id);
            }
            return id;
        }
    }

    public sealed class ArenaCoreBehaviour : ArenaWorldObjectBehaviour, Hoverable, Interactable, IRemoved
    {
        private static readonly Color CombatRadiusColor = new Color(0.05f, 0.75f, 1f, 1f);
        private static readonly Color ProtectedRadiusColor = new Color(1f, 0.65f, 0f, 1f);
        private Renderer[] _renderers = Array.Empty<Renderer>();
        private Projector[] _projectors = Array.Empty<Projector>();
        private Light[] _lights = Array.Empty<Light>();
        private LightFlicker[] _lightFlickers = Array.Empty<LightFlicker>();
        private LightLod[] _lightLods = Array.Empty<LightLod>();
        private AudioSource[] _audioSources = Array.Empty<AudioSource>();
        private SphereCollider _interactionCollider;
        private LineRenderer _combatRadiusRing;
        private LineRenderer _protectedRadiusRing;
        private readonly MaterialPropertyBlock _combatRadiusProperties = new MaterialPropertyBlock();
        private readonly MaterialPropertyBlock _protectedRadiusProperties = new MaterialPropertyBlock();
        private float _renderedCombatRadius = float.NaN;
        private float _renderedProtectedRadius = float.NaN;
        private bool _combatRadiusStyleApplied;
        private bool _protectedRadiusStyleApplied;
        private float _nextVisibilityRefresh;

        protected override void Awake()
        {
            base.Awake();
            _renderers = GetComponentsInChildren<Renderer>(true);
            _projectors = GetComponentsInChildren<Projector>(true);
            _lights = GetComponentsInChildren<Light>(true);
            _lightFlickers = GetComponentsInChildren<LightFlicker>(true);
            _lightLods = GetComponentsInChildren<LightLod>(true);
            _audioSources = GetComponentsInChildren<AudioSource>(true);
            _interactionCollider = GetComponent<SphereCollider>();
            _combatRadiusRing = GetComponentsInChildren<LineRenderer>(true)
                .FirstOrDefault(candidate => candidate.name == "CombatRadiusRing");
            _protectedRadiusRing = GetComponentsInChildren<LineRenderer>(true)
                .FirstOrDefault(candidate => candidate.name == "ProtectedRadiusRing");
            // Jotunn invokes Awake while constructing the registered prefab
            // template, before a local player/admin identity exists. Mutating
            // renderer state then makes the first placement preview inherit an
            // invisible Core. Defer visibility until a real local player exists.
            if (Player.m_localPlayer != null)
            {
                ApplyAdminVisibility();
            }
        }

        protected override void Start()
        {
            base.Start();
            ApplyAdminVisibility();
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextVisibilityRefresh)
            {
                return;
            }

            _nextVisibilityRefresh = Time.unscaledTime + 0.5f;
            ApplyAdminVisibility();
        }

        private void ApplyAdminVisibility()
        {
            bool visible = ArenaWorldObjects.ShouldShowAdminSetupVisuals();
            foreach (Renderer renderer in _renderers)
            {
                if (renderer != null)
                {
                    renderer.enabled = visible;
                }
            }
            foreach (Projector projector in _projectors)
            {
                if (projector != null)
                {
                    projector.enabled = visible;
                }
            }
            // LightLod can re-enable an explicitly disabled Light from its
            // coroutine, and LightFlicker keeps updating it globally. Disable
            // both controllers while setup visuals are hidden, before forcing
            // the light itself off, so a hidden Core costs no lighting work and
            // cannot leave a flashing pool on the arena floor.
            foreach (LightFlicker flicker in _lightFlickers)
            {
                if (flicker != null)
                {
                    flicker.enabled = visible;
                }
            }
            foreach (LightLod lightLod in _lightLods)
            {
                if (lightLod != null)
                {
                    lightLod.enabled = visible;
                }
            }
            foreach (Light light in _lights)
            {
                if (light != null)
                {
                    light.enabled = visible;
                    if (!visible)
                    {
                        light.intensity = 0f;
                        light.range = 0f;
                    }
                }
            }
            foreach (AudioSource source in _audioSources)
            {
                if (source != null)
                {
                    source.enabled = visible;
                }
            }
            if (_interactionCollider != null)
            {
                _interactionCollider.isTrigger = true;
                _interactionCollider.enabled = visible;
            }
            ApplyRadiusRingVisibility(visible);
        }

        internal void RefreshAdminVisibility()
        {
            ApplyAdminVisibility();
        }

        private void ApplyRadiusRingVisibility(bool adminVisible)
        {
            string arenaId = ResolveCanonicalArenaId();
            if (string.IsNullOrWhiteSpace(arenaId))
            {
                arenaId = ArenaWorldObjects.ArenaAtPositionResolver?.Invoke(transform.position) ?? string.Empty;
            }
            ArenaDefinition arena = string.IsNullOrWhiteSpace(arenaId)
                ? null
                : ArenaWorldObjects.ArenaDefinitionResolver?.Invoke(arenaId);
            ApplyRadiusRing(
                _combatRadiusRing,
                arena?.CombatRadius ?? 0f,
                CombatRadiusColor,
                adminVisible && arena != null,
                _combatRadiusProperties,
                ref _renderedCombatRadius,
                ref _combatRadiusStyleApplied);
            ApplyRadiusRing(
                _protectedRadiusRing,
                arena?.ProtectedRadius ?? 0f,
                ProtectedRadiusColor,
                adminVisible && arena != null,
                _protectedRadiusProperties,
                ref _renderedProtectedRadius,
                ref _protectedRadiusStyleApplied);
        }

        private static void ApplyRadiusRing(
            LineRenderer ring,
            float radius,
            Color color,
            bool visible,
            MaterialPropertyBlock properties,
            ref float renderedRadius,
            ref bool styleApplied)
        {
            if (ring == null)
            {
                return;
            }
            bool validRadius = !float.IsNaN(radius) && !float.IsInfinity(radius) && radius > 0f;
            ring.enabled = visible && validRadius;
            if (!ring.enabled)
            {
                return;
            }

            if (float.IsNaN(renderedRadius) || !Mathf.Approximately(renderedRadius, radius))
            {
                for (int index = 0; index < ring.positionCount; index++)
                {
                    float angle = index * Mathf.PI * 2f / ring.positionCount;
                    ring.SetPosition(index, new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius));
                }
                renderedRadius = radius;
            }
            if (styleApplied)
            {
                return;
            }

            ring.startColor = color;
            ring.endColor = color;
            ring.GetPropertyBlock(properties);
            Material material = ring.sharedMaterial;
            if (material != null && material.HasProperty("_MainTex"))
                properties.SetTexture("_MainTex", Texture2D.whiteTexture);
            if (material != null && material.HasProperty("_BaseMap"))
                properties.SetTexture("_BaseMap", Texture2D.whiteTexture);
            if (material != null && material.HasProperty("_Color"))
                properties.SetColor("_Color", color);
            if (material != null && material.HasProperty("_BaseColor"))
                properties.SetColor("_BaseColor", color);
            if (material != null && material.HasProperty("_EmissionColor"))
                properties.SetColor("_EmissionColor", color * 1.25f);
            ring.SetPropertyBlock(properties);
            styleApplied = true;
        }

        public string GetHoverName()
        {
            if (!ArenaWorldObjects.IsLocalAdmin())
            {
                return string.Empty;
            }
            var name = Read(ArenaWorldObjects.ZdoDisplayName);
            return ArenaWorldObjects.Localize(string.IsNullOrWhiteSpace(name) ? "$arenaguard_core" : name);
        }

        public string GetHoverText()
        {
            if (!ArenaWorldObjects.IsLocalAdmin())
            {
                return string.Empty;
            }
            return GetHoverName() + "\n[<color=yellow><b>" + ArenaWorldObjects.UseKeyLabel() +
                   "</b></color>] Configure arena";
        }

        public bool Interact(Humanoid user, bool hold, bool alt)
        {
            if (hold || user != Player.m_localPlayer)
            {
                return false;
            }
            if (!ArenaWorldObjects.IsLocalAdmin())
            {
                user.Message(MessageHud.MessageType.Center, "$arenaguard_admin_only", 0, null);
                return true;
            }

            var arenaId = ResolveCanonicalArenaId();
            if (string.IsNullOrWhiteSpace(arenaId))
            {
                // A freshly replicated ZNetView can briefly have neither of its
                // IDs locally even though the arena snapshot is already present.
                // Resolve the registered Core at this position instead of making
                // the first E press disappear without feedback.
                arenaId = ArenaWorldObjects.ArenaAtPositionResolver?.Invoke(transform.position) ?? string.Empty;
            }
            if (string.IsNullOrWhiteSpace(arenaId))
            {
                Plugin.Log?.LogWarning("Could not open Arena Core administration because the Core has no arena ID yet.");
                user.Message(MessageHud.MessageType.Center,
                    "This Arena Core is still initializing. Try again in a moment.", 0, null);
                return true;
            }
            ArenaWorldObjects.CoreActivated?.Invoke(
                arenaId,
                ArenaWorldObjects.ToPositionData(transform.position));
            ArenaWorldObjects.SelectAdminArena(arenaId);
            ArenaGuard.UI.ArenaUi.OpenAdminPanel(arenaId, ArenaWorldObjects.ArenaDefinitionResolver?.Invoke(arenaId));
            return true;
        }

        public void ApplyOwnedDisplayName(string displayName)
        {
            WriteOwned(ArenaWorldObjects.ZdoDisplayName, displayName);
        }

        public void OnRemoved()
        {
            if (!ArenaWorldObjects.IsLocalAdmin())
            {
                return;
            }

            string arenaId = ResolveCanonicalArenaId();
            if (!string.IsNullOrWhiteSpace(arenaId))
            {
                ArenaWorldObjects.CoreRemovalRequested?.Invoke(arenaId);
            }
        }

        private string ResolveCanonicalArenaId()
        {
            string objectId = ObjectId;
            if (!string.IsNullOrWhiteSpace(objectId) &&
                ArenaWorldObjects.ArenaDefinitionResolver?.Invoke(objectId) != null)
            {
                return objectId;
            }
            if (!string.IsNullOrWhiteSpace(ArenaId))
            {
                return ArenaId;
            }
            return objectId;
        }

        public bool UseItem(Humanoid user, ItemDrop.ItemData item) => false;
    }

    public sealed class ArenaSignBehaviour : ArenaWorldObjectBehaviour, Hoverable, Interactable
    {
        protected override void Start()
        {
            base.Start();
            if (IsOwner && ArenaWorldObjects.IsLocalAdmin())
            {
                string arenaId = ArenaWorldObjects.ResolveArenaId(transform.position, ArenaId);
                ArenaWorldObjects.ApplyAdminMarker(
                    arenaId,
                    ArenaMarkerKind.Staging,
                    transform.position);
            }
        }

        public string GetHoverName() => ArenaWorldObjects.Localize("$arenaguard_sign");

        public string GetHoverText()
        {
            return GetHoverName() + "\n[<color=yellow><b>" + ArenaWorldObjects.UseKeyLabel() +
                   "</b></color>] Choose challenge";
        }

        public bool Interact(Humanoid user, bool hold, bool alt)
        {
            if (hold || user != Player.m_localPlayer)
            {
                return false;
            }

            var arenaId = ArenaWorldObjects.ResolveArenaId(transform.position, ArenaId);
            if (string.IsNullOrWhiteSpace(arenaId))
            {
                user.Message(MessageHud.MessageType.Center, "$arenaguard_unconfigured", 0, null);
                return true;
            }
            ArenaWorldObjects.OpenArenaSign(arenaId);
            return true;
        }

        public bool UseItem(Humanoid user, ItemDrop.ItemData item) => false;
    }

    public sealed class ArenaChallengeHostBehaviour : ArenaWorldObjectBehaviour, Hoverable, Interactable
    {
        private Character _character;

        protected override void Awake()
        {
            base.Awake();
            _character = GetComponent<Character>() ?? GetComponentInParent<Character>();
            ArenaWorldObjects.TrackChallengeHost(_character);
        }

        private void OnDestroy()
        {
            ArenaWorldObjects.ForgetChallengeHost(_character);
        }

        protected override void Start()
        {
            base.Start();
            Character character = _character;
            if (character != null)
            {
                character.m_name = "$arenaguard_challenge_host";
                character.m_aiSkipTarget = true;
            }
            Humanoid humanoid = GetComponentInParent<Humanoid>();
            if (IsOwner && humanoid != null)
            {
                humanoid.EquipBestWeapon(null, null, null, null);
            }
            if (IsOwner && ArenaWorldObjects.IsLocalAdmin())
            {
                string arenaId = ArenaWorldObjects.ResolveArenaId(WorldPosition, ArenaId);
                ArenaWorldObjects.ApplyAdminMarker(arenaId, ArenaMarkerKind.Staging, WorldPosition);
            }
        }

        public override Vector3 WorldPosition => NView != null ? NView.transform.position : transform.position;
        public override float WorldRotationY => NView != null ? NView.transform.eulerAngles.y : transform.eulerAngles.y;

        public string GetHoverName() => ArenaWorldObjects.Localize("$arenaguard_challenge_host");

        public string GetHoverText()
        {
            return GetHoverName() + "\n[<color=yellow><b>" + ArenaWorldObjects.UseKeyLabel() +
                   "</b></color>] Choose challenge";
        }

        public bool Interact(Humanoid user, bool hold, bool alt)
        {
            if (hold || user != Player.m_localPlayer)
            {
                return false;
            }

            string arenaId = ArenaWorldObjects.ResolveArenaId(WorldPosition, ArenaId);
            if (string.IsNullOrWhiteSpace(arenaId))
            {
                user.Message(MessageHud.MessageType.Center, "$arenaguard_unconfigured", 0, null);
                return true;
            }
            ArenaWorldObjects.OpenArenaSign(arenaId);
            return true;
        }

        public bool UseItem(Humanoid user, ItemDrop.ItemData item) => false;
    }

    public sealed class ArenaChallengeHostRemoval : MonoBehaviour, IRemoved
    {
        public void OnRemoved()
        {
            if (!ArenaWorldObjects.IsLocalAdmin())
            {
                return;
            }
            ZNetView view = GetComponent<ZNetView>();
            if (view != null && view.IsValid())
            {
                view.ClaimOwnership();
                ZNetScene.instance?.Destroy(gameObject);
            }
        }
    }

    public sealed class ArenaMarkerBehaviour : ArenaWorldObjectBehaviour, Hoverable, Interactable, IRemoved
    {
        public ArenaMarkerKind MarkerKind;
        private Renderer[] _renderers = Array.Empty<Renderer>();
        private SphereCollider _selectionCollider;
        private readonly MaterialPropertyBlock _appearanceProperties = new MaterialPropertyBlock();
        private bool _appearanceApplied;
        private float _nextVisibilityRefresh;

        public int MarkerSlot => ReadInt(ArenaWorldObjects.ZdoMarkerSlot, ArenaWorldObjects.MarkerRequestRejected);

        protected override void Awake()
        {
            base.Awake();
            _renderers = GetComponentsInChildren<Renderer>(true);
            _selectionCollider = GetComponent<SphereCollider>();
            // Do not write hidden presentation state into Jotunn's registered
            // marker template before the local administrator is available.
            if (Player.m_localPlayer != null)
            {
                ApplyAdminVisibility();
            }
        }

        protected override void Start()
        {
            bool wasAlreadyReported = ReadBool(ArenaWorldObjects.ZdoPlacementReported, false);
            base.Start();
            ApplyAdminVisibility();
            if (IsOwner && ArenaWorldObjects.IsLocalAdmin())
            {
                string arenaId = ArenaWorldObjects.ResolveArenaId(WorldPosition, ArenaId);
                int requestedSlot = MarkerKind == ArenaMarkerKind.EnemySpawn
                    ? ResolveEnemySlot(arenaId)
                    : -1;
                int resolvedSlot = ArenaWorldObjects.ApplyAdminMarker(
                    arenaId,
                    MarkerKind,
                    WorldPosition,
                    requestedSlot);
                if (resolvedSlot != ArenaWorldObjects.MarkerRequestRejected)
                {
                    WriteOwned(ArenaWorldObjects.ZdoMarkerKind, (int)MarkerKind);
                    WriteOwned(ArenaWorldObjects.ZdoMarkerSlot, resolvedSlot);
                }
                else if (!wasAlreadyReported && NView != null && NView.IsValid())
                {
                    // A fifth Enemy Spawn should not leave an unsaved orphan in
                    // the world. Existing legacy markers are never auto-deleted.
                    NView.ClaimOwnership();
                    ZNetScene.instance?.Destroy(NView.gameObject);
                }
            }
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextVisibilityRefresh)
            {
                return;
            }
            _nextVisibilityRefresh = Time.unscaledTime + 0.5f;
            ApplyAdminVisibility();
        }

        private int ResolveEnemySlot(string arenaId)
        {
            int stored = MarkerSlot;
            if (stored >= 0 && stored < 4)
            {
                return stored;
            }
            int configured = ArenaWorldObjects.ResolveConfiguredEnemySlot(arenaId, WorldPosition);
            if (configured >= 0)
            {
                return configured;
            }

            var used = new HashSet<int>();
            foreach (ArenaMarkerBehaviour marker in UnityEngine.Object
                .FindObjectsByType<ArenaMarkerBehaviour>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (marker == null || ReferenceEquals(marker, this) ||
                    marker.MarkerKind != ArenaMarkerKind.EnemySpawn ||
                    !string.Equals(ArenaWorldObjects.ResolveArenaId(marker.WorldPosition, marker.ArenaId), arenaId,
                        StringComparison.Ordinal))
                {
                    continue;
                }
                int slot = marker.MarkerSlot;
                if (slot >= 0 && slot < 4)
                {
                    used.Add(slot);
                }
            }
            for (int slot = 0; slot < 4; slot++)
            {
                if (!used.Contains(slot))
                {
                    return slot;
                }
            }
            return ArenaWorldObjects.MarkerRequestRejected;
        }

        private void ApplyAdminVisibility()
        {
            bool visible = ArenaWorldObjects.ShouldShowAdminSetupVisuals();
            foreach (Renderer renderer in _renderers)
            {
                if (renderer != null)
                {
                    renderer.enabled = visible;
                }
            }
            if (!_appearanceApplied)
            {
                Color color = ArenaWorldObjects.MarkerColor(MarkerKind);
                foreach (Renderer renderer in _renderers)
                {
                    if (renderer == null)
                    {
                        continue;
                    }
                    Material material = renderer.sharedMaterial;
                    _appearanceProperties.Clear();
                    renderer.GetPropertyBlock(_appearanceProperties);
                    if (material != null && material.HasProperty("_MainTex"))
                        _appearanceProperties.SetTexture("_MainTex", Texture2D.whiteTexture);
                    if (material != null && material.HasProperty("_BaseMap"))
                        _appearanceProperties.SetTexture("_BaseMap", Texture2D.whiteTexture);
                    if (material != null && material.HasProperty("_Color"))
                        _appearanceProperties.SetColor("_Color", color);
                    if (material != null && material.HasProperty("_BaseColor"))
                        _appearanceProperties.SetColor("_BaseColor", color);
                    if (material != null && material.HasProperty("_EmissionColor"))
                        _appearanceProperties.SetColor("_EmissionColor", color * 1.4f);
                    renderer.SetPropertyBlock(_appearanceProperties);
                }
                _appearanceApplied = true;
            }
            if (_selectionCollider != null)
            {
                _selectionCollider.isTrigger = true;
                _selectionCollider.enabled = visible;
            }
        }

        internal void RefreshAdminVisibility()
        {
            ApplyAdminVisibility();
        }

        public string GetHoverName()
        {
            if (!ArenaWorldObjects.IsLocalAdmin())
            {
                return string.Empty;
            }
            int slot = MarkerSlot;
            if (MarkerKind == ArenaMarkerKind.EnemySpawn && (slot < 0 || slot > 3))
            {
                slot = ArenaWorldObjects.ResolveConfiguredEnemySlot(
                    ArenaWorldObjects.ResolveArenaId(WorldPosition, ArenaId),
                    WorldPosition);
            }
            return MarkerKind == ArenaMarkerKind.EnemySpawn && slot >= 0
                ? "Enemy Spawn " + (slot + 1)
                : MarkerKind == ArenaMarkerKind.CombatantStart
                    ? "Combat Start"
                    : MarkerKind + " Position";
        }

        public string GetHoverText()
        {
            return ArenaWorldObjects.IsLocalAdmin()
                ? GetHoverName() + "\nRemove with the Arena Admin Hammer"
                : string.Empty;
        }

        public void OnRemoved()
        {
            if (!ArenaWorldObjects.IsLocalAdmin())
            {
                return;
            }
            string arenaId = ArenaWorldObjects.ResolveArenaId(WorldPosition, ArenaId);
            int slot = MarkerKind == ArenaMarkerKind.EnemySpawn ? MarkerSlot : -1;
            if (MarkerKind == ArenaMarkerKind.EnemySpawn && (slot < 0 || slot > 3))
            {
                slot = ArenaWorldObjects.ResolveConfiguredEnemySlot(arenaId, WorldPosition);
            }
            if (MarkerKind != ArenaMarkerKind.EnemySpawn || (slot >= 0 && slot < 4))
            {
                ArenaWorldObjects.RemoveAdminMarker(arenaId, MarkerKind, WorldPosition, slot);
            }
        }

        public bool Interact(Humanoid user, bool hold, bool alt) => false;
        public bool UseItem(Humanoid user, ItemDrop.ItemData item) => false;
    }
}
