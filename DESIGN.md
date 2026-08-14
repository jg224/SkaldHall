# ArenaGuard — Design Document

ArenaGuard is a required client/server Valheim mod that lets administrators place named Arena Cores for protected challenges. One player at a time selects a challenge from the Arena Master, enters a server-authoritative queue, and fights curated enemies while everyone else becomes an untargetable, non-interacting ghost spectator. Arena structures cannot be damaged, lethal damage ends the challenge without killing the player, and every exit restores a consequence-free state while server-wide leaderboards record performance without granting loot or other rewards.

## Domain

```csharp
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
        public System.Collections.Generic.List<PositionData> EnemySpawnPositions;
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

    public sealed class CreatureDefinition
    {
        public string CreatureKey;
        public string PrefabName;
        public string DisplayName;
        public BiomeTier Biome;
        public int DifficultyOrder;
        public bool IsMiniboss;
        public bool Enabled;
        public System.Collections.Generic.List<StarLevel> SupportedStars;
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
        public System.DateTime RequestedUtc;
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
        public System.Collections.Generic.List<EncounterDefinition> Encounters;
    }

    public sealed class QueueEntry
    {
        public ChallengeRequest Request;
        public int QueueSequence;
        public System.DateTime EnqueuedUtc;
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
        public System.Collections.Generic.List<FoodStateSnapshot> Foods;
        public float RestedRemainingSeconds;
        public System.Collections.Generic.List<string> ArenaFoodPrefabNames;
        public System.Collections.Generic.Dictionary<string, int> AllowedConsumableCounts;
        public System.Collections.Generic.Dictionary<string, int> AmmunitionCounts;
        public System.Collections.Generic.Dictionary<string, float> EquipmentDurabilityBySlot;
        public System.DateTime CapturedUtc;
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
        public System.Collections.Generic.List<string> SpawnedEnemyIds;
        public PlayerResourceSnapshot ResourceSnapshot;
        public System.DateTime PhaseStartedUtc;
        public System.DateTime PhaseDeadlineUtc;
        public System.DateTime SessionStartedUtc;
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
        public System.DateTime RecordedUtc;
    }

    public sealed class ArenaClientSnapshot
    {
        public string ArenaId;
        public string ArenaName;
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
    }

    public sealed class PersistedWorldState
    {
        public long WorldUid;
        public long RosterRevision;
        public System.Collections.Generic.List<ArenaDefinition> Arenas;
        public System.Collections.Generic.List<QueueEntry> Queue;
        public System.Collections.Generic.List<ArenaSession> InterruptedSessions;
        public System.Collections.Generic.List<LeaderboardEntry> Leaderboard;
    }
}
```

## Modules (each is one Piece of work)

| File | Responsibility |
|---|---|
| `src/ArenaGuard/Domain/ArenaContracts.cs` | Owns the shared domain types above and no game-facing behavior; every other module imports these exact contracts. |
| `src/ArenaGuard/Arenas/ArenaRegistry.cs` | Owns in-memory Arena Core, marker, occupancy, and queue indexes; exports `RegisterArena(ArenaDefinition)`, `RemoveArena(string)`, `TryGetArena(string, out ArenaDefinition)`, `FindProtectedArena(PositionData)`, `FindCombatArena(PositionData)`, and `SetMarkers(string, ArenaMarkerSet)`. |
| `src/ArenaGuard/Persistence/ArenaStore.cs` | Owns atomic world-UID-keyed JSON persistence and server-wide records; exports `Load(long)`, `Save(PersistedWorldState)`, `RecordResult(LeaderboardEntry)`, `GetLeaderboard(LeaderboardKey)`, and `MarkInterrupted(ArenaSession)`. |
| `src/ArenaGuard/Challenges/ChallengeCatalog.cs` | Owns the checked-in, administrator-configurable vanilla land-biome roster and planning rules; exports `LoadRoster()`, `ListCreatures(BiomeTier)`, `BuildPlan(ChallengeRequest, BiomeTier)`, and `ValidateCustomSelection(CustomEncounterSelection)`. Mode 1 emits each enabled creature once at base level; mode 2 emits base, one-star, and two-star encounters back-to-back before advancing; mode 3 emits one selected group of 1–10 creatures. Minibosses sort last, Ocean/boss/passive/modded entries are excluded, and Dvergr are explicitly included. |
| `src/ArenaGuard/Sessions/ArenaSessionEngine.cs` | Owns the pure deterministic queue and session state machine and emits `ArenaEffect` values without calling Unity; exports `Enqueue(QueueEntry)`, `AcceptTurn(long)`, `EnterCombatFloor(long)`, `Tick(DateTime)`, `ReportEncounterCleared(string)`, `ReportBoundaryState(long, bool)`, `ReportLethalDamage(long)`, `Forfeit(long)`, `Disconnect(long)`, `RecoverAfterRestart(PersistedWorldState)`, and `ApplyEffectResult(ArenaEffect, bool)`. |
| `src/ArenaGuard/Runtime/ArenaServerRuntime.cs` | Owns the main-thread server coordinator that applies engine effects to live Valheim objects; exports `TickOnMainThread()`, `SpawnEncounter(EncounterDefinition)`, `DespawnSessionEnemies(string)`, `MoveToStaging(long)`, `MoveToSpectatorArea(long)`, `CaptureResources(long)`, `RestoreResources(long, SessionOutcome)`, and `RepositionEscapedOrStuckEnemies(string)`. |
| `src/ArenaGuard/World/ArenaWorldObjects.cs` | Owns Jotunn registration and interaction adapters for the hidden admin-only Arena Hammer, Arena Core, Arena Master, Arena Sign compatibility piece, active marker types, and the legacy hidden staging marker. Exports `RegisterPrefabs()`, `GrantOrRemoveAdminHammer()`, `ApplyAdminMarker()`, and `OpenArenaSign()`. |
| `src/ArenaGuard/Rules/ArenaRulePatches.cs`<br>`src/ArenaGuard/Rules/ArenaFoodSelectionPolicy.cs`<br>`src/ArenaGuard/Rules/ArenaFoodIndexPolicy.cs` | Owns the Harmony rules for permanent structure invulnerability, admin-only building/demolition and terrain editing, lethal-damage interception, spectator ghosting and target rejection, arena-enemy targeting, collision suppression, no loot/skill XP, no ammunition use, no durability loss, consumable allowlisting, combatant/spectator interaction locks, the pure exactly-three-food selection invariant, and CraftIndex-compatible Health/Stamina/Eitr grouping. Exports query methods `GetRole(long)`, `CanDamage(...)`, `CanInteract(...)`, `CanConsume(...)`, `ShouldConsumeAmmo(long)`, `ShouldLoseDurability(long)`, `CanGainSkillXp(long)`, `CanDropLoot(string)`, `IsValidArenaTarget(string, long)`, and `RefreshGhostCollisions()`. |
| `src/ArenaGuard/Networking/ArenaRpc.cs` | Owns required-version client/server registration, sender-to-character resolution, server validation, request/response packages, and client snapshots; exports `Register()`, `RequestChallenge(ChallengeRequest)`, `AcceptQueueCall(string)`, `RequestForfeit(string)`, `RequestAdminMutation(...)`, `BroadcastArenaState(string)`, `SendLeaderboard(long, LeaderboardKey)`, and `Shutdown()`. Clients never choose authoritative player IDs, roles, spawn results, records, or admin status. |
| `src/ArenaGuard/UI/ArenaUi.cs` | Owns the Arena Master challenge selector, right-side biome and creature pickers, queue acceptance prompt, 60-second three-column discovered-food preparation picker, three-second countdown, combat HUD, forfeit action, admin panel, and server-wide leaderboard display; exports `OpenChallengeMenu(string)`, `OpenFoodPreparation(...)`, `OpenAdminPanel(string)`, `ShowQueueCall(...)`, `RenderSnapshot(ArenaClientSnapshot)`, `ShowLeaderboard(...)`, and `CloseArenaUi()`. Modes 1 and 2 announce starts and outcomes globally; mode 3 remains silent outside the arena. |
| `src/ArenaGuard/Plugin.cs`<br>`src/ArenaGuard/ArenaGuard.csproj`<br>`ArenaGuard.slnx`<br>`Directory.Build.props`<br>`verify.ps1` | Owns composition, configuration binding, Harmony/Jotunn startup and shutdown, the `net472` client/server project, strict local references with `Private=false`, solution wiring, and the single verification entry point. The server tick is attached to a verified main-thread Valheim lifecycle method; no game API is called from a ThreadPool timer. |
| `tests/ArenaGuard.CoreTests/Program.cs`<br>`tests/ArenaGuard.CoreTests/ArenaGuard.CoreTests.csproj`<br>`tests/ArenaGuard.ApiTests/Program.cs`<br>`tests/ArenaGuard.ApiTests/ArenaGuard.ApiTests.csproj` | Owns executable zero-NuGet regression suites. Core tests cover plan ordering, star sequences, queue fairness, timeouts, restart recovery, boundary forfeits, resource restoration, announcements, and leaderboard ranking. Cecil API tests prove every patched type/member, RPC payload, AI target hook, damage/death hook, item-use hook, skill/loot hook, Jotunn dependency, plugin metadata, and output-directory invariant against the installed DLLs. |

## Conventions

- Build one required client/server `SkaldHall.dll` targeting `net472`, with BepInEx 5.4.2333, Harmony, Jotunn, Valheim, and Unity references resolved from `C:\ValheimServer\server` and always marked `Private=false`.
- Use BepInEx GUID `jg224.arenaguard`. Require an exact ArenaGuard version match on server and clients; fail connection with a clear message when absent or mismatched.
- The dedicated server owns arenas, queues, sessions, ladder scopes, spawn plans, roles, records, and admin authorization. Clients may request actions and report owner-side game events, but the server resolves the sending peer and validates the request against current state.
- All Unity, Valheim, Jotunn, ZNet, ZDO, AI, spawning, teleporting, and player mutations run on the Unity main thread. Pure state-machine calculations may run independently but have no game references.
- Arena IDs, request IDs, plan IDs, session IDs, and enemy IDs are lowercase GUID strings. User-facing arena names are trimmed, case-insensitively unique, and stored separately from IDs.
- Each Arena Core defaults to a 20-metre combat radius and 30-metre protected radius; require `ProtectedRadius > CombatRadius`, exactly four enemy spawn markers, one Arena Master staging position outside the combat radius, and one combatant-start marker inside it before enabling challenges.
- The outer protected radius is permanently structure-safe: direct, support, and weather damage are rejected, damage-driven resource refunds are suppressed, and generic destructibles, rocks, mine-rock sections, trees, and logs reject combat damage. Intentional admin demolition still returns normal materials. Only server-authenticated non-combatant admins may build, dismantle, or modify terrain under the restored 0.1.5 rules. Doors and containers remain usable when no challenge is active; active combatants and ghost spectators cannot interact with world objects.
- The Arena Hammer prefab is registered on every client for network consistency but hidden from non-admin UI. It is automatically spawned for authenticated admins, cannot be dropped or transferred, is removed when authorization ends or the player leaves, and never grants authority by possession alone.
- One active combatant is allowed per arena. Queues are FIFO. Each server-created call session has a distinct client prompt identity; replacement calls after a timeout reopen the prompt, repeated snapshots for one call do not, and unavailable UI creation is retried. A called player has 30 seconds to accept, then 60 seconds to select and confirm exactly three distinct discovered foods. A missed preparation deadline restores any accepted snapshot, returns the player to the Arena Master, and advances the queue.
- Food confirmation captures one immutable pre-challenge resource snapshot. The client may select only valid food prefabs known to that character; the server independently validates the count, uniqueness, prefab identity, and food properties before accepting the snapshot. The picker groups foods into three simultaneous CraftIndex-compatible Health, Stamina, and Eitr scroll lists, orders each by its relevant stat, shows color-coded stats in compact rows, and maintains one ordered three-slot selection across every list. The selected foods are applied directly without inventory mutation, health/stamina/eitr are filled, and a non-expiring Rested effect is supplied for the session. Only then does the server move the player to Combat Start and begin the three-second countdown.
- Every cleared encounter is followed by the same three-second countdown before the next encounter spawns. Victory displays results for five seconds before cleanup and Arena Master return.
- Leaving the combat radius starts a center-screen five-second countdown without moving the combatant. Returning inside clears the warning and continues the fight; remaining outside ends the run as a boundary forfeit and returns the combatant to the Arena Master. A manual Forfeit action ends immediately.
- An active combatant reaching lethal damage never dies, drops a tombstone, or loses skills. The session ends as a defeat, arena enemies despawn, original foods and Rested duration plus all allowed resources are restored, other statuses are cleared, health is set to maximum, and the player returns to the Arena Master.
- Equipment loses no durability and ammunition stacks do not decrement during a challenge. Personal food use is blocked only for the active combatant because the temporary arena loadout is active; queued players and spectators eat normally. Health, stamina, and eitr recovery consumables remain usable and their original stack counts are restored at exit. Other consumables, dropping, picking up, crafting, building, and transferring items are blocked during combat to prevent duplication.
- Arena enemies are tagged with arena ID and session ID in persistent network state. They drop no items, grant no skill experience, cannot damage structures or spectators, target only the active combatant, and are despawned at every terminal state.
- Spectators retain collision with terrain, floors, walls, and doors but have collision disabled against the combatant, arena enemies, and arena projectiles. During an active challenge they cannot damage, heal, buff, block, push, build, pick up, drop, use containers, operate mechanisms, or otherwise affect the fight. The 0.1.5 admin exception remains limited to ArenaGuard setup objects.
- Unrelated world creatures entering the combat zone are moved outside. The server distributes arena enemies through randomized shuffled bags that use every configured spawn marker before reshuffling and do not immediately repeat the last marker. Arena enemies outside the combat radius for two seconds are repositioned to a different randomized spawn marker and re-aggroed. After 60 seconds without meaningful combat they are repositioned; a further 60 seconds ends that encounter with a visible runtime-error result rather than awarding a victory.
- Mode 1 orders enabled base-level creatures by biome, difficulty, and miniboss-last. Mode 2 uses the same order but runs base, one-star, and two-star versions back-to-back for each creature. Mode 3 allows one curated creature, supported star level, and quantity from 1 through 10, and completes when that group is defeated.
- The land-biome roster begins in Meadows and excludes Ocean, bosses, passive wildlife, automatic modded-prefab discovery, and unsupported scripted creatures. Dvergr entries are explicitly hostile only to the combatant. Miniboss membership and ordering are configurable and covered by tests.
- `Gauntlet` uses every enabled populated land biome in the configured roster. `Biome` uses every eligible creature chosen from a right-side Black Forest-through-Ashlands picker. Both scopes apply independently to Biome Ladder and Star Ladder, and the immutable plan is created when the queued run is called.
- Modes 1 and 2 send global start, victory, defeat, and forfeit announcements. Mode 3 messages are arena-local. All three modes update the local HUD; live and leaderboard fighting durations use `H:MM:SS`. Only valid completed or failed server sessions may write leaderboard entries.
- Leaderboards are server-wide. Modes 1 and 2 rank fastest completions and furthest encounter reached, separated by mode and progression-cap choice. Mode 3 ranks fastest completion per creature, star level, and quantity. Every entry stores the roster revision so results remain auditable after configuration changes.
- Persist world state with atomic temporary-file replacement under the BepInEx config directory, keyed by world UID. Persist the immutable session/resource snapshot before teleporting or spawning. On every terminal path—including timeout, disconnect, runtime failure, and server restart—restore the original foods, exact remaining Rested duration, resources, ammunition, and durability before returning the interrupted combatant to the Arena Master. Restart recovery reuses the persisted immutable `ChallengePlan`, places the player first in queue, and restarts from encounter one.
- Configuration reloads may change future plans but never mutate an active plan. Invalid prefab names, duplicate normalized names, missing markers, unsupported star levels, or malformed saved state fail closed with an administrator-visible error.
- Public APIs and persisted fields use explicit version numbers. RPC packages start with a protocol version and reject unknown trailing/required shapes instead of guessing.
- `verify.ps1` must use assertions and nonzero exit codes; printing inspection output is not verification. It must never deploy to or restart the live server.

## Definition of Done

`powershell -NoProfile -ExecutionPolicy Bypass -File .\verify.ps1` passes — it builds the release solution with zero warnings, runs the deterministic core suite, validates every game/Jotunn/Harmony dependency and patched API against the installed assemblies, verifies plugin metadata and exact-version requirements, checks that no runtime dependency DLL is copied into the output or package, and validates the generated package contents.
