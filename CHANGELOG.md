# Changelog

## 0.0.1 Alpha - 2026-08-11

- Rebrand the first public alpha as SkaldHall while retaining the `jg224.arenaguard` plugin identity, state files, prefabs, commands, and DLL name for test-world compatibility.
- Publish the Arena system for alpha testing and document planned quest-giver and additional Hall NPC systems.
- Reset the public Thunderstore version sequence to 0.0.1; the 0.1.x entries below are internal ArenaGuard development builds and were not public SkaldHall releases.

### Pre-release ArenaGuard development history

## 0.1.50 - 2026-08-11

- Increase Health, Stamina, and Eitr food-list mouse-wheel sensitivity from 120 to 300 UI points per wheel step.

## 0.1.49 - 2026-08-11

- Remove every inherited vanilla ward `EffectArea` from the Arena Core clone before removing its colliders, preventing `EffectArea.Awake` from crashing placement-preview creation.
- Validate that Arena Core prefabs retain neither `PrivateArea` nor `EffectArea` behavior while preserving the single trigger used for admin interaction.
- Preserve all existing arena pieces, saved positions, and world data; this release changes prefab cleanup only.

## 0.1.48 - 2026-08-11

- Preserve admin-only Core, radius-ring, and position-beacon visibility after login, reconnect, or recovered queue sessions by accepting Jötunn's server-synchronized administrator status.
- Refresh every live setup visual and force an Arena Admin Hammer audit as soon as synchronized administrator status changes.
- Make F7 explain when server administrator synchronization is still pending instead of silently ignoring the shortcut.

## 0.1.47 - 2026-08-11

- Identify queue acceptance prompts by the server-created session instead of only by arena ID.
- Reopen the prompt when a timed-out request receives a replacement call, while suppressing duplicate snapshots for the same call.
- Record a prompt as displayed only after its UI is successfully created, allowing temporary UI-unavailable states to retry.
- Advance the ArenaGuard RPC protocol to version 8 for queue-call session identity.

## 0.1.46 - 2026-08-11

- Increase Health, Stamina, and Eitr food-list mouse-wheel sensitivity from 30 to 120 UI points per wheel step.

## 0.1.45 - 2026-08-11

- Display live HUD fight time as `H:MM:SS` without milliseconds.
- Use the same duration format for leaderboard entries, including runs longer than 24 hours.

## 0.1.44 - 2026-08-11

- Block Valheim's direct `WearNTear.ApplyDamage` path so support, weather, and other owner-side damage cannot bypass arena structure protection.
- Protect destructibles, rocks, mine-rock sections, trees, and logs through their authoritative damage handlers, preventing combat-generated wood and stone inside the protected radius.
- Suppress damage-driven structure resource refunds as a final safeguard while preserving normal resource returns from intentional admin demolition.

## 0.1.43 - 2026-08-11

- Randomize enemy placement across all four configured spawn beacons instead of restarting at Spawn 1 for every encounter.
- Use shuffled spawn sets so every beacon is used before reshuffling, consecutive sets cannot immediately repeat the last beacon, and multi-enemy encounters are distributed.
- Move escaped enemies to a newly randomized beacon and immediately re-aggro them on the combatant.

## 0.1.42 - 2026-08-11

- Replace immediate combat-boundary correction with a real five-second grace period.
- Show a center-screen countdown once per second, clear it when the combatant returns, and forfeit only if they remain outside through the deadline.
- Keep spectators unrestricted and return a boundary-forfeiting combatant to the Arena Master through the normal restoration path.

## 0.1.41 - 2026-08-11

- Keep the combatant's arena Rested/comfort effect non-expiring for the entire challenge instead of allowing Valheim's comfort-duration reset to restore a countdown.
- Continue restoring the player's exact pre-arena Rested duration when the challenge ends.

## 0.1.40 - 2026-08-11

- Replace the single active food tab and 3x3 card pages with simultaneous Health, Stamina, and Eitr scroll lists modeled after CraftIndex's compact recipe rows.
- Show each discovered food's in-game icon and color-coded HP, STAM, and EITR values while retaining strongest-first category ordering.
- Preserve each column's scroll position when selecting or removing foods and keep one numbered three-food loadout shared across all lists.

## 0.1.39 - 2026-08-11

- Rename the Selected challenge-mode card to Specific Monster without changing its custom encounter behavior.

## 0.1.38 - 2026-08-11

- Replace the cycling Biome button with a persistent right-side picker listing Black Forest through Ashlands.
- Organize discovered arena foods into Health, Stamina, and Eitr tabs using CraftIndex-compatible stat classification and strongest-first ordering.
- Keep one shared, ordered three-food selection across every food tab and show each chosen food's slot number on its card.
- Route near-even balanced foods to their slightly stronger Health or Stamina tab so every eligible food appears exactly once without adding a fourth tab.

## 0.1.37 - 2026-08-11

- Add the ArenaGuard package icon as a transparent cutout with no black side panels.
- Add a 60-second pre-combat food picker requiring exactly three distinct foods previously discovered by that character.
- Apply the selected foods directly without creating or consuming inventory items, fill health/stamina/eitr, and grant Rested for the full challenge.
- Remember each character's last eligible arena-food loadout and preselect it on the next run.
- Lock personal food use only for the active combatant while continuing to allow health, stamina, and eitr recovery consumables; queued players and spectators may eat normally.
- Restore the exact pre-challenge foods, remaining Rested duration, and protected resources after victory, defeat, forfeit, timeout, disconnect, or server-restart recovery.
- Make accepted resource snapshots immutable, restore prepared players on staging timeout, migrate persistence to schema 3, and advance the exact ArenaGuard network protocol to version 7.

## 0.1.36 - 2026-08-11

- Open the creature selection panel immediately when the player chooses Selected Encounter.
- Preserve the main challenge panel position when switching between ladder modes while the creature picker is already closed.

## 0.1.35 - 2026-08-11

- Replace World Unlocked/Maximum Possible progression with two ladder scopes: Gauntlet (every configured biome) and Biome (every eligible mob in one selected biome).
- Apply both scopes to Biome Ladder and Star Ladder, including base → 1-star → 2-star expansion within a selected biome.
- Add a native biome-cycle button and scope-specific encounter summaries without reintroducing dropdown-driven challenge rows.
- Separate leaderboard records by Gauntlet or selected biome, bump the curated roster revision, and migrate schema-1 queues/sessions/records safely to Gauntlet scope.
- Bump the exact ArenaGuard network protocol for the selected-biome request and leaderboard fields.

## 0.1.34 - 2026-08-11

- Change the arena clock to accumulated fighting time, excluding initial preparation, intermission countdowns, and the victory-results delay.
- Interpolate the HUD timer locally at a throttled cadence between authoritative server snapshots so it advances smoothly.
- Use Valheim's short non-distant teleport mode for Combat Start, boundary correction, and Arena Master returns; world gates retain the normal portal animation.
- Label the victory hold as `Returning in` while preserving the configured results delay.

## 0.1.33 - 2026-08-11

- Add a combatant-only soft boundary that returns an escaping fighter to their last safe in-arena position while spectators remain free to cross the radius.
- Show a FoodGuard-style center-screen return warning and retain the five-second boundary forfeit as a fail-safe if correction cannot complete.
- Add authenticated `arenaguard status`, `arenaguard abort`, and `arenaguard clearqueue` recovery commands for local and dedicated-server administrators.
- Create and rotate five timestamped world-state backups at server startup, in addition to the existing atomic-save backup.

## 0.1.32 - 2026-08-11

- Fix the three server-wide admin permission buttons being silently ignored when the local Arena Core existed before its arena definition synchronized.
- Route permission changes independently of per-arena configuration and show an explicit rejection message if administrator identity or networking is unavailable.

## 0.1.31 - 2026-08-11

- Add independent Core-panel switches for admin terrain editing, building/demolition, and dropped-item pickup.
- Persist those gameplay permissions in the server config and synchronize them to clients through authoritative arena snapshots.
- Keep setup/spawn visibility as a separate local admin-only toggle; non-admins never see setup visuals.
- Require local devcommands for permitted admin terrain edits and deny every admin exception while actively fighting.

## 0.1.30 - 2026-08-10

- Show Biome Ladder and Star Ladder rankings together on one leaderboard screen for the selected progression cap.
- Add the recorded UTC date and time to every result and filter rankings to the current roster revision.
- Preserve the leaderboard panel position while its two server responses arrive.

## 0.1.29 - 2026-08-10

- Return completed, defeated, forfeiting, timed-out, and recovered combatants three metres in front of the live Arena Master instead of at its occupied center point.
- Face returned players in the Arena Master's direction and retain the saved staging position as a safe fallback when the Master is not loaded.

## 0.1.28 - 2026-08-10

- Restore Valheim's default generic wood-panel color instead of applying an ArenaGuard tint.
- Preserve the main and creature-picker panel positions when changing biome tabs or creature selections.

## 0.1.27 - 2026-08-10

- Remove Meadows and Greyling from the current roster so challenge progression begins in Black Forest.
- Replace generic miniboss flags with Brenna, Geirrhafa, Zil & Thungr, and Lord Reto; the Plains encounter spawns Zil and Thungr as one tracked duo.
- Redesign the challenge screens as compact, darker native panels with illustrated creature cards sourced from Valheim's trophy sprites.

## 0.1.26 - 2026-08-10

- Cache Arena Master, enemy-arena, and active-combatant identity so the global death and AI hostility patches avoid repeated hierarchy and ZDO lookups.
- Throttle admin-hammer and combat-start-arrival polling and reuse cleanup buffers instead of allocating collections every frame.
- Reuse material property blocks for arena boundaries, position beacons, and gates; gate proximity checks now run at 5 Hz with squared distance.

## 0.1.25 - 2026-08-10

- Redesign the challenge menu with Valheim-native wood panels, large selectable mode cards, progression buttons, compact star/quantity controls, and a clear encounter summary.
- Add a side-by-side creature picker with biome tabs, a four-column curated creature grid, miniboss labels, selection highlighting, and Escape/cancel cleanup.
- Preserve the existing server-authoritative challenge and leaderboard request contracts while removing dropdown-driven challenge setup.

## 0.1.24 - 2026-08-10

- Add a persistent local admin toggle for Arena Cores, boundary rings, Combat Start, and Enemy Spawn setup visuals.
- Expose the toggle in the Arena Core panel and through a configurable F7 shortcut so hidden Cores can always be restored.
- Keep setup visuals fail-closed for non-admin clients regardless of their local configuration value.

## 0.1.23 - 2026-08-10

- Show non-solid cyan-blue combat and gold protected-radius ground rings to authenticated admins only.
- Reject Combat Start placement outside the combat radius on the placing client and authoritative server.
- Refuse to start a legacy arena whose saved Combat Start is outside its combat radius.

## 0.1.22 - 2026-08-10

- Wake every initial and summoned arena enemy, force hunting/alert state, and immediately target the active combatant when it spawns.

## 0.1.21 - 2026-08-10

- Skip the Arena Master staging teleport when the accepted player is already standing beside the Master.
- Add an authenticated, payload-free Combat Start arrival acknowledgement so remote players can stand still after teleporting without waiting for a movement update or hitting the staging timeout.
- Mirror the exact server-commanded Combat Start position into the server player representation before starting the countdown.

## 0.1.20 - 2026-08-10

- Make creatures produced by an arena enemy's `SpawnAbility` inherit its arena and session identity, target only the combatant, drop no loot, and count as encounter enemies.
- Despawn tracked and network-tagged summons whenever the run ends, and immediately destroy delayed summons created after their session has closed.
- Reset untamed non-player creatures inside the protected arena at terminal cleanup while preserving players, tames, and the Arena Master.

## 0.1.19 - 2026-08-10

- Retry the Combat Start teleport when Valheim is still completing the Arena Master staging move.
- Keep the session in staging until the server confirms the combatant has arrived at Combat Start; countdown, enemy spawning, and boundary enforcement begin only after confirmation.

## 0.1.18 - 2026-08-10

- Apply the supported Valheim marker material to every cylinder mesh slot so the flat base caps render red or blue like their poles instead of purple.

## 0.1.17 - 2026-08-10

- Eliminate purple Enemy Spawn materials by reusing Valheim's supported sign material and applying red through a per-renderer property block.
- Reject and automatically remove a second Combat Start placement; preserve the authoritative marker when an old duplicate is dismantled.
- Replace the obsolete staging/combat/hub error with exact missing-position diagnostics and explicitly state that gates are optional.

## 0.1.16 - 2026-08-10

- Teleport an accepted combatant directly to the exact Combat Start beacon as soon as their resource snapshot is secured; no boundary walk is required.
- Render Enemy Spawn beacons with a known Valheim shader/material, white base texture, and pure red color/emission instead of Unity's purple fallback.
- Make gates optional travel infrastructure rather than required challenge configuration.
- Route optional entrance-gate arrivals to the Arena Master and keep every challenge outcome returning to the Arena Master rather than a gate.

## 0.1.15 - 2026-08-10

- Register Combat Start and Enemy Spawn before hidden legacy marker prefabs.
- Make marker-prefab validation diagnostic-only so it can never remove hammer options.
- Isolate hidden Staging and Hub Gate compatibility registration failures from the usable setup pieces.

## 0.1.14 - 2026-08-10

- Fix marker registration aborting on the hidden Staging compatibility prefab and leaving only four Arena Hammer entries.
- Remove the runtime TextMesh/material dependency from position beacons; use the three self-contained colored beacon meshes and numbered hover names.
- Report the exact failed marker-prefab invariant instead of one composite validation message.

## 0.1.13 - 2026-08-10

- Replace sign-derived position markers with purpose-built cyan Combat Start and red Enemy Spawn admin beacons.
- Show and select beacons only for authenticated admins; use a trigger-only selection volume so they never obstruct players.
- Number Enemy Spawn beacons 1 through 4 as they are placed and reject a fifth until one is removed.
- Clear the exact saved marker slot when its beacon is removed with the Arena Admin Hammer, persist incomplete layouts, and disable the arena until it is re-enabled.
- Use the physical Arena Return Gate as the hub arrival point and hide the redundant Hub Gate Position build option.
- Advance the strict ArenaGuard RPC protocol to version 2 for authoritative marker-removal messages.

## 0.1.12 - 2026-08-10

- Replace the wooden Challenge Sign workflow with an admin-placed named Dvergr rogue called Arena Master.
- Make the Arena Master stationary, non-solid, persistent, invulnerable, and untargetable.
- Open the existing challenge and leaderboard menu when any player talks to the Arena Master.
- Use the Arena Master's exact position as staging and hide the old Challenge Sign while retaining existing-world compatibility.
- Prevent routine smoke verification from launching a copied server executable or triggering repeated Windows Firewall prompts.

## 0.1.11 - 2026-08-10

- Restore the exact visible Valheim sign model and hierarchy used by ArenaGuard 0.1.5.
- Make Challenge Signs and position markers non-solid by converting their original colliders to triggers in place.
- Stop deleting collider objects or rewriting sign renderers, LOD groups, meshes, and active state.
- Add an isolated Valheim/Jötunn smoke test that validates the real sign renderers, materials, mesh bounds, and trigger colliders.

## 0.1.10 - 2026-08-10

- Restore the archived 0.1.5 rule and runtime behavior as the development baseline.
- Keep Challenge Signs and position-marker signs non-solid with one trigger-only selection collider.
- Make the Arena Challenge Sign's exact location the staging position.
- Hide the separate Staging Position build option while retaining its prefab for existing-world compatibility.

## 0.1.9 - 2026-08-10

- Roll back the admin-only position-marker rendering system completely.
- Restore the original vanilla sign source and keep Challenge Signs and position markers visible to every client.
- Keep sign-derived pieces non-solid and retain server-authenticated admin placement and demolition rules.

## 0.1.8 - 2026-08-10

- Fix invisible Challenge Signs and position markers by using Valheim's dedicated no-text sign model.
- Keep source-prefab renderers enabled during registration and apply admin-only marker visibility only to live instances.
- Restore marker visual objects and renderer state deterministically when a client gains or already has administrator status.
- Remove a parallel build race that could intermittently lock the ArenaGuard assembly during verification.

## 0.1.7 - 2026-08-10

- Show position markers only to authenticated admins while keeping the Challenge Sign public.
- Allow authenticated non-combatant admins to pick up items manually or automatically inside an arena.
- Permit protected-terrain changes only for authenticated admins with `devcommands` enabled; active combatants remain blocked.
- Authenticate terrain-compiler operations by routed sender and preserve authorized terrain updates for connected peers.

## 0.1.6 - 2026-08-10

- Make the Challenge Sign and all four position-marker types non-solid while retaining trigger-based interaction and demolition.
- Protect terrain throughout the arena's full protected radius.
- Cover attack-spawned terrain operations, modern terrain compilation, legacy modifiers, and effects overlapping the arena boundary.

## 0.1.5 - 2026-08-10

- Allow combatants to eat normal health, stamina, and eitr food during a challenge.
- Restore consumed food item stacks and the original active-food state during challenge cleanup.
- Make physical Arena Admin Hammer markers the sole position-setting workflow and simplify the Core panel.
- Preserve pre-combat sessions and their immutable plans through server restarts.

## 0.1.4 - 2026-08-10

- Allow authenticated administrators to configure ArenaGuard pieces while their in-arena role is spectator.
- Validate at startup that the Arena Core has exactly one interaction handler and one trigger collider.
- Aggregate local roles across all arenas so unrelated snapshots cannot remove combatant, queue, or spectator rules.
- Add an installed-game regression check for the complete Core hover-to-interaction path.

## 0.1.3 - 2026-08-10

- Hide closed arena metadata snapshots instead of leaving a stale HUD and Forfeit button.
- Show Forfeit only to the active combatant while a challenge can be forfeited.
- Fix replacement Arena Core identity and administrator interaction feedback.
- Remove overlapping stale arena records when an administrator opens a replacement Core.
- Remove the saved arena definition when its Core is dismantled by an administrator.

## 0.1.2 - 2026-08-10

- Made Arena Cores non-solid with trigger-only administrator interaction.
- Hid Core models, effects, sounds, collision, and hover prompts from non-admins.
- Fixed hover prompts to display the player's actual bound Use key.

## 0.1.1 - 2026-08-10

- Fixed current-Valheim sign prefab registration so all eight Arena build pieces load.
- Removed inherited vanilla crafting-station and placement blockers from admin pieces.
- Added fail-fast source-prefab and build-menu metadata validation.

## 0.1.0 - 2026-08-09

- Add administrator-only Arena Hammer, named Arena Cores, challenge signs, position markers, and vanilla-derived Arena Gates.
- Support many named world entrances leading to one arena hub, with exact-origin return routing and a fallback entrance.
- Add one-combatant FIFO queues, acceptance and staging timeouts, encounter countdowns, boundary forfeiture, manual forfeiture, and restart recovery.
- Add biome ladder, base/one-star/two-star ladder, and selected-encounter modes over a curated land-biome roster.
- Include hostile Dvergr and biome-ending minibosses while excluding Ocean, bosses, passive wildlife, unsupported scripted creatures, and automatic modded-creature discovery.
- Protect arena structures and enforce administrator-only construction, dismantling, and terrain modification.
- Add non-interacting ghost spectators and arena-enemy targeting restricted to the active combatant.
- Convert lethal combatant damage into arena defeat without death, loot, skill loss, or arena rewards.
- Prevent ammunition consumption and equipment durability loss, allow only health/stamina/eitr recovery consumables, and restore permitted resources and food state after a challenge.
- Add challenge selection, queue, countdown, combat HUD, admin, gate setup, and server-wide leaderboard interfaces.
- Add world-scoped atomic persistence, required-client version checks, deterministic core tests, installed-API checks, and release-package verification.
