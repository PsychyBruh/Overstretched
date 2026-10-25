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

        private IEnumerator Guarded()
        {
            var run = Run();
            float deadline = Time.realtimeSinceStartup + 900f; // boot + landing alone can take several minutes
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
            menu.DebugShow(3, 0, 0);
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
                menu.DebugShow(3, 1, list);
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
            menu.DebugShow(-1, 0, 0);
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
            Log("PASSED script");
        }
    }
}
