using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using ArenaGuard.Domain;
using ArenaGuard.World;
using HarmonyLib;
using UnityEngine;

namespace ArenaGuard.Rules
{
    [HarmonyPatch(typeof(SpawnSystem), "IsSpawnPointGood")]
    internal static class ProtectedNaturalSpawnPatch
    {
        [HarmonyPostfix]
        private static void Postfix(ref Vector3 spawnPoint, ref bool __result)
        {
            if (__result && ArenaRuleContext.IsProtectedPoint(spawnPoint))
            {
                __result = false;
            }
        }
    }

    [HarmonyPatch(typeof(CreatureSpawner), "Spawn")]
    internal static class ProtectedCreatureSpawnerPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(CreatureSpawner __instance, ref ZNetView __result)
        {
            if (__instance == null || !ArenaRuleContext.IsProtectedPoint(__instance.transform.position))
            {
                return true;
            }

            __result = null;
            return false;
        }
    }

    [HarmonyPatch(typeof(SpawnArea), "FindSpawnPoint")]
    internal static class ProtectedSpawnAreaPatch
    {
        [HarmonyPostfix]
        private static void Postfix(ref Vector3 point, ref bool __result)
        {
            if (__result && ArenaRuleContext.IsProtectedPoint(point))
            {
                __result = false;
            }
        }
    }

    [HarmonyPatch]
    internal static class ArenaSpawnAbilityPatch
    {
        private static readonly FieldInfo OwnerField = AccessTools.Field(typeof(SpawnAbility), "m_owner");
        private static FieldInfo _abilityField;

        private static MethodBase TargetMethod()
        {
            MethodInfo spawn = AccessTools.Method(typeof(SpawnAbility), "Spawn");
            MethodInfo moveNext = spawn == null ? null : AccessTools.EnumeratorMoveNext(spawn);
            _abilityField = moveNext == null ? null : AccessTools.Field(moveNext.DeclaringType, "<>4__this");
            if (moveNext == null || _abilityField == null || OwnerField == null)
            {
                throw new MissingMethodException("Valheim SpawnAbility.Spawn state machine is unavailable.");
            }
            return moveNext;
        }

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int patchedInstantiations = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                yield return instruction;
                if (!(instruction.operand is MethodInfo method) ||
                    method.DeclaringType != typeof(UnityEngine.Object) ||
                    method.Name != nameof(UnityEngine.Object.Instantiate) ||
                    !method.IsGenericMethod ||
                    method.GetGenericArguments().Length != 1 ||
                    method.GetGenericArguments()[0] != typeof(GameObject) ||
                    method.GetParameters().Length != 3)
                {
                    continue;
                }

                // Preserve the instantiated GameObject for Valheim's following
                // stloc while passing a duplicate and the owning SpawnAbility to
                // ArenaGuard's authoritative registration callback.
                yield return new CodeInstruction(OpCodes.Dup);
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Ldfld, _abilityField);
                yield return new CodeInstruction(OpCodes.Call,
                    AccessTools.Method(typeof(ArenaSpawnAbilityPatch), nameof(RegisterSpawned)));
                patchedInstantiations++;
            }

            if (patchedInstantiations != 1)
            {
                throw new InvalidOperationException(
                    "ArenaGuard expected exactly one SpawnAbility character creation point but found " +
                    patchedInstantiations + ".");
            }
        }

        private static void RegisterSpawned(GameObject spawned, SpawnAbility source)
        {
            Character owner = source == null ? null : OwnerField?.GetValue(source) as Character;
            if (owner != null && spawned != null)
            {
                ArenaRuleContext.RegisterArenaSummon(owner, spawned);
            }
        }
    }

    [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.Damage), new[] { typeof(HitData) })]
    internal static class ProtectedStructureDamagePatch
    {
        [HarmonyPrefix]
        private static bool Prefix(WearNTear __instance, HitData hit)
        {
            return !ArenaRuleContext.IsProtectedDamage(__instance, hit);
        }
    }

    [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.ApplyDamage),
        new[] { typeof(float), typeof(HitData) })]
    internal static class ProtectedStructureApplyDamagePatch
    {
        [HarmonyPrefix]
        private static bool Prefix(WearNTear __instance, HitData hitData, ref bool __result)
        {
            if (!ArenaRuleContext.IsProtectedDamage(__instance, hitData))
            {
                return true;
            }

            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(Piece), nameof(Piece.DropResources), new[] { typeof(HitData) })]
    internal static class ProtectedStructureDropPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(Piece __instance, HitData hitData)
        {
            // A null HitData is Valheim's normal admin-hammer removal path and
            // must continue returning construction resources. Damage-driven
            // destruction always carries HitData and must never create refunds
            // inside a protected arena.
            return hitData == null || !ArenaRuleContext.IsProtectedDamage(__instance, hitData);
        }
    }

    [HarmonyPatch]
    internal static class ProtectedWorldObjectDamagePatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return RequireDamageMethod(typeof(WearNTear), "RPC_Damage",
                typeof(long), typeof(HitData));
            yield return RequireDamageMethod(typeof(Destructible), "RPC_Damage",
                typeof(long), typeof(HitData));
            yield return RequireDamageMethod(typeof(MineRock), "RPC_Hit",
                typeof(long), typeof(HitData), typeof(int));
            yield return RequireDamageMethod(typeof(TreeBase), "RPC_Damage",
                typeof(long), typeof(HitData));
            yield return RequireDamageMethod(typeof(TreeLog), "RPC_Damage",
                typeof(long), typeof(HitData));
        }

        private static MethodBase RequireDamageMethod(Type type, string name, params Type[] parameters)
        {
            MethodInfo method = AccessTools.DeclaredMethod(type, name, parameters);
            if (method == null)
            {
                throw new MissingMethodException(type.FullName, name);
            }
            return method;
        }

        [HarmonyPrefix]
        private static bool Prefix(Component __instance, HitData hit)
        {
            return !ArenaRuleContext.IsProtectedDamage(__instance, hit);
        }
    }

    [HarmonyPatch(typeof(MineRock5), "DamageArea", new[] { typeof(int), typeof(HitData) })]
    internal static class ProtectedMineRockDamagePatch
    {
        [HarmonyPrefix]
        private static bool Prefix(MineRock5 __instance, HitData hit, ref bool __result)
        {
            if (!ArenaRuleContext.IsProtectedDamage(__instance, hit))
            {
                return true;
            }

            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.TryPlacePiece), new[] { typeof(Piece) })]
    internal static class ProtectedBuildPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(Player __instance, ref bool __result)
        {
            if (__instance == null || !ArenaRuleContext.IsProtectedPoint(__instance.transform.position))
            {
                return true;
            }

            if (ArenaRuleContext.CanAdminBuild(__instance))
            {
                return true;
            }

            __result = false;
            __instance.Message(MessageHud.MessageType.Center, "$arenaguard_admin_build_only");
            return false;
        }
    }

    [HarmonyPatch(typeof(Player), "CheckCanRemovePiece", new[] { typeof(Piece) })]
    internal static class ProtectedDemolitionPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(Player __instance, Piece piece, ref bool __result)
        {
            if (piece == null || !ArenaRuleContext.IsProtectedPoint(piece.transform.position))
            {
                return true;
            }

            __result = ArenaRuleContext.CanAdminBuild(__instance);
            if (!__result)
            {
                __instance?.Message(MessageHud.MessageType.Center, "$arenaguard_admin_build_only");
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(TerrainOp), "Awake")]
    internal static class ProtectedTerrainPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(TerrainOp __instance)
        {
            if (__instance == null ||
                !ArenaRuleContext.IsProtectedPoint(__instance.transform.position))
            {
                return true;
            }

            Player player = Player.m_localPlayer;
            if (ArenaRuleContext.CanAdminModifyTerrain(player))
            {
                return true;
            }

            UnityEngine.Object.Destroy(__instance.gameObject);
            return false;
        }
    }

    [HarmonyPatch(typeof(Character), "RPC_Damage", new[] { typeof(long), typeof(HitData) })]
    internal static class SpectatorDamagePatch
    {
        [HarmonyPrefix]
        private static bool Prefix(Character __instance)
        {
            if (ArenaWorldObjects.IsChallengeHost(__instance))
            {
                return false;
            }
            Player player = __instance as Player;
            return player == null || !ArenaRuleContext.IsSpectator(player);
        }
    }

    [HarmonyPatch(typeof(Character), nameof(Character.ApplyDamage),
        new[] { typeof(HitData), typeof(bool), typeof(bool), typeof(HitData.DamageModifier) })]
    internal static class CombatantLethalDamagePatch
    {
        [HarmonyPostfix]
        private static void Postfix(Character __instance)
        {
            Player player = __instance as Player;
            if (player == null || !ArenaRuleContext.IsCombatant(player) || player.GetHealth() > 0f)
            {
                return;
            }

            player.SetHealth(1f);
            ArenaRuleContext.ReportLethalDamage(player.GetPlayerID());
        }
    }

    [HarmonyPatch(typeof(Character), "CheckDeath")]
    internal static class CombatantDeathSafetyPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(Character __instance)
        {
            Player player = __instance as Player;
            if (player != null)
            {
                if (!ArenaRuleContext.IsCombatant(player) || player.GetHealth() > 0f)
                {
                    return true;
                }

                player.SetHealth(1f);
                ArenaRuleContext.ReportLethalDamage(player.GetPlayerID());
                return false;
            }

            if (ArenaWorldObjects.IsChallengeHost(__instance))
            {
                if (__instance.GetHealth() <= 0f)
                {
                    __instance.SetHealth(__instance.GetMaxHealth());
                }
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Player), "Interact", new[] { typeof(GameObject), typeof(bool), typeof(bool) })]
    internal static class SpectatorInteractionPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(Player __instance, GameObject go)
        {
            if (!ArenaRuleContext.IsSpectator(__instance))
            {
                return true;
            }

            if (go != null &&
                (go.GetComponentInParent<ArenaChallengeHostBehaviour>() != null ||
                 go.GetComponentInParent<ArenaSignBehaviour>() != null))
            {
                return true;
            }

            if (go != null && go.GetComponentInParent<ItemDrop>() != null &&
                ArenaRuleContext.CanPickupDroppedItems(__instance))
            {
                return true;
            }

            return __instance != null &&
                   ArenaRuleContext.IsAdministrator(__instance) &&
                   go != null &&
                   go.GetComponentInParent<ArenaWorldObjectBehaviour>() != null;
        }
    }

    [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.Pickup),
        new[] { typeof(GameObject), typeof(bool), typeof(bool) })]
    internal static class ArenaPickupPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(Humanoid __instance, ref bool __result)
        {
            Player player = __instance as Player;
            if (player == null || ArenaRuleContext.CanPickupDroppedItems(player))
            {
                return true;
            }

            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(Player), "AutoPickup")]
    internal static class ArenaAutoPickupPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(Player __instance)
        {
            return ArenaRuleContext.CanPickupDroppedItems(__instance);
        }
    }

    [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.DropItem),
        new[] { typeof(Inventory), typeof(ItemDrop.ItemData), typeof(int) })]
    internal static class ArenaDropPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(Humanoid __instance, ItemDrop.ItemData item, ref bool __result)
        {
            Player player = __instance as Player;
            bool isAdminHammer = item?.m_dropPrefab != null && item.m_dropPrefab.name == "ArenaGuard_AdminHammer";
            if (!isAdminHammer && (player == null || !ArenaRuleContext.IsArenaParticipant(player)))
            {
                return true;
            }

            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.MoveItemToThis),
        new[] { typeof(Inventory), typeof(ItemDrop.ItemData) })]
    internal static class ArenaInventoryTransferPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(Inventory __instance, Inventory fromInventory, ItemDrop.ItemData item)
        {
            Player player = Player.m_localPlayer;
            if (player == null || fromInventory != player.GetInventory() || __instance == fromInventory)
            {
                return true;
            }

            bool isAdminHammer = item?.m_dropPrefab != null && item.m_dropPrefab.name == "ArenaGuard_AdminHammer";
            return !isAdminHammer && !ArenaRuleContext.IsArenaParticipant(player);
        }
    }

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.MoveItemToThis),
        new[] { typeof(Inventory), typeof(ItemDrop.ItemData), typeof(int), typeof(int), typeof(int) })]
    internal static class ArenaInventoryTransferAtPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(
            Inventory __instance,
            Inventory fromInventory,
            ItemDrop.ItemData item,
            ref bool __result)
        {
            Player player = Player.m_localPlayer;
            if (player == null || fromInventory != player.GetInventory() || __instance == fromInventory)
            {
                return true;
            }

            bool isAdminHammer = item?.m_dropPrefab != null && item.m_dropPrefab.name == "ArenaGuard_AdminHammer";
            if (!isAdminHammer && !ArenaRuleContext.IsArenaParticipant(player))
            {
                return true;
            }

            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(InventoryGui), "DoCrafting")]
    internal static class ArenaCraftingPatch
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            return Player.m_localPlayer == null || !ArenaRuleContext.IsArenaParticipant(Player.m_localPlayer);
        }
    }

    [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.CanConsumeItem),
        new[] { typeof(ItemDrop.ItemData), typeof(bool) })]
    internal static class ArenaConsumablePatch
    {
        [HarmonyPostfix]
        private static void Postfix(Humanoid __instance, ItemDrop.ItemData item, ref bool __result)
        {
            Player player = __instance as Player;
            if (player != null && ArenaRuleContext.IsCombatant(player))
            {
                __result = __result &&
                    ArenaRuleContext.IsAllowedArenaConsumable(item);
            }
        }
    }

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveItem),
        new[] { typeof(ItemDrop.ItemData), typeof(int) })]
    internal static class ArenaAmmoPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(Inventory __instance, ItemDrop.ItemData item, ref bool __result)
        {
            Player player = Player.m_localPlayer;
            if (player == null || player.GetInventory() != __instance || !ArenaRuleContext.IsCombatant(player) || item?.m_shared == null)
            {
                return true;
            }

            ItemDrop.ItemData.ItemType type = item.m_shared.m_itemType;
            if (type != ItemDrop.ItemData.ItemType.Ammo && type != ItemDrop.ItemData.ItemType.AmmoNonEquipable)
            {
                return true;
            }

            __result = true;
            return false;
        }
    }

    [HarmonyPatch(typeof(Skills), nameof(Skills.RaiseSkill),
        new[] { typeof(Skills.SkillType), typeof(float) })]
    internal static class ArenaSkillGainPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(Skills __instance)
        {
            foreach (Player player in Player.GetAllPlayers())
            {
                if (player != null && player.GetSkills() == __instance && ArenaRuleContext.IsCombatant(player))
                {
                    return false;
                }
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(Player), "DamageArmorDurability", new[] { typeof(HitData) })]
    internal static class ArenaArmorDurabilityPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(Player __instance)
        {
            return !ArenaRuleContext.IsCombatant(__instance);
        }
    }

    [HarmonyPatch(typeof(Humanoid), "DrainEquipedItemDurability",
        new[] { typeof(ItemDrop.ItemData), typeof(float) })]
    internal static class ArenaEquippedDurabilityPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(Humanoid __instance)
        {
            Player player = __instance as Player;
            return player == null || !ArenaRuleContext.IsCombatant(player);
        }
    }

    [HarmonyPatch]
    internal static class ArenaAttackDurabilityPatch
    {
        private static readonly FieldInfo CharacterField = AccessTools.Field(typeof(Attack), "m_character");
        private static readonly FieldInfo WeaponField = AccessTools.Field(typeof(Attack), "m_weapon");

        private static IEnumerable<MethodBase> TargetMethods()
        {
            string[] names = { "ProjectileAttackTriggered", "DoNonAttack", "DoMeleeAttack", "DoAreaAttack" };
            foreach (string name in names)
            {
                MethodInfo method = AccessTools.Method(typeof(Attack), name);
                if (method != null)
                {
                    yield return method;
                }
            }
        }

        [HarmonyPrefix]
        private static void Prefix(Attack __instance, out float? __state)
        {
            __state = null;
            Player player = CharacterField?.GetValue(__instance) as Player;
            ItemDrop.ItemData weapon = WeaponField?.GetValue(__instance) as ItemDrop.ItemData;
            if (player != null && weapon != null && ArenaRuleContext.IsCombatant(player))
            {
                __state = weapon.m_durability;
            }
        }

        [HarmonyPostfix]
        private static void Postfix(Attack __instance, float? __state)
        {
            if (!__state.HasValue)
            {
                return;
            }

            ItemDrop.ItemData weapon = WeaponField?.GetValue(__instance) as ItemDrop.ItemData;
            if (weapon != null)
            {
                weapon.m_durability = __state.Value;
            }
        }
    }

    [HarmonyPatch(typeof(BaseAI), nameof(BaseAI.IsEnemy), new[] { typeof(Character), typeof(Character) })]
    internal static class ArenaEnemyTargetPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(Character a, Character b, ref bool __result)
        {
            if (ArenaWorldObjects.IsChallengeHost(a) || ArenaWorldObjects.IsChallengeHost(b))
            {
                __result = false;
                return false;
            }

            string arenaA = ArenaRuleContext.GetEnemyArenaId(a);
            string arenaB = ArenaRuleContext.GetEnemyArenaId(b);
            if (!string.IsNullOrEmpty(arenaA))
            {
                Player combatantB = b as Player;
                __result = combatantB != null && combatantB.GetPlayerID() == ArenaRuleContext.GetCombatantId(arenaA);
                return false;
            }

            if (!string.IsNullOrEmpty(arenaB))
            {
                Player combatantA = a as Player;
                __result = combatantA != null && combatantA.GetPlayerID() == ArenaRuleContext.GetCombatantId(arenaB);
                return false;
            }

            if (a is Player playerA && ArenaRuleContext.IsSpectator(playerA) ||
                b is Player playerB && ArenaRuleContext.IsSpectator(playerB))
            {
                __result = false;
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Valheim randomizes a new MonsterAI's first target scan between zero and
    /// two seconds. Run on whichever peer currently owns the AI and remove that
    /// delay before UpdateAI executes. ArenaEnemyTargetPatch then restricts the
    /// immediate search to the authenticated combatant.
    /// </summary>
    [HarmonyPatch(typeof(MonsterAI), nameof(MonsterAI.UpdateAI))]
    internal static class ArenaEnemyImmediateAggroPatch
    {
        private static readonly FieldInfo UpdateTargetTimerField =
            AccessTools.Field(typeof(MonsterAI), "m_updateTargetTimer");
        private static readonly FieldInfo TargetCreatureField =
            AccessTools.Field(typeof(MonsterAI), "m_targetCreature");
        private static readonly MethodInfo WakeupMethod =
            AccessTools.Method(typeof(MonsterAI), "Wakeup", Type.EmptyTypes);
        private static readonly MethodInfo SetTargetMethod =
            AccessTools.Method(typeof(MonsterAI), "SetTarget", new[] { typeof(Character) });

        [HarmonyPrefix]
        private static void Prefix(MonsterAI __instance)
        {
            if (__instance == null)
            {
                return;
            }

            ZNetView view = __instance.GetComponent<ZNetView>();
            Character enemy = __instance.GetComponent<Character>();
            if (view == null || !view.IsValid() || !view.IsOwner() || enemy == null)
            {
                return;
            }

            string arenaId = ArenaRuleContext.GetEnemyArenaId(enemy);
            long combatantPlayerId = ArenaRuleContext.GetCombatantId(arenaId);
            if (string.IsNullOrEmpty(arenaId) || combatantPlayerId == 0L)
            {
                return;
            }

            Character currentTarget = __instance.GetTargetCreature();
            bool targetMatches = currentTarget is Player player &&
                                 player.GetPlayerID() == combatantPlayerId;
            if (!ArenaEnemyAggroPolicy.ShouldReassertTarget(
                    true,
                    true,
                    targetMatches,
                    __instance.IsSleeping(),
                    __instance.IsAlerted(),
                    __instance.HuntPlayer()))
            {
                return;
            }

            WakeupMethod?.Invoke(__instance, null);
            __instance.SetHuntPlayer(true);
            __instance.Alert();
            if (!targetMatches)
            {
                Player combatant = FindCombatant(combatantPlayerId);
                TargetCreatureField?.SetValue(__instance, null);
                if (combatant != null)
                {
                    // Direct assignment primes last-known position and clears
                    // any static target before the first UpdateAI body runs.
                    SetTargetMethod?.Invoke(__instance, new object[] { combatant });
                }
                // Keep the vanilla search primed as a same-frame fallback if
                // the replicated Player object was not available yet.
                UpdateTargetTimerField?.SetValue(__instance, 0f);
            }
        }

        private static Player FindCombatant(long playerId)
        {
            IList<Player> players = Player.GetAllPlayers();
            for (int index = 0; index < players.Count; index++)
            {
                Player player = players[index];
                if (player != null && player.GetPlayerID() == playerId)
                {
                    return player;
                }
            }
            return null;
        }
    }

    [HarmonyPatch(typeof(CharacterDrop), nameof(CharacterDrop.GenerateDropList))]
    internal static class ArenaEnemyDropListPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(CharacterDrop __instance, ref List<KeyValuePair<GameObject, int>> __result)
        {
            Character character = __instance == null ? null : __instance.GetComponent<Character>();
            if (character == null || string.IsNullOrEmpty(ArenaRuleContext.GetEnemyArenaId(character)))
            {
                return true;
            }

            __result = new List<KeyValuePair<GameObject, int>>();
            return false;
        }
    }

    [HarmonyPatch(typeof(Character), "OnDeath")]
    internal static class ArenaEnemyDeathPatch
    {
        [HarmonyPrefix]
        private static void Prefix(Character __instance)
        {
            if (__instance != null && !string.IsNullOrEmpty(ArenaRuleContext.GetEnemyArenaId(__instance)))
            {
                ArenaRuleContext.ReportArenaEnemyDeath(__instance);
            }
        }
    }

    [HarmonyPatch(typeof(Container), nameof(Container.Interact),
        new[] { typeof(Humanoid), typeof(bool), typeof(bool) })]
    internal static class ArenaContainerPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(Container __instance, ref bool __result)
        {
            if (__instance == null || !ArenaRuleContext.IsActiveProtectedPoint(__instance.transform.position))
            {
                return true;
            }

            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(Door), nameof(Door.Interact),
        new[] { typeof(Humanoid), typeof(bool), typeof(bool) })]
    internal static class ArenaDoorPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(Door __instance, ref bool __result)
        {
            if (__instance == null || !ArenaRuleContext.IsActiveProtectedPoint(__instance.transform.position))
            {
                return true;
            }

            __result = false;
            return false;
        }
    }
}
