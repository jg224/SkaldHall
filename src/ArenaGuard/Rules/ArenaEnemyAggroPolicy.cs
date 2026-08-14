using System;

namespace ArenaGuard.Rules
{
    /// <summary>
    /// Keeps arena enemies bound to the authenticated combatant instead of
    /// falling back to Valheim's normal sight and hearing acquisition delay.
    /// </summary>
    internal static class ArenaEnemyAggroPolicy
    {
        internal static readonly TimeSpan UnconfirmedRetryDelay = TimeSpan.FromMilliseconds(100);
        internal static readonly TimeSpan ConfirmedRefreshDelay = TimeSpan.FromMilliseconds(500);

        internal static bool ShouldReassertTarget(
            bool isAuthoritativeOwner,
            bool combatantAvailable,
            bool targetMatches,
            bool isSleeping,
            bool isAlerted,
            bool huntsPlayer)
        {
            return combatantAvailable &&
                   (!isAuthoritativeOwner || !targetMatches || isSleeping || !isAlerted || !huntsPlayer);
        }

        internal static DateTime NextRefreshUtc(DateTime nowUtc, bool targetConfirmed)
        {
            return nowUtc.Add(targetConfirmed ? ConfirmedRefreshDelay : UnconfirmedRetryDelay);
        }
    }
}
