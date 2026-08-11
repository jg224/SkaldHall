using System;
using System.Collections.Generic;
using System.Linq;
using ArenaGuard.Domain;

namespace ArenaGuard.Arenas
{
    /// <summary>
    /// Server-owned indexes for arena definitions and transient routing/session state.
    /// Values crossing this boundary are copied so callers cannot mutate an index in place.
    /// </summary>
    internal static class ArenaRegistry
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, ArenaDefinition> Arenas =
            new Dictionary<string, ArenaDefinition>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> ArenaNames =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, ArenaGateDefinition> Gates =
            new Dictionary<string, ArenaGateDefinition>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> GateNames =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<long, PlayerArenaRoute> Routes =
            new Dictionary<long, PlayerArenaRoute>();
        private static readonly Dictionary<string, List<QueueEntry>> Queues =
            new Dictionary<string, List<QueueEntry>>(StringComparer.Ordinal);
        private static readonly Dictionary<string, ArenaSession> ActiveSessions =
            new Dictionary<string, ArenaSession>(StringComparer.Ordinal);

        private const float UnsetMarkerCoordinate = float.MaxValue;

        internal static bool RegisterArena(ArenaDefinition definition)
        {
            ArenaDefinition candidate = NormalizeAndCopyArena(definition, true);
            lock (Sync)
            {
                if (ArenaNames.TryGetValue(candidate.NormalizedName, out string namedArenaId) &&
                    !string.Equals(namedArenaId, candidate.ArenaId, StringComparison.Ordinal))
                    return false;

                if (Arenas.TryGetValue(candidate.ArenaId, out ArenaDefinition existing))
                {
                    ArenaNames.Remove(existing.NormalizedName);
                    candidate.Revision = Math.Max(existing.Revision + 1L, candidate.Revision);
                }
                else
                {
                    candidate.Revision = Math.Max(1L, candidate.Revision);
                }

                Arenas[candidate.ArenaId] = candidate;
                ArenaNames[candidate.NormalizedName] = candidate.ArenaId;
                if (!Queues.ContainsKey(candidate.ArenaId)) Queues[candidate.ArenaId] = new List<QueueEntry>();
                return true;
            }
        }

        internal static bool RemoveArena(string arenaId)
        {
            arenaId = NormalizeId(arenaId);
            if (arenaId == null) return false;

            lock (Sync)
            {
                if (!Arenas.TryGetValue(arenaId, out ArenaDefinition arena)) return false;
                Arenas.Remove(arenaId);
                ArenaNames.Remove(arena.NormalizedName);

                foreach (string gateId in Gates.Values
                    .Where(gate => string.Equals(gate.ArenaId, arenaId, StringComparison.Ordinal))
                    .Select(gate => gate.GateId)
                    .ToList())
                {
                    GateNames.Remove(Gates[gateId].NormalizedName);
                    Gates.Remove(gateId);
                }

                foreach (long playerId in Routes.Values
                    .Where(route => string.Equals(route.ArenaId, arenaId, StringComparison.Ordinal))
                    .Select(route => route.PlayerId)
                    .ToList())
                    Routes.Remove(playerId);

                Queues.Remove(arenaId);
                ActiveSessions.Remove(arenaId);
                return true;
            }
        }

        internal static bool TryGetArena(string arenaId, out ArenaDefinition definition)
        {
            arenaId = NormalizeId(arenaId);
            lock (Sync)
            {
                if (arenaId != null && Arenas.TryGetValue(arenaId, out ArenaDefinition stored))
                {
                    definition = CopyArena(stored);
                    return true;
                }
            }

            definition = null;
            return false;
        }

        internal static bool TryGetArenaByName(string displayName, out ArenaDefinition definition)
        {
            string normalizedName = NormalizeName(displayName);
            lock (Sync)
            {
                if (normalizedName != null && ArenaNames.TryGetValue(normalizedName, out string arenaId) &&
                    Arenas.TryGetValue(arenaId, out ArenaDefinition stored))
                {
                    definition = CopyArena(stored);
                    return true;
                }
            }

            definition = null;
            return false;
        }

        internal static ArenaDefinition FindProtectedArena(PositionData position)
        {
            lock (Sync)
            {
                ArenaDefinition match = FindContainingArena(position, false);
                return match == null ? null : CopyArena(match);
            }
        }

        internal static ArenaDefinition FindCombatArena(PositionData position)
        {
            lock (Sync)
            {
                ArenaDefinition match = FindContainingArena(position, true);
                return match == null ? null : CopyArena(match);
            }
        }

        internal static bool SetMarkers(string arenaId, ArenaMarkerSet markers)
        {
            arenaId = NormalizeId(arenaId);
            if (arenaId == null || !MarkersAreComplete(markers)) return false;

            lock (Sync)
            {
                if (!Arenas.TryGetValue(arenaId, out ArenaDefinition arena)) return false;
                if (!IsWithinCombatRadius(arena, markers.CombatantStartPosition)) return false;
                arena.Markers = CopyMarkers(markers);
                arena.Revision = Math.Max(1L, arena.Revision + 1L);
                return true;
            }
        }

        internal static bool SetMarker(string arenaId, ArenaMarkerKind kind, int slot, PositionData position)
        {
            arenaId = NormalizeId(arenaId);
            if (arenaId == null || !IsFinite(position) || IsUnsetMarker(position) ||
                !MarkerSlotIsValid(kind, slot))
                return false;

            lock (Sync)
            {
                if (!Arenas.TryGetValue(arenaId, out ArenaDefinition arena)) return false;
                if (kind == ArenaMarkerKind.CombatantStart && !IsWithinCombatRadius(arena, position))
                    return false;
                ArenaMarkerSet markers = CopyOrCreatePartialMarkers(arena.Markers);
                PositionData existing;
                switch (kind)
                {
                    case ArenaMarkerKind.Staging:
                        existing = markers.StagingPosition;
                        markers.StagingPosition = position;
                        break;
                    case ArenaMarkerKind.CombatantStart:
                        existing = markers.CombatantStartPosition;
                        markers.CombatantStartPosition = position;
                        break;
                    case ArenaMarkerKind.HubGate:
                        existing = markers.HubGatePosition;
                        markers.HubGatePosition = position;
                        break;
                    case ArenaMarkerKind.EnemySpawn:
                        existing = markers.EnemySpawnPositions[slot];
                        markers.EnemySpawnPositions[slot] = position;
                        break;
                    default:
                        return false;
                }

                if (SamePosition(existing, position)) return true;

                arena.Markers = markers;
                // Marker edits are configuration changes. An administrator must
                // explicitly re-enable the arena after confirming the new layout.
                if (kind != ArenaMarkerKind.HubGate) arena.Enabled = false;
                arena.Revision = Math.Max(1L, arena.Revision + 1L);
                return true;
            }
        }

        internal static bool RemoveMarker(string arenaId, ArenaMarkerKind kind, int slot)
        {
            arenaId = NormalizeId(arenaId);
            if (arenaId == null || !MarkerSlotIsValid(kind, slot)) return false;

            lock (Sync)
            {
                if (!Arenas.TryGetValue(arenaId, out ArenaDefinition arena)) return false;
                ArenaMarkerSet markers = CopyOrCreatePartialMarkers(arena.Markers);
                PositionData unset = UnsetMarkerPosition();
                PositionData existing;
                switch (kind)
                {
                    case ArenaMarkerKind.Staging:
                        existing = markers.StagingPosition;
                        markers.StagingPosition = unset;
                        break;
                    case ArenaMarkerKind.CombatantStart:
                        existing = markers.CombatantStartPosition;
                        markers.CombatantStartPosition = unset;
                        break;
                    case ArenaMarkerKind.HubGate:
                        existing = markers.HubGatePosition;
                        markers.HubGatePosition = unset;
                        break;
                    case ArenaMarkerKind.EnemySpawn:
                        existing = markers.EnemySpawnPositions[slot];
                        markers.EnemySpawnPositions[slot] = unset;
                        break;
                    default:
                        return false;
                }

                if (IsUnsetMarker(existing)) return true;

                arena.Markers = markers;
                if (kind != ArenaMarkerKind.HubGate) arena.Enabled = false;
                arena.Revision = Math.Max(1L, arena.Revision + 1L);
                return true;
            }
        }

        internal static int NextAvailableEnemyMarkerSlot(string arenaId)
        {
            arenaId = NormalizeId(arenaId);
            lock (Sync)
            {
                if (arenaId == null || !Arenas.TryGetValue(arenaId, out ArenaDefinition arena)) return -1;
                ArenaMarkerSet markers = CopyOrCreatePartialMarkers(arena.Markers);
                for (int index = 0; index < markers.EnemySpawnPositions.Count; index++)
                {
                    if (IsUnsetMarker(markers.EnemySpawnPositions[index])) return index;
                }
                return -1;
            }
        }

        internal static bool TryGetMarkerPosition(
            string arenaId,
            ArenaMarkerKind kind,
            int slot,
            out PositionData position)
        {
            arenaId = NormalizeId(arenaId);
            lock (Sync)
            {
                if (arenaId == null || !MarkerSlotIsValid(kind, slot) ||
                    !Arenas.TryGetValue(arenaId, out ArenaDefinition arena) || arena.Markers == null)
                {
                    position = default(PositionData);
                    return false;
                }

                ArenaMarkerSet markers = CopyOrCreatePartialMarkers(arena.Markers);
                switch (kind)
                {
                    case ArenaMarkerKind.Staging:
                        position = markers.StagingPosition;
                        break;
                    case ArenaMarkerKind.CombatantStart:
                        position = markers.CombatantStartPosition;
                        break;
                    case ArenaMarkerKind.HubGate:
                        position = markers.HubGatePosition;
                        break;
                    case ArenaMarkerKind.EnemySpawn:
                        position = markers.EnemySpawnPositions[slot];
                        break;
                    default:
                        position = default(PositionData);
                        return false;
                }
                return MarkerPositionIsSet(position);
            }
        }

        internal static bool MarkerPositionIsSet(PositionData position)
        {
            return IsFinite(position) && !IsUnsetMarker(position);
        }

        internal static bool IsWithinCombatRadius(ArenaDefinition arena, PositionData position)
        {
            return arena != null && IsFinite(position) && !IsUnsetMarker(position) &&
                   IsFinitePositive(arena.CombatRadius) && Contains(arena, position, arena.CombatRadius);
        }

        internal static PositionData UnsetMarkerPosition()
        {
            return new PositionData
            {
                X = UnsetMarkerCoordinate,
                Y = UnsetMarkerCoordinate,
                Z = UnsetMarkerCoordinate
            };
        }

        internal static bool RegisterGate(ArenaGateDefinition definition)
        {
            ArenaGateDefinition candidate = NormalizeAndCopyGate(definition);
            lock (Sync)
            {
                if (!Arenas.ContainsKey(candidate.ArenaId)) return false;
                if (GateNames.TryGetValue(candidate.NormalizedName, out string namedGateId) &&
                    !string.Equals(namedGateId, candidate.GateId, StringComparison.Ordinal))
                    return false;

                if (Gates.TryGetValue(candidate.GateId, out ArenaGateDefinition existing))
                    GateNames.Remove(existing.NormalizedName);

                // Exactly one fallback is retained per arena. Setting a new one clears the old flag.
                if (candidate.IsFallbackEntrance)
                {
                    foreach (ArenaGateDefinition gate in Gates.Values.Where(gate =>
                        string.Equals(gate.ArenaId, candidate.ArenaId, StringComparison.Ordinal)))
                        gate.IsFallbackEntrance = false;
                }

                Gates[candidate.GateId] = candidate;
                GateNames[candidate.NormalizedName] = candidate.GateId;
                return true;
            }
        }

        internal static bool RemoveGate(string gateId)
        {
            gateId = NormalizeId(gateId);
            if (gateId == null) return false;
            lock (Sync)
            {
                if (!Gates.TryGetValue(gateId, out ArenaGateDefinition gate)) return false;
                Gates.Remove(gateId);
                GateNames.Remove(gate.NormalizedName);
                return true;
            }
        }

        internal static bool TryGetGate(string gateId, out ArenaGateDefinition definition)
        {
            gateId = NormalizeId(gateId);
            lock (Sync)
            {
                if (gateId != null && Gates.TryGetValue(gateId, out ArenaGateDefinition stored))
                {
                    definition = CopyGate(stored);
                    return true;
                }
            }

            definition = null;
            return false;
        }

        internal static bool TryGetGateByName(string displayName, out ArenaGateDefinition definition)
        {
            string normalizedName = NormalizeName(displayName);
            lock (Sync)
            {
                if (normalizedName != null && GateNames.TryGetValue(normalizedName, out string gateId) &&
                    Gates.TryGetValue(gateId, out ArenaGateDefinition stored))
                {
                    definition = CopyGate(stored);
                    return true;
                }
            }

            definition = null;
            return false;
        }

        internal static bool RecordRoute(PlayerArenaRoute route)
        {
            if (route == null || route.PlayerId <= 0) return false;
            string arenaId = NormalizeId(route.ArenaId);
            string gateId = NormalizeId(route.OriginGateId);
            if (arenaId == null || gateId == null) return false;

            lock (Sync)
            {
                if (!Arenas.ContainsKey(arenaId) || !Gates.TryGetValue(gateId, out ArenaGateDefinition gate) ||
                    !string.Equals(gate.ArenaId, arenaId, StringComparison.Ordinal))
                    return false;

                Routes[route.PlayerId] = new PlayerArenaRoute
                {
                    PlayerId = route.PlayerId,
                    ArenaId = arenaId,
                    OriginGateId = gateId,
                    EnteredUtc = AsUtc(route.EnteredUtc == default(DateTime) ? DateTime.UtcNow : route.EnteredUtc)
                };
                return true;
            }
        }

        internal static ArenaGateDefinition ResolveReturnGate(long playerId)
        {
            if (playerId <= 0) return null;
            lock (Sync)
            {
                if (!Routes.TryGetValue(playerId, out PlayerArenaRoute route)) return null;
                if (Gates.TryGetValue(route.OriginGateId, out ArenaGateDefinition origin) &&
                    string.Equals(origin.ArenaId, route.ArenaId, StringComparison.Ordinal))
                    return CopyGate(origin);

                ArenaGateDefinition fallback = Gates.Values
                    .Where(gate => string.Equals(gate.ArenaId, route.ArenaId, StringComparison.Ordinal) &&
                                   gate.IsFallbackEntrance)
                    .OrderBy(gate => gate.NormalizedName, StringComparer.Ordinal)
                    .ThenBy(gate => gate.GateId, StringComparer.Ordinal)
                    .FirstOrDefault();
                return fallback == null ? null : CopyGate(fallback);
            }
        }

        internal static bool ClearRoute(long playerId)
        {
            lock (Sync) return Routes.Remove(playerId);
        }

        internal static bool TryGetRoute(long playerId, out PlayerArenaRoute route)
        {
            lock (Sync)
            {
                if (playerId > 0 && Routes.TryGetValue(playerId, out PlayerArenaRoute stored))
                {
                    route = CopyRoute(stored);
                    return true;
                }
            }
            route = null;
            return false;
        }

        internal static void ReplaceQueue(IEnumerable<QueueEntry> entries)
        {
            lock (Sync)
            {
                Queues.Clear();
                foreach (string arenaId in Arenas.Keys) Queues[arenaId] = new List<QueueEntry>();
                if (entries == null) return;

                foreach (QueueEntry entry in entries.Where(IsValidQueueEntry)
                    .OrderBy(entry => entry.QueueSequence)
                    .ThenBy(entry => entry.EnqueuedUtc))
                {
                    string arenaId = NormalizeId(entry.Request.ArenaId);
                    if (!Arenas.ContainsKey(arenaId) || !QueueOriginIsValid(entry, arenaId)) continue;
                    if (!Queues.TryGetValue(arenaId, out List<QueueEntry> queue))
                        Queues[arenaId] = queue = new List<QueueEntry>();
                    if (queue.Any(current => current.Request.PlayerId == entry.Request.PlayerId)) continue;
                    queue.Add(CopyQueueEntry(entry));
                }
            }
        }

        internal static List<QueueEntry> GetQueueSnapshot(string arenaId = null)
        {
            arenaId = string.IsNullOrWhiteSpace(arenaId) ? null : NormalizeId(arenaId);
            lock (Sync)
            {
                IEnumerable<QueueEntry> entries = arenaId == null
                    ? Queues.Values.SelectMany(queue => queue)
                    : Queues.TryGetValue(arenaId, out List<QueueEntry> queue)
                        ? queue
                        : Enumerable.Empty<QueueEntry>();
                return entries.OrderBy(entry => entry.QueueSequence)
                    .ThenBy(entry => entry.EnqueuedUtc)
                    .Select(CopyQueueEntry)
                    .ToList();
            }
        }

        internal static void SetActiveSession(ArenaSession session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            string arenaId = RequireId(session.ArenaId, nameof(session.ArenaId));
            lock (Sync)
            {
                if (!Arenas.ContainsKey(arenaId))
                    throw new InvalidOperationException($"Unknown arena '{arenaId}'.");
                ActiveSessions[arenaId] = CopySession(session);
            }
        }

        internal static bool TryGetActiveSession(string arenaId, out ArenaSession session)
        {
            arenaId = NormalizeId(arenaId);
            lock (Sync)
            {
                if (arenaId != null && ActiveSessions.TryGetValue(arenaId, out ArenaSession stored))
                {
                    session = CopySession(stored);
                    return true;
                }
            }

            session = null;
            return false;
        }

        internal static bool TryGetActiveSessionForPlayer(long playerId, out ArenaSession session)
        {
            lock (Sync)
            {
                ArenaSession stored = ActiveSessions.Values.FirstOrDefault(active =>
                    active?.Request?.PlayerId == playerId);
                if (stored != null)
                {
                    session = CopySession(stored);
                    return true;
                }
            }
            session = null;
            return false;
        }

        internal static bool ClearActiveSession(string arenaId)
        {
            arenaId = NormalizeId(arenaId);
            lock (Sync) return arenaId != null && ActiveSessions.Remove(arenaId);
        }

        internal static List<ArenaDefinition> GetArenaSnapshot()
        {
            lock (Sync)
                return Arenas.Values.OrderBy(arena => arena.NormalizedName, StringComparer.Ordinal)
                    .Select(CopyArena).ToList();
        }

        internal static List<ArenaGateDefinition> GetGateSnapshot()
        {
            lock (Sync)
                return Gates.Values.OrderBy(gate => gate.NormalizedName, StringComparer.Ordinal)
                    .Select(CopyGate).ToList();
        }

        internal static List<ArenaGateDefinition> GetGatesForArena(string arenaId)
        {
            arenaId = NormalizeId(arenaId);
            lock (Sync)
            {
                if (arenaId == null) return new List<ArenaGateDefinition>();
                return Gates.Values.Where(gate => string.Equals(gate.ArenaId, arenaId, StringComparison.Ordinal))
                    .OrderBy(gate => gate.NormalizedName, StringComparer.Ordinal).Select(CopyGate).ToList();
            }
        }

        internal static List<PlayerArenaRoute> GetRouteSnapshot()
        {
            lock (Sync) return Routes.Values.OrderBy(route => route.PlayerId).Select(CopyRoute).ToList();
        }

        internal static void Replace(PersistedWorldState state)
        {
            lock (Sync)
            {
                Arenas.Clear();
                ArenaNames.Clear();
                Gates.Clear();
                GateNames.Clear();
                Routes.Clear();
                Queues.Clear();
                ActiveSessions.Clear();

                if (state == null) return;
                foreach (ArenaDefinition raw in state.Arenas ?? new List<ArenaDefinition>())
                {
                    ArenaDefinition arena = NormalizeAndCopyArena(raw, true);
                    if (Arenas.ContainsKey(arena.ArenaId) || ArenaNames.ContainsKey(arena.NormalizedName))
                        throw new InvalidOperationException("Saved arena IDs and names must be unique.");
                    Arenas.Add(arena.ArenaId, arena);
                    ArenaNames.Add(arena.NormalizedName, arena.ArenaId);
                    Queues.Add(arena.ArenaId, new List<QueueEntry>());
                }

                foreach (ArenaGateDefinition raw in state.Gates ?? new List<ArenaGateDefinition>())
                {
                    ArenaGateDefinition gate = NormalizeAndCopyGate(raw);
                    if (!Arenas.ContainsKey(gate.ArenaId) || Gates.ContainsKey(gate.GateId) ||
                        GateNames.ContainsKey(gate.NormalizedName))
                        throw new InvalidOperationException("Saved gates must target an arena and have unique IDs and names.");
                    Gates.Add(gate.GateId, gate);
                    GateNames.Add(gate.NormalizedName, gate.GateId);
                }

                foreach (PlayerArenaRoute raw in state.Routes ?? new List<PlayerArenaRoute>())
                {
                    if (!RecordRouteWhileLocked(raw))
                        throw new InvalidOperationException("Saved player route does not target a valid arena entrance.");
                }

                foreach (QueueEntry raw in (state.Queue ?? new List<QueueEntry>())
                    .OrderBy(entry => entry.QueueSequence).ThenBy(entry => entry.EnqueuedUtc))
                {
                    if (!IsValidQueueEntry(raw)) throw new InvalidOperationException("Saved queue contains an invalid entry.");
                    string arenaId = NormalizeId(raw.Request.ArenaId);
                    if (!Arenas.ContainsKey(arenaId) || !QueueOriginIsValid(raw, arenaId))
                        throw new InvalidOperationException("Saved queue targets an unknown arena or entrance.");
                    List<QueueEntry> queue = Queues[arenaId];
                    if (queue.Any(entry => entry.Request.PlayerId == raw.Request.PlayerId))
                        throw new InvalidOperationException("A player occurs more than once in an arena queue.");
                    queue.Add(CopyQueueEntry(raw));
                }
            }
        }

        internal static void Clear()
        {
            Replace(null);
        }

        internal static PersistedWorldState Snapshot(long worldUid, long rosterRevision,
            IEnumerable<ArenaSession> interruptedSessions, IEnumerable<LeaderboardEntry> leaderboard)
        {
            lock (Sync)
            {
                return new PersistedWorldState
                {
                    WorldUid = worldUid,
                    RosterRevision = rosterRevision,
                    Arenas = Arenas.Values.OrderBy(arena => arena.NormalizedName, StringComparer.Ordinal)
                        .Select(CopyArena).ToList(),
                    Gates = Gates.Values.OrderBy(gate => gate.NormalizedName, StringComparer.Ordinal)
                        .Select(CopyGate).ToList(),
                    Routes = Routes.Values.OrderBy(route => route.PlayerId).Select(CopyRoute).ToList(),
                    Queue = Queues.Values.SelectMany(queue => queue).OrderBy(entry => entry.QueueSequence)
                        .ThenBy(entry => entry.EnqueuedUtc).Select(CopyQueueEntry).ToList(),
                    InterruptedSessions = (interruptedSessions ?? Enumerable.Empty<ArenaSession>())
                        .Select(CopySession).ToList(),
                    Leaderboard = (leaderboard ?? Enumerable.Empty<LeaderboardEntry>())
                        .Select(CopyLeaderboardEntry).ToList()
                };
            }
        }

        internal static string NormalizeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string trimmed = value.Trim();
            if (trimmed.Length > 64) throw new ArgumentException("Arena and gate names are limited to 64 characters.");
            return trimmed.ToLowerInvariant();
        }

        internal static string NormalizeId(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || !Guid.TryParse(value.Trim(), out Guid id)) return null;
            return id.ToString("N").ToLowerInvariant();
        }

        private static ArenaDefinition FindContainingArena(PositionData position, bool combat)
        {
            return Arenas.Values
                .Where(arena => (!combat || arena.Enabled) &&
                                Contains(arena, position, combat ? arena.CombatRadius : arena.ProtectedRadius))
                .OrderBy(arena => DistanceSquaredXZ(arena.CorePosition, position))
                .ThenBy(arena => arena.ArenaId, StringComparer.Ordinal)
                .FirstOrDefault();
        }

        private static bool Contains(ArenaDefinition arena, PositionData position, float radius)
        {
            return DistanceSquaredXZ(arena.CorePosition, position) <= radius * radius;
        }

        private static float DistanceSquaredXZ(PositionData first, PositionData second)
        {
            float dx = first.X - second.X;
            float dz = first.Z - second.Z;
            return dx * dx + dz * dz;
        }

        private static ArenaDefinition NormalizeAndCopyArena(ArenaDefinition source, bool validateEnabled)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            string arenaId = RequireId(source.ArenaId, nameof(source.ArenaId));
            string normalizedName = NormalizeName(source.DisplayName);
            if (normalizedName == null) throw new ArgumentException("Arena name is required.", nameof(source));
            if (!IsFinitePositive(source.CombatRadius) || !IsFinitePositive(source.ProtectedRadius) ||
                source.ProtectedRadius <= source.CombatRadius)
                throw new ArgumentException("ProtectedRadius must be finite and greater than CombatRadius.", nameof(source));
            if (!IsFinite(source.CorePosition)) throw new ArgumentException("Arena position must be finite.", nameof(source));
            if (validateEnabled && source.Enabled && !MarkersAreComplete(source.Markers))
                throw new ArgumentException("The arena cannot be enabled. Missing: " +
                                            string.Join(", ", MissingRequiredMarkers(source.Markers)) +
                                            ". Gates are optional.", nameof(source));

            return new ArenaDefinition
            {
                ArenaId = arenaId,
                DisplayName = source.DisplayName.Trim(),
                NormalizedName = normalizedName,
                CorePosition = source.CorePosition,
                CombatRadius = source.CombatRadius,
                ProtectedRadius = source.ProtectedRadius,
                Markers = CopyMarkers(source.Markers),
                Enabled = source.Enabled,
                Revision = source.Revision
            };
        }

        private static ArenaGateDefinition NormalizeAndCopyGate(ArenaGateDefinition source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            string gateId = RequireId(source.GateId, nameof(source.GateId));
            string arenaId = RequireId(source.ArenaId, nameof(source.ArenaId));
            string normalizedName = NormalizeName(source.DisplayName);
            if (normalizedName == null) throw new ArgumentException("Gate name is required.", nameof(source));
            if (!IsFinite(source.Position) || !IsFinite(source.RotationY))
                throw new ArgumentException("Gate position and rotation must be finite.", nameof(source));
            return new ArenaGateDefinition
            {
                GateId = gateId,
                ArenaId = arenaId,
                DisplayName = source.DisplayName.Trim(),
                NormalizedName = normalizedName,
                Position = source.Position,
                RotationY = source.RotationY,
                IsFallbackEntrance = source.IsFallbackEntrance
            };
        }

        internal static bool MarkersAreComplete(ArenaMarkerSet markers)
        {
            return markers != null && markers.EnemySpawnPositions != null && markers.EnemySpawnPositions.Count == 4 &&
                   MarkerPositionIsSet(markers.StagingPosition) && MarkerPositionIsSet(markers.CombatantStartPosition) &&
                   markers.EnemySpawnPositions.All(MarkerPositionIsSet);
        }

        internal static List<string> MissingRequiredMarkers(ArenaMarkerSet markers)
        {
            var missing = new List<string>();
            if (markers == null || !MarkerPositionIsSet(markers.StagingPosition))
                missing.Add("Arena Master staging position");
            if (markers == null || !MarkerPositionIsSet(markers.CombatantStartPosition))
                missing.Add("Combat Start");
            for (int slot = 0; slot < 4; slot++)
            {
                if (markers?.EnemySpawnPositions == null || markers.EnemySpawnPositions.Count <= slot ||
                    !MarkerPositionIsSet(markers.EnemySpawnPositions[slot]))
                    missing.Add("Enemy Spawn " + (slot + 1));
            }
            return missing;
        }

        private static ArenaMarkerSet CopyOrCreatePartialMarkers(ArenaMarkerSet source)
        {
            PositionData unset = UnsetMarkerPosition();
            var markers = new ArenaMarkerSet
            {
                StagingPosition = source == null ? unset : source.StagingPosition,
                CombatantStartPosition = source == null ? unset : source.CombatantStartPosition,
                HubGatePosition = source == null ? unset : source.HubGatePosition,
                EnemySpawnPositions = source?.EnemySpawnPositions == null
                    ? new List<PositionData>()
                    : new List<PositionData>(source.EnemySpawnPositions)
            };
            while (markers.EnemySpawnPositions.Count < 4) markers.EnemySpawnPositions.Add(unset);
            if (markers.EnemySpawnPositions.Count > 4)
                markers.EnemySpawnPositions.RemoveRange(4, markers.EnemySpawnPositions.Count - 4);
            return markers;
        }

        private static bool MarkerSlotIsValid(ArenaMarkerKind kind, int slot)
        {
            return kind >= ArenaMarkerKind.Staging && kind <= ArenaMarkerKind.HubGate &&
                   (kind == ArenaMarkerKind.EnemySpawn ? slot >= 0 && slot < 4 : slot == -1);
        }

        private static bool IsUnsetMarker(PositionData position)
        {
            return position.X == UnsetMarkerCoordinate && position.Y == UnsetMarkerCoordinate &&
                   position.Z == UnsetMarkerCoordinate;
        }

        private static bool SamePosition(PositionData first, PositionData second)
        {
            return Math.Abs(first.X - second.X) < 0.001f &&
                   Math.Abs(first.Y - second.Y) < 0.001f &&
                   Math.Abs(first.Z - second.Z) < 0.001f;
        }

        private static bool IsFinite(PositionData value)
        {
            return IsFinite(value.X) && IsFinite(value.Y) && IsFinite(value.Z);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool IsFinitePositive(float value)
        {
            return IsFinite(value) && value > 0f;
        }

        private static string RequireId(string value, string parameterName)
        {
            string normalized = NormalizeId(value);
            if (normalized == null) throw new ArgumentException("A lowercase GUID identifier is required.", parameterName);
            return normalized;
        }

        private static bool IsValidQueueEntry(QueueEntry entry)
        {
            return entry != null && entry.Request != null && entry.Request.PlayerId > 0 &&
                   NormalizeId(entry.Request.RequestId) != null && NormalizeId(entry.Request.ArenaId) != null &&
                   entry.QueueSequence >= 0;
        }

        private static bool QueueOriginIsValid(QueueEntry entry, string arenaId)
        {
            if (string.IsNullOrWhiteSpace(entry.OriginGateId)) return true;
            string gateId = NormalizeId(entry.OriginGateId);
            if (gateId == null) return false;
            return !Gates.TryGetValue(gateId, out ArenaGateDefinition gate) ||
                   string.Equals(gate.ArenaId, arenaId, StringComparison.Ordinal);
        }

        private static bool RecordRouteWhileLocked(PlayerArenaRoute route)
        {
            if (route == null || route.PlayerId <= 0) return false;
            string arenaId = NormalizeId(route.ArenaId);
            string gateId = NormalizeId(route.OriginGateId);
            if (arenaId == null || gateId == null || !Arenas.ContainsKey(arenaId)) return false;
            if (Gates.TryGetValue(gateId, out ArenaGateDefinition gate) &&
                !string.Equals(gate.ArenaId, arenaId, StringComparison.Ordinal)) return false;
            Routes[route.PlayerId] = new PlayerArenaRoute
            {
                PlayerId = route.PlayerId,
                ArenaId = arenaId,
                OriginGateId = gateId,
                EnteredUtc = AsUtc(route.EnteredUtc)
            };
            return true;
        }

        private static DateTime AsUtc(DateTime value)
        {
            if (value == default(DateTime)) return DateTime.SpecifyKind(value, DateTimeKind.Utc);
            return value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        }

        private static ArenaDefinition CopyArena(ArenaDefinition source)
        {
            if (source == null) return null;
            return new ArenaDefinition
            {
                ArenaId = source.ArenaId,
                DisplayName = source.DisplayName,
                NormalizedName = source.NormalizedName,
                CorePosition = source.CorePosition,
                CombatRadius = source.CombatRadius,
                ProtectedRadius = source.ProtectedRadius,
                Markers = CopyMarkers(source.Markers),
                Enabled = source.Enabled,
                Revision = source.Revision
            };
        }

        private static ArenaMarkerSet CopyMarkers(ArenaMarkerSet source)
        {
            if (source == null) return null;
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

        private static ArenaGateDefinition CopyGate(ArenaGateDefinition source)
        {
            if (source == null) return null;
            return new ArenaGateDefinition
            {
                GateId = source.GateId,
                ArenaId = source.ArenaId,
                DisplayName = source.DisplayName,
                NormalizedName = source.NormalizedName,
                Position = source.Position,
                RotationY = source.RotationY,
                IsFallbackEntrance = source.IsFallbackEntrance
            };
        }

        private static PlayerArenaRoute CopyRoute(PlayerArenaRoute source)
        {
            return source == null ? null : new PlayerArenaRoute
            {
                PlayerId = source.PlayerId,
                ArenaId = source.ArenaId,
                OriginGateId = source.OriginGateId,
                EnteredUtc = source.EnteredUtc
            };
        }

        private static QueueEntry CopyQueueEntry(QueueEntry source)
        {
            if (source == null) return null;
            return new QueueEntry
            {
                Request = CopyRequest(source.Request),
                OriginGateId = source.OriginGateId,
                QueueSequence = source.QueueSequence,
                EnqueuedUtc = source.EnqueuedUtc
            };
        }

        private static ChallengeRequest CopyRequest(ChallengeRequest source)
        {
            if (source == null) return null;
            return new ChallengeRequest
            {
                RequestId = source.RequestId,
                ArenaId = source.ArenaId,
                PlayerId = source.PlayerId,
                PlayerName = source.PlayerName,
                Mode = source.Mode,
                CapMode = source.CapMode,
                SelectedBiome = source.SelectedBiome,
                RequestedUtc = source.RequestedUtc,
                CustomSelection = source.CustomSelection == null ? null : new CustomEncounterSelection
                {
                    CreatureKey = source.CustomSelection.CreatureKey,
                    Stars = source.CustomSelection.Stars,
                    Quantity = source.CustomSelection.Quantity
                }
            };
        }

        private static ArenaSession CopySession(ArenaSession source)
        {
            if (source == null) return null;
            return new ArenaSession
            {
                SessionId = source.SessionId,
                ArenaId = source.ArenaId,
                Request = CopyRequest(source.Request),
                Plan = CopyPlan(source.Plan),
                Phase = source.Phase,
                Outcome = source.Outcome,
                EncounterIndex = source.EncounterIndex,
                LivingArenaEnemyCount = source.LivingArenaEnemyCount,
                SpawnedEnemyIds = source.SpawnedEnemyIds == null ? new List<string>() : new List<string>(source.SpawnedEnemyIds),
                ResourceSnapshot = CopyResourceSnapshot(source.ResourceSnapshot),
                PhaseStartedUtc = source.PhaseStartedUtc,
                PhaseDeadlineUtc = source.PhaseDeadlineUtc,
                SessionStartedUtc = source.SessionStartedUtc,
                ElapsedMilliseconds = source.ElapsedMilliseconds
            };
        }

        private static ChallengePlan CopyPlan(ChallengePlan source)
        {
            if (source == null) return null;
            return new ChallengePlan
            {
                PlanId = source.PlanId,
                Mode = source.Mode,
                CapMode = source.CapMode,
                HighestBiome = source.HighestBiome,
                RosterRevision = source.RosterRevision,
                Encounters = source.Encounters == null ? new List<EncounterDefinition>() : source.Encounters.Select(CopyEncounter).ToList()
            };
        }

        private static EncounterDefinition CopyEncounter(EncounterDefinition source)
        {
            if (source == null) return null;
            return new EncounterDefinition
            {
                Sequence = source.Sequence,
                CreatureKey = source.CreatureKey,
                Biome = source.Biome,
                Stars = source.Stars,
                Quantity = source.Quantity,
                IsMiniboss = source.IsMiniboss,
                PreparationSeconds = source.PreparationSeconds
            };
        }

        private static PlayerResourceSnapshot CopyResourceSnapshot(PlayerResourceSnapshot source)
        {
            if (source == null) return null;
            return new PlayerResourceSnapshot
            {
                PlayerId = source.PlayerId,
                Foods = source.Foods == null ? new List<FoodStateSnapshot>() : source.Foods.Select(food => new FoodStateSnapshot
                {
                    ItemPrefabName = food.ItemPrefabName,
                    RemainingSeconds = food.RemainingSeconds,
                    Health = food.Health,
                    Stamina = food.Stamina,
                    Eitr = food.Eitr
                }).ToList(),
                RestedRemainingSeconds = source.RestedRemainingSeconds,
                ArenaFoodPrefabNames = source.ArenaFoodPrefabNames == null
                    ? new List<string>()
                    : new List<string>(source.ArenaFoodPrefabNames),
                AllowedConsumableCounts = source.AllowedConsumableCounts == null
                    ? new Dictionary<string, int>()
                    : new Dictionary<string, int>(source.AllowedConsumableCounts, StringComparer.Ordinal),
                AmmunitionCounts = source.AmmunitionCounts == null
                    ? new Dictionary<string, int>()
                    : new Dictionary<string, int>(source.AmmunitionCounts, StringComparer.Ordinal),
                EquipmentDurabilityBySlot = source.EquipmentDurabilityBySlot == null
                    ? new Dictionary<string, float>()
                    : new Dictionary<string, float>(source.EquipmentDurabilityBySlot, StringComparer.Ordinal),
                CapturedUtc = source.CapturedUtc
            };
        }

        private static LeaderboardEntry CopyLeaderboardEntry(LeaderboardEntry source)
        {
            if (source == null) return null;
            return new LeaderboardEntry
            {
                Key = source.Key == null ? null : new LeaderboardKey
                {
                    Mode = source.Key.Mode,
                    CapMode = source.Key.CapMode,
                    SelectedBiome = source.Key.SelectedBiome,
                    CustomCreatureKey = source.Key.CustomCreatureKey,
                    CustomStars = source.Key.CustomStars,
                    CustomQuantity = source.Key.CustomQuantity
                },
                PlayerId = source.PlayerId,
                PlayerName = source.PlayerName,
                Completed = source.Completed,
                FurthestEncounterIndex = source.FurthestEncounterIndex,
                ElapsedMilliseconds = source.ElapsedMilliseconds,
                RosterRevision = source.RosterRevision,
                RecordedUtc = source.RecordedUtc
            };
        }
    }
}
