using System;
using System.Reflection;
using HarmonyLib;

namespace ArenaGuard.Rules
{
    [HarmonyPatch(typeof(Player), "GetJogSpeedFactor")]
    [HarmonyAfter(SpeedyPathsCompatibility.PluginGuid)]
    internal static class ArenaJogSpeedFactorPatch
    {
        [HarmonyPostfix]
        [HarmonyPriority(Priority.First)]
        private static void Postfix(Player __instance, ref float __result)
        {
            if (__instance == null)
            {
                return;
            }

            __result = ArenaMovementSpeedPolicy.NormalizeJog(
                ArenaRuleContext.IsProtectedPoint(__instance.transform.position),
                __result,
                __instance.GetEquipmentMovementModifier());
        }
    }

    [HarmonyPatch(typeof(Player), "GetRunSpeedFactor")]
    [HarmonyAfter(SpeedyPathsCompatibility.PluginGuid)]
    internal static class ArenaRunSpeedFactorPatch
    {
        [HarmonyPostfix]
        [HarmonyPriority(Priority.First)]
        private static void Postfix(Player __instance, ref float __result)
        {
            if (__instance == null)
            {
                return;
            }

            __result = ArenaMovementSpeedPolicy.NormalizeRun(
                ArenaRuleContext.IsProtectedPoint(__instance.transform.position),
                __result,
                __instance.GetSkills().GetSkillFactor(Skills.SkillType.Run),
                __instance.GetEquipmentMovementModifier());
        }
    }

    /// <summary>
    /// Optional, reflection-only integration with Speedy Paths. Normalizing the
    /// final Player factors enforces the rule regardless; this source hook also
    /// keeps Speedy Paths from displaying an inactive path bonus in the arena.
    /// </summary>
    internal static class SpeedyPathsCompatibility
    {
        internal const string PluginGuid = "nex.SpeedyPaths";
        private const string ClientTypeName = "SpeedyPaths.SpeedyPathsClientMod";
        private const string ModifierMethodName = "GetSpeedyPathModifier";

        internal static void TryInstall(Harmony harmony)
        {
            if (harmony == null)
            {
                throw new ArgumentNullException(nameof(harmony));
            }

            Type clientType = AccessTools.TypeByName(ClientTypeName);
            MethodInfo modifier = clientType == null
                ? null
                : AccessTools.Method(clientType, ModifierMethodName, new[] { typeof(Player) });
            MethodInfo postfix = AccessTools.Method(
                typeof(SpeedyPathsCompatibility),
                nameof(NeutralizeSpeedModifier));
            if (modifier == null || postfix == null)
            {
                return;
            }

            harmony.Patch(modifier, postfix: new HarmonyMethod(postfix)
            {
                priority = Priority.First
            });
            Plugin.Log?.LogInfo(
                "Speedy Paths compatibility enabled: path speed bonuses are suppressed inside arenas.");
        }

        private static void NeutralizeSpeedModifier(Player __0, ref float __result)
        {
            if (__0 != null && ArenaRuleContext.IsProtectedPoint(__0.transform.position))
            {
                __result = 1f;
            }
        }
    }
}
