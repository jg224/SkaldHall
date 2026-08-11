using ArenaGuard.Domain;

namespace ArenaGuard.Sessions
{
    /// <summary>
    /// Defines the server-owned transition from staging/resource capture to the
    /// exact Combat Start marker. Countdown cannot begin until arrival is confirmed.
    /// </summary>
    public static class ArenaStartPolicy
    {
        public static bool ShouldTeleportToCombatStart(SessionPhase phase, bool resourceSnapshotReady)
        {
            return phase == SessionPhase.Staging && resourceSnapshotReady;
        }

        public static bool ShouldBeginCountdown(
            SessionPhase phase,
            bool resourceSnapshotReady,
            bool combatStartArrivalConfirmed)
        {
            return ShouldTeleportToCombatStart(phase, resourceSnapshotReady) &&
                   combatStartArrivalConfirmed;
        }
    }
}
