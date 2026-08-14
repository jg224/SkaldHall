using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ArenaGuard.World
{
    /// <summary>
    /// Valheim skips SetupPlacementGhost when the player clicks the piece-table
    /// slot which is already selected. A newly equipped custom hammer can have
    /// slot zero selected before its first ghost exists, forcing the player to
    /// select another piece and return. Repair that exact missing-preview case.
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.SetSelectedPiece), new[] { typeof(Vector2Int) })]
    internal static class ArenaCorePlacementPreviewPatch
    {
        private static readonly MethodInfo SetupPlacementGhost =
            AccessTools.Method(typeof(Player), "SetupPlacementGhost");

        [HarmonyPostfix]
        private static void Postfix(Player __instance, GameObject ___m_placementGhost)
        {
            if (__instance == null || __instance != Player.m_localPlayer ||
                ___m_placementGhost != null || !__instance.InPlaceMode())
            {
                return;
            }

            Piece selected = __instance.GetSelectedPiece();
            if (selected == null || selected.gameObject == null ||
                !selected.gameObject.name.StartsWith(ArenaWorldObjects.CorePrefabName, StringComparison.Ordinal))
            {
                return;
            }

            if (SetupPlacementGhost == null)
            {
                Plugin.Log?.LogError("Valheim's SetupPlacementGhost method is unavailable; the Arena Core preview cannot be repaired.");
                return;
            }

            SetupPlacementGhost.Invoke(__instance, null);
        }
    }
}
