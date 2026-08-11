# SkaldHall

SkaldHall is a server-authoritative framework for NPC-led activities in Valheim. Version 0.0.1 is an alpha release of its administrator-built challenge arenas; a quest-giver NPC and additional Hall activity roles are planned for future releases. Any number of named entrance gates can lead to one arena hub. One player fights at a time while nearby players watch as non-interacting ghost spectators.

Arena structures cannot take damage. Arena combat grants no loot or skill experience, consumes no ammunition, and causes no equipment durability loss. Before teleporting, each combatant chooses exactly three foods their character has discovered. SkaldHall applies that temporary loadout directly, fills health/stamina/eitr, and supplies a challenge-long Rested effect without creating or consuming inventory items. The player's original foods and Rested duration are restored when the run ends. Lethal damage ends the challenge without a player death, clears negative status effects, restores full health, and returns the player three metres in front of the Arena Master.

## Requirements

- BepInExPack for Valheim 5.4.2333
- Jötunn 2.29.2
- SkaldHall 0.0.1 on the server and every client

SkaldHall uses Jötunn's required-client compatibility check. Keep the same SkaldHall patch version on the server and clients.

## Installation

Install the package in the shared r2modman profile and on the dedicated server, or copy `ArenaGuard.dll` to `BepInEx/plugins/SkaldHall/` in each installation. Restart the server and every game client after replacing the DLL. The internal DLL name and `jg224.arenaguard` plugin identity are intentionally retained so existing ArenaGuard test worlds continue working; never load both an old and new copy of the DLL.

The server persists arena definitions, gates, queues, interrupted sessions, player return routes, and leaderboards per world under:

```text
BepInEx/config/jg224.arenaguard.world-<worldUID>.json
```

Writes use a temporary file and atomic replacement. Before each world-state load, SkaldHall also makes a timestamped `.startup-*.bak` copy beside the state file and retains the newest five. Do not hand-edit these files while the world is running.

## Administrator setup

SkaldHall recognizes accounts authenticated by the server's normal administrator list. An authenticated admin automatically receives one **Arena Admin Hammer** after joining. The hammer has no recipe, is removed when authorization ends, and exposes its own Arena category without adding pieces to the ordinary Hammer. If it cannot be granted, make one inventory space.

To build the first arena:

1. Place an **Arena Core** near the center of the intended arena.
2. Use the core to open the admin panel. Give the arena a unique name and set its combat and protected radii. Defaults are 20 and 30 metres. Admins see the combat boundary as a cyan-blue ring and the protected boundary as a gold ring.
3. Close the core panel and place the named **Arena Master** Dvergr rogue in the waiting area, outside the combat radius. His exact location automatically becomes the staging position.
4. Place one cyan **Combat Start** beacon inside the blue combat-radius ring and four red **Enemy Spawn** beacons around the combat floor. Out-of-bounds Combat Start placement is rejected by both client and server. Enemy beacons number themselves 1 through 4. Gates are optional travel infrastructure and are not required for the challenge.
5. Place one or more **Arena Gates** elsewhere in the world. Alternate-use a gate to assign its unique name, target the selected arena, and optionally designate it as the fallback entrance. Only one fallback should be configured for an arena.
6. Enable the arena from the core's admin panel after all required positions are present.

The Core panel has independent admin switches for terrain editing, building/demolition, and dropped-item pickup. These are server-wide, saved to the server config, and never apply while the admin is the active combatant. Terrain editing additionally requires that admin to have devcommands enabled locally. Non-admin arena participants cannot pick up items. Everyone may use doors and containers while no challenge is active. Arena structures never need repair because direct hits, support/weather damage, and damage-driven resource refunds are rejected within the protected radius. Rocks, trees, and logs are protected through their separate damage handlers so arena combat cannot produce stray stone or wood. Intentional admin demolition remains allowed and returns its normal materials.

The Arena Core, boundary rings, and position beacons are visible/selectable only to authenticated admins. Admins can independently hide or show all of these setup visuals from the Core panel or with the configurable F7 shortcut; this visibility preference is local, and non-admins never see them regardless of that setting. Position beacons and rings have no solid collider and cannot obstruct players. Remove a beacon with the Arena Admin Hammer; its saved position is cleared, the arena is disabled for safety, and the next Enemy Spawn placed reuses the missing number. Re-enable the arena at its Core after the layout is complete. The Arena Master is public, stationary, persistent, invulnerable, untargetable, and non-solid. The former Challenge Sign, Staging Position, and Hub Gate Position pieces are hidden for new builds but remain registered so existing worlds load safely.

On login or reconnect, SkaldHall accepts Jötunn's server-synchronized administrator status in addition to Valheim's host/admin list and immediately refreshes setup visuals when that status arrives. Recovered queue sessions do not hide admin setup objects. If F7 is pressed before authorization finishes synchronizing, SkaldHall displays an explicit waiting message instead of doing nothing.

The Arena Core clone removes the vanilla ward's inherited `PrivateArea` and `EffectArea` components before removing its colliders. This prevents Valheim's placement preview from running `EffectArea.Awake` without the same-object collider it requires, so equipping the Arena Admin Hammer cannot abort the setup-piece menu. Existing ArenaGuard test pieces and saved arena positions are unchanged.

The server randomizes enemies across all four spawn beacons using shuffled sets. Every beacon is used once before the set reshuffles, consecutive sets do not immediately reuse the last beacon, and multi-enemy encounters spread enemies across distinct beacons until all four have been used. An escaped enemy is moved to a newly randomized beacon and immediately re-aggroed, preventing players from camping Spawn 1.

## Player flow

The compact challenge screen uses Valheim's default wood panels, fonts, buttons, and trophy sprites. Choose a mode card, select Gauntlet or Biome scope for either ladder, and configure custom stars/quantity directly. Choosing Biome opens a right-side picker listing Black Forest through Ashlands. Choosing Specific Monster immediately opens a side-by-side illustrated creature browser organized by biome; changing tabs or selections preserves the panels' positions.

1. Walk into any named Arena Gate. It obeys the normal world portal locks, active-boss portal restriction, and inventory teleportability rules.
2. The gate records that exact entrance before sending the player to the arena hub. The return gate later sends them back to it; if it was removed, the arena's fallback entrance is used.
3. Talk to the Arena Master, choose a mode and scope, and join the FIFO queue. Gauntlet uses every configured biome. Biome adds a button that cycles through the available biomes and uses every eligible mob from the selected biome.
4. When called, accept within 30 seconds. If a call expires and the request returns to the back of the queue, its next server call has a new identity and always opens a fresh acceptance prompt; a temporarily unavailable UI retries instead of suppressing that prompt. A preparation screen offers only valid foods that this character has previously discovered. Three simultaneous, independently scrollable Health, Stamina, and Eitr lists use CraftIndex-compatible grouping and strongest-first ordering. Each compact row shows the food icon plus color-coded HP, STAM, and EITR values. Choose exactly three distinct foods total across all lists and confirm within 60 seconds; SkaldHall remembers the last selection for that character when those foods remain eligible.
5. Confirmation snapshots the original foods, Rested duration, resources, ammunition, and durability. The selected foods are applied directly without touching inventory, Rested lasts for the challenge, and health/stamina/eitr are filled to their new maximums. Only then is the combatant moved to the exact Combat Start beacon with Valheim's short non-distant relocation.
6. Each opponent has a three-second visible countdown. The smooth HUD clock uses `H:MM:SS`, records fighting time only, and pauses during food preparation, encounter countdowns, intermissions, and results. If the combatant crosses the combat radius, a center-screen five-second countdown appears. They are not moved during that grace period; returning inside clears the warning and continues the fight. Remaining outside forfeits the challenge after five seconds and returns the combatant to the Arena Master. Spectators remain free to cross, and the combatant can also forfeit from the HUD.
7. Victory results remain visible for five seconds before the player's original foods, Rested time, and protected resources are restored and the player is moved three metres in front of the Arena Master, facing the same direction, using the same short non-distant relocation. Defeat, forfeit, timeout, disconnect, and restart recovery use the same restoration rule. Gates are never used as challenge-completion destinations, and world-gate travel retains the normal portal animation.

During a challenge, spectators retain collision with terrain, floors, walls, and doors, but not with the combatant, arena enemies, or arena projectiles. Ordinary spectators cannot interact with objects or influence the fight. Authenticated admins retain the narrow setup exceptions described above while they are not the combatant.

## Challenge modes

- **Biome ladder:** one base-level encounter for every eligible creature in the selected scope, ordered weakest to strongest with each biome's miniboss last.
- **Star ladder:** the same scoped ordering, but each creature is fought base, one-star, and two-star back-to-back.
- **Specific Monster:** choose one curated creature, its supported star level, and a quantity from 1 through 10. The challenge ends when that group is defeated.

**Gauntlet** includes the complete configured land-biome roster from Black Forest through Ashlands. **Biome** opens a right-side picker and includes every eligible mob from the chosen biome. Both scopes work with Biome Ladder and Star Ladder. Meadows, Greyling, Ocean, bosses, passive wildlife, automatic modded-creature discovery, and unsupported scripted creatures are currently excluded. Dvergr are included and become hostile only to the active combatant. The only miniboss encounters are Brenna, Geirrhafa, Zil & Thungr, and Lord Reto; each appears last in its biome, and Zil & Thungr spawn as a tracked duo.

Modes 1 and 2 announce starts and outcomes globally. Mode 3 remains arena-local. The leaderboard screen shows Biome Ladder and Star Ladder together for the selected Gauntlet or Biome scope, with separate boards for each biome and each current-roster result's recorded UTC date and time. Completed runs rank by fastest fighting time; incomplete runs rank by furthest stage and elapsed fighting time. There are no material rewards.

## Consequence-free combat

While actively fighting:

- ammunition does not decrement;
- equipped items do not lose durability;
- personal food use is locked because the temporary arena food loadout is already active;
- health, stamina, and eitr recovery consumables remain allowed and are restored afterward;
- other consumables, dropping, picking up, crafting, building, and transferring items are blocked;
- arena enemies drop nothing and grant no skill experience;
- lethal damage becomes arena defeat instead of death.

If the server restarts during a run, SkaldHall restores the saved resources, returns the combatant in front of the Arena Master, keeps them first in queue, and restarts the selected challenge from encounter one after they reconnect.

## Live-server recovery commands

Authenticated admins can use these commands from the in-game console; the dedicated-server console can use them directly:

```text
arenaguard status [arena name or ID]
arenaguard abort <arena name or ID|all>
arenaguard clearqueue <arena name or ID|all>
```

`status` reports active challenges, queues, tracked enemies, and pending resource restores. `abort` safely despawns the run, restores the combatant, and clears that arena's queue. `clearqueue` removes waiting players without stopping an active challenge. Arena names containing spaces are accepted.

## Configuration

Server settings are written to `BepInEx/config/jg224.arenaguard.cfg`:

| Section | Setting | Default | Meaning |
|---|---|---:|---|
| Arena | `DefaultCombatRadius` | `20` | Default combat-floor radius in metres. |
| Arena | `DefaultProtectedRadius` | `30` | Default protected radius in metres; must exceed combat radius. |
| Admin | `ShowSetupVisuals` | `true` | Local admin preference for Core, radius-ring, and position-beacon visibility; ignored for non-admins. |
| Admin | `ToggleSetupVisualsShortcut` | `F7` | Local admin shortcut for toggling all setup visuals. |
| Admin | `AllowTerrainEditing` | `true` | Server-wide admin terrain permission; also requires local devcommands. |
| Admin | `AllowBuilding` | `true` | Server-wide admin build and demolition permission. |
| Admin | `AllowDroppedItemPickup` | `true` | Server-wide admin manual and automatic dropped-item pickup permission. |
| Timing | `QueueAcceptSeconds` | `30` | Time to accept a queue call. |
| Timing | `StagingTimeoutSeconds` | `60` | Time to confirm three discovered foods after accepting a queue call. |
| Timing | `EncounterCountdownSeconds` | `3` | Preparation countdown before every encounter. |
| Timing | `BoundaryGraceSeconds` | `5` | Time outside the floor before forfeiture. |
| Timing | `ResultsSeconds` | `5` | Results display time before staging return. |
| Diagnostics | `VerboseLogging` | `false` | Additional diagnostic logging. |

The three gameplay permission settings are changed from any Arena Core and saved by the server. Editing those entries in a client-only config has no effect on a remote server. Arena-specific names, radii, markers, gate assignments, and enabled state are managed in game and stored in the world file.

## Build and verification

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\verify.ps1
```

The verification entry point builds the release DLL, runs pure rule and session tests, checks patched members against the installed Valheim and Jötunn assemblies, validates metadata, and inspects package contents. Automated checks do not replace a real multiplayer smoke test for portal travel, spectator collision, AI focus, and defeat interception.

Routine verification never launches `valheim_server.exe`, so it cannot trigger a Windows Firewall prompt. The optional isolated live-server harness is deliberately gated behind `smoke-server.ps1 -AllowNetworkLaunch`; do not use that switch unless an interactive network smoke is explicitly wanted.
