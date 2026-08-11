using HarmonyLib;

namespace ArenaGuard.Runtime
{
    [HarmonyPatch(typeof(Game), "OnDestroy")]
    internal static class ArenaWorldShutdownPatch
    {
        [HarmonyPrefix]
        private static void Prefix()
        {
            ArenaServerRuntime.PrepareForWorldShutdown();
        }
    }
}
