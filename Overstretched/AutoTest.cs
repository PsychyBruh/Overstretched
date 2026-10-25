using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;

namespace Overstretched
{
    /// <summary>
    /// Developer self-test, only active when BepInEx/plugins/Overstretched/autotest.txt exists. Hosts a LAN
    /// game on a throwaway save, lands, goes inside, spawns The Stretched, drives every behaviour, logs
    /// everything with an [AUTOTEST] prefix, then quits the game.
    /// </summary>
    public class AutoTest : MonoBehaviour
    {
        public static string FlagFile => Path.Combine(BepInEx.Paths.PluginPath, "Overstretched", "autotest.txt");
        private readonly List<string> errors = new List<string>();

        private void Start()
        {
            Application.logMessageReceived += OnLog;
            StartCoroutine(Guarded());
        }

        private void OnLog(string message, string stack, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception) return;
            string line = message.Split('\n')[0];
            if (errors.Count < 40 && !errors.Contains(line)) errors.Add(line);
        }

        private static void Log(string msg) => Plugin.Log.LogInfo("[AUTOTEST] " + msg);

        private static void LogPack()
        {
            Log($"pack: {BepInEx.Bootstrap.Chainloader.PluginInfos.Count} plugins loaded; pack present {ModPack.Present}");
            if (!ModPack.Present) return;
            foreach (var p in ModPack.Pack.packages.Where(p => p.top))
                Log($"pack: {p.id} {p.version} -> {(ModPack.PluginsOf(p).Count > 0 ? "loaded " + string.Join(",", ModPack.PluginsOf(p).Select(i => i.Metadata.GUID)) : "no plugin of its own")}");
            Log($"pack: LLL {Compat.Has(Compat.LethalLevelLoader)}, WeatherRegistry {Compat.Has(Compat.WeatherRegistry)}, Mirage {Compat.Has(Compat.Mirage)}");
        }

        private IEnumerable<object> ModdedMoon(ModMenu menu, GameNetcodeStuff.PlayerControllerB me)
        {
            var sor = StartOfRound.Instance;
            var custom = Compat.CustomLevels();
            Log($"pack: {custom.Count} modded moons: {string.Join(", ", custom.Keys.Select(l => l.PlanetName).Take(25))}");
            if (custom.Count == 0) yield break;
            Features.ForceLeave();
            float end = Time.realtimeSinceStartup + 120f;
            while (!sor.inShipPhase && Time.realtimeSinceStartup < end) yield return new WaitForSeconds(1f);
            yield return new WaitForSeconds(3f);
            Log($"pack: back in orbit {sor.inShipPhase}");
            var target = custom.Keys.FirstOrDefault(l => l.PlanetName.Contains("Harloth")) ?? custom.Keys.First();
            Features.RouteTo(target);
            end = Time.realtimeSinceStartup + 60f;
            while ((sor.currentLevel != target || sor.travellingToNewLevel) && Time.realtimeSinceStartup < end) yield return new WaitForSeconds(1f);
            Log($"pack: routed to {sor.currentLevel.PlanetName} (wanted {target.PlanetName})");
            var weathers = Compat.Weathers();
            var clear = weathers.FirstOrDefault(w => w.Name == "None") ?? weathers.First();
            Compat.SetWeather(sor.currentLevel, clear);
            Log($"pack: {weathers.Count} weathers ({string.Join(", ", weathers.Select(w => w.Name))}); set {clear.Name} -> {Compat.CurrentWeather(sor.currentLevel)}");
            menu.DebugShow(7, ModMenu.ViewLive, 0);
            yield return new WaitForSeconds(1f);
            Shot("ship_moons");
            yield return new WaitForSeconds(0.3f);
            menu.DebugShow(11, ModMenu.ViewLive, 0);
            menu.DebugOpenMod("IAmBatby-LethalLevelLoader");
            yield return new WaitForSeconds(1f);
            Shot("mods_tab");
            yield return new WaitForSeconds(0.3f);
            menu.DebugShow(-1, 0, 0);

            sor.StartGame();
            float landStart = Time.realtimeSinceStartup;
            end = Time.realtimeSinceStartup + 360f;
            while (!(sor.shipHasLanded && RoundManager.Instance.dungeonCompletedGenerating) && Time.realtimeSinceStartup < end)
                yield return new WaitForSeconds(1f);
            Log($"pack: landed on {sor.currentLevel.PlanetName}: {sor.shipHasLanded} after {Time.realtimeSinceStartup - landStart:0}s, dungeon generated {RoundManager.Instance.dungeonCompletedGenerating}");
            Log($"pack: The Stretched in {sor.currentLevel.PlanetName}'s spawn list: {sor.currentLevel.Enemies.Any(e => e.enemyType == StretchedRegistry.Type)}");
            yield return new WaitForSeconds(3f);
            var entrance = FindObjectsOfType<EntranceTeleport>().FirstOrDefault(e => e.isEntranceToBuilding && e.entranceId == 0);
            if (entrance != null)
            {
                entrance.FindExitPoint();
                entrance.TeleportPlayer();
                yield return new WaitForSeconds(3f);
            }
            Log($"pack: inside {me.isInsideFactory} on {sor.currentLevel.PlanetName}");
            Settings.FreezeEnemies = true;
            Settings.SpawnAllModded = true;
            int spawned = menu.DebugSpawnAll();
            yield return new WaitForSeconds(5f);
            var live = RoundManager.Instance.SpawnedEnemies.Where(e => e != null).ToList();
            Log($"pack: spawn all (with modded) requested {spawned}, alive {live.Count}, modded alive {live.Count(e => Features.IsModded(e.enemyType))}: " +
                string.Join(", ", live.Where(e => Features.IsModded(e.enemyType)).Select(e => e.enemyType.enemyName).Distinct()));
            var modded = live.Where(e => e != null && Features.IsModded(e.enemyType)).ToList();
            yield return new WaitForSeconds(1f); // let anything still dropping onto the floor settle
            var startPos = modded.ToDictionary(e => e, e => e.transform.position);
            yield return new WaitForSeconds(4f);
            var moved = modded.Where(e => e != null && Vector3.Distance(startPos[e], e.transform.position) > 0.5f).Select(e => $"{e.enemyType.enemyName} {Vector3.Distance(startPos[e], e.transform.position):0.0}m").ToList();
            Log($"pack: frozen modded monsters that still moved in 4s: {moved.Count}/{modded.Count} {string.Join(", ", moved)}; player dead {me.isPlayerDead}");
            menu.DebugShow(ModMenu.MonstersTab, ModMenu.ViewLive, 0);
            yield return new WaitForSeconds(1f);
            Shot("modded_monsters");
            yield return new WaitForSeconds(0.3f);
            menu.DebugShow(-1, 0, 0);
            foreach (var e in live) menu.DebugRemove(e);
            Settings.FreezeEnemies = false;
            Settings.SpawnAllModded = false;
            yield return new WaitForSeconds(2f);
            Log($"pack: after remove all alive {RoundManager.Instance.SpawnedEnemies.Count(e => e != null)}; player dead {me.isPlayerDead}");
        }

        private static void Shot(string name)
        {
            var dir = Path.Combine(BepInEx.Paths.PluginPath, "Overstretched", "shots");
            Directory.CreateDirectory(dir);
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, name + ".png"));
        }

        private IEnumerator Guarded()
        {
            var run = Run();
            float deadline = Time.realtimeSinceStartup + 2400f; // boot + landing take several minutes, longer with the mod pack
            while (true)
            {
                object current;
                try
                {
                    if (!run.MoveNext()) break;
                    current = run.Current;
                }
                catch (Exception e)
                {
                    Log("FAILED with exception: " + e);
                    break;
                }
                if (Time.realtimeSinceStartup > deadline) { Log("FAILED: timed out"); break; }
                yield return current;
            }
            Log($"errors logged during test: {errors.Count}");
            foreach (var e in errors) Log("  error: " + e);
            Log("END");
            try { File.Delete(FlagFile); } catch { }
            yield return new WaitForSeconds(1f);
            Application.Quit();
        }

        private static IEnumerator WaitFor(Func<bool> condition, float timeout, string what)
        {
            float end = Time.realtimeSinceStartup + timeout;
            while (!SafeCheck(condition))
            {
                if (Time.realtimeSinceStartup > end) throw new TimeoutException("waiting for " + what);
                yield return new WaitForSeconds(0.5f);
            }
            Log("ok: " + what);
        }

        private static bool SafeCheck(Func<bool> condition)
        {
            try { return condition(); } catch { return false; }
        }

        private IEnumerator Run()
        {
            Log("start");
            yield return WaitFor(() => FindObjectOfType<PreInitSceneScript>() != null || FindObjectOfType<MenuManager>() != null, 90, "boot");
            var pre = FindObjectOfType<PreInitSceneScript>();
            if (pre != null)
            {
                yield return new WaitForSeconds(2f);
                pre.ChooseLaunchOption(false); // LAN, no Steam lobby
            }
            yield return WaitFor(() => FindObjectOfType<MenuManager>() != null && GameNetworkManager.Instance != null, 90, "main menu");
            yield return new WaitForSeconds(4f);
            Log($"registry prefab={(StretchedRegistry.Prefab != null)} type={(StretchedRegistry.Type != null)}");
            LogPack();
            // "boot" in the flag file: only check what loaded (used to test the Mods tab's on/off switches).
            if (File.ReadAllText(FlagFile).Trim() == "boot")
            {
                yield return new WaitForSeconds(2f);
                Shot("main_menu");
                yield return new WaitForSeconds(0.5f);
                foreach (var line in File.Exists(FlagFile + ".toggle") ? File.ReadAllLines(FlagFile + ".toggle") : new string[0])
                {
                    if (line.Trim() == "upgrade")
                    {
                        // Same as clicking "Install mod pack" on the main menu popup.
                        PackUpgrade.Begin();
                        float until = Time.realtimeSinceStartup + 120f;
                        while (PackUpgrade.StatusText == null || PackUpgrade.Busy)
                        {
                            if (Time.realtimeSinceStartup > until) break;
                            yield return new WaitForSeconds(0.5f);
                        }
                        Log($"upgrade: launched {PackUpgrade.Launched}: {PackUpgrade.StatusText}");
                        continue;
                    }
                    var parts = line.Split(' ');
                    var pkg = ModPack.Find(parts[1]);
                    if (pkg == null) continue;
                    Log($"pack: {pkg.id} loaded={ModPack.PluginsOf(pkg).Count > 0} offOnDisk={ModPack.IsDisabledOnDisk(pkg)}; switching {parts[0]}: " +
                        (ModPack.SetEnabled(pkg, parts[0] == "on") ?? "ok"));
                }
                yield break;
            }

            var gnm = GameNetworkManager.Instance;
            gnm.currentSaveFileName = "LPAutoTestFile"; // never touch the player's saves
            gnm.saveFileNum = 3;
            gnm.lobbyHostSettings = new HostSettings("overstretched-autotest", false, "");
            FindObjectOfType<MenuManager>().LAN_HostSetLocal();
            gnm.StartHost();
            yield return WaitFor(() => StartOfRound.Instance != null && gnm.localPlayerController != null, 120, "in ship");
            yield return new WaitForSeconds(6f);
            Log($"moon {StartOfRound.Instance.currentLevel.PlanetName}; spawn entry in list: " +
                StartOfRound.Instance.currentLevel.Enemies.Any(e => e.enemyType == StretchedRegistry.Type));

            StartOfRound.Instance.StartGame();
            yield return WaitFor(() => StartOfRound.Instance.shipHasLanded && RoundManager.Instance.dungeonCompletedGenerating, 150, "landed");
            yield return new WaitForSeconds(4f);

            var me = gnm.localPlayerController;
            var entrance = FindObjectsOfType<EntranceTeleport>().FirstOrDefault(e => e.isEntranceToBuilding && e.entranceId == 0);
            if (entrance == null) throw new Exception("no main entrance");
            entrance.FindExitPoint();
            entrance.TeleportPlayer();
            yield return new WaitForSeconds(3f);
            Log($"inside factory: {me.isInsideFactory} at {me.transform.position}");

            // ---- Monsters tab: live list controls + spawn table, with the menu drawn on screen.
            var menu = ModMenu.Instance;
            var vanillaType = StartOfRound.Instance.currentLevel.Enemies
                .Select(e => e.enemyType)
                .FirstOrDefault(t => t != null && t != StretchedRegistry.Type && t.enemyPrefab != null && !t.isOutsideEnemy);
            NavMesh.SamplePosition(me.transform.position + me.transform.right * 8f, out var vhit, 15f, NavMesh.AllAreas);
            RoundManager.Instance.SpawnEnemyGameObject(vhit.position, 0f, -1, vanillaType);
            yield return new WaitForSeconds(3f);
            var vanilla = RoundManager.Instance.SpawnedEnemies.FirstOrDefault(e => e != null && e.enemyType == vanillaType);
            Log($"menu: spawned vanilla {vanillaType?.enemyName}: {vanilla != null}");
            menu.DebugShow(ModMenu.MonstersTab, ModMenu.ViewLive, 0);
            yield return new WaitForSeconds(2f);
            Settings.FrozenEnemies.Add(vanilla);
            var frozenAt = vanilla.transform.position;
            yield return new WaitForSeconds(3f);
            Log($"menu: frozen moved {(vanilla.transform.position - frozenAt).magnitude:0.00}m in 3s");
            Settings.FrozenEnemies.Remove(vanilla);
            var thawedAt = vanilla.transform.position;
            yield return new WaitForSeconds(4f);
            Log($"menu: after unfreeze moved {(vanilla.transform.position - thawedAt).magnitude:0.00}m in 4s (should be > 0 if it wanders)");
            Settings.FrozenEnemies.Add(vanilla);
            menu.DebugBring(vanilla);
            yield return new WaitForSeconds(1.5f);
            Log($"menu: bring -> {menu.DebugStatus}; distance now {Vector3.Distance(vanilla.transform.position, me.transform.position):0.0}");
            menu.DebugGoTo(vanilla);
            yield return new WaitForSeconds(1f);
            Log($"menu: go to -> {menu.DebugStatus}; distance now {Vector3.Distance(vanilla.transform.position, me.transform.position):0.0}");
            for (int list = 0; list < 3; list++)
            {
                menu.DebugShow(ModMenu.MonstersTab, ModMenu.ViewTable, list);
                yield return new WaitForSeconds(1.2f);
            }
            var level = StartOfRound.Instance.currentLevel;
            var mine = level.Enemies.First(e => e.enemyType == StretchedRegistry.Type);
            mine.rarity = 7;
            StretchedRegistry.SyncSpawnLists();
            Log($"menu: spawn table edit kept after refresh: {mine.rarity == 7}");
            menu.DebugRemove(vanilla);
            Settings.FrozenEnemies.Clear();
            yield return new WaitForSeconds(1.5f);
            Log($"menu: removed vanilla: {vanilla == null}");
            // Spawn ALL, spammed: three batches a quarter-second apart, like holding the spam button.
            Settings.FreezeEnemies = true; // frozen before they arrive, so the crowd can't kill the test player
            int spawned = 0;
            for (int i = 0; i < 3; i++) { spawned += menu.DebugSpawnAll(); yield return new WaitForSeconds(0.25f); }
            yield return new WaitForSeconds(5f);
            var live = RoundManager.Instance.SpawnedEnemies.Where(e => e != null).ToList();
            Log($"menu: spawn all x3 requested {spawned}, alive {live.Count}: " +
                string.Join(", ", live.GroupBy(e => e.enemyType.enemyName).Select(g => $"{g.Key} x{g.Count()}")));
            menu.DebugShow(ModMenu.MonstersTab, ModMenu.ViewLive, 0);
            menu.DebugExpandAll();
            yield return new WaitForSeconds(1f);
            Shot("monsters_live");
            yield return new WaitForSeconds(1f);
            foreach (var e in live) menu.DebugRemove(e);
            Settings.FreezeEnemies = false;
            yield return new WaitForSeconds(2f);
            Log($"menu: after remove all, alive {RoundManager.Instance.SpawnedEnemies.Count(e => e != null)}; player dead={me.isPlayerDead}");

            // Outdoor Spawn ALL, which has to include Old Birds (they need a nest to exist).
            me.TeleportPlayer(StartOfRound.Instance.playerSpawnPositions[0].position + Vector3.forward * 12f);
            me.isInsideFactory = false;
            me.isInHangarShipRoom = false;
            me.isInElevator = false;
            yield return new WaitForSeconds(1.5f);
            Settings.FreezeEnemies = true;
            int outdoor = menu.DebugSpawnAll();
            yield return new WaitForSeconds(5f);
            var outside = RoundManager.Instance.SpawnedEnemies.Where(e => e != null).ToList();
            var birds = outside.Where(e => e is RadMechAI).ToList();
            Log($"menu: outdoor spawn all requested {outdoor}, alive {outside.Count}: " +
                string.Join(", ", outside.GroupBy(e => e.enemyType.enemyName).Select(g => $"{g.Key} x{g.Count()}")));
            Log($"menu: old birds {birds.Count}, alive and visible: {birds.Count(b => !b.isEnemyDead && b.gameObject.activeInHierarchy)}");
            foreach (var e in outside) menu.DebugRemove(e);
            Settings.FreezeEnemies = false;
            yield return new WaitForSeconds(2f);

            // ---- Menu screenshots, one per tab (outside, by the ship).
            for (int t = 0; t < ModMenu.TabCount; t++)
            {
                menu.DebugShow(t, ModMenu.ViewLive, 0);
                yield return new WaitForSeconds(0.8f);
                Shot($"tab_{t:00}");
                yield return new WaitForSeconds(0.3f);
            }
            menu.DebugShow(ModMenu.MonstersTab, ModMenu.ViewSpawn, 0);
            yield return new WaitForSeconds(0.8f);
            Shot("monsters_spawn");
            menu.DebugShow(ModMenu.MonstersTab, ModMenu.ViewTable, 0);
            yield return new WaitForSeconds(0.8f);
            Shot("monsters_table");
            menu.DebugShow(-1, 0, 0);

            // ---- No fall damage: drop from 45 m.
            Log($"feature: health before fall test {me.health}, last damage {PlayerPatches.LastDamage}");
            me.health = 100;
            Settings.NoFallDamage = true;
            me.TeleportPlayer(me.transform.position + Vector3.up * 45f);
            yield return new WaitForSeconds(6f);
            Log($"feature: fall from 45m with no fall damage -> health {me.health}, dead {me.isPlayerDead}, last damage {PlayerPatches.LastDamage}");
            Settings.NoFallDamage = false;

            // ---- Low gravity + earthquake (messages to every client).
            Features.LowGravity(6f);
            Features.Earthquake(2f);
            me.TeleportPlayer(me.transform.position + Vector3.up * 20f);
            yield return new WaitForSeconds(4f);
            Log($"feature: low gravity active {Features.Instance.LowGravityActive}, after 20m drop health {me.health}, dead {me.isPlayerDead}");
            yield return new WaitForSeconds(3f);

            // ---- ESP + night vision on screen.
            Settings.MonsterEsp = Settings.PlayerEsp = Settings.ScrapEsp = Settings.TrapEsp = true;
            entrance.TeleportPlayer();
            yield return new WaitForSeconds(2f);
            Settings.NightVision = true;
            yield return new WaitForSeconds(0.5f);
            Log($"feature: night vision light on {me.nightVision.enabled}, intensity {me.nightVision.intensity:0.0}, range {me.nightVision.range:0.0}");
            Shot("esp_nightvision");
            yield return new WaitForSeconds(0.3f);
            Settings.NightVision = false;
            yield return new WaitForSeconds(0.5f);
            Shot("esp_plain");
            yield return new WaitForSeconds(0.3f);
            Settings.MonsterEsp = Settings.PlayerEsp = Settings.ScrapEsp = Settings.TrapEsp = false;

            // ---- Items: spawn with a custom value, then haul all scrap to the ship.
            var gold = Features.AllItems().FirstOrDefault(i => i.isScrap);
            var spawnedItem = Features.SpawnItem(gold, 123);
            yield return new WaitForSeconds(1f);
            Log($"feature: spawned item {gold?.itemName} value {(spawnedItem != null ? spawnedItem.scrapValue : -1)}");
            int loose = Features.LooseScrap().Count;
            Features.BringScrapToShip();
            yield return new WaitForSeconds(2f);
            Log($"feature: loose scrap before {loose}, after bring-to-ship {Features.LooseScrap().Count}, in ship {FindObjectsOfType<GrabbableObject>().Count(g => g.isInShipRoom && g.itemProperties.isScrap)}");

            // ---- Doors and traps.
            Features.Doors(MenuNet.DoorAction.Open);
            yield return new WaitForSeconds(1f);
            var doors = FindObjectsOfType<DoorLock>();
            Log($"feature: doors open {doors.Count(d => d.GetComponent<AnimatedObjectTrigger>() != null && d.GetComponent<AnimatedObjectTrigger>().boolValue)}/{doors.Length}");
            Features.Doors(MenuNet.DoorAction.Lock);
            yield return new WaitForSeconds(1f);
            Log($"feature: doors locked {doors.Count(d => d.isLocked)}/{doors.Length}");
            Features.Doors(MenuNet.DoorAction.Unlock);
            Features.Doors(MenuNet.DoorAction.Close);
            Features.SetTraps(false);
            yield return new WaitForSeconds(1f);
            Log($"feature: turrets off {FindObjectsOfType<Turret>().Count(t => !t.turretActive)}/{FindObjectsOfType<Turret>().Length}, " +
                $"mines off {FindObjectsOfType<Landmine>().Count(m => !Features.MineActive(m))}/{FindObjectsOfType<Landmine>().Length}");
            Features.SetTraps(true);

            // ---- Pet, size, invisibility.
            NavMesh.SamplePosition(me.transform.position + me.transform.forward * 4f, out var phit, 10f, NavMesh.AllAreas);
            RoundManager.Instance.SpawnEnemyGameObject(phit.position, 0f, -1, vanillaType);
            yield return new WaitForSeconds(2f);
            var pet = RoundManager.Instance.SpawnedEnemies.LastOrDefault(e => e != null && e.enemyType == vanillaType);
            // A walker too, besides the ceiling-clinging Snare Flea.
            var bugType = Resources.FindObjectsOfTypeAll<EnemyType>().FirstOrDefault(t => t.enemyName == "Hoarding bug");
            RoundManager.Instance.SpawnEnemyGameObject(phit.position, 0f, -1, bugType);
            yield return new WaitForSeconds(1.5f);
            var bug = RoundManager.Instance.SpawnedEnemies.LastOrDefault(e => e != null && e.enemyType == bugType);
            Features.SetPet(bug, true);
            Features.SetPet(pet, true);
            Features.SetSize(pet, 2f);
            Settings.Invisible = true;
            yield return new WaitForSeconds(0.5f);
            Log($"feature: invisible -> targetable {pet.PlayerIsTargetable(me)}; pet size {pet.transform.localScale.x:0.00}");
            Settings.Invisible = false;
            yield return new WaitForSeconds(0.5f);
            Log($"feature: visible again -> targetable {pet.PlayerIsTargetable(me)}");
            var petStart = Vector3.Distance(pet.transform.position, me.transform.position);
            if (NavMesh.SamplePosition(me.transform.position - me.transform.forward * 12f, out var away, 15f, NavMesh.AllAreas))
                me.TeleportPlayer(away.position);
            yield return new WaitForSeconds(5f);
            Log($"feature: pet distance {petStart:0.0} -> after walking off {Vector3.Distance(pet.transform.position, me.transform.position):0.0}, " +
                $"agent on {pet.agent.enabled} navmesh {pet.agent.isOnNavMesh}; bug pet distance {Vector3.Distance(bug.transform.position, me.transform.position):0.0}; player dead {me.isPlayerDead}");
            menu.DebugRemove(pet);
            menu.DebugRemove(bug);

            // ---- Chaos: horde (frozen), scatter, lights out.
            Settings.FreezeEnemies = true;
            Features.Horde(4);
            yield return new WaitForSeconds(2f);
            Log($"feature: horde -> alive {RoundManager.Instance.SpawnedEnemies.Count(e => e != null)}");
            foreach (var e in RoundManager.Instance.SpawnedEnemies.Where(e => e != null).ToList()) menu.DebugRemove(e);
            Settings.FreezeEnemies = false;
            var before = me.transform.position;
            Features.Scatter();
            yield return new WaitForSeconds(1f);
            Log($"feature: scatter moved {(me.transform.position - before).magnitude:0}m");
            Features.LightsOut(2f);
            yield return new WaitForSeconds(3.5f);
            Log($"feature: lights out finished, menu status: {menu.DebugStatus}");

            entrance.TeleportPlayer(); // back inside near the entrance for the Stretched test
            yield return new WaitForSeconds(2f);

            var ahead = me.transform.position + me.transform.forward * 10f;
            if (!NavMesh.SamplePosition(ahead, out var hit, 12f, NavMesh.AllAreas) &&
                !NavMesh.SamplePosition(me.transform.position, out hit, 30f, NavMesh.AllAreas))
                throw new Exception("no navmesh near player");
            RoundManager.Instance.SpawnEnemyGameObject(hit.position, 0f, -1, StretchedRegistry.Type);
            yield return WaitFor(() => FindObjectOfType<StretchedAI>() != null, 15, "spawned");
            var ai = FindObjectOfType<StretchedAI>();
            ai.DebugNoKill = true; // keep the test player alive until the final step

            // Flattened into Run (foreach) so any exception reaches the Guarded driver instead of hanging.
            IEnumerable<object> Watch(string phase, float seconds)
            {
                float end = Time.time + seconds;
                while (Time.time < end)
                {
                    if (ai == null) throw new Exception("The Stretched was destroyed during " + phase);
                    Log($"{phase,-10} {ai.DebugSummary()} | player fear={StartOfRound.Instance.fearLevel:0.00} insanity={me.insanityLevel:0.0} dead={me.isPlayerDead}");
                    yield return new WaitForSeconds(1.5f);
                }
            }

            foreach (var s in Watch("spawn", 9f)) yield return s;
            foreach (var s in Watch("natural", 12f)) yield return s;
            ai.DebugFlicker(me);       foreach (var s in Watch("flicker", 4f)) yield return s;
            ai.DebugDoorway(me);       foreach (var s in Watch("doorway", 5f)) yield return s;
            ai.DebugStare(me);         foreach (var s in Watch("stare", 6f)) yield return s;
            ai.DebugLit(me);           foreach (var s in Watch("lit", 6f)) yield return s;
            ai.DebugNoise(me);         foreach (var s in Watch("noise", 4f)) yield return s;
            RoundManager.Instance.SwitchPower(false); yield return new WaitForSeconds(1f);
            RoundManager.Instance.SwitchPower(true);  foreach (var s in Watch("power-on", 4f)) yield return s;
            ai.DebugCeiling(me);       foreach (var s in Watch("ceiling", 9f)) yield return s;
            ai.DebugKnockAndVoice(me); foreach (var s in Watch("knock", 3f)) yield return s;

            // Preview model + every clip on it, to catch animation errors.
            var preview = StretchedModel.Instantiate(me.transform.position + me.transform.forward * 4f, Quaternion.identity);
            var pa = preview.GetComponent<StretchedAnimator>();
            foreach (var clip in StretchedAnimations.Clips.Keys)
            {
                pa.Play(clip, 0.05f, 4f);
                yield return new WaitForSeconds(0.25f);
            }
            Destroy(preview);
            Log("previewed all clips");

            // Ship siege: hide in the ship with the doors shut.
            me.TeleportPlayer(StartOfRound.Instance.playerSpawnPositions[0].position);
            me.isInsideFactory = false;
            me.isInHangarShipRoom = true;
            me.isInElevator = true;
            yield return new WaitForSeconds(1f);
            var door = FindObjectOfType<HangarShipDoor>();
            door.PlayDoorAnimation(true); // like pressing the close button: the animation sets the closed flag
            yield return new WaitForSeconds(2.5f);
            if (!StartOfRound.Instance.hangarDoorsClosed) door.SetDoorClosed();
            Log($"doors closed: {StartOfRound.Instance.hangarDoorsClosed}, door power {door.doorPower:0.00}");
            ai.DebugSiege(me);
            float siegeEnd = Time.time + 28f;
            while (Time.time < siegeEnd)
            {
                Log($"siege      {ai.DebugSummary()} | doors closed={StartOfRound.Instance.hangarDoorsClosed} power={door.doorPower:0.00}");
                yield return new WaitForSeconds(2f);
            }

            Log($"siege result: doors closed={StartOfRound.Instance.hangarDoorsClosed} state={ai.CurrentState}");
            ai.DebugKill(me);
            foreach (var s in Watch("kill", 6f)) yield return s;
            Log($"player dead after kill: {me.isPlayerDead}; radar redirect: {(me.redirectToEnemy == ai)}");
            Features.ReviveAll();
            yield return new WaitForSeconds(3f);
            Log($"feature: revive all -> dead {me.isPlayerDead}, health {me.health}, on ship {me.isInHangarShipRoom}");

            // ---- Mod pack: fly to a modded moon and play on it.
            foreach (var step in ModdedMoon(menu, me)) yield return step;
            Log("PASSED script");
        }
    }
}
