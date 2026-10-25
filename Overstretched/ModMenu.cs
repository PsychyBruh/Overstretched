using System.Collections.Generic;
using System.Linq;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using static Overstretched.UiKit;

namespace Overstretched
{
    public partial class ModMenu : MonoBehaviour
    {
        private static readonly (string icon, string name, string blurb)[] Tabs =
        {
            ("☻", "Player", "Movement, survival and cheats for you"),
            ("◉", "Vision", "Night vision and see-through-walls ESP"),
            ("➜", "Teleport", "Jump to the ship, exits, players or loot"),
            ("✚", "Crew", "Revive, heal and move your friends"),
            ("◆", "Items", "Spawn any item and haul scrap"),
            ("☠", "Monsters", "Spawn, freeze, tame and resize monsters"),
            ("☀", "World", "Time, lights, doors, traps and weather"),
            ("✈", "Ship", "Lever, free moons, upgrades and store"),
            ("$", "Economy", "Credits, quota and scrap value"),
            ("⚡", "Chaos", "Hordes, blackouts, earthquakes and more"),
            ("⚠", "The Stretched", "The new monster: live status, preview, tuning"),
            ("▤", "Mods", "Every mod in the pack: credits, on/off and settings"),
            ("⚙", "Settings", "Menu size, look and saved settings"),
        };

        internal const int MonstersTab = 5;
        internal static int TabCount => Tabs.Length;
        internal const int ViewLive = 0, ViewSpawn = 1, ViewTable = 2;

        private const float SidebarWidth = 196f, HeaderHeight = 66f;

        private bool open;
        private float openAnim;
        private int tab;
        private Rect window = new Rect(60, 50, 920, 660);
        private readonly Vector2[] scrolls = new Vector2[Tabs.Length];
        private bool resizing;
        private string toast = "";
        private float toastTime = -10f;
        private string creditsInput = "1000", quotaInput = "130";
        private List<EnemyType> enemyTypes = new List<EnemyType>();
        private GUISkin skin;

        public static ModMenu Instance { get; private set; } // hidden object: FindObjectOfType can't see it
        internal static bool IsOpen => Instance != null && Instance.open;

        private static PlayerControllerB Local => Features.Local;
        private static bool IsHost => Features.IsHost;

        private void Awake()
        {
            Instance = this;
            Features.Notify = Toast;
        }

        private void Toast(string message)
        {
            toast = message;
            toastTime = Time.unscaledTime;
        }

        private bool waitingForKey;

        private static string KeyName => System.Enum.IsDefined(typeof(Key), Settings.MenuKey) ? ((Key)Settings.MenuKey).ToString() : "F8";

        private void Update()
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            if (waitingForKey)
            {
                // Rebinding the menu key: take the next key pressed (Escape cancels).
                foreach (var k in kb.allKeys)
                    if (k.wasPressedThisFrame)
                    {
                        if (k.keyCode != Key.Escape) { Settings.MenuKey = (int)k.keyCode; Toast($"Menu key is now {k.keyCode}"); }
                        waitingForKey = false;
                        return;
                    }
                return;
            }
            var key = System.Enum.IsDefined(typeof(Key), Settings.MenuKey) && Settings.MenuKey != (int)Key.None ? (Key)Settings.MenuKey : Key.F8;
            if (kb[key].wasPressedThisFrame) SetOpen(!open);
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
            else Settings.Save();
        }

        private void RefreshEnemyTypes()
        {
            // Every enemy registered with the network (all of them, whatever moon you're on) plus anything
            // else loaded in memory, e.g. modded enemies.
            var fromPrefabs = NetworkManager.Singleton != null
                ? NetworkManager.Singleton.NetworkConfig.Prefabs.Prefabs
                    .Select(p => p.Prefab != null ? p.Prefab.GetComponent<EnemyAI>() : null)
                    .Where(ai => ai != null && ai.enemyType != null)
                    .Select(ai => ai.enemyType)
                : Enumerable.Empty<EnemyType>();
            enemyTypes = fromPrefabs.Concat(Resources.FindObjectsOfTypeAll<EnemyType>())
                .Where(e => e != null && e.enemyPrefab != null)
                .GroupBy(e => e.enemyName).Select(g => g.First())
                .OrderBy(DisplayName).ToList();
        }

        private static string DisplayName(EnemyType t) => Features.DisplayName(t);

        /// <summary>
        /// Enemies like the Old Bird only exist attached to a nest: without one the game deletes them on the host
        /// and leaves them invisible and dead for everyone else. Make a nest at the spot first, synced to clients.
        /// </summary>
        private static void SpawnNestFor(EnemyType type, Vector3 position)
        {
            if (!type.requireNestObjectsToSpawn || type.nestSpawnPrefab == null) return;
            var rm = RoundManager.Instance;
            var nest = Instantiate(type.nestSpawnPrefab, position, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f));
            var netObj = nest.GetComponentInChildren<NetworkObject>();
            if (netObj != null) netObj.Spawn(destroyWithScene: true);
            var nestScript = nest.GetComponent<EnemyAINestSpawnObject>();
            if (nestScript == null) return;
            rm.enemyNestSpawnObjects.RemoveAll(n => n == null);
            rm.enemyNestSpawnObjects.Add(nestScript);
            type.nestsSpawned++;
            rm.SyncNestSpawnObjectsOrderServerRpc(rm.enemyNestSpawnObjects
                .Select(n => new NetworkObjectReference(n.GetComponentInChildren<NetworkObject>())).ToArray());
        }

        // ================================================================== window

        private void OnGUI()
        {
            openAnim = Mathf.MoveTowards(openAnim, open ? 1f : 0f, Time.unscaledDeltaTime * 6f);
            if (openAnim <= 0f) return;
            Init();
            BuildSkin();

            var oldSkin = GUI.skin;
            var oldMatrix = GUI.matrix;
            var oldColor = GUI.color;
            GUI.skin = skin;
            float scale = Mathf.Clamp(Settings.UiScale, 0.6f, 2f);
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));

            // Keep it on screen whatever the resolution and scale.
            float sw = Screen.width / scale, sh = Screen.height / scale;
            window.width = Mathf.Clamp(window.width, 760f, Mathf.Max(760f, sw - 20f));
            window.height = Mathf.Clamp(window.height, 540f, Mathf.Max(540f, sh - 20f));
            window.x = Mathf.Clamp(window.x, 0f, Mathf.Max(0f, sw - window.width));
            window.y = Mathf.Clamp(window.y, 0f, Mathf.Max(0f, sh - window.height));

            float ease = 1f - Mathf.Pow(1f - openAnim, 3f);
            GUI.color = new Color(1f, 1f, 1f, ease);
            GUI.enabled = open;
            var draw = window;
            draw.y += (1f - ease) * 22f;
            // Soft drop shadow.
            for (int i = 1; i <= 4; i++)
                RoundRect(new Rect(draw.x - i * 3f, draw.y - i * 2f, draw.width + i * 6f, draw.height + i * 6f), new Color(0f, 0f, 0f, 0.07f), 14f + i * 3f);
            var moved = GUI.Window(0x4C50, draw, DrawWindow, GUIContent.none, GUIStyle.none);
            window.position += moved.position - draw.position;

            GUI.enabled = true;
            GUI.color = oldColor;
            GUI.matrix = oldMatrix;
            GUI.skin = oldSkin;
        }

        private void BuildSkin()
        {
            if (skin != null) return;
            skin = Instantiate(GUI.skin);
            skin.hideFlags = HideFlags.HideAndDontSave;
            var track = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            track.SetPixel(0, 0, new Color(1f, 1f, 1f, 0.04f));
            track.Apply();
            var thumb = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            thumb.SetPixel(0, 0, Alpha(Faint, 0.9f));
            thumb.Apply();
            skin.verticalScrollbar = new GUIStyle { normal = { background = track }, fixedWidth = 6f, margin = new RectOffset(6, 0, 0, 0) };
            skin.verticalScrollbarThumb = new GUIStyle { normal = { background = thumb }, fixedWidth = 6f };
            skin.verticalScrollbarUpButton = new GUIStyle();
            skin.verticalScrollbarDownButton = new GUIStyle();
            skin.horizontalScrollbar = new GUIStyle();
            skin.horizontalScrollbarThumb = new GUIStyle();
            skin.horizontalScrollbarLeftButton = new GUIStyle();
            skin.horizontalScrollbarRightButton = new GUIStyle();
            skin.scrollView = new GUIStyle();
            skin.label = new GUIStyle(UiKit.Label);
            skin.textField = new GUIStyle(UiKit.Input);
            skin.settings.cursorColor = Accent;
            skin.settings.selectionColor = Alpha(Accent, 0.4f);
        }

        private void DrawWindow(int id)
        {
            float w = window.width, h = window.height;
            RoundRect(new Rect(0, 0, w, h), Alpha(Bg, Settings.UiOpacity), 14f);
            // Sidebar: rounded on the left only.
            RoundRect(new Rect(0, 0, SidebarWidth, h), Alpha(UiKit.Sidebar, Settings.UiOpacity), 14f);
            Fill(new Rect(SidebarWidth - 14f, 0, 14f, h), Alpha(UiKit.Sidebar, Settings.UiOpacity));
            Fill(new Rect(SidebarWidth, 0, 1f, h), Line);
            RoundOutline(new Rect(0, 0, w, h), Hex(0x262A35), 14f);

            DrawSidebar(h);

            // Content header.
            var info = Tabs[tab];
            GUI.Label(new Rect(SidebarWidth + 22f, 14f, 400f, 28f), info.name, Title);
            GUI.Label(new Rect(SidebarWidth + 22f, 40f, 520f, 18f), info.blurb, LabelSmall);
            var roleRect = new Rect(w - 150f, 20f, 82f, 24f);
            BadgeAt(roleRect, IsHost ? "HOST" : NetworkManager.Singleton != null && NetworkManager.Singleton.IsClient ? "CLIENT" : "OFFLINE",
                    IsHost ? Green : Blue);
            if (ButtonAt(new Rect(w - 56f, 18f, 34f, 28f), "✕", Kind.Ghost)) SetOpen(false);
            Fill(new Rect(SidebarWidth + 1f, HeaderHeight, w - SidebarWidth - 1f, 1f), Line);

            var content = new Rect(SidebarWidth + 18f, HeaderHeight + 14f, w - SidebarWidth - 32f, h - HeaderHeight - 24f);
            UiKit.ContentWidth = content.width - 12f - 28f;
            GUILayout.BeginArea(content);
            try { DrawTab(); }
            catch (System.Exception e)
            {
                // A layout mismatch would otherwise spam errors every frame; show it and carry on.
                if (Event.current.type == EventType.Repaint) Plugin.Log.LogError($"Menu tab {Tabs[tab].name}: {e}");
            }
            GUILayout.EndArea();

            DrawToast(content);
            DrawResizeGrip(w, h);
            GUI.DragWindow(new Rect(0, 0, w, HeaderHeight));
        }

        private void DrawSidebar(float h)
        {
            // Logo.
            GUI.Label(new Rect(20f, 16f, 170f, 26f), $"<color={Rich(Accent)}>OVER</color>STRETCHED", Title);
            GUI.Label(new Rect(21f, 41f, 170f, 16f), $"v{Plugin.Version}  ·  {KeyName} to toggle", LabelSmall);

            float y = 78f;
            float rowH = Mathf.Clamp((h - 78f - 56f) / Tabs.Length, 30f, 38f);
            float sel = Anim(0x51DE, tab, 16f);
            RoundRect(new Rect(10f, y + sel * rowH + 2f, SidebarWidth - 20f, rowH - 4f), Alpha(Accent, 0.14f), 8f);
            RoundRect(new Rect(10f, y + sel * rowH + 8f, 3f, rowH - 16f), Accent, 1.5f);
            for (int i = 0; i < Tabs.Length; i++)
            {
                var r = new Rect(10f, y + i * rowH, SidebarWidth - 20f, rowH);
                bool hover = r.Contains(Event.current.mousePosition);
                float hv = Anim(0x7AB0 + i, hover && i != tab ? 1f : 0f);
                RoundRect(new Rect(r.x, r.y + 2f, r.width, r.height - 4f), Alpha(Color.white, 0.04f * hv), 8f);
                var color = i == tab ? Color.white : Color.Lerp(Dim, Text, hv);
                var iconStyle = i == tab ? LabelBold : UiKit.Label;
                var old = iconStyle.normal.textColor;
                iconStyle.normal.textColor = i == tab ? Accent : color;
                GUI.Label(new Rect(r.x + 14f + hv * 2f, r.y, 22f, r.height), Tabs[i].icon, iconStyle);
                iconStyle.normal.textColor = color;
                GUI.Label(new Rect(r.x + 40f + hv * 2f, r.y, r.width - 44f, r.height), Tabs[i].name, iconStyle);
                iconStyle.normal.textColor = old;
                if (GUI.Button(r, GUIContent.none, GUIStyle.none)) tab = i;
            }

            var me = Local;
            string who = me != null ? me.playerUsername : "Not in a game";
            GUI.Label(new Rect(20f, h - 44f, SidebarWidth - 30f, 18f), who, LabelBold);
            string where = me == null ? "Main menu" : me.isPlayerDead ? "Dead" : me.isInHangarShipRoom ? "On the ship" : me.isInsideFactory ? "Inside the facility" : "Outside";
            GUI.Label(new Rect(20f, h - 26f, SidebarWidth - 30f, 16f), where, LabelSmall);
        }

        private void DrawToast(Rect content)
        {
            float age = Time.unscaledTime - toastTime;
            if (string.IsNullOrEmpty(toast) || age > 3.5f) return;
            float a = Mathf.Clamp01(age * 8f) * Mathf.Clamp01((3.5f - age) * 2f);
            var size = UiKit.Label.CalcSize(new GUIContent(toast));
            float width = Mathf.Min(size.x + 46f, content.width);
            var r = new Rect(content.x + (content.width - width) / 2f, content.yMax - 44f + (1f - a) * 12f, width, 36f);
            RoundRect(new Rect(r.x, r.y + 3f, r.width, r.height), new Color(0f, 0f, 0f, 0.35f * a), 10f);
            RoundRect(r, Alpha(Hex(0x232733), a), 10f);
            RoundOutline(r, Alpha(Accent, 0.7f * a), 10f);
            RoundRect(new Rect(r.x + 12f, r.center.y - 4f, 8f, 8f), Alpha(Accent, a), 4f);
            var old = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, a * old.a);
            GUI.Label(new Rect(r.x + 28f, r.y, r.width - 34f, r.height), toast, UiKit.Label);
            GUI.color = old;
        }

        private void DrawResizeGrip(float w, float h)
        {
            var grip = new Rect(w - 20f, h - 20f, 18f, 18f);
            var e = Event.current;
            bool hover = grip.Contains(e.mousePosition);
            for (int i = 0; i < 3; i++)
                RoundRect(new Rect(w - 8f - i * 5f, h - 8f, 3f, 3f), hover || resizing ? Accent : Faint, 1.5f);
            for (int i = 0; i < 2; i++)
                RoundRect(new Rect(w - 8f - i * 5f, h - 13f, 3f, 3f), hover || resizing ? Accent : Faint, 1.5f);
            RoundRect(new Rect(w - 8f, h - 18f, 3f, 3f), hover || resizing ? Accent : Faint, 1.5f);
            if (e.type == EventType.MouseDown && hover) { resizing = true; e.Use(); }
            else if (e.type == EventType.MouseDrag && resizing) { window.width += e.delta.x; window.height += e.delta.y; e.Use(); }
            else if (e.rawType == EventType.MouseUp) resizing = false;
        }

        private void DrawTab()
        {
            switch (tab)
            {
                case 0: Scroll(DrawPlayer); break;
                case 1: Scroll(DrawVision); break;
                case 2: Scroll(DrawTeleport); break;
                case 3: Scroll(DrawCrew); break;
                case 4: DrawItems(); break;
                case 5: DrawMonsters(); break;
                case 6: Scroll(DrawWorld); break;
                case 7: Scroll(DrawShip); break;
                case 8: Scroll(DrawEconomy); break;
                case 9: Scroll(DrawChaos); break;
                case 10: Scroll(DrawStretched); break;
                case 11: DrawMods(); break;
                case 12: Scroll(DrawSettings); break;
            }
        }

        private void Scroll(System.Action body)
        {
            scrolls[tab] = GUILayout.BeginScrollView(scrolls[tab]);
            body();
            GUILayout.Space(50); // room for the toast
            GUILayout.EndScrollView();
        }

        private static bool HostGate(string what = "Only the host can use these.")
        {
            if (IsHost) return true;
            Callout(what, Yellow);
            return false;
        }

        private static bool InGameGate()
        {
            if (Local != null) return true;
            Callout("Join or host a game first.", Blue);
            return false;
        }

        private static void Row(System.Action body)
        {
            GUILayout.BeginHorizontal();
            body();
            GUILayout.EndHorizontal();
        }

        // ================================================================== Player

        private void DrawPlayer()
        {
            BeginCard("Movement", null, Accent);
            Settings.SpeedMultiplier = Slider("Move speed", Settings.SpeedMultiplier, 0.25f, 5f);
            Settings.JumpMultiplier = Slider("Jump height", Settings.JumpMultiplier, 0.25f, 5f);
            Settings.NoClip = Switch("Noclip", Settings.NoClip, "Fly through walls: WASD, Space up, Ctrl down, Shift faster");
            if (Settings.NoClip) Settings.NoClipSpeed = Slider("Noclip speed", Settings.NoClipSpeed, 2f, 50f, "0", " m/s");
            EndCard();

            BeginCard("Survival", null, Green);
            Settings.GodMode = Switch("God mode", Settings.GodMode, "Nothing can hurt or kill you", Green);
            Settings.NoFallDamage = Switch("No fall damage", Settings.NoFallDamage, "Land from any height", Green);
            Settings.Invisible = Switch("Invisible to monsters", Settings.Invisible, "They can't see, chase or touch you (the host needs the mod)", Green);
            EndCard();

            BeginCard("Endless", null, Cyan);
            Settings.InfiniteSprint = Switch("Infinite stamina", Settings.InfiniteSprint, "Sprint forever", Cyan);
            Settings.InfiniteBattery = Switch("Infinite battery", Settings.InfiniteBattery, "Flashlights, walkies, jetpacks... never run out", Cyan);
            Settings.InfiniteItemUses = Switch("Infinite item uses", Settings.InfiniteItemUses, "Spray paint, TZP and shotgun shells never run out", Cyan);
            Settings.SuperStrength = Switch("Super strength", Settings.SuperStrength, "Carry anything with no weight slowdown", Cyan);
            EndCard();

            var p = Local;
            if (p == null) return;
            BeginCard("Quick actions");
            Row(() =>
            {
                if (Button("Heal to full", Kind.Good))
                {
                    p.health = 100;
                    p.criticallyInjured = false;
                    p.bleedingHeavily = false;
                    HUDManager.Instance.UpdateHealthUI(100, hurtPlayer: false);
                    Toast("Healed to full");
                }
                if (Button("Teleport to ship")) Features.TeleportToShip();
            });
            EndCard();
        }

        // ================================================================== Vision

        private void DrawVision()
        {
            BeginCard("Night vision", null, Green);
            Settings.NightVision = Switch("Night vision", Settings.NightVision, "Light up dark rooms around you (only you see it)", Green);
            if (Settings.NightVision) Settings.NightVisionStrength = Slider("Brightness", Settings.NightVisionStrength, 0f, 1f, "0%", "");
            EndCard();

            BeginCard("ESP", "see through walls", Red);
            Settings.MonsterEsp = Switch("Monsters", Settings.MonsterEsp, "Name and distance of every monster", Red);
            Settings.PlayerEsp = Switch("Players", Settings.PlayerEsp, "Your crew with their health", Blue);
            Settings.ScrapEsp = Switch("Scrap", Settings.ScrapEsp, "Loot and its value", Yellow);
            if (Settings.ScrapEsp) Settings.ScrapEspMinValue = (int)Slider("Only scrap worth at least", Settings.ScrapEspMinValue, 0, 150, "0", " $");
            Settings.TrapEsp = Switch("Traps", Settings.TrapEsp, "Mines, turrets and spike traps (grey = disabled)", Orange);
            Settings.EspRange = Slider("Range", Settings.EspRange, 10f, 400f, "0", " m");
            GUILayout.Space(4);
            Row(() =>
            {
                Badge("monster", Red); GUILayout.Space(4); Badge("frozen", Cyan); GUILayout.Space(4); Badge("pet", Green);
                GUILayout.Space(4); Badge("player", Blue); GUILayout.Space(4); Badge("scrap", Yellow); GUILayout.Space(4); Badge("trap", Orange);
                GUILayout.FlexibleSpace();
            });
            EndCard();
        }

        // ================================================================== Teleport

        private float nextPlaceScan;
        private List<EntranceTeleport> entrances = new List<EntranceTeleport>();
        private List<GrabbableObject> scrapList = new List<GrabbableObject>();

        private void ScanPlaces()
        {
            if (Time.unscaledTime < nextPlaceScan) return;
            nextPlaceScan = Time.unscaledTime + 1f;
            entrances = FindObjectsOfType<EntranceTeleport>().Where(e => e.entrancePoint != null)
                .OrderBy(e => e.entranceId).ThenBy(e => !e.isEntranceToBuilding).ToList();
            scrapList = FindObjectsOfType<GrabbableObject>()
                .Where(g => g.itemProperties != null && g.itemProperties.isScrap && !g.isHeld && !g.isInShipRoom && !(g is RagdollGrabbableObject))
                .ToList();
        }

        private void DrawTeleport()
        {
            if (!InGameGate()) return;
            ScanPlaces();
            var me = Local;

            BeginCard("Places", null, Accent);
            if (Button("✈  Ship", Kind.Accent, GUILayout.Height(36))) Features.TeleportToShip();
            if (entrances.Count == 0) Note("Exits show up once you've landed.");
            else
            {
                int pick = ButtonGrid(entrances.Select(Features.EntranceName).ToList(), 2);
                if (pick >= 0) Features.TeleportToEntrance(entrances[pick]);
            }
            EndCard();

            BeginCard("Players", null, Blue);
            var others = StartOfRound.Instance.allPlayerScripts.Where(p => p != me && p.isPlayerControlled && !p.isPlayerDead).ToList();
            if (others.Count == 0) Note("Nobody else is alive in the game.");
            foreach (var p in others)
                Row(() =>
                {
                    GUILayout.Label(p.playerUsername, UiKit.Label, GUILayout.Width(UiKit.ContentWidth * 0.4f));
                    GUILayout.Label($"{Vector3.Distance(me.transform.position, p.transform.position):0} m", LabelSmall, GUILayout.Width(60));
                    GUILayout.FlexibleSpace();
                    if (Button("Go to", Kind.Normal, GUILayout.Width(90), GUILayout.Height(28))) Features.TeleportToPlayer(p);
                    if (IsHost && Button("Bring here", Kind.Ghost, GUILayout.Width(100), GUILayout.Height(28))) Features.BringPlayer(p);
                });
            EndCard();

            BeginCard("Scrap", $"{scrapList.Count} loose · ${scrapList.Sum(s => s.scrapValue)}", Yellow);
            if (scrapList.Count == 0) Note("No loose scrap around.");
            foreach (var s in scrapList.Where(s => s != null).OrderByDescending(s => s.scrapValue).Take(25))
                Row(() =>
                {
                    GUILayout.Label(s.itemProperties.itemName, UiKit.Label, GUILayout.Width(UiKit.ContentWidth * 0.42f));
                    GUILayout.Label($"<color={Rich(Gold)}>${s.scrapValue}</color>", UiKit.Label, GUILayout.Width(60));
                    GUILayout.Label($"{Vector3.Distance(me.transform.position, s.transform.position):0} m", LabelSmall, GUILayout.Width(60));
                    GUILayout.FlexibleSpace();
                    if (Button("Go to", Kind.Normal, GUILayout.Width(90), GUILayout.Height(28)))
                        Features.TeleportToObject(s.transform, s.isInFactory, s.itemProperties.itemName);
                });
            EndCard();
        }

        // ================================================================== Crew

        private void DrawCrew()
        {
            if (!InGameGate()) return;
            var me = Local;
            var players = StartOfRound.Instance.allPlayerScripts.Where(p => p.isPlayerControlled || p.isPlayerDead).ToList();

            if (HostGate("Only the host can revive, heal or move other players. You can still teleport to them."))
            {
                BeginCard("Everyone", null, Green);
                Row(() =>
                {
                    int dead = players.Count(p => p.isPlayerDead);
                    if (Button(dead > 0 ? $"✚  Revive all dead ({dead})" : "✚  Revive all dead", Kind.Accent, GUILayout.Height(38))) Features.ReviveAll();
                    if (Button("Heal everyone", Kind.Good, GUILayout.Height(38))) Features.Heal(null);
                });
                Note("Revived players wake up on the ship, like at the end of a day.");
                EndCard();
            }

            BeginCard("Crew", $"{players.Count(p => !p.isPlayerDead)} alive · {players.Count(p => p.isPlayerDead)} dead", Blue);
            foreach (var p in players)
            {
                GUILayout.BeginHorizontal();
                GUILayout.BeginVertical(GUILayout.Width(UiKit.ContentWidth * 0.45f));
                GUILayout.Label(p.playerUsername + (p == me ? $"  <color={Rich(Dim)}>(you)</color>" : ""), LabelBold);
                string where = p.isPlayerDead ? $"<color={Rich(Red)}>dead</color>"
                    : p.isInHangarShipRoom ? "on the ship" : p.isInsideFactory ? "inside" : "outside";
                GUILayout.Label(where, LabelSmall);
                GUILayout.EndVertical();
                // Health bar.
                var bar = GUILayoutUtility.GetRect(90f, 34f, GUILayout.Width(90f));
                var track = new Rect(bar.x, bar.center.y - 4f, bar.width, 8f);
                RoundRect(track, Hex(0x2B2F3B), 4f);
                float hp = p.isPlayerDead ? 0f : Mathf.Clamp01(p.health / 100f);
                RoundRect(new Rect(track.x, track.y, Mathf.Max(8f, track.width * hp), track.height), hp > 0.5f ? Green : hp > 0.2f ? Yellow : Red, 4f);
                GUILayout.Space(8);
                GUILayout.Label(p.isPlayerDead ? "" : p.health + "hp", LabelSmall, GUILayout.Width(44));
                GUILayout.FlexibleSpace();
                if (p != me && !p.isPlayerDead && Button("Go to", Kind.Normal, GUILayout.Width(80), GUILayout.Height(28))) Features.TeleportToPlayer(p);
                if (IsHost && p != me && !p.isPlayerDead && Button("Bring", Kind.Ghost, GUILayout.Width(80), GUILayout.Height(28))) Features.BringPlayer(p);
                if (IsHost && !p.isPlayerDead && Button("Heal", Kind.Good, GUILayout.Width(70), GUILayout.Height(28))) Features.Heal(p);
                GUILayout.EndHorizontal();
                GUILayout.Space(4);
            }
            EndCard();
        }

        // ================================================================== Items

        private static readonly string[] ItemFilters = { "All", "Scrap", "Tools" };
        private int itemFilter;
        private string itemSearch = "", itemValue = "";
        private List<Item> itemCache;

        private void DrawItems()
        {
            if (!InGameGate()) return;
            if (!HostGate("Only the host can spawn or move items.")) return;

            // Pinned: scrap hauling + search, so the item list scrolls on its own below.
            ScanPlaces();
            BeginCard("Scrap", $"{scrapList.Count} loose · ${scrapList.Sum(s => s != null ? s.scrapValue : 0)}", Yellow);
            if (Button("◆  Bring ALL scrap to the ship", Kind.Accent, GUILayout.Height(38))) Features.BringScrapToShip();
            EndCard();

            BeginCard("Spawn an item", "appears in front of you", Accent);
            Row(() =>
            {
                itemSearch = TextField(itemSearch, "Search items...", GUILayout.Width(UiKit.ContentWidth * 0.55f));
                GUILayout.Space(6);
                itemValue = TextField(itemValue, "Value (blank = normal)", GUILayout.ExpandWidth(true));
            });
            GUILayout.Space(6);
            itemFilter = Segmented(itemFilter, ItemFilters, 28f);
            EndCard();

            if (itemCache == null || itemCache.Count == 0) itemCache = Features.AllItems();
            var shown = itemCache.Where(i => itemFilter == 0 || (itemFilter == 1) == i.isScrap)
                .Where(i => string.IsNullOrEmpty(itemSearch) || i.itemName.IndexOf(itemSearch, System.StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
            scrolls[tab] = GUILayout.BeginScrollView(scrolls[tab]);
            BeginCard(null);
            if (shown.Count == 0) Note("No items match.");
            int pick = ButtonGrid(shown.Select(i => i.itemName).ToList(), 3);
            if (pick >= 0)
                Features.SpawnItem(shown[pick], int.TryParse(itemValue, out int v) ? Mathf.Max(0, v) : -1);
            EndCard();
            GUILayout.Space(50);
            GUILayout.EndScrollView();
        }

        // ================================================================== Monsters

        private static readonly string[] SpawnLists = { "Inside", "Outside", "Daytime" };
        private int monsterView, spawnList, tableLevel = -1;
        private bool showAddList;
        private string monsterSearch = "";
        private int spawnCount = 1;
        private readonly HashSet<string> expanded = new HashSet<string>();
        private readonly Dictionary<SpawnableEnemyWithRarity, int> savedRarity = new Dictionary<SpawnableEnemyWithRarity, int>();
        private float nextSpamBatch;
        private int spawnedByAll;

        private void DrawMonsters()
        {
            if (!HostGate("Only the host can control monsters.")) return;
            var rm = RoundManager.Instance;
            if (rm == null || Local == null) { Callout("Join or host a game first.", Blue); return; }
            Settings.FrozenEnemies.RemoveWhere(e => e == null);
            var alive = rm.SpawnedEnemies.Where(e => e != null).ToList();

            // Pinned toolbar: stays put however long the list below gets, so spamming never needs scrolling.
            BeginCard();
            var big = GUILayoutUtility.GetRect(10f, 54f, GUILayout.ExpandWidth(true));
            float half = (big.width - 10f) / 2f;
            if (ButtonAt(new Rect(big.x, big.y, half, big.height), "☠  SPAWN ALL", Kind.Accent, ButtonTextBig)) SpawnAll();
            // Held: a batch every few hundred ms for as long as the button is down.
            if (HoldButton(new Rect(big.x + half + 10f, big.y, half, big.height), "HOLD TO SPAM", Kind.Ghost, out _)
                && Time.unscaledTime >= nextSpamBatch)
            {
                nextSpamBatch = Time.unscaledTime + Settings.SpamInterval;
                SpawnAll();
            }
            GUILayout.Space(8);
            GUILayout.BeginHorizontal();
            Badge($"{alive.Count} alive", alive.Count > 60 ? Red : alive.Count > 25 ? Yellow : Green);
            GUILayout.Space(6);
            Badge($"{spawnedByAll} from Spawn ALL", Dim);
            GUILayout.FlexibleSpace();
            bool freezeAll = Settings.FreezeEnemies;
            if (Button(freezeAll ? "❄ Unfreeze all" : "❄ Freeze all", freezeAll ? Kind.Accent : Kind.Normal, GUILayout.Width(120), GUILayout.Height(28)))
            {
                if (!freezeAll) foreach (var e in alive) Features.TakeControl(e);
                Settings.FreezeEnemies = !freezeAll;
            }
            if (Button("Kill all", Kind.Danger, GUILayout.Width(80), GUILayout.Height(28)))
            {
                foreach (var e in alive) KillMonster(e);
                Toast("Killed every monster that can die");
            }
            if (Button("Remove all", Kind.Danger, GUILayout.Width(100), GUILayout.Height(28)))
            {
                foreach (var e in alive) RemoveMonster(e);
                Toast("Removed every monster");
            }
            GUILayout.EndHorizontal();
            EndCard();

            monsterView = Segmented(monsterView, new[] { $"Live ({alive.Count})", "Spawn one", "Spawn table" });

            scrolls[tab] = GUILayout.BeginScrollView(scrolls[tab]);
            if (monsterView == ViewLive) DrawLiveMonsters(alive);
            else if (monsterView == ViewSpawn) DrawSpawnOne();
            else DrawSpawnTable();
            GUILayout.Space(50);
            GUILayout.EndScrollView();
        }

        private void DrawLiveMonsters(List<EnemyAI> alive)
        {
            if (alive.Count == 0) { Callout("No monsters in the level right now. Hit SPAWN ALL above.", Dim); return; }
            var me = Local;
            float Dist(EnemyAI e) => Vector3.Distance(me.transform.position, e.transform.position);

            foreach (var group in alive.GroupBy(e => DisplayName(e.enemyType)).OrderBy(g => g.Min(Dist)))
            {
                var list = group.OrderBy(Dist).ToList();
                bool isOpen = expanded.Contains(group.Key);
                bool allFrozen = list.All(Settings.IsFrozen);
                BeginCard();
                GUILayout.BeginHorizontal();
                var head = GUILayoutUtility.GetRect(10f, 30f, GUILayout.ExpandWidth(true));
                float chevron = Anim(group.Key.GetHashCode(), isOpen ? 1f : 0f);
                GUI.Label(new Rect(head.x, head.y, 20f, head.height), chevron > 0.5f ? "▾" : "▸", LabelBold);
                GUI.Label(new Rect(head.x + 20f, head.y, head.width - 20f, head.height),
                    $"{group.Key}  <color={Rich(Accent)}>×{list.Count}</color>   <size=12><color={Rich(Dim)}>nearest {Dist(list[0]):0} m</color></size>", LabelBold);
                if (GUI.Button(head, GUIContent.none, GUIStyle.none))
                {
                    if (!expanded.Remove(group.Key)) expanded.Add(group.Key);
                }
                if (Button(allFrozen ? "Unfreeze" : "Freeze", allFrozen ? Kind.Accent : Kind.Normal, GUILayout.Width(88), GUILayout.Height(28)))
                    foreach (var e in list)
                    {
                        if (allFrozen) Settings.FrozenEnemies.Remove(e);
                        else { Features.TakeControl(e); Settings.FrozenEnemies.Add(e); }
                    }
                if (Button("Bring", Kind.Normal, GUILayout.Width(70), GUILayout.Height(28)))
                    foreach (var e in list.Where(x => !x.isEnemyDead)) StartCoroutine(BringMonster(e));
                if (list.Any(e => e.enemyType.canDie && !e.isEnemyDead) && Button("Kill", Kind.Danger, GUILayout.Width(56), GUILayout.Height(28)))
                    foreach (var e in list) KillMonster(e);
                if (Button("Remove", Kind.Danger, GUILayout.Width(80), GUILayout.Height(28)))
                    foreach (var e in list) RemoveMonster(e);
                GUILayout.EndHorizontal();

                if (isOpen)
                    foreach (var e in list) DrawMonsterRow(e, Dist(e));
                EndCard();
            }
        }

        private static readonly float[] Sizes = { 0.25f, 0.5f, 1f, 2f, 3f, 5f };

        private void DrawMonsterRow(EnemyAI e, float dist)
        {
            GUILayout.Space(4);
            Fill(GUILayoutUtility.GetRect(10f, 1f, GUILayout.ExpandWidth(true)), Line);
            GUILayout.Space(4);
            bool frozen = Settings.IsFrozen(e);
            bool pet = Features.IsPet(e);
            string state = e.isEnemyDead ? "dead"
                : e.currentBehaviourState != null && !string.IsNullOrEmpty(e.currentBehaviourState.name)
                    ? e.currentBehaviourState.name : $"state {e.currentBehaviourStateIndex}";

            GUILayout.BeginHorizontal();
            GUILayout.Label($"{dist:0} m", LabelBold, GUILayout.Width(50));
            GUILayout.Label($"{state} · {(e.isOutside ? "outside" : "inside")}", LabelSmall, GUILayout.Width(150));
            if (frozen) { Badge("FROZEN", Cyan); GUILayout.Space(4); }
            if (pet) { Badge("PET", Green); GUILayout.Space(4); }
            float size = Features.SizeOf(e);
            if (!Mathf.Approximately(size, 1f)) Badge($"{size:0.##}x", Purple);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (Button(Settings.FrozenEnemies.Contains(e) ? "Unfreeze" : "Freeze", Kind.Normal, GUILayout.Height(26)))
            {
                if (!Settings.FrozenEnemies.Remove(e)) { Features.TakeControl(e); Settings.FrozenEnemies.Add(e); }
            }
            if (!e.isEnemyDead && Button(pet ? "Unpet" : "♥ Pet", pet ? Kind.Good : Kind.Normal, GUILayout.Height(26))) Features.SetPet(e, !pet);
            if (!e.isEnemyDead && Button("Bring here", Kind.Normal, GUILayout.Height(26))) StartCoroutine(BringMonster(e));
            if (Button("Go to it", Kind.Normal, GUILayout.Height(26))) GoToMonster(e);
            if (!e.isEnemyDead && e.enemyType.canDie && Button("Kill", Kind.Danger, GUILayout.Height(26))) KillMonster(e);
            if (Button("Remove", Kind.Danger, GUILayout.Height(26))) RemoveMonster(e);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Size", LabelSmall, GUILayout.Width(40));
            foreach (var s in Sizes)
                if (Button(s + "x", Mathf.Approximately(size, s) ? Kind.Accent : Kind.Ghost, GUILayout.Height(24), GUILayout.Width(52)))
                {
                    Features.SetSize(e, s);
                    Toast($"{DisplayName(e.enemyType)} is now {s}x size");
                }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        private void DrawSpawnOne()
        {
            BeginCard("Spawn in front of you", null, Accent);
            monsterSearch = TextField(monsterSearch, "Search monsters...");
            GUILayout.Space(6);
            spawnCount = Mathf.RoundToInt(Slider("How many per click", spawnCount, 1, 20, "0", ""));
            GUILayout.Space(4);
            var shown = enemyTypes.Where(t => string.IsNullOrEmpty(monsterSearch) ||
                                              DisplayName(t).IndexOf(monsterSearch, System.StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            int pick = ButtonGrid(shown.Select(t => DisplayName(t) + (Features.IsModded(t) ? " ★" : "")).ToList(), 3);
            if (pick >= 0) for (int i = 0; i < spawnCount; i++) SpawnEnemy(shown[pick], i);
            if (shown.Any(Features.IsModded)) Note("★ added by another mod in the pack.");
            EndCard();
        }

        /// <summary>Monsters chasing a client are run by that client; pull the AI back to the host so host controls apply.</summary>
        private void KillMonster(EnemyAI e)
        {
            if (e == null || e.isEnemyDead || !e.enemyType.canDie) return;
            e.KillEnemyClientRpc(false); // host: runs the death on every client, whoever owns the AI
        }

        private void RemoveMonster(EnemyAI e)
        {
            if (e == null) return;
            Settings.FrozenEnemies.Remove(e);
            Features.SetPet(e, false);
            Compat.BeforeDespawn(e);
            if (e.thisNetworkObject != null && e.thisNetworkObject.IsSpawned) e.thisNetworkObject.Despawn();
        }

        private System.Collections.IEnumerator BringMonster(EnemyAI e)
        {
            var me = Local;
            var cam = me.gameplayCamera.transform;
            var flat = Vector3.ProjectOnPlane(cam.forward, Vector3.up).normalized;
            var around = me.transform.position + flat * 4f + Quaternion.Euler(0f, Random.Range(-40f, 40f), 0f) * flat * Random.Range(0f, 2f);
            if (!UnityEngine.AI.NavMesh.SamplePosition(around, out var hit, 6f, UnityEngine.AI.NavMesh.AllAreas))
            {
                Toast("No floor for it to stand on there");
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
            e.SyncPositionToClients(); // sends only when it moved from the last synced spot, so don't touch serverPosition first
            Toast($"Brought {DisplayName(e.enemyType)} to you");
        }

        private void GoToMonster(EnemyAI e)
        {
            var back = e.transform.position - e.transform.forward * 3f;
            var spot = UnityEngine.AI.NavMesh.SamplePosition(back, out var hit, 4f, UnityEngine.AI.NavMesh.AllAreas) ? hit.position : e.transform.position;
            Features.TeleportLocal(spot, !e.isOutside);
            Toast($"Teleported to {DisplayName(e.enemyType)}");
        }

        private void DrawSpawnTable()
        {
            var levels = StartOfRound.Instance != null ? StartOfRound.Instance.levels : null;
            if (levels == null || levels.Length == 0) { Callout("Not in a game.", Blue); return; }
            int routed = Mathf.Max(0, System.Array.IndexOf(levels, StartOfRound.Instance.currentLevel));
            int index = tableLevel >= 0 && tableLevel < levels.Length ? tableLevel : routed;
            var level = levels[index];

            BeginCard();
            GUILayout.BeginHorizontal();
            if (Button("◀", Kind.Normal, GUILayout.Width(40), GUILayout.Height(30))) tableLevel = (index - 1 + levels.Length) % levels.Length;
            GUILayout.FlexibleSpace();
            GUILayout.Label($"{level.PlanetName}{(index == routed ? $"  <color={Rich(Accent)}>· current moon</color>" : "")}", LabelBold);
            GUILayout.FlexibleSpace();
            if (Button("▶", Kind.Normal, GUILayout.Width(40), GUILayout.Height(30))) tableLevel = (index + 1) % levels.Length;
            GUILayout.EndHorizontal();
            GUILayout.Space(6);
            Settings.EnemyPowerMultiplier = Slider("Spawn cap, all moons (from the next landing)", Settings.EnemyPowerMultiplier, 0f, 5f);
            spawnList = Segmented(spawnList, SpawnLists, 28f);
            Note("Changes apply to every spawn from now on, including the rest of today.");
            GUILayout.Space(4);
            var list = spawnList == 0 ? level.Enemies : spawnList == 1 ? level.OutsideEnemies : level.DaytimeEnemies;
            GUILayout.BeginHorizontal();
            if (Button("All off", Kind.Normal, GUILayout.Height(28)))
                foreach (var entry in list.Where(x => x.rarity > 0)) { savedRarity[entry] = entry.rarity; entry.rarity = 0; }
            if (Button("All on", Kind.Normal, GUILayout.Height(28)))
                foreach (var entry in list.Where(x => x.rarity == 0)) entry.rarity = RestoredRarity(entry);
            GUILayout.EndHorizontal();
            EndCard();

            BeginCard($"{SpawnLists[spawnList]} spawns", $"{list.Count(x => x.enemyType != null && x.rarity > 0)} active");
            int total = list.Where(x => x.enemyType != null).Sum(x => x.rarity);
            if (list.Count == 0) Note("Nothing can spawn here.");
            foreach (var entry in list.ToList())
            {
                if (entry.enemyType == null) continue;
                bool on = entry.rarity > 0;
                GUILayout.BeginHorizontal();
                bool nowOn = Switch($"{DisplayName(entry.enemyType)}  <color={Rich(Dim)}><size=12>{(total > 0 ? entry.rarity * 100f / total : 0f):0}%</size></color>", on);
                if (Button("✕", Kind.Ghost, GUILayout.Width(32), GUILayout.Height(28))) list.Remove(entry);
                GUILayout.EndHorizontal();
                if (nowOn != on)
                {
                    if (!nowOn) { savedRarity[entry] = entry.rarity; entry.rarity = 0; }
                    else entry.rarity = RestoredRarity(entry);
                }
                if (entry.rarity > 0) entry.rarity = Mathf.RoundToInt(Slider("Weight", entry.rarity, 1, 300, "0", ""));
            }
            EndCard();

            BeginCard($"Add to {level.PlanetName}");
            showAddList = Switch($"Show monsters that can be added to the {SpawnLists[spawnList].ToLower()} list", showAddList);
            if (showAddList)
            {
                var candidates = enemyTypes
                    .Where(t => list.All(x => x.enemyType != t))
                    .Where(t => spawnList == 0 ? !t.isOutsideEnemy : spawnList == 1 ? t.isOutsideEnemy && !t.isDaytimeEnemy : t.isDaytimeEnemy)
                    .ToList();
                if (candidates.Count == 0) Note("Every suitable monster is already on this list.");
                int pick = ButtonGrid(candidates.Select(c => "+ " + DisplayName(c)).ToList(), 3, Kind.Ghost);
                if (pick >= 0)
                {
                    list.Add(new SpawnableEnemyWithRarity(candidates[pick], 25));
                    Toast($"{DisplayName(candidates[pick])} can now spawn on {level.PlanetName}");
                }
            }
            EndCard();
        }

        private int RestoredRarity(SpawnableEnemyWithRarity entry) =>
            savedRarity.TryGetValue(entry, out int r) && r > 0 ? r : 25;

        private void SpawnEnemy(EnemyType type, int index = 0)
        {
            var p = Local;
            var rm = RoundManager.Instance;
            if (p == null || rm == null) return;
            var cam = p.gameplayCamera.transform;
            var pos = Physics.Raycast(cam.position, cam.forward, out var hit, 20f, StartOfRound.Instance.collidersAndRoomMaskAndDefault)
                ? hit.point - cam.forward * 0.5f
                : cam.position + cam.forward * 6f;
            if (index > 0) { var o = Random.insideUnitCircle * 3f; pos += new Vector3(o.x, 0f, o.y); }
            pos = rm.GetNavMeshPosition(pos, default, 10f);
            try
            {
                SpawnNestFor(type, pos);
                rm.SpawnEnemyGameObject(pos, p.transform.eulerAngles.y + 180f, -1, type);
            }
            catch (System.Exception e)
            {
                Toast($"{DisplayName(type)} couldn't spawn here: {e.Message}");
                return;
            }
            Toast(index > 0 ? $"Spawned {index + 1}x {DisplayName(type)}" : $"Spawned {DisplayName(type)}");
        }

        /// <summary>
        /// One of every monster, indoor and outdoor alike, wherever you're standing, scattered in a ring in
        /// front of you so they don't all stack on one spot. Monsters out of their usual area may wander oddly.
        /// </summary>
        private int SpawnAll()
        {
            var p = Local;
            var rm = RoundManager.Instance;
            if (p == null || rm == null) return 0;
            if (enemyTypes.Count == 0) RefreshEnemyTypes();
            var flat = Vector3.ProjectOnPlane(p.gameplayCamera.transform.forward, Vector3.up).normalized;
            var centre = p.transform.position + flat * 7f;
            int count = 0;
            // Only monsters that spawn naturally on some moon: helper/companion ones (Cadaver Bloom, Butler Bees)
            // and cut ones (Lasso, Red pill) break or error when spawned on their own.
            var natural = Features.NaturalTypes();
            for (int batch = 0; batch < Mathf.Max(1, Settings.SpawnAllBatches); batch++)
                foreach (var type in enemyTypes.Where(t => natural.Contains(t)))
                {
                    var offset = Random.insideUnitCircle * 5f;
                    var spot = rm.GetNavMeshPosition(centre + new Vector3(offset.x, 0f, offset.y), default, 10f);
                    try
                    {
                        SpawnNestFor(type, spot);
                        rm.SpawnEnemyGameObject(spot, Random.Range(0f, 360f), -1, type);
                        count++;
                    }
                    catch (System.Exception e) { Plugin.Log.LogWarning($"Spawn ALL skipped {DisplayName(type)}: {e.Message}"); }
                }
            spawnedByAll += count;
            Toast($"Spawned {count} monsters ({spawnedByAll} from Spawn ALL so far)");
            return count;
        }

        // ================================================================== World

        private void DrawWorld()
        {
            if (!HostGate()) return;
            BeginCard("Time", TimeOfDay.Instance != null ? $"day {TimeOfDay.Instance.normalizedTimeOfDay * 100f:0}% done" : null, Yellow);
            Settings.TimeSpeed = Slider("Time speed", Settings.TimeSpeed, 0f, 10f);
            Settings.FreezeTime = Switch("Freeze the clock", Settings.FreezeTime, "The day never ends", Yellow);
            EndCard();

            var rm = RoundManager.Instance;
            if (!Features.InRound) { Callout("Lights, doors and traps work once you've landed.", Blue); }
            else
            {
                BeginCard("Facility power", null, Yellow);
                Row(() =>
                {
                    if (Button("Lights ON", Kind.Good)) { rm.SwitchPower(true); Toast("Power on"); }
                    if (Button("Lights OFF", Kind.Danger)) { rm.SwitchPower(false); Toast("Power off"); }
                    if (Button("Flicker")) rm.FlickerLights(true, false);
                });
                EndCard();

                BeginCard("Doors", "every door in the facility", Blue);
                Row(() =>
                {
                    if (Button("Open all", Kind.Good)) Features.Doors(MenuNet.DoorAction.Open);
                    if (Button("Close all")) Features.Doors(MenuNet.DoorAction.Close);
                    if (Button("Unlock all")) Features.Doors(MenuNet.DoorAction.Unlock);
                    if (Button("Lock all", Kind.Danger)) Features.Doors(MenuNet.DoorAction.Lock);
                });
                EndCard();

                BeginCard("Traps", "mines, turrets, spike traps", Orange);
                Row(() =>
                {
                    if (Button("Disable all traps", Kind.Good)) Features.SetTraps(false);
                    if (Button("Enable all traps", Kind.Danger)) Features.SetTraps(true);
                });
                EndCard();
            }

            var level = StartOfRound.Instance != null ? StartOfRound.Instance.currentLevel : null;
            if (level == null) return;
            string current = Compat.CurrentWeather(level);
            BeginCard("Weather", $"{level.PlanetName}: {current} (applies when you land)", Cyan);
            var weathers = Compat.Weathers();
            for (int i = 0; i < weathers.Count; i += 4)
                Row(() =>
                {
                    for (int j = i; j < Mathf.Min(i + 4, weathers.Count); j++)
                        if (Button(weathers[j].Name, weathers[j].Name == current ? Kind.Accent : Kind.Normal,
                                   GUILayout.Height(30), GUILayout.Width(UiKit.ContentWidth / 4f - 6f)))
                        {
                            try { Compat.SetWeather(level, weathers[j]); Toast($"{level.PlanetName}: {weathers[j].Name}"); }
                            catch (System.Exception e) { Toast("Couldn't change the weather: " + e.Message); }
                        }
                });
            if (Compat.Has(Compat.WeatherRegistry)) Note("Includes custom weathers from WeatherRegistry.");
            EndCard();
        }

        // ================================================================== Ship

        private string unlockSearch = "";

        private void DrawShip()
        {
            if (!InGameGate()) return;
            var sor = StartOfRound.Instance;
            string phase = sor.inShipPhase ? (sor.travellingToNewLevel ? "flying to a moon" : "in orbit")
                : sor.shipIsLeaving ? "taking off" : sor.shipHasLanded ? "landed" : "landing";
            BeginCard("Lever", $"{sor.currentLevel.PlanetName} · {phase}", Accent);
            Row(() =>
            {
                if (Button(sor.inShipPhase ? "✈  Land the ship" : "✈  Leave the moon", Kind.Accent, GUILayout.Height(38))) Features.PullLever();
                if (IsHost && sor.inShipPhase && Button("Force land", Kind.Ghost, GUILayout.Height(38))) Features.ForceLand();
                if (IsHost && sor.shipHasLanded && Button("Force leave", Kind.Danger, GUILayout.Height(38))) Features.ForceLeave();
            });
            Note("Pulls the lever for you, even when you're alone. Force skips the lever checks.");
            EndCard();

            if (!HostGate("Moons, upgrades and store items are host only.")) return;

            var custom = Compat.CustomLevels();
            var allLevels = sor.levels.Where(l => l != null).ToList();
            DrawMoonGrid("Moons", "free routing, in orbit only", allLevels.Where(l => !custom.ContainsKey(l)).ToList(), null);
            var modded = allLevels.Where(custom.ContainsKey).OrderBy(l => l.PlanetName).ToList();
            if (modded.Count > 0) DrawMoonGrid("Modded moons", $"{modded.Count} from the mod pack", modded, custom);

            var terminal = FindObjectOfType<Terminal>();
            if (terminal != null && terminal.buyableItemsList != null)
            {
                BeginCard("Store", "free, spawns in front of you", Green);
                int pick = ButtonGrid(terminal.buyableItemsList.Select(i => i.itemName).ToList(), 3, Kind.Good);
                if (pick >= 0) Features.SpawnItem(terminal.buyableItemsList[pick], -1);
                EndCard();
            }

            BeginCard("Ship upgrades & decor", "free", Purple);
            unlockSearch = TextField(unlockSearch, "Search upgrades, suits, furniture...");
            GUILayout.Space(6);
            var unlockables = sor.unlockablesList.unlockables;
            var missing = Enumerable.Range(0, unlockables.Count)
                .Where(i => unlockables[i] != null && !unlockables[i].hasBeenUnlockedByPlayer && !unlockables[i].alreadyUnlocked)
                .Where(i => string.IsNullOrEmpty(unlockSearch) || unlockables[i].unlockableName.IndexOf(unlockSearch, System.StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
            if (missing.Count == 0) Note("Everything matching is already unlocked.");
            int unlock = ButtonGrid(missing.Select(i => unlockables[i].unlockableName).ToList(), 3);
            if (unlock >= 0) Features.UnlockForFree(missing[unlock]);
            EndCard();
        }

        private static void DrawMoonGrid(string title, string subtitle, List<SelectableLevel> levels, Dictionary<SelectableLevel, string> sources)
        {
            var sor = StartOfRound.Instance;
            BeginCard(title, subtitle, sources == null ? Blue : Purple);
            for (int i = 0; i < levels.Count; i += 3)
                Row(() =>
                {
                    for (int j = i; j < Mathf.Min(i + 3, levels.Count); j++)
                    {
                        var l = levels[j];
                        bool here = l == sor.currentLevel;
                        if (Button(l.PlanetName, here ? Kind.Accent : Kind.Normal, GUILayout.Height(30), GUILayout.Width(UiKit.ContentWidth / 3f - 6f)) && !here)
                            Features.RouteTo(l);
                    }
                });
            EndCard();
        }

        // ================================================================== Economy

        private void DrawEconomy()
        {
            var terminal = FindObjectOfType<Terminal>();
            var time = TimeOfDay.Instance;
            BeginCard("Credits", terminal != null ? $"${terminal.groupCredits}" : "-", Green);
            GUILayout.BeginHorizontal();
            creditsInput = TextField(creditsInput, "Amount", GUILayout.Width(160));
            if (Button("Set credits", Kind.Accent, GUILayout.Height(32)) && terminal != null && int.TryParse(creditsInput, out int credits))
            {
                terminal.groupCredits = credits;
                terminal.SyncGroupCreditsServerRpc(credits, terminal.numberOfItemsInDropship);
                Toast($"Credits set to {credits}");
            }
            foreach (var add in new[] { 100, 1000, 10000 })
                if (Button($"+{add}", Kind.Ghost, GUILayout.Height(32)) && terminal != null)
                {
                    terminal.groupCredits += add;
                    terminal.SyncGroupCreditsServerRpc(terminal.groupCredits, terminal.numberOfItemsInDropship);
                    Toast($"Credits: {terminal.groupCredits}");
                }
            GUILayout.EndHorizontal();
            EndCard();

            if (time == null) return;
            BeginCard("Quota", $"{time.quotaFulfilled} / {time.profitQuota} · {time.daysUntilDeadline} days left", Yellow);
            if (HostGate())
            {
                GUILayout.BeginHorizontal();
                quotaInput = TextField(quotaInput, "Quota", GUILayout.Width(160));
                if (Button("Set quota", Kind.Accent, GUILayout.Height(32)) && int.TryParse(quotaInput, out int quota))
                {
                    time.profitQuota = quota;
                    time.SyncNewProfitQuotaClientRpc(quota, 0, time.timesFulfilledQuota);
                    Toast($"Quota set to {quota}");
                }
                if (Button("Mark quota as met", Kind.Good, GUILayout.Height(32)))
                {
                    time.quotaFulfilled = time.profitQuota;
                    Toast("Quota marked as met");
                }
                GUILayout.EndHorizontal();
            }
            EndCard();

            if (!IsHost) return;
            BeginCard("Scrap on the next moons", null, Gold);
            Settings.ScrapValueMultiplier = Slider("Scrap value", Settings.ScrapValueMultiplier, 0.1f, 10f);
            Settings.ScrapAmountMultiplier = Slider("Scrap amount", Settings.ScrapAmountMultiplier, 0.1f, 5f);
            EndCard();
        }

        // ================================================================== Chaos

        private void DrawChaos()
        {
            if (!HostGate()) return;
            BeginCard();
            if (Button("⚡  RANDOM CHAOS EVENT", Kind.Accent, GUILayout.Height(52))) Features.RandomChaos();
            Note("Picks one of the events below at random.");
            EndCard();

            BeginCard("Events", null, Red);
            Settings.HordeSize = Mathf.RoundToInt(Slider("Horde size", Settings.HordeSize, 2, 40, "0", " monsters"));
            Settings.ChaosSeconds = Slider("Event length", Settings.ChaosSeconds, 5f, 120f, "0", " s");
            GUILayout.Space(4);
            Row(() =>
            {
                if (Button("☠ Horde", Kind.Danger, GUILayout.Height(36))) Features.Horde(Settings.HordeSize);
                if (Button("☾ Lights out", Kind.Normal, GUILayout.Height(36))) Features.LightsOut(Settings.ChaosSeconds);
                if (Button("✦ Scatter players", Kind.Normal, GUILayout.Height(36))) Features.Scatter();
            });
            Row(() =>
            {
                if (Button("☁ Low gravity", Kind.Normal, GUILayout.Height(36))) Features.LowGravity(Settings.ChaosSeconds);
                if (Button("≋ Earthquake", Kind.Normal, GUILayout.Height(36))) Features.Earthquake(8f);
            });
            EndCard();
            Callout("Pets and monster sizes live on each monster in Monsters > Live: open a group to find them.", Dim);
        }

        // ================================================================== The Stretched

        private readonly List<GameObject> previews = new List<GameObject>();
        private float previewSpeed = 1f;
        private string previewClip = "idle";

        private void DrawStretched()
        {
            var live = FindObjectsOfType<StretchedAI>();
            BeginCard("Live", $"{live.Length} in the level", Red);
            if (live.Length == 0) Note("None right now.");
            foreach (var ai in live)
            {
                var players = StartOfRound.Instance.allPlayerScripts;
                var marked = ai.MarkedIndex >= 0 && ai.MarkedIndex < players.Length ? players[ai.MarkedIndex].playerUsername : "-";
                Row(() =>
                {
                    Badge(ai.CurrentState.ToString(), Red); GUILayout.Space(6);
                    GUILayout.Label($"mode {ai.Mode} · target {(ai.Target != null ? ai.Target.playerUsername : "-")} · marked {marked} · escapes {ai.Escapes}", LabelSmall);
                    GUILayout.FlexibleSpace();
                });
            }
            if (IsHost && StretchedRegistry.Type != null && Local != null &&
                Button("Spawn The Stretched (real AI) in front of me", Kind.Accent, GUILayout.Height(36)))
                SpawnEnemy(StretchedRegistry.Type);
            EndCard();

            BeginCard("Model preview", "only you see it, no AI", Purple);
            Row(() =>
            {
                if (Local != null && Button("Spawn a preview")) SpawnPreview();
                previews.RemoveAll(p => p == null);
                if (previews.Count > 0 && Button($"Remove previews ({previews.Count})", Kind.Danger))
                {
                    foreach (var p in previews)
                        if (p != null) Destroy(p);
                    previews.Clear();
                }
            });
            if (previews.Count > 0 && StretchedAnimations.Clips != null)
            {
                GUILayout.Space(6);
                float speed = Slider("Animation speed", previewSpeed, 0.1f, 2f);
                if (!Mathf.Approximately(speed, previewSpeed))
                {
                    previewSpeed = speed;
                    foreach (var p in previews) p.GetComponent<StretchedAnimator>().Speed = speed;
                }
                var names = StretchedAnimations.Clips.Keys.OrderBy(n => n).ToList();
                var labels = names.Select(n =>
                {
                    var clip = StretchedAnimations.Clips[n];
                    return n.Replace('_', ' ') + (clip.Loop ? " ↻" : "") + (clip.Ceiling ? " ▲" : "");
                }).ToList();
                int pick = ButtonGrid(labels, 3, Kind.Normal, 28f);
                if (pick >= 0) PlayOnPreviews(names[pick]);
                Note($"Playing: {previewClip}.  ↻ loops   ▲ plays upside down on the ceiling above it");
            }
            EndCard();

            BeginCard("Behaviour", null, Accent);
            if (HostGate())
            {
                bool enabled = Switch("Can spawn naturally", Settings.StretchedEnabled, "On every moon's indoor list");
                int rarity = Mathf.RoundToInt(Slider("Spawn weight (all moons)", Settings.StretchedRarity, 0, 300, "0", ""));
                if (enabled != Settings.StretchedEnabled || rarity != Settings.StretchedRarity)
                {
                    Settings.StretchedEnabled = enabled;
                    Settings.StretchedRarity = rarity;
                    StretchedRegistry.SyncSpawnLists(overwrite: true);
                }
                Settings.StretchedSilenceRadius = Slider("Silence radius", Settings.StretchedSilenceRadius, 5f, 40f, "0", " m");
                Settings.StretchedAggression = Slider("Aggression", Settings.StretchedAggression, 0.25f, 3f);
                Settings.StretchedSpeed = Slider("Speed", Settings.StretchedSpeed, 0.25f, 3f);
                Settings.StretchedVoiceTheft = Switch("Steals voices", Settings.StretchedVoiceTheft,
                    Compat.Has(Compat.Mirage) ? "Mirage is installed too: turn this off to leave voices to Mirage" : "Replays what players said near it");
            }
            EndCard();
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
                Toast("Spawned The Stretched preview");
            }
            catch (System.Exception e)
            {
                Toast("Preview failed: " + e.Message);
                Plugin.Log.LogError(e);
            }
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
            Toast($"Playing {clipName}");
        }

        // ================================================================== Settings

        private void DrawSettings()
        {
            BeginCard("Look", null, Accent);
            Settings.UiScale = Slider("Menu size", Settings.UiScale, 0.6f, 1.8f, "0%", "");
            Settings.UiOpacity = Slider("Background opacity", Settings.UiOpacity, 0.6f, 1f, "0%", "");
            EndCard();

            BeginCard("Menu key", null, Blue);
            GUILayout.BeginHorizontal();
            GUILayout.Label(waitingForKey ? "Press any key (Esc to cancel)..." : $"Open / close the menu with <b>{KeyName}</b>", UiKit.Label);
            GUILayout.FlexibleSpace();
            if (Button(waitingForKey ? "Waiting..." : "Change", waitingForKey ? Kind.Accent : Kind.Normal, GUILayout.Width(110), GUILayout.Height(30)))
                waitingForKey = !waitingForKey;
            GUILayout.EndHorizontal();
            EndCard();

            BeginCard("Spawn ALL", null, Red);
            Settings.SpawnAllModded = Switch("Include modded monsters", Settings.SpawnAllModded,
                "Monsters from other mods in the pack too (some may misbehave away from their own moons)");
            Settings.SpawnAllBatches = Mathf.RoundToInt(Slider("Copies of every monster per press", Settings.SpawnAllBatches, 1, 5, "0", "x"));
            Settings.SpamInterval = Slider("Hold-to-spam delay", Settings.SpamInterval, 0.05f, 1f, "0.00", " s");
            EndCard();

            BeginCard("Saved settings", null, Green);
            Note("Your toggles and sliders are saved when you close the menu and load on the next launch. " +
                 "Noclip, freeze-all and freeze-clock always start off.");
            GUILayout.Space(6);
            Row(() =>
            {
                if (Button("Save now", Kind.Good)) { Settings.Save(); Toast("Settings saved"); }
                if (Button("Reload saved")) { Settings.Load(); Toast("Settings reloaded"); }
                if (Button("Reset everything", Kind.Danger)) { Settings.ResetToDefaults(); Toast("Everything back to default"); }
            });
            EndCard();

            BeginCard("Keys", null, Blue);
            Note($"{KeyName}  open / close this menu\nDrag the header to move it, the corner dots to resize it.");
            EndCard();
        }

        // ================================================================== hooks for the automated self-test

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
        internal int DebugSpawnAll() => SpawnAll();
        internal void DebugExpandAll() { foreach (var t in enemyTypes) expanded.Add(DisplayName(t)); }
        internal void DebugOpenMod(string id) => openMods.Add(id);
        internal string DebugStatus => toast;
    }
}
