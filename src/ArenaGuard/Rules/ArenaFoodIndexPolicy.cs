using System;

namespace ArenaGuard.Rules
{
    internal enum ArenaFoodTab
    {
        Health,
        Stamina,
        Eitr
    }

    /// <summary>
    /// CraftIndex-compatible food grouping adapted to ArenaGuard's three-tab
    /// picker. Near-even non-eitr foods keep the 20% balanced threshold and
    /// are placed under whichever of health or stamina is slightly higher.
    /// </summary>
    internal static class ArenaFoodIndexPolicy
    {
        internal const float SpecialistRatio = 1.2f;

        internal static ArenaFoodTab Classify(float health, float stamina, float eitr)
        {
            if (eitr > 0f)
            {
                return ArenaFoodTab.Eitr;
            }

            if (health >= stamina * SpecialistRatio)
            {
                return ArenaFoodTab.Health;
            }

            if (stamina >= health * SpecialistRatio)
            {
                return ArenaFoodTab.Stamina;
            }

            return health >= stamina ? ArenaFoodTab.Health : ArenaFoodTab.Stamina;
        }

        internal static bool IsBalanced(float health, float stamina, float eitr)
        {
            return eitr <= 0f &&
                   health < stamina * SpecialistRatio &&
                   stamina < health * SpecialistRatio;
        }

        internal static float Strength(ArenaFoodTab tab, float health, float stamina, float eitr)
        {
            switch (tab)
            {
                case ArenaFoodTab.Health:
                    return Math.Max(0f, health);
                case ArenaFoodTab.Stamina:
                    return Math.Max(0f, stamina);
                default:
                    return Math.Max(0f, eitr);
            }
        }
    }
}
