using System.Collections.Generic;
using System.Linq;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Overstretched
{
    public class ModMenu : MonoBehaviour
    {
        private static readonly string[] Tabs = { "Player", "Economy", "World", "Monsters", "The Stretched" };

        private bool open;
        private int tab;
        private Rect window = new Rect(40, 40, 560, 640);
        private Vector2 scroll;
        private string creditsInput = "1000";
        private string quotaInput = "130";
        private float baseSpeed = -1f, baseJump = -1f;
        private PlayerControllerB trackedPlayer;
        private List<EnemyType> enemyTypes = new List<EnemyType>();
        private string status = "";

        public static ModMenu Instance { get; private set; } // hidden object: FindObjectOfType can't see it

        private void Awake() => Instance = this;

        private static PlayerControllerB Local =>
            StartOfRound.Instance != null ? StartOfRound.Instance.localPlayerController : null;

        private static bool IsHost =>
            NetworkManager.Singleton != null && NetworkManager.Singleton.IsHost;

        private void Update()
        {
            if (Keyboard.current != null && Keyboard.current.f8Key.wasPressedThisFrame)
                SetOpen(!open);

            ApplyPlayer();
            ApplyWorld();
            if (StartOfRound.Instance != null && !StartOfRound.Instance.inShipPhase) VoiceThief.Tick();
        }

        private void SetOpen(bool value)
        {
            open = value;
            Cursor.visible = open;
            Cursor.lockState = open ? CursorLockMode.None : CursorLockMode.Locked;
            var player = Local;
            if (player != null && player.quickMenuManager != null)
                player.quickMenuManager.isMenuOpen = open;
            if (open) RefreshEnemyTypes();
        }

        private void ApplyPlayer()
        {
            var p = Local;
            if (p == null) return;
            if (p != trackedPlayer)
            {
                trackedPlayer = p;
                baseSpeed = p.movementSpeed;
                baseJump = p.jumpForce;
            }

            p.movementSpeed = baseSpeed * Settings.SpeedMultiplier;
            p.jumpForce = baseJump * Settings.JumpMultiplier;
            if (Settings.InfiniteSprint) p.sprintMeter = 1f;
            if (Settings.GodMode) p.health = 100;

            p.thisController.enabled = !Settings.NoClip || p.isPlayerDead;
            if (Settings.NoClip && !p.isPlayerDead && !open && Keyboard.current != null)
            {
                var kb = Keyboard.current;
                var cam = p.gameplayCamera.transform;
                var dir = Vector3.zero;
                if (kb.wKey.isPressed) dir += cam.forward;
                if (kb.sKey.isPressed) dir -= cam.forward;
                if (kb.dKey.isPressed) dir += cam.right;
                if (kb.aKey.isPressed) dir -= cam.right;
                if (kb.spaceKey.isPressed) dir += Vector3.up;
                if (kb.leftCtrlKey.isPressed) dir -= Vector3.up;
                float speed = Settings.NoClipSpeed * (kb.leftShiftKey.isPressed ? 2.5f : 1f);
                p.transform.position += dir.normalized * speed * Time.deltaTime;
                p.fallValue = 0f;
                p.fallValueUncapped = 0f;
            }
        }

        private void ApplyWorld()
        {
            if (!IsHost || TimeOfDay.Instance == null) return;
            TimeOfDay.Instance.globalTimeSpeedMultiplier = Settings.TimeSpeed;
        }

        private void RefreshEnemyTypes()
        {
            enemyTypes = Resources.FindObjectsOfTypeAll<EnemyType>()
                .Where(e => e != null && e.enemyPrefab != null)
                .GroupBy(e => e.enemyName).Select(g => g.First())
                .OrderBy(e => e.enemyName).ToList();
        }

        private void OnGUI()
        {
            if (!open) return;
            GUI.backgroundColor = new Color(0.1f, 0.1f, 0.1f, 0.95f);
            GUI.skin.label.richText = true;
            GUI.skin.toggle.richText = true;
            window = GUI.Window(0x4C50, window, DrawWindow, $"Overstretched  (F8)   {(IsHost ? "HOST" : "CLIENT")}");
        }

        private void DrawWindow(int id)
        {
            tab = GUILayout.Toolbar(tab, Tabs);
            scroll = GUILayout.BeginScrollView(scroll);
            switch (tab)
            {
                case 0: DrawPlayer(); break;
                case 1: DrawEconomy(); break;
                case 2: DrawWorld(); break;
                case 3: DrawMonsters(); break;
                case 4: DrawStretched(); break;
            }
            GUILayout.EndScrollView();
            if (!string.IsNullOrEmpty(status)) GUILayout.Label(status);
            GUI.DragWindow();
        }

        private void DrawPlayer()
        {
            Settings.SpeedMultiplier = Slider("Move speed", Settings.SpeedMultiplier, 0.25f, 5f);
            Settings.JumpMultiplier = Slider("Jump height", Settings.JumpMultiplier, 0.25f, 5f);
            Settings.InfiniteSprint = GUILayout.Toggle(Settings.InfiniteSprint, " Infinite sprint");
            Settings.GodMode = GUILayout.Toggle(Settings.GodMode, " God mode");
            Settings.NoFallDamage = GUILayout.Toggle(Settings.NoFallDamage, " No fall damage");
            Settings.NoClip = GUILayout.Toggle(Settings.NoClip, " Noclip (WASD, Space up, Ctrl down, Shift fast)");
            if (Settings.NoClip) Settings.NoClipSpeed = Slider("Noclip speed", Settings.NoClipSpeed, 2f, 50f);

            var p = Local;
            if (p != null && GUILayout.Button("Heal to full"))
            {
                p.health = 100;
                p.criticallyInjured = false;
                p.bleedingHeavily = false;
            }
            if (p != null && GUILayout.Button("Teleport to ship"))
            {
                p.TeleportPlayer(StartOfRound.Instance.playerSpawnPositions[0].position);
                p.isInsideFactory = false;
                p.isInHangarShipRoom = true;
                p.isInElevator = true;
            }
        }

        private void DrawEconomy()
        {
            var terminal = Object.FindObjectOfType<Terminal>();
            var time = TimeOfDay.Instance;
            GUILayout.Label($"Credits: {(terminal != null ? terminal.groupCredits.ToString() : "-")}");
            GUILayout.BeginHorizontal();
            creditsInput = GUILayout.TextField(creditsInput, GUILayout.Width(120));
            if (GUILayout.Button("Set credits") && terminal != null && int.TryParse(creditsInput, out int credits))
            {
                terminal.groupCredits = credits;
                terminal.SyncGroupCreditsServerRpc(credits, terminal.numberOfItemsInDropship);
                status = $"Credits set to {credits}";
            }
            GUILayout.EndHorizontal();

            if (time == null) return;
            GUILayout.Space(8);
            GUILayout.Label($"Quota: {time.quotaFulfilled} / {time.profitQuota}   Days left: {time.daysUntilDeadline}");
            if (!HostOnly()) return;
            GUILayout.BeginHorizontal();
            quotaInput = GUILayout.TextField(quotaInput, GUILayout.Width(120));
            if (GUILayout.Button("Set quota") && int.TryParse(quotaInput, out int quota))
            {
                time.profitQuota = quota;
                time.SyncNewProfitQuotaClientRpc(quota, 0, time.timesFulfilledQuota);
                status = $"Quota set to {quota}";
            }
            GUILayout.EndHorizontal();
            if (GUILayout.Button("Mark quota as met"))
            {
                time.quotaFulfilled = time.profitQuota;
                status = "Quota marked as met";
            }

            GUILayout.Space(8);
            Settings.ScrapValueMultiplier = Slider("Scrap value (next moon)", Settings.ScrapValueMultiplier, 0.1f, 10f);
            Settings.ScrapAmountMultiplier = Slider("Scrap amount (next moon)", Settings.ScrapAmountMultiplier, 0.1f, 5f);
        }

        private void DrawWorld()
        {
            if (!HostOnly()) return;
            Settings.TimeSpeed = Slider("Time speed", Settings.TimeSpeed, 0f, 10f);
            Settings.FreezeTime = GUILayout.Toggle(Settings.FreezeTime, " Freeze clock");

            var rm = RoundManager.Instance;
            if (rm == null) return;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Lights ON")) rm.SwitchPower(true);
            if (GUILayout.Button("Lights OFF")) rm.SwitchPower(false);
            if (GUILayout.Button("Flicker")) rm.FlickerLights(true, false);
            GUILayout.EndHorizontal();

            var level = StartOfRound.Instance != null ? StartOfRound.Instance.currentLevel : null;
            if (level == null) return;
            GUILayout.Label($"Weather on {level.PlanetName}: {level.currentWeather} (applies when you land)");
            GUILayout.BeginHorizontal();
            foreach (LevelWeatherType w in System.Enum.GetValues(typeof(LevelWeatherType)))
                if (GUILayout.Button(w.ToString())) level.currentWeather = w;
            GUILayout.EndHorizontal();
        }

        // ------------------------------------------------------------------ Monsters tab

        private static readonly string[] MonsterViews = { "Spawned now", "Spawn table" };
        private static readonly string[] SpawnLists = { "Inside", "Outside", "Daytime" };
        private int monsterView, spawnList, tableLevel = -1;
        private bool showAddList;
        private readonly Dictionary<SpawnableEnemyWithRarity, int> savedRarity = new Dictionary<SpawnableEnemyWithRarity, int>();

        private void DrawMonsters()
        {
            if (!HostOnly()) return;
            monsterView = GUILayout.Toolbar(monsterView, MonsterViews);
            GUILayout.Space(4);
            if (monsterView == 0) DrawSpawnedMonsters();
            else DrawSpawnTable();
        }

        private void DrawSpawnedMonsters()
        {
            var rm = RoundManager.Instance;
            if (rm == null) { GUILayout.Label("Not in a game."); return; }
            Settings.FrozenEnemies.RemoveWhere(e => e == null);
            var alive = rm.SpawnedEnemies.Where(e => e != null).ToList();

            bool freezeAll = GUILayout.Toggle(Settings.FreezeEnemies, " Freeze ALL monsters");
            if (freezeAll && !Settings.FreezeEnemies) foreach (var e in alive) TakeControl(e);
            Settings.FreezeEnemies = freezeAll;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button($"Kill all ({alive.Count(e => !e.isEnemyDead && e.enemyType.canDie)})"))
            {
                foreach (var e in alive) KillMonster(e);
                status = "Killed every monster that can die";
            }
            if (GUILayout.Button($"Remove all ({alive.Count})"))
            {
                foreach (var e in alive) RemoveMonster(e);
                status = "Removed every monster";
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            if (alive.Count == 0) GUILayout.Label("No monsters in the level right now.");
            var me = Local;
            foreach (var e in alive.OrderBy(e => me != null ? Vector3.Distance(me.transform.position, e.transform.position) : 0f))
            {
                bool frozen = Settings.IsFrozen(e);
                string state = e.isEnemyDead ? "dead"
                    : e.currentBehaviourState != null && !string.IsNullOrEmpty(e.currentBehaviourState.name)
                        ? e.currentBehaviourState.name : $"state {e.currentBehaviourStateIndex}";
                float dist = me != null ? Vector3.Distance(me.transform.position, e.transform.position) : 0f;
                GUILayout.Label($"<b>{e.enemyType.enemyName}</b>  [{state}]  {(e.isOutside ? "outside" : "inside")}  {dist:0}m" +
                                (frozen ? "  <color=cyan>FROZEN</color>" : ""));
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(Settings.FrozenEnemies.Contains(e) ? "Unfreeze" : "Freeze"))
                {
                    if (!Settings.FrozenEnemies.Remove(e)) { TakeControl(e); Settings.FrozenEnemies.Add(e); }
                }
                if (me != null && !e.isEnemyDead && GUILayout.Button("Bring here")) StartCoroutine(BringMonster(e));
                if (me != null && GUILayout.Button("Go to it")) GoToMonster(e);
                if (!e.isEnemyDead && e.enemyType.canDie && GUILayout.Button("Kill")) KillMonster(e);
                if (GUILayout.Button("Remove")) RemoveMonster(e);
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(8);
            GUILayout.Label("Spawn one in front of you:");
            for (int i = 0; i < enemyTypes.Count; i += 3)
            {
                GUILayout.BeginHorizontal();
                for (int j = i; j < Mathf.Min(i + 3, enemyTypes.Count); j++)
                    if (GUILayout.Button(enemyTypes[j].enemyName)) SpawnEnemy(enemyTypes[j]);
                GUILayout.EndHorizontal();
            }
        }

        /// <summary>Monsters chasing a client are run by that client; pull the AI back to the host so host controls apply.</summary>
        private static void TakeControl(EnemyAI e)
        {
            if (e != null && !e.isEnemyDead && !e.IsOwner) e.ChangeOwnershipOfEnemy(NetworkManager.ServerClientId);
        }

        private void KillMonster(EnemyAI e)
        {
            if (e == null || e.isEnemyDead || !e.enemyType.canDie) return;
            e.KillEnemyClientRpc(false); // host: runs the death on every client, whoever owns the AI
        }

        private void RemoveMonster(EnemyAI e)
        {
            if (e == null) return;
            Settings.FrozenEnemies.Remove(e);
            if (e.thisNetworkObject != null && e.thisNetworkObject.IsSpawned) e.thisNetworkObject.Despawn();
        }

        private System.Collections.IEnumerator BringMonster(EnemyAI e)
        {
            var me = Local;
            var cam = me.gameplayCamera.transform;
            var flat = Vector3.ProjectOnPlane(cam.forward, Vector3.up).normalized;
            if (!UnityEngine.AI.NavMesh.SamplePosition(me.transform.position + flat * 4f, out var hit, 6f, UnityEngine.AI.NavMesh.AllAreas))
            {
                status = "No floor for it to stand on there";
                yield break;
            }
            if (!e.IsOwner)
            {
                // Only the owner moves an enemy's agent: take it back from the client running it.
                e.ChangeOwnershipOfEnemy(NetworkManager.ServerClientId);
                yield return new WaitForSeconds(0.4f);
            }
            if (e == null) yield break;
            if (e.isOutside == me.isInsideFactory) e.SetEnemyOutside(!me.isInsideFactory);
            if (e.agent != null && e.agent.enabled) e.agent.Warp(hit.position);
            else e.transform.position = hit.position;
            e.serverPosition = hit.position;
            e.SyncPositionToClients();
            status = $"Brought {e.enemyType.enemyName} to you";
        }

        private void GoToMonster(EnemyAI e)
        {
            var me = Local;
            var back = e.transform.position - e.transform.forward * 3f;
            var spot = UnityEngine.AI.NavMesh.SamplePosition(back, out var hit, 4f, UnityEngine.AI.NavMesh.AllAreas) ? hit.position : e.transform.position;
            me.TeleportPlayer(spot);
            me.isInsideFactory = !e.isOutside;
            me.isInHangarShipRoom = false;
            me.isInElevator = false;
            status = $"Teleported to {e.enemyType.enemyName}";
        }

        private void DrawSpawnTable()
        {
            var levels = StartOfRound.Instance != null ? StartOfRound.Instance.levels : null;
            if (levels == null || levels.Length == 0) { GUILayout.Label("Not in a game."); return; }
            int routed = Mathf.Max(0, System.Array.IndexOf(levels, StartOfRound.Instance.currentLevel));
            int index = tableLevel >= 0 && tableLevel < levels.Length ? tableLevel : routed;
            var level = levels[index];

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("<", GUILayout.Width(30))) tableLevel = (index - 1 + levels.Length) % levels.Length;
            GUILayout.Label($"<b>{level.PlanetName}</b>{(index == routed ? "  (current moon)" : "")}");
            if (GUILayout.Button(">", GUILayout.Width(30))) tableLevel = (index + 1) % levels.Length;
            GUILayout.EndHorizontal();
            Settings.EnemyPowerMultiplier = Slider("Spawn cap, all moons (applies from the next landing)", Settings.EnemyPowerMultiplier, 0f, 5f);

            spawnList = GUILayout.Toolbar(spawnList, SpawnLists);
            var list = spawnList == 0 ? level.Enemies : spawnList == 1 ? level.OutsideEnemies : level.DaytimeEnemies;
            GUILayout.Label("Changes apply to every spawn from now on, including the rest of today.");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("All off"))
                foreach (var entry in list.Where(x => x.rarity > 0)) { savedRarity[entry] = entry.rarity; entry.rarity = 0; }
            if (GUILayout.Button("All on"))
                foreach (var entry in list.Where(x => x.rarity == 0)) entry.rarity = RestoredRarity(entry);
            GUILayout.EndHorizontal();

            int total = list.Where(x => x.enemyType != null).Sum(x => x.rarity);
            if (list.Count == 0) GUILayout.Label("Nothing can spawn here.");
            foreach (var entry in list.ToList())
            {
                if (entry.enemyType == null) continue;
                GUILayout.BeginHorizontal();
                bool on = entry.rarity > 0;
                bool nowOn = GUILayout.Toggle(on, " " + entry.enemyType.enemyName, GUILayout.Width(170));
                if (nowOn != on)
                {
                    if (!nowOn) { savedRarity[entry] = entry.rarity; entry.rarity = 0; }
                    else entry.rarity = RestoredRarity(entry);
                }
                GUILayout.Label(total > 0 ? $"{entry.rarity * 100f / total:0}%" : "0%", GUILayout.Width(38));
                entry.rarity = Mathf.RoundToInt(GUILayout.HorizontalSlider(entry.rarity, 0, 300, GUILayout.Width(150)));
                GUILayout.Label(entry.rarity.ToString(), GUILayout.Width(30));
                if (GUILayout.Button("X", GUILayout.Width(24))) list.Remove(entry);
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(6);
            showAddList = GUILayout.Toggle(showAddList, $" Add a monster to {level.PlanetName}'s {SpawnLists[spawnList].ToLower()} list");
            if (!showAddList) return;
            var candidates = enemyTypes
                .Where(t => list.All(x => x.enemyType != t))
                .Where(t => spawnList == 0 ? !t.isOutsideEnemy : spawnList == 1 ? t.isOutsideEnemy && !t.isDaytimeEnemy : t.isDaytimeEnemy)
                .ToList();
            if (candidates.Count == 0) GUILayout.Label("Every suitable monster is already on this list.");
            for (int i = 0; i < candidates.Count; i += 3)
            {
                GUILayout.BeginHorizontal();
                for (int j = i; j < Mathf.Min(i + 3, candidates.Count); j++)
                    if (GUILayout.Button(candidates[j].enemyName))
                    {
                        list.Add(new SpawnableEnemyWithRarity(candidates[j], 25));
                        status = $"{candidates[j].enemyName} can now spawn on {level.PlanetName}";
                    }
                GUILayout.EndHorizontal();
            }
        }

        // Hooks for the automated self-test.
        internal void DebugShow(int showTab, int view, int list)
        {
            if (showTab < 0) { if (open) SetOpen(false); return; }
            if (!open) SetOpen(true);
            tab = showTab;
            monsterView = view;
            spawnList = list;
            showAddList = true;
        }
        internal void DebugBring(EnemyAI e) => StartCoroutine(BringMonster(e));
        internal void DebugGoTo(EnemyAI e) => GoToMonster(e);
        internal void DebugRemove(EnemyAI e) => RemoveMonster(e);
        internal string DebugStatus => status;

        private int RestoredRarity(SpawnableEnemyWithRarity entry) =>
            savedRarity.TryGetValue(entry, out int r) && r > 0 ? r : 25;

        private readonly List<GameObject> previews = new List<GameObject>();
        private float previewSpeed = 1f;
        private string previewClip = "idle";

        private void DrawStretched()
        {
            var live = FindObjectsOfType<StretchedAI>();
            GUILayout.Label($"The Stretched (live): {live.Length}");
            foreach (var ai in live)
            {
                var marked = ai.MarkedIndex >= 0 && ai.MarkedIndex < StartOfRound.Instance.allPlayerScripts.Length
                    ? StartOfRound.Instance.allPlayerScripts[ai.MarkedIndex].playerUsername : "-";
                GUILayout.Label($"  {ai.CurrentState}  mode {ai.Mode}  target {(ai.Target != null ? ai.Target.playerUsername : "-")}  " +
                                $"marked {marked}  escapes {ai.Escapes}");
            }
            if (IsHost && StretchedRegistry.Type != null && Local != null && GUILayout.Button("Spawn The Stretched (real AI) in front of me"))
                SpawnEnemy(StretchedRegistry.Type);

            GUILayout.Space(6);
            GUILayout.Label("Model preview (only you see it, no AI)");
            if (Local != null && GUILayout.Button("Spawn The Stretched in front of me"))
                SpawnPreview();
            previews.RemoveAll(p => p == null);
            if (previews.Count > 0 && GUILayout.Button($"Remove previews ({previews.Count})"))
            {
                foreach (var p in previews)
                    if (p != null) Destroy(p);
                previews.Clear();
            }

            if (previews.Count > 0 && StretchedAnimations.Clips != null)
            {
                GUILayout.Space(6);
                GUILayout.Label($"Animations (playing: {previewClip})");
                float speed = Slider("Speed", previewSpeed, 0.1f, 2f);
                if (!Mathf.Approximately(speed, previewSpeed))
                {
                    previewSpeed = speed;
                    foreach (var p in previews) p.GetComponent<StretchedAnimator>().Speed = speed;
                }
                var names = StretchedAnimations.Clips.Keys.OrderBy(n => n).ToList();
                for (int i = 0; i < names.Count; i += 3)
                {
                    GUILayout.BeginHorizontal();
                    for (int j = i; j < Mathf.Min(i + 3, names.Count); j++)
                    {
                        var clip = StretchedAnimations.Clips[names[j]];
                        string label = names[j].Replace('_', ' ') + (clip.Loop ? " ↻" : "") + (clip.Ceiling ? " ▲" : "");
                        if (GUILayout.Button(label)) PlayOnPreviews(names[j]);
                    }
                    GUILayout.EndHorizontal();
                }
                GUILayout.Label("↻ loops   ▲ plays upside down on the ceiling above it");
            }

            GUILayout.Space(8);
            GUILayout.Label("Behaviour settings");
            if (!HostOnly()) return;
            bool enabled = GUILayout.Toggle(Settings.StretchedEnabled, " Can spawn naturally (all moons)");
            int rarity = (int)Slider("Spawn weight (all moons)", Settings.StretchedRarity, 0, 300);
            if (enabled != Settings.StretchedEnabled || rarity != Settings.StretchedRarity)
            {
                Settings.StretchedEnabled = enabled;
                Settings.StretchedRarity = rarity;
                StretchedRegistry.SyncSpawnLists(overwrite: true);
            }
            Settings.StretchedSilenceRadius = Slider("Silence radius (m)", Settings.StretchedSilenceRadius, 5f, 40f);
            Settings.StretchedAggression = Slider("Aggression", Settings.StretchedAggression, 0.25f, 3f);
            Settings.StretchedSpeed = Slider("Speed", Settings.StretchedSpeed, 0.25f, 3f);
        }

        private void SpawnPreview()
        {
            var cam = Local.gameplayCamera.transform;
            var flat = Vector3.ProjectOnPlane(cam.forward, Vector3.up).normalized;
            var origin = Local.transform.position + flat * 3.5f + Vector3.up;
            var pos = Physics.Raycast(origin, Vector3.down, out var hit, 10f, StartOfRound.Instance.collidersAndRoomMaskAndDefault)
                ? hit.point
                : Local.transform.position + flat * 3.5f;
            try
            {
                var rot = Quaternion.LookRotation(-flat, Vector3.up);
                var preview = StretchedModel.Instantiate(pos, rot);
                var placement = preview.AddComponent<PreviewPlacement>();
                placement.Init(pos, rot);
                previews.Add(preview);
                if (previewClip != "idle") PlayOnPreviews(previewClip);
                status = "Spawned The Stretched preview";
            }
            catch (System.Exception e)
            {
                status = "Preview failed: " + e.Message;
                Plugin.Log.LogError(e);
            }
        }

        private void SpawnEnemy(EnemyType type)
        {
            var p = Local;
            var rm = RoundManager.Instance;
            if (p == null || rm == null) return;
            var cam = p.gameplayCamera.transform;
            var pos = Physics.Raycast(cam.position, cam.forward, out var hit, 20f, StartOfRound.Instance.collidersAndRoomMaskAndDefault)
                ? hit.point - cam.forward * 0.5f
                : cam.position + cam.forward * 6f;
            pos = RoundManager.Instance.GetNavMeshPosition(pos, default, 10f);
            rm.SpawnEnemyGameObject(pos, p.transform.eulerAngles.y + 180f, -1, type);
            status = $"Spawned {type.enemyName}";
        }

        private void PlayOnPreviews(string clipName)
        {
            var clip = StretchedAnimations.Get(clipName);
            if (clip == null) return;
            previewClip = clipName;
            foreach (var p in previews)
            {
                if (p == null) continue;
                p.GetComponent<PreviewPlacement>().SetCeiling(clip.Ceiling);
                var anim = p.GetComponent<StretchedAnimator>();
                anim.ReturnClip = clip.Ceiling ? "ceiling_hang" : "idle";
                anim.Play(clipName, 0.15f, previewSpeed);
            }
            status = $"Playing {clipName}";
        }

        private bool HostOnly()
        {
            if (IsHost) return true;
            GUILayout.Label("Only the host can change these.");
            return false;
        }

        private static float Slider(string label, float value, float min, float max)
        {
            GUILayout.Label($"{label}: {value:0.##}");
            return GUILayout.HorizontalSlider(value, min, max);
        }
    }
}
