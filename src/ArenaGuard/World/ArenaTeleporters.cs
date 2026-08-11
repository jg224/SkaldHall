using System;
using ArenaGuard.Domain;
using UnityEngine;

namespace ArenaGuard.World
{
    public sealed class ArenaGateDestination
    {
        public string GateId;
        public string ArenaId;
        public Vector3 Position;
        public Quaternion Rotation;
        public float ExitDistance;
    }

    /// <summary>
    /// Many-to-one arena portal routing. Networked callers should bind
    /// GateTravelRequested; the direct resolvers are retained for an authoritative
    /// host/single-player composition and deterministic smoke tests.
    /// </summary>
    public static class ArenaTeleporters
    {
        internal const string ZdoIsHub = "arenaguard.is_hub_gate";
        internal const string ZdoIsFallback = "arenaguard.is_fallback_gate";

        public static Func<Player, string, bool, bool> GateTravelRequested;
        public static Action<string, string, string, bool> GateConfigurationRequested;

        public static Func<string, ArenaGateDestination> EntranceDestinationResolver;
        public static Func<long, ArenaGateDestination> ReturnDestinationResolver;
        public static Func<PlayerArenaRoute, bool> RouteRecorder;
        public static Action<long> RouteRollback;
        public static Action<long> SuccessfulReturn;

        public static ArenaGateDestination DestinationFromGate(ArenaGateDefinition gate, float exitDistance = 2f)
        {
            if (gate == null)
            {
                return null;
            }
            return new ArenaGateDestination
            {
                GateId = gate.GateId,
                ArenaId = gate.ArenaId,
                Position = ArenaWorldObjects.ToVector3(gate.Position),
                Rotation = Quaternion.Euler(0f, gate.RotationY, 0f),
                ExitDistance = exitDistance
            };
        }

        /// <summary>Applies a destination only after the server's two-phase gate authorization arrives.</summary>
        public static bool ApplyAuthorizedGateTravel(PositionData destination, float rotationY)
        {
            var player = Player.m_localPlayer;
            if (!CanUseVanillaPortal(player) || float.IsNaN(rotationY) || float.IsInfinity(rotationY))
            {
                return false;
            }
            return Teleport(player, new ArenaGateDestination
            {
                Position = ArenaWorldObjects.ToVector3(destination),
                Rotation = Quaternion.Euler(0f, rotationY, 0f),
                ExitDistance = 2f
            });
        }

        public static bool TryEnterGate(Player player, ArenaGateDefinition gate)
        {
            if (gate == null)
            {
                return false;
            }
            return TryEnterGate(player, gate.GateId, gate.ArenaId);
        }

        public static bool TryEnterGate(Player player, string gateId, string arenaId)
        {
            if (!CanUseVanillaPortal(player) || string.IsNullOrWhiteSpace(gateId) || string.IsNullOrWhiteSpace(arenaId))
            {
                return false;
            }

            if (GateTravelRequested != null)
            {
                return GateTravelRequested(player, gateId, false);
            }

            var destination = EntranceDestinationResolver?.Invoke(gateId);
            if (destination == null || RouteRecorder == null)
            {
                Show(player, "$arenaguard_unconfigured");
                return false;
            }

            var route = new PlayerArenaRoute
            {
                PlayerId = player.GetPlayerID(),
                ArenaId = arenaId,
                OriginGateId = gateId,
                EnteredUtc = DateTime.UtcNow
            };

            // Persistence happens before movement so a disconnect during teleport
            // still has a deterministic return gate.
            if (!RouteRecorder(route))
            {
                Show(player, "Arena route could not be saved.");
                return false;
            }

            if (Teleport(player, destination))
            {
                return true;
            }

            RouteRollback?.Invoke(player.GetPlayerID());
            return false;
        }

        public static bool ReturnThroughHubGate(Player player)
        {
            if (!CanUseVanillaPortal(player))
            {
                return false;
            }

            if (GateTravelRequested != null)
            {
                return GateTravelRequested(player, string.Empty, true);
            }

            var destination = ReturnDestinationResolver?.Invoke(player.GetPlayerID());
            if (destination == null)
            {
                Show(player, "Your entrance gate and fallback gate are unavailable.");
                return false;
            }

            if (!Teleport(player, destination))
            {
                return false;
            }

            // The route and queue entry are cleared only after TeleportTo accepts
            // the move, matching the arena recovery contract.
            SuccessfulReturn?.Invoke(player.GetPlayerID());
            return true;
        }

        public static bool CanUseVanillaPortal(Player player)
        {
            if (player == null)
            {
                return false;
            }

            var zones = ZoneSystem.instance;
            if (zones != null && zones.GetGlobalKey(GlobalKeys.NoPortals))
            {
                Show(player, "$msg_blocked");
                return false;
            }

            if (zones != null && zones.GetGlobalKey(GlobalKeys.NoBossPortals))
            {
                float activeBosses;
                var bossEventActive = RandEventSystem.instance != null &&
                                      !string.IsNullOrEmpty(RandEventSystem.instance.GetBossEvent());
                var bossKeyActive = zones.GetGlobalKey(GlobalKeys.activeBosses, out activeBosses) && activeBosses > 0f;
                if (bossEventActive || bossKeyActive)
                {
                    Show(player, "$msg_blockedbyboss");
                    return false;
                }
            }

            if (!player.IsTeleportable())
            {
                Show(player, "$msg_noteleport");
                return false;
            }

            return true;
        }

        public static void ApplyGateConfiguration(string objectId, string arenaId, string displayName, bool isFallback)
        {
            if (string.IsNullOrWhiteSpace(objectId))
            {
                return;
            }

            foreach (var gate in UnityEngine.Object.FindObjectsByType<ArenaGateBehaviour>(FindObjectsSortMode.None))
            {
                if (gate.ObjectId == objectId)
                {
                    gate.ApplyOwnedConfiguration(arenaId, displayName, isFallback);
                    return;
                }
            }
        }

        private static bool Teleport(Player player, ArenaGateDestination destination)
        {
            var rotation = destination.Rotation;
            var distance = destination.ExitDistance > 0f ? destination.ExitDistance : 2f;
            var exit = destination.Position + rotation * Vector3.forward * distance + Vector3.up;
            return player.TeleportTo(exit, rotation, true);
        }

        private static void Show(Player player, string message)
        {
            player?.Message(MessageHud.MessageType.Center, message, 0, null);
        }
    }

    public sealed class ArenaGateBehaviour : ArenaWorldObjectBehaviour, Hoverable, Interactable, TextReceiver
    {
        public bool IsHubGate;
        public float ActivationRange = 1.5f;
        public float ExitDistance = 2f;
        public Transform ProximityRoot;
        public Color UnconnectedColor = Color.black;
        public Color ConnectedColor = Color.cyan;
        public EffectFade TargetFoundEffect;
        public MeshRenderer Model;
        public EffectList ConnectedEffects;

        private bool _wasConfigured;
        private float _colorAlpha;
        private float _nextStateRefreshTime;
        private float _nextVisualRefreshTime;
        private float _lastVisualRefreshTime;
        private bool _gateColorApplied;
        private readonly MaterialPropertyBlock _modelProperties = new MaterialPropertyBlock();

        protected override void Awake()
        {
            base.Awake();
            if (IsOwner)
            {
                NView.GetZDO().Set(ArenaTeleporters.ZdoIsHub, IsHubGate);
            }
            else if (NView != null && NView.IsValid())
            {
                IsHubGate = NView.GetZDO().GetBool(ArenaTeleporters.ZdoIsHub, IsHubGate);
            }
        }

        public string GetHoverName()
        {
            var displayName = Read(ArenaWorldObjects.ZdoDisplayName);
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                return displayName;
            }
            return IsHubGate ? "$arenaguard_hub_gate" : "$arenaguard_entrance_gate";
        }

        public string GetHoverText()
        {
            var action = IsHubGate ? "Return through gate" : "Enter arena";
            var useKey = ArenaWorldObjects.UseKeyLabel();
            var text = ArenaWorldObjects.Localize(GetHoverName()) + "\n[<color=yellow><b>" + useKey +
                       "</b></color>] " + action;
            if (ArenaWorldObjects.IsLocalAdmin())
            {
                text += "\n[<color=yellow><b>Shift + " + useKey + "</b></color>] Rename";
            }
            return text;
        }

        public bool Interact(Humanoid user, bool hold, bool alt)
        {
            if (hold || user != Player.m_localPlayer)
            {
                return false;
            }

            if (alt && ArenaWorldObjects.IsLocalAdmin())
            {
                var arenaId = ArenaWorldObjects.ResolveArenaId(transform.position, ArenaId);
                var fallback = NView != null && NView.IsValid() &&
                               NView.GetZDO().GetBool(ArenaTeleporters.ZdoIsFallback, false);
                ArenaGuard.UI.ArenaUi.OpenGateAdminPanel(ObjectId, arenaId, GetText(), IsHubGate, fallback);
                return true;
            }

            var player = user as Player;
            if (player == null)
            {
                return false;
            }

            return Activate(player);
        }

        public bool Activate(Player player)
        {
            if (player == null || player != Player.m_localPlayer || player.IsTeleporting())
            {
                return false;
            }

            if (IsHubGate)
            {
                return ArenaTeleporters.ReturnThroughHubGate(player);
            }

            var arenaId = ArenaWorldObjects.ResolveArenaId(transform.position, ArenaId);
            return ArenaTeleporters.TryEnterGate(player, ObjectId, arenaId);
        }

        private void Update()
        {
            float now = Time.unscaledTime;
            if (now >= _nextStateRefreshTime)
            {
                _nextStateRefreshTime = now + 0.2f;
                bool configured = IsHubGate || !string.IsNullOrWhiteSpace(ArenaId);
                if (configured && !_wasConfigured && ConnectedEffects != null && ConnectedEffects.HasEffects())
                {
                    ConnectedEffects.Create(transform.position, transform.rotation, null, 1f, -1);
                }
                _wasConfigured = configured;

                if (TargetFoundEffect != null)
                {
                    Player local = Player.m_localPlayer;
                    bool inRange = false;
                    if (local != null && ProximityRoot != null)
                    {
                        Vector3 offset = local.transform.position - ProximityRoot.position;
                        inRange = offset.sqrMagnitude <= ActivationRange * ActivationRange &&
                                  local.IsTeleportable();
                    }
                    TargetFoundEffect.SetActive(configured && inRange);
                }
            }

            float targetAlpha = _wasConfigured ? 1f : 0f;
            if (now < _nextVisualRefreshTime ||
                _gateColorApplied && Mathf.Approximately(_colorAlpha, targetAlpha))
            {
                return;
            }

            _nextVisualRefreshTime = now + 0.05f;
            float elapsed = _lastVisualRefreshTime <= 0f ? 0.05f : now - _lastVisualRefreshTime;
            _lastVisualRefreshTime = now;
            _colorAlpha = Mathf.MoveTowards(_colorAlpha, targetAlpha, elapsed);
            ApplyGateColor(Color.Lerp(UnconnectedColor, ConnectedColor, _colorAlpha));
        }

        private void ApplyGateColor(Color color)
        {
            if (Model == null)
            {
                return;
            }

            _modelProperties.Clear();
            Model.GetPropertyBlock(_modelProperties);
            Material material = Model.sharedMaterial;
            if (material != null && material.HasProperty("_EmissionColor"))
            {
                _modelProperties.SetColor("_EmissionColor", color);
            }
            Model.SetPropertyBlock(_modelProperties);
            _gateColorApplied = true;
        }

        public bool UseItem(Humanoid user, ItemDrop.ItemData item) => false;

        public string GetText()
        {
            return Read(ArenaWorldObjects.ZdoDisplayName);
        }

        public void SetText(string text)
        {
            if (!ArenaWorldObjects.IsLocalAdmin())
            {
                return;
            }

            var clean = (text ?? string.Empty).Trim();
            if (clean.Length == 0 || clean.Length > 48)
            {
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Gate names must be 1-48 characters.", 0, null);
                return;
            }

            var arenaId = ArenaWorldObjects.ResolveArenaId(transform.position, ArenaId);
            var isFallback = NView != null && NView.IsValid() &&
                             NView.GetZDO().GetBool(ArenaTeleporters.ZdoIsFallback, false);
            if (ArenaTeleporters.GateConfigurationRequested != null)
            {
                ArenaTeleporters.GateConfigurationRequested(ObjectId, arenaId, clean, isFallback);
            }
        }

        public void ApplyOwnedConfiguration(string arenaId, string displayName, bool isFallback)
        {
            if (!IsOwner)
            {
                return;
            }
            WriteOwned(ArenaWorldObjects.ZdoArenaId, arenaId);
            WriteOwned(ArenaWorldObjects.ZdoDisplayName, displayName);
            NView.GetZDO().Set(ArenaTeleporters.ZdoIsFallback, isFallback);
        }
    }

    public sealed class ArenaGateTrigger : MonoBehaviour
    {
        private ArenaGateBehaviour _gate;

        private void Awake()
        {
            _gate = GetComponentInParent<ArenaGateBehaviour>();
        }

        private void OnTriggerEnter(Collider colliderIn)
        {
            var player = colliderIn != null ? colliderIn.GetComponent<Player>() : null;
            if (player == null || player != Player.m_localPlayer)
            {
                return;
            }
            if (_gate == null)
            {
                _gate = GetComponentInParent<ArenaGateBehaviour>();
            }
            _gate?.Activate(player);
        }
    }
}
