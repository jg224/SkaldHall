# SkaldHall 0.0.1 Alpha publishing notes

## Package

- Name: `SkaldHall`
- Version: `0.0.1`
- Namespace/author: `jg224`
- Dependency string after upload: `jg224-SkaldHall-0.0.1`
- Status: Alpha testing

## Suggested Thunderstore categories

- Mods
- NPCs
- Server-side
- Client-side
- Enemies
- Utility

## Short description

Alpha server-authoritative NPC activities for Valheim. Build protected arenas now; quest-giver and additional Hall NPCs are planned.

## Compatibility note

The public name is SkaldHall, but the first package intentionally contains `ArenaGuard.dll` and retains the `jg224.arenaguard` plugin GUID. This preserves existing test-world arenas, configuration, state files, RPC identity, prefab names, and administrator commands.

Early testers must replace the previous `ArenaGuard.dll` rather than keep two copies installed.
