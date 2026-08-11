using System;
using System.Collections.Generic;

namespace ArenaGuard.Domain
{
    public enum BiomeTier
    {
        Meadows = 0,
        BlackForest = 1,
        Swamp = 2,
        Mountain = 3,
        Plains = 4,
        Mistlands = 5,
        Ashlands = 6,
        DeepNorth = 7
    }

    public enum ChallengeMode
    {
        BiomeLadder = 1,
        StarLadder = 2,
        CustomEncounter = 3
    }

    public enum ProgressionCapMode
    {
        Gauntlet = 1,
        Biome = 2
    }

    public enum StarLevel
    {
        Base = 0,
        OneStar = 1,
        TwoStar = 2
    }

    public enum SessionPhase
    {
        Queued,
        Called,
        Staging,
        Countdown,
        Fighting,
        Intermission,
        Victory,
        Defeat,
        Forfeit,
        Recovering,
        Closed
    }

    public enum SessionOutcome
    {
        None,
        Victory,
        Defeat,
        Forfeit,
        StartTimeout,
        BoundaryForfeit,
        Disconnect,
        ServerRestart,
        ArenaRemoved,
        RuntimeError
    }

    public enum ArenaRole
    {
        Visitor,
        Queued,
        Combatant,
        Spectator,
        Administrator
    }

    public enum ArenaMarkerKind
    {
        Staging,
        CombatantStart,
        EnemySpawn,
        HubGate
    }

    public enum ArenaEffectType
    {
        CallQueuedPlayer,
        MoveToStaging,
        StartCountdown,
        SpawnEncounter,
        DespawnSessionEnemies,
        RestorePlayerState,
        MoveToSpectatorArea,
        SetArenaRole,
        PersistState,
        UpdateLeaderboard,
        SendLocalMessage,
        SendGlobalMessage
    }

    public struct PositionData
    {
        public float X;
        public float Y;
        public float Z;
    }

    public sealed class ArenaMarkerSet
    {
        public PositionData StagingPosition;
        public PositionData CombatantStartPosition;
        public PositionData HubGatePosition;
        public List<PositionData> EnemySpawnPositions;
    }

    public sealed class ArenaDefinition
    {
        public string ArenaId;
        public string DisplayName;
        public string NormalizedName;
        public PositionData CorePosition;
        public float CombatRadius;
        public float ProtectedRadius;
        public ArenaMarkerSet Markers;
        public bool Enabled;
        public long Revision;
    }

    public sealed class ArenaGateDefinition
    {
        public string GateId;
        public string ArenaId;
        public string DisplayName;
        public string NormalizedName;
        public PositionData Position;
        public float RotationY;
        public bool IsFallbackEntrance;
    }

    public sealed class PlayerArenaRoute
    {
        public long PlayerId;
        public string ArenaId;
        public string OriginGateId;
        public DateTime EnteredUtc;
    }

    public sealed class CreatureDefinition
    {
        public string CreatureKey;
        public string PrefabName;
        public string SecondaryPrefabName;
        public string IconPrefabName;
        public string SecondaryIconPrefabName;
        public string DisplayName;
        public BiomeTier Biome;
        public int DifficultyOrder;
        public bool IsMiniboss;
        public bool Enabled;
        public List<StarLevel> SupportedStars;
    }

    public sealed class CustomEncounterSelection
    {
        public string CreatureKey;
        public StarLevel Stars;
        public int Quantity;
    }

    public sealed class ChallengeRequest
    {
        public string RequestId;
        public string ArenaId;
        public long PlayerId;
        public string PlayerName;
        public ChallengeMode Mode;
        public ProgressionCapMode CapMode;
        public BiomeTier SelectedBiome;
        public CustomEncounterSelection CustomSelection;
        public DateTime RequestedUtc;
    }

    public sealed class EncounterDefinition
    {
        public int Sequence;
        public string CreatureKey;
        public BiomeTier Biome;
        public StarLevel Stars;
        public int Quantity;
        public bool IsMiniboss;
        public int PreparationSeconds;
    }

    public sealed class ChallengePlan
    {
        public string PlanId;
        public ChallengeMode Mode;
        public ProgressionCapMode CapMode;
        public BiomeTier HighestBiome;
        public long RosterRevision;
        public List<EncounterDefinition> Encounters;
    }

    public sealed class QueueEntry
    {
        public ChallengeRequest Request;
        public string OriginGateId;
        public int QueueSequence;
        public DateTime EnqueuedUtc;
    }

    public sealed class FoodStateSnapshot
    {
        public string ItemPrefabName;
        public float RemainingSeconds;
        public float Health;
        public float Stamina;
        public float Eitr;
    }

    public sealed class ArenaFoodDefinition
    {
        public string PrefabName;
        public string DisplayName;
        public float Health;
        public float Stamina;
        public float Eitr;
        public float DurationSeconds;
    }

    public sealed class PlayerResourceSnapshot
    {
        public long PlayerId;
        public List<FoodStateSnapshot> Foods;
        public float RestedRemainingSeconds;
        public List<string> ArenaFoodPrefabNames;
        public Dictionary<string, int> AllowedConsumableCounts;
        public Dictionary<string, int> AmmunitionCounts;
        public Dictionary<string, float> EquipmentDurabilityBySlot;
        public DateTime CapturedUtc;
    }

    public sealed class ArenaSession
    {
        public string SessionId;
        public string ArenaId;
        public ChallengeRequest Request;
        public ChallengePlan Plan;
        public SessionPhase Phase;
        public SessionOutcome Outcome;
        public int EncounterIndex;
        public int LivingArenaEnemyCount;
        public List<string> SpawnedEnemyIds;
        public PlayerResourceSnapshot ResourceSnapshot;
        public DateTime PhaseStartedUtc;
        public DateTime PhaseDeadlineUtc;
        public DateTime SessionStartedUtc;
        public long ElapsedMilliseconds;
    }

    public sealed class ArenaEffect
    {
        public ArenaEffectType Type;
        public string ArenaId;
        public string SessionId;
        public long PlayerId;
        public EncounterDefinition Encounter;
        public string Message;
    }

    public sealed class LeaderboardKey
    {
        public ChallengeMode Mode;
        public ProgressionCapMode CapMode;
        public BiomeTier SelectedBiome;
        public string CustomCreatureKey;
        public StarLevel CustomStars;
        public int CustomQuantity;
    }

    public sealed class LeaderboardEntry
    {
        public LeaderboardKey Key;
        public long PlayerId;
        public string PlayerName;
        public bool Completed;
        public int FurthestEncounterIndex;
        public long ElapsedMilliseconds;
        public long RosterRevision;
        public DateTime RecordedUtc;
    }

    public sealed class ArenaClientSnapshot
    {
        public string ArenaId;
        public string SessionId;
        public string ArenaName;
        public PositionData CorePosition;
        public float CombatRadius;
        public float ProtectedRadius;
        public ArenaMarkerSet Markers;
        public SessionPhase Phase;
        public long CombatantPlayerId;
        public string CombatantName;
        public int QueuePosition;
        public int QueueLength;
        public int EncounterIndex;
        public int EncounterCount;
        public EncounterDefinition CurrentEncounter;
        public int CountdownSeconds;
        public long ElapsedMilliseconds;
        public bool PreparationComplete;
        public bool AdminsMayModifyTerrain;
        public bool AdminsMayBuild;
        public bool AdminsMayPickupDroppedItems;
    }

    public sealed class PersistedWorldState
    {
        public long WorldUid;
        public long RosterRevision;
        public List<ArenaDefinition> Arenas;
        public List<ArenaGateDefinition> Gates;
        public List<PlayerArenaRoute> Routes;
        public List<QueueEntry> Queue;
        public List<ArenaSession> InterruptedSessions;
        public List<LeaderboardEntry> Leaderboard;
    }
}
