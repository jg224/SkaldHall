using BepInEx.Configuration;
using UnityEngine;

namespace ArenaGuard.Config
{
    internal static class ArenaConfig
    {
        internal static ConfigEntry<float> DefaultCombatRadius { get; private set; }
        internal static ConfigEntry<float> DefaultProtectedRadius { get; private set; }
        internal static ConfigEntry<int> QueueAcceptSeconds { get; private set; }
        internal static ConfigEntry<int> StagingTimeoutSeconds { get; private set; }
        internal static ConfigEntry<int> EncounterCountdownSeconds { get; private set; }
        internal static ConfigEntry<int> BoundaryGraceSeconds { get; private set; }
        internal static ConfigEntry<int> ResultsSeconds { get; private set; }
        internal static ConfigEntry<bool> ShowAdminSetupVisuals { get; private set; }
        internal static ConfigEntry<KeyboardShortcut> ToggleAdminSetupVisualsShortcut { get; private set; }
        internal static ConfigEntry<bool> AllowAdminTerrainEditing { get; private set; }
        internal static ConfigEntry<bool> AllowAdminBuilding { get; private set; }
        internal static ConfigEntry<bool> AllowAdminDroppedItemPickup { get; private set; }
        internal static ConfigEntry<bool> VerboseLogging { get; private set; }

        private static ConfigFile _config;

        internal static void Bind(ConfigFile config)
        {
            _config = config;
            DefaultCombatRadius = config.Bind(
                "Arena",
                "DefaultCombatRadius",
                20f,
                "Default horizontal combat-floor radius, in metres.");
            DefaultProtectedRadius = config.Bind(
                "Arena",
                "DefaultProtectedRadius",
                30f,
                "Default horizontal structure-protection radius, in metres.");
            QueueAcceptSeconds = config.Bind(
                "Timing",
                "QueueAcceptSeconds",
                30,
                "Seconds the next queued player has to accept their turn.");
            StagingTimeoutSeconds = config.Bind(
                "Timing",
                "StagingTimeoutSeconds",
                60,
                "Seconds an accepted player has to enter the combat floor.");
            EncounterCountdownSeconds = config.Bind(
                "Timing",
                "EncounterCountdownSeconds",
                3,
                "Countdown before each encounter spawns.");
            BoundaryGraceSeconds = config.Bind(
                "Timing",
                "BoundaryGraceSeconds",
                5,
                "Seconds a combatant may remain outside the combat floor before forfeiting.");
            ResultsSeconds = config.Bind(
                "Timing",
                "ResultsSeconds",
                5,
                "Seconds to show results before returning the player to staging.");
            ShowAdminSetupVisuals = config.Bind(
                "Admin",
                "ShowSetupVisuals",
                true,
                "Show Arena Cores, radius rings, Combat Start, and Enemy Spawn beacons to this admin client. Non-admins never see them.");
            ToggleAdminSetupVisualsShortcut = config.Bind(
                "Admin",
                "ToggleSetupVisualsShortcut",
                new KeyboardShortcut(KeyCode.F7),
                "Local shortcut used by an authenticated admin to show or hide SkaldHall setup visuals.");
            AllowAdminTerrainEditing = config.Bind(
                "Admin",
                "AllowTerrainEditing",
                true,
                "Server-wide: allow authenticated non-combatant admins with devcommands enabled to modify terrain inside protected arenas.");
            AllowAdminBuilding = config.Bind(
                "Admin",
                "AllowBuilding",
                true,
                "Server-wide: allow authenticated non-combatant admins to build and demolish inside protected arenas.");
            AllowAdminDroppedItemPickup = config.Bind(
                "Admin",
                "AllowDroppedItemPickup",
                true,
                "Server-wide: allow authenticated non-combatant admins to manually or automatically pick up dropped items inside arenas.");
            VerboseLogging = config.Bind(
                "Diagnostics",
                "VerboseLogging",
                false,
                "Write additional SkaldHall diagnostic messages.");
        }

        internal static void Save()
        {
            _config?.Save();
        }
    }
}
