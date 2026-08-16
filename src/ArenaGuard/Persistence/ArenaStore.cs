using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using ArenaGuard.Arenas;
using ArenaGuard.Domain;
using ArenaGuard.Rules;
using BepInEx;

namespace ArenaGuard.Persistence
{
    /// <summary>Atomic, world-scoped storage for arena state and server-wide records.</summary>
    internal static class ArenaStore
    {
        private const int SchemaVersion = 4;
        private const int ArenaFoodSchemaVersion = 3;
        private const int ScopeSchemaVersion = 2;
        private const int LegacySchemaVersion = 1;
        private const long MaximumSaveBytes = 64L * 1024L * 1024L;
        private const int MaximumCollectionEntries = 100000;
        private const int MaximumStartupBackups = 5;
        private static readonly object Sync = new object();
        private static PersistedWorldState _current = Empty(0);

        internal static long WorldUid
        {
            get { lock (Sync) return _current.WorldUid; }
        }

        internal static PersistedWorldState Load(long worldUid)
        {
            if (worldUid == 0) throw new ArgumentOutOfRangeException(nameof(worldUid), "A non-zero world UID is required.");
            string path = GetPath(worldUid);
            if (!File.Exists(path))
            {
                PersistedWorldState empty = Empty(worldUid);
                lock (Sync) _current = Clone(empty);
                return empty;
            }

            try
            {
                CreateStartupBackup(path);
                var info = new FileInfo(path);
                if (info.Length <= 0 || info.Length > MaximumSaveBytes)
                    throw new SerializationException($"Arena state file length {info.Length} is invalid.");

                StoreFile file;
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    file = (StoreFile)CreateSerializer().ReadObject(stream);
                }

                if (file == null ||
                    (file.SchemaVersion != SchemaVersion && file.SchemaVersion != ArenaFoodSchemaVersion &&
                     file.SchemaVersion != ScopeSchemaVersion &&
                     file.SchemaVersion != LegacySchemaVersion) ||
                    file.State == null ||
                    file.State.WorldUid != worldUid)
                    throw new SerializationException("Arena state schema or world UID does not match.");

                if (file.SchemaVersion == LegacySchemaVersion)
                {
                    MigrateLegacyProgression(file.State);
                    Plugin.Log?.LogInfo("Migrated ArenaGuard world state from progression schema 1 to scope schema 2.");
                }
                if (file.SchemaVersion < ArenaFoodSchemaVersion)
                {
                    InitializeArenaFoodSnapshots(file.State);
                    Plugin.Log?.LogInfo("Migrated ArenaGuard resource snapshots to arena-food schema 3.");
                }
                if (file.SchemaVersion < SchemaVersion)
                {
                    Plugin.Log?.LogInfo("Migrated SkaldHall world state to schema 4; obsolete arena travel fields were discarded.");
                }

                NormalizeAndValidate(file.State, worldUid);
                PersistedWorldState loaded = Clone(file.State);
                lock (Sync) _current = Clone(loaded);
                Plugin.Log?.LogInfo($"Loaded ArenaGuard world state {worldUid}: " +
                                    $"{loaded.Arenas.Count} arena(s), " +
                                    $"{loaded.Queue.Count} queued player(s), {loaded.Leaderboard.Count} record(s).");
                return loaded;
            }
            catch (Exception exception)
            {
                string preserved = PreserveCorruptCopy(path);
                Plugin.Log?.LogError($"Could not load ArenaGuard state '{path}'. The world was opened with empty " +
                                     $"arena state. Preserved copy: '{preserved}'. {exception}");
                PersistedWorldState empty = Empty(worldUid);
                lock (Sync) _current = Clone(empty);
                return empty;
            }
        }

        internal static bool Save(PersistedWorldState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (state.WorldUid == 0) return false;
            PersistedWorldState snapshot = Clone(state);
            NormalizeAndValidate(snapshot, state.WorldUid);

            string path = GetPath(snapshot.WorldUid);
            string directory = Path.GetDirectoryName(path);
            string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            string backup = path + ".bak";
            try
            {
                Directory.CreateDirectory(directory);
                using (FileStream stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    CreateSerializer().WriteObject(stream, new StoreFile
                    {
                        SchemaVersion = SchemaVersion,
                        State = snapshot
                    });
                    stream.Flush(true);
                }

                if (File.Exists(path))
                {
                    // File.Replace is atomic on the supported Windows server filesystem and preserves
                    // the former good file as a backup. Never fall back to an in-place overwrite.
                    File.Replace(temp, path, backup);
                }
                else
                {
                    File.Move(temp, path);
                }

                lock (Sync) _current = Clone(snapshot);
                return true;
            }
            catch (Exception exception)
            {
                Plugin.Log?.LogError($"Could not atomically save ArenaGuard state '{path}': {exception}");
                return false;
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (Exception cleanupException)
                {
                    Plugin.Log?.LogWarning($"Could not remove ArenaGuard temporary state '{temp}': {cleanupException.Message}");
                }
            }
        }

        internal static bool RecordResult(LeaderboardEntry entry)
        {
            LeaderboardEntry candidate = NormalizeLeaderboardEntry(entry);
            PersistedWorldState snapshot;
            lock (Sync)
            {
                if (_current.WorldUid == 0) return false;
                int existingIndex = _current.Leaderboard.FindIndex(current =>
                    current.PlayerId == candidate.PlayerId && KeysEqual(current.Key, candidate.Key));
                if (existingIndex >= 0)
                {
                    LeaderboardEntry existing = _current.Leaderboard[existingIndex];
                    if (!IsBetter(candidate, existing)) return false;
                    _current.Leaderboard[existingIndex] = candidate;
                }
                else
                {
                    _current.Leaderboard.Add(candidate);
                }

                snapshot = Clone(_current);
            }

            return Save(snapshot);
        }

        internal static List<LeaderboardEntry> GetLeaderboard(LeaderboardKey key)
        {
            LeaderboardKey normalizedKey = NormalizeLeaderboardKey(key);
            lock (Sync)
            {
                return _current.Leaderboard.Where(entry => KeysEqual(entry.Key, normalizedKey))
                    .OrderByDescending(entry => entry.Completed)
                    .ThenByDescending(entry => entry.Completed ? int.MinValue : entry.FurthestEncounterIndex)
                    .ThenBy(entry => entry.ElapsedMilliseconds)
                    .ThenBy(entry => entry.RecordedUtc)
                    .ThenBy(entry => entry.PlayerName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(entry => entry.PlayerId)
                    .Select(CopyLeaderboardEntry)
                    .ToList();
            }
        }

        internal static bool MarkInterrupted(ArenaSession session)
        {
            ArenaSession candidate = CopySession(session);
            ValidateInterruptedSession(candidate);
            PersistedWorldState snapshot;
            lock (Sync)
            {
                if (_current.WorldUid == 0) return false;
                _current.InterruptedSessions.RemoveAll(current =>
                    string.Equals(current.SessionId, candidate.SessionId, StringComparison.Ordinal) ||
                    string.Equals(current.ArenaId, candidate.ArenaId, StringComparison.Ordinal) ||
                    current.Request?.PlayerId == candidate.Request?.PlayerId);
                _current.InterruptedSessions.Add(candidate);
                snapshot = Clone(_current);
            }

            return Save(snapshot);
        }

        internal static bool RemoveInterrupted(string sessionId)
        {
            sessionId = ArenaRegistry.NormalizeId(sessionId);
            if (sessionId == null) return false;
            PersistedWorldState snapshot;
            lock (Sync)
            {
                if (_current.WorldUid == 0 || _current.InterruptedSessions.RemoveAll(session =>
                    string.Equals(session.SessionId, sessionId, StringComparison.Ordinal)) == 0) return false;
                snapshot = Clone(_current);
            }

            return Save(snapshot);
        }

        internal static PersistedWorldState Snapshot()
        {
            lock (Sync) return Clone(_current);
        }

        internal static void Clear()
        {
            lock (Sync) _current = Empty(0);
        }

        internal static string GetPath(long worldUid)
        {
            return Path.Combine(Paths.ConfigPath,
                $"{Plugin.PluginGuid}.world-{worldUid.ToString(CultureInfo.InvariantCulture)}.json");
        }

        private static void CreateStartupBackup(string path)
        {
            try
            {
                string directory = Path.GetDirectoryName(path);
                string fileName = Path.GetFileName(path);
                string prefix = fileName + ".startup-";
                string backup = Path.Combine(
                    directory,
                    prefix + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) +
                    "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".bak");
                File.Copy(path, backup, false);

                FileInfo[] backups = new DirectoryInfo(directory)
                    .GetFiles(prefix + "*.bak", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .ThenByDescending(file => file.Name, StringComparer.Ordinal)
                    .ToArray();
                foreach (FileInfo stale in backups.Skip(MaximumStartupBackups))
                {
                    stale.Delete();
                }
            }
            catch (Exception exception)
            {
                // Backups are a safety aid. Failure to create or rotate one must not prevent
                // a valid arena state from loading.
                Plugin.Log?.LogWarning($"Could not create ArenaGuard startup state backup for '{path}': " +
                                       exception.Message);
            }
        }

        private static DataContractJsonSerializer CreateSerializer()
        {
            return new DataContractJsonSerializer(typeof(StoreFile), new DataContractJsonSerializerSettings
            {
                UseSimpleDictionaryFormat = true,
                EmitTypeInformation = EmitTypeInformation.Never,
                MaxItemsInObjectGraph = int.MaxValue
            });
        }

        private static void MigrateLegacyProgression(PersistedWorldState state)
        {
            if (state == null)
            {
                return;
            }

            foreach (QueueEntry entry in state.Queue ?? new List<QueueEntry>())
            {
                MigrateLegacyRequest(entry?.Request);
            }
            foreach (ArenaSession session in state.InterruptedSessions ?? new List<ArenaSession>())
            {
                MigrateLegacyRequest(session?.Request);
                if (session?.Plan != null)
                {
                    session.Plan.CapMode = ProgressionCapMode.Gauntlet;
                }
            }
            foreach (LeaderboardEntry entry in state.Leaderboard ?? new List<LeaderboardEntry>())
            {
                if (entry?.Key == null)
                {
                    continue;
                }
                entry.Key.CapMode = ProgressionCapMode.Gauntlet;
                entry.Key.SelectedBiome = BiomeTier.BlackForest;
            }
        }

        private static void MigrateLegacyRequest(ChallengeRequest request)
        {
            if (request == null)
            {
                return;
            }
            request.CapMode = ProgressionCapMode.Gauntlet;
            request.SelectedBiome = BiomeTier.BlackForest;
        }

        private static void InitializeArenaFoodSnapshots(PersistedWorldState state)
        {
            foreach (ArenaSession session in state?.InterruptedSessions ?? new List<ArenaSession>())
            {
                if (session?.ResourceSnapshot != null)
                {
                    session.ResourceSnapshot.ArenaFoodPrefabNames =
                        session.ResourceSnapshot.ArenaFoodPrefabNames ?? new List<string>();
                }
            }
        }

        private static void NormalizeAndValidate(PersistedWorldState state, long expectedWorldUid)
        {
            if (state == null || state.WorldUid == 0 || state.WorldUid != expectedWorldUid)
                throw new SerializationException("World UID is missing or does not match.");

            state.Arenas = state.Arenas ?? new List<ArenaDefinition>();
            state.Queue = state.Queue ?? new List<QueueEntry>();
            state.InterruptedSessions = state.InterruptedSessions ?? new List<ArenaSession>();
            state.Leaderboard = state.Leaderboard ?? new List<LeaderboardEntry>();
            EnsureCollectionLimits(state);

            var arenaIds = new HashSet<string>(StringComparer.Ordinal);
            var arenaNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (ArenaDefinition arena in state.Arenas)
            {
                if (arena == null) throw new SerializationException("Null arena definition.");
                string id = ArenaRegistry.NormalizeId(arena.ArenaId);
                string name = ArenaRegistry.NormalizeName(arena.DisplayName);
                if (id == null || name == null || !arenaIds.Add(id) || !arenaNames.Add(name))
                    throw new SerializationException("Arena IDs and names must be valid and unique.");
                if (!FinitePositive(arena.CombatRadius) || !FinitePositive(arena.ProtectedRadius) ||
                    arena.ProtectedRadius <= arena.CombatRadius || !Finite(arena.CorePosition))
                    throw new SerializationException($"Arena '{id}' has invalid bounds.");
                arena.ArenaId = id;
                arena.DisplayName = arena.DisplayName.Trim();
                arena.NormalizedName = name;
                arena.Markers = NormalizeMarkers(arena.Markers);
                if (arena.Enabled && !ArenaRegistry.MarkersAreComplete(arena.Markers))
                    throw new SerializationException($"Enabled arena '{id}' does not have a complete marker layout.");
            }

            var queuedByArena = new HashSet<string>(StringComparer.Ordinal);
            foreach (QueueEntry entry in state.Queue)
            {
                if (entry?.Request == null ||
                    !ArenaPlayerIdentityPolicy.IsValid(entry.Request.PlayerId) || entry.QueueSequence < 0)
                    throw new SerializationException("Queue entry is malformed.");
                NormalizeRequest(entry.Request, true);
                if (!arenaIds.Contains(entry.Request.ArenaId) ||
                    !queuedByArena.Add(entry.Request.ArenaId + ":" + entry.Request.PlayerId.ToString(CultureInfo.InvariantCulture)))
                    throw new SerializationException("Queue entry targets an unknown arena or duplicates a player.");
                entry.EnqueuedUtc = Utc(entry.EnqueuedUtc);
            }

            var interruptedIds = new HashSet<string>(StringComparer.Ordinal);
            var interruptedPlayers = new HashSet<long>();
            foreach (ArenaSession session in state.InterruptedSessions)
            {
                ValidateInterruptedSession(session);
                if (!arenaIds.Contains(session.ArenaId) || !interruptedIds.Add(session.SessionId) ||
                    !interruptedPlayers.Add(session.Request.PlayerId))
                    throw new SerializationException("Interrupted sessions must have unique IDs and players in a saved arena.");
            }

            var recordPlayers = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < state.Leaderboard.Count; i++)
            {
                LeaderboardEntry normalized = NormalizeLeaderboardEntry(state.Leaderboard[i]);
                string identity = KeyToken(normalized.Key) + ":" + normalized.PlayerId.ToString(CultureInfo.InvariantCulture);
                if (!recordPlayers.Add(identity))
                    throw new SerializationException("Leaderboard contains duplicate per-player records.");
                state.Leaderboard[i] = normalized;
            }
        }

        private static void EnsureCollectionLimits(PersistedWorldState state)
        {
            if (state.Arenas.Count > MaximumCollectionEntries || state.Queue.Count > MaximumCollectionEntries ||
                state.InterruptedSessions.Count > MaximumCollectionEntries || state.Leaderboard.Count > MaximumCollectionEntries)
                throw new SerializationException("Arena state collection limit exceeded.");
        }

        private static ArenaMarkerSet NormalizeMarkers(ArenaMarkerSet markers)
        {
            if (markers == null) return null;
            if (!Finite(markers.StagingPosition) || !Finite(markers.CombatantStartPosition) ||
                markers.EnemySpawnPositions == null ||
                markers.EnemySpawnPositions.Count != 4 ||
                markers.EnemySpawnPositions.Any(position => !Finite(position)))
                throw new SerializationException("Arena marker positions must be finite.");
            return markers;
        }

        private static void NormalizeRequest(ChallengeRequest request, bool requireIdentity)
        {
            if (request == null) throw new SerializationException("Challenge request is missing.");
            request.RequestId = ArenaRegistry.NormalizeId(request.RequestId);
            request.ArenaId = ArenaRegistry.NormalizeId(request.ArenaId);
            if (request.RequestId == null || request.ArenaId == null ||
                (requireIdentity && !ArenaPlayerIdentityPolicy.IsValid(request.PlayerId)) ||
                request.Mode < ChallengeMode.BiomeLadder || request.Mode > ChallengeMode.CustomEncounter ||
                request.CapMode < ProgressionCapMode.Gauntlet || request.CapMode > ProgressionCapMode.Biome ||
                request.CapMode == ProgressionCapMode.Biome &&
                (request.SelectedBiome < BiomeTier.Meadows || request.SelectedBiome > BiomeTier.DeepNorth))
                throw new SerializationException("Challenge request fields are invalid.");
            if (request.Mode == ChallengeMode.CustomEncounter)
            {
                request.CapMode = ProgressionCapMode.Gauntlet;
                request.SelectedBiome = BiomeTier.BlackForest;
            }
            else if (request.CapMode == ProgressionCapMode.Gauntlet)
            {
                request.SelectedBiome = BiomeTier.BlackForest;
            }
            request.PlayerName = (request.PlayerName ?? string.Empty).Trim();
            if (request.PlayerName.Length > 64) throw new SerializationException("Player name is too long.");
            request.RequestedUtc = Utc(request.RequestedUtc);
            if (request.Mode == ChallengeMode.CustomEncounter)
            {
                if (request.CustomSelection == null || string.IsNullOrWhiteSpace(request.CustomSelection.CreatureKey) ||
                    request.CustomSelection.CreatureKey.Length > 128 || request.CustomSelection.Quantity < 1 ||
                    request.CustomSelection.Quantity > 10 || request.CustomSelection.Stars < StarLevel.Base ||
                    request.CustomSelection.Stars > StarLevel.TwoStar)
                    throw new SerializationException("Custom encounter selection is invalid.");
                request.CustomSelection.CreatureKey = request.CustomSelection.CreatureKey.Trim();
            }
            else
            {
                request.CustomSelection = null;
            }
        }

        private static void ValidateInterruptedSession(ArenaSession session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            session.SessionId = ArenaRegistry.NormalizeId(session.SessionId);
            session.ArenaId = ArenaRegistry.NormalizeId(session.ArenaId);
            bool snapshotOptional = session.Phase == SessionPhase.Called || session.Phase == SessionPhase.Staging;
            if (session.SessionId == null || session.ArenaId == null || session.Request == null ||
                session.Plan == null || (session.ResourceSnapshot == null && !snapshotOptional))
                throw new SerializationException("Interrupted session is incomplete.");
            NormalizeRequest(session.Request, true);
            if (!string.Equals(session.Request.ArenaId, session.ArenaId, StringComparison.Ordinal) ||
                session.ResourceSnapshot != null && session.ResourceSnapshot.PlayerId != session.Request.PlayerId)
                throw new SerializationException("Interrupted session identity is inconsistent.");
            session.Plan.PlanId = ArenaRegistry.NormalizeId(session.Plan.PlanId);
            session.Plan.Encounters = session.Plan.Encounters ?? new List<EncounterDefinition>();
            if (session.Plan.PlanId == null || session.Plan.Encounters.Count == 0 ||
                session.Plan.Encounters.Count > MaximumCollectionEntries ||
                session.Plan.Mode != session.Request.Mode || session.Plan.CapMode != session.Request.CapMode ||
                session.Request.CapMode == ProgressionCapMode.Biome &&
                session.Plan.HighestBiome != session.Request.SelectedBiome)
                throw new SerializationException("Interrupted challenge plan is invalid.");
            for (int i = 0; i < session.Plan.Encounters.Count; i++)
            {
                EncounterDefinition encounter = session.Plan.Encounters[i];
                if (encounter == null || encounter.Sequence != i || string.IsNullOrWhiteSpace(encounter.CreatureKey) ||
                    encounter.CreatureKey.Length > 128 || encounter.Biome < BiomeTier.Meadows ||
                    encounter.Biome > BiomeTier.DeepNorth || encounter.Stars < StarLevel.Base ||
                    encounter.Stars > StarLevel.TwoStar || encounter.Quantity < 1 || encounter.Quantity > 10 ||
                    encounter.PreparationSeconds < 0 || encounter.PreparationSeconds > 300)
                    throw new SerializationException("Interrupted challenge encounter is invalid.");
            }
            session.SpawnedEnemyIds = session.SpawnedEnemyIds ?? new List<string>();
            if (session.EncounterIndex < 0 || session.EncounterIndex >= session.Plan.Encounters.Count ||
                session.LivingArenaEnemyCount < 0 || session.ElapsedMilliseconds < 0 ||
                session.SpawnedEnemyIds.Count > MaximumCollectionEntries)
                throw new SerializationException("Interrupted session counters or resource collection sizes are invalid.");
            if (session.ResourceSnapshot != null)
            {
                session.ResourceSnapshot.Foods = session.ResourceSnapshot.Foods ?? new List<FoodStateSnapshot>();
                session.ResourceSnapshot.ArenaFoodPrefabNames =
                    session.ResourceSnapshot.ArenaFoodPrefabNames ?? new List<string>();
                session.ResourceSnapshot.AllowedConsumableCounts = session.ResourceSnapshot.AllowedConsumableCounts ?? new Dictionary<string, int>();
                session.ResourceSnapshot.AmmunitionCounts = session.ResourceSnapshot.AmmunitionCounts ?? new Dictionary<string, int>();
                session.ResourceSnapshot.EquipmentDurabilityBySlot = session.ResourceSnapshot.EquipmentDurabilityBySlot ?? new Dictionary<string, float>();
                if (session.ResourceSnapshot.Foods.Count > 3 ||
                    session.ResourceSnapshot.ArenaFoodPrefabNames.Count > ArenaFoodSelectionPolicy.RequiredFoodCount ||
                    session.ResourceSnapshot.AllowedConsumableCounts.Count > MaximumCollectionEntries ||
                    session.ResourceSnapshot.AmmunitionCounts.Count > MaximumCollectionEntries ||
                    session.ResourceSnapshot.EquipmentDurabilityBySlot.Count > MaximumCollectionEntries)
                    throw new SerializationException("Interrupted resource collection sizes are invalid.");
                if (!FiniteNonNegative(session.ResourceSnapshot.RestedRemainingSeconds) ||
                    session.ResourceSnapshot.ArenaFoodPrefabNames.Any(value =>
                        string.IsNullOrWhiteSpace(value) || value.Length > 128) ||
                    session.ResourceSnapshot.ArenaFoodPrefabNames.Distinct(StringComparer.Ordinal).Count() !=
                    session.ResourceSnapshot.ArenaFoodPrefabNames.Count)
                    throw new SerializationException("Interrupted arena food snapshot is invalid.");
                foreach (FoodStateSnapshot food in session.ResourceSnapshot.Foods)
                {
                    if (food == null || string.IsNullOrWhiteSpace(food.ItemPrefabName) || food.ItemPrefabName.Length > 128 ||
                        !FiniteNonNegative(food.RemainingSeconds) || !FiniteNonNegative(food.Health) ||
                        !FiniteNonNegative(food.Stamina) || !FiniteNonNegative(food.Eitr))
                        throw new SerializationException("Interrupted food snapshot is invalid.");
                }
                ValidateCounts(session.ResourceSnapshot.AllowedConsumableCounts, "consumable");
                ValidateCounts(session.ResourceSnapshot.AmmunitionCounts, "ammunition");
                foreach (KeyValuePair<string, float> pair in session.ResourceSnapshot.EquipmentDurabilityBySlot)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 128 || !FiniteNonNegative(pair.Value))
                        throw new SerializationException("Interrupted equipment durability snapshot is invalid.");
                }
                session.ResourceSnapshot.CapturedUtc = Utc(session.ResourceSnapshot.CapturedUtc);
            }
            session.PhaseStartedUtc = Utc(session.PhaseStartedUtc);
            session.PhaseDeadlineUtc = Utc(session.PhaseDeadlineUtc);
            session.SessionStartedUtc = Utc(session.SessionStartedUtc);
        }

        private static LeaderboardEntry NormalizeLeaderboardEntry(LeaderboardEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (!ArenaPlayerIdentityPolicy.IsValid(entry.PlayerId) ||
                entry.FurthestEncounterIndex < 0 || entry.ElapsedMilliseconds < 0 ||
                entry.RosterRevision < 0)
                throw new ArgumentException("Leaderboard result contains invalid numeric values.", nameof(entry));
            string playerName = (entry.PlayerName ?? string.Empty).Trim();
            if (playerName.Length == 0 || playerName.Length > 64)
                throw new ArgumentException("Leaderboard player name is invalid.", nameof(entry));
            return new LeaderboardEntry
            {
                Key = NormalizeLeaderboardKey(entry.Key),
                PlayerId = entry.PlayerId,
                PlayerName = playerName,
                Completed = entry.Completed,
                FurthestEncounterIndex = entry.FurthestEncounterIndex,
                ElapsedMilliseconds = entry.ElapsedMilliseconds,
                RosterRevision = entry.RosterRevision,
                RecordedUtc = Utc(entry.RecordedUtc == default(DateTime) ? DateTime.UtcNow : entry.RecordedUtc)
            };
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
                    CustomCreatureKey = string.Empty,
                    CustomStars = StarLevel.Base,
                    CustomQuantity = 0
                };
            }

            string creatureKey = (key.CustomCreatureKey ?? string.Empty).Trim();
            if (creatureKey.Length == 0 || creatureKey.Length > 128 || key.CustomStars < StarLevel.Base ||
                key.CustomStars > StarLevel.TwoStar || key.CustomQuantity < 1 || key.CustomQuantity > 10)
                throw new ArgumentException("Custom leaderboard key is invalid.", nameof(key));
            return new LeaderboardKey
            {
                Mode = key.Mode,
                CapMode = ProgressionCapMode.Gauntlet,
                SelectedBiome = BiomeTier.BlackForest,
                CustomCreatureKey = creatureKey.ToLowerInvariant(),
                CustomStars = key.CustomStars,
                CustomQuantity = key.CustomQuantity
            };
        }

        private static bool IsBetter(LeaderboardEntry candidate, LeaderboardEntry existing)
        {
            if (candidate.Completed != existing.Completed) return candidate.Completed;
            if (candidate.Completed) return candidate.ElapsedMilliseconds < existing.ElapsedMilliseconds;
            if (candidate.FurthestEncounterIndex != existing.FurthestEncounterIndex)
                return candidate.FurthestEncounterIndex > existing.FurthestEncounterIndex;
            return candidate.ElapsedMilliseconds < existing.ElapsedMilliseconds;
        }

        private static bool KeysEqual(LeaderboardKey first, LeaderboardKey second)
        {
            return first != null && second != null && first.Mode == second.Mode && first.CapMode == second.CapMode &&
                   first.SelectedBiome == second.SelectedBiome &&
                   first.CustomStars == second.CustomStars && first.CustomQuantity == second.CustomQuantity &&
                   string.Equals(first.CustomCreatureKey ?? string.Empty, second.CustomCreatureKey ?? string.Empty,
                       StringComparison.OrdinalIgnoreCase);
        }

        private static string KeyToken(LeaderboardKey key)
        {
            return ((int)key.Mode).ToString(CultureInfo.InvariantCulture) + ":" +
                   ((int)key.CapMode).ToString(CultureInfo.InvariantCulture) + ":" +
                   ((int)key.SelectedBiome).ToString(CultureInfo.InvariantCulture) + ":" +
                   (key.CustomCreatureKey ?? string.Empty).ToLowerInvariant() + ":" +
                   ((int)key.CustomStars).ToString(CultureInfo.InvariantCulture) + ":" +
                   key.CustomQuantity.ToString(CultureInfo.InvariantCulture);
        }

        private static bool Finite(PositionData position)
        {
            return Finite(position.X) && Finite(position.Y) && Finite(position.Z);
        }

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool FinitePositive(float value)
        {
            return Finite(value) && value > 0f;
        }

        private static bool FiniteNonNegative(float value)
        {
            return Finite(value) && value >= 0f;
        }

        private static void ValidateCounts(Dictionary<string, int> values, string label)
        {
            foreach (KeyValuePair<string, int> pair in values)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 128 || pair.Value < 0)
                    throw new SerializationException($"Interrupted {label} snapshot is invalid.");
            }
        }

        private static DateTime Utc(DateTime value)
        {
            if (value == default(DateTime)) return DateTime.SpecifyKind(value, DateTimeKind.Utc);
            return value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        }

        private static string PreserveCorruptCopy(string path)
        {
            string copy = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            try
            {
                File.Copy(path, copy, false);
                return copy;
            }
            catch (Exception exception)
            {
                Plugin.Log?.LogWarning($"Could not preserve malformed ArenaGuard state '{path}': {exception.Message}");
                return "(copy failed)";
            }
        }

        private static PersistedWorldState Empty(long worldUid)
        {
            return new PersistedWorldState
            {
                WorldUid = worldUid,
                RosterRevision = 0,
                Arenas = new List<ArenaDefinition>(),
                Queue = new List<QueueEntry>(),
                InterruptedSessions = new List<ArenaSession>(),
                Leaderboard = new List<LeaderboardEntry>()
            };
        }

        private static PersistedWorldState Clone(PersistedWorldState source)
        {
            if (source == null) return null;
            return new PersistedWorldState
            {
                WorldUid = source.WorldUid,
                RosterRevision = source.RosterRevision,
                Arenas = (source.Arenas ?? new List<ArenaDefinition>()).Select(CopyArena).ToList(),
                Queue = (source.Queue ?? new List<QueueEntry>()).Select(CopyQueueEntry).ToList(),
                InterruptedSessions = (source.InterruptedSessions ?? new List<ArenaSession>()).Select(CopySession).ToList(),
                Leaderboard = (source.Leaderboard ?? new List<LeaderboardEntry>()).Select(CopyLeaderboardEntry).ToList()
            };
        }

        private static ArenaDefinition CopyArena(ArenaDefinition source)
        {
            return source == null ? null : new ArenaDefinition
            {
                ArenaId = source.ArenaId,
                DisplayName = source.DisplayName,
                NormalizedName = source.NormalizedName,
                CorePosition = source.CorePosition,
                CombatRadius = source.CombatRadius,
                ProtectedRadius = source.ProtectedRadius,
                Markers = source.Markers == null ? null : new ArenaMarkerSet
                {
                    StagingPosition = source.Markers.StagingPosition,
                    CombatantStartPosition = source.Markers.CombatantStartPosition,
                    EnemySpawnPositions = source.Markers.EnemySpawnPositions == null
                        ? new List<PositionData>()
                        : new List<PositionData>(source.Markers.EnemySpawnPositions)
                },
                Enabled = source.Enabled,
                Revision = source.Revision
            };
        }

        private static QueueEntry CopyQueueEntry(QueueEntry source)
        {
            return source == null ? null : new QueueEntry
            {
                Request = CopyRequest(source.Request),
                QueueSequence = source.QueueSequence,
                EnqueuedUtc = source.EnqueuedUtc
            };
        }

        private static ChallengeRequest CopyRequest(ChallengeRequest source)
        {
            return source == null ? null : new ChallengeRequest
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
            return source == null ? null : new ArenaSession
            {
                SessionId = source.SessionId,
                ArenaId = source.ArenaId,
                Request = CopyRequest(source.Request),
                Plan = source.Plan == null ? null : new ChallengePlan
                {
                    PlanId = source.Plan.PlanId,
                    Mode = source.Plan.Mode,
                    CapMode = source.Plan.CapMode,
                    HighestBiome = source.Plan.HighestBiome,
                    RosterRevision = source.Plan.RosterRevision,
                    Encounters = source.Plan.Encounters == null ? new List<EncounterDefinition>() :
                        source.Plan.Encounters.Select(encounter => encounter == null ? null : new EncounterDefinition
                        {
                            Sequence = encounter.Sequence,
                            CreatureKey = encounter.CreatureKey,
                            Biome = encounter.Biome,
                            Stars = encounter.Stars,
                            Quantity = encounter.Quantity,
                            IsMiniboss = encounter.IsMiniboss,
                            PreparationSeconds = encounter.PreparationSeconds
                        }).ToList()
                },
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

        private static PlayerResourceSnapshot CopyResourceSnapshot(PlayerResourceSnapshot source)
        {
            return source == null ? null : new PlayerResourceSnapshot
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
                ArenaFoodPrefabNames = source.ArenaFoodPrefabNames == null ? new List<string>() :
                    new List<string>(source.ArenaFoodPrefabNames),
                AllowedConsumableCounts = source.AllowedConsumableCounts == null ? new Dictionary<string, int>() :
                    new Dictionary<string, int>(source.AllowedConsumableCounts, StringComparer.Ordinal),
                AmmunitionCounts = source.AmmunitionCounts == null ? new Dictionary<string, int>() :
                    new Dictionary<string, int>(source.AmmunitionCounts, StringComparer.Ordinal),
                EquipmentDurabilityBySlot = source.EquipmentDurabilityBySlot == null ? new Dictionary<string, float>() :
                    new Dictionary<string, float>(source.EquipmentDurabilityBySlot, StringComparer.Ordinal),
                CapturedUtc = source.CapturedUtc
            };
        }

        private static LeaderboardEntry CopyLeaderboardEntry(LeaderboardEntry source)
        {
            return source == null ? null : new LeaderboardEntry
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

        [DataContract]
        private sealed class StoreFile
        {
            [DataMember(Name = "schemaVersion", Order = 1)] public int SchemaVersion;
            [DataMember(Name = "state", Order = 2)] public PersistedWorldState State;
        }
    }
}
