using System.Linq;
using System.Reflection;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

namespace Overstretched
{
    /// <summary>
    /// Builds The Stretched's network prefab and EnemyType at runtime (no asset bundle) and registers it
    /// with the NetworkManager and every moon's spawn list. Every player in the lobby needs the mod.
    /// </summary>
    public static class StretchedRegistry
    {
        public const string EnemyName = "The Stretched";
        private const uint PrefabHash = 0x5E7C4ED1; // fixed so every client agrees on the prefab id

        public static GameObject Prefab { get; private set; }
        public static EnemyType Type { get; private set; }
        private static GameObject container;

        public static void CreateAndRegister(NetworkManager networkManager)
        {
            if (networkManager == null) return;
            if (Prefab == null) Build(networkManager);
            if (Prefab != null && !networkManager.NetworkConfig.Prefabs.Contains(Prefab))
                networkManager.AddNetworkPrefab(Prefab);
        }

        private static void Build(NetworkManager networkManager)
        {
            // The Bracken is the closest vanilla relative: borrow its agent, audio routing, map dot and scan node.
            var template = networkManager.NetworkConfig.Prefabs.Prefabs
                .Select(p => p.Prefab)
                .FirstOrDefault(p => p != null && p.GetComponent<FlowermanAI>() != null);
            if (template == null)
            {
                Plugin.Log.LogError("The Stretched: could not find the Bracken prefab to copy settings from");
                return;
            }
            var templateAI = template.GetComponent<FlowermanAI>();

            container = new GameObject("OverstretchedPrefabs");
            container.SetActive(false); // keeps the prefab dormant; spawned copies are active
            container.hideFlags = HideFlags.HideAndDontSave;
            Object.DontDestroyOnLoad(container);

            int enemyLayer = LayerMask.NameToLayer("Enemies");
            var root = new GameObject("TheStretched");
            root.transform.SetParent(container.transform, false);
            root.layer = enemyLayer;

            var netObj = root.AddComponent<NetworkObject>();
            typeof(NetworkObject).GetField("GlobalObjectIdHash", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(netObj, PrefabHash);

            var agent = root.AddComponent<NavMeshAgent>();
            var srcAgent = template.GetComponentInChildren<NavMeshAgent>();
            if (srcAgent != null)
            {
                agent.agentTypeID = srcAgent.agentTypeID;
                agent.areaMask = srcAgent.areaMask;
                agent.radius = srcAgent.radius;
                agent.obstacleAvoidanceType = srcAgent.obstacleAvoidanceType;
                agent.avoidancePriority = srcAgent.avoidancePriority;
            }
            agent.height = 3.4f;
            agent.speed = 3f;
            agent.acceleration = 14f;
            agent.angularSpeed = 300f;
            agent.stoppingDistance = 0.4f;
            agent.autoBraking = true;

            var ai = root.AddComponent<StretchedAI>();

            var eye = new GameObject("Eye").transform;
            eye.SetParent(root.transform, false);
            eye.localPosition = new Vector3(0f, 3.3f, 0.5f);

            ai.creatureSFX = MakeAudio(root.transform, "SFX", templateAI.creatureSFX);
            ai.creatureVoice = MakeAudio(root.transform, "Voice", templateAI.creatureVoice);
            ai.creatureVoice.maxDistance = 40f;

            var collision = new GameObject("Collision");
            collision.transform.SetParent(root.transform, false);
            collision.layer = enemyLayer;
            collision.tag = "Enemy";
            var box = collision.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = new Vector3(1.1f, 3.4f, 1.1f);
            box.center = new Vector3(0f, 1.7f, 0f);
            var body = collision.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            collision.AddComponent<EnemyAICollisionDetect>().mainScript = ai;

            // Red enemy dot on the ship radar, like every vanilla monster.
            int radarLayer = LayerMask.NameToLayer("MapRadar");
            var dot = template.GetComponentsInChildren<Transform>(true)
                .FirstOrDefault(t => t.gameObject.layer == radarLayer && t.GetComponent<MeshRenderer>() != null);
            if (dot != null)
            {
                var copy = Object.Instantiate(dot.gameObject, root.transform, false);
                copy.name = "MapDot";
                copy.transform.localPosition = new Vector3(0f, 1.5f, 0f);
            }

            var scan = template.GetComponentInChildren<ScanNodeProperties>(true);
            if (scan != null)
            {
                var node = Object.Instantiate(scan.gameObject, root.transform, false).GetComponent<ScanNodeProperties>();
                node.transform.localPosition = new Vector3(0f, 2.8f, 0f);
                node.headerText = "???";
                node.subText = "";
                node.creatureScanID = -1;
            }

            Type = Object.Instantiate(templateAI.enemyType);
            Type.name = "TheStretchedType";
            Type.enemyName = EnemyName;
            Type.enemyPrefab = root;
            Type.PowerLevel = 3f;
            Type.MaxCount = 1;
            Type.isOutsideEnemy = false;
            Type.isDaytimeEnemy = false;
            Type.canDie = false;
            Type.canBeStunned = true;
            Type.canBeDestroyed = true;
            Type.destroyOnDeath = false;
            Type.doorSpeedMultiplier = 2f;
            Type.stunTimeMultiplier = 1.5f;
            Type.stunGameDifficultyMultiplier = 0.5f;
            Type.numberSpawned = 0;
            Type.spawningDisabled = false;

            ai.enemyType = Type;
            ai.eye = eye;
            ai.agent = agent;
            ai.AIIntervalTime = 0.2f;
            ai.exitVentAnimationTime = 2.2f;
            ai.enemyHP = 999;
            ai.syncMovementSpeed = 0.12f;
            ai.updatePositionThreshold = 0.5f;
            ai.enemyBehaviourStates = System.Enum.GetNames(typeof(StretchedAI.State))
                .Select(n => new EnemyBehaviourState { name = n }).ToArray();

            Prefab = root;
            Plugin.Log.LogInfo("The Stretched enemy prefab built");
        }

        private static AudioSource MakeAudio(Transform parent, string name, AudioSource template)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, 3f, 0f);
            var a = go.AddComponent<AudioSource>();
            a.playOnAwake = false;
            a.spatialBlend = 1f;
            a.dopplerLevel = 0f;
            a.rolloffMode = AudioRolloffMode.Linear;
            a.minDistance = 2f;
            a.maxDistance = 30f;
            if (template != null)
            {
                a.outputAudioMixerGroup = template.outputAudioMixerGroup;
                a.rolloffMode = template.rolloffMode;
                a.minDistance = template.minDistance;
                a.maxDistance = template.maxDistance;
                a.spread = template.spread;
                if (template.rolloffMode == AudioRolloffMode.Custom)
                    a.SetCustomCurve(AudioSourceCurveType.CustomRolloff, template.GetCustomCurve(AudioSourceCurveType.CustomRolloff));
            }
            return a;
        }

        /// <summary>
        /// Adds The Stretched to every moon's indoor spawn list. With overwrite, also pushes the Stretched
        /// tab's spawn weight to every moon (otherwise per-moon edits from the spawn table are kept).
        /// </summary>
        public static void SyncSpawnLists(bool overwrite = false)
        {
            if (Type == null || StartOfRound.Instance == null) return;
            int rarity = Settings.StretchedEnabled ? Settings.StretchedRarity : 0;
            foreach (var level in StartOfRound.Instance.levels)
            {
                if (level == null || level.Enemies == null) continue;
                var entry = level.Enemies.FirstOrDefault(e => e.enemyType == Type);
                if (entry == null) level.Enemies.Add(new SpawnableEnemyWithRarity(Type, rarity));
                else if (overwrite) entry.rarity = rarity;
            }
        }
    }

    [HarmonyPatch]
    internal static class StretchedRegistryPatches
    {
        [HarmonyPostfix, HarmonyPatch(typeof(GameNetworkManager), "Start")]
        private static void RegisterPrefab()
        {
            try { StretchedRegistry.CreateAndRegister(NetworkManager.Singleton); }
            catch (System.Exception e) { Plugin.Log.LogError($"The Stretched registration failed: {e}"); }
        }

        [HarmonyPostfix, HarmonyPatch(typeof(StartOfRound), "Start")]
        private static void AddToMoons() => StretchedRegistry.SyncSpawnLists();

        // Makes sure the entry exists before each day's enemies are chosen (weights are left as edited).
        [HarmonyPrefix, HarmonyPatch(typeof(RoundManager), "RefreshEnemiesList")]
        private static void BeforeEnemyList() => StretchedRegistry.SyncSpawnLists();
    }
}
