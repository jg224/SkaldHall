namespace ArenaGuard.Rules
{
    internal static class ArenaWildlifePolicy
    {
        internal static bool ShouldRemoveCharacter(
            bool insideProtectedRadius,
            bool isPlayer,
            bool isTamed,
            bool isArenaMaster,
            bool isArenaEnemy,
            bool isPassiveWildlife)
        {
            return insideProtectedRadius &&
                   !isPlayer &&
                   !isTamed &&
                   !isArenaMaster &&
                   !isArenaEnemy &&
                   isPassiveWildlife;
        }
    }
}
