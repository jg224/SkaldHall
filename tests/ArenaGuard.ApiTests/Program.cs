using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Mono.Cecil;

internal static class Program
{
    private const string PluginGuid = "jg224.arenaguard";
    private const string PluginName = "SkaldHall";
    private const string PluginVersion = "0.0.3";
    private const string JotunnGuid = "com.jotunn.jotunn";

    private static readonly List<KeyValuePair<string, Action<TestContext>>> Tests =
        new List<KeyValuePair<string, Action<TestContext>>>
        {
            Test("plugin metadata and assembly version are exact", VerifyPluginMetadata),
            Test("Jotunn dependency and network version policy are strict", VerifyCompatibilityMetadata),
            Test("RPC transport carries an exact protocol and mod version", VerifyRpcVersionContract),
            Test("gauntlet and biome scopes cross UI, wire, leaderboard, and storage", VerifyChallengeScopes),
            Test("arena food preparation is discovered-only, temporary, and recoverable", VerifyArenaFoodPreparation),
            Test("world piece source prefabs match the installed Valheim assets", VerifyWorldPieceSources),
            Test("named minibosses and creature portraits use installed Valheim assets", VerifyCreatureAssets),
            Test("admin spectators retain the Arena Core hover interaction path", VerifyCoreInteractionPath),
            Test("admin permission toggles are server-controlled and enforced", VerifyAdminPermissions),
            Test("live-server recovery and combat boundary safety are wired", VerifyLiveServerSafety),
            Test("the Arena Master is a safe persistent challenge host and legacy signs remain loadable", VerifyChallengeAccessPieces),
            Test("runtime hot paths are cached, throttled, and allocation-free", VerifyHotPathPerformance),
            Test("arena movement preserves equipment while suppressing general speed bonuses", VerifyArenaMovementSpeedPolicy),
            Test("all required Harmony patches are present", VerifyHarmonyPatchMetadata),
            Test("all patched Valheim members exist in the installed API", VerifyInstalledPatchTargets),
            Test("runtime dependencies are referenced but never bundled", VerifyCleanOutput)
        };

    private static int Main()
    {
        TestContext context = null;
        var failures = new List<string>();
        var passed = 0;
        try
        {
            context = TestContext.Load();
            foreach (var test in Tests)
            {
                try
                {
                    test.Value(context);
                    passed++;
                    Console.WriteLine("PASS  " + test.Key);
                }
                catch (Exception exception)
                {
                    failures.Add(test.Key + ": " + exception.Message);
                    Console.Error.WriteLine("FAIL  " + test.Key);
                    Console.Error.WriteLine(exception);
                }
            }
        }
        catch (Exception exception)
        {
            failures.Add("test setup: " + exception.Message);
            Console.Error.WriteLine("FAIL  test setup");
            Console.Error.WriteLine(exception);
        }
        finally
        {
            context?.Dispose();
        }

        Console.WriteLine();
        Console.WriteLine(passed + "/" + Tests.Count + " API tests passed.");
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

    private static void VerifyPluginMetadata(TestContext context)
    {
        TypeDefinition plugin = RequireType(context.Mod, "ArenaGuard.Plugin");
        CustomAttribute attribute = RequireAttribute(plugin, "BepInEx.BepInPlugin");
        Equal(3, attribute.ConstructorArguments.Count);
        Equal(PluginGuid, AttributeString(attribute, 0));
        Equal(PluginName, AttributeString(attribute, 1));
        Equal(PluginVersion, AttributeString(attribute, 2));
        Equal(new Version(0, 0, 3, 0), context.Mod.Name.Version);

        Equal(PluginGuid, ConstantString(plugin, "PluginGuid"));
        Equal(PluginName, ConstantString(plugin, "PluginName"));
        Equal(PluginVersion, ConstantString(plugin, "PluginVersion"));
    }

    private static void VerifyCompatibilityMetadata(TestContext context)
    {
        TypeDefinition plugin = RequireType(context.Mod, "ArenaGuard.Plugin");
        CustomAttribute dependency = RequireAttribute(plugin, "BepInEx.BepInDependency");
        Equal(JotunnGuid, AttributeString(dependency, 0));
        Equal("HardDependency", EnumArgumentName(context.BepInEx, dependency.ConstructorArguments[1]));

        CustomAttribute compatibility = RequireAttribute(plugin, "Jotunn.Utils.NetworkCompatibilityAttribute");
        Equal("EveryoneMustHaveMod", EnumArgumentName(context.Jotunn, compatibility.ConstructorArguments[0]));
        Equal("Patch", EnumArgumentName(context.Jotunn, compatibility.ConstructorArguments[1]));
    }

    private static void VerifyRpcVersionContract(TestContext context)
    {
        TypeDefinition rpc = RequireType(context.Mod, "ArenaGuard.Networking.ArenaRpc");
        FieldDefinition protocol = rpc.Fields.SingleOrDefault(field => field.Name == "ProtocolVersion");
        True(protocol != null && protocol.HasConstant, "ArenaRpc.ProtocolVersion must be a compile-time constant.");
        Equal(8, Convert.ToInt32(protocol.Constant, CultureInfo.InvariantCulture));
        RequireMethod(rpc, "ValidatePackageHeader");
        RequireMethod(rpc, "NewPackage");
        RequireMethod(rpc, "RequireConsumed");

        MethodDefinition validator = RequireMethod(rpc, "ValidatePackageHeader");
        True(ContainsString(validator, PluginVersion),
            "RPC header validation must compare the peer's ArenaGuard version.");
        MethodDefinition packageFactory = RequireMethod(rpc, "NewPackage");
        True(ContainsString(packageFactory, PluginVersion),
            "Every outgoing RPC package must include the ArenaGuard version.");
        MethodDefinition arrivalRequest = RequireMethod(rpc, "ReportCombatStartArrival");
        MethodDefinition arrivalHandler = RequireMethod(rpc, "OnCombatStartArrival");
        True(ContainsOperandText(arrivalRequest, "CombatStartArrivalRpc") ||
             ContainsString(arrivalRequest, PluginGuid + ".CombatStartArrived"),
            "The client must send an authenticated Combat Start arrival acknowledgement.");
        True(ContainsOperandText(arrivalHandler, "RequireConsumed") &&
             ContainsOperandText(arrivalHandler, "CombatStartArrived"),
            "The arrival acknowledgement must contain no client-selected identity or destination payload.");

        TypeDefinition clientSnapshot = RequireType(context.Mod, "ArenaGuard.Domain.ArenaClientSnapshot");
        True(clientSnapshot.Fields.Any(field => field.Name == "SessionId"),
            "Client snapshots must identify each authoritative queue call session.");
        True(ContainsOperandText(RequireMethod(rpc, "WriteArenaSnapshot"),
                 "ArenaClientSnapshot::SessionId") &&
             ContainsOperandText(RequireMethod(rpc, "ReadArenaSnapshot"),
                 "ArenaClientSnapshot::SessionId"),
            "Queue-call session identity must round-trip through arena snapshots.");
        TypeDefinition runtime = RequireType(context.Mod, "ArenaGuard.Runtime.ArenaServerRuntime");
        True(runtime.Fields.Any(field => field.Name == "ClientQueuePromptSessions") &&
             ContainsOperandText(RequireMethod(runtime, "BuildClientSnapshot"), "ArenaSession::SessionId") &&
             ContainsOperandText(RequireMethod(runtime, "OnClientSnapshotReceived"),
                 "ArenaQueuePromptPolicy::ShouldOpen") &&
             ContainsOperandText(RequireMethod(runtime, "OnClientSnapshotReceived"),
                 "ArenaUi::ShowQueueCall"),
            "Replacement queue calls must be tracked by session and displayed only after UI creation succeeds.");
        TypeDefinition ui = RequireType(context.Mod, "ArenaGuard.UI.ArenaUi");
        MethodDefinition showQueueCall = RequireMethod(ui, "ShowQueueCall");
        Equal("System.Boolean", showQueueCall.ReturnType.FullName);
        True(ContainsOperandText(showQueueCall, "ArenaUi::CanDraw") &&
             ContainsOperandText(RequireMethod(ui, "CloseQueuePromptForArena"), "ArenaUi::CloseQueuePrompt"),
            "Queue prompts must report draw success and close when their arena is no longer called.");
    }

    private static void VerifyChallengeScopes(TestContext context)
    {
        TypeDefinition scope = RequireType(context.Mod, "ArenaGuard.Domain.ProgressionCapMode");
        True(scope.Fields.Any(field => field.Name == "Gauntlet") &&
             scope.Fields.Any(field => field.Name == "Biome") &&
             !scope.Fields.Any(field => field.Name == "WorldUnlocked" || field.Name == "MaximumPossible"),
            "Progression must expose only Gauntlet and Biome scopes.");

        TypeDefinition request = RequireType(context.Mod, "ArenaGuard.Domain.ChallengeRequest");
        TypeDefinition leaderboardKey = RequireType(context.Mod, "ArenaGuard.Domain.LeaderboardKey");
        True(request.Fields.Any(field => field.Name == "SelectedBiome") &&
             leaderboardKey.Fields.Any(field => field.Name == "SelectedBiome"),
            "Challenge requests and leaderboard keys must carry the selected biome.");

        TypeDefinition catalog = RequireType(context.Mod, "ArenaGuard.Challenges.ChallengeCatalog");
        MethodDefinition buildPlan = RequireMethod(catalog, "BuildPlan");
        True(ContainsOperandText(buildPlan, "ChallengeRequest::SelectedBiome"),
            "Both ladder planners must filter by the authenticated selected biome when requested.");

        TypeDefinition ui = RequireType(context.Mod, "ArenaGuard.UI.ArenaUi");
        MethodDefinition open = RequireMethod(ui, "OpenChallengeMenu");
        True(ContainsString(open, "GAUNTLET — Every biome") &&
             ContainsString(open, "BIOME — Choose one") &&
             ContainsOperandText(open, "OpenBiomePicker") &&
             ContainsString(RequireMethod(ui, "BuildBiomePicker"), "SELECT BIOME"),
            "The native challenge menu must expose Gauntlet and a right-side Biome picker.");
        RequireType(context.Mod, "ArenaGuard.Rules.ArenaDurationFormatter");
        True(ContainsOperandText(RequireMethod(ui, "FormatTime"),
                "ArenaDurationFormatter::FormatHms"),
            "HUD and leaderboard durations must share the H:MM:SS formatter.");

        TypeDefinition rpc = RequireType(context.Mod, "ArenaGuard.Networking.ArenaRpc");
        True(ContainsOperandText(RequireMethod(rpc, "WriteChallengeRequest"), "ChallengeRequest::SelectedBiome") &&
             ContainsOperandText(RequireMethod(rpc, "ReadChallengeRequest"), "ChallengeRequest::SelectedBiome") &&
             ContainsOperandText(RequireMethod(rpc, "WriteLeaderboardKey"), "LeaderboardKey::SelectedBiome") &&
             ContainsOperandText(RequireMethod(rpc, "ReadLeaderboardKey"), "LeaderboardKey::SelectedBiome"),
            "Selected-biome scope must round-trip through challenge and leaderboard RPC payloads.");

        TypeDefinition store = RequireType(context.Mod, "ArenaGuard.Persistence.ArenaStore");
        FieldDefinition schema = store.Fields.SingleOrDefault(field => field.Name == "SchemaVersion");
        True(schema != null && schema.HasConstant && Convert.ToInt32(schema.Constant, CultureInfo.InvariantCulture) == 3 &&
             ContainsOperandText(RequireMethod(store, "Load"), "MigrateLegacyProgression"),
            "Persistence schema 3 must migrate legacy progression state before validation.");
    }

    private static void VerifyArenaFoodPreparation(TestContext context)
    {
        TypeDefinition snapshot = RequireType(context.Mod, "ArenaGuard.Domain.PlayerResourceSnapshot");
        TypeDefinition clientSnapshot = RequireType(context.Mod, "ArenaGuard.Domain.ArenaClientSnapshot");
        True(snapshot.Fields.Any(field => field.Name == "RestedRemainingSeconds") &&
             snapshot.Fields.Any(field => field.Name == "ArenaFoodPrefabNames") &&
             clientSnapshot.Fields.Any(field => field.Name == "PreparationComplete"),
            "Resource and client snapshots must carry Rested, arena-food, and preparation state.");

        TypeDefinition ui = RequireType(context.Mod, "ArenaGuard.UI.ArenaUi");
        RequireMethod(ui, "OpenFoodPreparation");
        True(ContainsOperandInType(ui, "ArenaFoodDefinition::PrefabName"),
            "The food preparation UI must bind selectable food definitions.");
        True(ContainsString(RequireMethod(ui, "BuildFoodPreparation"),
                "Choose exactly three foods your character has discovered.  Ready in "),
            "The food preparation UI must explain its mandatory three-food rule.");
        TypeDefinition foodTab = RequireType(context.Mod, "ArenaGuard.Rules.ArenaFoodTab");
        True(foodTab.Fields.Any(field => field.Name == "Health") &&
             foodTab.Fields.Any(field => field.Name == "Stamina") &&
             foodTab.Fields.Any(field => field.Name == "Eitr"),
            "Food preparation must classify Health, Stamina, and Eitr foods.");
        True(ContainsOperandInType(ui, "ArenaFoodIndexPolicy::Strength"),
            "Food lists must use the CraftIndex-compatible stat ordering policy.");
        MethodDefinition foodList = RequireMethod(ui, "CreateFoodList");
        Equal("UnityEngine.UI.ScrollRect", foodList.ReturnType.FullName);
        True(ContainsOperandText(foodList, "RectMask2D") &&
             ContainsOperandText(foodList, "Scrollbar") &&
             ContainsSingle(foodList, 300f) &&
             ContainsOperandText(foodList, "HP ") &&
             ContainsOperandText(foodList, "STAM ") &&
             ContainsOperandText(foodList, "EITR "),
            "Food preparation must show three compact stat lists with 300-point wheel scrolling.");
        True(ContainsOperandText(RequireMethod(ui, "BuildFoodPreparation"), "CreateFoodList") &&
             !ContainsString(RequireMethod(ui, "BuildFoodPreparation"), "Page "),
            "The picker must render all three lists together instead of paging one active tab.");
        True(ContainsString(RequireMethod(ui, "BuildFoodPreparation"), "Selected 0/3: none") &&
             ContainsString(RequireMethod(ui, "ToggleFoodSelection"),
                 "Remove one selected food before choosing another."),
            "Food lists must share one three-item selection across every column.");
        True(ContainsOperandText(RequireMethod(ui, "ConfirmFoodPreparation"), "FoodPreparationConfirmed"),
            "Ready must submit the selected arena food loadout.");

        TypeDefinition runtime = RequireType(context.Mod, "ArenaGuard.Runtime.ArenaServerRuntime");
        True(ContainsOperandText(RequireMethod(runtime, "GetArenaFoods"), "Player::IsKnownMaterial") &&
             ContainsOperandText(RequireMethod(runtime, "ConfirmLocalFoodPreparation"), "ArenaFoodSelectionPolicy::IsValid") &&
             ContainsOperandText(RequireMethod(runtime, "ConfirmLocalFoodPreparation"), "SubmitResourceSnapshot"),
            "The owning character must revalidate discovered foods before submitting preparation.");
        True(ContainsOperandText(RequireMethod(runtime, "OnClientSnapshotReceived"), "SubmitResourceSnapshot") &&
             RequireType(context.Mod, "ArenaGuard.Runtime.ArenaServerRuntime/ClientPreparationState")
                 .Fields.Any(field => field.Name == "SubmissionAttempts"),
            "An unacknowledged loadout submission must retry without discarding the immutable pre-arena snapshot.");
        MethodDefinition applyArenaFood = RequireMethod(runtime, "ApplyTemporaryArenaFood");
        True(ContainsOperandText(applyArenaFood, "SEMan::s_statusEffectRested") &&
             ContainsOperandText(applyArenaFood, "StatusEffect::m_ttl") &&
             ContainsOperandText(RequireMethod(runtime, "RestoreResources"), "RestoreRested"),
            "Arena food must fill resources, keep Rested for the session, and restore the original Rested state.");
        MethodDefinition restoreResources = RequireMethod(runtime, "RestoreResources");
        True(ContainsOperandText(restoreResources, "SEMan::RemoveStatusEffect") &&
             !ContainsOperandText(restoreResources, "SEMan::RemoveAllStatusEffects"),
            "Arena restoration must remove only its temporary Rested effect and preserve equipment effects such as Megingjord.");
        int restedReset = FirstOperandIndex(applyArenaFood, "StatusEffect::ResetTime");
        int restedTtl = FirstOperandIndex(applyArenaFood, "StatusEffect::m_ttl");
        True(restedReset >= 0 && restedTtl > restedReset,
            "The non-expiring arena Rested TTL must be assigned after Valheim recalculates comfort duration.");

        TypeDefinition rpc = RequireType(context.Mod, "ArenaGuard.Networking.ArenaRpc");
        True(ContainsOperandText(RequireMethod(rpc, "WriteResourceSnapshot"),
                 "PlayerResourceSnapshot::ArenaFoodPrefabNames") &&
             ContainsOperandText(RequireMethod(rpc, "ReadResourceSnapshot"),
                 "PlayerResourceSnapshot::RestedRemainingSeconds"),
            "Arena food and Rested state must round-trip through the strict resource protocol.");

        TypeDefinition engine = RequireType(context.Mod, "ArenaGuard.Sessions.ArenaSessionEngine");
        True(ContainsString(RequireMethod(engine, "HandleStagingTimeout"),
                "Restore the timed-out arena food loadout."),
            "A timed-out prepared loadout must enter the normal durable restoration path.");

        TypeDefinition consumablePatch = RequireType(context.Mod, "ArenaGuard.Rules.ArenaConsumablePatch");
        True(ContainsOperandText(RequireMethod(consumablePatch, "Postfix"), "IsCombatant") &&
             ContainsOperandText(RequireMethod(consumablePatch, "Postfix"), "IsAllowedArenaConsumable"),
            "Only the active combatant should have personal food blocked; spectators remain unaffected.");
    }

    private static void VerifyLiveServerSafety(TestContext context)
    {
        TypeDefinition plugin = RequireType(context.Mod, "ArenaGuard.Plugin");
        TypeDefinition command = RequireType(context.Mod, "ArenaGuard.Runtime.ArenaAdminCommand");
        True(command.BaseType?.FullName == "Jotunn.Entities.ConsoleCommand",
            "The ArenaGuard administrator command must use Jotunn's console command contract.");
        MethodDefinition networkProperty = RequireMethod(command, "get_IsNetwork");
        True(networkProperty.HasBody && networkProperty.Body.Instructions.Any(instruction =>
                instruction.OpCode.Code == Mono.Cecil.Cil.Code.Ldc_I4_0),
            "Recovery commands must use ArenaGuard's authenticated RPC rather than Jotunn's generic network relay.");
        True(ContainsOperandText(RequireMethod(plugin, "Awake"), "CommandManager::AddConsoleCommand"),
            "Plugin startup must register the authenticated administrator recovery command.");

        TypeDefinition engine = RequireType(context.Mod, "ArenaGuard.Sessions.ArenaSessionEngine");
        True(RequireMethod(engine, "AbortArena").IsPublic && RequireMethod(engine, "ClearQueue").IsPublic,
            "The session engine must expose deterministic abort and queue-clearing operations.");

        TypeDefinition runtime = RequireType(context.Mod, "ArenaGuard.Runtime.ArenaServerRuntime");
        MethodDefinition detection = RequireMethod(runtime, "DetectFloorEntryAndBoundary");
        MethodDefinition boundary = RequireMethod(runtime, "HandleCombatBoundary");
        MethodDefinition popup = RequireMethod(runtime, "SendPlayerPopup");
        True(ContainsOperandText(detection, "HandleCombatBoundary") &&
             ContainsOperandText(boundary, "SendPlayerPopup") &&
             ContainsString(boundary, "RETURN TO THE ARENA\n") &&
             ContainsOperandText(boundary, "BoundaryGraceSeconds") &&
             runtime.Fields.Any(field => field.Name == "CombatBoundaryStates"),
            "An out-of-bounds combatant must receive the configured countdown popup.");
        True(!ContainsOperandText(boundary, "SendPlayerMove") &&
             !ContainsOperandText(boundary, "TeleportTo"),
            "Boundary handling must not move the combatant before the grace period expires.");
        MethodDefinition runtimeTick = RequireMethod(runtime, "TickOnMainThread");
        int boundaryDetection = FirstOperandIndex(runtimeTick, "DetectFloorEntryAndBoundary");
        int engineTick = FirstOperandIndex(runtimeTick, "ArenaSessionEngine::Tick");
        True(boundaryDetection >= 0 && engineTick > boundaryDetection,
            "A last-moment return inside must be observed before the boundary deadline is evaluated.");
        True(ContainsString(popup, "ShowMessage") || ContainsOperandText(popup, "MessageHud::ShowMessage"),
            "Boundary warnings must use the in-game center popup transport.");

        MethodDefinition localMove = RequireMethod(runtime, "OnClientMoveRequested");
        int teleportCall = FirstOperandIndex(localMove, "TeleportTo");
        True(teleportCall > 0 &&
             localMove.Body.Instructions[teleportCall - 1].OpCode.Code == Mono.Cecil.Cil.Code.Ldc_I4_0,
            "Arena-internal movement must use Valheim's short non-distant teleport mode.");

        MethodDefinition elapsed = RequireMethod(engine, "UpdateElapsed");
        True(ContainsOperandText(elapsed, "ArenaSession::PhaseStartedUtc") &&
             ContainsOperandText(elapsed, "ArenaSession::ElapsedMilliseconds"),
            "Authoritative elapsed time must accumulate fighting intervals rather than wall-clock preparation time.");
        TypeDefinition ui = RequireType(context.Mod, "ArenaGuard.UI.ArenaUi");
        MethodDefinition liveTimer = RequireMethod(ui, "UpdateLiveHudTimer");
        True(ContainsOperandText(RequireMethod(ui, "DriverUpdate"), "UpdateLiveHudTimer") &&
             ContainsOperandText(liveTimer, "Time::get_unscaledTime") && ContainsSingle(liveTimer, 0.1f),
            "The HUD fight timer must interpolate at a throttled client cadence between server snapshots.");

        TypeDefinition store = RequireType(context.Mod, "ArenaGuard.Persistence.ArenaStore");
        MethodDefinition load = RequireMethod(store, "Load");
        MethodDefinition startupBackup = RequireMethod(store, "CreateStartupBackup");
        True(ContainsOperandText(load, "CreateStartupBackup") &&
             ContainsString(startupBackup, ".startup-") &&
             store.Fields.Any(field => field.Name == "MaximumStartupBackups" &&
                                       Convert.ToInt32(field.Constant, CultureInfo.InvariantCulture) == 5),
            "World load must rotate five startup recovery backups before deserialization.");
    }

    private static void VerifyWorldPieceSources(TestContext context)
    {
        TypeDefinition worldObjects = RequireType(context.Mod, "ArenaGuard.World.ArenaWorldObjects");
        Equal("sign", ConstantString(worldObjects, "SignBasePrefabName"));
        Equal("Dverger", ConstantString(worldObjects, "ChallengeHostBasePrefabName"));
        Equal("TrophyDvergr", ConstantString(worldObjects, "ChallengeHostIconPrefabName"));
        True(!ContainsStringInType(worldObjects, "piece_sign"),
            "ArenaGuard must not use the removed piece_sign prefab name.");
        True(ContainsStringInType(worldObjects, "guard_stone"),
            "Arena Core must inherit the installed guard_stone prefab.");
        True(ContainsStringInType(worldObjects, "portal_wood"),
            "Arena gates must inherit the installed portal_wood prefab.");

        string valheimRoot = Environment.GetEnvironmentVariable("VALHEIM_ROOT") ?? @"C:\ValheimServer\server";
        string manifestPath = Path.Combine(valheimRoot, "valheim_server_Data", "StreamingAssets", "SoftRef",
            "manifest_extended");
        True(File.Exists(manifestPath), "Valheim SoftRef manifest is missing: " + manifestPath);
        string manifest = File.ReadAllText(manifestPath);
        foreach (string asset in new[]
        {
            "Assets/GameElements/Pieces/sign.prefab",
            "Assets/GameElements/Pieces/guard_stone.prefab",
            "Assets/GameElements/Pieces/portal_wood.prefab",
            "Assets/Characters/Dverger/Dverger.prefab",
            "Assets/GameElements/Items/trophies/TrophyDvergr.prefab"
        })
        {
            True(manifest.IndexOf(asset, StringComparison.OrdinalIgnoreCase) >= 0,
                "Installed Valheim asset manifest is missing " + asset + ".");
        }
    }

    private static void VerifyCreatureAssets(TestContext context)
    {
        TypeDefinition creature = RequireType(context.Mod, "ArenaGuard.Domain.CreatureDefinition");
        foreach (string fieldName in new[]
                 {
                     "SecondaryPrefabName", "IconPrefabName", "SecondaryIconPrefabName"
                 })
        {
            True(creature.Fields.Any(field => field.Name == fieldName),
                "CreatureDefinition is missing " + fieldName + ".");
        }

        TypeDefinition catalog = RequireType(context.Mod, "ArenaGuard.Challenges.ChallengeCatalog");
        MethodDefinition loadRoster = RequireMethod(catalog, "LoadRoster");
        foreach (string required in new[]
                 {
                     "brenna", "Skeleton_Hildir", "geirrhafa", "Fenring_Cultist_Hildir",
                     "zil_and_thungr", "GoblinShaman_Hildir_nochest", "GoblinBrute_Hildir",
                     "lord_reto", "Charred_Melee_Dyrnwyn"
                 })
        {
            True(ContainsString(loadRoster, required), "Default roster is missing " + required + ".");
        }
        True(!ContainsString(loadRoster, "greyling") && !ContainsString(loadRoster, "Greyling"),
            "Meadows Greyling must be absent from the default roster.");

        TypeDefinition arenaUi = RequireType(context.Mod, "ArenaGuard.UI.ArenaUi");
        True(ContainsOperandText(RequireMethod(arenaUi, "ResolveCreatureIcon"), "ObjectDB::GetItemPrefab") &&
             ContainsOperandText(RequireMethod(arenaUi, "ResolveCreatureIcon"), "ItemData::GetIcon") &&
             ContainsOperandText(RequireMethod(arenaUi, "AddCreatureCard"), "ArenaUi::AddImage"),
            "Creature cards must render cached in-game item sprites.");

        TypeDefinition runtime = RequireType(context.Mod, "ArenaGuard.Runtime.ArenaServerRuntime");
        MethodDefinition spawn = runtime.Methods.Single(method => method.Name == "SpawnEncounter" &&
                                                         method.Parameters.Count == 2);
        True(ContainsOperandText(spawn, "CreatureDefinition::SecondaryPrefabName") &&
             ContainsOperandText(spawn, "DestroyEnemy"),
            "The Zil & Thungr encounter must spawn as one complete, rollback-safe duo.");
        TypeDefinition spawnPolicy = RequireType(context.Mod, "ArenaGuard.Rules.ArenaSpawnSelectionPolicy");
        RequireMethod(spawnPolicy, "BuildOrder");
        True(ContainsOperandText(spawn, "ArenaSpawnSelectionPolicy::BuildOrder") &&
             runtime.Fields.Any(field => field.Name == "LastSpawnMarkerBySessionId") &&
             ContainsOperandText(RequireMethod(runtime, "RepositionEscapedOrStuckEnemies"),
                 "ArenaSpawnSelectionPolicy::BuildOrder"),
            "Initial and corrected enemies must use fair randomized spawn-marker bags.");

        string valheimRoot = Environment.GetEnvironmentVariable("VALHEIM_ROOT") ?? @"C:\ValheimServer\server";
        string manifestPath = Path.Combine(valheimRoot, "valheim_server_Data", "StreamingAssets", "SoftRef",
            "manifest_extended");
        string manifest = File.ReadAllText(manifestPath);
        foreach (string asset in new[]
                 {
                     "Assets/Characters/Skeleton/Skeleton_Hildir.prefab",
                     "Assets/Characters/Fenring/Fenring_Cultist_Hildir.prefab",
                     "Assets/Characters/GoblinBruteBros/GoblinShaman_Hildir_nochest.prefab",
                     "Assets/Characters/GoblinBruteBros/GoblinBrute_Hildir.prefab",
                     "Assets/Characters/TheCharred/Charred_Melee_Dyrnwyn.prefab",
                     "Assets/GameElements/Items/trophies/TrophySkeletonHildir.prefab",
                     "Assets/GameElements/Items/trophies/TrophyCultist_Hildir.prefab",
                     "Assets/GameElements/Items/trophies/TrophyGoblinBruteBrosShaman.prefab",
                     "Assets/GameElements/Items/trophies/TrophyGoblinBruteBrosBrute.prefab"
                 })
        {
            True(manifest.IndexOf(asset, StringComparison.OrdinalIgnoreCase) >= 0,
                "Installed Valheim asset manifest is missing " + asset + ".");
        }
    }

    private static void VerifyCoreInteractionPath(TestContext context)
    {
        TypeDefinition core = RequireType(context.Mod, "ArenaGuard.World.ArenaCoreBehaviour");
        True(core.Interfaces.Any(item => item.InterfaceType.FullName == "Hoverable"),
            "Arena Core behaviour must implement Valheim's Hoverable interface.");
        True(core.Interfaces.Any(item => item.InterfaceType.FullName == "Interactable"),
            "Arena Core behaviour must implement Valheim's Interactable interface.");
        True(core.Methods.Any(method => method.Name == "GetHoverText" && method.Parameters.Count == 0 &&
                                      method.ReturnType.FullName == "System.String"),
            "Arena Core must provide hover text.");
        True(core.Methods.Any(method => method.Name == "Interact" && method.Parameters.Count == 3 &&
                                      method.Parameters[0].ParameterType.FullName == "Humanoid" &&
                                      method.Parameters[1].ParameterType.FullName == "System.Boolean" &&
                                      method.Parameters[2].ParameterType.FullName == "System.Boolean" &&
                                      method.ReturnType.FullName == "System.Boolean"),
            "Arena Core interaction signature no longer matches Valheim's Interactable contract.");

        TypeDefinition worldObjects = RequireType(context.Mod, "ArenaGuard.World.ArenaWorldObjects");
        MethodDefinition registerCore = RequireMethod(worldObjects, "RegisterCore");
        int removeCoreEffectArea = FirstOperandIndex(registerCore,
            "RemoveComponent<EffectArea>");
        int removeCoreColliders = FirstOperandIndex(registerCore,
            "RemoveComponent<UnityEngine.Collider>");
        True(removeCoreEffectArea >= 0 && removeCoreColliders >= 0 &&
             removeCoreEffectArea < removeCoreColliders,
            "Arena Core must remove inherited EffectArea components before their ward colliders.");
        True(ContainsOperandText(registerCore, "GameObject::AddComponent<UnityEngine.SphereCollider>"),
            "Arena Core registration must add a selectable collider.");
        True(ContainsOperandText(registerCore,
                "AddWorldBehaviour<ArenaGuard.World.ArenaCoreBehaviour>"),
            "Arena Core registration must attach its Hoverable/Interactable behaviour to the prefab root.");
        MethodDefinition validateCore = RequireMethod(worldObjects, "ValidateCoreInteractionPrefab");
        True(ContainsOperandText(validateCore, "GetComponentInChildren<PrivateArea>") &&
             ContainsOperandText(validateCore, "GetComponentInChildren<EffectArea>") &&
             ContainsString(validateCore, "Arena Core prefab retained inherited ward or EffectArea behavior."),
            "Arena Core validation must reject every surviving ward/effect-area component.");
        TypeDefinition effectArea = RequireType(context.Game, "EffectArea");
        MethodDefinition effectAreaAwake = RequireMethod(effectArea, "Awake");
        True(ContainsOperandText(effectAreaAwake, "GetComponent<UnityEngine.Collider>") &&
             ContainsOperandText(effectAreaAwake, "Collider::set_isTrigger"),
            "Installed EffectArea.Awake no longer has the collider dependency guarded by Core cleanup.");
        True(ContainsOperandText(registerCore, "CreateArenaRadiusRing"),
            "Arena Core registration must create the admin combat/protected radius indicators.");
        MethodDefinition createRadiusRing = RequireMethod(worldObjects, "CreateArenaRadiusRing");
        True(ContainsOperandText(createRadiusRing, "GameObject::AddComponent<UnityEngine.LineRenderer>") &&
             ContainsOperandText(createRadiusRing, "LineRenderer::set_loop") &&
             ContainsString(createRadiusRing, "piece_nonsolid") &&
             !ContainsOperandText(createRadiusRing, "Collider"),
            "Radius rings must use non-solid looped LineRenderers with no colliders.");
        MethodDefinition radiusVisibility = RequireMethod(core, "ApplyRadiusRingVisibility");
        MethodDefinition applyRadiusRing = RequireMethod(core, "ApplyRadiusRing");
        True(ContainsOperandText(radiusVisibility, "ArenaDefinitionResolver") &&
             ContainsOperandText(radiusVisibility, "ArenaDefinition::CombatRadius") &&
             ContainsOperandText(radiusVisibility, "ArenaDefinition::ProtectedRadius") &&
             ContainsOperandText(applyRadiusRing, "LineRenderer::SetPosition") &&
             ContainsOperandText(applyRadiusRing, "Renderer::set_enabled") &&
             ContainsOperandText(applyRadiusRing, "MaterialPropertyBlock::SetColor"),
            "Only admins must see correctly sized and colored combat/protected radius rings.");
        TypeDefinition visualPolicy = RequireType(context.Mod, "ArenaGuard.Rules.ArenaAdminVisualPolicy");
        MethodDefinition shouldShowPolicy = RequireMethod(visualPolicy, "ShouldShow");
        MethodDefinition shouldShowWorld = RequireMethod(worldObjects, "ShouldShowAdminSetupVisuals");
        True(shouldShowPolicy.Parameters.Count == 2 &&
             ContainsOperandText(shouldShowWorld, "ArenaWorldObjects::IsLocalAdmin") &&
             ContainsOperandText(shouldShowWorld, "ArenaAdminVisualPolicy::ShouldShow") &&
             ContainsOperandText(RequireMethod(core, "ApplyAdminVisibility"),
                 "ArenaWorldObjects::ShouldShowAdminSetupVisuals"),
            "Core visuals must require both authenticated admin status and the local visibility toggle.");
        True(ContainsOperandText(RequireMethod(worldObjects, "ToggleAdminSetupVisuals"),
                 "ArenaWorldObjects::IsLocalAdmin") &&
             ContainsOperandText(RequireMethod(worldObjects, "RefreshAdminSetupVisuals"),
                 "RefreshAdminVisibility") &&
             ContainsOperandText(RequireMethod(RequireType(context.Mod, "ArenaGuard.Plugin"), "Update"),
                 "ArenaWorldObjects::HandleAdminVisualToggle"),
            "The Core-panel/hotkey toggle must authenticate the admin and refresh every live setup object.");
        MethodDefinition localAdmin = RequireMethod(worldObjects, "IsLocalAdmin");
        MethodDefinition subscribeAdminRefresh = RequireMethod(worldObjects, "SubscribeAdminStatusRefresh");
        True(ContainsOperandText(localAdmin, "LocalAdminResolver") &&
             ContainsOperandText(localAdmin, "SynchronizationManager::get_Instance") &&
             ContainsOperandText(localAdmin, "SynchronizationManager::get_PlayerIsAdmin"),
            "A stale vanilla admin resolver must not suppress Jotunn's synchronized local admin status.");
        True(ContainsOperandText(subscribeAdminRefresh, "add_OnAdminStatusChanged") &&
             ContainsOperandText(RequireMethod(worldObjects, "Shutdown"), "remove_OnAdminStatusChanged") &&
             ContainsOperandText(RequireMethod(worldObjects, "OnAdminStatusChanged"),
                 "RefreshAdminSetupVisuals"),
            "Reconnect-time admin synchronization must immediately refresh setup visuals and unsubscribe cleanly.");
        True(ContainsString(RequireMethod(worldObjects, "ToggleAdminSetupVisuals"),
                "Arena setup visuals are waiting for server administrator synchronization."),
            "F7 must explain a pending admin synchronization instead of failing silently.");

        TypeDefinition player = RequireType(context.Game, "Player");
        MethodDefinition findHover = RequireMethod(player, "FindHoverObject");
        True(findHover.Parameters.Count == 2 &&
             findHover.Parameters[0].ParameterType is ByReferenceType first &&
             first.ElementType.FullName == "UnityEngine.GameObject",
            "Installed Player.FindHoverObject signature has changed.");
        True(ContainsOperandText(findHover, "Physics::RaycastNonAlloc"),
            "Player hover selection no longer uses the expected physics raycast.");
        True(ContainsOperandText(findHover, "Player::m_interactMask"),
            "Player hover selection no longer applies the interaction layer mask.");
        True(ContainsOperandText(findHover, "GetComponent<Hoverable>"),
            "Player hover selection no longer recognizes Hoverable colliders.");

        MethodDefinition playerInteract = player.Methods.Single(method =>
            method.Name == "Interact" && method.Parameters.Count == 3);
        True(ContainsOperandText(playerInteract, "GetComponentInParent<Interactable>"),
            "Player interaction no longer resolves Interactable from the selected object's parent.");
        True(ContainsOperandText(playerInteract, "Interactable::Interact"),
            "Player interaction no longer dispatches the Interactable contract.");

        TypeDefinition spectatorPatch = RequireType(context.Mod, "ArenaGuard.Rules.SpectatorInteractionPatch");
        MethodDefinition prefix = RequireMethod(spectatorPatch, "Prefix");
        True(prefix.Parameters.Count == 2 &&
             prefix.Parameters[0].ParameterType.FullName == "Player" &&
             prefix.Parameters[1].ParameterType.FullName == "UnityEngine.GameObject",
            "Spectator interaction patch must receive the selected GameObject so it can exempt ArenaGuard controls.");
        True(ContainsOperandText(prefix, "ArenaRuleContext::IsAdministrator"),
            "Spectator interaction patch must authenticate the administrator exemption.");
        True(ContainsOperandText(prefix, "GetComponentInParent<ArenaGuard.World.ArenaWorldObjectBehaviour>"),
            "Spectator interaction patch must limit its exemption to ArenaGuard world objects.");
        True(ContainsOperandText(prefix, "GetComponentInParent<ItemDrop>") &&
             ContainsOperandText(prefix, "ArenaRuleContext::CanPickupDroppedItems"),
            "Loose-item interaction must use the authenticated admin pickup permission.");
    }

    private static void VerifyAdminPermissions(TestContext context)
    {
        TypeDefinition config = RequireType(context.Mod, "ArenaGuard.Config.ArenaConfig");
        foreach (string name in new[]
                 {
                     "AllowAdminTerrainEditing", "AllowAdminBuilding", "AllowAdminDroppedItemPickup"
                 })
        {
            True(config.Properties.Any(property => property.Name == name),
                "ArenaConfig is missing " + name + ".");
        }

        TypeDefinition ruleContext = RequireType(context.Mod, "ArenaGuard.Rules.ArenaRuleContext");
        True(ContainsOperandText(RequireMethod(ruleContext, "CanAdminBuild"),
                "ArenaAdminPermissionPolicy::CanBuild") &&
             ContainsOperandText(RequireMethod(ruleContext, "CanAdminModifyTerrain"),
                "ArenaAdminPermissionPolicy::CanModifyTerrain") &&
             ContainsOperandText(RequireMethod(ruleContext, "CanPickupDroppedItems"),
                "ArenaAdminPermissionPolicy::CanPickup"),
            "All protected admin actions must use the shared permission policy.");

        TypeDefinition buildPatch = RequireType(context.Mod, "ArenaGuard.Rules.ProtectedBuildPatch");
        TypeDefinition demolitionPatch = RequireType(context.Mod, "ArenaGuard.Rules.ProtectedDemolitionPatch");
        TypeDefinition terrainPatch = RequireType(context.Mod, "ArenaGuard.Rules.ProtectedTerrainPatch");
        TypeDefinition pickupPatch = RequireType(context.Mod, "ArenaGuard.Rules.ArenaPickupPatch");
        TypeDefinition autoPickupPatch = RequireType(context.Mod, "ArenaGuard.Rules.ArenaAutoPickupPatch");
        True(ContainsOperandText(RequireMethod(buildPatch, "Prefix"), "CanAdminBuild") &&
             ContainsOperandText(RequireMethod(demolitionPatch, "Prefix"), "CanAdminBuild") &&
             ContainsOperandText(RequireMethod(terrainPatch, "Prefix"), "CanAdminModifyTerrain") &&
             ContainsOperandText(RequireMethod(pickupPatch, "Prefix"), "CanPickupDroppedItems") &&
             ContainsOperandText(RequireMethod(autoPickupPatch, "Prefix"), "CanPickupDroppedItems"),
            "Build, demolition, terrain, manual pickup, and auto-pickup must enforce the toggles.");

        TypeDefinition snapshot = RequireType(context.Mod, "ArenaGuard.Domain.ArenaClientSnapshot");
        foreach (string name in new[]
                 {
                     "AdminsMayModifyTerrain", "AdminsMayBuild", "AdminsMayPickupDroppedItems"
                 })
        {
            True(snapshot.Fields.Any(field => field.Name == name),
                "The server snapshot is missing " + name + ".");
        }

        TypeDefinition rpc = RequireType(context.Mod, "ArenaGuard.Networking.ArenaRpc");
        MethodDefinition writeSnapshot = RequireMethod(rpc, "WriteArenaSnapshot");
        MethodDefinition readSnapshot = RequireMethod(rpc, "ReadArenaSnapshot");
        MethodDefinition writeMutation = RequireMethod(rpc, "WriteAdminMutation");
        MethodDefinition readMutation = RequireMethod(rpc, "ReadAdminMutation");
        foreach (string name in new[]
                 {
                     "AdminsMayModifyTerrain", "AdminsMayBuild", "AdminsMayPickupDroppedItems"
                 })
        {
            True(ContainsOperandText(writeSnapshot, name) && ContainsOperandText(readSnapshot, name),
                "The arena wire snapshot does not round-trip " + name + ".");
        }
        True(ContainsOperandText(writeMutation, "ArenaAdminMutation::Enabled") &&
             ContainsOperandText(readMutation, "ArenaAdminMutation::Enabled"),
            "Admin permission mutations must round-trip their enabled state.");

        TypeDefinition runtime = RequireType(context.Mod, "ArenaGuard.Runtime.ArenaServerRuntime");
        MethodDefinition localAdministrator = RequireMethod(runtime, "IsLocalAdministrator");
        True(ContainsOperandText(localAdministrator, "SynchronizationManager::get_PlayerIsAdmin") &&
             ContainsOperandText(localAdministrator, "ZNet::LocalPlayerIsAdminOrHost"),
            "Runtime local-admin resolution must accept Jotunn synchronization after reconnect as well as host status.");
        True(ContainsOperandText(RequireMethod(runtime, "IsAdministrator"),
                 "ArenaServerRuntime::IsLocalAdministrator"),
            "Protected local-player rules must use Jotunn's synchronized dedicated-server admin status.");
        True(ContainsString(RequireMethod(
                    RequireType(context.Mod, "ArenaGuard.World.ArenaWorldObjects"),
                    "RegisterLocalization"),
                "arenaguard_admin_build_only"),
            "The protected-building rejection must have a registered localization token.");
        MethodDefinition requestUiMutation = RequireMethod(runtime, "RequestUiAdminMutation");
        True(FirstOperandIndex(requestUiMutation, "ArenaRpc::RequestAdminMutation") >= 0 &&
             FirstOperandIndex(requestUiMutation, "ArenaRpc::RequestAdminMutation") <
             FirstOperandIndex(requestUiMutation, "ArenaRegistry::TryGetArena"),
            "Server-wide permission toggles must not depend on a synchronized per-arena definition.");
        True(ContainsOperandText(RequireMethod(runtime, "OnAdminMutationRequested"), "ArenaConfig::Save") &&
             ContainsOperandText(RequireMethod(runtime, "OnAdminMutationRequested"), "BroadcastAllArenaStates") &&
             ContainsOperandText(RequireMethod(runtime, "AreLocalDevcommandsEnabled"), "FieldInfo::GetValue"),
            "Permission changes must persist, broadcast, and require local devcommands for terrain.");
    }

    private static void VerifyChallengeAccessPieces(TestContext context)
    {
        TypeDefinition host = RequireType(context.Mod, "ArenaGuard.World.ArenaChallengeHostBehaviour");
        True(host.Interfaces.Any(item => item.InterfaceType.FullName == "Hoverable") &&
             host.Interfaces.Any(item => item.InterfaceType.FullName == "Interactable"),
            "Arena Master interaction bubble must implement Hoverable and Interactable.");
        True(ContainsOperandText(RequireMethod(host, "Interact"), "ArenaWorldObjects::OpenArenaSign"),
            "Arena Master must open the existing challenge and leaderboard menu.");
        True(ContainsOperandText(RequireMethod(host, "Start"), "ArenaWorldObjects::ApplyAdminMarker"),
            "Arena Master's exact position must publish the arena staging position.");
        True(ContainsOperandText(RequireMethod(host, "Start"), "Humanoid::EquipBestWeapon"),
            "The named Dvergr rogue must equip its normal rogue weapon after spawning.");

        TypeDefinition arenaUi = RequireType(context.Mod, "ArenaGuard.UI.ArenaUi");
        MethodDefinition challengeMenu = RequireMethod(arenaUi, "OpenChallengeMenu");
        True(ContainsOperandText(challengeMenu, "CreatePanel") &&
             ContainsOperandText(challengeMenu, "AddModeCard") &&
             ContainsOperandText(challengeMenu, "AddSectionLabel") &&
             ContainsString(challengeMenu, "<b>SPECIFIC MONSTER</b>\n<size=15>Build a custom\nfight</size>") &&
             !ContainsOperandText(challengeMenu, "AddDropdown"),
            "The challenge screen must use the redesigned native button-card layout rather than dropdown rows.");
        MethodDefinition creaturePicker = RequireMethod(arenaUi, "BuildCreaturePicker");
        True(ContainsOperandText(creaturePicker, "CreatePanel") &&
             ContainsOperandText(creaturePicker, "AddCreatureCard") &&
             ContainsOperandText(creaturePicker, "SetButtonSelected") &&
             ContainsOperandInType(arenaUi, "CreatureDefinition::Biome"),
            "Specific Monster must open a native wood-panel creature picker with biome tabs and creature cards.");
        True(ContainsOperandText(RequireMethod(arenaUi, "SetChallengeMode"), "OpenCreaturePicker"),
            "Choosing Specific Monster mode must open the creature picker immediately.");
        True(ContainsOperandText(RequireMethod(arenaUi, "SubmitChallenge"), "ChallengeRequested") &&
             ContainsOperandText(RequireMethod(arenaUi, "RequestChallengeLeaderboard"), "LeaderboardRequested") &&
             ContainsOperandText(RequireMethod(arenaUi, "SetButtonSelected"), "Selectable::set_colors") &&
             ContainsOperandText(RequireMethod(arenaUi, "CreatePanel"), "GUIManager::CreateWoodpanel") &&
             ContainsOperandText(RequireMethod(arenaUi, "AddButton"), "GUIManager::CreateButton"),
            "The redesign must retain request/leaderboard actions and use Valheim/Jotunn-native panels and buttons.");
        MethodDefinition leaderboardRequest = RequireMethod(arenaUi, "RequestChallengeLeaderboard");
        MethodDefinition leaderboardRender = RequireMethod(arenaUi, "RenderCombinedLeaderboard");
        True(CountOperandText(leaderboardRequest, "LeaderboardRequested") >= 2 &&
             ContainsString(leaderboardRender, "BIOME LADDER • ") &&
             ContainsString(leaderboardRender, "STAR LADDER • ") &&
             ContainsString(RequireMethod(arenaUi, "FormatLeaderboardDate"), "yyyy-MM-dd HH:mm 'UTC'") &&
             ContainsOperandText(RequireMethod(arenaUi, "ReceiveLeaderboard"), "RenderCombinedLeaderboard"),
            "The leaderboard must combine both ladder modes and display each result's UTC date and time.");
        True(ContainsOperandText(RequireMethod(arenaUi, "CloseModal"), "_creaturePickerRoot") &&
             ContainsOperandText(RequireMethod(arenaUi, "CloseCreaturePicker"), "SetPanelPosition"),
            "Closing either screen must clean up the picker and restore the main-panel position.");
        True(ContainsOperandText(creaturePicker, "GetPanelPosition") &&
             ContainsOperandText(creaturePicker, "SetPanelPosition"),
            "Changing creature tabs or selections must preserve both menu positions.");

        TypeDefinition sign = RequireType(context.Mod, "ArenaGuard.World.ArenaSignBehaviour");
        True(sign.Interfaces.Any(item => item.InterfaceType.FullName == "Hoverable"),
            "Arena Challenge Sign must remain hoverable.");
        True(sign.Interfaces.Any(item => item.InterfaceType.FullName == "Interactable"),
            "Arena Challenge Sign must remain interactable.");
        True(sign.Methods.Any(method => method.Name == "Interact" && method.Parameters.Count == 3 &&
                                       method.Parameters[0].ParameterType.FullName == "Humanoid" &&
                                       method.Parameters[1].ParameterType.FullName == "System.Boolean" &&
                                       method.Parameters[2].ParameterType.FullName == "System.Boolean" &&
                                       method.ReturnType.FullName == "System.Boolean"),
            "Arena Challenge Sign interaction signature no longer matches Valheim's Interactable contract.");

        TypeDefinition worldObjects = RequireType(context.Mod, "ArenaGuard.World.ArenaWorldObjects");
        MethodDefinition registerHost = RequireMethod(worldObjects, "RegisterChallengeHost");
        foreach (string operand in new[]
                 {
                     "PrefabManager::CreateClonedPrefab", "GameObject::AddComponent<Piece>",
                     "GameObject::AddComponent<UnityEngine.SphereCollider>", "Behaviour::set_enabled",
                     "Rigidbody::set_isKinematic", "Rigidbody::set_useGravity",
                     "Rigidbody::set_constraints", "ZNetView::m_persistent",
                     "AddWorldBehaviour<ArenaGuard.World.ArenaChallengeHostBehaviour>",
                     "ValidateChallengeHostPrefab"
                 })
        {
            True(ContainsOperandText(registerHost, operand),
                "Arena Master registration is missing " + operand + ".");
        }
        True(ContainsString(registerHost, "piece_nonsolid"),
            "Arena Master and its interaction bubble must use Valheim's non-solid piece layer.");

        MethodDefinition makeNonSolid = RequireMethod(worldObjects, "MakeSignCollidersNonSolid");
        True(ContainsOperandText(makeNonSolid, "GetComponentsInChildren<UnityEngine.Collider>"),
            "Sign-derived pieces must enumerate every inherited collider.");
        True(ContainsOperandText(makeNonSolid, "Collider::set_isTrigger"),
            "Sign-derived pieces must convert every inherited collider to a trigger.");
        True(ContainsOperandText(makeNonSolid, "Collider::set_enabled"),
            "Sign-derived pieces must keep every inherited collider enabled.");
        True(!ContainsOperandText(makeNonSolid, "RemoveComponent<UnityEngine.Collider>") &&
             !ContainsOperandText(makeNonSolid, "GameObject::AddComponent<UnityEngine.BoxCollider>"),
            "Sign collider objects must never be deleted or replaced; doing so breaks the live sign visual lifecycle.");

        True(worldObjects.Methods.All(method => method.Name != "EnsureSignVisuals"),
            "ArenaGuard must not rewrite the visible 0.1.5 sign renderer, LOD, or active hierarchy.");

        MethodDefinition signRegistration = RequireMethod(worldObjects, "RegisterSign");
        True(ContainsOperandText(signRegistration, "MakeSignCollidersNonSolid") &&
             ContainsOperandText(signRegistration, "ValidateSignDerivedPrefab"),
            "The hidden legacy Challenge Sign must retain the proven visible non-solid sign path.");

        TypeDefinition marker = RequireType(context.Mod, "ArenaGuard.World.ArenaMarkerBehaviour");
        True(marker.Interfaces.Any(item => item.InterfaceType.FullName == "IRemoved"),
            "Position beacons must clear their saved marker when removed with the hammer.");
        True(marker.Fields.Any(field => field.FieldType.FullName == "UnityEngine.Renderer[]") &&
             marker.Fields.Any(field => field.FieldType.FullName == "UnityEngine.SphereCollider"),
            "Position beacons must own explicit visual and selection state.");
        MethodDefinition visibility = RequireMethod(marker, "ApplyAdminVisibility");
        True(ContainsOperandText(visibility, "ArenaWorldObjects::ShouldShowAdminSetupVisuals") &&
             ContainsOperandText(visibility, "Renderer::set_enabled") &&
             ContainsOperandText(visibility, "Collider::set_enabled"),
            "Position beacon visuals and selection must be enabled only for authenticated admins.");
        True(ContainsOperandText(RequireMethod(marker, "OnRemoved"), "ArenaWorldObjects::RemoveAdminMarker"),
            "Hammer removal must clear the authoritative marker slot.");
        True(!ContainsOperandText(RequireMethod(sign, "GetHoverText"), "ArenaWorldObjects::IsLocalAdmin"),
            "Arena Challenge Sign must remain visible and interactive for non-admin players.");
        True(!sign.Methods.Any(method => ContainsOperandText(method, "Renderer::") ||
                                        ContainsOperandText(method, "LODGroup::") ||
                                        ContainsOperandText(method, "GameObject::SetActive")),
            "Challenge Sign behaviour must preserve Valheim's visible 0.1.5 renderer hierarchy at runtime.");
        True(ContainsOperandText(RequireMethod(sign, "Start"), "ArenaWorldObjects::ApplyAdminMarker"),
            "The hidden legacy Arena Challenge Sign must retain staging compatibility for existing worlds.");
        MethodDefinition registerSign = RequireMethod(worldObjects, "RegisterSign");
        True(registerSign.Parameters.Any(parameter =>
                 parameter.Name == "enabled" && parameter.ParameterType.FullName == "System.Boolean") &&
             ContainsOperandText(registerSign, "AddPiece"),
            "The legacy Challenge Sign must remain registered but support hidden build-menu registration.");
        MethodDefinition registerMarker = RequireMethod(worldObjects, "RegisterMarker");
        True(registerMarker.Parameters.Any(parameter =>
                 parameter.Name == "enabled" && parameter.ParameterType.FullName == "System.Boolean") &&
             ContainsOperandText(registerMarker, "AddPiece"),
            "Legacy position markers must support hidden registration without losing existing-world prefab compatibility.");
        foreach (string operand in new[]
                 {
                     "CreateMarkerVisuals", "GameObject::AddComponent<UnityEngine.SphereCollider>",
                     "Collider::set_isTrigger", "ZNetView::m_persistent", "ValidateMarkerPrefab"
                 })
        {
            True(ContainsOperandText(registerMarker, operand),
                "Purpose-built marker registration is missing " + operand + ".");
        }
        True(!ContainsString(registerMarker, "sign") &&
             !ContainsOperandText(registerMarker, "MakeSignCollidersNonSolid"),
            "Position beacons must not reuse the fragile vanilla sign prefab.");
        MethodDefinition markerPrimitive = RequireMethod(worldObjects, "CreateMarkerPrimitive");
        True(ContainsOperandText(markerPrimitive, "GameObject::CreatePrimitive") &&
             ContainsOperandText(markerPrimitive, "Object::DestroyImmediate") &&
             ContainsOperandText(markerPrimitive, "PrefabManager::GetPrefab") &&
             ContainsOperandText(markerPrimitive, "Renderer::set_sharedMaterials") &&
             ContainsString(markerPrimitive, "sign") &&
             !ContainsOperandText(markerPrimitive, "Material::.ctor"),
            "Every beacon mesh slot must reuse a game-owned supported material rather than serialize a runtime material.");
        MethodDefinition markerVisuals = RequireMethod(worldObjects, "CreateMarkerVisuals");
        True(!ContainsOperandText(markerVisuals, "AddComponent<UnityEngine.TextMesh>"),
            "Marker registration must not depend on a runtime TextMesh renderer or its uninitialized material.");
        MethodDefinition markerValidator = RequireMethod(worldObjects, "ValidateMarkerPrefab");
        foreach (string diagnostic in new[]
                 {
                     "persistent ZNetView", "three initialized beacon renderers",
                     "one trigger-only selection collider", "one Hoverable/Interactable handler"
                 })
        {
            True(ContainsString(markerValidator, diagnostic),
                "Marker validation must identify a failed invariant: " + diagnostic + ".");
        }
        True(!ContainsOperandText(markerValidator, "InvalidOperationException::.ctor"),
            "Marker diagnostics must never abort hammer-piece registration.");
        MethodDefinition registerPrefabs = RequireMethod(worldObjects, "RegisterPrefabs");
        True(ContainsOperandText(registerPrefabs, "TryRegisterCompatibilityMarker"),
            "Hidden legacy markers must use an isolated compatibility registration path.");

        TypeDefinition playerType = RequireType(context.Game, "Player");
        MethodDefinition removePiece = playerType.Methods.Single(method =>
            method.Name == "RemovePiece" && method.Parameters.Count == 0);
        True(ContainsOperandText(removePiece, "GetComponent<IRemoved>") &&
             ContainsOperandText(removePiece, "IRemoved::OnRemoved") &&
             ContainsOperandText(removePiece, "ZNetScene::Destroy"),
            "Installed Valheim hammer removal must invoke beacon cleanup before destroying its network object.");

        TypeDefinition runtime = RequireType(context.Mod, "ArenaGuard.Runtime.ArenaServerRuntime");
        True(ContainsOperandText(RequireMethod(runtime, "DetectFloorEntryAndBoundary"),
                "ArenaStartPolicy::ShouldTeleportToCombatStart"),
            "Staging must transition directly to the exact Combat Start marker after resource capture.");
        True(ContainsOperandText(RequireMethod(runtime, "DetectFloorEntryAndBoundary"),
                "ArenaStartPolicy::ShouldBeginCountdown") &&
             ContainsOperandText(RequireMethod(runtime, "DetectFloorEntryAndBoundary"),
                "SendPlayerMove"),
            "The countdown must wait for confirmed Combat Start arrival while movement is retried.");
        True(ContainsOperandText(RequireMethod(runtime, "OnCombatStartArrived"), "EnterCombatFloor") &&
             !ContainsOperandText(RequireMethod(runtime, "OnCombatStartArrived"), "FindPlayer") &&
             ContainsOperandText(RequireMethod(runtime, "OnCombatStartArrived"), "Near") &&
             ContainsOperandText(RequireMethod(runtime, "ReportClientCombatStartArrival"),
                 "ReportCombatStartArrival"),
            "An authenticated dedicated-server combatant must begin without a local Player component only after its peer position reaches Combat Start.");
        True(ContainsOperandText(RequireMethod(runtime, "ReportClientCombatStartArrival"),
                 "OnClientMoveRequested") &&
             ContainsOperandText(RequireMethod(runtime, "ReportClientCombatStartArrival"),
                 "ArenaClientSnapshot::PreparationComplete"),
            "A prepared client must retry the server-supplied Combat Start destination when the one-way move packet is lost.");
        MethodDefinition combatStartDetection = RequireMethod(runtime, "DetectFloorEntryAndBoundary");
        True(ContainsOperandText(combatStartDetection, "TryResolvePlayerState") &&
             ContainsOperandText(RequireMethod(runtime, "TryResolvePlayerState"), "ZDO::GetPosition") &&
             ContainsOperandText(RequireMethod(runtime, "TryResolvePlayerState"), "ZNet::GetConnectedPeers") &&
             ContainsOperandText(RequireMethod(runtime, "AggroArenaEnemy"), "TryResolvePlayerState"),
            "Dedicated-server combat checks must resolve remote players through authenticated peer ZDO state.");
        True(ContainsOperandText(combatStartDetection, "Near") &&
             !ContainsOperandText(combatStartDetection, "Near3D") &&
             ContainsOperandText(RequireMethod(runtime, "ReportClientCombatStartArrival"), "Near") &&
             !ContainsOperandText(RequireMethod(runtime, "ReportClientCombatStartArrival"), "Near3D"),
            "Combat Start arrival must tolerate Valheim floor-height correction and validate horizontal proximity.");
        True(ContainsOperandText(RequireMethod(runtime, "TrySendArenaMove"), "Near3D"),
            "Accepting beside the Arena Master must not issue a redundant staging teleport.");
        MethodDefinition masterExit = RequireMethod(runtime, "TryResolveArenaMasterExit");
        True(ContainsOperandText(RequireMethod(runtime, "ApplyEngineEffect"), "IsTerminalMove") &&
             ContainsOperandText(masterExit, "ArenaWorldObjects::IsChallengeHost") &&
             ContainsOperandText(masterExit, "Transform::get_forward") &&
             ContainsOperandText(masterExit, "Transform::get_eulerAngles") &&
             ContainsSingle(masterExit, 3f),
            "Terminal runs must return the player three metres in front of the live Arena Master.");
        MethodDefinition requestMarker = RequireMethod(runtime, "RequestMarkerMutation");
        True(ContainsOperandText(requestMarker, "TryGetMarkerPosition") &&
             ContainsOperandText(requestMarker, "already has a Combat Start"),
            "Client placement must reject a second physical Combat Start beacon.");
        True(ContainsOperandText(requestMarker, "ArenaRegistry::IsWithinCombatRadius") &&
             ContainsString(requestMarker, "Combat Start must be inside the blue combat-radius ring."),
            "Client placement must reject a Combat Start outside the combat radius.");
        MethodDefinition adminMutation = RequireMethod(runtime, "OnAdminMutationRequested");
        True(ContainsOperandText(adminMutation, "already has a Combat Start") &&
             ContainsOperandText(adminMutation, "Duplicate beacon removed"),
            "The server must enforce Combat Start uniqueness and preserve the registered position when deleting duplicates.");
        True(ContainsOperandText(adminMutation, "ArenaRegistry::IsWithinCombatRadius") &&
             ContainsOperandText(RequireMethod(runtime, "OnChallengeRequested"),
                 "ArenaRegistry::IsWithinCombatRadius"),
            "The server must reject out-of-bounds Combat Start mutations and legacy challenge starts.");
        TypeDefinition registry = RequireType(context.Mod, "ArenaGuard.Arenas.ArenaRegistry");
        True(!ContainsOperandText(RequireMethod(registry, "MarkersAreComplete"), "ArenaMarkerSet::HubGatePosition"),
            "Optional gates must not be required to enable a challenge arena.");
        True(!ContainsOperandText(RequireMethod(registry, "MissingRequiredMarkers"),
                "ArenaMarkerSet::HubGatePosition"),
            "Enable diagnostics must not report an optional hub or gate.");
        MethodDefinition markerVisibility = RequireMethod(marker, "ApplyAdminVisibility");
        True(!ContainsOperandText(markerVisibility, "MaterialPropertyBlock::.ctor") &&
             marker.Fields.Any(field => field.FieldType.FullName == "UnityEngine.MaterialPropertyBlock") &&
             ContainsOperandText(markerVisibility, "Renderer::SetPropertyBlock") &&
             ContainsOperandText(markerVisibility, "MaterialPropertyBlock::SetColor"),
            "Beacon colors must reuse a cached per-renderer property override on the supported shared material.");

        MethodDefinition registerSummon = RequireMethod(runtime, "RegisterArenaSummon");
        True(ContainsString(registerSummon, "arenaguard.arena_id") &&
             ContainsString(registerSummon, "arenaguard.session_id") &&
             ContainsOperandText(registerSummon, "CharacterDrop::SetDropsEnabled") &&
             ContainsOperandText(registerSummon, "DestroyArenaCharacter"),
            "Summoned creatures must inherit encounter identity, suppress drops, and be destroyed if the run ended.");
        MethodDefinition aggroEnemy = RequireMethod(runtime, "AggroArenaEnemy");
        MethodDefinition spawnEncounter = runtime.Methods.Single(method =>
            method.Name == "SpawnEncounter" && method.Parameters.Count == 2);
        True(ContainsOperandText(spawnEncounter, "AggroArenaEnemy") &&
             ContainsOperandText(registerSummon, "AggroArenaEnemy") &&
             ContainsOperandText(aggroEnemy, "Character::GetBaseAI") &&
             ContainsOperandText(aggroEnemy, "BaseAI::SetHuntPlayer") &&
             ContainsOperandText(aggroEnemy, "BaseAI::Alert") &&
             ContainsStringInType(runtime, "Wakeup") &&
             ContainsStringInType(runtime, "SetTarget"),
            "Initial enemies and summons must wake, hunt, alert, and immediately target the active combatant.");
        MethodDefinition despawnEnemies = runtime.Methods
            .First(method => method.Name == "DespawnSessionEnemies" && method.Parameters.Count == 2);
        True(ContainsOperandText(despawnEnemies, "Character::GetAllCharacters") &&
             ContainsString(despawnEnemies, "arenaguard.session_id"),
            "Terminal cleanup must scan network-tagged summons in addition to in-memory enemy handles.");
        True(runtime.Methods.Where(method => method.Name == "DespawnSessionEnemies")
                 .Any(method => ContainsOperandText(method, "Character::IsTamed") &&
                                ContainsOperandText(method, "ArenaWorldObjects::IsChallengeHost") &&
                                ContainsOperandText(method, "ArenaDefinition::ProtectedRadius")),
            "Terminal cleanup must reset untamed creatures in the arena while preserving players, tames, and the Arena Master.");
        MethodDefinition wildlifeSweep = RequireMethod(runtime, "RemoveProtectedWildlife");
        True(ContainsOperandText(wildlifeSweep, "Character::GetAllCharacters") &&
             ContainsOperandText(wildlifeSweep, "RandomFlyingBird::get_Instances") &&
             ContainsOperandText(wildlifeSweep, "ArenaWildlifePolicy::ShouldRemoveCharacter") &&
             ContainsOperandText(wildlifeSweep, "Character::IsTamed") &&
             ContainsOperandText(wildlifeSweep, "ArenaWorldObjects::IsChallengeHost") &&
             ContainsString(wildlifeSweep, "arenaguard.arena_id") &&
             ContainsOperandText(wildlifeSweep, "DestroyProtectedWildlife"),
            "The server wildlife sweep must remove passive characters and birds while preserving protected actors.");
        True(runtime.Fields.Any(field => field.Name == "ProtectedWildlifeCharacters") &&
             runtime.Fields.Any(field => field.Name == "ProtectedWildlifeBirds") &&
             ContainsOperandText(RequireMethod(runtime, "TickOnMainThread"), "RemoveProtectedWildlife"),
            "Protected wildlife cleanup must be low-frequency and reuse static buffers.");

        TypeDefinition ruleContext = RequireType(context.Mod, "ArenaGuard.Rules.ArenaRuleContext");
        MethodDefinition protectedDamage = RequireMethod(ruleContext, "IsProtectedDamage");
        True(ContainsOperandText(protectedDamage, "Component::get_transform") &&
             ContainsOperandText(protectedDamage, "HitData::m_point"),
            "Protected damage checks must cover both the object center and the actual impact point.");
        foreach (string patchName in new[]
                 {
                     "ProtectedStructureDamagePatch", "ProtectedStructureApplyDamagePatch",
                     "ProtectedStructureDropPatch", "ProtectedWorldObjectDamagePatch",
                     "ProtectedMineRockDamagePatch"
                 })
        {
            True(ContainsOperandText(RequireMethod(
                    RequireType(context.Mod, "ArenaGuard.Rules." + patchName), "Prefix"),
                    "ArenaRuleContext::IsProtectedDamage"),
                patchName + " must enforce protected-area invulnerability.");
        }
        TypeDefinition worldDamage = RequireType(context.Mod, "ArenaGuard.Rules.ProtectedWorldObjectDamagePatch");
        RequireMethod(worldDamage, "TargetMethods");
        foreach (string targetName in new[] { "WearNTear", "Destructible", "MineRock", "TreeBase", "TreeLog" })
        {
            True(ContainsOperandInType(worldDamage, targetName),
                "Protected world-object damage is missing " + targetName + ".");
        }

        TypeDefinition damagePatch = RequireType(context.Mod, "ArenaGuard.Rules.SpectatorDamagePatch");
        TypeDefinition deathPatch = RequireType(context.Mod, "ArenaGuard.Rules.CombatantDeathSafetyPatch");
        TypeDefinition targetPatch = RequireType(context.Mod, "ArenaGuard.Rules.ArenaEnemyTargetPatch");
        TypeDefinition interactionPatch = RequireType(context.Mod, "ArenaGuard.Rules.SpectatorInteractionPatch");
        foreach (MethodDefinition method in new[]
                 {
                     RequireMethod(damagePatch, "Prefix"), RequireMethod(deathPatch, "Prefix"),
                     RequireMethod(targetPatch, "Prefix"), RequireMethod(interactionPatch, "Prefix")
                 })
        {
            True(ContainsOperandText(method, "ArenaChallengeHostBehaviour") ||
                 ContainsOperandText(method, "ArenaWorldObjects::IsChallengeHost"),
                method.DeclaringType.Name + " must explicitly protect or allow the Arena Master.");
        }
    }

    private static void VerifyHotPathPerformance(TestContext context)
    {
        TypeDefinition deathPatch = RequireType(context.Mod, "ArenaGuard.Rules.CombatantDeathSafetyPatch");
        MethodDefinition deathPrefix = RequireMethod(deathPatch, "Prefix");
        True(!ContainsOperandText(deathPrefix, "GetComponent") &&
             ContainsOperandText(deathPrefix, "ArenaWorldObjects::IsChallengeHost"),
            "Character.CheckDeath must use the Arena Master identity cache without a hierarchy search.");

        TypeDefinition targetPatch = RequireType(context.Mod, "ArenaGuard.Rules.ArenaEnemyTargetPatch");
        MethodDefinition targetPrefix = RequireMethod(targetPatch, "Prefix");
        True(!ContainsOperandText(targetPrefix, "GetComponent") &&
             !ContainsOperandText(targetPrefix, "ResolveEnemyArenaId") &&
             !ContainsOperandText(targetPrefix, "ResolveCombatant") &&
             ContainsOperandText(targetPrefix, "ArenaRuleContext::GetEnemyArenaId") &&
             ContainsOperandText(targetPrefix, "ArenaRuleContext::GetCombatantId"),
            "BaseAI.IsEnemy must use cached arena/combatant identity lookups.");

        TypeDefinition ruleContext = RequireType(context.Mod, "ArenaGuard.Rules.ArenaRuleContext");
        True(ContainsOperandText(RequireMethod(ruleContext, "GetEnemyArenaId"),
                 "ConditionalWeakTable") &&
             ContainsOperandText(RequireMethod(ruleContext, "GetCombatantId"), "CombatantCache"),
            "Arena identity caches must be present behind the AI hot path.");

        TypeDefinition worldObjects = RequireType(context.Mod, "ArenaGuard.World.ArenaWorldObjects");
        MethodDefinition hammerAudit = RequireMethod(worldObjects, "GrantOrRemoveAdminHammer");
        True(ContainsOperandText(hammerAudit, "Time::get_unscaledTime") &&
             ContainsOperandText(hammerAudit, "FindAdminHammer") &&
             !ContainsOperandText(hammerAudit, "FindAdminHammers"),
            "The admin hammer inventory audit must be throttled and stop allocating result lists.");

        TypeDefinition runtime = RequireType(context.Mod, "ArenaGuard.Runtime.ArenaServerRuntime");
        MethodDefinition arrival = RequireMethod(runtime, "ReportClientCombatStartArrival");
        True(ContainsOperandText(arrival, "Time::get_unscaledTime") &&
             !ContainsOperandText(arrival, "HashSet") &&
             !ContainsOperandText(arrival, "System.Linq.Enumerable") &&
             runtime.Fields.Any(field => field.Name == "StaleClientCombatStartArrivalArenaIds" &&
                                         field.FieldType.FullName.StartsWith("System.Collections.Generic.List")),
            "Client arrival detection must be throttled and reuse its cleanup buffer.");

        TypeDefinition core = RequireType(context.Mod, "ArenaGuard.World.ArenaCoreBehaviour");
        MethodDefinition coreAwake = RequireMethod(core, "Awake");
        MethodDefinition coreStart = RequireMethod(core, "Start");
        True(ContainsOperandText(coreAwake, "Player::m_localPlayer") &&
             ContainsOperandText(coreAwake, "ArenaCoreBehaviour::ApplyAdminVisibility") &&
             ContainsOperandText(coreStart, "ArenaCoreBehaviour::ApplyAdminVisibility"),
            "Core prefab templates must retain visible renderers until a local player exists, then enforce admin visibility at Start.");
        MethodDefinition coreVisibility = RequireMethod(core, "ApplyAdminVisibility");
        True(core.Fields.Any(field => field.FieldType is ArrayType array &&
                                      array.ElementType.FullName == "LightFlicker") &&
             core.Fields.Any(field => field.FieldType is ArrayType array &&
                                      array.ElementType.FullName == "LightLod") &&
             ContainsOperandText(coreVisibility, "Behaviour::set_enabled") &&
             ContainsOperandText(coreVisibility, "Light::set_intensity") &&
             ContainsOperandText(coreVisibility, "Light::set_range"),
            "A hidden Core must disable its flicker/LOD controllers and perform no residual lighting work.");
        MethodDefinition applyRadiusRing = RequireMethod(core, "ApplyRadiusRing");
        True(!ContainsOperandText(applyRadiusRing, "MaterialPropertyBlock::.ctor") &&
             core.Fields.Count(field => field.FieldType.FullName == "UnityEngine.MaterialPropertyBlock") >= 2,
            "Arena Core radius rings must reuse cached material property blocks.");

        TypeDefinition marker = RequireType(context.Mod, "ArenaGuard.World.ArenaMarkerBehaviour");
        MethodDefinition markerAwake = RequireMethod(marker, "Awake");
        MethodDefinition markerStart = RequireMethod(marker, "Start");
        True(ContainsOperandText(markerAwake, "Player::m_localPlayer") &&
             ContainsOperandText(markerAwake, "ArenaMarkerBehaviour::ApplyAdminVisibility") &&
             ContainsOperandText(markerStart, "ArenaMarkerBehaviour::ApplyAdminVisibility"),
            "Marker prefab templates must not inherit a hidden first-preview state before local admin synchronization.");
        MethodDefinition markerVisibility = RequireMethod(marker, "ApplyAdminVisibility");
        True(!ContainsOperandText(markerVisibility, "MaterialPropertyBlock::.ctor") &&
             marker.Fields.Any(field => field.FieldType.FullName == "UnityEngine.MaterialPropertyBlock"),
            "Arena marker visibility must reuse a cached material property block.");

        TypeDefinition gate = RequireType(context.Mod, "ArenaGuard.World.ArenaGateBehaviour");
        MethodDefinition gateUpdate = RequireMethod(gate, "Update");
        MethodDefinition gateColor = RequireMethod(gate, "ApplyGateColor");
        True(ContainsOperandText(gateUpdate, "Time::get_unscaledTime") &&
             ContainsOperandText(gateUpdate, "Vector3::get_sqrMagnitude") &&
             !ContainsOperandText(gateUpdate, "Vector3::Distance") &&
             !ContainsOperandText(gateUpdate, "Renderer::get_material") &&
             ContainsOperandText(gateColor, "Renderer::SetPropertyBlock"),
            "Arena gates must throttle proximity work and update shared-material overrides without instancing materials.");
    }

    private static void VerifyHarmonyPatchMetadata(TestContext context)
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ProtectedStructureDamagePatch"] = "WearNTear.Damage",
            ["ProtectedStructureApplyDamagePatch"] = "WearNTear.ApplyDamage",
            ["ProtectedStructureDropPatch"] = "Piece.DropResources",
            ["ProtectedMineRockDamagePatch"] = "MineRock5.DamageArea",
            ["ProtectedBuildPatch"] = "Player.TryPlacePiece",
            ["ProtectedDemolitionPatch"] = "Player.CheckCanRemovePiece",
            ["ProtectedTerrainPatch"] = "TerrainOp.Awake",
            ["SpectatorDamagePatch"] = "Character.RPC_Damage",
            ["CombatantLethalDamagePatch"] = "Character.ApplyDamage",
            ["CombatantDeathSafetyPatch"] = "Character.CheckDeath",
            ["SpectatorInteractionPatch"] = "Player.Interact",
            ["ArenaPickupPatch"] = "Humanoid.Pickup",
            ["ArenaAutoPickupPatch"] = "Player.AutoPickup",
            ["ArenaDropPatch"] = "Humanoid.DropItem",
            ["ArenaInventoryTransferPatch"] = "Inventory.MoveItemToThis",
            ["ArenaInventoryTransferAtPatch"] = "Inventory.MoveItemToThis",
            ["ArenaCraftingPatch"] = "InventoryGui.DoCrafting",
            ["ArenaConsumablePatch"] = "Humanoid.CanConsumeItem",
            ["ArenaAmmoPatch"] = "Inventory.RemoveItem",
            ["ArenaSkillGainPatch"] = "Skills.RaiseSkill",
            ["ArenaArmorDurabilityPatch"] = "Player.DamageArmorDurability",
            ["ArenaEquippedDurabilityPatch"] = "Humanoid.DrainEquipedItemDurability",
            ["ArenaEnemyTargetPatch"] = "BaseAI.IsEnemy",
            ["ArenaEnemyDropListPatch"] = "CharacterDrop.GenerateDropList",
            ["ArenaEnemyDeathPatch"] = "Character.OnDeath",
            ["ArenaContainerPatch"] = "Container.Interact",
            ["ArenaDoorPatch"] = "Door.Interact",
            ["ArenaJogSpeedFactorPatch"] = "Player.GetJogSpeedFactor",
            ["ArenaRunSpeedFactorPatch"] = "Player.GetRunSpeedFactor",
            ["ProtectedNaturalSpawnPatch"] = "SpawnSystem.IsSpawnPointGood",
            ["ProtectedCreatureSpawnerPatch"] = "CreatureSpawner.Spawn",
            ["ProtectedSpawnAreaPatch"] = "SpawnArea.FindSpawnPoint"
        };

        foreach (var pair in expected)
        {
            TypeDefinition patch = RequireType(context.Mod, "ArenaGuard.Rules." + pair.Key);
            CustomAttribute attribute = RequireAttribute(patch, "HarmonyLib.HarmonyPatch");
            string actual = HarmonyTarget(attribute);
            Equal(pair.Value, actual);
            True(patch.Methods.Any(method => method.CustomAttributes.Any(item =>
                    item.AttributeType.FullName == "HarmonyLib.HarmonyPrefix" ||
                    item.AttributeType.FullName == "HarmonyLib.HarmonyPostfix")),
                pair.Key + " must expose a Harmony prefix or postfix.");
        }

        TypeDefinition attackPatch = RequireType(context.Mod, "ArenaGuard.Rules.ArenaAttackDurabilityPatch");
        RequireAttribute(attackPatch, "HarmonyLib.HarmonyPatch");
        RequireMethod(attackPatch, "TargetMethods");
        foreach (string methodName in new[] { "ProjectileAttackTriggered", "DoNonAttack", "DoMeleeAttack", "DoAreaAttack" })
        {
            True(ContainsStringInType(attackPatch, methodName),
                "Dynamic durability patch is missing " + methodName + ".");
        }

        TypeDefinition summonPatch = RequireType(context.Mod, "ArenaGuard.Rules.ArenaSpawnAbilityPatch");
        RequireAttribute(summonPatch, "HarmonyLib.HarmonyPatch");
        RequireMethod(summonPatch, "TargetMethod");
        True(ContainsOperandInType(summonPatch, "RegisterSpawned") &&
             ContainsOperandText(RequireMethod(summonPatch, "RegisterSpawned"), "RegisterArenaSummon"),
            "SpawnAbility's instantiated creature must be passed to arena summon registration.");

        TypeDefinition worldDamagePatch = RequireType(context.Mod,
            "ArenaGuard.Rules.ProtectedWorldObjectDamagePatch");
        RequireAttribute(worldDamagePatch, "HarmonyLib.HarmonyPatch");
        RequireMethod(worldDamagePatch, "TargetMethods");
        RequireMethod(worldDamagePatch, "Prefix");
    }

    private static void VerifyArenaMovementSpeedPolicy(TestContext context)
    {
        TypeDefinition policy = RequireType(context.Mod, "ArenaGuard.Rules.ArenaMovementSpeedPolicy");
        RequireMethod(policy, "NormalizeJog");
        RequireMethod(policy, "NormalizeRun");

        TypeDefinition jogPatch = RequireType(context.Mod, "ArenaGuard.Rules.ArenaJogSpeedFactorPatch");
        TypeDefinition runPatch = RequireType(context.Mod, "ArenaGuard.Rules.ArenaRunSpeedFactorPatch");
        MethodDefinition jogPostfix = RequireMethod(jogPatch, "Postfix");
        MethodDefinition runPostfix = RequireMethod(runPatch, "Postfix");
        True(ContainsOperandText(jogPostfix, "ArenaRuleContext::IsProtectedPoint") &&
             ContainsOperandText(jogPostfix, "Character::GetEquipmentMovementModifier") &&
             ContainsOperandText(jogPostfix, "ArenaMovementSpeedPolicy::NormalizeJog"),
            "Arena jog speed must be rebuilt from the protected-area state and equipment modifier.");
        True(ContainsOperandText(runPostfix, "ArenaRuleContext::IsProtectedPoint") &&
             ContainsOperandText(runPostfix, "Character::GetEquipmentMovementModifier") &&
             ContainsOperandText(runPostfix, "Skills::GetSkillFactor") &&
             ContainsOperandText(runPostfix, "ArenaMovementSpeedPolicy::NormalizeRun"),
            "Arena run speed must preserve equipment and Run skill while discarding later bonuses.");
        RequireAttribute(jogPatch, "HarmonyLib.HarmonyAfter");
        RequireAttribute(runPatch, "HarmonyLib.HarmonyAfter");
        RequireAttribute(jogPostfix, "HarmonyLib.HarmonyPriority");
        RequireAttribute(runPostfix, "HarmonyLib.HarmonyPriority");

        TypeDefinition compatibility = RequireType(context.Mod,
            "ArenaGuard.Rules.SpeedyPathsCompatibility");
        True(ConstantString(compatibility, "PluginGuid") == "nex.SpeedyPaths" &&
             ContainsStringInType(compatibility, "SpeedyPaths.SpeedyPathsClientMod") &&
             ContainsStringInType(compatibility, "GetSpeedyPathModifier"),
            "Speedy Paths integration must remain optional and target its installed speed source exactly.");
        True(ContainsOperandText(RequireMethod(compatibility, "NeutralizeSpeedModifier"),
                 "ArenaRuleContext::IsProtectedPoint") &&
             ContainsOperandText(RequireMethod(RequireType(context.Mod, "ArenaGuard.Plugin"), "Awake"),
                 "SpeedyPathsCompatibility::TryInstall"),
            "The optional Speedy Paths hook must install at startup and apply only inside protected arenas.");

        TypeDefinition player = RequireType(context.Game, "Player");
        True(player.Methods.Any(method => method.Name == "GetJogSpeedFactor" && method.Parameters.Count == 0) &&
             player.Methods.Any(method => method.Name == "GetRunSpeedFactor" && method.Parameters.Count == 0) &&
             player.Methods.Any(method => method.Name == "GetEquipmentMovementModifier" && method.Parameters.Count == 0),
            "The installed Valheim Player API no longer exposes the movement-factor contract.");
    }

    private static void VerifyInstalledPatchTargets(TestContext context)
    {
        var members = new[]
        {
            Member("WearNTear", "Damage", 1),
            Member("WearNTear", "ApplyDamage", 2),
            Member("WearNTear", "RPC_Damage", 2),
            Member("Piece", "DropResources", 1),
            Member("Destructible", "RPC_Damage", 2),
            Member("MineRock", "RPC_Hit", 3),
            Member("MineRock5", "DamageArea", 2),
            Member("TreeBase", "RPC_Damage", 2),
            Member("TreeLog", "RPC_Damage", 2),
            Member("Player", "TryPlacePiece", 1),
            Member("Player", "CheckCanRemovePiece", 1),
            Member("TerrainOp", "Awake", 0),
            Member("Character", "RPC_Damage", 2),
            Member("Character", "ApplyDamage", 4),
            Member("Character", "CheckDeath", 0),
            Member("Player", "Interact", 3),
            Member("Humanoid", "Pickup", 3),
            Member("Player", "AutoPickup", 1),
            Member("Humanoid", "DropItem", 3),
            Member("Inventory", "MoveItemToThis", 2),
            Member("Inventory", "MoveItemToThis", 5),
            Member("InventoryGui", "DoCrafting", 1),
            Member("Humanoid", "CanConsumeItem", 2),
            Member("Inventory", "RemoveItem", 2),
            Member("Skills", "RaiseSkill", 2),
            Member("Player", "DamageArmorDurability", 1),
            Member("Humanoid", "DrainEquipedItemDurability", 2),
            Member("BaseAI", "IsEnemy", 2),
            Member("BaseAI", "Alert", 0),
            Member("BaseAI", "SetHuntPlayer", 1),
            Member("MonsterAI", "Wakeup", 0),
            Member("MonsterAI", "SetTarget", 1),
            Member("CharacterDrop", "GenerateDropList", 0),
            Member("Character", "OnDeath", 0),
            Member("Container", "Interact", 3),
            Member("Door", "Interact", 3),
            Member("Player", "GetJogSpeedFactor", 0),
            Member("Player", "GetRunSpeedFactor", 0),
            Member("Player", "GetEquipmentMovementModifier", 0),
            Member("SpawnSystem", "IsSpawnPointGood", 2),
            Member("CreatureSpawner", "Spawn", 0),
            Member("SpawnArea", "FindSpawnPoint", 2),
            Member("Attack", "ProjectileAttackTriggered", 0),
            Member("Attack", "DoNonAttack", 0),
            Member("Attack", "DoMeleeAttack", 0),
            Member("Attack", "DoAreaAttack", 0),
            Member("SpawnAbility", "Spawn", 0)
        };

        foreach (var member in members)
        {
            TypeDefinition type = RequireType(context.Game, member.TypeName);
            True(type.Methods.Any(method => method.Name == member.MethodName && method.Parameters.Count == member.ParameterCount),
                member.TypeName + "." + member.MethodName + " with " + member.ParameterCount +
                " parameter(s) is missing from the installed Valheim API.");
        }

        TypeDefinition attack = RequireType(context.Game, "Attack");
        True(attack.Fields.Any(field => field.Name == "m_character"), "Attack.m_character hook is missing.");
        True(attack.Fields.Any(field => field.Name == "m_weapon"), "Attack.m_weapon hook is missing.");

        TypeDefinition spawnAbility = RequireType(context.Game, "SpawnAbility");
        True(spawnAbility.Fields.Any(field => field.Name == "m_owner"),
            "SpawnAbility.m_owner is required to inherit arena ownership.");
        True(ContainsOperandInType(spawnAbility, "Object::Instantiate") &&
             ContainsOperandInType(spawnAbility, "GameObject"),
            "Installed SpawnAbility coroutine no longer exposes the expected GameObject creation point.");
    }

    private static void VerifyCleanOutput(TestContext context)
    {
        var references = new HashSet<string>(
            context.Mod.MainModule.AssemblyReferences.Select(reference => reference.Name),
            StringComparer.OrdinalIgnoreCase);
        foreach (string dependency in new[] { "BepInEx", "0Harmony", "assembly_valheim", "Jotunn" })
        {
            True(references.Contains(dependency), "ArenaGuard must reference " + dependency + ".");
        }

        var forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "BepInEx.dll",
            "0Harmony.dll",
            "Jotunn.dll",
            "assembly_valheim.dll"
        };
        string[] copied = Directory.GetFiles(context.ModOutputDirectory, "*.dll")
            .Select(Path.GetFileName)
            .Where(name => forbidden.Contains(name))
            .ToArray();
        Equal(0, copied.Length);

        string[] unexpected = Directory.GetFiles(context.ModOutputDirectory, "*.dll")
            .Select(Path.GetFileName)
            .Where(name => !string.Equals(name, "SkaldHall.dll", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Equal(0, unexpected.Length);
    }

    private static string HarmonyTarget(CustomAttribute attribute)
    {
        string typeName = null;
        string methodName = null;
        foreach (CustomAttributeArgument argument in attribute.ConstructorArguments)
        {
            if (argument.Value is TypeReference type)
            {
                typeName = type.Name;
            }
            else if (argument.Type.FullName == "System.String")
            {
                methodName = (string)argument.Value;
            }
        }

        True(typeName != null && methodName != null, "Harmony patch must name its target type and method.");
        return typeName + "." + methodName;
    }

    private static string EnumArgumentName(AssemblyDefinition assembly, CustomAttributeArgument argument)
    {
        TypeDefinition type = FindType(assembly.MainModule.Types, argument.Type.FullName);
        True(type != null && type.IsEnum, "Could not resolve enum " + argument.Type.FullName + ".");
        long value = Convert.ToInt64(argument.Value, CultureInfo.InvariantCulture);
        FieldDefinition field = type.Fields.SingleOrDefault(candidate =>
            candidate.HasConstant && Convert.ToInt64(candidate.Constant, CultureInfo.InvariantCulture) == value);
        True(field != null, "Could not resolve enum value " + value + " for " + argument.Type.FullName + ".");
        return field.Name;
    }

    private static TypeDefinition RequireType(AssemblyDefinition assembly, string fullName)
    {
        TypeDefinition type = FindType(assembly.MainModule.Types, fullName);
        if (type == null)
        {
            throw new InvalidOperationException("Required type is missing: " + fullName);
        }

        return type;
    }

    private static TypeDefinition FindType(IEnumerable<TypeDefinition> types, string fullName)
    {
        foreach (TypeDefinition type in types)
        {
            if (type.FullName == fullName)
            {
                return type;
            }

            TypeDefinition nested = FindType(type.NestedTypes, fullName);
            if (nested != null)
            {
                return nested;
            }
        }

        return null;
    }

    private static CustomAttribute RequireAttribute(ICustomAttributeProvider provider, string fullName)
    {
        CustomAttribute attribute = provider.CustomAttributes.SingleOrDefault(item => item.AttributeType.FullName == fullName);
        if (attribute == null)
        {
            throw new InvalidOperationException("Required attribute is missing: " + fullName);
        }

        return attribute;
    }

    private static MethodDefinition RequireMethod(TypeDefinition type, string name)
    {
        MethodDefinition method = type.Methods.FirstOrDefault(candidate => candidate.Name == name);
        if (method == null)
        {
            throw new InvalidOperationException("Required method is missing: " + type.FullName + "." + name);
        }

        return method;
    }

    private static bool ContainsString(MethodDefinition method, string value)
    {
        return method.HasBody && method.Body.Instructions.Any(instruction =>
            instruction.Operand is string text && string.Equals(text, value, StringComparison.Ordinal));
    }

    private static bool ContainsOperandText(MethodDefinition method, string value)
    {
        return method.HasBody && method.Body.Instructions.Any(instruction =>
            instruction.Operand != null &&
            instruction.Operand.ToString().IndexOf(value, StringComparison.Ordinal) >= 0);
    }

    private static int CountOperandText(MethodDefinition method, string value)
    {
        return method.HasBody
            ? method.Body.Instructions.Count(instruction => instruction.Operand != null &&
                instruction.Operand.ToString().IndexOf(value, StringComparison.Ordinal) >= 0)
            : 0;
    }

    private static int FirstOperandIndex(MethodDefinition method, string value)
    {
        if (!method.HasBody)
        {
            return -1;
        }
        for (int index = 0; index < method.Body.Instructions.Count; index++)
        {
            object operand = method.Body.Instructions[index].Operand;
            if (operand != null && operand.ToString().IndexOf(value, StringComparison.Ordinal) >= 0)
            {
                return index;
            }
        }
        return -1;
    }

    private static bool ContainsSingle(MethodDefinition method, float value)
    {
        return method.HasBody && method.Body.Instructions.Any(instruction =>
            instruction.Operand is float actual && Math.Abs(actual - value) < 0.0001f);
    }

    private static bool ContainsStringInType(TypeDefinition type, string value)
    {
        return type.Methods.Any(method => ContainsString(method, value)) ||
            type.NestedTypes.Any(nested => ContainsStringInType(nested, value));
    }

    private static bool ContainsOperandInType(TypeDefinition type, string value)
    {
        return type.Methods.Any(method => ContainsOperandText(method, value)) ||
            type.NestedTypes.Any(nested => ContainsOperandInType(nested, value));
    }

    private static string AttributeString(CustomAttribute attribute, int index)
    {
        return (string)attribute.ConstructorArguments[index].Value;
    }

    private static string ConstantString(TypeDefinition type, string fieldName)
    {
        FieldDefinition field = type.Fields.SingleOrDefault(candidate => candidate.Name == fieldName);
        True(field != null && field.HasConstant, type.FullName + "." + fieldName + " must be a constant.");
        return (string)field.Constant;
    }

    private static KeyValuePair<string, Action<TestContext>> Test(string name, Action<TestContext> action)
    {
        return new KeyValuePair<string, Action<TestContext>>(name, action);
    }

    private static MemberExpectation Member(string type, string method, int parameterCount)
    {
        return new MemberExpectation(type, method, parameterCount);
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException("Expected '" + expected + "' but received '" + actual + "'.");
        }
    }

    private readonly struct MemberExpectation
    {
        internal MemberExpectation(string typeName, string methodName, int parameterCount)
        {
            TypeName = typeName;
            MethodName = methodName;
            ParameterCount = parameterCount;
        }

        internal string TypeName { get; }
        internal string MethodName { get; }
        internal int ParameterCount { get; }
    }

    private sealed class TestContext : IDisposable
    {
        private TestContext(
            AssemblyDefinition mod,
            AssemblyDefinition game,
            AssemblyDefinition bepinex,
            AssemblyDefinition jotunn,
            string outputDirectory,
            DefaultAssemblyResolver resolver)
        {
            Mod = mod;
            Game = game;
            BepInEx = bepinex;
            Jotunn = jotunn;
            ModOutputDirectory = outputDirectory;
            Resolver = resolver;
        }

        internal AssemblyDefinition Mod { get; }
        internal AssemblyDefinition Game { get; }
        internal AssemblyDefinition BepInEx { get; }
        internal AssemblyDefinition Jotunn { get; }
        internal string ModOutputDirectory { get; }
        private DefaultAssemblyResolver Resolver { get; }

        internal static TestContext Load()
        {
            string root = FindRepositoryRoot();
            string valheimRoot = Environment.GetEnvironmentVariable("VALHEIM_ROOT") ?? @"C:\ValheimServer\server";
            string output = Path.Combine(root, "src", "ArenaGuard", "bin", "Release", "net472");
            string modPath = Path.Combine(output, "SkaldHall.dll");
            string gamePath = Path.Combine(valheimRoot, "valheim_server_Data", "Managed", "assembly_valheim.dll");
            string bepinexPath = Path.Combine(valheimRoot, "BepInEx", "core", "BepInEx.dll");
            string jotunnPath = Path.Combine(valheimRoot, "BepInEx", "plugins", "Jotunn.dll");
            foreach (string path in new[] { modPath, gamePath, bepinexPath, jotunnPath })
            {
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException("Required assembly was not found.", path);
                }
            }

            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(output);
            resolver.AddSearchDirectory(Path.Combine(valheimRoot, "BepInEx", "core"));
            resolver.AddSearchDirectory(Path.Combine(valheimRoot, "BepInEx", "plugins"));
            resolver.AddSearchDirectory(Path.Combine(valheimRoot, "valheim_server_Data", "Managed"));
            var parameters = new ReaderParameters { AssemblyResolver = resolver };
            return new TestContext(
                AssemblyDefinition.ReadAssembly(modPath, parameters),
                AssemblyDefinition.ReadAssembly(gamePath, parameters),
                AssemblyDefinition.ReadAssembly(bepinexPath, parameters),
                AssemblyDefinition.ReadAssembly(jotunnPath, parameters),
                output,
                resolver);
        }

        public void Dispose()
        {
            Mod.Dispose();
            Game.Dispose();
            BepInEx.Dispose();
            Jotunn.Dispose();
            Resolver.Dispose();
        }

        private static string FindRepositoryRoot()
        {
            DirectoryInfo current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, "DESIGN.md")) &&
                    Directory.Exists(Path.Combine(current.FullName, "src", "ArenaGuard")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate the ArenaGuard repository root.");
        }
    }
}
