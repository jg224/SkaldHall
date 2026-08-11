using System;
using System.Collections.Generic;
using System.Linq;
using ArenaGuard.Domain;

namespace ArenaGuard.Challenges
{
    /// <summary>
    /// Builds immutable challenge plans from a curated, validated creature roster.
    /// This type deliberately has no Unity or Valheim assembly dependencies.
    /// </summary>
    public sealed class ChallengeCatalog
    {
        public const int DefaultPreparationSeconds = 3;
        public const int MinimumCustomQuantity = 1;
        public const int MaximumCustomQuantity = 10;

        private readonly List<CreatureDefinition> _roster;

        public ChallengeCatalog()
            : this(LoadRoster(), 3L)
        {
        }

        public ChallengeCatalog(IEnumerable<CreatureDefinition> roster, long rosterRevision)
        {
            if (roster == null)
            {
                throw new ArgumentNullException(nameof(roster));
            }

            if (rosterRevision < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(rosterRevision), "Roster revision must be positive.");
            }

            _roster = roster.Select(CloneCreature).ToList();
            ValidateRoster(_roster);
            RosterRevision = rosterRevision;
        }

        public long RosterRevision { get; }

        public static IList<CreatureDefinition> LoadRoster()
        {
            return new List<CreatureDefinition>
            {
                Creature("greydwarf", "Greydwarf", "Greydwarf", BiomeTier.BlackForest, 10, iconPrefab: "TrophyGreydwarf"),
                Creature("greydwarf_shaman", "Greydwarf_Shaman", "Greydwarf Shaman", BiomeTier.BlackForest, 20, iconPrefab: "TrophyGreydwarfShaman"),
                Creature("skeleton", "Skeleton", "Skeleton", BiomeTier.BlackForest, 30, iconPrefab: "TrophySkeleton"),
                Creature("rancid_remains", "Skeleton_Poison", "Rancid Remains", BiomeTier.BlackForest, 40, iconPrefab: "TrophySkeletonPoison"),
                Creature("ghost", "Ghost", "Ghost", BiomeTier.BlackForest, 50, iconPrefab: "TrophyGhost"),
                Creature("greydwarf_brute", "Greydwarf_Elite", "Greydwarf Brute", BiomeTier.BlackForest, 60, iconPrefab: "TrophyGreydwarfBrute"),
                Creature("troll", "Troll", "Troll", BiomeTier.BlackForest, 70, iconPrefab: "TrophyFrostTroll"),
                Creature("bear", "Bjorn", "Bear", BiomeTier.BlackForest, 80, iconPrefab: "TrophyBjorn"),
                Creature("brenna", "Skeleton_Hildir", "Brenna", BiomeTier.BlackForest, 90, true, "TrophySkeletonHildir"),

                Creature("leech", "Leech", "Leech", BiomeTier.Swamp, 10, iconPrefab: "TrophyLeech"),
                Creature("blob", "Blob", "Blob", BiomeTier.Swamp, 20, iconPrefab: "TrophyBlob"),
                Creature("draugr", "Draugr", "Draugr", BiomeTier.Swamp, 30, iconPrefab: "TrophyDraugr"),
                Creature("draugr_archer", "Draugr_Ranged", "Draugr Archer", BiomeTier.Swamp, 40, iconPrefab: "TrophyDraugr"),
                Creature("surtling", "Surtling", "Surtling", BiomeTier.Swamp, 50, iconPrefab: "TrophySurtling"),
                Creature("wraith", "Wraith", "Wraith", BiomeTier.Swamp, 60, iconPrefab: "TrophyWraith"),
                Creature("draugr_elite", "Draugr_Elite", "Draugr Elite", BiomeTier.Swamp, 70, iconPrefab: "TrophyDraugrElite"),
                Creature("oozer", "BlobElite", "Oozer", BiomeTier.Swamp, 80, iconPrefab: "TrophyBlob"),
                Creature("abomination", "Abomination", "Abomination", BiomeTier.Swamp, 90, iconPrefab: "TrophyAbomination"),

                Creature("wolf", "Wolf", "Wolf", BiomeTier.Mountain, 10, iconPrefab: "TrophyWolf"),
                Creature("drake", "Hatchling", "Drake", BiomeTier.Mountain, 20, iconPrefab: "TrophyHatchling"),
                Creature("bat", "Bat", "Bat", BiomeTier.Mountain, 30, iconPrefab: "TrophyUlv"),
                Creature("ulv", "Ulv", "Ulv", BiomeTier.Mountain, 40, iconPrefab: "TrophyUlv"),
                Creature("cultist", "Fenring_Cultist", "Cultist", BiomeTier.Mountain, 50, iconPrefab: "TrophyCultist"),
                Creature("fenring", "Fenring", "Fenring", BiomeTier.Mountain, 60, iconPrefab: "TrophyFenring"),
                Creature("stone_golem", "StoneGolem", "Stone Golem", BiomeTier.Mountain, 70, iconPrefab: "TrophySGolem"),
                Creature("geirrhafa", "Fenring_Cultist_Hildir", "Geirrhafa", BiomeTier.Mountain, 80, true, "TrophyCultist_Hildir"),

                Creature("deathsquito", "Deathsquito", "Deathsquito", BiomeTier.Plains, 10, iconPrefab: "TrophyDeathsquito"),
                Creature("fuling", "Goblin", "Fuling", BiomeTier.Plains, 20, iconPrefab: "TrophyGoblin"),
                Creature("fuling_archer", "GoblinArcher", "Fuling Archer", BiomeTier.Plains, 30, iconPrefab: "TrophyGoblin"),
                Creature("growth", "BlobTar", "Growth", BiomeTier.Plains, 40, iconPrefab: "TrophyGrowth"),
                Creature("fuling_shaman", "GoblinShaman", "Fuling Shaman", BiomeTier.Plains, 50, iconPrefab: "TrophyGoblinShaman"),
                Creature("fuling_berserker", "GoblinBrute", "Fuling Berserker", BiomeTier.Plains, 60, iconPrefab: "TrophyGoblinBrute"),
                Creature("cursed_bear", "Unbjorn", "Cursed Bear", BiomeTier.Plains, 70, iconPrefab: "TrophyBjornUndead"),
                Creature("zil_and_thungr", "GoblinShaman_Hildir_nochest", "Zil & Thungr", BiomeTier.Plains, 80, true,
                    "TrophyGoblinBruteBrosShaman", "GoblinBrute_Hildir", "TrophyGoblinBruteBrosBrute"),

                Creature("tick", "Tick", "Tick", BiomeTier.Mistlands, 10, iconPrefab: "TrophyTick"),
                Creature("seeker_brood", "SeekerBrood", "Seeker Brood", BiomeTier.Mistlands, 20, iconPrefab: "TrophySeeker"),
                Creature("seeker", "Seeker", "Seeker", BiomeTier.Mistlands, 30, iconPrefab: "TrophySeeker"),
                Creature("dvergr", "Dverger", "Dvergr Rogue", BiomeTier.Mistlands, 40, iconPrefab: "TrophyDvergr"),
                Creature("dvergr_fire_mage", "DvergerMageFire", "Dvergr Fire Mage", BiomeTier.Mistlands, 50, iconPrefab: "TrophyDvergr"),
                Creature("dvergr_ice_mage", "DvergerMageIce", "Dvergr Ice Mage", BiomeTier.Mistlands, 60, iconPrefab: "TrophyDvergr"),
                Creature("dvergr_support_mage", "DvergerMageSupport", "Dvergr Support Mage", BiomeTier.Mistlands, 70, iconPrefab: "TrophyDvergr"),
                Creature("gjall", "Gjall", "Gjall", BiomeTier.Mistlands, 80, iconPrefab: "TrophyGjall"),
                Creature("seeker_soldier", "SeekerBrute", "Seeker Soldier", BiomeTier.Mistlands, 90, iconPrefab: "TrophySeekerBrute"),

                Creature("charred_twitcher", "Charred_Twitcher", "Charred Twitcher", BiomeTier.Ashlands, 10, iconPrefab: "TrophyCharredMelee"),
                Creature("charred_archer", "Charred_Archer", "Charred Marksman", BiomeTier.Ashlands, 20, iconPrefab: "TrophyCharredArcher"),
                Creature("charred_warrior", "Charred_Melee", "Charred Warrior", BiomeTier.Ashlands, 30, iconPrefab: "TrophyCharredMelee"),
                Creature("charred_warlock", "Charred_Mage", "Charred Warlock", BiomeTier.Ashlands, 40, iconPrefab: "TrophyCharredMage"),
                Creature("volture", "Volture", "Volture", BiomeTier.Ashlands, 50, iconPrefab: "TrophyVolture"),
                Creature("lavablob", "BlobLava", "Lava Blob", BiomeTier.Ashlands, 60, iconPrefab: "TrophyBlob"),
                Creature("asksvin", "Asksvin", "Asksvin", BiomeTier.Ashlands, 70, iconPrefab: "TrophyAsksvin"),
                Creature("ashlands_dvergr", "DvergerAshlands", "Ashlands Dvergr", BiomeTier.Ashlands, 80, iconPrefab: "TrophyDvergr"),
                Creature("morgen", "Morgen", "Morgen", BiomeTier.Ashlands, 90, iconPrefab: "TrophyMorgen"),
                Creature("fallen_valkyrie", "FallenValkyrie", "Fallen Valkyrie", BiomeTier.Ashlands, 100, iconPrefab: "TrophyFallenValkyrie"),
                Creature("lord_reto", "Charred_Melee_Dyrnwyn", "Lord Reto", BiomeTier.Ashlands, 110, true, "TrophyCharredMelee")
            };
        }

        public IList<CreatureDefinition> ListCreatures(BiomeTier biome)
        {
            return OrderedEnabledRoster()
                .Where(creature => creature.Biome == biome)
                .Select(CloneCreature)
                .ToList();
        }

        public IList<CreatureDefinition> GetRoster()
        {
            return _roster.Select(CloneCreature).ToList();
        }

        public ChallengePlan BuildPlan(ChallengeRequest request, BiomeTier highestUnlockedBiome)
        {
            ValidateRequest(request);
            ValidateBiome(highestUnlockedBiome, nameof(highestUnlockedBiome));

            var enabled = OrderedEnabledRoster().ToList();
            if (enabled.Count == 0)
            {
                throw new InvalidOperationException("The enabled arena roster is empty.");
            }

            var highestPopulatedBiome = enabled.Max(creature => creature.Biome);
            IEnumerable<CreatureDefinition> ladderRoster = request.CapMode == ProgressionCapMode.Biome
                ? enabled.Where(creature => creature.Biome == request.SelectedBiome)
                : enabled;
            var scoped = ladderRoster.ToList();
            var effectiveCap = request.CapMode == ProgressionCapMode.Biome
                ? request.SelectedBiome
                : highestPopulatedBiome;

            var encounters = new List<EncounterDefinition>();
            switch (request.Mode)
            {
                case ChallengeMode.BiomeLadder:
                    foreach (var creature in scoped)
                    {
                        RequireSupportedStar(creature, StarLevel.Base, "Biome ladder");
                        encounters.Add(CreateEncounter(encounters.Count, creature, StarLevel.Base, 1));
                    }

                    break;

                case ChallengeMode.StarLadder:
                    foreach (var creature in scoped)
                    {
                        foreach (var star in new[] { StarLevel.Base, StarLevel.OneStar, StarLevel.TwoStar })
                        {
                            RequireSupportedStar(creature, star, "Star ladder");
                            encounters.Add(CreateEncounter(encounters.Count, creature, star, 1));
                        }
                    }

                    break;

                case ChallengeMode.CustomEncounter:
                    string validationError;
                    if (!TryValidateCustomSelection(request.CustomSelection, out validationError))
                    {
                        throw new ArgumentException(validationError, nameof(request));
                    }

                    var selected = enabled.Single(creature =>
                        string.Equals(creature.CreatureKey, request.CustomSelection.CreatureKey.Trim(), StringComparison.OrdinalIgnoreCase));
                    encounters.Add(CreateEncounter(
                        0,
                        selected,
                        request.CustomSelection.Stars,
                        request.CustomSelection.Quantity));
                    effectiveCap = selected.Biome;
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(request), "Unknown challenge mode.");
            }

            if (encounters.Count == 0)
            {
                throw new InvalidOperationException(request.CapMode == ProgressionCapMode.Biome
                    ? "The selected biome contains no enabled arena encounters."
                    : "The selected challenge scope contains no enabled encounters.");
            }

            return new ChallengePlan
            {
                PlanId = NewId(),
                Mode = request.Mode,
                CapMode = request.CapMode,
                HighestBiome = effectiveCap,
                RosterRevision = RosterRevision,
                Encounters = encounters
            };
        }

        public bool ValidateCustomSelection(CustomEncounterSelection selection)
        {
            string ignored;
            return TryValidateCustomSelection(selection, out ignored);
        }

        public bool TryValidateCustomSelection(CustomEncounterSelection selection, out string error)
        {
            if (selection == null)
            {
                error = "A custom encounter selection is required.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(selection.CreatureKey))
            {
                error = "A creature must be selected.";
                return false;
            }

            if (selection.Quantity < MinimumCustomQuantity || selection.Quantity > MaximumCustomQuantity)
            {
                error = "Custom encounter quantity must be between 1 and 10.";
                return false;
            }

            if (!Enum.IsDefined(typeof(StarLevel), selection.Stars))
            {
                error = "The selected star level is invalid.";
                return false;
            }

            var creature = _roster.FirstOrDefault(candidate =>
                candidate.Enabled &&
                string.Equals(candidate.CreatureKey, selection.CreatureKey.Trim(), StringComparison.OrdinalIgnoreCase));
            if (creature == null)
            {
                error = "The selected creature is not in the enabled arena roster.";
                return false;
            }

            if (!creature.SupportedStars.Contains(selection.Stars))
            {
                error = "The selected creature does not support that star level.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private IEnumerable<CreatureDefinition> OrderedEnabledRoster()
        {
            return _roster
                .Where(creature => creature.Enabled)
                .OrderBy(creature => creature.Biome)
                .ThenBy(creature => creature.IsMiniboss ? 1 : 0)
                .ThenBy(creature => creature.DifficultyOrder)
                .ThenBy(creature => creature.CreatureKey, StringComparer.OrdinalIgnoreCase);
        }

        private static void ValidateRequest(ChallengeRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (string.IsNullOrWhiteSpace(request.RequestId))
            {
                throw new ArgumentException("Request ID is required.", nameof(request));
            }

            if (string.IsNullOrWhiteSpace(request.ArenaId))
            {
                throw new ArgumentException("Arena ID is required.", nameof(request));
            }

            if (request.PlayerId == 0)
            {
                throw new ArgumentException("Player ID is required.", nameof(request));
            }

            if (!Enum.IsDefined(typeof(ChallengeMode), request.Mode))
            {
                throw new ArgumentException("Challenge mode is invalid.", nameof(request));
            }

            if (!Enum.IsDefined(typeof(ProgressionCapMode), request.CapMode))
            {
                throw new ArgumentException("Challenge scope is invalid.", nameof(request));
            }

            if (request.CapMode == ProgressionCapMode.Biome &&
                !Enum.IsDefined(typeof(BiomeTier), request.SelectedBiome))
            {
                throw new ArgumentException("Selected biome is invalid.", nameof(request));
            }
        }

        private static void ValidateRoster(IList<CreatureDefinition> roster)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var creature in roster)
            {
                if (creature == null)
                {
                    throw new ArgumentException("The creature roster cannot contain null entries.", nameof(roster));
                }

                if (string.IsNullOrWhiteSpace(creature.CreatureKey) ||
                    string.IsNullOrWhiteSpace(creature.PrefabName) ||
                    string.IsNullOrWhiteSpace(creature.DisplayName))
                {
                    throw new ArgumentException("Every roster entry requires a key, prefab name, and display name.", nameof(roster));
                }

                creature.CreatureKey = creature.CreatureKey.Trim();
                creature.PrefabName = creature.PrefabName.Trim();
                creature.DisplayName = creature.DisplayName.Trim();
                ValidateBiome(creature.Biome, nameof(roster));

                if (!keys.Add(creature.CreatureKey))
                {
                    throw new ArgumentException("Creature keys must be unique (case-insensitive).", nameof(roster));
                }

                if (creature.DifficultyOrder < 0)
                {
                    throw new ArgumentException("Creature difficulty order cannot be negative.", nameof(roster));
                }

                if (creature.SupportedStars == null || creature.SupportedStars.Count == 0 ||
                    creature.SupportedStars.Distinct().Count() != creature.SupportedStars.Count ||
                    creature.SupportedStars.Any(star => !Enum.IsDefined(typeof(StarLevel), star)))
                {
                    throw new ArgumentException("Each creature needs a unique, valid supported-star list.", nameof(roster));
                }
            }
        }

        private static void ValidateBiome(BiomeTier biome, string parameterName)
        {
            if (!Enum.IsDefined(typeof(BiomeTier), biome))
            {
                throw new ArgumentOutOfRangeException(parameterName, "Biome is invalid.");
            }
        }

        private static void RequireSupportedStar(CreatureDefinition creature, StarLevel star, string modeName)
        {
            if (!creature.SupportedStars.Contains(star))
            {
                throw new InvalidOperationException(
                    modeName + " cannot include '" + creature.CreatureKey + "' because it does not support " + star + ".");
            }
        }

        private static EncounterDefinition CreateEncounter(
            int sequence,
            CreatureDefinition creature,
            StarLevel stars,
            int quantity)
        {
            return new EncounterDefinition
            {
                Sequence = sequence,
                CreatureKey = creature.CreatureKey,
                Biome = creature.Biome,
                Stars = stars,
                Quantity = quantity,
                IsMiniboss = creature.IsMiniboss,
                PreparationSeconds = DefaultPreparationSeconds
            };
        }

        private static CreatureDefinition Creature(
            string key,
            string prefab,
            string displayName,
            BiomeTier biome,
            int difficulty,
            bool isMiniboss = false,
            string iconPrefab = null,
            string secondaryPrefab = null,
            string secondaryIconPrefab = null)
        {
            return new CreatureDefinition
            {
                CreatureKey = key,
                PrefabName = prefab,
                SecondaryPrefabName = secondaryPrefab,
                IconPrefabName = iconPrefab,
                SecondaryIconPrefabName = secondaryIconPrefab,
                DisplayName = displayName,
                Biome = biome,
                DifficultyOrder = difficulty,
                IsMiniboss = isMiniboss,
                Enabled = true,
                SupportedStars = new List<StarLevel>
                {
                    StarLevel.Base,
                    StarLevel.OneStar,
                    StarLevel.TwoStar
                }
            };
        }

        private static CreatureDefinition CloneCreature(CreatureDefinition creature)
        {
            if (creature == null)
            {
                return null;
            }

            return new CreatureDefinition
            {
                CreatureKey = creature.CreatureKey,
                PrefabName = creature.PrefabName,
                SecondaryPrefabName = creature.SecondaryPrefabName,
                IconPrefabName = creature.IconPrefabName,
                SecondaryIconPrefabName = creature.SecondaryIconPrefabName,
                DisplayName = creature.DisplayName,
                Biome = creature.Biome,
                DifficultyOrder = creature.DifficultyOrder,
                IsMiniboss = creature.IsMiniboss,
                Enabled = creature.Enabled,
                SupportedStars = creature.SupportedStars == null
                    ? null
                    : new List<StarLevel>(creature.SupportedStars)
            };
        }

        private static string NewId()
        {
            return Guid.NewGuid().ToString("N").ToLowerInvariant();
        }
    }
}
