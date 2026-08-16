# SkaldHall

SkaldHall is a server-authoritative framework for NPC-led activities in Valheim. Its first release lets server administrators build protected challenge arenas where one player fights while everyone nearby watches as a harmless ghost spectator.

## Available now: Arena Challenges

- **Biome Ladder:** Fight every eligible monster in one selected biome or across the full Gauntlet, weakest to strongest.
- **Star Ladder:** Fight the same scoped roster at base, one-star, and two-star difficulty.
- **Specific Monster:** Choose an eligible creature, star level, and quantity from 1 through 10.
- **Admin-built arenas:** Configure an Arena Core, Arena Master, combat radius, protected radius, Combat Start, and four randomized Enemy Spawns.
- **Immediate enemy pressure:** Arena enemies receive the authenticated combatant as a direct target before their first AI update, skip Valheim's randomized initial delay, and remain target-locked throughout the encounter.
- **Safe spectators:** Non-combatants inside the arena become non-targetable, non-interacting ghost spectators.
- **Protected competition:** Arena combat causes no player death, loot, skill experience, ammunition use, equipment durability loss, or combat damage to protected structures and world objects.
- **Normalized movement:** Speedy Paths and other general speed bonuses are suppressed inside arenas while Run skill, equipment modifiers, and trinket bonuses remain active.
- **Temporary food loadouts:** Choose exactly three foods the character has discovered. SkaldHall fills health, stamina, and eitr, preserves Rested for the challenge, and restores the original player state afterward.
- **Live-server support:** FIFO queues, acceptance timeouts, boundary warnings, forfeiture, server-restart recovery, administrator recovery commands, and persistent leaderboards.
- **Hall of Champions:** Admins can place a grand one-sided, non-solid wood leaderboard showing every biome and Gauntlet. Each section displays completed Biome Ladder and Star Ladder top-five times and refreshes live when records change.

Arena challenges currently provide leaderboard records only. There are no material rewards.

## How an Arena challenge works

1. Talk to the named **Arena Master** and choose a ladder or Specific Monster encounter.
2. Join the server-wide queue and accept the call within 30 seconds.
3. Choose exactly three eligible discovered foods during preparation.
4. SkaldHall moves the combatant to the configured Combat Start and spawns each encounter across randomized beacons.
5. Victory, defeat, forfeit, timeout, disconnect, and restart recovery restore the player's original state and return them in front of the Arena Master.

Crossing the combat boundary starts a visible five-second warning. Returning inside clears it; remaining outside forfeits the run. Spectators may cross freely.

## Administrator setup

Authenticated server administrators automatically receive an **Arena Admin Hammer**.

1. Place an **Arena Core**, set a unique name and radii, and save it.
2. Place an **Arena Master** Dvergr in the waiting area. His position becomes staging.
3. Place one cyan **Combat Start** inside the combat ring.
4. Place exactly four red **Enemy Spawn** beacons around the combat floor.
5. Enable the arena from its Core.

Optionally place one or more **Hall of Champions** boards with the Arena Admin Hammer. Each four-meter-tall board presents Black Forest through Ashlands in order, with Gauntlet as the final section. Players can read every compact live top-five column directly in the world.

Setup objects, boundary rings, and spawn beacons are visible only to authenticated administrators and can be toggled with F7. Admin permission switches separately control terrain editing, construction/demolition, and dropped-item pickup.

## Planned development

SkaldHall is intended to grow beyond arenas:

- a dedicated **quest-giver NPC** with server-managed objectives and persistent quest progression;
- additional activity NPCs and Hall roles;
- a separately designed named hub-travel system with player-buildable entrances and exact return routing;
- contracts, special events, and other structured multiplayer activities.

These systems are planned and are **not included in 0.0.5**.

## License

MIT
