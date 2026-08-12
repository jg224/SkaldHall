# Changelog

## 0.0.2 Alpha - 2026-08-11

- Fix prepared combatants remaining at the Arena Master until staging timed out when the one-way Combat Start move packet was lost. The client now retries the exact server-supplied marker, while the server still verifies arrival before countdown.
- Resolve dedicated-server combatants through their authenticated peer character ZDO when no local `Player` component exists, fixing the false “staged combatant is unavailable” rejection and keeping boundary popups and combat tracking functional.
- Rename the packaged assembly to `SkaldHall.dll`; remove the old `ArenaGuard.dll` when upgrading to avoid loading the same plugin twice.
- Fix dedicated-server administrator recognition for protected arena building, demolition, pickup, and setup interaction.
- Replace the raw `$arenaguard_admin_build_only` token with its intended localized message.
- Fix the first Arena Core placement preview appearing invisible until another hammer piece was selected.
- Stop rendering or updating the Arena Core's inherited flashing light while admin setup visuals are hidden.
- Keep protected arenas free of naturally spawned or roaming passive wildlife and birds without affecting tamed animals or arena actors.
- Suppress Speedy Paths and other general movement-speed bonuses inside protected arenas.
- Preserve Valheim's Run skill and equipped-item modifiers, including trinket bonuses and armor penalties.
- Keep Speedy Paths behavior unchanged outside arenas and hide its inactive arena speed status.

## 0.0.1 Alpha - 2026-08-11

- Publish the first SkaldHall alpha for live Arena testing.
- Add administrator-built protected arenas with Arena Cores, an Arena Master, configurable boundaries, one Combat Start, and four randomized Enemy Spawns.
- Add Biome Ladder, Star Ladder, and Specific Monster challenges using the curated Black Forest-through-Ashlands roster.
- Add discovered-food preparation, challenge-long Rested, protected resources, and restoration on every terminal path.
- Add one-combatant FIFO queues, queue-call recovery, spectators, boundary warnings, safe defeat interception, restart recovery, and leaderboards.
- Add server-controlled administrator permissions, hidden setup visuals, persistent world state, startup backups, and recovery commands.
- Preserve the `jg224.arenaguard` plugin identity, data files, console commands, prefab names, and `ArenaGuard.dll` filename so existing alpha-test arenas continue working after the SkaldHall rebrand.
- Mark quest-giver NPCs, persistent quests, and additional Hall activity roles as planned future development.
