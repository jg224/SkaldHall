using System;
using System.Collections.Generic;
using System.Linq;
using ArenaGuard.Challenges;
using ArenaGuard.Domain;
using ArenaGuard.Rules;
using ArenaGuard.Sessions;

internal static partial class Program
{
    private static readonly List<KeyValuePair<string, Action>> Tests = new List<KeyValuePair<string, Action>>
    {
        Test("mode 1 supports gauntlet and single-biome scopes", ModeOneOrdersRoster),
        Test("mode 2 emits base, one-star, two-star back-to-back", ModeTwoOrdersStars),
        Test("mode 3 rejects malformed or unavailable selections", ModeThreeValidation),
        Test("default roster is land-only and includes Dvergr", DefaultRosterPolicy),
        Test("combat locks personal food but permits recovery consumables", ArenaConsumableAllowList),
        Test("arena food requires three distinct eligible discoveries", ArenaFoodSelection),
        Test("arena food lists follow CraftIndex stat grouping", ArenaFoodIndexGrouping),
        Test("enemy spawns use shuffled markers without immediate repeats", RandomEnemySpawnMarkers),
        Test("admin setup visuals fail closed for non-admins", AdminSetupVisualPolicy),
        Test("admin arena permissions are independent and fail closed", AdminArenaPermissionPolicy),
        Test("food inventory snapshot remains immutable until restoration", FoodSnapshotIsPreserved),
        Test("queue is FIFO", QueueIsFifo),
        Test("queue acceptance timeout moves player to back", QueueAcceptanceTimeout),
        Test("replacement queue calls reopen their prompt", ReplacementQueueCallPrompt),
        Test("staging timeout advances queue without a record", StagingTimeout),
        Test("secured staging teleports directly to Combat Start", CombatStartTeleportPolicy),
        Test("boundary grace can be cleared and later forfeits", BoundaryTimeout),
        Test("fight timer excludes preparation and results delays", FightTimerExcludesNonCombatTime),
        Test("displayed fight times use H:MM:SS", DisplayedFightTimeFormat),
        Test("restart restores and retries immutable plan first", RestartRecovery),
        Test("restart requeues pre-combat sessions without a resource snapshot", RestartRecoveryBeforeResourceCapture),
        Test("lethal damage becomes a restored defeat", LethalDamage),
        Test("manual forfeit restores and closes", ManualForfeit),
        Test("administrator recovery aborts safely and clears queues", AdministratorRecovery),
        Test("victory waits five seconds before restoration", VictoryDelay),
        Test("mode announcements respect privacy", AnnouncementPolicy),
        Test("leaderboard application failure cannot block cleanup", LeaderboardEffectsAreIndependent),
        Test("spawn failure closes as runtime error", SpawnFailure),
        Test("registry enforces normalized unique names", RegistryUniqueNames),
        Test("registry routes many entrances to exact origin then fallback", RegistryReturnRouting),
        Test("registry queue snapshots preserve FIFO order", RegistryQueueOrdering),
        Test("marker beacons save incrementally and deletions survive reload", MarkerBeaconLifecycle),
        Test("combat start cannot be saved outside the combat radius", CombatStartRadiusInvariant),
        Test("store retains only each player's best result and ranks correctly", StoreLeaderboardOrdering),
        Test("store round-trips pre-combat sessions without resource snapshots", StorePreCombatSessionRoundTrip),
        Test("store rotates five startup recovery backups", StoreRotatesStartupBackups),
        Test("store migrates legacy progression records to gauntlet scope", StoreMigratesLegacyProgression),
        Test("store rejects malformed and wrong-world files", StoreRejectsMalformedAndWrongWorld)
    };

    private static int Main()
    {
        var failures = new List<string>();
        foreach (var test in Tests)
        {
            try
            {
                test.Value();
                Console.WriteLine("PASS  " + test.Key);
            }
            catch (Exception exception)
            {
                failures.Add(test.Key + ": " + exception.Message);
                Console.Error.WriteLine("FAIL  " + test.Key);
                Console.Error.WriteLine(exception);
            }
        }

        Console.WriteLine();
        Console.WriteLine((Tests.Count - failures.Count) + "/" + Tests.Count + " core tests passed.");
        if (failures.Count == 0)
        {
            return 0;
        }

        Console.Error.WriteLine("Failures:");
        foreach (var failure in failures)
        {
            Console.Error.WriteLine("- " + failure);
        }

        return 1;
    }

    private static void AdminSetupVisualPolicy()
    {
        False(ArenaAdminVisualPolicy.ShouldShow(false, false),
            "A non-admin must not see setup visuals when the local toggle is disabled.");
        False(ArenaAdminVisualPolicy.ShouldShow(false, true),
            "A non-admin must not see setup visuals even when the local toggle is enabled.");
        False(ArenaAdminVisualPolicy.ShouldShow(true, false),
            "An admin must be able to hide setup visuals locally.");
        True(ArenaAdminVisualPolicy.ShouldShow(true, true),
            "An authenticated admin with the toggle enabled should see setup visuals.");
    }

    private static void AdminArenaPermissionPolicy()
    {
        False(ArenaAdminPermissionPolicy.CanBuild(false, false, true),
            "A non-admin cannot use the admin building permission.");
        False(ArenaAdminPermissionPolicy.CanBuild(true, false, false),
            "Disabled admin building must fail closed.");
        False(ArenaAdminPermissionPolicy.CanBuild(true, true, true),
            "An active combatant cannot use admin building privileges.");
        True(ArenaAdminPermissionPolicy.CanBuild(true, false, true),
            "An authenticated non-combatant admin may build when enabled.");

        False(ArenaAdminPermissionPolicy.CanModifyTerrain(true, false, true, false),
            "Terrain editing additionally requires local devcommands.");
        True(ArenaAdminPermissionPolicy.CanModifyTerrain(true, false, true, true),
            "An authenticated non-combatant admin may edit terrain when both switches are enabled.");

        True(ArenaAdminPermissionPolicy.CanPickup(ArenaRole.Visitor, false, false),
            "Visitors outside arena roles retain normal pickup behavior.");
        False(ArenaAdminPermissionPolicy.CanPickup(ArenaRole.Spectator, false, true),
            "A non-admin spectator cannot pick up items.");
        False(ArenaAdminPermissionPolicy.CanPickup(ArenaRole.Spectator, true, false),
            "Disabled admin pickup must fail closed.");
        True(ArenaAdminPermissionPolicy.CanPickup(ArenaRole.Spectator, true, true),
            "An authenticated admin spectator may pick up items when enabled.");
        False(ArenaAdminPermissionPolicy.CanPickup(ArenaRole.Combatant, true, true),
            "An active combatant cannot use admin pickup privileges.");
    }

    private static void ModeOneOrdersRoster()
    {
        var roster = new[]
        {
            Creature("swamp_bossy", BiomeTier.Swamp, 1, true),
            Creature("forest_hard", BiomeTier.BlackForest, 30),
            Creature("meadows", BiomeTier.Meadows, 5),
            Creature("forest_mini", BiomeTier.BlackForest, 1, true),
            Creature("forest_easy", BiomeTier.BlackForest, 10),
            Creature("swamp_easy", BiomeTier.Swamp, 10)
        };
        var catalog = new ChallengeCatalog(roster, 42);
        var request = Request(1, ChallengeMode.BiomeLadder, ProgressionCapMode.Biome);
        request.SelectedBiome = BiomeTier.BlackForest;

        var plan = catalog.BuildPlan(request, BiomeTier.BlackForest);

        SequenceEqual(new[] { "forest_easy", "forest_hard", "forest_mini" },
            plan.Encounters.Select(encounter => encounter.CreatureKey));
        SequenceEqual(new[] { 0, 1, 2 }, plan.Encounters.Select(encounter => encounter.Sequence));
        True(plan.Encounters.All(encounter => encounter.Stars == StarLevel.Base), "Mode 1 must use base stars.");
        True(plan.Encounters.All(encounter => encounter.Quantity == 1), "Mode 1 must spawn one creature.");
        True(plan.Encounters.All(encounter => encounter.PreparationSeconds == 3), "Prep must default to three seconds.");
        Equal(BiomeTier.BlackForest, plan.HighestBiome);
        Equal(42L, plan.RosterRevision);

        request.CapMode = ProgressionCapMode.Gauntlet;
        plan = catalog.BuildPlan(request, BiomeTier.Meadows);
        Equal(BiomeTier.Swamp, plan.HighestBiome);
        Equal(6, plan.Encounters.Count);
    }

    private static void ModeTwoOrdersStars()
    {
        var catalog = new ChallengeCatalog(new[]
        {
            Creature("first", BiomeTier.Meadows, 1),
            Creature("second", BiomeTier.Meadows, 2),
            Creature("third", BiomeTier.Swamp, 1)
        }, 1);

        ChallengeRequest request = Request(1, ChallengeMode.StarLadder, ProgressionCapMode.Biome);
        request.SelectedBiome = BiomeTier.Meadows;
        var plan = catalog.BuildPlan(request, BiomeTier.Ashlands);
        SequenceEqual(
            new[]
            {
                "first:Base", "first:OneStar", "first:TwoStar",
                "second:Base", "second:OneStar", "second:TwoStar"
            },
            plan.Encounters.Select(encounter => encounter.CreatureKey + ":" + encounter.Stars));

        request.CapMode = ProgressionCapMode.Gauntlet;
        plan = catalog.BuildPlan(request, BiomeTier.Meadows);
        Equal(9, plan.Encounters.Count);
        Equal("third", plan.Encounters[6].CreatureKey);
    }

    private static void ModeThreeValidation()
    {
        var supported = Creature("supported", BiomeTier.Meadows, 1);
        var baseOnly = Creature("base_only", BiomeTier.Meadows, 2);
        baseOnly.SupportedStars = new List<StarLevel> { StarLevel.Base };
        var disabled = Creature("disabled", BiomeTier.Meadows, 3);
        disabled.Enabled = false;
        var catalog = new ChallengeCatalog(new[] { supported, baseOnly, disabled }, 1);

        False(catalog.ValidateCustomSelection(null), "Null selection must fail.");
        False(catalog.ValidateCustomSelection(Selection("supported", StarLevel.Base, 0)), "Zero quantity must fail.");
        False(catalog.ValidateCustomSelection(Selection("supported", StarLevel.Base, 11)), "Quantity above ten must fail.");
        False(catalog.ValidateCustomSelection(Selection("missing", StarLevel.Base, 1)), "Unknown creature must fail.");
        False(catalog.ValidateCustomSelection(Selection("disabled", StarLevel.Base, 1)), "Disabled creature must fail.");
        False(catalog.ValidateCustomSelection(Selection("base_only", StarLevel.OneStar, 1)), "Unsupported stars must fail.");
        True(catalog.ValidateCustomSelection(Selection(" SUPPORTED ", StarLevel.TwoStar, 10)),
            "Keys should be trimmed and case-insensitive.");

        var request = Request(1, ChallengeMode.CustomEncounter);
        request.CustomSelection = Selection("supported", StarLevel.TwoStar, 10);
        var plan = catalog.BuildPlan(request, BiomeTier.Meadows);
        Equal(1, plan.Encounters.Count);
        Equal(10, plan.Encounters[0].Quantity);
        Equal(StarLevel.TwoStar, plan.Encounters[0].Stars);

        request.CustomSelection = Selection("supported", StarLevel.Base, 11);
        Throws<ArgumentException>(() => catalog.BuildPlan(request, BiomeTier.Meadows));
    }

    private static void CombatStartTeleportPolicy()
    {
        False(ArenaStartPolicy.ShouldTeleportToCombatStart(SessionPhase.Called, true),
            "A player who has not accepted must not enter combat.");
        False(ArenaStartPolicy.ShouldTeleportToCombatStart(SessionPhase.Staging, false),
            "Combat teleport must wait for the immutable resource snapshot.");
        True(ArenaStartPolicy.ShouldTeleportToCombatStart(SessionPhase.Staging, true),
            "A secured staging player must teleport directly to Combat Start without walking across the floor.");
        False(ArenaStartPolicy.ShouldBeginCountdown(SessionPhase.Staging, true, false),
            "Countdown must not begin until the server confirms arrival at Combat Start.");
        True(ArenaStartPolicy.ShouldBeginCountdown(SessionPhase.Staging, true, true),
            "A secured player confirmed at Combat Start may begin the countdown.");
        False(ArenaStartPolicy.ShouldTeleportToCombatStart(SessionPhase.Countdown, true),
            "Combat Start teleport must happen only once.");
    }

    private static void DefaultRosterPolicy()
    {
        var roster = ChallengeCatalog.LoadRoster();
        False(roster.Any(creature => creature.Biome == BiomeTier.Meadows),
            "Meadows and Greylings must be excluded from the current arena roster.");
        False(roster.Any(creature => creature.CreatureKey == "greyling"),
            "Greyling must not remain as a hidden selectable encounter.");
        True(roster.Any(creature => creature.CreatureKey == "dvergr"), "Dvergr rogue is required.");
        True(roster.Any(creature => creature.CreatureKey == "dvergr_fire_mage"), "Dvergr fire mage is required.");
        True(roster.Any(creature => creature.CreatureKey == "dvergr_ice_mage"), "Dvergr ice mage is required.");
        True(roster.Any(creature => creature.CreatureKey == "dvergr_support_mage"), "Dvergr support mage is required.");
        True(roster.Any(creature => creature.CreatureKey == "ashlands_dvergr"), "Ashlands Dvergr is required.");
        False(roster.Any(creature => creature.Biome == BiomeTier.DeepNorth), "Unpopulated Deep North must be excluded.");
        False(roster.Any(creature => creature.CreatureKey.IndexOf("boss", StringComparison.OrdinalIgnoreCase) >= 0),
            "Boss-prefixed entries must not be present.");
        Equal("brenna,geirrhafa,lord_reto,zil_and_thungr",
            string.Join(",", roster.Where(creature => creature.IsMiniboss)
                .Select(creature => creature.CreatureKey)
                .OrderBy(key => key, StringComparer.Ordinal)));
        Equal("Skeleton_Hildir", roster.Single(creature => creature.CreatureKey == "brenna").PrefabName);
        Equal("Fenring_Cultist_Hildir", roster.Single(creature => creature.CreatureKey == "geirrhafa").PrefabName);
        Equal("Charred_Melee_Dyrnwyn", roster.Single(creature => creature.CreatureKey == "lord_reto").PrefabName);
        var duo = roster.Single(creature => creature.CreatureKey == "zil_and_thungr");
        Equal("GoblinShaman_Hildir_nochest", duo.PrefabName);
        Equal("GoblinBrute_Hildir", duo.SecondaryPrefabName);
        True(roster.All(creature => !string.IsNullOrWhiteSpace(creature.IconPrefabName)),
            "Every selectable creature must have an in-game icon fallback.");
        True(roster.GroupBy(creature => creature.Biome)
                .Where(group => group.Any(creature => creature.IsMiniboss))
                .All(group => group.Last().IsMiniboss),
            "Configured minibosses should be the final entries in their biomes.");
    }

    private static void ArenaConsumableAllowList()
    {
        False(ArenaConsumablePolicy.IsAllowed(true, true, false, false, false),
            "Personal food must be locked after the player becomes the combatant.");
        True(ArenaConsumablePolicy.IsAllowed(true, false, true, false, false),
            "Health recovery must remain usable.");
        True(ArenaConsumablePolicy.IsAllowed(true, false, false, true, false),
            "Stamina recovery must remain usable.");
        True(ArenaConsumablePolicy.IsAllowed(true, false, false, false, true),
            "Eitr recovery must remain usable.");
        False(ArenaConsumablePolicy.IsAllowed(true, false, false, false, false),
            "Utility consumables must remain blocked.");
        False(ArenaConsumablePolicy.IsAllowed(false, true, true, true, true),
            "A non-consumable item must never pass the consumable allow-list.");
    }

    private static void ArenaFoodSelection()
    {
        var eligible = new HashSet<string>(StringComparer.Ordinal)
        {
            "FoodCookedDeer",
            "FoodCookedWolf",
            "FoodMagecap",
            "FoodBread"
        };
        True(ArenaFoodSelectionPolicy.IsValid(
                new[] { "FoodCookedDeer", "FoodCookedWolf", "FoodMagecap" }, eligible),
            "Three distinct discovered foods should form a valid arena loadout.");
        False(ArenaFoodSelectionPolicy.IsValid(new[] { "FoodCookedDeer", "FoodCookedWolf" }, eligible),
            "Fewer than three foods must not start an arena.");
        False(ArenaFoodSelectionPolicy.IsValid(
                new[] { "FoodCookedDeer", "FoodCookedDeer", "FoodMagecap" }, eligible),
            "Duplicate foods must be rejected.");
        False(ArenaFoodSelectionPolicy.IsValid(
                new[] { "FoodCookedDeer", "FoodCookedWolf", "FoodUnknown" }, eligible),
            "Unknown foods must be rejected.");
    }

    private static void ArenaFoodIndexGrouping()
    {
        Equal(ArenaFoodTab.Health, ArenaFoodIndexPolicy.Classify(100f, 34f, 0f));
        Equal(ArenaFoodTab.Stamina, ArenaFoodIndexPolicy.Classify(32f, 95f, 0f));
        Equal(ArenaFoodTab.Eitr, ArenaFoodIndexPolicy.Classify(30f, 15f, 90f));
        True(ArenaFoodIndexPolicy.IsBalanced(33f, 33f, 0f),
            "Near-even food should retain CraftIndex's balanced classification.");
        Equal(ArenaFoodTab.Health, ArenaFoodIndexPolicy.Classify(33f, 33f, 0f));
        Equal(ArenaFoodTab.Stamina, ArenaFoodIndexPolicy.Classify(23f, 25f, 0f));
        Equal(100f, ArenaFoodIndexPolicy.Strength(ArenaFoodTab.Health, 100f, 34f, 0f));
        Equal(95f, ArenaFoodIndexPolicy.Strength(ArenaFoodTab.Stamina, 32f, 95f, 0f));
        Equal(90f, ArenaFoodIndexPolicy.Strength(ArenaFoodTab.Eitr, 30f, 15f, 90f));
    }

    private static void RandomEnemySpawnMarkers()
    {
        IList<int> order = ArenaSpawnSelectionPolicy.BuildOrder(4, 12, 12345, 0);
        Equal(12, order.Count);
        True(order.All(marker => marker >= 0 && marker < 4),
            "Every selected marker must be one of the four configured spawns.");
        False(order[0] == 0, "A new encounter must not immediately repeat the previous spawn marker.");
        for (int index = 1; index < order.Count; index++)
        {
            False(order[index] == order[index - 1],
                "Adjacent spawned enemies must not reuse the same marker.");
        }
        for (int start = 0; start < order.Count; start += 4)
        {
            Equal(4, order.Skip(start).Take(4).Distinct().Count());
        }
    }

    private static void DisplayedFightTimeFormat()
    {
        Equal("0:00:00", ArenaDurationFormatter.FormatHms(-1));
        Equal("0:00:00", ArenaDurationFormatter.FormatHms(999));
        Equal("0:00:01", ArenaDurationFormatter.FormatHms(1000));
        Equal("0:05:09", ArenaDurationFormatter.FormatHms(309999));
        Equal("1:00:00", ArenaDurationFormatter.FormatHms(3600000));
        Equal("27:04:05", ArenaDurationFormatter.FormatHms(97445000));
    }

    private static void FoodSnapshotIsPreserved()
    {
        var fixture = new EngineFixture();
        fixture.Enqueue(1);
        True(fixture.Engine.AcceptTurn(1), "Player should accept their turn.");
        var snapshot = Snapshot(1);
        snapshot.AllowedConsumableCounts["MeadHealthMedium"] = 3;
        snapshot.RestedRemainingSeconds = 417f;
        snapshot.ArenaFoodPrefabNames.AddRange(new[] { "FoodCookedDeer", "FoodCookedWolf", "FoodMagecap" });
        True(fixture.Engine.EnterCombatFloor(1, snapshot), "Player should enter with a food inventory snapshot.");

        snapshot.AllowedConsumableCounts["MeadHealthMedium"] = 0;
        snapshot.RestedRemainingSeconds = 0f;
        snapshot.ArenaFoodPrefabNames.Clear();
        Equal(3, fixture.Active().ResourceSnapshot.AllowedConsumableCounts["MeadHealthMedium"]);
        Equal(417f, fixture.Active().ResourceSnapshot.RestedRemainingSeconds);
        Equal(3, fixture.Active().ResourceSnapshot.ArenaFoodPrefabNames.Count);

        fixture.Engine.DrainEffects();
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        fixture.Engine.Tick(fixture.Clock.UtcNow);
        string sessionId = fixture.Active().SessionId;
        True(fixture.Engine.ReportLethalDamage(1), "Terminal cleanup should begin.");

        ArenaSession ended;
        True(fixture.Engine.TryGetSession(sessionId, out ended),
            "Terminal session must remain available to the restoration effect.");
        Equal(3, ended.ResourceSnapshot.AllowedConsumableCounts["MeadHealthMedium"]);
        Equal(417f, ended.ResourceSnapshot.RestedRemainingSeconds);
        var effects = fixture.Engine.DrainEffects().ToList();
        int restoreIndex = effects.FindIndex(effect => effect.Type == ArenaEffectType.RestorePlayerState);
        int persistIndex = effects.FindLastIndex(effect => effect.Type == ArenaEffectType.PersistState);
        True(restoreIndex >= 0 && persistIndex > restoreIndex,
            "Restoration must be registered before final state persistence.");
    }

    private static void QueueIsFifo()
    {
        var fixture = new EngineFixture();
        fixture.Enqueue(1);
        fixture.Enqueue(2);
        fixture.Enqueue(3);
        Equal(1L, fixture.Active().Request.PlayerId);

        True(fixture.Engine.AcceptTurn(1), "First player should accept.");
        True(fixture.Engine.Forfeit(1), "First player should leave staging.");
        Equal(2L, fixture.Active().Request.PlayerId);

        True(fixture.Engine.AcceptTurn(2), "Second player should accept.");
        True(fixture.Engine.Forfeit(2), "Second player should leave staging.");
        Equal(3L, fixture.Active().Request.PlayerId);
    }

    private static void QueueAcceptanceTimeout()
    {
        var fixture = new EngineFixture();
        fixture.Enqueue(1);
        fixture.Enqueue(2);
        fixture.Engine.DrainEffects();

        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        fixture.Engine.Tick(fixture.Clock.UtcNow);

        Equal(2L, fixture.Active().Request.PlayerId);
        SequenceEqual(new[] { 1L }, fixture.Engine.GetQueueSnapshot().Select(entry => entry.Request.PlayerId));
        True(fixture.Engine.DrainEffects().Any(effect =>
            effect.PlayerId == 1 && effect.Message.IndexOf("back of the queue", StringComparison.OrdinalIgnoreCase) >= 0),
            "Timed-out player should be notified.");
    }

    private static void ReplacementQueueCallPrompt()
    {
        var fixture = new EngineFixture();
        fixture.Enqueue(1);
        string firstSessionId = fixture.Active().SessionId;
        fixture.Engine.DrainEffects();
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        fixture.Engine.Tick(fixture.Clock.UtcNow);
        ArenaSession replacement = fixture.Active();
        Equal(1L, replacement.Request.PlayerId);
        Equal(SessionPhase.Called, replacement.Phase);
        False(string.Equals(firstSessionId, replacement.SessionId, StringComparison.Ordinal),
            "A requeued request must receive a distinct authoritative call session.");

        False(ArenaQueuePromptPolicy.ShouldOpen(false, null, "session-1"),
            "A call for another player must not open locally.");
        True(ArenaQueuePromptPolicy.ShouldOpen(true, null, "session-1"),
            "The first local call must open.");
        False(ArenaQueuePromptPolicy.ShouldOpen(true, "session-1", "session-1"),
            "Repeated snapshots for one call must not recreate its prompt.");
        True(ArenaQueuePromptPolicy.ShouldOpen(true, "session-1", "session-2"),
            "A replacement session after timeout must reopen the prompt.");
        True(ArenaQueuePromptPolicy.ShouldOpen(true, firstSessionId, replacement.SessionId),
            "The engine's replacement call must reopen the client's prompt.");
        False(ArenaQueuePromptPolicy.ShouldOpen(true, "session-1", string.Empty),
            "A malformed call without authoritative identity must fail closed.");
    }

    private static void StagingTimeout()
    {
        var fixture = new EngineFixture();
        fixture.Enqueue(1);
        fixture.Enqueue(2);
        True(fixture.Engine.AcceptTurn(1), "First player should accept.");
        fixture.Engine.DrainEffects();
        PlayerResourceSnapshot prepared = Snapshot(1);
        prepared.ArenaFoodPrefabNames.AddRange(new[] { "FoodCookedDeer", "FoodCookedWolf", "FoodMagecap" });
        True(fixture.Engine.SetResourceSnapshot(1, prepared), "Prepared arena food should be secured in staging.");
        False(fixture.Engine.SetResourceSnapshot(1, prepared),
            "A secured pre-challenge snapshot must be immutable against replay or replacement.");
        fixture.Engine.DrainEffects();

        fixture.Clock.Advance(TimeSpan.FromSeconds(60));
        fixture.Engine.Tick(fixture.Clock.UtcNow);
        var effects = fixture.Engine.DrainEffects();

        Equal(2L, fixture.Active().Request.PlayerId);
        True(effects.Any(effect => effect.Type == ArenaEffectType.MoveToSpectatorArea && effect.PlayerId == 1),
            "Timed-out player must return to spectator area.");
        True(effects.Any(effect => effect.Type == ArenaEffectType.RestorePlayerState && effect.PlayerId == 1),
            "A timed-out prepared loadout must restore the player's original food and Rested state.");
        False(effects.Any(effect => effect.Type == ArenaEffectType.UpdateLeaderboard && effect.PlayerId == 1),
            "A challenge that never started must not write a result.");
    }

    private static void BoundaryTimeout()
    {
        var fixture = new EngineFixture();
        fixture.Start(1);
        fixture.Engine.DrainEffects();

        True(fixture.Engine.ReportBoundaryState(1, true), "Outside state should be accepted.");
        fixture.Clock.Advance(TimeSpan.FromSeconds(4));
        fixture.Engine.Tick(fixture.Clock.UtcNow);
        Equal(SessionPhase.Fighting, fixture.Active().Phase);

        True(fixture.Engine.ReportBoundaryState(1, false), "Returning should clear warning.");
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        fixture.Engine.Tick(fixture.Clock.UtcNow);
        Equal(SessionPhase.Fighting, fixture.Active().Phase);

        fixture.Engine.ReportBoundaryState(1, true);
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        fixture.Engine.Tick(fixture.Clock.UtcNow);
        ArenaSession ended;
        True(fixture.Engine.TryGetSession(fixture.SessionId, out ended), "Ended session should remain inspectable.");
        Equal(SessionOutcome.BoundaryForfeit, ended.Outcome);
        True(fixture.Engine.DrainEffects().Any(effect => effect.Type == ArenaEffectType.RestorePlayerState),
            "Boundary forfeit must restore state.");
    }

    private static void FightTimerExcludesNonCombatTime()
    {
        var fixture = new EngineFixture();
        fixture.Start(1);
        Equal(0L, fixture.Active().ElapsedMilliseconds);

        fixture.Clock.Advance(TimeSpan.FromMilliseconds(2250));
        fixture.Engine.Tick(fixture.Clock.UtcNow);
        Equal(2250L, fixture.Active().ElapsedMilliseconds);

        True(fixture.Engine.ReportEncounterCleared(fixture.SessionId), "The encounter should complete.");
        Equal(SessionPhase.Victory, fixture.Active().Phase);
        Equal(2250L, fixture.Active().ElapsedMilliseconds);

        fixture.Clock.Advance(TimeSpan.FromSeconds(4));
        fixture.Engine.Tick(fixture.Clock.UtcNow);
        Equal(2250L, fixture.Active().ElapsedMilliseconds);
    }

    private static void RestartRecovery()
    {
        var fixture = new EngineFixture();
        var immutablePlan = fixture.MakePlan(Request(1));
        immutablePlan.PlanId = "persisted-plan";
        immutablePlan.Encounters.Add(new EncounterDefinition
        {
            Sequence = 1,
            CreatureKey = "second",
            Biome = BiomeTier.Meadows,
            Stars = StarLevel.Base,
            Quantity = 1,
            PreparationSeconds = 3
        });
        var interrupted = new ArenaSession
        {
            SessionId = "old-session",
            ArenaId = "arena",
            Request = Request(1),
            Plan = immutablePlan,
            Phase = SessionPhase.Fighting,
            EncounterIndex = 1,
            SpawnedEnemyIds = new List<string> { "enemy" },
            ResourceSnapshot = Snapshot(1),
            SessionStartedUtc = fixture.Clock.UtcNow.AddSeconds(-20)
        };
        var queued = Entry(2);
        queued.QueueSequence = 10;
        var state = new PersistedWorldState
        {
            Queue = new List<QueueEntry> { queued },
            InterruptedSessions = new List<ArenaSession> { interrupted }
        };

        fixture.Engine.RecoverAfterRestart(state);
        var recovered = fixture.Active();
        Equal(1L, recovered.Request.PlayerId);
        Equal(SessionPhase.Called, recovered.Phase);
        Equal("persisted-plan", recovered.Plan.PlanId);
        Equal(0, recovered.EncounterIndex);
        Equal(2, recovered.Plan.Encounters.Count);
        var effects = fixture.Engine.DrainEffects();
        True(effects.Any(effect => effect.Type == ArenaEffectType.RestorePlayerState && effect.SessionId == "old-session"),
            "Interrupted resources must be restored.");
        True(effects.Any(effect => effect.Type == ArenaEffectType.MoveToStaging && effect.SessionId == "old-session"),
            "Interrupted player must return to staging.");

        fixture.Engine.AcceptTurn(1);
        fixture.Engine.Forfeit(1);
        Equal(2L, fixture.Active().Request.PlayerId);
    }

    private static void RestartRecoveryBeforeResourceCapture()
    {
        foreach (var phase in new[] { SessionPhase.Called, SessionPhase.Staging })
        {
            var fixture = new EngineFixture();
            var interrupted = new ArenaSession
            {
                SessionId = "old-session-" + phase,
                ArenaId = "arena",
                Request = Request(1),
                Plan = fixture.MakePlan(Request(1)),
                Phase = phase,
                EncounterIndex = 0,
                SpawnedEnemyIds = new List<string>(),
                ResourceSnapshot = null
            };

            fixture.Engine.RecoverAfterRestart(new PersistedWorldState
            {
                InterruptedSessions = new List<ArenaSession> { interrupted }
            });

            var recovered = fixture.Active();
            Equal(1L, recovered.Request.PlayerId);
            Equal(SessionPhase.Called, recovered.Phase);
            Equal(interrupted.Plan.PlanId, recovered.Plan.PlanId);
            var effects = fixture.Engine.DrainEffects();
            False(effects.Any(effect =>
                    effect.Type == ArenaEffectType.RestorePlayerState &&
                    effect.SessionId == interrupted.SessionId),
                "A pre-combat session has no player snapshot to restore.");
            True(effects.Any(effect =>
                    effect.Type == ArenaEffectType.MoveToStaging &&
                    effect.SessionId == interrupted.SessionId),
                "An interrupted pre-combat player must still return to staging.");
        }
    }

    private static void LethalDamage()
    {
        var fixture = new EngineFixture();
        fixture.Start(1);
        fixture.Engine.DrainEffects();

        True(fixture.Engine.ReportLethalDamage(1), "Lethal damage must be intercepted for active combatant.");
        ArenaSession ended;
        fixture.Engine.TryGetSession(fixture.SessionId, out ended);
        Equal(SessionOutcome.Defeat, ended.Outcome);
        Equal(SessionPhase.Defeat, ended.Phase);
        var effects = fixture.Engine.DrainEffects();
        ContainsEffects(effects,
            ArenaEffectType.DespawnSessionEnemies,
            ArenaEffectType.RestorePlayerState,
            ArenaEffectType.MoveToStaging,
            ArenaEffectType.UpdateLeaderboard);
        False(fixture.Engine.ReportLethalDamage(1), "Closed combatant must no longer be intercepted by this session.");
    }

    private static void ManualForfeit()
    {
        var fixture = new EngineFixture();
        fixture.Start(1);
        fixture.Engine.DrainEffects();

        True(fixture.Engine.Forfeit(1), "Active player should be able to forfeit.");
        ArenaSession ended;
        fixture.Engine.TryGetSession(fixture.SessionId, out ended);
        Equal(SessionOutcome.Forfeit, ended.Outcome);
        ContainsEffects(fixture.Engine.DrainEffects(),
            ArenaEffectType.RestorePlayerState,
            ArenaEffectType.MoveToStaging,
            ArenaEffectType.UpdateLeaderboard);
    }

    private static void AdministratorRecovery()
    {
        var fixture = new EngineFixture();
        fixture.Start(1);
        fixture.Engine.DrainEffects();
        fixture.Enqueue(2);
        fixture.Enqueue(3);
        fixture.Engine.DrainEffects();

        Equal(2, fixture.Engine.ClearQueue("arena"));
        True(fixture.Engine.AbortArena("arena"), "The active arena should be abortable by an administrator.");
        False(fixture.Engine.TryGetActiveSession("arena", out _),
            "An administrator abort must close the active session without calling another player.");
        ContainsEffects(fixture.Engine.DrainEffects(),
            ArenaEffectType.DespawnSessionEnemies,
            ArenaEffectType.RestorePlayerState,
            ArenaEffectType.MoveToStaging,
            ArenaEffectType.SetArenaRole,
            ArenaEffectType.PersistState);

        var preCapture = new EngineFixture();
        preCapture.Enqueue(4);
        preCapture.Engine.DrainEffects();
        True(preCapture.Engine.AbortArena("arena"), "A called player should also be recoverable.");
        False(preCapture.Engine.DrainEffects().Any(effect =>
                effect.Type == ArenaEffectType.RestorePlayerState),
            "A pre-capture abort must not request restoration from a nonexistent snapshot.");
    }

    private static void VictoryDelay()
    {
        var fixture = new EngineFixture();
        fixture.Start(1);
        fixture.Engine.DrainEffects();

        True(fixture.Engine.ReportEncounterCleared(fixture.SessionId), "Encounter clear should be accepted.");
        Equal(SessionPhase.Victory, fixture.Active().Phase);
        var victoryEffects = fixture.Engine.DrainEffects();
        True(victoryEffects.Any(effect => effect.Type == ArenaEffectType.UpdateLeaderboard), "Victory should write a record.");
        False(victoryEffects.Any(effect => effect.Type == ArenaEffectType.RestorePlayerState),
            "Restoration should wait for the results display.");

        fixture.Clock.Advance(TimeSpan.FromSeconds(4));
        fixture.Engine.Tick(fixture.Clock.UtcNow);
        Equal(SessionPhase.Victory, fixture.Active().Phase);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        fixture.Engine.Tick(fixture.Clock.UtcNow);
        ArenaSession ignored;
        False(fixture.Engine.TryGetActiveSession("arena", out ignored), "Victory should close after five seconds.");
        ContainsEffects(fixture.Engine.DrainEffects(),
            ArenaEffectType.DespawnSessionEnemies,
            ArenaEffectType.RestorePlayerState,
            ArenaEffectType.MoveToStaging);
    }

    private static void AnnouncementPolicy()
    {
        var ladder = new EngineFixture(ChallengeMode.BiomeLadder);
        ladder.Enqueue(1);
        ladder.Engine.AcceptTurn(1);
        ladder.Engine.EnterCombatFloor(1, Snapshot(1));
        var effects = ladder.Engine.DrainEffects();
        True(effects.Any(effect => effect.Type == ArenaEffectType.SendGlobalMessage),
            "Mode 1 start should be global.");

        var custom = new EngineFixture(ChallengeMode.CustomEncounter);
        custom.Enqueue(1);
        custom.Engine.AcceptTurn(1);
        custom.Engine.EnterCombatFloor(1, Snapshot(1));
        effects = custom.Engine.DrainEffects();
        True(effects.Any(effect => effect.Type == ArenaEffectType.SendLocalMessage),
            "Mode 3 start should be local.");
        False(effects.Any(effect => effect.Type == ArenaEffectType.SendGlobalMessage),
            "Mode 3 must remain globally silent.");
    }

    private static void LeaderboardEffectsAreIndependent()
    {
        var fixture = new EngineFixture();
        fixture.Start(1);
        fixture.Engine.DrainEffects();
        fixture.Engine.ReportLethalDamage(1);
        var effects = fixture.Engine.DrainEffects();
        var leaderboard = effects.Single(effect => effect.Type == ArenaEffectType.UpdateLeaderboard);
        fixture.Engine.ApplyEffectResult(leaderboard, false);

        ArenaSession ended;
        fixture.Engine.TryGetSession(fixture.SessionId, out ended);
        Equal(SessionOutcome.Defeat, ended.Outcome);
        True(effects.Any(effect => effect.Type == ArenaEffectType.RestorePlayerState),
            "Cleanup must already be emitted independently of leaderboard storage.");
    }

    private static void SpawnFailure()
    {
        var fixture = new EngineFixture();
        fixture.Enqueue(1);
        fixture.Engine.AcceptTurn(1);
        fixture.Engine.EnterCombatFloor(1, Snapshot(1));
        fixture.Engine.DrainEffects();
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        fixture.Engine.Tick(fixture.Clock.UtcNow);
        var spawn = fixture.Engine.DrainEffects().Single(effect => effect.Type == ArenaEffectType.SpawnEncounter);
        fixture.Engine.ApplyEffectResult(spawn, false);

        ArenaSession ended;
        fixture.Engine.TryGetSession(spawn.SessionId, out ended);
        Equal(SessionOutcome.RuntimeError, ended.Outcome);
        ContainsEffects(fixture.Engine.DrainEffects(),
            ArenaEffectType.RestorePlayerState,
            ArenaEffectType.MoveToStaging,
            ArenaEffectType.UpdateLeaderboard);
    }

    private static KeyValuePair<string, Action> Test(string name, Action action)
    {
        return new KeyValuePair<string, Action>(name, action);
    }

    private static CreatureDefinition Creature(string key, BiomeTier biome, int difficulty, bool miniboss = false)
    {
        return new CreatureDefinition
        {
            CreatureKey = key,
            PrefabName = key,
            DisplayName = key,
            Biome = biome,
            DifficultyOrder = difficulty,
            IsMiniboss = miniboss,
            Enabled = true,
            SupportedStars = new List<StarLevel> { StarLevel.Base, StarLevel.OneStar, StarLevel.TwoStar }
        };
    }

    private static ChallengeRequest Request(
        long playerId,
        ChallengeMode mode = ChallengeMode.BiomeLadder,
        ProgressionCapMode cap = ProgressionCapMode.Gauntlet)
    {
        return new ChallengeRequest
        {
            RequestId = "request-" + playerId,
            ArenaId = "arena",
            PlayerId = playerId,
            PlayerName = "Player " + playerId,
            Mode = mode,
            CapMode = cap,
            SelectedBiome = BiomeTier.BlackForest,
            CustomSelection = mode == ChallengeMode.CustomEncounter
                ? Selection("test", StarLevel.Base, 1)
                : null,
            RequestedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
    }

    private static QueueEntry Entry(long playerId, ChallengeMode mode = ChallengeMode.BiomeLadder)
    {
        return new QueueEntry
        {
            Request = Request(playerId, mode),
            OriginGateId = "gate-" + playerId
        };
    }

    private static CustomEncounterSelection Selection(string creature, StarLevel stars, int quantity)
    {
        return new CustomEncounterSelection { CreatureKey = creature, Stars = stars, Quantity = quantity };
    }

    private static PlayerResourceSnapshot Snapshot(long playerId)
    {
        return new PlayerResourceSnapshot
        {
            PlayerId = playerId,
            Foods = new List<FoodStateSnapshot>(),
            ArenaFoodPrefabNames = new List<string>(),
            AllowedConsumableCounts = new Dictionary<string, int>(),
            AmmunitionCounts = new Dictionary<string, int>(),
            EquipmentDurabilityBySlot = new Dictionary<string, float>(),
            CapturedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
    }

    private static void ContainsEffects(IEnumerable<ArenaEffect> effects, params ArenaEffectType[] types)
    {
        var actual = effects.Select(effect => effect.Type).ToList();
        foreach (var type in types)
        {
            True(actual.Contains(type), "Expected effect " + type + ".");
        }
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void False(bool condition, string message)
    {
        True(!condition, message);
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException("Expected '" + expected + "' but received '" + actual + "'.");
        }
    }

    private static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        var expectedList = expected.ToList();
        var actualList = actual.ToList();
        if (!expectedList.SequenceEqual(actualList))
        {
            throw new InvalidOperationException(
                "Expected [" + string.Join(", ", expectedList) + "] but received [" + string.Join(", ", actualList) + "].");
        }
    }

    private static void Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException("Expected exception " + typeof(TException).Name + ".");
    }

    private sealed class ManualClock
    {
        public DateTime UtcNow = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        public void Advance(TimeSpan duration)
        {
            UtcNow = UtcNow.Add(duration);
        }
    }

    private sealed class EngineFixture
    {
        private readonly ChallengeMode _mode;

        public EngineFixture(ChallengeMode mode = ChallengeMode.BiomeLadder)
        {
            _mode = mode;
            Clock = new ManualClock();
            Engine = new ArenaSessionEngine(MakePlan, () => Clock.UtcNow);
        }

        public ManualClock Clock { get; }
        public ArenaSessionEngine Engine { get; }
        public string SessionId { get; private set; }

        public ChallengePlan MakePlan(ChallengeRequest request)
        {
            return new ChallengePlan
            {
                PlanId = "plan-" + request.RequestId,
                Mode = request.Mode,
                CapMode = request.CapMode,
                HighestBiome = BiomeTier.Meadows,
                RosterRevision = 7,
                Encounters = new List<EncounterDefinition>
                {
                    new EncounterDefinition
                    {
                        Sequence = 0,
                        CreatureKey = "test",
                        Biome = BiomeTier.Meadows,
                        Stars = StarLevel.Base,
                        Quantity = 1,
                        PreparationSeconds = 3
                    }
                }
            };
        }

        public void Enqueue(long playerId)
        {
            Engine.Enqueue(Entry(playerId, _mode));
        }

        public void Start(long playerId)
        {
            Enqueue(playerId);
            True(Engine.AcceptTurn(playerId), "Player should accept their turn.");
            True(Engine.EnterCombatFloor(playerId, Snapshot(playerId)), "Player should enter combat floor.");
            SessionId = Active().SessionId;
            Clock.Advance(TimeSpan.FromSeconds(3));
            Engine.Tick(Clock.UtcNow);
            Equal(SessionPhase.Fighting, Active().Phase);
        }

        public ArenaSession Active()
        {
            ArenaSession session;
            True(Engine.TryGetActiveSession("arena", out session), "Expected an active arena session.");
            return session;
        }
    }
}
