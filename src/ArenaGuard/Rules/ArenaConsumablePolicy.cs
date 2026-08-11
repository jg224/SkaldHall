namespace ArenaGuard.Rules
{
    /// <summary>
    /// Game-independent allow-list for items a combatant may consume in an arena.
    /// </summary>
    internal static class ArenaConsumablePolicy
    {
        internal static bool IsAllowed(
            bool isConsumable,
            bool suppliesFood,
            bool restoresHealth,
            bool restoresStamina,
            bool restoresEitr)
        {
            // Arena food is supplied through the mandatory pre-fight picker. Once
            // a player becomes the combatant, personal food is locked and only
            // recovery consumables remain available.
            return isConsumable && !suppliesFood &&
                (restoresHealth || restoresStamina || restoresEitr);
        }
    }
}
