using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ArenaGuard.Domain;
using ArenaGuard.Rules;
using Jotunn.Managers;
using UnityEngine;

namespace ArenaGuard.World
{
    public sealed class ArenaLeaderboardBoardBehaviour : ArenaWorldObjectBehaviour, Hoverable
    {
        private sealed class BoardSection
        {
            internal ArenaLeaderboardCategory Category;
            internal TextMesh Heading;
            internal TextMesh BiomeTitle;
            internal TextMesh BiomeEntries;
            internal TextMesh StarTitle;
            internal TextMesh StarEntries;
        }

        private static readonly HashSet<ArenaLeaderboardBoardBehaviour> Instances =
            new HashSet<ArenaLeaderboardBoardBehaviour>();
        private static readonly Dictionary<string, List<LeaderboardEntry>> CachedEntries =
            new Dictionary<string, List<LeaderboardEntry>>(StringComparer.Ordinal);
        private static readonly HashSet<string> PendingRequests =
            new HashSet<string>(StringComparer.Ordinal);
        private static float _nextGlobalRequestRetryTime;

        private readonly MaterialPropertyBlock _glowProperties = new MaterialPropertyBlock();
        private readonly List<BoardSection> _sections = new List<BoardSection>();
        private Renderer[] _woodRenderers = Array.Empty<Renderer>();
        private TextMesh _headerFront;
        private TextMesh _taglineFront;
        private float _nextRefreshTime;
        private float _nextDataRequestTime;
        private float _glowUntil;
        private bool _glowCleared = true;
        private static int _textOcclusionMask;

        protected override void Awake()
        {
            base.Awake();
            _woodRenderers = GetComponentsInChildren<Renderer>(true);
        }

        protected override void Start()
        {
            // This decoration is independent of an arena and therefore must not
            // enter the arena placement mutation pipeline used by setup pieces.
            Instances.Add(this);
            EnsureTextMeshes();
            RenderBoard();
            UpdateFrontTextVisibility();
            RequestMissingCategories();
        }

        private void OnDestroy()
        {
            Instances.Remove(this);
        }

        private void Update()
        {
            if (!_glowCleared)
            {
                UpdateGlow();
            }
            if (Time.unscaledTime < _nextRefreshTime)
            {
                return;
            }
            _nextRefreshTime = Time.unscaledTime + 0.5f;
            if (_headerFront == null && !GUIManager.IsHeadless())
            {
                EnsureTextMeshes();
                RenderBoard();
            }
            UpdateFrontTextVisibility();
            if (Time.unscaledTime >= _nextDataRequestTime && !HasCompleteBoardCache())
            {
                RequestMissingCategories();
            }
        }

        public string GetHoverName()
        {
            return ArenaWorldObjects.Localize("$arenaguard_leaderboard_board");
        }

        public string GetHoverText()
        {
            return GetHoverName();
        }

        internal static void ReceiveLeaderboard(LeaderboardKey key, IEnumerable<LeaderboardEntry> entries)
        {
            if (key == null || (key.Mode != ChallengeMode.BiomeLadder && key.Mode != ChallengeMode.StarLadder))
            {
                return;
            }
            string cacheKey = CacheKey(key);
            List<LeaderboardEntry> next = ArenaLeaderboardBoardPolicy.CompletedTopFive(entries);
            bool hadPrevious = CachedEntries.TryGetValue(cacheKey, out List<LeaderboardEntry> previous);
            bool changed = hadPrevious && !SameResults(previous, next);
            CachedEntries[cacheKey] = next;
            PendingRequests.Remove(cacheKey);
            foreach (ArenaLeaderboardBoardBehaviour board in Instances.ToArray())
            {
                if (board == null)
                {
                    continue;
                }
                board.RenderBoard();
                if (changed)
                {
                    board.BeginGlow();
                }
            }
        }

        internal static void ResetClientState()
        {
            CachedEntries.Clear();
            PendingRequests.Clear();
            _nextGlobalRequestRetryTime = 0f;
            Instances.Clear();
        }

        private void RequestMissingCategories()
        {
            _nextDataRequestTime = Time.unscaledTime + 5f;
            Action<LeaderboardKey> request = ArenaWorldObjects.LeaderboardRequested;
            if (request == null || Player.m_localPlayer == null)
            {
                return;
            }
            if (Time.unscaledTime >= _nextGlobalRequestRetryTime)
            {
                PendingRequests.Clear();
                _nextGlobalRequestRetryTime = Time.unscaledTime + 5f;
            }
            foreach (ArenaLeaderboardCategory category in ArenaLeaderboardBoardPolicy.DisplayOrder)
            {
                RequestIfMissing(request, ArenaLeaderboardBoardPolicy.Key(category, ChallengeMode.BiomeLadder));
                RequestIfMissing(request, ArenaLeaderboardBoardPolicy.Key(category, ChallengeMode.StarLadder));
            }
        }

        private static void RequestIfMissing(Action<LeaderboardKey> request, LeaderboardKey key)
        {
            string cacheKey = CacheKey(key);
            if (!CachedEntries.ContainsKey(cacheKey) && PendingRequests.Add(cacheKey))
            {
                request(key);
            }
        }

        private static bool HasCompleteBoardCache()
        {
            foreach (ArenaLeaderboardCategory category in ArenaLeaderboardBoardPolicy.DisplayOrder)
            {
                if (!CachedEntries.ContainsKey(CacheKey(
                        ArenaLeaderboardBoardPolicy.Key(category, ChallengeMode.BiomeLadder))) ||
                    !CachedEntries.ContainsKey(CacheKey(
                        ArenaLeaderboardBoardPolicy.Key(category, ChallengeMode.StarLadder))))
                {
                    return false;
                }
            }
            return true;
        }

        private void EnsureTextMeshes()
        {
            if (_headerFront != null || GUIManager.IsHeadless() || GUIManager.Instance == null)
            {
                return;
            }
            Font bold = GUIManager.Instance.AveriaSerifBold;
            Font regular = GUIManager.Instance.AveriaSerif;
            if (bold == null || regular == null)
            {
                return;
            }

            const float face = -0.151f;
            _headerFront = CreateText("HeaderFront", new Vector3(0f, 3.72f, face),
                TextAnchor.MiddleCenter, TextAlignment.Center, bold, 0.036f, 72);
            _taglineFront = CreateText("TaglineFront", new Vector3(0f, 3.42f, face),
                TextAnchor.MiddleCenter, TextAlignment.Center, regular, 0.024f, 48);

            AddSection(ArenaLeaderboardCategory.BlackForest, "BlackForest", -7.425f, -8.04375f, -6.80625f, 3.08f, bold, face);
            AddSection(ArenaLeaderboardCategory.Swamp, "Swamp", -4.95f, -5.56875f, -4.33125f, 3.08f, bold, face);
            AddSection(ArenaLeaderboardCategory.Mountain, "Mountain", -2.475f, -3.09375f, -1.85625f, 3.08f, bold, face);
            AddSection(ArenaLeaderboardCategory.Plains, "Plains", 0f, -0.61875f, 0.61875f, 3.08f, bold, face);
            AddSection(ArenaLeaderboardCategory.Mistlands, "Mistlands", 2.475f, 1.85625f, 3.09375f, 3.08f, bold, face);
            AddSection(ArenaLeaderboardCategory.Ashlands, "Ashlands", 4.95f, 4.33125f, 5.56875f, 3.08f, bold, face);
            AddSection(ArenaLeaderboardCategory.Gauntlet, "Gauntlet", 7.425f, 6.80625f, 8.04375f, 3.08f, bold, face);
        }

        private void AddSection(
            ArenaLeaderboardCategory category,
            string name,
            float centerX,
            float biomeX,
            float starX,
            float topY,
            Font bold,
            float face)
        {
            _sections.Add(new BoardSection
            {
                Category = category,
                Heading = CreateText(name + "Heading", new Vector3(centerX, topY, face),
                    TextAnchor.MiddleCenter, TextAlignment.Center, bold, 0.022f, 46),
                BiomeTitle = CreateText(name + "BiomeTitle", new Vector3(biomeX, topY - 0.21f, face),
                    TextAnchor.UpperCenter, TextAlignment.Center, bold, 0.020f, 38),
                BiomeEntries = CreateText(name + "BiomeEntries", new Vector3(biomeX, topY - 0.43f, face),
                    TextAnchor.UpperCenter, TextAlignment.Center, bold, 0.020f, 38, 0.52f),
                StarTitle = CreateText(name + "StarTitle", new Vector3(starX, topY - 0.21f, face),
                    TextAnchor.UpperCenter, TextAlignment.Center, bold, 0.020f, 38),
                StarEntries = CreateText(name + "StarEntries", new Vector3(starX, topY - 0.43f, face),
                    TextAnchor.UpperCenter, TextAlignment.Center, bold, 0.020f, 38, 0.52f)
            });
        }

        private TextMesh CreateText(
            string name,
            Vector3 position,
            TextAnchor anchor,
            TextAlignment alignment,
            Font font,
            float characterSize,
            int fontSize,
            float lineSpacing = 0.82f)
        {
            var host = new GameObject(name);
            host.layer = gameObject.layer;
            host.transform.SetParent(transform, false);
            host.transform.localPosition = position;
            host.transform.localRotation = Quaternion.identity;
            var text = host.AddComponent<TextMesh>();
            text.font = font;
            text.fontSize = fontSize;
            text.characterSize = characterSize;
            text.anchor = anchor;
            text.alignment = alignment;
            text.color = Color.white;
            text.richText = true;
            text.lineSpacing = lineSpacing;
            MeshRenderer renderer = host.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = font.material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return text;
        }

        private void RenderBoard()
        {
            EnsureTextMeshes();
            if (_headerFront == null)
            {
                return;
            }
            _headerFront.text = "<color=#F4D58A><b>HALL OF CHAMPIONS</b></color>";
            _taglineFront.text = "<color=#CFA65C>—  RECORDS OF VALOR  •  TOP FIVE  —</color>";
            foreach (BoardSection section in _sections)
            {
                section.Heading.text = "<color=#E2B35D><b>★  " +
                                       ArenaLeaderboardBoardPolicy.Label(section.Category).ToUpperInvariant() +
                                       "  ★</b></color>";
                section.BiomeTitle.text = "<color=#E8CC91><b>BIOME LADDER</b></color>";
                section.StarTitle.text = "<color=#E8CC91><b>STAR LADDER</b></color>";
                LeaderboardKey biomeKey = ArenaLeaderboardBoardPolicy.Key(
                    section.Category, ChallengeMode.BiomeLadder);
                LeaderboardKey starKey = ArenaLeaderboardBoardPolicy.Key(
                    section.Category, ChallengeMode.StarLadder);
                section.BiomeEntries.text = BuildColumn(
                    CachedEntries.TryGetValue(CacheKey(biomeKey), out List<LeaderboardEntry> biome)
                        ? biome
                        : null);
                section.StarEntries.text = BuildColumn(
                    CachedEntries.TryGetValue(CacheKey(starKey), out List<LeaderboardEntry> stars)
                        ? stars
                        : null);
            }
        }

        private static string BuildColumn(List<LeaderboardEntry> entries)
        {
            var builder = new StringBuilder();
            if (entries == null)
            {
                return builder.Append("<color=#D9C7A5>Loading records...</color>").ToString();
            }
            if (entries.Count == 0)
            {
                return builder.Append("<color=#D9C7A5>— No completed runs —</color>").ToString();
            }
            for (int index = 0; index < entries.Count && index < ArenaLeaderboardBoardPolicy.MaximumEntries; index++)
            {
                LeaderboardEntry entry = entries[index];
                string name = string.IsNullOrWhiteSpace(entry.PlayerName) ? "Unknown" : entry.PlayerName.Trim();
                name = name.Replace('<', '‹').Replace('>', '›');
                if (name.Length > 9)
                {
                    name = name.Substring(0, 8) + "…";
                }
                string rankColor = index == 0 ? "#FFD36A" : index == 1 ? "#D9E0E8" : index == 2 ? "#D28A55" : "#FFFFFF";
                builder.Append("<color=").Append(rankColor).Append("><b>")
                    .Append(index + 1).Append(". ").Append(name).Append("  ")
                    .Append(ArenaLeaderboardBoardPolicy.FormatDuration(entry.ElapsedMilliseconds))
                    .Append("</b></color>\n<size=21><color=#D9C7A5>   ")
                    .Append(ArenaLeaderboardBoardPolicy.FormatCompactDate(entry))
                    .Append("</color></size>\n");
            }
            return builder.ToString();
        }

        private void UpdateFrontTextVisibility()
        {
            Camera camera = Utils.GetMainCamera();
            bool facingFront = camera == null ||
                               transform.InverseTransformPoint(camera.transform.position).z < 0f;
            SetTextVisible(_headerFront, facingFront && HasClearLineOfSight(camera, _headerFront));
            SetTextVisible(_taglineFront, facingFront && HasClearLineOfSight(camera, _taglineFront));
            foreach (BoardSection section in _sections)
            {
                bool headingVisible = facingFront && HasClearLineOfSight(camera, section.Heading);
                bool biomeVisible = facingFront && HasClearLineOfSight(camera, section.BiomeEntries);
                bool starVisible = facingFront && HasClearLineOfSight(camera, section.StarEntries);
                SetTextVisible(section.Heading, headingVisible);
                SetTextVisible(section.BiomeTitle, biomeVisible);
                SetTextVisible(section.BiomeEntries, biomeVisible);
                SetTextVisible(section.StarTitle, starVisible);
                SetTextVisible(section.StarEntries, starVisible);
            }
        }

        private static bool HasClearLineOfSight(Camera camera, TextMesh text)
        {
            if (camera == null || text == null)
            {
                return camera == null;
            }
            Vector3 origin = camera.transform.position;
            Vector3 offset = text.transform.position - origin;
            float distance = offset.magnitude;
            if (distance <= 0.2f)
            {
                return true;
            }
            if (_textOcclusionMask == 0)
            {
                _textOcclusionMask = LayerMask.GetMask(
                    "Default", "static_solid", "terrain", "vehicle", "piece", "viewblock");
            }
            return !Physics.Raycast(origin, offset / distance, distance - 0.15f,
                _textOcclusionMask, QueryTriggerInteraction.Ignore);
        }

        private static void SetTextVisible(TextMesh text, bool visible)
        {
            Renderer renderer = text != null ? text.GetComponent<Renderer>() : null;
            if (renderer != null && renderer.enabled != visible)
            {
                renderer.enabled = visible;
            }
        }

        private void BeginGlow()
        {
            _glowUntil = Time.unscaledTime + 3f;
            _glowCleared = false;
        }

        private void UpdateGlow()
        {
            float remaining = _glowUntil - Time.unscaledTime;
            if (remaining <= 0f)
            {
                foreach (Renderer renderer in _woodRenderers)
                {
                    if (renderer != null)
                    {
                        renderer.SetPropertyBlock(null);
                    }
                }
                _glowCleared = true;
                return;
            }
            float pulse = 0.35f + 0.25f * Mathf.Sin(Time.unscaledTime * 8f);
            Color gold = new Color(1f, 0.58f, 0.12f, 1f);
            foreach (Renderer renderer in _woodRenderers)
            {
                if (renderer == null)
                {
                    continue;
                }
                Material material = renderer.sharedMaterial;
                _glowProperties.Clear();
                renderer.GetPropertyBlock(_glowProperties);
                if (material != null && material.HasProperty("_Color"))
                    _glowProperties.SetColor("_Color", Color.Lerp(Color.white, gold, pulse));
                if (material != null && material.HasProperty("_BaseColor"))
                    _glowProperties.SetColor("_BaseColor", Color.Lerp(Color.white, gold, pulse));
                if (material != null && material.HasProperty("_EmissionColor"))
                    _glowProperties.SetColor("_EmissionColor", gold * pulse);
                renderer.SetPropertyBlock(_glowProperties);
            }
        }

        private static bool SameResults(IList<LeaderboardEntry> left, IList<LeaderboardEntry> right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null || left.Count != right.Count) return false;
            for (int index = 0; index < left.Count; index++)
            {
                if (left[index].PlayerId != right[index].PlayerId ||
                    left[index].ElapsedMilliseconds != right[index].ElapsedMilliseconds ||
                    left[index].RecordedUtc != right[index].RecordedUtc)
                    return false;
            }
            return true;
        }

        private static string CacheKey(LeaderboardKey key)
        {
            return ((int)key.Mode).ToString(CultureInfo.InvariantCulture) + ":" +
                   ((int)key.CapMode).ToString(CultureInfo.InvariantCulture) + ":" +
                   ((int)key.SelectedBiome).ToString(CultureInfo.InvariantCulture);
        }
    }
}
