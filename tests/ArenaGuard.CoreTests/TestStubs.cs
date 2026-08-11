namespace BepInEx
{
    internal static class Paths
    {
        internal static string ConfigPath { get; set; }
    }
}

namespace ArenaGuard
{
    internal static class Plugin
    {
        internal const string PluginGuid = "jg224.arenaguard";
        internal static TestLog Log { get; set; }
    }

    internal sealed class TestLog
    {
        internal void LogInfo(object value) { }
        internal void LogWarning(object value) { }
        internal void LogError(object value) { }
    }
}
