using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;

namespace Overstretched
{
    /// <summary>
    /// Optional hooks into other mods in the pack, all through reflection: Overstretched never references their
    /// DLLs, so it builds and runs the same with or without them.
    /// </summary>
    internal static class Compat
    {
        public const string LethalLevelLoader = "imabatby.lethallevelloader";
        public const string WeatherRegistry = "mrov.WeatherRegistry";
        public const string Mirage = "qwbarch.Mirage";

        public static bool Has(string guid) => Chainloader.PluginInfos.ContainsKey(guid);

        private static readonly Dictionary<string, Type> typeCache = new Dictionary<string, Type>();

        private static Type FindType(string guid, string typeName)
        {
            if (!Has(guid)) return null;
            if (typeCache.TryGetValue(typeName, out var cached) && cached != null) return cached;
            Type found = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { found = asm.GetType(typeName, false); } catch { }
                if (found != null) break;
            }
            typeCache[typeName] = found;
            if (found == null) Plugin.Log.LogWarning($"Compat: {typeName} not found although {guid} is loaded");
            return found;
        }

        private static object StaticGet(Type type, string property) =>
            type?.GetProperty(property, BindingFlags.Public | BindingFlags.Static)?.GetValue(null);

        // ------------------------------------------------------------------ LethalLevelLoader

        private static Dictionary<SelectableLevel, string> customLevels;

        /// <summary>Moons added by other mods, with the mod/author they came from. Empty without LethalLevelLoader.</summary>
        public static Dictionary<SelectableLevel, string> CustomLevels()
        {
            if (customLevels != null && customLevels.Count > 0) return customLevels;
            customLevels = new Dictionary<SelectableLevel, string>();
            try
            {
                var patched = FindType(LethalLevelLoader, "LethalLevelLoader.PatchedContent");
                var raw = StaticGet(patched, "CustomExtendedLevels");
                if (!(raw is IEnumerable levels))
                {
                    if (patched != null) Plugin.Log.LogWarning($"Compat: CustomExtendedLevels returned {raw?.GetType().FullName ?? "null"}");
                    return customLevels;
                }
                foreach (var extended in levels)
                {
                    if (extended == null) continue;
                    var t = extended.GetType();
                    var level = (t.GetProperty("SelectableLevel")?.GetValue(extended) ?? t.GetField("selectableLevel")?.GetValue(extended)) as SelectableLevel;
                    if (level == null) continue;
                    var author = t.GetProperty("AuthorName")?.GetValue(extended) as string;
                    var mod = t.GetProperty("ModName")?.GetValue(extended) as string;
                    customLevels[level] = !string.IsNullOrEmpty(mod) ? mod : author ?? "modded";
                }
                if (customLevels.Count == 0) Plugin.Log.LogInfo("Compat: LethalLevelLoader reports no custom moons yet");
            }
            catch (Exception e) { Plugin.Log.LogWarning("LethalLevelLoader moons unavailable: " + e.Message); }
            return customLevels;
        }

        // ------------------------------------------------------------------ ButteryFixes

        /// <summary>
        /// ButteryFixes keeps radar icons for Butlers and expects them to die, never vanish: despawning one mid-round
        /// makes its radar update throw every frame. Drop our Butler from its list before we despawn it.
        /// </summary>
        public static void BeforeDespawn(EnemyAI enemy)
        {
            DespawnCompanions(enemy);
            if (!(enemy is ButlerEnemyAI) || !Has("butterystancakes.lethalcompany.butteryfixes")) return;
            try
            {
                var radar = FindType("butterystancakes.lethalcompany.butteryfixes", "ButteryFixes.Utility.EnemyRadar");
                if (!(radar?.GetField("allButlers", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) is IList list)) return;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var entry = list[i];
                    var t = entry?.GetType();
                    if (t == null || !ReferenceEquals(t.GetField("butler", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(entry), enemy)) continue;
                    if (t.GetField("radarIcon", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(entry) is UnityEngine.Transform icon && icon != null)
                        UnityEngine.Object.Destroy(icon.gameObject);
                    list.RemoveAt(i);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("ButteryFixes radar cleanup failed: " + e.Message); }
        }

        // ------------------------------------------------------------------ parts other mods attach to monsters

        private static Dictionary<EnemyAI, List<Unity.Netcode.NetworkBehaviour>> companions;
        private static float companionsTime = -10f;
        private static int companionsFrame = -1;
        private static readonly Dictionary<Type, FieldInfo[]> enemyFields = new Dictionary<Type, FieldInfo[]>();
        private static readonly HashSet<UnityEngine.Behaviour> pausedByUs = new HashSet<UnityEngine.Behaviour>();
        // Frozen monsters' crowd avoidance, switched off so a pile of frozen monsters can't shove each other around.
        private static readonly Dictionary<UnityEngine.AI.NavMeshAgent, UnityEngine.AI.ObstacleAvoidanceType> avoidance =
            new Dictionary<UnityEngine.AI.NavMeshAgent, UnityEngine.AI.ObstacleAvoidanceType>();

        private static bool IsModType(Type t)
        {
            var asm = t.Assembly;
            if (asm == typeof(EnemyAI).Assembly || asm == typeof(Compat).Assembly) return false;
            var name = asm.GetName().Name;
            return !name.StartsWith("Unity") && !name.StartsWith("UnityEngine");
        }

        /// <summary>
        /// Networked parts other mods give a monster: ones inside it (ToilHead's turret heads) and separate ones that
        /// point back at it (LethalPhones' masked phones). Rebuilt at most once per frame, or every half second when
        /// only used for freezing.
        /// </summary>
        private static Dictionary<EnemyAI, List<Unity.Netcode.NetworkBehaviour>> Companions(bool fresh)
        {
            bool stale = companions == null || (fresh ? companionsFrame != UnityEngine.Time.frameCount : UnityEngine.Time.time - companionsTime > 0.5f);
            if (!stale) return companions;
            companionsFrame = UnityEngine.Time.frameCount;
            companionsTime = UnityEngine.Time.time;
            companions = new Dictionary<EnemyAI, List<Unity.Netcode.NetworkBehaviour>>();
            void Add(EnemyAI owner, Unity.Netcode.NetworkBehaviour nb)
            {
                if (!companions.TryGetValue(owner, out var list)) companions[owner] = list = new List<Unity.Netcode.NetworkBehaviour>();
                if (!list.Contains(nb)) list.Add(nb);
            }
            foreach (var nb in UnityEngine.Object.FindObjectsOfType<Unity.Netcode.NetworkBehaviour>())
            {
                var type = nb.GetType();
                if (nb is EnemyAI || !IsModType(type)) continue;
                var parentEnemy = nb.GetComponentInParent<EnemyAI>();
                if (parentEnemy != null) { Add(parentEnemy, nb); continue; }
                if (!enemyFields.TryGetValue(type, out var fields))
                    enemyFields[type] = fields = AllFields(type).Where(f => typeof(EnemyAI).IsAssignableFrom(f.FieldType)).ToArray();
                foreach (var f in fields)
                    if (f.GetValue(nb) is EnemyAI owner && owner != null) Add(owner, nb);
            }
            return companions;
        }

        /// <summary>When we remove a monster, remove its companions with it: they expect it to die, never to vanish.</summary>
        private static void DespawnCompanions(EnemyAI enemy)
        {
            if (!Companions(fresh: true).TryGetValue(enemy, out var mine)) return;
            foreach (var obj in mine.Where(nb => nb != null).Select(nb => nb.NetworkObject).Distinct())
                if (obj != null && obj.IsSpawned && obj != enemy.NetworkObject)
                {
                    try { obj.Despawn(); } catch (Exception e) { Plugin.Log.LogWarning($"Couldn't remove {obj.name} with its monster: {e.Message}"); }
                }
        }

        /// <summary>
        /// Host, every frame: frozen monsters and pets also pause their modded parts, so a frozen Coil-Head's ToilHead
        /// turret doesn't keep shooting. Everything we paused is resumed when the monster is unfrozen or gone.
        /// </summary>
        public static void UpdateCompanionPause()
        {
            var rm = RoundManager.Instance;
            bool any = rm != null && rm.SpawnedEnemies.Any(e => e != null && (Settings.IsFrozen(e) || Features.IsPet(e)));
            if (!any && pausedByUs.Count == 0 && avoidance.Count == 0) return;
            UpdateAvoidance(rm, any);
            var pause = new HashSet<UnityEngine.Behaviour>();
            if (any)
            {
                // Modded monsters run AI code our freeze hooks can't always reach (generic base classes, helper
                // components), so a frozen or pet modded monster has its AI script switched off outright.
                foreach (var e in rm.SpawnedEnemies)
                {
                    if (e == null || e.isEnemyDead || !(Settings.IsFrozen(e) || Features.IsPet(e)) || !Features.IsModded(e.enemyType)) continue;
                    pause.Add(e);
                    foreach (var b in e.GetComponentsInChildren<UnityEngine.Behaviour>(true))
                        if (!(b is EnemyAI) && IsModType(b.GetType())) pause.Add(b);
                    if (!Features.IsPet(e) && e.agent != null && e.agent.enabled && e.agent.isOnNavMesh)
                    {
                        e.agent.isStopped = true;
                        e.agent.velocity = UnityEngine.Vector3.zero;
                    }
                }
                foreach (var pair in Companions(fresh: false))
                {
                    if (pair.Key == null || !(Settings.IsFrozen(pair.Key) || Features.IsPet(pair.Key))) continue;
                    foreach (var nb in pair.Value)
                        if (nb != null)
                            foreach (var b in nb.GetComponentsInChildren<UnityEngine.Behaviour>(true))
                                if (!(b is EnemyAI) && IsModType(b.GetType())) pause.Add(b);
                }
            }
            foreach (var b in pause)
                if (b.enabled) { b.enabled = false; pausedByUs.Add(b); }
            foreach (var b in pausedByUs.ToList())
                if (b == null) pausedByUs.Remove(b);
                else if (!pause.Contains(b))
                {
                    b.enabled = true;
                    pausedByUs.Remove(b);
                    if (b is EnemyAI e && e.agent != null && e.agent.enabled && e.agent.isOnNavMesh) e.agent.isStopped = false;
                }
        }

        private static IEnumerable<FieldInfo> AllFields(Type type)
        {
            for (var t = type; t != null && t != typeof(Unity.Netcode.NetworkBehaviour); t = t.BaseType)
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    yield return f;
        }

        private static void UpdateAvoidance(RoundManager rm, bool any)
        {
            var frozen = new HashSet<UnityEngine.AI.NavMeshAgent>();
            if (any)
                foreach (var e in rm.SpawnedEnemies)
                    if (e != null && e.agent != null && Settings.IsFrozen(e) && !Features.IsPet(e)) frozen.Add(e.agent);
            foreach (var agent in frozen)
                if (!avoidance.ContainsKey(agent))
                {
                    avoidance[agent] = agent.obstacleAvoidanceType;
                    agent.obstacleAvoidanceType = UnityEngine.AI.ObstacleAvoidanceType.NoObstacleAvoidance;
                }
            foreach (var pair in avoidance.ToList())
                if (pair.Key == null) avoidance.Remove(pair.Key);
                else if (!frozen.Contains(pair.Key)) { pair.Key.obstacleAvoidanceType = pair.Value; avoidance.Remove(pair.Key); }
        }

        // ------------------------------------------------------------------ WeatherRegistry

        public sealed class WeatherOption
        {
            public string Name;
            public object Handle; // WeatherRegistry.Weather, or a LevelWeatherType without the mod
        }

        /// <summary>Every weather the game knows: WeatherRegistry's list (custom ones included) or the vanilla enum.</summary>
        public static List<WeatherOption> Weathers()
        {
            try
            {
                var manager = FindType(WeatherRegistry, "WeatherRegistry.WeatherManager");
                if (StaticGet(manager, "Weathers") is IEnumerable list)
                {
                    var result = new List<WeatherOption>();
                    foreach (var w in list)
                    {
                        var name = w.GetType().GetProperty("Name")?.GetValue(w) as string;
                        if (!string.IsNullOrEmpty(name)) result.Add(new WeatherOption { Name = name, Handle = w });
                    }
                    if (result.Count > 0) return result;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("WeatherRegistry weathers unavailable: " + e.Message); }
            return Enum.GetValues(typeof(LevelWeatherType)).Cast<LevelWeatherType>()
                .Select(w => new WeatherOption { Name = w.ToString(), Handle = w }).ToList();
        }

        public static string CurrentWeather(SelectableLevel level)
        {
            try
            {
                var manager = FindType(WeatherRegistry, "WeatherRegistry.WeatherManager");
                var method = manager?.GetMethod("GetCurrentWeatherName", new[] { typeof(SelectableLevel), typeof(bool) });
                if (method != null) return (string)method.Invoke(null, new object[] { level, false });
            }
            catch { }
            return level.currentWeather.ToString();
        }

        /// <summary>Host: set a moon's weather, through WeatherRegistry when it's loaded so custom weathers work and sync.</summary>
        public static void SetWeather(SelectableLevel level, WeatherOption weather)
        {
            if (weather.Handle is LevelWeatherType vanilla && !Has(WeatherRegistry))
            {
                level.currentWeather = vanilla;
                return;
            }
            var controller = FindType(WeatherRegistry, "WeatherRegistry.WeatherController");
            var method = controller?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "ChangeWeather" && m.GetParameters().Length == 2 &&
                                     m.GetParameters()[1].ParameterType.IsInstanceOfType(weather.Handle));
            if (method != null) method.Invoke(null, new[] { level, weather.Handle });
            else if (weather.Handle is LevelWeatherType fallback) level.currentWeather = fallback;
        }
    }
}
