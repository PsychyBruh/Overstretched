# Overstretched 2.0: mod pack plan

## Context

Overstretched becomes a mod pack. Players still run one installer, and they get our mod plus a curated set
of other people's mods. Our F8 menu gets a **Mods** tab that lists each included mod with its credits, lets
you turn it on or off, and edits its settings. The whole pack has to compile, load together without
conflicts, and keep the game playing correctly.

The research (2026-10-25) changed the assumptions:

- **Your game is v81** ("The Blooming Update", 2026-04-05). The CurseForge copies were built for v40/v50
  and **will not work**. Every mod must come from its current Thunderstore build.
- **Many newer builds and their dependencies have no license**, or ask not to be re-uploaded: the Toliann
  moons, JLL, Mirage's request, ViewExtension, MaskedInvisFix and DawnLib. With no license we may not
  re-host them, even for free.

**Decision:** we don't re-host anyone's files. Our installer downloads every third-party mod **directly
from its official Thunderstore page**, at a version we pin and check, the same way r2modman and other mod
managers do. Overstretched's GitHub release only contains our own code plus a pack list. This:

- covers every mod the research found usable, including the "unmodified only" and unlicensed-dependency
  ones;
- respects authors (Mirage's author asks for exactly this) and counts their downloads;
- keeps third-party binaries out of our repo.

We still show full credits.

---

## 1. What goes in

### Tier 1: the CurseForge list, updated to v81 builds

| Mod (Thunderstore) | Pinned version | License | Why / notes |
|---|---|---|---|
| IAmBatby-LethalLevelLoader | 1.7.13 | MIT | Needed for custom moons. Pulls in Evaisa-FixPluginTypesSerialization (LGPL-3) |
| mrov-WeatherRegistry | 0.8.8 | LGPL-3 | Weather API, plus mrov-MrovLib (LGPL-3) and Hamunii-AutoHookGenPatcher / DetourContext_Dispose_Fix (MIT) |
| Rune580-LethalCompany_InputUtils | 0.7.13 | LGPL-3 | Rebindable keys. We'll also use it so F8 can be rebound |
| darmuh-darmuhsTerminalStuff + darmuh-OpenLib | 3.10.4 / 0.4.3 | MIT | Terminal overhaul. Development is ending (moving to DawnLib); fine to use now |
| TheFluff-FairAI | 1.6.1 | MIT | Mines and turrets hurt monsters too |
| PureFPSZac-NestFix | 1.3.0 | MIT | Baboon hawk nest fix (v81) |
| Tolian-Tolian_Moons | 2.5.2 | none on the new build: download only, never re-host | ~19 moons. Pulls in JacobG5-JLL (no license) and AudioKnight-StarlancerAIFix (NC-SA). **Don't also install the separate Toliann moons**: the pack already contains them, and Echelon is deprecated |
| qwbarch-Mirage | 1.29.1 | CC BY-NC-ND; the author asks people to "use the official mod" | Monsters mimic voices. Pulls in MirageCore, NAudio, LethalSettings, Concentus, OpusDotNet, ButteryStancakes-MaskFixes, VirusTLNR-MaskedInvisFix |

**Dropped, with reasons (shown in the README):**

- AdvancedCompany: removed from Thunderstore, unmaintained since 2024, no v81 build, and it would clash
  with the lobby, terminal and level mods.
- Hellboy: the CurseForge "MIT" copy is an uncredited re-upload of an unlicensed mod, and it's a character
  owned by someone else.
- Mortar BepInEx Bridge: only useful for the Mortar mod manager.
- EnumUtils: nothing in the pack needs it.
- The separate Toliann moon packages: already inside Tolian_Moons.

### Tier 2: Thunderstore picks (permissive licenses, v81-era, work together)

Grouped so the installer can show each group as a checkbox. The groups marked "on" are installed by
default.

- **Base** (always installed when something needs it): BepInExPack, LethalLib (+HookGenPatcher,
  MonoDetour), LethalNetworkAPI (+OdinSerializer), PathfindingLib, TestAccountCore.
- **Lobby (on):** MoreCompany (bigger lobbies) and LobbyControl.
- **Monsters (on):** Mimics, Peepers, Locker, Haunted_Harpist, ImmortalSnail, SCP966, SCP1507, Moonswept,
  ToilHead, SoftDiamond-RollingGiant, Biodiversity, ClaySurgeonOverhaul (+BarberFixes).
- **Items (on):** LethalThings, Whiteboard, OuijaBoard, LethalPhones, EliteFlashlight, LCGoldScrapMod,
  SellBodiesFixed, Remnants.
- **Interiors (on):** PoolRooms, LiminalPools.
- **Emotes and suits (on):** TooManyEmotes (+TooManyEmotesScrap), More_Suits, TooManySuits.
- **Quality of life (on):** HotbarPlus, ReservedItemSlotCore (+Flashlight and Walkie slots), OpenBodyCams,
  SpectateEnemies, EmployeeAssignmentsRevived, LethalConstellations (darmuh's companion to TerminalStuff).
- **Stability and performance (on):** ButteryFixes, LethalFixes, MaskFixes, JetpackFixes, Matty_Fixes,
  PathfindingLagFix, SmartEnemyPathfinding, CullFactory, LethalSponge, BetterLadders, CruiserImproved.
- **Optional (off):** ScarletDevilMansion (Touhou fan work, CC BY), Wither moon. Off because of IP risk or
  extra libraries.

**Left out on purpose:**

- They duplicate our menu: Lategame_Upgrades, LethalMon, LittleCompany, Brutal Company (Minus and Extra
  Reborn), Imperium, MoreMoneyStart, SellMyScrap, Poltergeist, Malfunctions, EnhancedLockpicker,
  EnhancedRadarBooster.
- Characters owned by someone else: SirenHead, FacelessStalker, CastleGrounds.
- Conflicts: a second terminal overhaul (TerminalFormatter), a second lobby mod (LateCompany,
  BiggerLobby), and EladsHUD.
- Need DawnLib, which has no license: CodeRebirth, ShipWindows, JPOGTrex, UsualScrap.

---

## 2. How it's packaged

New files in the repo (source only, no third-party binaries):

- `pack/pack.json`: what we want, by Thunderstore id, group, and default on/off.
- `pack/pack.lock.json`: generated from pack.json. For every package (dependencies included) it records
  the exact version, download URL `https://thunderstore.io/package/download/<ns>/<name>/<ver>/`, SHA-256,
  license, author, source URL and whether every player needs it.
- `tools/pack/Update-PackLock.ps1`:
  - reads pack.json and walks dependencies through `https://thunderstore.io/api/experimental/package/<ns>/<name>/`;
  - keeps the highest version when two mods need the same library;
  - downloads each zip once into `pack/.cache/` (gitignored) to hash it;
  - writes pack.lock.json;
  - generates `THIRD_PARTY_NOTICES.md` and the README's "Included mods" table.
- The release adds a `pack.lock.json` asset next to `Overstretched.dll` and `OverstretchedInstaller.exe`.

Our MIT license covers only our code. The README and NOTICES say each mod keeps its own license, link
its source, and note that the pack is free and never sold (needed for the NC licenses).

---

## 3. Installer changes (`Installer/Program.cs`)

Right now it downloads one DLL into `BepInEx/plugins/Overstretched` and writes `version.txt`.
`Uninstall` deletes that folder.

- **Install / Update:**
  1. Fetch the release, then `pack.lock.json`.
  2. Download each selected package, check its SHA-256 (refuse anything that doesn't match), and extract
     it with mod-manager layout rules:
     - `BepInEx/plugins|patchers|config|core` folders go where they say;
     - loose `.dll` files go to `BepInEx/plugins/<ns>-<name>/`;
     - `manifest.json`, `README`, `LICENSE` and `CHANGELOG` are kept next to the mod.
  3. Never overwrite an existing config file on update.
  4. Write `BepInEx/plugins/Overstretched/installed.json`: every file we placed, by package and version.
- **Update** only downloads packages whose version changed and removes files that disappeared from the
  lock.
- **Uninstall** deletes exactly the files in installed.json. It still offers "also remove BepInEx".
- **Conflict check before install:** scan `BepInEx/plugins` for the same plugin GUIDs or folder names
  installed some other way (by hand or an old copy). Show them and offer "use ours" or "keep yours".
  Mention that r2modman profiles live elsewhere and aren't touched.
- **GUI:**
  - a group checklist with sizes, built from pack.json groups;
  - a total download size;
  - a **Credits** button that opens THIRD_PARTY_NOTICES.
- **CLI:** keep `--install/--update/--uninstall/--status`, and add `--groups core,monsters,...`.
- Downloads are cached in `%TEMP%` and retried once. Progress shows per package.

---

## 4. In-game: the Mods tab

New files: `Overstretched/ModPack.cs`, plus `Overstretched.Preloader` (a tiny BepInEx patcher project).

- **List:**
  - built from `installed.json` and `BepInEx.Bootstrap.Chainloader.PluginInfos`;
  - shows name, author, version, loaded or failed, license, and a "needs everyone in the lobby" badge;
  - has a search box and group filter chips;
  - libraries are hidden behind a "show libraries" switch.
- **Credits panel per mod:** author, license, Thunderstore and source links, and what it adds.
- **On/off switch:**
  - It writes `BepInEx/config/overstretched.mods.json`. A DLL can't be moved while the game has it
    loaded, so the change applies **next launch**, and the tab says so.
  - At the next launch, `Overstretched.Preloader` (in `BepInEx/patchers`) runs before mods load. It
    moves a disabled mod's folder to `BepInEx/Overstretched.Disabled/` and moves re-enabled ones back.
  - It refuses to disable a library that an enabled mod still needs, using the dependency graph in the
    lock.
- **Settings editor:**
  - generic, for any loaded mod, through `PluginInfo.Instance.Config`;
  - bools become our switches, numbers with a range become sliders, enums become segmented buttons, text
    becomes a text field;
  - each mod's "takes effect on restart" note is shown when it doesn't hot-reload;
  - edits are written through BepInEx `ConfigEntry`, so nobody's files are modified.
- **Host settings that must match** (darmuhsTerminalStuff networking, Mirage's host-synced config) get a
  "host decides" label.

---

## 5. Making everything work together

Soft integration only. We never reference other mods' DLLs at compile time, so **our build never depends
on them**: a check through `Chainloader.PluginInfos` plus reflection, or a separate class that only runs
when the mod is loaded.

| Area | Change | Files |
|---|---|---|
| Load order | `[BepInDependency("imabatby.lethallevelloader", SoftDependency)]` and the same for WeatherRegistry, so we load after them | `Plugin.cs` |
| The Stretched on custom moons | Already re-synced at `StartOfRound.Start` and before every `RoundManager.RefreshEnemiesList`, so LLL moons are covered. Add a test that confirms it on a Toliann moon | `StretchedRegistry.cs` |
| Spawn ALL / Spawn one with modded monsters | `Features.NaturalTypes()` will now include modded enemies. Add a "Vanilla only / Include modded" switch, a per-type try/catch with a skip list for types that throw, and a warning badge for modded types | `ModMenu.cs`, `Features.cs` |
| Ship tab moons | The levels list grows to ~40. Group by vanilla / modded and sort by name. LLL patches `ChangeLevel`, so free routing goes through `ChangeLevelServerRpc` as now. Test it | `ModMenu.DrawShip` |
| Weather picker | When WeatherRegistry is loaded, list its weathers and change them through its API (reflection), so custom weathers show and sync. Otherwise use the vanilla enum | `ModMenu.DrawWorld`, `Features.cs` |
| F8 key | Register through InputUtils when it's present, so it can be rebound in the game's keybind menu. Otherwise F8 | `ModMenu.cs` |
| Lobby size (MoreCompany) | Check that our code never assumes 4 players. Crew/ESP/StretchedAI already loop over `allPlayerScripts`; review `StretchedAI` mark logic and `VoiceThief` | `StretchedAI.cs`, `StretchedAudio.cs` |
| Voice (Mirage vs our VoiceThief) | Both read player voice audio. VoiceThief taps the Dissonance output with `OnAudioFilterRead`, which should stack. Test both, and add a setting "Let Mirage handle voices" that turns VoiceThief off when Mirage is loaded | `StretchedAudio.cs`, `Settings.cs` |
| Traps (FairAI) | FairAI patches Turret/Landmine targeting; our `SetTraps` only toggles. No change, just test | – |
| Invisibility / freeze / pets vs other AI mods | Our patches are a postfix on `PlayerIsTargetable`, a postfix on `MeetsStandardPlayerCollisionConditions`, and prefixes on enemy `Update`/`DoAIInterval`. Modded enemies that skip `base.Update` won't freeze: add a "can't freeze this one" badge after testing | `Patches.cs` |
| Terminal | Only TerminalStuff (+OpenLib, LethalConstellations), LLL and WeatherRegistry patch the Terminal, and they support each other. No other terminal mod goes in | – |
| Lobby mismatch | On join, the client sends a hash of its pack list over `MenuNet`. If it differs, the host sees a warning naming the missing or extra mods | `MenuNet.cs`, `Features.cs` |
| Names in menus | Modded monsters show their `enemyName`. Allow optional friendly names from pack.json | `Features.DisplayName` |

---

## 6. Build order (each step leaves a working game)

1. **Pack tooling:** pack.json, the lock script, NOTICES generation. Check every license in the generated
   NOTICES by hand.
2. **Installer v2:** lock-based install, update and uninstall, installed.json, the conflict check, groups
   and credits. Test against a copy of the game folder first, then the real one.
3. **Tier 1 only:**
   - install it, run the game, fix load errors;
   - add the soft-dependency, weather, moon-grouping and F8/InputUtils changes;
   - run the self-test.
4. **Mods tab + preloader:** list, credits, on/off, settings editor.
5. **Tier 2, one group at a time** (Lobby, then Monsters, Items, Interiors, Emotes/Suits, QoL, Stability).
   After each group, run the self-test and read the BepInEx log. Any mod that throws on v81 or breaks a
   test gets pinned to an older build or dropped.
6. **Compatibility work from section 5:** Spawn ALL with modded monsters, the VoiceThief/Mirage switch,
   the lobby hash check, the MoreCompany review.
7. **Docs and release:**
   - README "Included mods", THIRD_PARTY_NOTICES, CHANGELOG 2.0.0;
   - release assets: Overstretched.dll, pack.lock.json, OverstretchedInstaller.exe.
   - Keep the old installer working: it still finds `Overstretched.dll`.

## 7. Verification

- **Compile:** both projects build clean with `-c Release` and no third-party references. The
  preloader project builds against BepInEx.Preloader only.
- **Load check (self-test):** after boot, compare `Chainloader.PluginInfos` with the expected GUIDs from
  the lock. Log any missing plugin and every error or exception during load. The pass rule is no new
  errors compared with a vanilla-plus-Overstretched baseline.
- **Gameplay self-test** (extend `AutoTest.cs`):
  - everything the current test already covers;
  - route for free to a Toliann moon and land: the dungeon generates, The Stretched is in its spawn list,
    Spawn ALL works and Remove all cleans up;
  - change weather through WeatherRegistry;
  - run a darmuhsTerminalStuff command through the terminal;
  - confirm Mirage is loaded and its components are on the masked enemy prefab;
  - switch a mod off in the Mods tab, restart, and confirm it isn't loaded, then switch it back on;
  - take screenshots of the Mods tab and the Ship tab moon grid.
- **Performance:** load time and FPS on the ship and on a moon, compared before and after the pack. Report
  the numbers.
- **Multiplayer (manual, with a friend):**
  - host plus one client, both installed with the same groups;
  - land on a modded moon, see the same monsters and items;
  - check that voice mimic works and that the lobby-mismatch warning fires when one player is missing a
    group;
  - check MoreCompany with 5+ players if possible.
- **Uninstall:** after uninstall, the plugins folder matches the state before install (installed.json
  diff), and no other mods or configs are touched.
