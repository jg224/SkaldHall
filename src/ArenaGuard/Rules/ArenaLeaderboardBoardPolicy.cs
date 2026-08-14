using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ArenaGuard.Domain;

namespace ArenaGuard.Rules
{
    internal static class ArenaLeaderboardBoardPolicy
    {
        internal const int MaximumEntries = 5;

        internal static readonly ArenaLeaderboardCategory[] DisplayOrder =
        {
            ArenaLeaderboardCategory.BlackForest,
            ArenaLeaderboardCategory.Swamp,
            ArenaLeaderboardCategory.Mountain,
            ArenaLeaderboardCategory.Plains,
            ArenaLeaderboardCategory.Mistlands,
            ArenaLeaderboardCategory.Ashlands,
            ArenaLeaderboardCategory.Gauntlet
        };

        internal static bool IsValidCategory(ArenaLeaderboardCategory category)
        {
            return category >= ArenaLeaderboardCategory.Gauntlet &&
                   category <= ArenaLeaderboardCategory.Ashlands;
        }

        internal static ProgressionCapMode CapMode(ArenaLeaderboardCategory category)
        {
            if (!IsValidCategory(category))
                throw new ArgumentOutOfRangeException(nameof(category));
            return category == ArenaLeaderboardCategory.Gauntlet
                ? ProgressionCapMode.Gauntlet
                : ProgressionCapMode.Biome;
        }

        internal static BiomeTier Biome(ArenaLeaderboardCategory category)
        {
            switch (category)
            {
                case ArenaLeaderboardCategory.Gauntlet:
                case ArenaLeaderboardCategory.BlackForest: return BiomeTier.BlackForest;
                case ArenaLeaderboardCategory.Swamp: return BiomeTier.Swamp;
                case ArenaLeaderboardCategory.Mountain: return BiomeTier.Mountain;
                case ArenaLeaderboardCategory.Plains: return BiomeTier.Plains;
                case ArenaLeaderboardCategory.Mistlands: return BiomeTier.Mistlands;
                case ArenaLeaderboardCategory.Ashlands: return BiomeTier.Ashlands;
                default: throw new ArgumentOutOfRangeException(nameof(category));
            }
        }

        internal static string Label(ArenaLeaderboardCategory category)
        {
            switch (category)
            {
                case ArenaLeaderboardCategory.Gauntlet: return "Gauntlet";
                case ArenaLeaderboardCategory.BlackForest: return "Black Forest";
                case ArenaLeaderboardCategory.Swamp: return "Swamp";
                case ArenaLeaderboardCategory.Mountain: return "Mountain";
                case ArenaLeaderboardCategory.Plains: return "Plains";
                case ArenaLeaderboardCategory.Mistlands: return "Mistlands";
                case ArenaLeaderboardCategory.Ashlands: return "Ashlands";
                default: throw new ArgumentOutOfRangeException(nameof(category));
            }
        }

        internal static LeaderboardKey Key(ArenaLeaderboardCategory category, ChallengeMode mode)
        {
            if (mode != ChallengeMode.BiomeLadder && mode != ChallengeMode.StarLadder)
                throw new ArgumentOutOfRangeException(nameof(mode));
            return new LeaderboardKey
            {
                Mode = mode,
                CapMode = CapMode(category),
                SelectedBiome = Biome(category),
                CustomCreatureKey = string.Empty,
                CustomStars = StarLevel.Base,
                CustomQuantity = 0
            };
        }

        internal static List<LeaderboardEntry> CompletedTopFive(IEnumerable<LeaderboardEntry> entries)
        {
            return (entries ?? Enumerable.Empty<LeaderboardEntry>())
                .Where(entry => entry != null && entry.Completed)
                .GroupBy(entry => entry.PlayerId)
                .Select(group => group
                    .OrderBy(entry => entry.ElapsedMilliseconds)
                    .ThenBy(entry => entry.RecordedUtc)
                    .First())
                .OrderBy(entry => entry.ElapsedMilliseconds)
                .ThenBy(entry => entry.RecordedUtc)
                .ThenBy(entry => entry.PlayerName, StringComparer.OrdinalIgnoreCase)
                .Take(MaximumEntries)
                .ToList();
        }

        internal static string FormatDuration(long milliseconds)
        {
            return ArenaDurationFormatter.FormatHms(milliseconds);
        }

        internal static string FormatDate(LeaderboardEntry entry)
        {
            if (!string.IsNullOrWhiteSpace(entry?.RecordedServerLocal))
                return entry.RecordedServerLocal.Trim();
            if (entry == null || entry.RecordedUtc == default(DateTime))
                return "date unavailable";
            return entry.RecordedUtc.ToUniversalTime()
                .ToString("MMM d, yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture);
        }

        internal static string FormatCompactDate(LeaderboardEntry entry)
        {
            if (!string.IsNullOrWhiteSpace(entry?.RecordedServerLocal) &&
                DateTime.TryParseExact(entry.RecordedServerLocal.Trim(), "yyyy-MM-dd HH:mm",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime local))
            {
                return local.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
            }
            if (entry == null || entry.RecordedUtc == default(DateTime))
            {
                return "date unavailable";
            }
            return entry.RecordedUtc.ToUniversalTime()
                .ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
        }
    }
}
