namespace ArenaGuard.Rules
{
    /// <summary>
    /// Reconstructs Valheim's unmodified movement factors inside an arena.
    /// Equipment modifiers include legitimate trinket bonuses as well as armor
    /// penalties, so both remain intact while environmental and general mod
    /// multipliers are discarded.
    /// </summary>
    internal static class ArenaMovementSpeedPolicy
    {
        internal static float NormalizeJog(
            bool insideArena,
            float currentFactor,
            float equipmentMovementModifier)
        {
            return insideArena
                ? 1f + equipmentMovementModifier
                : currentFactor;
        }

        internal static float NormalizeRun(
            bool insideArena,
            float currentFactor,
            float runSkillFactor,
            float equipmentMovementModifier)
        {
            return insideArena
                ? (1f + runSkillFactor * 0.25f) *
                  (1f + equipmentMovementModifier * 1.5f)
                : currentFactor;
        }
    }
}
