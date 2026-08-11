using System;
using System.Collections.Generic;

namespace ArenaGuard.Rules
{
    /// <summary>
    /// Builds fair randomized spawn-marker bags. Every configured marker is
    /// used once before a new bag begins, and adjacent bags never repeat the
    /// marker that was used last.
    /// </summary>
    internal static class ArenaSpawnSelectionPolicy
    {
        internal static IList<int> BuildOrder(
            int markerCount,
            int spawnCount,
            int seed,
            int previousMarker = -1)
        {
            if (markerCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(markerCount));
            }
            if (spawnCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(spawnCount));
            }

            var result = new List<int>(spawnCount);
            var random = new Random(seed);
            int lastMarker = previousMarker >= 0 && previousMarker < markerCount ? previousMarker : -1;
            while (result.Count < spawnCount)
            {
                var bag = new List<int>(markerCount);
                for (int marker = 0; marker < markerCount; marker++)
                {
                    bag.Add(marker);
                }
                for (int index = bag.Count - 1; index > 0; index--)
                {
                    int swap = random.Next(index + 1);
                    int value = bag[index];
                    bag[index] = bag[swap];
                    bag[swap] = value;
                }

                if (bag.Count > 1 && bag[0] == lastMarker)
                {
                    int swap = 1 + random.Next(bag.Count - 1);
                    int value = bag[0];
                    bag[0] = bag[swap];
                    bag[swap] = value;
                }

                for (int index = 0; index < bag.Count && result.Count < spawnCount; index++)
                {
                    result.Add(bag[index]);
                    lastMarker = bag[index];
                }
            }
            return result;
        }
    }
}
