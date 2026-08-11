namespace ArenaGuard.Rules
{
    internal static class ArenaAdminVisualPolicy
    {
        internal static bool ShouldShow(bool isAuthenticatedAdmin, bool localToggleEnabled)
        {
            return isAuthenticatedAdmin && localToggleEnabled;
        }
    }
}
