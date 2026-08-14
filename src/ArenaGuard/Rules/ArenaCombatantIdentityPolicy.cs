using ArenaGuard.Domain;

namespace ArenaGuard.Rules
{
    /// <summary>
    /// Resolves the combatant identity used by client and server targeting.
    /// The authoritative server session always wins; dedicated-server clients
    /// fall back to the authenticated arena snapshot they received over RPC.
    /// </summary>
    internal static class ArenaCombatantIdentityPolicy
    {
        internal static long Resolve(
            long authoritativePlayerId,
            long snapshotPlayerId,
            SessionPhase snapshotPhase)
        {
            if (authoritativePlayerId != 0L)
            {
                return authoritativePlayerId;
            }

            return IsCombatantPhase(snapshotPhase) ? snapshotPlayerId : 0L;
        }

        internal static bool IsCombatantPhase(SessionPhase phase)
        {
            return phase == SessionPhase.Staging ||
                   phase == SessionPhase.Countdown ||
                   phase == SessionPhase.Fighting ||
                   phase == SessionPhase.Intermission;
        }
    }
}
