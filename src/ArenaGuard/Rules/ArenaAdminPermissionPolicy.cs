using ArenaGuard.Domain;

namespace ArenaGuard.Rules
{
    internal static class ArenaAdminPermissionPolicy
    {
        internal static bool CanBuild(bool isAuthenticatedAdmin, bool isCombatant, bool enabled)
        {
            return enabled && isAuthenticatedAdmin && !isCombatant;
        }

        internal static bool CanModifyTerrain(
            bool isAuthenticatedAdmin,
            bool isCombatant,
            bool enabled,
            bool devcommandsEnabled)
        {
            return enabled && devcommandsEnabled && isAuthenticatedAdmin && !isCombatant;
        }

        internal static bool CanPickup(
            ArenaRole role,
            bool isAuthenticatedAdmin,
            bool enabled)
        {
            if (role == ArenaRole.Visitor || role == ArenaRole.Administrator)
            {
                return true;
            }

            return role != ArenaRole.Combatant && enabled && isAuthenticatedAdmin;
        }
    }
}
