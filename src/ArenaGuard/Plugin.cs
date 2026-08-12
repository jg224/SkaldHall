using ArenaGuard.Config;
using ArenaGuard.Rules;
using ArenaGuard.Runtime;
using ArenaGuard.World;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Jotunn.Managers;
using Jotunn.Utils;

namespace ArenaGuard
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(JotunnGuid, BepInDependency.DependencyFlags.HardDependency)]
    [NetworkCompatibility(CompatibilityLevel.EveryoneMustHaveMod, VersionStrictness.Patch)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "jg224.arenaguard";
        public const string PluginName = "SkaldHall";
        public const string PluginVersion = "0.0.3";
        public const string JotunnGuid = "com.jotunn.jotunn";

        internal static ManualLogSource Log { get; private set; }

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;
            ArenaConfig.Bind(Config);
            CommandManager.Instance.AddConsoleCommand(new ArenaAdminCommand());

            PrefabManager.OnVanillaPrefabsAvailable += RegisterWorldPrefabs;
            ArenaServerRuntime.Initialize();

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);
            SpeedyPathsCompatibility.TryInstall(_harmony);

            Log.LogInfo(PluginName + " v" + PluginVersion +
                        " loaded. The same version is required on the server and every client.");
        }

        private void Update()
        {
            ArenaWorldObjects.HandleAdminVisualToggle();
            ArenaWorldObjects.GrantOrRemoveAdminHammer();
            ArenaServerRuntime.TickOnMainThread();
        }

        private void OnDestroy()
        {
            PrefabManager.OnVanillaPrefabsAvailable -= RegisterWorldPrefabs;
            ArenaServerRuntime.Shutdown();
            ArenaWorldObjects.Shutdown();
            _harmony?.UnpatchSelf();
        }

        private static void RegisterWorldPrefabs()
        {
            PrefabManager.OnVanillaPrefabsAvailable -= RegisterWorldPrefabs;
            ArenaWorldObjects.RegisterPrefabs();
        }

        internal static void Debug(string message)
        {
            if (ArenaConfig.VerboseLogging?.Value == true)
            {
                Log?.LogInfo("[debug] " + message);
            }
        }
    }
}
