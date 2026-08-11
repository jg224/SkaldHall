using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArenaGuard.Arenas;
using ArenaGuard.Domain;
using ArenaGuard.Persistence;

internal static partial class Program
{
    private const string ArenaIdOne = "11111111111111111111111111111111";
    private const string ArenaIdTwo = "22222222222222222222222222222222";
    private const string GateIdOne = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string GateIdTwo = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string GateIdFallback = "cccccccccccccccccccccccccccccccc";

    private static void RegistryUniqueNames()
    {
        ArenaRegistry.Clear();
        try
        {
            True(ArenaRegistry.RegisterArena(Arena(ArenaIdOne, "  Grand Arena  ")), "First arena should register.");
            False(ArenaRegistry.RegisterArena(Arena(ArenaIdTwo, "GRAND ARENA")),
                "Arena names must be case-insensitively unique after trimming.");

            ArenaDefinition found;
            True(ArenaRegistry.TryGetArenaByName(" grand arena ", out found), "Normalized name lookup should succeed.");
            Equal(ArenaIdOne, found.ArenaId);
            Equal("Grand Arena", found.DisplayName);
            Equal("grand arena", found.NormalizedName);

            True(ArenaRegistry.RegisterGate(Gate(GateIdOne, ArenaIdOne, " North Gate ", false, 10f)),
                "First gate should register.");
            False(ArenaRegistry.RegisterGate(Gate(GateIdTwo, ArenaIdOne, "north gate", false, 20f)),
                "Gate names must be case-insensitively unique after trimming.");
        }
        finally
        {
            ArenaRegistry.Clear();
        }
    }

    private static void RegistryReturnRouting()
    {
        ArenaRegistry.Clear();
        try
        {
            ArenaRegistry.RegisterArena(Arena(ArenaIdOne, "Arena"));
            True(ArenaRegistry.RegisterGate(Gate(GateIdFallback, ArenaIdOne, "Fallback", true, 90f)),
                "Fallback gate should register.");
            True(ArenaRegistry.RegisterGate(Gate(GateIdOne, ArenaIdOne, "West", false, 180f)),
                "First origin should register.");
            True(ArenaRegistry.RegisterGate(Gate(GateIdTwo, ArenaIdOne, "East", false, 270f)),
                "Second origin should register to the same arena.");
            Equal(3, ArenaRegistry.GetGatesForArena(ArenaIdOne).Count);

            True(ArenaRegistry.RecordRoute(new PlayerArenaRoute
            {
                PlayerId = 7,
                ArenaId = ArenaIdOne,
                OriginGateId = GateIdTwo,
                EnteredUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            }), "Route should be recorded.");

            ArenaGateDefinition destination = ArenaRegistry.ResolveReturnGate(7);
            Equal(GateIdTwo, destination.GateId);
            Equal(270f, destination.RotationY);
            Equal(270f, destination.Position.X);

            True(ArenaRegistry.RemoveGate(GateIdTwo), "Origin gate should be removable.");
            destination = ArenaRegistry.ResolveReturnGate(7);
            Equal(GateIdFallback, destination.GateId);
            Equal(90f, destination.RotationY);
        }
        finally
        {
            ArenaRegistry.Clear();
        }
    }

    private static void RegistryQueueOrdering()
    {
        ArenaRegistry.Clear();
        try
        {
            ArenaRegistry.RegisterArena(Arena(ArenaIdOne, "Queue Arena"));
            ArenaRegistry.RegisterGate(Gate(GateIdOne, ArenaIdOne, "Queue Gate", true, 0f));
            var sameTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            ArenaRegistry.ReplaceQueue(new[]
            {
                RegistryQueueEntry(3, 30, sameTime.AddSeconds(2)),
                RegistryQueueEntry(1, 10, sameTime.AddSeconds(3)),
                RegistryQueueEntry(2, 20, sameTime.AddSeconds(1)),
                RegistryQueueEntry(2, 40, sameTime.AddSeconds(4))
            });

            SequenceEqual(new[] { 1L, 2L, 3L },
                ArenaRegistry.GetQueueSnapshot(ArenaIdOne).Select(entry => entry.Request.PlayerId));
            SequenceEqual(new[] { 10, 20, 30 },
                ArenaRegistry.GetQueueSnapshot().Select(entry => entry.QueueSequence));
        }
        finally
        {
            ArenaRegistry.Clear();
        }
    }

    private static void MarkerBeaconLifecycle()
    {
        WithTemporaryStore(() =>
        {
            const long worldUid = 9020;
            ArenaRegistry.Clear();
            True(ArenaRegistry.RegisterArena(Arena(ArenaIdOne, "Marker Arena")),
                "Complete arena should register.");

            True(ArenaRegistry.RemoveMarker(ArenaIdOne, ArenaMarkerKind.HubGate, -1),
                "Optional Return Gate location should be removable.");
            ArenaDefinition arena;
            True(ArenaRegistry.TryGetArena(ArenaIdOne, out arena), "Arena should remain registered.");
            True(arena.Enabled, "Editing the optional Return Gate location must not disable the challenge arena.");
            True(ArenaRegistry.MarkersAreComplete(arena.Markers),
                "Arena Master, Combat Start, and four Enemy Spawns are the complete challenge layout.");
            False(ArenaRegistry.MissingRequiredMarkers(arena.Markers)
                    .Any(item => item.IndexOf("hub", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 item.IndexOf("gate", StringComparison.OrdinalIgnoreCase) >= 0),
                "Missing-marker diagnostics must never require an optional gate.");

            True(ArenaRegistry.RemoveMarker(ArenaIdOne, ArenaMarkerKind.EnemySpawn, 1),
                "Enemy Spawn 2 should be removable by its stable slot.");
            True(ArenaRegistry.TryGetArena(ArenaIdOne, out arena), "Arena should remain registered.");
            False(arena.Enabled, "Editing a marker must disable the arena until the layout is confirmed.");
            False(ArenaRegistry.MarkersAreComplete(arena.Markers),
                "Deleted marker must not remain a valid saved spawn position.");
            Equal(1, ArenaRegistry.NextAvailableEnemyMarkerSlot(ArenaIdOne));

            PersistedWorldState snapshot = ArenaRegistry.Snapshot(
                worldUid,
                1,
                Enumerable.Empty<ArenaSession>(),
                Enumerable.Empty<LeaderboardEntry>());
            True(ArenaStore.Save(snapshot), "Incomplete disabled layout should persist.");
            PersistedWorldState loaded = ArenaStore.Load(worldUid);
            ArenaRegistry.Replace(loaded);
            Equal(1, ArenaRegistry.NextAvailableEnemyMarkerSlot(ArenaIdOne));

            PositionData replacement = new PositionData { X = 42f, Y = 3f, Z = -7f };
            True(ArenaRegistry.SetMarker(ArenaIdOne, ArenaMarkerKind.EnemySpawn, 1, replacement),
                "Replacement beacon should reuse the deleted stable slot.");
            True(ArenaRegistry.TryGetArena(ArenaIdOne, out arena), "Updated arena should remain registered.");
            True(ArenaRegistry.MarkersAreComplete(arena.Markers), "Replacement should restore a complete layout.");
            Equal(42f, arena.Markers.EnemySpawnPositions[1].X);
            Equal(-1, ArenaRegistry.NextAvailableEnemyMarkerSlot(ArenaIdOne));

            arena.Enabled = true;
            True(ArenaRegistry.RegisterArena(arena), "Complete replacement layout should be enableable.");
            True(ArenaRegistry.RemoveMarker(ArenaIdOne, ArenaMarkerKind.CombatantStart, -1),
                "Combat Start should be removable.");
            True(ArenaRegistry.TryGetArena(ArenaIdOne, out arena), "Arena should remain after Combat Start removal.");
            arena.Enabled = true;
            Throws<ArgumentException>(() => ArenaRegistry.RegisterArena(arena));
        });
    }

    private static void CombatStartRadiusInvariant()
    {
        ArenaRegistry.Clear();
        try
        {
            ArenaDefinition arena = Arena(ArenaIdOne, "Bounded Arena");
            True(ArenaRegistry.RegisterArena(arena), "Arena should register with an in-bounds Combat Start.");
            True(ArenaRegistry.RemoveMarker(ArenaIdOne, ArenaMarkerKind.CombatantStart, -1),
                "Combat Start should be removable before replacement.");

            False(ArenaRegistry.SetMarker(
                    ArenaIdOne,
                    ArenaMarkerKind.CombatantStart,
                    -1,
                    new PositionData { X = 20.01f }),
                "Combat Start outside the XZ combat radius must be rejected.");
            True(ArenaRegistry.SetMarker(
                    ArenaIdOne,
                    ArenaMarkerKind.CombatantStart,
                    -1,
                    new PositionData { X = 12f, Y = 50f, Z = 16f }),
                "Combat Start on the radius boundary must be accepted regardless of height.");

            True(ArenaRegistry.TryGetArena(ArenaIdOne, out arena), "Arena should retain the boundary marker.");
            arena.Markers.CombatantStartPosition = new PositionData { Z = 20.01f };
            False(ArenaRegistry.SetMarkers(ArenaIdOne, arena.Markers),
                "Bulk marker updates must enforce the same Combat Start radius invariant.");
        }
        finally
        {
            ArenaRegistry.Clear();
        }
    }

    private static void StoreLeaderboardOrdering()
    {
        WithTemporaryStore(() =>
        {
            const long worldUid = 9001;
            True(ArenaStore.Save(EmptyWorld(worldUid)), "Initial world state should save.");
            var key = new LeaderboardKey
            {
                Mode = ChallengeMode.BiomeLadder,
                CapMode = ProgressionCapMode.Gauntlet,
                SelectedBiome = BiomeTier.BlackForest
            };

            True(ArenaStore.RecordResult(Result(key, 1, false, 2, 10000)), "First result should record.");
            False(ArenaStore.RecordResult(Result(key, 1, false, 1, 1000)), "Lower progress must not replace best.");
            True(ArenaStore.RecordResult(Result(key, 1, false, 2, 9000)), "Faster equal progress should replace best.");
            True(ArenaStore.RecordResult(Result(key, 1, true, 4, 50000)), "Completion should replace failure.");
            False(ArenaStore.RecordResult(Result(key, 1, true, 4, 60000)), "Slower completion must not replace best.");
            True(ArenaStore.RecordResult(Result(key, 1, true, 4, 20000)), "Faster completion should replace best.");
            True(ArenaStore.RecordResult(Result(key, 2, true, 4, 30000)), "Second player should record.");
            True(ArenaStore.RecordResult(Result(key, 3, false, 5, 1000)), "Failed progress should record.");

            var board = ArenaStore.GetLeaderboard(key);
            SequenceEqual(new[] { 1L, 2L, 3L }, board.Select(entry => entry.PlayerId));
            Equal(3, board.Count);
            Equal(20000L, board[0].ElapsedMilliseconds);
            Equal(5, board[2].FurthestEncounterIndex);
            False(board[2].Completed, "Incomplete entry should rank after completions.");

            var snapshot = ArenaStore.Snapshot();
            Equal(3, snapshot.Leaderboard.Count);
            snapshot.Leaderboard.Clear();
            Equal(3, ArenaStore.Snapshot().Leaderboard.Count);

            var swampKey = new LeaderboardKey
            {
                Mode = ChallengeMode.BiomeLadder,
                CapMode = ProgressionCapMode.Biome,
                SelectedBiome = BiomeTier.Swamp
            };
            var mountainKey = new LeaderboardKey
            {
                Mode = ChallengeMode.BiomeLadder,
                CapMode = ProgressionCapMode.Biome,
                SelectedBiome = BiomeTier.Mountain
            };
            True(ArenaStore.RecordResult(Result(swampKey, 1, true, 3, 5000)),
                "A biome-scoped result should record independently.");
            True(ArenaStore.RecordResult(Result(mountainKey, 1, true, 3, 6000)),
                "A second biome must use a distinct leaderboard key.");
            Equal(5000L, ArenaStore.GetLeaderboard(swampKey).Single().ElapsedMilliseconds);
            Equal(6000L, ArenaStore.GetLeaderboard(mountainKey).Single().ElapsedMilliseconds);
        });
    }

    private static void StoreRejectsMalformedAndWrongWorld()
    {
        WithTemporaryStore(() =>
        {
            const long sourceWorld = 1001;
            const long wrongWorld = 1002;
            const long malformedWorld = 1003;
            True(ArenaStore.Save(EmptyWorld(sourceWorld)), "Source world should save.");

            string sourcePath = ArenaStore.GetPath(sourceWorld);
            string wrongPath = ArenaStore.GetPath(wrongWorld);
            File.Copy(sourcePath, wrongPath);
            var loaded = ArenaStore.Load(wrongWorld);
            Equal(wrongWorld, loaded.WorldUid);
            Equal(0, loaded.Arenas.Count);
            True(Directory.GetFiles(BepInEx.Paths.ConfigPath,
                    Path.GetFileName(wrongPath) + ".corrupt-*").Length == 1,
                "Wrong-world save should be preserved as corrupt and rejected.");

            string malformedPath = ArenaStore.GetPath(malformedWorld);
            File.WriteAllText(malformedPath, "{ definitely-not-json }");
            loaded = ArenaStore.Load(malformedWorld);
            Equal(malformedWorld, loaded.WorldUid);
            Equal(0, loaded.Leaderboard.Count);
            True(Directory.GetFiles(BepInEx.Paths.ConfigPath,
                    Path.GetFileName(malformedPath) + ".corrupt-*").Length == 1,
                "Malformed save should be preserved as corrupt and rejected.");
        });
    }

    private static void StorePreCombatSessionRoundTrip()
    {
        WithTemporaryStore(() =>
        {
            const long worldUid = 9010;
            PersistedWorldState state = EmptyWorld(worldUid);
            state.Arenas.Add(Arena(ArenaIdOne, "Recovery Arena"));
            state.InterruptedSessions.Add(PreCombatSession(1, SessionPhase.Called));
            state.InterruptedSessions.Add(PreCombatSession(2, SessionPhase.Staging));

            True(ArenaStore.Save(state), "Pre-combat interrupted sessions should save without resource snapshots.");
            PersistedWorldState loaded = ArenaStore.Load(worldUid);

            Equal(2, loaded.InterruptedSessions.Count);
            SequenceEqual(new[] { SessionPhase.Called, SessionPhase.Staging },
                loaded.InterruptedSessions.OrderBy(session => session.Request.PlayerId).Select(session => session.Phase));
            True(loaded.InterruptedSessions.All(session => session.ResourceSnapshot == null),
                "Persistence must not invent a pre-combat resource snapshot.");
            Equal(1, loaded.InterruptedSessions.Select(session => session.ArenaId).Distinct().Count());
        });
    }

    private static void StoreRotatesStartupBackups()
    {
        WithTemporaryStore(() =>
        {
            const long worldUid = 9011;
            True(ArenaStore.Save(EmptyWorld(worldUid)), "Initial world state should save.");
            string path = ArenaStore.GetPath(worldUid);
            for (int index = 0; index < 8; index++)
            {
                PersistedWorldState loaded = ArenaStore.Load(worldUid);
                Equal(worldUid, loaded.WorldUid);
            }

            string[] backups = Directory.GetFiles(
                BepInEx.Paths.ConfigPath,
                Path.GetFileName(path) + ".startup-*.bak",
                SearchOption.TopDirectoryOnly);
            Equal(5, backups.Length);
            True(File.Exists(path), "Rotating startup backups must preserve the live state file.");
        });
    }

    private static void StoreMigratesLegacyProgression()
    {
        WithTemporaryStore(() =>
        {
            const long worldUid = 9012;
            PersistedWorldState state = EmptyWorld(worldUid);
            state.Arenas.Add(Arena(ArenaIdOne, "Legacy Arena"));
            state.Queue.Add(RegistryQueueEntry(9, 1, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
            state.Queue[0].Request.CapMode = ProgressionCapMode.Biome;
            state.Queue[0].Request.SelectedBiome = BiomeTier.Swamp;
            state.Leaderboard.Add(Result(new LeaderboardKey
            {
                Mode = ChallengeMode.BiomeLadder,
                CapMode = ProgressionCapMode.Biome,
                SelectedBiome = BiomeTier.Swamp
            }, 9, true, 3, 1234));
            True(ArenaStore.Save(state), "New state should save before the migration fixture is rewritten.");

            string path = ArenaStore.GetPath(worldUid);
            string serialized = File.ReadAllText(path);
            serialized = serialized.Replace("\"schemaVersion\":3", "\"schemaVersion\":1");
            File.WriteAllText(path, serialized);

            PersistedWorldState loaded = ArenaStore.Load(worldUid);
            Equal(ProgressionCapMode.Gauntlet, loaded.Queue.Single().Request.CapMode);
            Equal(BiomeTier.BlackForest, loaded.Queue.Single().Request.SelectedBiome);
            Equal(ProgressionCapMode.Gauntlet, loaded.Leaderboard.Single().Key.CapMode);
            Equal(BiomeTier.BlackForest, loaded.Leaderboard.Single().Key.SelectedBiome);
        });
    }

    private static void WithTemporaryStore(Action action)
    {
        string path = Path.Combine(Path.GetTempPath(), "ArenaGuard-CoreTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        BepInEx.Paths.ConfigPath = path;
        ArenaGuard.Plugin.Log = null;
        ArenaStore.Clear();
        try
        {
            action();
        }
        finally
        {
            ArenaStore.Clear();
            string resolved = Path.GetFullPath(path);
            string temp = Path.GetFullPath(Path.GetTempPath());
            if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
                resolved.IndexOf("ArenaGuard-CoreTests-", StringComparison.OrdinalIgnoreCase) < 0)
            {
                throw new InvalidOperationException("Refusing to clean an unexpected test directory.");
            }

            Directory.Delete(resolved, true);
        }
    }

    private static ArenaDefinition Arena(string arenaId, string displayName)
    {
        return new ArenaDefinition
        {
            ArenaId = arenaId,
            DisplayName = displayName,
            CorePosition = new PositionData(),
            CombatRadius = 20f,
            ProtectedRadius = 30f,
            Markers = new ArenaMarkerSet
            {
                StagingPosition = new PositionData { X = 25f },
                CombatantStartPosition = new PositionData(),
                HubGatePosition = new PositionData { X = 28f },
                EnemySpawnPositions = new List<PositionData>
                {
                    new PositionData { X = 10f },
                    new PositionData { X = -10f },
                    new PositionData { Z = 10f },
                    new PositionData { Z = -10f }
                }
            },
            Enabled = true,
            Revision = 1
        };
    }

    private static ArenaGateDefinition Gate(
        string gateId,
        string arenaId,
        string displayName,
        bool fallback,
        float x)
    {
        return new ArenaGateDefinition
        {
            GateId = gateId,
            ArenaId = arenaId,
            DisplayName = displayName,
            Position = new PositionData { X = x },
            RotationY = x,
            IsFallbackEntrance = fallback
        };
    }

    private static QueueEntry RegistryQueueEntry(long playerId, int sequence, DateTime enqueued)
    {
        return new QueueEntry
        {
            Request = new ChallengeRequest
            {
                RequestId = GuidForPlayer(playerId),
                ArenaId = ArenaIdOne,
                PlayerId = playerId,
                PlayerName = "Player " + playerId,
                Mode = ChallengeMode.BiomeLadder,
                CapMode = ProgressionCapMode.Gauntlet,
                SelectedBiome = BiomeTier.BlackForest,
                RequestedUtc = enqueued
            },
            OriginGateId = GateIdOne,
            QueueSequence = sequence,
            EnqueuedUtc = enqueued
        };
    }

    private static PersistedWorldState EmptyWorld(long uid)
    {
        return new PersistedWorldState
        {
            WorldUid = uid,
            RosterRevision = 1,
            Arenas = new List<ArenaDefinition>(),
            Gates = new List<ArenaGateDefinition>(),
            Routes = new List<PlayerArenaRoute>(),
            Queue = new List<QueueEntry>(),
            InterruptedSessions = new List<ArenaSession>(),
            Leaderboard = new List<LeaderboardEntry>()
        };
    }

    private static ArenaSession PreCombatSession(long playerId, SessionPhase phase)
    {
        return new ArenaSession
        {
            SessionId = GuidForPlayer(playerId + 100),
            ArenaId = ArenaIdOne,
            Request = new ChallengeRequest
            {
                RequestId = GuidForPlayer(playerId),
                ArenaId = ArenaIdOne,
                PlayerId = playerId,
                PlayerName = "Player " + playerId,
                Mode = ChallengeMode.BiomeLadder,
                CapMode = ProgressionCapMode.Gauntlet,
                SelectedBiome = BiomeTier.BlackForest,
                RequestedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            },
            Plan = new ChallengePlan
            {
                PlanId = GuidForPlayer(playerId + 200),
                Mode = ChallengeMode.BiomeLadder,
                CapMode = ProgressionCapMode.Gauntlet,
                HighestBiome = BiomeTier.Meadows,
                RosterRevision = 1,
                Encounters = new List<EncounterDefinition>
                {
                    new EncounterDefinition
                    {
                        Sequence = 0,
                        CreatureKey = "greyling",
                        Biome = BiomeTier.Meadows,
                        Stars = StarLevel.Base,
                        Quantity = 1,
                        PreparationSeconds = 3
                    }
                }
            },
            Phase = phase,
            EncounterIndex = 0,
            SpawnedEnemyIds = new List<string>(),
            ResourceSnapshot = null
        };
    }

    private static LeaderboardEntry Result(
        LeaderboardKey key,
        long playerId,
        bool completed,
        int furthest,
        long milliseconds)
    {
        return new LeaderboardEntry
        {
            Key = key,
            PlayerId = playerId,
            PlayerName = "Player " + playerId,
            Completed = completed,
            FurthestEncounterIndex = furthest,
            ElapsedMilliseconds = milliseconds,
            RosterRevision = 1,
            RecordedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(milliseconds)
        };
    }

    private static string GuidForPlayer(long playerId)
    {
        return playerId.ToString("x32");
    }
}
