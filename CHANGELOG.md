# Changelog

## 2.0.1

- Installer: uninstalling no longer leaves a stray file behind in a pack mod's folder.

## 2.0.0

- **Overstretched is now a mod pack.** The installer adds 53 mods by other creators (plus 29 libraries)
  in groups you tick: Tolian's moons, Mirage voice mimicking, bigger lobbies, new monsters, items, an
  interior, emotes and suits, quality of life, and bug and lag fixes. Each mod is downloaded from its
  official Thunderstore page at a pinned, checksum-verified version. Credits in THIRD_PARTY_NOTICES.md.
- **Installer**: group checklist with sizes, credits button, updates only download what changed and never
  overwrite settings, uninstall removes exactly what it installed. Same file layout as r2modman.
  It now updates itself when a newer installer is released.
- **Upgrading from 1.x**: click UPDATE in your old installer as usual. Next time you start the game,
  Overstretched offers the mod pack; it opens the new installer, which installs the pack as soon as you
  close the game.
- **Mods tab** in the F8 menu: every mod with author, license and links, an on/off switch (applies next
  launch) that won't turn off a library something still needs, and a settings editor for every mod.
- Moons from the pack get their own section in Ship; the weather picker includes custom weathers.
- Spawn ALL can include modded monsters (Settings); monsters that can't spawn are skipped, not crashed on.
- The menu key can be changed (Settings).
- The Stretched's voice stealing can be switched off (useful with Mirage).
- Joining players' mods are compared with the host's; mismatches get a warning.

## 1.1.0

- **Brand new menu**: sidebar tabs, cards, animated switches and sliders, toasts, fade-in, drag to move,
  drag the corner to resize, adjustable size and opacity.
- **Spawn ALL / Hold to spam are pinned** at the top of the Monsters tab, so the growing list never pushes
  them away. Live monsters are grouped by type (Bracken ×6) with group freeze / bring / kill / remove.
- **Fixed: No fall damage** now blocks hard landings too.
- **Player**: infinite stamina, infinite battery, infinite item uses (spray paint, TZP, shotgun), super
  strength, invisible to monsters.
- **Vision**: night vision, ESP for monsters, players, scrap (with a minimum value) and traps.
- **Teleport**: ship, main entrance, fire exits, any player, any loose scrap.
- **Crew** (host): revive all dead, heal anyone, bring players to you.
- **Items** (host): spawn any item with a custom value, bring all loose scrap to the ship.
- **Monsters** (host): pets that follow you and ignore everyone, per-monster size (0.25x to 5x).
- **World** (host): open / close / unlock / lock every door, disable or enable all traps.
- **Ship** (host): land or leave from the menu, free moon routing, free store items, free upgrades and decor.
- **Chaos** (host): horde, lights out, scatter players, low gravity, earthquake, or a random one.
- **Settings are saved** between launches (BepInEx/config/psychy.overstretched.menu.cfg).

## 1.0.2

- **Spawn ALL** now spawns every monster, indoor and outdoor, right in front of you wherever you are.
  Monsters outside their usual area may wander oddly.

## 1.0.1

- **Spawn ALL** button in Monsters > Spawned now: one of every monster that belongs where you stand (indoor or outdoor), scattered in front of you.
- **Hold to spam**: hold the button to keep spawning a batch every quarter second.
- **Old Birds** can be spawned (the menu builds the nest they need, synced to everyone) and are included outdoors.
- Every monster in the game now shows in the menu lists, with the names players know (Old Bird, Bracken, Coil-Head...).

## 1.0.0

First release.

- **The Stretched**: a new indoor monster with its own model, 28 animations and a full AI (stalking,
  light flickers, doorway stares, ceiling ambushes, stolen voices, the kill, the ship siege, and more).
- **F8 menu**: player cheats, economy, world controls, a live list of every spawned monster with
  freeze / bring / go to / kill / remove, and a per-moon spawn table editor.
- **Installer**: install, update and uninstall with one click.
