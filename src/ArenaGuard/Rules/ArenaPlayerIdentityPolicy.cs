namespace ArenaGuard.Rules
{
    /// <summary>Valheim player IDs are signed; zero alone represents a missing identity.</summary>
    internal static class ArenaPlayerIdentityPolicy
    {
        internal static bool IsValid(long playerId)
        {
            return playerId != 0L;
        }
    }
}
