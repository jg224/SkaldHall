using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ArenaGuard.Domain;
using UnityEngine;

namespace ArenaGuard.Rules
{
    /// <summary>
    /// Narrow composition seam between Harmony patches and the authoritative runtime.
    /// Defaults are deliberately inert so patch installation is safe during world loading.
    /// </summary>
    internal static class ArenaRuleContext
    {
        private static ConditionalWeakTable<Character, CachedEnemyArena> _enemyArenaCache =
            new ConditionalWeakTable<Character, CachedEnemyArena>();
        private static readonly Dictionary<string, CachedCombatant> CombatantCache =
            new Dictionary<string, CachedCombatant>(StringComparer.Ordinal);

        internal static Func<Vector3, bool> IsProtectedPoint = _ => false;
        internal static Func<Vector3, bool> IsActiveProtectedPoint = _ => false;
        internal static Func<Player, bool> IsAdministrator = _ => false;
        internal static Func<bool> AdminsMayModifyTerrain = () => false;
        internal static Func<bool> AdminsMayBuild = () => false;
        internal static Func<bool> AdminsMayPickupDroppedItems = () => false;
        internal static Func<bool> AreLocalDevcommandsEnabled = () => false;
        internal static Func<long, ArenaRole> ResolveRole = _ => ArenaRole.Visitor;
        internal static Func<Character, string> ResolveEnemyArenaId = _ => null;
        internal static Func<string, long> ResolveCombatant = _ => 0L;
        internal static Action<long> ReportLethalDamage = _ => { };
        internal static Action<Character> ReportArenaEnemyDeath = _ => { };
        internal static Action<Character, GameObject> RegisterArenaSummon = (_, __) => { };

        internal static ArenaRole GetRole(Player player)
        {
            if (player == null)
            {
                return ArenaRole.Visitor;
            }

            try
            {
                return ResolveRole(player.GetPlayerID());
            }
            catch (Exception error)
            {
                Plugin.Log?.LogWarning("Arena role lookup failed: " + error.Message);
                return ArenaRole.Visitor;
            }
        }

        internal static bool IsCombatant(Player player)
        {
            return GetRole(player) == ArenaRole.Combatant;
        }

        internal static bool CanAdminBuild(Player player)
        {
            return ArenaAdminPermissionPolicy.CanBuild(
                player != null && IsAdministrator(player),
                IsCombatant(player),
                AdminsMayBuild());
        }

        internal static bool CanAdminModifyTerrain(Player player)
        {
            return ArenaAdminPermissionPolicy.CanModifyTerrain(
                player != null && IsAdministrator(player),
                IsCombatant(player),
                AdminsMayModifyTerrain(),
                AreLocalDevcommandsEnabled());
        }

        internal static bool CanPickupDroppedItems(Player player)
        {
            return ArenaAdminPermissionPolicy.CanPickup(
                GetRole(player),
                player != null && IsAdministrator(player),
                AdminsMayPickupDroppedItems());
        }

        internal static bool IsProtectedDamage(Component target, HitData hit)
        {
            return target != null && IsProtectedPoint(target.transform.position) ||
                   hit != null && IsProtectedPoint(hit.m_point);
        }

        internal static bool IsSpectator(Player player)
        {
            ArenaRole role = GetRole(player);
            return role == ArenaRole.Spectator || role == ArenaRole.Queued;
        }

        internal static bool IsArenaParticipant(Player player)
        {
            ArenaRole role = GetRole(player);
            return role == ArenaRole.Combatant || role == ArenaRole.Spectator;
        }

        internal static string GetEnemyArenaId(Character character)
        {
            if (character == null)
            {
                return null;
            }

            if (!_enemyArenaCache.TryGetValue(character, out CachedEnemyArena cached))
            {
                cached = new CachedEnemyArena();
                _enemyArenaCache.Add(character, cached);
            }
            float now = Time.unscaledTime;
            if (cached.Initialized && (!string.IsNullOrEmpty(cached.ArenaId) || now < cached.RefreshAfter))
            {
                return cached.ArenaId;
            }

            cached.ArenaId = ResolveEnemyArenaId(character);
            cached.Initialized = true;
            cached.RefreshAfter = now + 1f;
            return cached.ArenaId;
        }

        internal static void CacheEnemyArenaId(Character character, string arenaId)
        {
            if (character == null)
            {
                return;
            }
            if (!_enemyArenaCache.TryGetValue(character, out CachedEnemyArena cached))
            {
                cached = new CachedEnemyArena();
                _enemyArenaCache.Add(character, cached);
            }
            cached.ArenaId = arenaId;
            cached.Initialized = true;
            cached.RefreshAfter = float.PositiveInfinity;
        }

        internal static void ForgetEnemy(Character character)
        {
            if (character != null)
            {
                _enemyArenaCache.Remove(character);
            }
        }

        internal static long GetCombatantId(string arenaId)
        {
            if (string.IsNullOrEmpty(arenaId))
            {
                return 0L;
            }
            float now = Time.unscaledTime;
            if (CombatantCache.TryGetValue(arenaId, out CachedCombatant cached) && now < cached.RefreshAfter)
            {
                return cached.PlayerId;
            }
            long playerId = ResolveCombatant(arenaId);
            CombatantCache[arenaId] = new CachedCombatant
            {
                PlayerId = playerId,
                RefreshAfter = now + 0.25f
            };
            return playerId;
        }

        internal static bool IsRecoveryConsumable(ItemDrop.ItemData item)
        {
            if (item?.m_shared == null ||
                item.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Consumable)
            {
                return false;
            }

            SE_Stats stats = item.m_shared.m_consumeStatusEffect as SE_Stats;
            return stats != null &&
                   (stats.m_healthUpFront > 0f ||
                    stats.m_healthOverTime > 0f ||
                    stats.m_healthPerTick > 0f ||
                    stats.m_staminaUpFront > 0f ||
                    stats.m_staminaOverTime > 0f ||
                    stats.m_eitrUpFront > 0f ||
                    stats.m_eitrOverTime > 0f);
        }

        internal static bool IsAllowedArenaConsumable(ItemDrop.ItemData item)
        {
            if (item?.m_shared == null)
            {
                return false;
            }

            SE_Stats stats = item.m_shared.m_consumeStatusEffect as SE_Stats;
            return ArenaConsumablePolicy.IsAllowed(
                item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Consumable,
                item.m_shared.m_food > 0f ||
                    item.m_shared.m_foodStamina > 0f ||
                    item.m_shared.m_foodEitr > 0f,
                stats != null &&
                    (stats.m_healthUpFront > 0f ||
                     stats.m_healthOverTime > 0f ||
                     stats.m_healthPerTick > 0f),
                stats != null &&
                    (stats.m_staminaUpFront > 0f || stats.m_staminaOverTime > 0f),
                stats != null &&
                    (stats.m_eitrUpFront > 0f || stats.m_eitrOverTime > 0f));
        }

        internal static void Reset()
        {
            IsProtectedPoint = _ => false;
            IsActiveProtectedPoint = _ => false;
            IsAdministrator = _ => false;
            AdminsMayModifyTerrain = () => false;
            AdminsMayBuild = () => false;
            AdminsMayPickupDroppedItems = () => false;
            AreLocalDevcommandsEnabled = () => false;
            ResolveRole = _ => ArenaRole.Visitor;
            ResolveEnemyArenaId = _ => null;
            ResolveCombatant = _ => 0L;
            ReportLethalDamage = _ => { };
            ReportArenaEnemyDeath = _ => { };
            RegisterArenaSummon = (_, __) => { };
            _enemyArenaCache = new ConditionalWeakTable<Character, CachedEnemyArena>();
            CombatantCache.Clear();
        }

        private sealed class CachedEnemyArena
        {
            internal bool Initialized;
            internal string ArenaId;
            internal float RefreshAfter;
        }

        private struct CachedCombatant
        {
            internal long PlayerId;
            internal float RefreshAfter;
        }
    }
}
