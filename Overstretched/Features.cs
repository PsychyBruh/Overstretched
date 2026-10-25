using System.Collections;
using System.Collections.Generic;
using System.Linq;
using GameNetcodeStuff;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;

namespace Overstretched
{
    /// <summary>
    /// Everything the menu turns on: per-frame player effects, ESP, and the host actions (revives, items,
    /// traps, doors, ship, pets, chaos). The menu only draws controls and calls in here.
    /// </summary>
    public class Features : MonoBehaviour
    {
        public static Features Instance { get; private set; }

        internal static PlayerControllerB Local =>
            StartOfRound.Instance != null ? StartOfRound.Instance.localPlayerController : null;

        internal static bool IsHost => NetworkManager.Singleton != null && NetworkManager.Singleton.IsHost;

        internal static bool InRound => RoundManager.Instance != null && StartOfRound.Instance != null && !StartOfRound.Instance.inShipPhase;

        /// <summary>Set by the menu; the menu reports what happened through this.</summary>
        internal static System.Action<string> Notify = _ => { };

        private void Awake() => Instance = this;

        private void Update()
        {
            MenuNet.EnsureRegistered();
            ApplyPlayer();
            ApplyWorld();
            if (IsHost) UpdatePets();
            if (InRound) VoiceThief.Tick();
        }

        private void LateUpdate() => ApplyNightVision();

        private void OnApplicationQuit() => Settings.Save();

        // ================================================================== names

        // Internal names that don't match what players call them in game.
        private static readonly Dictionary<string, string> FriendlyNames = new Dictionary<string, string>
        {
            { "RadMech", "Old Bird" }, { "Flowerman", "Bracken" }, { "Spring", "Coil-Head" }, { "Crawler", "Thumper" },
            { "Centipede", "Snare Flea" }, { "Puffer", "Spore Lizard" }, { "Blob", "Hygrodere" }, { "Girl", "Ghost Girl" },
            { "MouthDog", "Eyeless Dog" }, { "ForestGiant", "Forest Keeper" }, { "Red Locust Bees", "Circuit Bees" },
            { "Docile Locust Bees", "Roaming Locusts" }, { "Hoarding bug", "Hoarder Bug" }, { "SandSpider", "Bunker Spider" },
            { "ClaySurgeon", "Barber" }, { "Bush Wolf", "Kidnapper Fox" }, { "CaveDweller", "Maneater" },
            { "GiantKiwi", "Giant Sapsucker" }, { "SandWorm", "Earth Leviathan" },
        };

        internal static string DisplayName(EnemyType t) =>
            t == null ? "?" : FriendlyNames.TryGetValue(t.enemyName, out var friendly) ? friendly : t.enemyName;

        // ================================================================== player (local, anyone)

        private PlayerControllerB trackedPlayer;
        private float baseSpeed = -1f, baseJump = -1f;
        private bool strengthWasOn;
        private float lowGravityUntil;
        private int sentInvisible = -1; // last state told to the host: -1 never, 0 off, 1 on

        private static readonly AccessTools.FieldRef<SprayPaintItem, float> SprayTank = AccessTools.FieldRefAccess<SprayPaintItem, float>("sprayCanTank");
        private static readonly AccessTools.FieldRef<SprayPaintItem, float> SprayShake = AccessTools.FieldRefAccess<SprayPaintItem, float>("sprayCanShakeMeter");
        private static readonly AccessTools.FieldRef<TetraChemicalItem, float> TzpFuel = AccessTools.FieldRefAccess<TetraChemicalItem, float>("fuel");
        internal static readonly AccessTools.FieldRef<Landmine, bool> MineActive = AccessTools.FieldRefAccess<Landmine, bool>("mineActivated");

        internal bool LowGravityActive => Time.time < lowGravityUntil;

        private void ApplyPlayer()
        {
            var p = Local;
            if (p == null) return;
            if (p != trackedPlayer)
            {
                trackedPlayer = p;
                baseSpeed = p.movementSpeed;
                baseJump = p.jumpForce;
                sentInvisible = -1;
            }

            bool lowGravity = LowGravityActive;
            p.movementSpeed = baseSpeed * Settings.SpeedMultiplier;
            p.jumpForce = baseJump * Settings.JumpMultiplier * (lowGravity ? 2.2f : 1f);
            if (Settings.InfiniteSprint) { p.sprintMeter = 1f; p.isExhausted = false; }
            if (Settings.GodMode && !p.isPlayerDead) { p.health = 100; p.criticallyInjured = false; p.bleedingHeavily = false; }

            if (Settings.SuperStrength) { p.carryWeight = 1f; strengthWasOn = true; }
            else if (strengthWasOn)
            {
                strengthWasOn = false;
                p.carryWeight = Mathf.Clamp(1f + p.ItemSlots.Where(i => i != null).Sum(i => i.itemProperties.weight - 1f), 1f, 10f);
            }

            if (Settings.InfiniteBattery || Settings.InfiniteItemUses) RefillItems(p);

            p.thisController.enabled = !Settings.NoClip || p.isPlayerDead;
            if (Settings.NoClip && !p.isPlayerDead && !ModMenu.IsOpen && Keyboard.current != null)
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
            else if (lowGravity && !p.isPlayerDead && !p.thisController.isGrounded)
            {
                // Float down slowly; never fast enough to hurt.
                p.fallValue = Mathf.Max(p.fallValue, -4f);
                p.fallValueUncapped = Mathf.Max(p.fallValueUncapped, -4f);
            }

            // Tell the host whenever the invisibility choice changes (the host runs most monsters' AI).
            int invisible = Settings.Invisible ? 1 : 0;
            if (invisible != sentInvisible && NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
            {
                sentInvisible = invisible;
                int index = (int)p.playerClientId;
                bool on = Settings.Invisible;
                MenuNet.ToHost(MenuNet.Msg.Invisible, (ref FastBufferWriter w) => { w.WriteValueSafe(index); w.WriteValueSafe(on); });
            }
        }

        private static void RefillItems(PlayerControllerB p)
        {
            foreach (var item in p.ItemSlots)
            {
                if (item == null) continue;
                if (Settings.InfiniteBattery && item.insertedBattery != null && item.itemProperties.requiresBattery)
                {
                    item.insertedBattery.charge = 1f;
                    item.insertedBattery.empty = false;
                }
                if (!Settings.InfiniteItemUses) continue;
                switch (item)
                {
                    case SprayPaintItem spray: SprayTank(spray) = 1f; SprayShake(spray) = 1f; break;
                    case TetraChemicalItem tzp: TzpFuel(tzp) = 1f; break;
                    case ShotgunItem gun: gun.shellsLoaded = 2; break;
                }
            }
        }

        private Light nightLight;
        private float nightBaseIntensity, nightBaseRange;

        private void ApplyNightVision()
        {
            var p = Local;
            var light = p != null ? p.nightVision : null;
            if (light != nightLight)
            {
                RestoreNightVision();
                nightLight = light;
                if (light != null) { nightBaseIntensity = light.intensity; nightBaseRange = light.range; }
            }
            if (light == null) return;
            if (Settings.NightVision && !p.isPlayerDead)
            {
                light.enabled = true;
                light.intensity = nightBaseIntensity * (1.5f + 8f * Settings.NightVisionStrength);
                light.range = Mathf.Max(nightBaseRange, 12f) * (1.5f + 2.5f * Settings.NightVisionStrength);
            }
            else RestoreNightVision();
        }

        private void RestoreNightVision()
        {
            if (nightLight == null || Mathf.Approximately(nightLight.intensity, nightBaseIntensity)) return;
            nightLight.intensity = nightBaseIntensity;
            nightLight.range = nightBaseRange;
            if (Local != null) nightLight.enabled = Local.isInsideFactory; // what the game itself would show
        }

        private void ApplyWorld()
        {
            if (!IsHost || TimeOfDay.Instance == null) return;
            TimeOfDay.Instance.globalTimeSpeedMultiplier = Settings.TimeSpeed;
        }

        // ================================================================== teleporting

        internal static void TeleportLocal(Vector3 position, bool inside, bool inShip = false)
        {
            var p = Local;
            if (p == null || p.isPlayerDead) return;
            p.TeleportPlayer(position);
            p.isInsideFactory = inside;
            p.isInHangarShipRoom = inShip;
            p.isInElevator = inShip;
            p.fallValue = 0f;
            p.fallValueUncapped = 0f;
        }

        internal static void TeleportToShip()
        {
            if (StartOfRound.Instance == null) return;
            TeleportLocal(StartOfRound.Instance.playerSpawnPositions[0].position, false, inShip: true);
            Notify("Teleported to the ship");
        }

        internal static void TeleportToEntrance(EntranceTeleport e)
        {
            if (e == null || e.entrancePoint == null) return;
            // Entrances that lead into the building stand outside; their partners stand inside.
            TeleportLocal(e.entrancePoint.position, inside: !e.isEntranceToBuilding);
            Notify("Teleported to " + EntranceName(e));
        }

        internal static string EntranceName(EntranceTeleport e) =>
            (e.entranceId == 0 ? "Main entrance" : $"Fire exit {e.entranceId}") + (e.isEntranceToBuilding ? " (outside)" : " (inside)");

        internal static void TeleportToPlayer(PlayerControllerB target)
        {
            if (target == null) return;
            var spot = target.transform.position - target.transform.forward * 1.5f;
            TeleportLocal(spot, target.isInsideFactory, target.isInHangarShipRoom);
            Notify("Teleported to " + target.playerUsername);
        }

        internal static void TeleportToObject(Transform t, bool inside, string name)
        {
            TeleportLocal(t.position + Vector3.up * 0.5f, inside);
            Notify("Teleported to " + name);
        }

        /// <summary>Host: move another player to a spot (their own game does the teleport).</summary>
        internal static void TeleportPlayerTo(PlayerControllerB target, Vector3 position, bool inside)
        {
            if (!IsHost || target == null) return;
            int index = (int)target.playerClientId;
            MenuNet.ToAll(MenuNet.Msg.TeleportPlayer, (ref FastBufferWriter w) =>
            {
                w.WriteValueSafe(index); w.WriteValueSafe(position); w.WriteValueSafe(inside);
            });
        }

        internal static void BringPlayer(PlayerControllerB target)
        {
            var me = Local;
            if (me == null || target == null) return;
            TeleportPlayerTo(target, me.transform.position + me.transform.forward * 1.5f, me.isInsideFactory);
            Notify($"Brought {target.playerUsername} to you");
        }

        // ================================================================== crew (host)

        internal static void ReviveAll()
        {
            if (!IsHost) return;
            MenuNet.ToAll(MenuNet.Msg.Revive);
            Notify("Revived every dead player (they wake up on the ship)");
        }

        internal static void Heal(PlayerControllerB target)
        {
            if (!IsHost) return;
            int index = target != null ? (int)target.playerClientId : -1;
            MenuNet.ToAll(MenuNet.Msg.Heal, (ref FastBufferWriter w) => w.WriteValueSafe(index));
            Notify(target != null ? $"Healed {target.playerUsername}" : "Healed everyone");
        }

        // ================================================================== items (host)

        internal static List<Item> AllItems() =>
            StartOfRound.Instance != null && StartOfRound.Instance.allItemsList != null
                ? StartOfRound.Instance.allItemsList.itemsList.Where(i => i != null && i.spawnPrefab != null)
                    .GroupBy(i => i.itemName).Select(g => g.First()).OrderBy(i => i.itemName).ToList()
                : new List<Item>();

        internal static Vector3 SpotInFront(float distance)
        {
            var p = Local;
            var cam = p.gameplayCamera.transform;
            var pos = Physics.Raycast(cam.position, cam.forward, out var hit, distance, StartOfRound.Instance.collidersAndRoomMaskAndDefault)
                ? hit.point - cam.forward * 0.4f
                : cam.position + cam.forward * distance;
            return Physics.Raycast(pos + Vector3.up * 0.5f, Vector3.down, out var floor, 30f, StartOfRound.Instance.collidersAndRoomMaskAndDefault)
                ? floor.point + Vector3.up * 0.2f
                : pos;
        }

        /// <summary>Host: spawn an item in front of you. value &lt; 0 keeps the item's normal random value.</summary>
        internal static GrabbableObject SpawnItem(Item item, int value)
        {
            var p = Local;
            if (!IsHost || p == null || item == null || item.spawnPrefab == null) return null;
            var rm = RoundManager.Instance;
            var pos = SpotInFront(2.5f);
            var parent = rm != null && rm.spawnedScrapContainer != null && !p.isInHangarShipRoom
                ? rm.spawnedScrapContainer : StartOfRound.Instance.elevatorTransform;
            var go = Instantiate(item.spawnPrefab, pos, Quaternion.identity, parent);
            var grab = go.GetComponent<GrabbableObject>();
            grab.fallTime = 0f;
            grab.isInFactory = p.isInsideFactory;
            grab.isInShipRoom = p.isInHangarShipRoom;
            grab.isInElevator = p.isInHangarShipRoom;
            go.GetComponent<NetworkObject>().Spawn();
            if (item.isScrap || value >= 0)
            {
                int v = value >= 0 ? value
                    : (int)(Random.Range(item.minValue, Mathf.Max(item.minValue, item.maxValue)) * (rm != null ? rm.scrapValueMultiplier : 0.4f));
                grab.SetScrapValue(v);
                if (rm != null)
                    rm.SyncScrapValuesClientRpc(new[] { new NetworkObjectReference(go.GetComponent<NetworkObject>()) }, new[] { v });
            }
            Notify($"Spawned {item.itemName}" + (grab.scrapValue > 0 ? $" (${grab.scrapValue})" : ""));
            return grab;
        }

        internal static List<GrabbableObject> LooseScrap() =>
            FindObjectsOfType<GrabbableObject>()
                .Where(g => g != null && g.itemProperties != null && g.itemProperties.isScrap && !g.isHeld && !g.isHeldByEnemy
                            && !g.isInShipRoom && !(g is RagdollGrabbableObject) && g.NetworkObject != null && g.NetworkObject.IsSpawned)
                .ToList();

        /// <summary>Host: every loose scrap item lands on the ship floor and counts as collected.</summary>
        internal static void BringScrapToShip()
        {
            if (!IsHost || StartOfRound.Instance == null) return;
            var scrap = LooseScrap();
            if (scrap.Count == 0) { Notify("No loose scrap left outside the ship"); return; }
            var bounds = StartOfRound.Instance.shipInnerRoomBounds.bounds;
            var mask = StartOfRound.Instance.collidersAndRoomMaskAndDefault;
            var spots = new List<Vector3>();
            foreach (var _ in scrap)
            {
                var top = new Vector3(Random.Range(bounds.min.x + 1f, bounds.max.x - 1f), bounds.max.y - 0.5f,
                                      Random.Range(bounds.min.z + 1f, bounds.max.z - 1f));
                spots.Add(Physics.Raycast(top, Vector3.down, out var hit, 10f, mask) ? hit.point + Vector3.up * 0.1f : new Vector3(top.x, bounds.min.y + 0.3f, top.z));
            }
            MenuNet.ToAll(MenuNet.Msg.ItemsToShip, (ref FastBufferWriter w) =>
            {
                w.WriteValueSafe(scrap.Count);
                for (int i = 0; i < scrap.Count; i++) { w.WriteValueSafe(scrap[i].NetworkObjectId); w.WriteValueSafe(spots[i]); }
            });
            Notify($"Brought {scrap.Count} scrap to the ship (${scrap.Sum(s => s.scrapValue)})");
        }

        // ================================================================== traps and doors (host)

        internal static void SetTraps(bool on)
        {
            if (!IsHost) return;
            int n = 0;
            foreach (var mine in FindObjectsOfType<Landmine>()) if (!mine.hasExploded) { mine.ToggleMine(on); n++; }
            foreach (var turret in FindObjectsOfType<Turret>()) { turret.ToggleTurretEnabled(on); n++; }
            foreach (var spikes in FindObjectsOfType<SpikeRoofTrap>()) { spikes.ToggleSpikesEnabled(on); n++; }
            Notify($"{(on ? "Enabled" : "Disabled")} {n} traps");
        }

        internal static void Doors(MenuNet.DoorAction action)
        {
            if (!IsHost) return;
            MenuNet.ToAll(MenuNet.Msg.Doors, (ref FastBufferWriter w) => w.WriteValueSafe((byte)action));
            if (action == MenuNet.DoorAction.Open || action == MenuNet.DoorAction.Close)
                foreach (var big in FindObjectsOfType<TerminalAccessibleObject>().Where(t => t.isBigDoor))
                    big.SetDoorOpenServerRpc(action == MenuNet.DoorAction.Open);
            Notify(action switch
            {
                MenuNet.DoorAction.Open => "Opened every door",
                MenuNet.DoorAction.Close => "Closed every door",
                MenuNet.DoorAction.Unlock => "Unlocked every door",
                _ => "Locked every door",
            });
        }

        private static void ApplyDoors(MenuNet.DoorAction action)
        {
            foreach (var door in FindObjectsOfType<DoorLock>())
            {
                var anim = door.GetComponent<AnimatedObjectTrigger>();
                switch (action)
                {
                    case MenuNet.DoorAction.Open:
                        if (door.isLocked) door.UnlockDoor();
                        if (anim != null) anim.SetBoolOnClientOnly(true);
                        door.SetDoorAsOpen(true);
                        break;
                    case MenuNet.DoorAction.Close:
                        if (anim != null) anim.SetBoolOnClientOnly(false);
                        door.SetDoorAsOpen(false);
                        break;
                    case MenuNet.DoorAction.Unlock:
                        if (door.isLocked) door.UnlockDoor();
                        break;
                    case MenuNet.DoorAction.Lock:
                        if (anim != null && anim.boolValue) anim.SetBoolOnClientOnly(false);
                        door.SetDoorAsOpen(false);
                        if (!door.isLocked) door.LockDoor();
                        break;
                }
            }
        }

        // ================================================================== ship (host)

        internal static void PullLever()
        {
            var lever = FindObjectOfType<StartMatchLever>();
            var sor = StartOfRound.Instance;
            if (lever == null || sor == null) return;
            if (sor.inShipPhase)
            {
                if (sor.travellingToNewLevel) { Notify("Still flying to the moon, wait a moment"); return; }
                lever.singlePlayerEnabled = true;
                lever.LeverAnimation();
                Notify("Landing the ship");
            }
            else if (sor.shipHasLanded && !sor.shipIsLeaving)
            {
                lever.LeverAnimation();
                Notify("Taking off");
            }
        }

        internal static void ForceLand()
        {
            var sor = StartOfRound.Instance;
            if (!IsHost || sor == null || !sor.inShipPhase || sor.travellingToNewLevel) return;
            sor.StartGame();
            Notify("Forcing the ship to land");
        }

        internal static void ForceLeave()
        {
            var sor = StartOfRound.Instance;
            if (!IsHost || sor == null || !sor.shipHasLanded || sor.shipIsLeaving || Local == null) return;
            sor.shipIsLeaving = true;
            sor.EndGameServerRpc((int)Local.playerClientId);
            Notify("Forcing the ship to leave");
        }

        internal static void RouteTo(SelectableLevel level)
        {
            var sor = StartOfRound.Instance;
            var terminal = FindObjectOfType<Terminal>();
            if (!IsHost || sor == null || terminal == null || level == null) return;
            if (!sor.inShipPhase || sor.travellingToNewLevel) { Notify("You can only route while in orbit"); return; }
            sor.ChangeLevelServerRpc(level.levelID, terminal.groupCredits);
            Notify($"Routing to {level.PlanetName} for free");
        }

        internal static void UnlockForFree(int index)
        {
            var sor = StartOfRound.Instance;
            var terminal = FindObjectOfType<Terminal>();
            if (!IsHost || sor == null || terminal == null) return;
            var u = sor.unlockablesList.unlockables[index];
            if (u.hasBeenUnlockedByPlayer || u.alreadyUnlocked) { Notify(u.unlockableName + " is already on the ship"); return; }
            sor.BuyShipUnlockableServerRpc(index, terminal.groupCredits);
            Notify($"Unlocked {u.unlockableName} for free");
        }

        // ================================================================== monsters (host)

        private static readonly Dictionary<EnemyAI, PlayerControllerB> pets = new Dictionary<EnemyAI, PlayerControllerB>();
        private static readonly Dictionary<EnemyAI, float> sizes = new Dictionary<EnemyAI, float>();
        private float nextPetSync;
        private static readonly Dictionary<EnemyAI, (Vector3 pos, float time)> petProgress = new Dictionary<EnemyAI, (Vector3, float)>();

        internal static bool IsPet(EnemyAI e) => pets.Count > 0 && e != null && pets.ContainsKey(e);

        internal static void TakeControl(EnemyAI e)
        {
            if (e != null && !e.isEnemyDead && !e.IsOwner) e.ChangeOwnershipOfEnemy(NetworkManager.ServerClientId);
        }

        internal static void SetPet(EnemyAI e, bool pet)
        {
            if (!IsHost || e == null) return;
            if (!pet)
            {
                if (pets.Remove(e)) Notify(DisplayName(e.enemyType) + " is wild again");
                return;
            }
            TakeControl(e);
            Settings.FrozenEnemies.Remove(e);
            pets[e] = Local;
            Notify(DisplayName(e.enemyType) + " is now your pet: it follows you and ignores everyone");
        }

        private void UpdatePets()
        {
            if (pets.Count == 0) return;
            bool sync = Time.time >= nextPetSync;
            if (sync) nextPetSync = Time.time + 0.2f;
            foreach (var pair in pets.ToList())
            {
                var e = pair.Key;
                var owner = pair.Value;
                if (e == null || e.isEnemyDead) { pets.Remove(e); continue; }
                if (!e.IsOwner) { TakeControl(e); continue; }
                var agent = e.agent;
                if (agent == null) continue;
                if (!agent.enabled) agent.enabled = true; // some monsters park their agent (ceiling clingers, etc.)
                if (owner == null || owner.isPlayerDead || !owner.isPlayerControlled)
                {
                    if (agent.isOnNavMesh) agent.isStopped = true;
                    continue;
                }
                var heel = owner.transform.position - owner.transform.forward * 2.5f;
                float dist = Vector3.Distance(e.transform.position, owner.transform.position);
                bool wrongSide = e.isOutside == owner.isInsideFactory;
                if (wrongSide || dist > 35f || !agent.isOnNavMesh)
                {
                    // Lost (other side of a door, too far behind): catch up instantly.
                    if (wrongSide) e.SetEnemyOutside(!owner.isInsideFactory);
                    if (NavMesh.SamplePosition(heel, out var hit, 6f, NavMesh.AllAreas)) agent.Warp(hit.position);
                    continue;
                }
                agent.isStopped = false;
                agent.speed = dist > 10f ? 10f : dist > 4f ? 5.5f : 2.5f;
                if (agent.acceleration < 20f) agent.acceleration = 20f;
                if (sync) agent.SetDestination(heel);
                if (sync) e.SyncPositionToClients();

                // Stuck (no path across a gap, wedged on a prop...): hop to its owner instead.
                if (!petProgress.TryGetValue(e, out var progress) || Vector3.Distance(progress.pos, e.transform.position) > 0.5f)
                    petProgress[e] = (e.transform.position, Time.time);
                else if (dist > 6f && Time.time - progress.time > 1.5f)
                {
                    if (NavMesh.SamplePosition(heel, out var hop, 6f, NavMesh.AllAreas)) agent.Warp(hop.position);
                    petProgress[e] = (e.transform.position, Time.time);
                    e.SyncPositionToClients();
                }
            }
        }

        internal static float SizeOf(EnemyAI e) => e != null && sizes.TryGetValue(e, out float s) ? s : 1f;

        internal static void SetSize(EnemyAI e, float scale)
        {
            if (!IsHost || e == null) return;
            sizes[e] = scale;
            ulong id = e.NetworkObjectId;
            MenuNet.ToAll(MenuNet.Msg.Scale, (ref FastBufferWriter w) => { w.WriteValueSafe(id); w.WriteValueSafe(scale); });
        }

        private static readonly Dictionary<ulong, Vector3> originalScales = new Dictionary<ulong, Vector3>();

        private static void ApplyScale(ulong id, float scale)
        {
            if (!NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(id, out var obj)) return;
            if (!originalScales.TryGetValue(id, out var original)) originalScales[id] = original = obj.transform.localScale;
            obj.transform.localScale = original * scale;
        }

        internal static HashSet<EnemyType> NaturalTypes() => new HashSet<EnemyType>(StartOfRound.Instance.levels
            .SelectMany(l => l.Enemies.Concat(l.OutsideEnemies).Concat(l.DaytimeEnemies))
            .Select(e => e.enemyType).Where(t => t != null && t.enemyPrefab != null));

        // ================================================================== chaos (host)

        internal static void Earthquake(float seconds)
        {
            if (!IsHost) return;
            MenuNet.ToAll(MenuNet.Msg.Quake, (ref FastBufferWriter w) => w.WriteValueSafe(seconds));
            Notify("EARTHQUAKE");
        }

        internal static void Scatter()
        {
            if (!IsHost || !InRound) { Notify("Land on a moon first"); return; }
            MenuNet.ToAll(MenuNet.Msg.Scatter);
            Notify("Scattered everyone across the map");
        }

        internal static void LowGravity(float seconds)
        {
            if (!IsHost) return;
            MenuNet.ToAll(MenuNet.Msg.LowGravity, (ref FastBufferWriter w) => w.WriteValueSafe(seconds));
            Notify($"Low gravity for {seconds:0} seconds");
        }

        internal static void LightsOut(float seconds)
        {
            if (!IsHost || !InRound) { Notify("Land on a moon first"); return; }
            Instance.StartCoroutine(Instance.LightsOutRoutine(seconds));
            Notify($"Lights out for {seconds:0} seconds");
        }

        private IEnumerator LightsOutRoutine(float seconds)
        {
            RoundManager.Instance.FlickerLights(true, true);
            yield return new WaitForSeconds(0.6f);
            if (RoundManager.Instance == null) yield break;
            RoundManager.Instance.SwitchPower(false);
            yield return new WaitForSeconds(seconds);
            if (RoundManager.Instance != null) RoundManager.Instance.SwitchPower(true);
        }

        /// <summary>Host: a pack of random monsters right next to a random living player.</summary>
        internal static void Horde(int count)
        {
            if (!IsHost || !InRound) { Notify("Land on a moon first"); return; }
            var living = StartOfRound.Instance.allPlayerScripts.Where(p => p.isPlayerControlled && !p.isPlayerDead).ToList();
            if (living.Count == 0) return;
            var victim = living[Random.Range(0, living.Count)];
            var types = NaturalTypes().Where(t => !t.requireNestObjectsToSpawn).ToList();
            var rm = RoundManager.Instance;
            for (int i = 0; i < count; i++)
            {
                var ring = Random.insideUnitCircle.normalized * Random.Range(6f, 11f);
                var spot = rm.GetNavMeshPosition(victim.transform.position + new Vector3(ring.x, 0f, ring.y), default, 10f);
                rm.SpawnEnemyGameObject(spot, Random.Range(0f, 360f), -1, types[Random.Range(0, types.Count)]);
            }
            Notify($"A horde of {count} is coming for {victim.playerUsername}");
        }

        internal static void RandomChaos()
        {
            switch (Random.Range(0, 5))
            {
                case 0: Horde(Settings.HordeSize); break;
                case 1: LightsOut(Settings.ChaosSeconds); break;
                case 2: Scatter(); break;
                case 3: LowGravity(Settings.ChaosSeconds); break;
                default: Earthquake(8f); break;
            }
        }

        private IEnumerator QuakeRoutine(float seconds)
        {
            float end = Time.time + seconds;
            int beat = 0;
            while (Time.time < end && HUDManager.Instance != null)
            {
                HUDManager.Instance.ShakeCamera(beat % 3 == 0 ? ScreenShakeType.VeryStrong : ScreenShakeType.Big);
                if (IsHost && beat % 5 == 0 && RoundManager.Instance != null) RoundManager.Instance.FlickerLights(true, false);
                var p = Local;
                if (p != null && !p.isPlayerDead && p.thisController.isGrounded)
                    p.externalForceAutoFade += new Vector3(Random.Range(-3f, 3f), 0f, Random.Range(-3f, 3f));
                beat++;
                yield return new WaitForSeconds(0.35f);
            }
        }

        // ================================================================== network messages

        internal static void Receive(ulong sender, MenuNet.Msg type, FastBufferReader reader)
        {
            var sor = StartOfRound.Instance;
            var me = Local;
            bool fromHost = sender == NetworkManager.ServerClientId;
            switch (type)
            {
                case MenuNet.Msg.Revive:
                    if (fromHost && sor != null) sor.ReviveDeadPlayers();
                    break;

                case MenuNet.Msg.Heal:
                {
                    reader.ReadValueSafe(out int index);
                    if (!fromHost || me == null || me.isPlayerDead || (index >= 0 && (int)me.playerClientId != index)) break;
                    me.health = 100;
                    me.criticallyInjured = false;
                    me.bleedingHeavily = false;
                    if (me.playerBodyAnimator != null) me.playerBodyAnimator.SetBool("Limp", false);
                    HUDManager.Instance.UpdateHealthUI(100, hurtPlayer: false);
                    break;
                }

                case MenuNet.Msg.TeleportPlayer:
                {
                    reader.ReadValueSafe(out int index);
                    reader.ReadValueSafe(out Vector3 position);
                    reader.ReadValueSafe(out bool inside);
                    if (fromHost && me != null && (int)me.playerClientId == index)
                    {
                        TeleportLocal(position, inside);
                        Notify("The host teleported you");
                    }
                    break;
                }

                case MenuNet.Msg.ItemsToShip:
                {
                    reader.ReadValueSafe(out int count);
                    for (int i = 0; i < count; i++)
                    {
                        reader.ReadValueSafe(out ulong id);
                        reader.ReadValueSafe(out Vector3 spot);
                        if (fromHost) PlaceInShip(id, spot);
                    }
                    break;
                }

                case MenuNet.Msg.Scale:
                {
                    reader.ReadValueSafe(out ulong id);
                    reader.ReadValueSafe(out float scale);
                    if (fromHost) ApplyScale(id, scale);
                    break;
                }

                case MenuNet.Msg.Doors:
                    reader.ReadValueSafe(out byte action);
                    if (fromHost) ApplyDoors((MenuNet.DoorAction)action);
                    break;

                case MenuNet.Msg.Quake:
                    reader.ReadValueSafe(out float quake);
                    if (fromHost) Instance.StartCoroutine(Instance.QuakeRoutine(quake));
                    break;

                case MenuNet.Msg.Scatter:
                    if (fromHost) ScatterSelf();
                    break;

                case MenuNet.Msg.LowGravity:
                    reader.ReadValueSafe(out float gravity);
                    if (fromHost)
                    {
                        Instance.lowGravityUntil = Time.time + gravity;
                        if (HUDManager.Instance != null) HUDManager.Instance.DisplayTip("Low gravity", $"Gravity is weak for {gravity:0} seconds. Jump!");
                    }
                    break;

                case MenuNet.Msg.Invisible:
                {
                    reader.ReadValueSafe(out int index);
                    reader.ReadValueSafe(out bool on);
                    if (on) Settings.InvisiblePlayers.Add((ulong)index); else Settings.InvisiblePlayers.Remove((ulong)index);
                    // The host shares it so whoever ends up running a monster's AI knows too.
                    if (!fromHost && IsHost)
                        MenuNet.ToAll(MenuNet.Msg.Invisible, (ref FastBufferWriter w) => { w.WriteValueSafe(index); w.WriteValueSafe(on); });
                    break;
                }
            }
        }

        private static void PlaceInShip(ulong id, Vector3 world)
        {
            if (!NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(id, out var obj)) return;
            var item = obj.GetComponent<GrabbableObject>();
            var ship = StartOfRound.Instance.elevatorTransform;
            if (item == null || item.isHeld || ship == null) return;
            item.transform.SetParent(ship, worldPositionStays: true);
            var local = ship.InverseTransformPoint(world + Vector3.up * item.itemProperties.verticalOffset);
            item.startFallingPosition = local + Vector3.up * 0.3f;
            item.targetFloorPosition = local;
            item.transform.localPosition = local;
            item.fallTime = 1f;
            item.hasHitGround = true;
            item.reachedFloorTarget = false;
            item.isInFactory = false;
            item.isInElevator = true;
            item.isInShipRoom = true;
            RoundManager.Instance.CollectNewScrapForThisRound(item);
            item.OnBroughtToShip();
        }

        private static void ScatterSelf()
        {
            var me = Local;
            var rm = RoundManager.Instance;
            if (me == null || me.isPlayerDead || rm == null) return;
            var nodes = (me.isInsideFactory ? rm.insideAINodes : rm.outsideAINodes)?.Where(n => n != null).ToArray();
            if (nodes == null || nodes.Length == 0) return;
            TeleportLocal(nodes[Random.Range(0, nodes.Length)].transform.position, me.isInsideFactory);
            Notify("You've been scattered!");
        }

        // ================================================================== ESP

        private readonly List<GrabbableObject> scrapCache = new List<GrabbableObject>();
        private readonly List<(Component trap, string name)> trapCache = new List<(Component, string)>();
        private float nextEspScan;
        private GUIStyle espStyle;

        private void OnGUI()
        {
            if (Event.current.type != EventType.Repaint) return;
            if (!(Settings.MonsterEsp || Settings.PlayerEsp || Settings.ScrapEsp || Settings.TrapEsp)) return;
            var me = Local;
            var cam = StartOfRound.Instance != null ? StartOfRound.Instance.activeCamera : null;
            if (me == null || cam == null) return;
            if (espStyle == null)
            {
                espStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 15, fontStyle = FontStyle.Bold, richText = true };
                espStyle.font = UiKit.Font ?? espStyle.font;
            }

            if (Time.time >= nextEspScan)
            {
                nextEspScan = Time.time + 1f;
                scrapCache.Clear();
                if (Settings.ScrapEsp)
                    scrapCache.AddRange(FindObjectsOfType<GrabbableObject>().Where(g => g.itemProperties != null && g.itemProperties.isScrap && !g.isHeld && !g.isInShipRoom));
                trapCache.Clear();
                if (Settings.TrapEsp)
                {
                    foreach (var m in FindObjectsOfType<Landmine>()) if (!m.hasExploded) trapCache.Add((m, "Mine"));
                    foreach (var t in FindObjectsOfType<Turret>()) trapCache.Add((t, "Turret"));
                    foreach (var s in FindObjectsOfType<SpikeRoofTrap>()) trapCache.Add((s, "Spike trap"));
                }
            }

            var origin = cam.transform.position;
            if (Settings.MonsterEsp && RoundManager.Instance != null)
                foreach (var e in RoundManager.Instance.SpawnedEnemies)
                    if (e != null && !e.isEnemyDead)
                        Label(cam, origin, e.transform.position + Vector3.up * 1.6f, DisplayName(e.enemyType),
                              IsPet(e) ? UiKit.Green : Settings.IsFrozen(e) ? UiKit.Cyan : UiKit.Red);
            if (Settings.PlayerEsp && StartOfRound.Instance != null)
                foreach (var p in StartOfRound.Instance.allPlayerScripts)
                    if (p != null && p != me && p.isPlayerControlled && !p.isPlayerDead)
                        Label(cam, origin, p.transform.position + Vector3.up * 2.1f, $"{p.playerUsername}  {p.health}hp", UiKit.Blue);
            foreach (var s in scrapCache)
                if (s != null && !s.isHeld && s.scrapValue >= Settings.ScrapEspMinValue)
                    Label(cam, origin, s.transform.position + Vector3.up * 0.4f, $"{s.itemProperties.itemName} ${s.scrapValue}",
                          s.scrapValue >= 80 ? UiKit.Gold : UiKit.Yellow);
            foreach (var (trap, name) in trapCache)
                if (trap != null)
                    Label(cam, origin, trap.transform.position + Vector3.up * 0.5f, name,
                          trap is Landmine mine && !MineActive(mine) || trap is Turret tur && !tur.turretActive ||
                          trap is SpikeRoofTrap sp && !sp.trapActive ? UiKit.Dim : UiKit.Orange);
        }

        private void Label(Camera cam, Vector3 origin, Vector3 world, string text, Color color)
        {
            float dist = Vector3.Distance(origin, world);
            if (dist > Settings.EspRange) return;
            var vp = cam.WorldToViewportPoint(world);
            if (vp.z <= 0f) return;
            var pos = new Vector2(vp.x * Screen.width, (1f - vp.y) * Screen.height);
            string content = $"{text}  <size=11>{dist:0}m</size>";
            var size = espStyle.CalcSize(new GUIContent(content));
            var rect = new Rect(pos.x - size.x / 2f - 6f, pos.y - size.y / 2f, size.x + 12f, size.y);
            UiKit.RoundRect(rect, new Color(0f, 0f, 0f, 0.6f), 5f);
            UiKit.RoundRect(new Rect(rect.x, rect.y, 3f, rect.height), color, 1.5f);
            espStyle.normal.textColor = color;
            GUI.Label(rect, content, espStyle);
            // Small dot on the exact spot.
            UiKit.RoundRect(new Rect(pos.x - 3f, pos.y + size.y / 2f + 3f, 6f, 6f), color, 3f);
        }
    }
}
