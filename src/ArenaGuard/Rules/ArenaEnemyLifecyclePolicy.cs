namespace ArenaGuard.Rules
{
    /// <summary>
    /// A fail-closed lifecycle for runtime-spawned arena enemies. A creature may
    /// only count as defeated after its positive health has remained observable
    /// long enough to prove that network initialization completed.
    /// </summary>
    public enum ArenaEnemyLifecycleDecision
    {
        WaitingForInitialization,
        Alive,
        Defeated,
        InitializationFailed
    }

    public static class ArenaEnemyLifecyclePolicy
    {
        public const double StableHealthSeconds = 0.25d;
        public const double InitializationTimeoutSeconds = 10d;

        public static ArenaEnemyLifecycleDecision Evaluate(
            bool initializationConfirmed,
            bool positiveHealthObserved,
            bool networkRecordExists,
            bool healthValueKnown,
            float health,
            bool deathCallbackObserved,
            double ageSeconds,
            double stableHealthSeconds)
        {
            if (deathCallbackObserved)
            {
                return initializationConfirmed || positiveHealthObserved
                    ? ArenaEnemyLifecycleDecision.Defeated
                    : ArenaEnemyLifecycleDecision.InitializationFailed;
            }

            if (initializationConfirmed)
            {
                if (!networkRecordExists || healthValueKnown && health <= 0f)
                {
                    return ArenaEnemyLifecycleDecision.Defeated;
                }

                return ArenaEnemyLifecycleDecision.Alive;
            }

            if (positiveHealthObserved && stableHealthSeconds >= StableHealthSeconds)
            {
                return ArenaEnemyLifecycleDecision.Alive;
            }

            if (ageSeconds >= InitializationTimeoutSeconds)
            {
                return ArenaEnemyLifecycleDecision.InitializationFailed;
            }

            return ArenaEnemyLifecycleDecision.WaitingForInitialization;
        }
    }
}
