using System;
using System.Collections.Generic;

namespace ArenaGuard.Rules
{
    /// <summary>Game-independent validation for the mandatory arena food loadout.</summary>
    internal static class ArenaFoodSelectionPolicy
    {
        internal const int RequiredFoodCount = 3;

        internal static bool IsValid(
            IEnumerable<string> selectedPrefabNames,
            ISet<string> eligiblePrefabNames)
        {
            if (selectedPrefabNames == null || eligiblePrefabNames == null)
            {
                return false;
            }

            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (string value in selectedPrefabNames)
            {
                string prefabName = value?.Trim();
                if (string.IsNullOrWhiteSpace(prefabName) ||
                    !unique.Add(prefabName) ||
                    !eligiblePrefabNames.Contains(prefabName))
                {
                    return false;
                }
            }
            return unique.Count == RequiredFoodCount;
        }
    }
}
