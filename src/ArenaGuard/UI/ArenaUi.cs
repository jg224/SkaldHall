using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ArenaGuard.Domain;
using ArenaGuard.Rules;
using ArenaGuard.World;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;

namespace ArenaGuard.UI
{
    public enum ArenaUiAdminMutationKind
    {
        SaveDefinition,
        EnableArena,
        DisableArena,
        SetTerrainPermission,
        SetBuildingPermission,
        SetPickupPermission
    }

    public enum ArenaAdminPermissionKind
    {
        Terrain,
        Building,
        DroppedItemPickup
    }

    public sealed class ArenaUiAdminMutation
    {
        public string ArenaId;
        public ArenaUiAdminMutationKind Kind;
        public string DisplayName;
        public float CombatRadius;
        public float ProtectedRadius;
        public bool Enabled;
    }

    /// <summary>
    /// Native Unity/Jötunn UI for ArenaGuard. The UI only creates requests; all
    /// authority-sensitive fields are resolved again by the server RPC handler.
    /// </summary>
    public static class ArenaUi
    {
        private static GameObject _modalRoot;
        private static GameObject _creaturePickerRoot;
        private static GameObject _biomePickerRoot;
        private static GameObject _hudRoot;
        private static GameObject _queueRoot;
        private static Text _hudText;
        private static Text _queueText;
        private static Button _forfeitButton;
        private static Button _setupVisibilityButton;
        private static Button _terrainPermissionButton;
        private static Button _buildingPermissionButton;
        private static Button _pickupPermissionButton;
        private static bool _inputBlocked;
        private static GameObject _driverHost;
        private static string _activeArenaId = string.Empty;
        private static string _hudArenaId = string.Empty;
        private static string _queueArenaId = string.Empty;
        private static ArenaClientSnapshot _hudSnapshot;
        private static long _hudElapsedBaseMilliseconds;
        private static float _hudElapsedBaseTime;
        private static float _nextHudTimerRefreshTime;
        private static DateTime _queueDeadlineUtc;
        private static List<CreatureDefinition> _creatures = new List<CreatureDefinition>();
        private static ChallengeMenuState _challengeMenu;
        private static bool _combinedLeaderboardActive;
        private static ProgressionCapMode _leaderboardCapMode;
        private static BiomeTier _leaderboardSelectedBiome;
        private static List<LeaderboardEntry> _biomeLeaderboardEntries;
        private static List<LeaderboardEntry> _starLeaderboardEntries;
        private static List<BiomeTier> _ladderBiomes = new List<BiomeTier>();
        private static FoodPreparationState _foodPreparation;
        private static readonly Dictionary<string, Sprite> CreatureIconCache =
            new Dictionary<string, Sprite>(StringComparer.Ordinal);

        private static readonly Color SectionColor = new Color(0.96f, 0.64f, 0.22f, 1f);
        private static readonly Color SelectedButtonColor = new Color(1f, 0.56f, 0.12f, 1f);
        private static readonly Color NormalButtonColor = new Color(0.58f, 0.56f, 0.52f, 1f);
        private static readonly Color DisabledTextColor = new Color(0.5f, 0.5f, 0.5f, 1f);

        public static Action<ChallengeRequest> ChallengeRequested;
        public static Action QueueAccepted;
        public static Action ForfeitRequested;
        public static Action<string, LeaderboardKey> LeaderboardRequested;
        public static Action<ArenaUiAdminMutation> AdminMutationRequested;
        public static Func<ArenaAdminPermissionKind, bool> AdminPermissionResolver;
        public static Func<IEnumerable<CreatureDefinition>> CreatureListResolver;
        public static Func<IList<string>, bool> FoodPreparationConfirmed;
        public static Action FoodPreparationCancelled;

        public static void OpenChallengeMenu(string arenaId)
        {
            if (string.IsNullOrWhiteSpace(arenaId) || !CanDraw())
            {
                return;
            }

            CloseModal();
            _activeArenaId = arenaId;
            _creatures = (CreatureListResolver?.Invoke() ?? Enumerable.Empty<CreatureDefinition>())
                .Where(c => c != null && c.Enabled)
                .OrderBy(c => c.Biome)
                .ThenBy(c => c.IsMiniboss)
                .ThenBy(c => c.DifficultyOrder)
                .ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _ladderBiomes = _creatures.Select(creature => creature.Biome)
                .Distinct()
                .OrderBy(biome => biome)
                .ToList();

            _challengeMenu = new ChallengeMenuState
            {
                ArenaId = arenaId,
                Mode = ChallengeMode.BiomeLadder,
                CapMode = ProgressionCapMode.Gauntlet,
                SelectedBiome = _ladderBiomes.Count == 0 ? BiomeTier.BlackForest : _ladderBiomes[0],
                SelectedCreature = _creatures.FirstOrDefault(),
                Stars = StarLevel.Base,
                Quantity = 1
            };

            _modalRoot = CreatePanel("ARENA CHALLENGE", 620f, 690f);
            AddLabel(_modalRoot.transform, "Choose your challenge", 19, new Vector2(0f, 249f), 560f, 32f).color = SectionColor;
            AddSectionLabel(_modalRoot.transform, "CHOOSE MODE", new Vector2(0f, 215f), 550f);

            _challengeMenu.ModeButtons = new[]
            {
                AddModeCard(_modalRoot.transform, "<b>BIOME LADDER</b>\n<size=15>Fight through\nbiome waves</size>",
                    new Vector2(-190f, 151f), () => SetChallengeMode(ChallengeMode.BiomeLadder)),
                AddModeCard(_modalRoot.transform, "<b>STAR LADDER</b>\n<size=15>Base, 1-star,\nthen 2-star</size>",
                    new Vector2(0f, 151f), () => SetChallengeMode(ChallengeMode.StarLadder)),
                AddModeCard(_modalRoot.transform, "<b>SPECIFIC MONSTER</b>\n<size=15>Build a custom\nfight</size>",
                    new Vector2(190f, 151f), () => SetChallengeMode(ChallengeMode.CustomEncounter))
            };

            AddSectionLabel(_modalRoot.transform, "PROGRESSION", new Vector2(0f, 79f), 550f);
            _challengeMenu.CapButtons = new[]
            {
                AddButton(_modalRoot.transform, "GAUNTLET — Every biome", new Vector2(-142f, 45f), 260f, 38f,
                    () => SetProgressionCap(ProgressionCapMode.Gauntlet)),
                AddButton(_modalRoot.transform, "BIOME — Choose one", new Vector2(142f, 45f), 260f, 38f,
                    () => SetProgressionCap(ProgressionCapMode.Biome))
            };

            AddSectionLabel(_modalRoot.transform, "ENCOUNTER SETUP", new Vector2(0f, 3f), 550f);
            _challengeMenu.CreatureIcon = AddImage(_modalRoot.transform, "SelectedCreatureIcon",
                new Vector2(-245f, -47f), 54f, 54f);
            _challengeMenu.SecondaryCreatureIcon = AddImage(_modalRoot.transform, "SelectedSecondaryCreatureIcon",
                new Vector2(-213f, -47f), 48f, 48f);
            _challengeMenu.CreatureSummary = AddLabel(_modalRoot.transform, string.Empty, 18,
                new Vector2(-43f, -47f), 350f, 58f);
            _challengeMenu.CreatureSummary.alignment = TextAnchor.MiddleLeft;
            _challengeMenu.ChangeCreatureButton = AddButton(_modalRoot.transform, "Change",
                new Vector2(235f, -47f), 112f, 40f, OpenCreaturePicker);
            _challengeMenu.ScopeSummary = AddLabel(_modalRoot.transform,
                "Every biome • Black Forest → Ashlands", 19, new Vector2(0f, -47f), 500f, 48f, true);
            _challengeMenu.BiomeButton = AddButton(_modalRoot.transform, "Biome: Black Forest",
                new Vector2(0f, -47f), 320f, 42f, OpenBiomePicker);

            _challengeMenu.StarsLabel = AddLabel(_modalRoot.transform, "STARS", 17,
                new Vector2(-175f, -91f), 180f, 26f);
            _challengeMenu.StarButtons = new[]
            {
                AddButton(_modalRoot.transform, "Base", new Vector2(-220f, -125f), 82f, 38f,
                    () => SetStarLevel(StarLevel.Base)),
                AddButton(_modalRoot.transform, "1 Star", new Vector2(-130f, -125f), 82f, 38f,
                    () => SetStarLevel(StarLevel.OneStar)),
                AddButton(_modalRoot.transform, "2 Stars", new Vector2(-40f, -125f), 82f, 38f,
                    () => SetStarLevel(StarLevel.TwoStar))
            };

            _challengeMenu.QuantityLabel = AddLabel(_modalRoot.transform, "QUANTITY", 17,
                new Vector2(165f, -91f), 190f, 26f);
            _challengeMenu.DecreaseQuantityButton = AddButton(_modalRoot.transform, "−",
                new Vector2(105f, -125f), 44f, 38f, () => ChangeQuantity(-1));
            _challengeMenu.QuantityText = AddLabel(_modalRoot.transform, "1", 22,
                new Vector2(165f, -125f), 62f, 38f, true);
            _challengeMenu.IncreaseQuantityButton = AddButton(_modalRoot.transform, "+",
                new Vector2(225f, -125f), 44f, 38f, () => ChangeQuantity(1));

            _challengeMenu.EncounterSummary = AddLabel(_modalRoot.transform, string.Empty, 17,
                new Vector2(0f, -179f), 550f, 42f, true);
            AddButton(_modalRoot.transform, "JOIN QUEUE", new Vector2(0f, -232f), 340f, 50f,
                SubmitChallenge);
            AddButton(_modalRoot.transform, "Leaderboard", new Vector2(-120f, -291f), 210f, 40f,
                RequestChallengeLeaderboard);
            AddButton(_modalRoot.transform, "Close", new Vector2(120f, -291f), 210f, 40f, CloseModal);
            RefreshChallengeMenu();
        }

        private static void SetChallengeMode(ChallengeMode mode)
        {
            if (_challengeMenu == null)
            {
                return;
            }
            _challengeMenu.Mode = mode;
            if (mode != ChallengeMode.CustomEncounter)
            {
                if (_creaturePickerRoot != null)
                {
                    CloseCreaturePicker();
                }
            }
            RefreshChallengeMenu();
            if (mode == ChallengeMode.CustomEncounter)
            {
                OpenCreaturePicker();
            }
        }

        private static void SetProgressionCap(ProgressionCapMode capMode)
        {
            if (_challengeMenu == null)
            {
                return;
            }
            _challengeMenu.CapMode = capMode;
            RefreshChallengeMenu();
            if (capMode == ProgressionCapMode.Biome)
            {
                OpenBiomePicker();
            }
        }

        private static void SetStarLevel(StarLevel stars)
        {
            if (_challengeMenu?.SelectedCreature == null ||
                _challengeMenu.SelectedCreature.SupportedStars == null ||
                !_challengeMenu.SelectedCreature.SupportedStars.Contains(stars))
            {
                return;
            }
            _challengeMenu.Stars = stars;
            RefreshChallengeMenu();
        }

        private static void ChangeQuantity(int amount)
        {
            if (_challengeMenu == null)
            {
                return;
            }
            _challengeMenu.Quantity = Mathf.Clamp(_challengeMenu.Quantity + amount, 1, 10);
            RefreshChallengeMenu();
        }

        private static void RefreshChallengeMenu()
        {
            ChallengeMenuState state = _challengeMenu;
            if (state == null || _modalRoot == null)
            {
                return;
            }

            for (int index = 0; index < state.ModeButtons.Length; index++)
            {
                SetButtonSelected(state.ModeButtons[index], (int)state.Mode == index + 1);
            }
            SetButtonSelected(state.CapButtons[0], state.CapMode == ProgressionCapMode.Gauntlet);
            SetButtonSelected(state.CapButtons[1], state.CapMode == ProgressionCapMode.Biome);

            bool custom = state.Mode == ChallengeMode.CustomEncounter;
            bool ladder = !custom;
            bool hasCreature = state.SelectedCreature != null;
            foreach (Button capButton in state.CapButtons)
            {
                capButton.interactable = ladder;
            }
            bool biomeScope = ladder && state.CapMode == ProgressionCapMode.Biome;
            if (!biomeScope && _biomePickerRoot != null)
            {
                CloseBiomePicker();
            }
            state.BiomeButton.gameObject.SetActive(biomeScope);
            state.ScopeSummary.gameObject.SetActive(ladder && !biomeScope);
            if (biomeScope && _ladderBiomes.Count > 0)
            {
                int biomeIndex = Math.Max(0, _ladderBiomes.IndexOf(state.SelectedBiome));
                state.SelectedBiome = _ladderBiomes[biomeIndex];
                UpdateButtonLabel(state.BiomeButton,
                    "Biome: " + BiomeLabel(state.SelectedBiome) + "  ›");
            }
            if (ladder && !biomeScope)
            {
                state.ScopeSummary.text = "Every biome • " +
                    (_ladderBiomes.Count == 0
                        ? "Roster unavailable"
                        : BiomeLabel(_ladderBiomes.First()) + " → " + BiomeLabel(_ladderBiomes.Last()));
            }
            state.CreatureIcon.gameObject.SetActive(custom);
            state.SecondaryCreatureIcon.gameObject.SetActive(custom);
            state.CreatureSummary.gameObject.SetActive(custom);
            state.ChangeCreatureButton.gameObject.SetActive(custom);
            state.ChangeCreatureButton.interactable = custom && _creatures.Count != 0;
            state.CreatureSummary.color = Color.white;
            state.StarsLabel.gameObject.SetActive(custom);
            state.QuantityLabel.gameObject.SetActive(custom);
            state.DecreaseQuantityButton.interactable = custom && state.Quantity > 1;
            state.IncreaseQuantityButton.interactable = custom && state.Quantity < 10;
            state.QuantityText.gameObject.SetActive(custom);
            state.DecreaseQuantityButton.gameObject.SetActive(custom);
            state.IncreaseQuantityButton.gameObject.SetActive(custom);

            for (int index = 0; index < state.StarButtons.Length; index++)
            {
                StarLevel stars = (StarLevel)index;
                bool supported = hasCreature && state.SelectedCreature.SupportedStars != null &&
                                  state.SelectedCreature.SupportedStars.Contains(stars);
                state.StarButtons[index].gameObject.SetActive(custom);
                state.StarButtons[index].interactable = custom && supported;
                SetButtonSelected(state.StarButtons[index], custom && state.Stars == stars);
            }

            state.CreatureSummary.text = hasCreature
                ? state.SelectedCreature.DisplayName + "\n<color=#9FD66B>" + BiomeLabel(state.SelectedCreature.Biome) +
                  (state.SelectedCreature.IsMiniboss ? " • Miniboss" : string.Empty) + "</color>"
                : "Roster unavailable";
            SetCreatureIcons(state.CreatureIcon, state.SecondaryCreatureIcon, state.SelectedCreature);
            state.QuantityText.text = state.Quantity.ToString(CultureInfo.InvariantCulture);
            switch (state.Mode)
            {
                case ChallengeMode.BiomeLadder:
                    state.EncounterSummary.text = state.CapMode == ProgressionCapMode.Gauntlet
                        ? "GAUNTLET: EVERY BIOME • WEAKEST TO STRONGEST"
                        : "BIOME: " + BiomeLabel(state.SelectedBiome).ToUpperInvariant() +
                          " • EVERY MOB • WEAKEST TO STRONGEST";
                    break;
                case ChallengeMode.StarLadder:
                    state.EncounterSummary.text = (state.CapMode == ProgressionCapMode.Gauntlet
                            ? "GAUNTLET: EVERY BIOME"
                            : "BIOME: " + BiomeLabel(state.SelectedBiome).ToUpperInvariant()) +
                        " • BASE → 1 STAR → 2 STARS";
                    break;
                default:
                    state.EncounterSummary.text = hasCreature
                        ? "ENCOUNTER: " + state.Quantity + " × " + StarLabel(state.Stars).ToUpperInvariant() + " " +
                          state.SelectedCreature.DisplayName.ToUpperInvariant()
                        : "ENCOUNTER: SELECT A CREATURE";
                    break;
            }
        }

        private static void SubmitChallenge()
        {
            ChallengeMenuState state = _challengeMenu;
            if (state == null)
            {
                return;
            }
            if (state.Mode == ChallengeMode.CustomEncounter && state.SelectedCreature == null)
            {
                ShowMessage("Choose a creature for the selected encounter.");
                return;
            }
            if (ChallengeRequested == null)
            {
                ShowMessage("Arena networking is not ready yet.");
                return;
            }

            var request = new ChallengeRequest
            {
                RequestId = Guid.NewGuid().ToString("N").ToLowerInvariant(),
                ArenaId = state.ArenaId,
                // The server replaces both identity fields from the RPC sender.
                PlayerId = 0,
                PlayerName = string.Empty,
                Mode = state.Mode,
                CapMode = state.CapMode,
                SelectedBiome = state.SelectedBiome,
                RequestedUtc = DateTime.UtcNow
            };
            if (state.Mode == ChallengeMode.CustomEncounter)
            {
                request.CustomSelection = new CustomEncounterSelection
                {
                    CreatureKey = state.SelectedCreature.CreatureKey,
                    Stars = state.Stars,
                    Quantity = state.Quantity
                };
            }
            ChallengeRequested(request);
            CloseModal();
        }

        private static void RequestChallengeLeaderboard()
        {
            ChallengeMenuState state = _challengeMenu;
            if (state == null)
            {
                return;
            }
            if (LeaderboardRequested == null)
            {
                ShowMessage("Arena leaderboard networking is not ready yet.");
                return;
            }

            string arenaId = state.ArenaId;
            _combinedLeaderboardActive = true;
            _leaderboardCapMode = state.CapMode;
            _leaderboardSelectedBiome = state.SelectedBiome;
            _biomeLeaderboardEntries = null;
            _starLeaderboardEntries = null;
            RenderCombinedLeaderboard();
            LeaderboardRequested(arenaId, new LeaderboardKey
            {
                Mode = ChallengeMode.BiomeLadder,
                CapMode = _leaderboardCapMode,
                SelectedBiome = _leaderboardSelectedBiome
            });
            LeaderboardRequested(arenaId, new LeaderboardKey
            {
                Mode = ChallengeMode.StarLadder,
                CapMode = _leaderboardCapMode,
                SelectedBiome = _leaderboardSelectedBiome
            });
        }

        private static void OpenBiomePicker()
        {
            ChallengeMenuState state = _challengeMenu;
            if (state == null || state.Mode == ChallengeMode.CustomEncounter ||
                state.CapMode != ProgressionCapMode.Biome)
            {
                return;
            }

            List<BiomeTier> choices = SelectableLadderBiomes();
            if (choices.Count == 0)
            {
                ShowMessage("No arena biomes are currently available.");
                return;
            }
            BiomeTier pending = choices.Contains(state.SelectedBiome) ? state.SelectedBiome : choices[0];
            BuildBiomePicker(pending);
        }

        private static void BuildBiomePicker(BiomeTier pendingSelection)
        {
            ChallengeMenuState state = _challengeMenu;
            List<BiomeTier> choices = SelectableLadderBiomes();
            if (state == null || choices.Count == 0 || _modalRoot == null)
            {
                return;
            }

            bool preservePositions = _biomePickerRoot != null;
            Vector2 mainPosition = GetPanelPosition(_modalRoot);
            Vector2 pickerPosition = preservePositions
                ? GetPanelPosition(_biomePickerRoot)
                : new Vector2(335f, 0f);
            Destroy(ref _biomePickerRoot);
            SetPanelPosition(_modalRoot, preservePositions ? mainPosition : new Vector2(-315f, 0f));
            _biomePickerRoot = CreatePanel("SELECT BIOME", 390f, 570f, true);
            SetPanelPosition(_biomePickerRoot, pickerPosition);

            AddLabel(_biomePickerRoot.transform,
                "Choose one biome for this ladder.",
                18, new Vector2(0f, 205f), 330f, 36f).color = SectionColor;

            for (int index = 0; index < choices.Count; index++)
            {
                BiomeTier candidate = choices[index];
                Button biomeButton = AddButton(_biomePickerRoot.transform, BiomeLabel(candidate),
                    new Vector2(0f, 150f - index * 55f), 300f, 42f,
                    () => BuildBiomePicker(candidate));
                SetButtonSelected(biomeButton, candidate == pendingSelection);
            }

            AddLabel(_biomePickerRoot.transform,
                "Selected: " + BiomeLabel(pendingSelection),
                18, new Vector2(0f, -188f), 330f, 34f).color = SectionColor;
            AddButton(_biomePickerRoot.transform, "CANCEL", new Vector2(-90f, -240f), 150f, 42f,
                CloseBiomePicker);
            AddButton(_biomePickerRoot.transform, "SELECT", new Vector2(90f, -240f), 150f, 42f, () =>
            {
                if (_challengeMenu == null)
                {
                    return;
                }
                _challengeMenu.SelectedBiome = pendingSelection;
                CloseBiomePicker();
                RefreshChallengeMenu();
            });
            _biomePickerRoot.transform.SetAsLastSibling();
        }

        private static List<BiomeTier> SelectableLadderBiomes()
        {
            return _ladderBiomes
                .Where(biome => biome >= BiomeTier.BlackForest && biome <= BiomeTier.Ashlands)
                .Distinct()
                .OrderBy(biome => biome)
                .ToList();
        }

        private static void CloseBiomePicker()
        {
            Destroy(ref _biomePickerRoot);
            if (_modalRoot != null)
            {
                SetPanelPosition(_modalRoot, Vector2.zero);
                _modalRoot.transform.SetAsLastSibling();
            }
            RefreshInputBlock();
        }

        private static void OpenCreaturePicker()
        {
            ChallengeMenuState state = _challengeMenu;
            if (state == null || state.Mode != ChallengeMode.CustomEncounter || _creatures.Count == 0)
            {
                return;
            }
            BiomeTier biome = state.SelectedCreature?.Biome ?? _creatures[0].Biome;
            BuildCreaturePicker(biome, state.SelectedCreature);
        }

        private static void BuildCreaturePicker(BiomeTier biome, CreatureDefinition pendingSelection)
        {
            bool preservePositions = _creaturePickerRoot != null;
            Vector2 mainPosition = GetPanelPosition(_modalRoot);
            Vector2 pickerPosition = preservePositions
                ? GetPanelPosition(_creaturePickerRoot)
                : new Vector2(325f, 0f);
            Destroy(ref _creaturePickerRoot);
            if (_modalRoot == null)
            {
                return;
            }

            SetPanelPosition(_modalRoot, preservePositions ? mainPosition : new Vector2(-340f, 0f));
            _creaturePickerRoot = CreatePanel("SELECT CREATURE", 680f, 630f, true);
            SetPanelPosition(_creaturePickerRoot, pickerPosition);

            List<BiomeTier> biomes = _creatures.Select(creature => creature.Biome).Distinct().OrderBy(value => value).ToList();
            float tabWidth = Math.Min(100f, 620f / Math.Max(1, biomes.Count));
            float firstTabX = -(biomes.Count - 1) * tabWidth / 2f;
            for (int index = 0; index < biomes.Count; index++)
            {
                BiomeTier tabBiome = biomes[index];
                Button tab = AddButton(_creaturePickerRoot.transform, BiomeLabel(tabBiome),
                    new Vector2(firstTabX + index * tabWidth, 220f), tabWidth - 4f, 36f,
                    () => BuildCreaturePicker(tabBiome, pendingSelection));
                SetButtonSelected(tab, tabBiome == biome);
                Text tabText = tab.GetComponentInChildren<Text>(true);
                if (tabText != null) tabText.fontSize = 14;
            }

            List<CreatureDefinition> visibleCreatures = _creatures.Where(creature => creature.Biome == biome).ToList();
            for (int index = 0; index < visibleCreatures.Count && index < 12; index++)
            {
                CreatureDefinition candidate = visibleCreatures[index];
                int column = index % 4;
                int row = index / 4;
                Button creatureButton = AddCreatureCard(_creaturePickerRoot.transform, candidate,
                    new Vector2(-246f + column * 164f, 133f - row * 125f), 148f, 112f,
                    () => BuildCreaturePicker(biome, candidate));
                SetButtonSelected(creatureButton,
                    pendingSelection != null && string.Equals(
                        pendingSelection.CreatureKey,
                        candidate.CreatureKey,
                        StringComparison.OrdinalIgnoreCase));
            }

            AddLabel(_creaturePickerRoot.transform,
                "Selected: " + (pendingSelection?.DisplayName ?? "None"),
                19, new Vector2(-80f, -226f), 430f, 38f).color = SectionColor;
            Button selectButton = AddButton(_creaturePickerRoot.transform, "SELECT",
                new Vector2(175f, -267f), 210f, 44f, () =>
                {
                    if (pendingSelection == null || _challengeMenu == null)
                    {
                        return;
                    }
                    _challengeMenu.SelectedCreature = pendingSelection;
                    if (_challengeMenu.SelectedCreature.SupportedStars == null ||
                        !_challengeMenu.SelectedCreature.SupportedStars.Contains(_challengeMenu.Stars))
                    {
                        _challengeMenu.Stars = StarLevel.Base;
                    }
                    CloseCreaturePicker();
                    RefreshChallengeMenu();
                });
            selectButton.interactable = pendingSelection != null;
            AddButton(_creaturePickerRoot.transform, "CANCEL", new Vector2(-65f, -267f), 210f, 44f,
                CloseCreaturePicker);
            _creaturePickerRoot.transform.SetAsLastSibling();
        }

        private static void CloseCreaturePicker()
        {
            Destroy(ref _creaturePickerRoot);
            if (_modalRoot != null)
            {
                SetPanelPosition(_modalRoot, Vector2.zero);
                _modalRoot.transform.SetAsLastSibling();
            }
            RefreshInputBlock();
        }

        public static void OpenAdminPanel(string arenaId)
        {
            OpenAdminPanel(arenaId, null);
        }

        public static void OpenAdminPanel(string arenaId, ArenaDefinition definition)
        {
            // The Core interaction has already authenticated the local admin and
            // every mutation is authenticated again by the server.  A redundant
            // asynchronous client-side check here only creates a silent no-op
            // path between the hover prompt and the panel request.
            if (string.IsNullOrWhiteSpace(arenaId))
            {
                Plugin.Log?.LogWarning("Ignored an Arena Core panel request without an arena ID.");
                ShowMessage("This Arena Core is still initializing. Try again in a moment.");
                return;
            }
            if (!CanDraw())
            {
                Plugin.Log?.LogWarning("Could not open Arena Core administration because the client GUI is not ready.");
                ShowMessage("The Arena administration screen is not ready. Try again in a moment.");
                return;
            }

            CloseModal();
            ArenaWorldObjects.SelectAdminArena(arenaId);
            _activeArenaId = arenaId;
            _modalRoot = CreatePanel("Arena Administration", 820f, 680f);

            AddLabel(_modalRoot.transform, "Name", 20, new Vector2(-285f, 230f), 100f, 35f);
            var name = AddInput(_modalRoot.transform, definition?.DisplayName ?? "Arena", new Vector2(75f, 230f), 560f, 42f, InputField.ContentType.Standard);

            AddLabel(_modalRoot.transform, "Combat radius (blue)", 20, new Vector2(-250f, 170f), 210f, 35f);
            var combat = AddInput(_modalRoot.transform,
                (definition?.CombatRadius ?? 20f).ToString("0.##", CultureInfo.InvariantCulture),
                new Vector2(-80f, 170f), 120f, 42f, InputField.ContentType.DecimalNumber);

            AddLabel(_modalRoot.transform, "Protected radius (gold)", 20, new Vector2(100f, 170f), 220f, 35f);
            var protectedRadius = AddInput(_modalRoot.transform,
                (definition?.ProtectedRadius ?? 30f).ToString("0.##", CultureInfo.InvariantCulture),
                new Vector2(275f, 170f), 120f, 42f, InputField.ContentType.DecimalNumber);

            AddLabel(_modalRoot.transform,
                "Close this panel, then place all position markers with the Arena Admin Hammer:\n" +
                "Required: Arena Master = staging  •  1 Combat Start inside blue ring  •  4 Enemy Spawns",
                18, new Vector2(0f, 90f), 750f, 75f);

            AddLabel(_modalRoot.transform,
                "New markers, signs, and gates will target this selected arena.",
                17, new Vector2(0f, 28f), 720f, 35f);

            AddButton(_modalRoot.transform, "Save", new Vector2(-220f, -42f), 190f, 50f, () =>
            {
                float combatValue;
                float protectedValue;
                if (string.IsNullOrWhiteSpace(name.text) ||
                    !float.TryParse(combat.text, NumberStyles.Float, CultureInfo.InvariantCulture, out combatValue) ||
                    !float.TryParse(protectedRadius.text, NumberStyles.Float, CultureInfo.InvariantCulture, out protectedValue) ||
                    combatValue <= 0f || protectedValue <= combatValue)
                {
                    ShowMessage("Enter a name and radii where protected is larger than combat.");
                    return;
                }

                AdminMutationRequested?.Invoke(new ArenaUiAdminMutation
                {
                    ArenaId = arenaId,
                    Kind = ArenaUiAdminMutationKind.SaveDefinition,
                    DisplayName = name.text.Trim(),
                    CombatRadius = combatValue,
                    ProtectedRadius = protectedValue
                });
            });

            AddButton(_modalRoot.transform, "Enable", new Vector2(0f, -42f), 190f, 50f,
                () => AdminMutationRequested?.Invoke(new ArenaUiAdminMutation { ArenaId = arenaId, Kind = ArenaUiAdminMutationKind.EnableArena }));
            AddButton(_modalRoot.transform, "Disable", new Vector2(220f, -42f), 190f, 50f,
                () => AdminMutationRequested?.Invoke(new ArenaUiAdminMutation { ArenaId = arenaId, Kind = ArenaUiAdminMutationKind.DisableArena }));

            AddLabel(_modalRoot.transform, "ADMIN PERMISSIONS", 18, new Vector2(0f, -100f), 320f, 30f);
            _setupVisibilityButton = AddButton(_modalRoot.transform, SetupVisibilityButtonText(),
                new Vector2(-205f, -145f), 350f, 42f, ArenaWorldObjects.ToggleAdminSetupVisuals);
            _terrainPermissionButton = AddButton(_modalRoot.transform,
                PermissionButtonText(ArenaAdminPermissionKind.Terrain),
                new Vector2(205f, -145f), 350f, 42f,
                () => ToggleAdminPermission(arenaId, ArenaAdminPermissionKind.Terrain));
            _buildingPermissionButton = AddButton(_modalRoot.transform,
                PermissionButtonText(ArenaAdminPermissionKind.Building),
                new Vector2(-205f, -200f), 350f, 42f,
                () => ToggleAdminPermission(arenaId, ArenaAdminPermissionKind.Building));
            _pickupPermissionButton = AddButton(_modalRoot.transform,
                PermissionButtonText(ArenaAdminPermissionKind.DroppedItemPickup),
                new Vector2(205f, -200f), 350f, 42f,
                () => ToggleAdminPermission(arenaId, ArenaAdminPermissionKind.DroppedItemPickup));
            AddLabel(_modalRoot.transform,
                "Terrain also requires devcommands. Permissions never apply to the active combatant.",
                16, new Vector2(0f, -245f), 720f, 28f);
            AddButton(_modalRoot.transform, "Close", new Vector2(0f, -292f), 220f, 45f, CloseModal);
            _modalRoot.transform.SetAsLastSibling();
            Plugin.Log?.LogInfo("Opened Arena Core administration for '" + arenaId + "'.");
        }

        public static bool ShowQueueCall(string arenaId, string sessionId, int seconds = 30)
        {
            if (string.IsNullOrWhiteSpace(arenaId) || string.IsNullOrWhiteSpace(sessionId) || !CanDraw())
            {
                return false;
            }
            Destroy(ref _queueRoot);
            _activeArenaId = arenaId ?? string.Empty;
            _queueArenaId = arenaId;
            _queueDeadlineUtc = DateTime.UtcNow.AddSeconds(Math.Max(1, seconds));
            _queueRoot = CreatePanel("Your arena is ready", 520f, 260f, true);
            _queueText = AddLabel(_queueRoot.transform, string.Empty, 23, new Vector2(0f, 35f), 460f, 80f);
            AddButton(_queueRoot.transform, "Accept", new Vector2(-115f, -65f), 180f, 48f, () =>
            {
                QueueAccepted?.Invoke();
                CloseQueuePrompt();
            });
            AddButton(_queueRoot.transform, "Not now", new Vector2(115f, -65f), 180f, 48f, CloseQueuePrompt);
            UpdateQueuePrompt();
            return _queueRoot != null;
        }

        public static bool OpenFoodPreparation(
            string arenaId,
            IEnumerable<ArenaFoodDefinition> foods,
            IEnumerable<string> preferredPrefabNames,
            int seconds)
        {
            if (string.IsNullOrWhiteSpace(arenaId) || !CanDraw())
            {
                return false;
            }

            List<ArenaFoodDefinition> available = (foods ?? Enumerable.Empty<ArenaFoodDefinition>())
                .Where(food => food != null && !string.IsNullOrWhiteSpace(food.PrefabName))
                .GroupBy(food => food.PrefabName, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(food => food.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var availableNames = new HashSet<string>(available.Select(food => food.PrefabName), StringComparer.Ordinal);
            var selected = new List<string>();
            foreach (string value in preferredPrefabNames ?? Enumerable.Empty<string>())
            {
                string prefabName = value?.Trim();
                if (prefabName != null && availableNames.Contains(prefabName) && !selected.Contains(prefabName))
                {
                    selected.Add(prefabName);
                    if (selected.Count == 3) break;
                }
            }

            CloseModal();
            _activeArenaId = arenaId;
            _foodPreparation = new FoodPreparationState
            {
                ArenaId = arenaId,
                Foods = available,
                SelectedPrefabNames = selected,
                DeadlineUtc = DateTime.UtcNow.AddSeconds(Math.Max(1, seconds)),
                ScrollPositions = new[] { 1f, 1f, 1f }
            };
            BuildFoodPreparation();
            return _modalRoot != null;
        }

        public static void CloseFoodPreparation(string arenaId)
        {
            if (_foodPreparation != null &&
                string.Equals(_foodPreparation.ArenaId, arenaId, StringComparison.Ordinal))
            {
                CloseModal();
            }
        }

        private static void BuildFoodPreparation()
        {
            FoodPreparationState state = _foodPreparation;
            if (state == null || !CanDraw())
            {
                return;
            }

            bool preservePosition = _modalRoot != null;
            Vector2 panelPosition = GetPanelPosition(_modalRoot);
            CaptureFoodScrollPositions(state);
            Destroy(ref _modalRoot);
            _modalRoot = CreatePanel("CHOOSE ARENA FOOD", 980f, 720f, true);
            if (preservePosition)
            {
                SetPanelPosition(_modalRoot, panelPosition);
            }
            int remaining = Math.Max(0, (int)Math.Ceiling((state.DeadlineUtc - DateTime.UtcNow).TotalSeconds));
            state.DeadlineLabel = AddLabel(_modalRoot.transform,
                "Choose exactly three foods your character has discovered.  Ready in " + remaining + "s",
                18, new Vector2(0f, 265f), 900f, 32f);
            state.DeadlineLabel.color = SectionColor;
            AddLabel(_modalRoot.transform,
                state.Foods.Count < 3
                    ? "Discover at least three foods before entering the arena."
                    : "Arena food is temporary. Pick any three total across Health, Stamina, and Eitr.",
                16, new Vector2(0f, 226f), 900f, 42f, true);

            string selection = state.SelectedPrefabNames.Count == 0
                ? "Selected 0/3: none"
                : "Selected " + state.SelectedPrefabNames.Count + "/3: " +
                  string.Join("  •  ", state.SelectedPrefabNames.Select((name, index) =>
                      (index + 1) + ". " + state.Foods.First(food => food.PrefabName == name).DisplayName));
            AddLabel(_modalRoot.transform, selection, 16, new Vector2(0f, 185f), 900f, 36f).color = SectionColor;

            ArenaFoodTab[] tabs = { ArenaFoodTab.Health, ArenaFoodTab.Stamina, ArenaFoodTab.Eitr };
            state.ScrollRects = new ScrollRect[tabs.Length];
            for (int index = 0; index < tabs.Length; index++)
            {
                ArenaFoodTab tab = tabs[index];
                List<ArenaFoodDefinition> categoryFoods = state.Foods
                    .Where(food => FoodTab(food) == tab)
                    .OrderByDescending(food => ArenaFoodIndexPolicy.Strength(
                        tab, food.Health, food.Stamina, food.Eitr))
                    .ThenByDescending(food => food.Health + food.Stamina + food.Eitr)
                    .ThenBy(food => food.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                AddLabel(_modalRoot.transform,
                    FoodTabLabel(tab).ToUpperInvariant() + " (" + categoryFoods.Count + ")",
                    17, new Vector2(-310f + index * 310f, 145f), 292f, 32f, true).color = SectionColor;
                state.ScrollRects[index] = CreateFoodList(
                    _modalRoot.transform,
                    state,
                    tab,
                    categoryFoods,
                    new Vector2(-310f + index * 310f, -45f),
                    292f,
                    350f,
                    state.ScrollPositions[index]);
            }

            Button ready = AddButton(_modalRoot.transform, "READY", new Vector2(-115f, -307f), 200f, 46f,
                ConfirmFoodPreparation);
            ready.interactable = state.SelectedPrefabNames.Count == 3 && state.Foods.Count >= 3;
            AddButton(_modalRoot.transform, "CANCEL", new Vector2(115f, -307f), 200f, 46f,
                CancelFoodPreparation);
            _modalRoot.transform.SetAsLastSibling();
        }

        private static ScrollRect CreateFoodList(
            Transform parent,
            FoodPreparationState state,
            ArenaFoodTab tab,
            IList<ArenaFoodDefinition> foods,
            Vector2 position,
            float width,
            float height,
            float normalizedPosition)
        {
            var host = new GameObject(FoodTabLabel(tab) + "FoodList", typeof(RectTransform), typeof(Image), typeof(ScrollRect));
            host.transform.SetParent(parent, false);
            RectTransform hostRect = host.GetComponent<RectTransform>();
            hostRect.anchorMin = new Vector2(0.5f, 0.5f);
            hostRect.anchorMax = new Vector2(0.5f, 0.5f);
            hostRect.pivot = new Vector2(0.5f, 0.5f);
            hostRect.anchoredPosition = position;
            hostRect.sizeDelta = new Vector2(width, height);
            Image background = host.GetComponent<Image>();
            background.color = new Color(0.035f, 0.03f, 0.025f, 0.72f);

            var viewportObject = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
            viewportObject.transform.SetParent(host.transform, false);
            RectTransform viewport = viewportObject.GetComponent<RectTransform>();
            viewport.anchorMin = Vector2.zero;
            viewport.anchorMax = Vector2.one;
            viewport.offsetMin = new Vector2(5f, 5f);
            viewport.offsetMax = new Vector2(-17f, -5f);

            var contentObject = new GameObject("Content", typeof(RectTransform));
            contentObject.transform.SetParent(viewport, false);
            RectTransform content = contentObject.GetComponent<RectTransform>();
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            const float rowHeight = 58f;
            float contentHeight = Math.Max(height - 10f, foods.Count * rowHeight);
            content.sizeDelta = new Vector2(0f, contentHeight);

            if (foods.Count == 0)
            {
                Text empty = AddLabel(content, "No discovered " + FoodTabLabel(tab).ToLowerInvariant() + " foods.",
                    15, new Vector2(0f, -(height - 10f) / 2f), width - 35f, 50f);
                empty.color = DisabledTextColor;
            }
            for (int index = 0; index < foods.Count; index++)
            {
                ArenaFoodDefinition food = foods[index];
                int selectedIndex = state.SelectedPrefabNames.IndexOf(food.PrefabName);
                string selectedPrefix = selectedIndex >= 0
                    ? "<color=#FFD27A>[" + (selectedIndex + 1) + "] </color>"
                    : string.Empty;
                string labelText = selectedPrefix + food.DisplayName +
                                   "\n<size=12><color=#FF9C7A>HP " + FormatStat(food.Health) +
                                   "</color>   <color=#FFE071>STAM " + FormatStat(food.Stamina) +
                                   "</color>   <color=#D7A5FF>EITR " + FormatStat(food.Eitr) + "</color></size>";
                Button row = AddCardButton(content, labelText,
                    new Vector2(0f, -(index * rowHeight + rowHeight / 2f)), width - 28f, rowHeight - 4f,
                    () => ToggleFoodSelection(food.PrefabName));
                RectTransform rowRect = row.GetComponent<RectTransform>();
                rowRect.anchorMin = new Vector2(0.5f, 1f);
                rowRect.anchorMax = new Vector2(0.5f, 1f);
                rowRect.pivot = new Vector2(0.5f, 0.5f);
                rowRect.anchoredPosition = new Vector2(0f, -(index * rowHeight + rowHeight / 2f));
                Image icon = AddImage(row.transform, "FoodIcon", new Vector2(-(width - 28f) / 2f + 25f, 0f), 38f, 38f);
                icon.sprite = ResolveFoodIcon(food.PrefabName);
                icon.gameObject.SetActive(icon.sprite != null);
                SetButtonSelected(row, selectedIndex >= 0);
                Text rowLabel = row.GetComponentInChildren<Text>(true);
                if (rowLabel != null)
                {
                    rowLabel.fontSize = 14;
                    rowLabel.alignment = TextAnchor.MiddleLeft;
                    RectTransform labelRect = rowLabel.rectTransform;
                    labelRect.anchorMin = Vector2.zero;
                    labelRect.anchorMax = Vector2.one;
                    labelRect.offsetMin = new Vector2(49f, 2f);
                    labelRect.offsetMax = new Vector2(-5f, -2f);
                }
            }

            var scrollbarObject = new GameObject("Scrollbar", typeof(RectTransform), typeof(Image), typeof(Scrollbar));
            scrollbarObject.transform.SetParent(host.transform, false);
            RectTransform scrollbarRect = scrollbarObject.GetComponent<RectTransform>();
            scrollbarRect.anchorMin = new Vector2(1f, 0f);
            scrollbarRect.anchorMax = new Vector2(1f, 1f);
            scrollbarRect.pivot = new Vector2(1f, 0.5f);
            scrollbarRect.offsetMin = new Vector2(-13f, 5f);
            scrollbarRect.offsetMax = new Vector2(-4f, -5f);
            scrollbarObject.GetComponent<Image>().color = new Color(0.12f, 0.09f, 0.06f, 0.9f);

            var handleObject = new GameObject("Handle", typeof(RectTransform), typeof(Image));
            handleObject.transform.SetParent(scrollbarObject.transform, false);
            RectTransform handleRect = handleObject.GetComponent<RectTransform>();
            handleRect.anchorMin = Vector2.zero;
            handleRect.anchorMax = Vector2.one;
            handleRect.offsetMin = Vector2.zero;
            handleRect.offsetMax = Vector2.zero;
            Image handle = handleObject.GetComponent<Image>();
            handle.color = SectionColor;
            Scrollbar scrollbar = scrollbarObject.GetComponent<Scrollbar>();
            scrollbar.handleRect = handleRect;
            scrollbar.targetGraphic = handle;
            scrollbar.direction = Scrollbar.Direction.BottomToTop;

            ScrollRect scroll = host.GetComponent<ScrollRect>();
            scroll.content = content;
            scroll.viewport = viewport;
            scroll.horizontal = false;
            scroll.vertical = foods.Count * rowHeight > height - 10f;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.inertia = true;
            scroll.decelerationRate = 0.12f;
            scroll.scrollSensitivity = 300f;
            scroll.verticalScrollbar = scrollbar;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
            scroll.verticalNormalizedPosition = Mathf.Clamp01(normalizedPosition);
            scrollbarObject.SetActive(scroll.vertical);
            return scroll;
        }

        private static void CaptureFoodScrollPositions(FoodPreparationState state)
        {
            if (state?.ScrollRects == null || state.ScrollPositions == null)
            {
                return;
            }
            for (int index = 0; index < state.ScrollRects.Length && index < state.ScrollPositions.Length; index++)
            {
                ScrollRect scroll = state.ScrollRects[index];
                if (scroll != null)
                {
                    state.ScrollPositions[index] = scroll.verticalNormalizedPosition;
                }
            }
        }

        private static ArenaFoodTab FoodTab(ArenaFoodDefinition food)
        {
            return ArenaFoodIndexPolicy.Classify(food?.Health ?? 0f, food?.Stamina ?? 0f, food?.Eitr ?? 0f);
        }

        private static string FoodTabLabel(ArenaFoodTab tab)
        {
            switch (tab)
            {
                case ArenaFoodTab.Stamina: return "Stamina";
                case ArenaFoodTab.Eitr: return "Eitr";
                default: return "Health";
            }
        }

        private static void ToggleFoodSelection(string prefabName)
        {
            FoodPreparationState state = _foodPreparation;
            if (state == null || string.IsNullOrWhiteSpace(prefabName)) return;
            if (state.SelectedPrefabNames.Remove(prefabName))
            {
                BuildFoodPreparation();
                return;
            }
            if (state.SelectedPrefabNames.Count >= 3)
            {
                ShowMessage("Remove one selected food before choosing another.");
                return;
            }
            state.SelectedPrefabNames.Add(prefabName);
            BuildFoodPreparation();
        }

        private static void ConfirmFoodPreparation()
        {
            FoodPreparationState state = _foodPreparation;
            if (state == null || state.SelectedPrefabNames.Count != 3)
            {
                ShowMessage("Choose exactly three discovered foods.");
                return;
            }
            if (FoodPreparationConfirmed?.Invoke(new List<string>(state.SelectedPrefabNames)) == true)
            {
                CloseModal();
            }
        }

        private static void CancelFoodPreparation()
        {
            FoodPreparationCancelled?.Invoke();
            CloseModal();
        }

        private static Sprite ResolveFoodIcon(string prefabName)
        {
            GameObject prefab = ObjectDB.instance?.GetItemPrefab(prefabName);
            return prefab?.GetComponent<ItemDrop>()?.m_itemData?.GetIcon();
        }

        private static string FormatStat(float value)
        {
            return Math.Max(0, Mathf.RoundToInt(value)).ToString(CultureInfo.InvariantCulture);
        }

        public static void RenderSnapshot(ArenaClientSnapshot snapshot)
        {
            if (snapshot == null)
            {
                DestroyHud();
                return;
            }

            // Closed snapshots are also sent as arena-definition metadata. They
            // may tear down only the HUD for their own arena; otherwise a closed
            // second arena could erase another arena's active HUD.
            if (!IsHudPhase(snapshot.Phase) || !ShouldDisplayHud(snapshot))
            {
                if (string.Equals(_hudArenaId, snapshot.ArenaId, StringComparison.Ordinal))
                {
                    DestroyHud();
                }
                return;
            }

            if (!CanDraw())
            {
                DestroyHud();
                return;
            }

            if (_hudRoot == null)
            {
                _hudRoot = GUIManager.Instance.CreateWoodpanel(GUIManager.CustomGUIFront.transform,
                    new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -105f), 610f, 155f);
                _hudRoot.name = "ArenaGuard_Hud";
                _hudText = AddLabel(_hudRoot.transform, string.Empty, 20, new Vector2(0f, 18f), 570f, 90f);
                _forfeitButton = AddButton(_hudRoot.transform, "Forfeit", new Vector2(0f, -52f), 155f, 36f,
                    () => ForfeitRequested?.Invoke());
            }

            _activeArenaId = snapshot.ArenaId ?? string.Empty;
            _hudArenaId = _activeArenaId;
            Player localPlayer = Player.m_localPlayer;
            bool localIsCombatant = localPlayer != null &&
                                    snapshot.CombatantPlayerId == localPlayer.GetPlayerID() &&
                                    IsForfeitPhase(snapshot.Phase);
            if (_forfeitButton != null)
            {
                _forfeitButton.gameObject.SetActive(localIsCombatant);
            }
            _hudSnapshot = snapshot;
            _hudElapsedBaseMilliseconds = snapshot.ElapsedMilliseconds;
            _hudElapsedBaseTime = Time.unscaledTime;
            _nextHudTimerRefreshTime = _hudElapsedBaseTime + 0.1f;
            RefreshHudText(snapshot.ElapsedMilliseconds);
        }

        private static void RefreshHudText(long elapsedMilliseconds)
        {
            ArenaClientSnapshot snapshot = _hudSnapshot;
            if (snapshot == null || _hudText == null)
            {
                return;
            }

            var line = new StringBuilder();
            line.Append(string.IsNullOrWhiteSpace(snapshot.ArenaName) ? "Arena" : snapshot.ArenaName);
            line.Append(" — ").Append(snapshot.Phase);
            if (!string.IsNullOrWhiteSpace(snapshot.CombatantName))
            {
                line.Append("\nCombatant: ").Append(snapshot.CombatantName);
            }
            if (snapshot.EncounterCount > 0)
            {
                line.Append("   Opponent ").Append(Math.Min(snapshot.EncounterIndex + 1, snapshot.EncounterCount))
                    .Append('/').Append(snapshot.EncounterCount);
            }
            if (snapshot.CurrentEncounter != null)
            {
                line.Append(" — ").Append(snapshot.CurrentEncounter.CreatureKey)
                    .Append(" (").Append(StarLabel(snapshot.CurrentEncounter.Stars)).Append(")")
                    .Append(" x").Append(snapshot.CurrentEncounter.Quantity);
            }
            if (snapshot.CountdownSeconds > 0)
            {
                string countdownLabel = snapshot.Phase == SessionPhase.Staging
                    ? "Securing loadout — start timeout "
                    : snapshot.Phase == SessionPhase.Victory
                        ? "Returning in "
                        : "Next fight in ";
                line.Append("\n").Append(countdownLabel)
                    .Append(snapshot.CountdownSeconds).Append('s');
            }
            line.Append("   Fight time ").Append(FormatTime(elapsedMilliseconds));
            if (snapshot.QueuePosition > 0)
            {
                line.Append("\nQueue position ").Append(snapshot.QueuePosition).Append('/').Append(snapshot.QueueLength);
            }
            _hudText.text = line.ToString();
        }

        public static void ShowLeaderboard(string title, IEnumerable<LeaderboardEntry> entries)
        {
            if (!CanDraw())
            {
                return;
            }

            CloseModal();
            _modalRoot = CreatePanel(string.IsNullOrWhiteSpace(title) ? "Server Leaderboard" : title, 760f, 680f);
            var builder = new StringBuilder();
            var rank = 0;
            foreach (var entry in (entries ?? Enumerable.Empty<LeaderboardEntry>()).Take(18))
            {
                rank++;
                builder.Append(rank.ToString(CultureInfo.InvariantCulture).PadLeft(2)).Append(". ")
                    .Append(string.IsNullOrWhiteSpace(entry.PlayerName) ? "Unknown player" : entry.PlayerName);
                if (entry.Completed)
                {
                    builder.Append(" — ").Append(FormatTime(entry.ElapsedMilliseconds));
                }
                else
                {
                    builder.Append(" — reached stage ").Append(entry.FurthestEncounterIndex + 1);
                }
                builder.Append(" — ").Append(FormatLeaderboardDate(entry.RecordedUtc)).Append('\n');
            }
            if (rank == 0)
            {
                builder.Append("No results have been recorded for this challenge yet.");
            }
            var text = AddLabel(_modalRoot.transform, builder.ToString(), 19, new Vector2(0f, 5f), 680f, 540f);
            text.alignment = TextAnchor.UpperLeft;
            AddButton(_modalRoot.transform, "Close", new Vector2(0f, -285f), 180f, 45f, CloseModal);
        }

        public static void ReceiveLeaderboard(LeaderboardKey key, IEnumerable<LeaderboardEntry> entries)
        {
            if (!_combinedLeaderboardActive || key == null || key.CapMode != _leaderboardCapMode ||
                key.CapMode == ProgressionCapMode.Biome && key.SelectedBiome != _leaderboardSelectedBiome)
            {
                return;
            }

            List<LeaderboardEntry> received = (entries ?? Enumerable.Empty<LeaderboardEntry>()).Take(18).ToList();
            if (key.Mode == ChallengeMode.BiomeLadder)
            {
                _biomeLeaderboardEntries = received;
            }
            else if (key.Mode == ChallengeMode.StarLadder)
            {
                _starLeaderboardEntries = received;
            }
            else
            {
                return;
            }
            RenderCombinedLeaderboard();
        }

        private static void RenderCombinedLeaderboard()
        {
            if (!_combinedLeaderboardActive || !CanDraw())
            {
                return;
            }

            bool refreshing = _modalRoot != null &&
                              string.Equals(_modalRoot.name, "ArenaGuard_ARENALEADERBOARD", StringComparison.Ordinal);
            Vector2 position = refreshing ? GetPanelPosition(_modalRoot) : Vector2.zero;
            Destroy(ref _creaturePickerRoot);
            Destroy(ref _modalRoot);
            _challengeMenu = null;
            _modalRoot = CreatePanel("ARENA LEADERBOARD", 760f, 680f, true);
            SetPanelPosition(_modalRoot, position);

            string cap = _leaderboardCapMode == ProgressionCapMode.Gauntlet
                ? "GAUNTLET • EVERY BIOME"
                : "BIOME • " + BiomeLabel(_leaderboardSelectedBiome).ToUpperInvariant();
            AddSectionLabel(_modalRoot.transform, "BIOME LADDER • " + cap,
                new Vector2(0f, 255f), 680f);
            Text biome = AddLabel(_modalRoot.transform,
                FormatLeaderboardSection(_biomeLeaderboardEntries),
                17, new Vector2(0f, 133f), 660f, 205f);
            biome.alignment = TextAnchor.UpperLeft;

            AddSectionLabel(_modalRoot.transform, "STAR LADDER • " + cap,
                new Vector2(0f, 12f), 680f);
            Text stars = AddLabel(_modalRoot.transform,
                FormatLeaderboardSection(_starLeaderboardEntries),
                17, new Vector2(0f, -110f), 660f, 205f);
            stars.alignment = TextAnchor.UpperLeft;
            AddButton(_modalRoot.transform, "Close", new Vector2(0f, -292f), 180f, 42f,
                CloseCombinedLeaderboard);
            _modalRoot.transform.SetAsLastSibling();
        }

        private static string FormatLeaderboardSection(List<LeaderboardEntry> entries)
        {
            if (entries == null)
            {
                return "Loading…";
            }
            if (entries.Count == 0)
            {
                return "No results have been recorded for this ladder yet.";
            }

            var builder = new StringBuilder();
            int rank = 0;
            foreach (LeaderboardEntry entry in entries.Take(8))
            {
                rank++;
                builder.Append(rank.ToString(CultureInfo.InvariantCulture).PadLeft(2)).Append(". ")
                    .Append(string.IsNullOrWhiteSpace(entry.PlayerName) ? "Unknown player" : entry.PlayerName);
                if (entry.Completed)
                {
                    builder.Append(" — ").Append(FormatTime(entry.ElapsedMilliseconds));
                }
                else
                {
                    builder.Append(" — stage ").Append(entry.FurthestEncounterIndex + 1);
                }
                builder.Append(" — ").Append(FormatLeaderboardDate(entry.RecordedUtc)).Append('\n');
            }
            return builder.ToString();
        }

        private static string FormatLeaderboardDate(DateTime recordedUtc)
        {
            if (recordedUtc == default(DateTime))
            {
                return "date unavailable";
            }
            DateTime utc = recordedUtc.Kind == DateTimeKind.Utc
                ? recordedUtc
                : recordedUtc.ToUniversalTime();
            return utc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        }

        private static void CloseCombinedLeaderboard()
        {
            _combinedLeaderboardActive = false;
            _biomeLeaderboardEntries = null;
            _starLeaderboardEntries = null;
            CloseModal();
        }

        public static void CloseArenaUi()
        {
            CloseModal();
            CloseQueuePrompt();
            DestroyHud();
            CreatureIconCache.Clear();
            _combinedLeaderboardActive = false;
            _biomeLeaderboardEntries = null;
            _starLeaderboardEntries = null;
            _activeArenaId = string.Empty;
        }

        public static void OnArenaRemoved(string arenaId)
        {
            if (string.Equals(_activeArenaId, arenaId, StringComparison.Ordinal) ||
                string.Equals(_hudArenaId, arenaId, StringComparison.Ordinal) ||
                string.Equals(_queueArenaId, arenaId, StringComparison.Ordinal))
            {
                CloseArenaUi();
            }
        }

        public static void CloseQueuePromptForArena(string arenaId)
        {
            if (string.Equals(_queueArenaId, arenaId, StringComparison.Ordinal))
            {
                CloseQueuePrompt();
            }
        }

        internal static void DriverUpdate()
        {
            if ((_modalRoot != null || _queueRoot != null) && Input.GetKeyDown(KeyCode.Escape))
            {
                if (_foodPreparation != null)
                {
                    CancelFoodPreparation();
                }
                else if (_creaturePickerRoot != null)
                {
                    CloseCreaturePicker();
                }
                else if (_biomePickerRoot != null)
                {
                    CloseBiomePicker();
                }
                else
                {
                    CloseModal();
                    CloseQueuePrompt();
                }
            }
            UpdateQueuePrompt();
            UpdateFoodPreparationTimer();
            UpdateLiveHudTimer();
            UpdateSetupVisibilityButton();
            UpdateAdminPermissionButtons();
        }

        private static bool CanDraw()
        {
            if (GUIManager.IsHeadless() || GUIManager.CustomGUIFront == null)
            {
                return false;
            }
            EnsureDriver();
            return true;
        }

        private static void EnsureDriver()
        {
            if (GUIManager.CustomGUIFront == null || _driverHost == GUIManager.CustomGUIFront)
            {
                return;
            }
            GUIManager.CustomGUIFront.AddComponent<ArenaUiDriver>();
            _driverHost = GUIManager.CustomGUIFront;
        }

        private static GameObject CreatePanel(string title, float width, float height, bool preserveExistingInputBlock = false)
        {
            var panel = GUIManager.Instance.CreateWoodpanel(GUIManager.CustomGUIFront.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, width, height);
            panel.name = "ArenaGuard_" + title.Replace(" ", string.Empty);
            AddLabel(panel.transform, title, 30, new Vector2(0f, height / 2f - 48f), width - 50f, 45f, true);
            if (!preserveExistingInputBlock || !_inputBlocked)
            {
                SetInputBlocked(true);
            }
            return panel;
        }

        private static Text AddLabel(Transform parent, string value, int size, Vector2 position, float width, float height, bool bold = false)
        {
            var font = bold ? GUIManager.Instance.AveriaSerifBold : GUIManager.Instance.AveriaSerif;
            var go = GUIManager.Instance.CreateText(value, parent, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                position, font, size, Color.white, true, Color.black, width, height, false);
            var text = go.GetComponent<Text>();
            text.alignment = TextAnchor.MiddleCenter;
            return text;
        }

        private static Text AddSectionLabel(Transform parent, string value, Vector2 position, float width)
        {
            Text label = AddLabel(parent, "──  " + value + "  ──", 17, position, width, 30f, true);
            label.color = SectionColor;
            return label;
        }

        private static Dropdown AddDropdown(Transform parent, Vector2 position, float width, params string[] values)
        {
            var go = GUIManager.Instance.CreateDropDown(parent, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                position, 18, width, 42f);
            var dropdown = go.GetComponent<Dropdown>();
            dropdown.ClearOptions();
            dropdown.AddOptions(values.Select(value => new Dropdown.OptionData(value)).ToList());
            return dropdown;
        }

        private static InputField AddInput(Transform parent, string value, Vector2 position, float width, float height,
            InputField.ContentType contentType)
        {
            var go = GUIManager.Instance.CreateInputField(parent, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                position, contentType, string.Empty, 18, width, height);
            var input = go.GetComponent<InputField>();
            input.text = value;
            return input;
        }

        private static Button AddButton(Transform parent, string value, Vector2 position, float width, float height, Action click)
        {
            var go = GUIManager.Instance.CreateButton(value, parent, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                position, width, height);
            var button = go.GetComponent<Button>();
            button.onClick.AddListener(() => click());
            return button;
        }

        private static Button AddModeCard(Transform parent, string value, Vector2 position, Action click)
        {
            return AddCardButton(parent, value, position, 176f, 104f, click);
        }

        private static Button AddCreatureCard(
            Transform parent,
            CreatureDefinition creature,
            Vector2 position,
            float width,
            float height,
            Action click)
        {
            string label = creature.DisplayName +
                           (creature.IsMiniboss ? "\n<size=13><color=#E8A43A>MINIBOSS</color></size>" : string.Empty);
            Button button = AddCardButton(parent, label, position, width, height, click);
            Text text = button.GetComponentInChildren<Text>(true);
            if (text != null)
            {
                RectTransform textRect = text.rectTransform;
                textRect.anchorMin = new Vector2(0f, 0f);
                textRect.anchorMax = new Vector2(1f, 0f);
                textRect.pivot = new Vector2(0.5f, 0f);
                textRect.anchoredPosition = new Vector2(0f, 5f);
                textRect.sizeDelta = new Vector2(-8f, 39f);
                text.fontSize = 15;
                text.resizeTextForBestFit = true;
                text.resizeTextMinSize = 11;
                text.resizeTextMaxSize = 15;
            }

            Sprite primary = ResolveCreatureIcon(creature.IconPrefabName);
            Sprite secondary = ResolveCreatureIcon(creature.SecondaryIconPrefabName);
            float primaryX = secondary == null ? 0f : -25f;
            Image primaryImage = AddImage(button.transform, "CreatureIcon", new Vector2(primaryX, 22f),
                secondary == null ? 62f : 50f, secondary == null ? 62f : 50f);
            primaryImage.sprite = primary;
            primaryImage.gameObject.SetActive(primary != null);
            if (secondary != null)
            {
                Image secondaryImage = AddImage(button.transform, "SecondaryCreatureIcon",
                    new Vector2(25f, 22f), 50f, 50f);
                secondaryImage.sprite = secondary;
            }
            return button;
        }

        private static Image AddImage(
            Transform parent,
            string name,
            Vector2 position,
            float width,
            float height)
        {
            var gameObject = new GameObject(name, typeof(RectTransform));
            gameObject.transform.SetParent(parent, false);
            RectTransform rect = gameObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = new Vector2(width, height);
            Image image = gameObject.AddComponent<Image>();
            image.preserveAspect = true;
            image.raycastTarget = false;
            image.color = Color.white;
            return image;
        }

        private static void SetCreatureIcons(Image primary, Image secondary, CreatureDefinition creature)
        {
            Sprite primarySprite = ResolveCreatureIcon(creature?.IconPrefabName);
            Sprite secondarySprite = ResolveCreatureIcon(creature?.SecondaryIconPrefabName);
            if (primary != null)
            {
                primary.sprite = primarySprite;
                primary.gameObject.SetActive(primarySprite != null);
            }
            if (secondary != null)
            {
                secondary.sprite = secondarySprite;
                secondary.gameObject.SetActive(secondarySprite != null);
            }
        }

        private static Sprite ResolveCreatureIcon(string prefabName)
        {
            if (string.IsNullOrWhiteSpace(prefabName))
            {
                return null;
            }
            if (CreatureIconCache.TryGetValue(prefabName, out Sprite cached) && cached != null)
            {
                return cached;
            }

            GameObject prefab = ObjectDB.instance?.GetItemPrefab(prefabName) ??
                                ZNetScene.instance?.GetPrefab(prefabName);
            Sprite icon = prefab?.GetComponent<ItemDrop>()?.m_itemData?.GetIcon();
            if (icon != null)
            {
                CreatureIconCache[prefabName] = icon;
            }
            return icon;
        }

        private static Button AddCardButton(
            Transform parent,
            string value,
            Vector2 position,
            float width,
            float height,
            Action click)
        {
            Button button = AddButton(parent, value, position, width, height, click);
            Text text = button.GetComponentInChildren<Text>(true);
            if (text != null)
            {
                text.fontSize = 18;
                text.alignment = TextAnchor.MiddleCenter;
                text.supportRichText = true;
            }
            return button;
        }

        private static void SetButtonSelected(Button button, bool selected)
        {
            if (button == null)
            {
                return;
            }
            ColorBlock colors = button.colors;
            colors.normalColor = selected ? SelectedButtonColor : NormalButtonColor;
            colors.highlightedColor = selected ? new Color(1f, 0.72f, 0.3f, 1f) : Color.white;
            colors.selectedColor = colors.highlightedColor;
            button.colors = colors;
        }

        private static void SetPanelPosition(GameObject panel, Vector2 position)
        {
            RectTransform rect = panel?.GetComponent<RectTransform>();
            if (rect != null)
            {
                rect.anchoredPosition = position;
            }
        }

        private static Vector2 GetPanelPosition(GameObject panel)
        {
            RectTransform rect = panel?.GetComponent<RectTransform>();
            return rect == null ? Vector2.zero : rect.anchoredPosition;
        }

        private static void UpdateQueuePrompt()
        {
            if (_queueRoot == null || _queueText == null)
            {
                return;
            }
            var remaining = Math.Max(0, (int)Math.Ceiling((_queueDeadlineUtc - DateTime.UtcNow).TotalSeconds));
            _queueText.text = "Accept within " + remaining + " seconds or you will move to the back of the queue.";
            if (remaining == 0)
            {
                CloseQueuePrompt();
            }
        }

        private static void UpdateFoodPreparationTimer()
        {
            FoodPreparationState state = _foodPreparation;
            if (state?.DeadlineLabel == null)
            {
                return;
            }
            int remaining = Math.Max(0, (int)Math.Ceiling((state.DeadlineUtc - DateTime.UtcNow).TotalSeconds));
            state.DeadlineLabel.text =
                "Choose exactly three foods your character has discovered.  Ready in " + remaining + "s";
        }

        private static void CloseModal()
        {
            _combinedLeaderboardActive = false;
            _biomeLeaderboardEntries = null;
            _starLeaderboardEntries = null;
            Destroy(ref _creaturePickerRoot);
            Destroy(ref _biomePickerRoot);
            Destroy(ref _modalRoot);
            _challengeMenu = null;
            _setupVisibilityButton = null;
            _terrainPermissionButton = null;
            _buildingPermissionButton = null;
            _pickupPermissionButton = null;
            _foodPreparation = null;
            RefreshInputBlock();
        }

        private static string SetupVisibilityButtonText()
        {
            return (ArenaWorldObjects.AdminSetupVisualsEnabled ? "Hide" : "Show") +
                   " setup/spawn visuals (" + ArenaWorldObjects.AdminSetupVisualShortcut + ")";
        }

        private static void UpdateSetupVisibilityButton()
        {
            if (_setupVisibilityButton == null)
            {
                return;
            }
            Text label = _setupVisibilityButton.GetComponentInChildren<Text>(true);
            if (label != null)
            {
                label.text = SetupVisibilityButtonText();
            }
        }

        private static void ToggleAdminPermission(string arenaId, ArenaAdminPermissionKind permission)
        {
            if (AdminMutationRequested == null)
            {
                ShowMessage("Arena administration is not connected to the server yet.");
                return;
            }
            bool enabled = !(AdminPermissionResolver?.Invoke(permission) ?? false);
            ArenaUiAdminMutationKind mutationKind;
            switch (permission)
            {
                case ArenaAdminPermissionKind.Terrain:
                    mutationKind = ArenaUiAdminMutationKind.SetTerrainPermission;
                    break;
                case ArenaAdminPermissionKind.Building:
                    mutationKind = ArenaUiAdminMutationKind.SetBuildingPermission;
                    break;
                default:
                    mutationKind = ArenaUiAdminMutationKind.SetPickupPermission;
                    break;
            }
            AdminMutationRequested(new ArenaUiAdminMutation
            {
                ArenaId = arenaId,
                Kind = mutationKind,
                Enabled = enabled
            });
        }

        private static string PermissionButtonText(ArenaAdminPermissionKind permission)
        {
            bool enabled = AdminPermissionResolver?.Invoke(permission) ?? false;
            string name = permission == ArenaAdminPermissionKind.Terrain ? "Terrain editing" :
                permission == ArenaAdminPermissionKind.Building ? "Build & demolish" : "Dropped-item pickup";
            return name + ": " + (enabled ? "ALLOWED" : "BLOCKED");
        }

        private static void UpdateAdminPermissionButtons()
        {
            UpdateButtonLabel(_terrainPermissionButton, PermissionButtonText(ArenaAdminPermissionKind.Terrain));
            UpdateButtonLabel(_buildingPermissionButton, PermissionButtonText(ArenaAdminPermissionKind.Building));
            UpdateButtonLabel(_pickupPermissionButton, PermissionButtonText(ArenaAdminPermissionKind.DroppedItemPickup));
        }

        private static void UpdateButtonLabel(Button button, string value)
        {
            if (button == null)
            {
                return;
            }
            Text label = button.GetComponentInChildren<Text>(true);
            if (label != null)
            {
                label.text = value;
            }
        }

        private static void CloseQueuePrompt()
        {
            Destroy(ref _queueRoot);
            _queueText = null;
            _queueArenaId = string.Empty;
            RefreshInputBlock();
        }

        private static void DestroyHud()
        {
            Destroy(ref _hudRoot);
            _hudText = null;
            _forfeitButton = null;
            _hudArenaId = string.Empty;
            _hudSnapshot = null;
            _hudElapsedBaseMilliseconds = 0L;
            _hudElapsedBaseTime = 0f;
            _nextHudTimerRefreshTime = 0f;
        }

        private static void UpdateLiveHudTimer()
        {
            if (_hudSnapshot == null || _hudText == null || _hudSnapshot.Phase != SessionPhase.Fighting)
            {
                return;
            }

            float now = Time.unscaledTime;
            if (now < _nextHudTimerRefreshTime)
            {
                return;
            }
            _nextHudTimerRefreshTime = now + 0.1f;
            long elapsed = _hudElapsedBaseMilliseconds +
                           Math.Max(0L, (long)((now - _hudElapsedBaseTime) * 1000f));
            RefreshHudText(elapsed);
        }

        private static bool IsHudPhase(SessionPhase phase)
        {
            return phase == SessionPhase.Staging ||
                   phase == SessionPhase.Countdown ||
                   phase == SessionPhase.Fighting ||
                   phase == SessionPhase.Intermission ||
                   phase == SessionPhase.Victory ||
                   phase == SessionPhase.Defeat ||
                   phase == SessionPhase.Forfeit ||
                   phase == SessionPhase.Recovering;
        }

        private static bool ShouldDisplayHud(ArenaClientSnapshot snapshot)
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                return false;
            }
            if (snapshot.CombatantPlayerId == player.GetPlayerID() || snapshot.QueuePosition > 0)
            {
                return true;
            }
            Vector3 position = player.transform.position;
            float dx = position.x - snapshot.CorePosition.X;
            float dz = position.z - snapshot.CorePosition.Z;
            return dx * dx + dz * dz <= snapshot.ProtectedRadius * snapshot.ProtectedRadius;
        }

        private static bool IsForfeitPhase(SessionPhase phase)
        {
            return phase == SessionPhase.Staging ||
                   phase == SessionPhase.Countdown ||
                   phase == SessionPhase.Fighting ||
                   phase == SessionPhase.Intermission;
        }

        private static void Destroy(ref GameObject gameObject)
        {
            if (gameObject != null)
            {
                UnityEngine.Object.Destroy(gameObject);
                gameObject = null;
            }
        }

        private static void RefreshInputBlock()
        {
            SetInputBlocked(_modalRoot != null || _creaturePickerRoot != null ||
                            _biomePickerRoot != null || _queueRoot != null);
        }

        private static void SetInputBlocked(bool block)
        {
            if (_inputBlocked == block)
            {
                return;
            }
            GUIManager.BlockInput(block);
            _inputBlocked = block;
        }

        private static void ShowMessage(string text)
        {
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, text, 0, null);
        }

        private static string StarLabel(StarLevel stars)
        {
            switch (stars)
            {
                case StarLevel.OneStar: return "1 star";
                case StarLevel.TwoStar: return "2 stars";
                default: return "base";
            }
        }

        private static string BiomeLabel(BiomeTier biome)
        {
            return biome == BiomeTier.BlackForest ? "Black Forest" : biome.ToString();
        }

        private static string FormatTime(long milliseconds)
        {
            return ArenaDurationFormatter.FormatHms(milliseconds);
        }

        private sealed class ChallengeMenuState
        {
            internal string ArenaId;
            internal ChallengeMode Mode;
            internal ProgressionCapMode CapMode;
            internal BiomeTier SelectedBiome;
            internal CreatureDefinition SelectedCreature;
            internal StarLevel Stars;
            internal int Quantity;
            internal Button[] ModeButtons;
            internal Button[] CapButtons;
            internal Button BiomeButton;
            internal Button[] StarButtons;
            internal Button ChangeCreatureButton;
            internal Button DecreaseQuantityButton;
            internal Button IncreaseQuantityButton;
            internal Image CreatureIcon;
            internal Image SecondaryCreatureIcon;
            internal Text CreatureSummary;
            internal Text ScopeSummary;
            internal Text StarsLabel;
            internal Text QuantityLabel;
            internal Text QuantityText;
            internal Text EncounterSummary;
        }

        private sealed class FoodPreparationState
        {
            internal string ArenaId;
            internal List<ArenaFoodDefinition> Foods;
            internal List<string> SelectedPrefabNames;
            internal DateTime DeadlineUtc;
            internal float[] ScrollPositions;
            internal ScrollRect[] ScrollRects;
            internal Text DeadlineLabel;
        }
    }

    public sealed class ArenaUiDriver : MonoBehaviour
    {
        private void Update()
        {
            ArenaUi.DriverUpdate();
        }
    }
}
