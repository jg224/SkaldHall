# SkaldHall 0.0.3 publishing notes

## Package

- Name: `SkaldHall`
- Version: `0.0.3`
- Namespace/author: `jg224`
- Dependency string after upload: `jg224-SkaldHall-0.0.3`

## Suggested Thunderstore categories

- Mods
- NPCs
- Server-side
- Client-side
- Enemies
- Utility

## Short description

Server-authoritative NPC activities for Valheim. Build protected arenas now; quest-giver and additional Hall NPCs are planned.

## Compatibility note

SkaldHall 0.0.3 contains `SkaldHall.dll` while retaining the `jg224.arenaguard` plugin GUID. This preserves existing test-world arenas, configuration, state files, RPC identity, prefab names, and administrator commands.

Early testers must delete the previous `ArenaGuard.dll` rather than keep both filenames installed.
