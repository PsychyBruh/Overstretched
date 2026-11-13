using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Overstretched
{
    /// <summary>
    /// Host only: extra natural-looking spawns on top of the game's own, at a steady rate, picked from the
    /// current moon's spawn table by weight. The game's spawner decides how many monsters come from the time of
    /// day and the moon's power cap, and the spawn table weights only choose which ones, so without this, maxing
    /// every weight changes nothing about how many show up.
    /// Also applies "Spawn cap" changes to the day in progress instead of only from the next landing.
    /// </summary>
    internal static class SpawnDirector
    {
        private static float pending;
        private static float baseInsidePower = -1f, baseOutsidePower = -1f, appliedMultiplier = -1f;
        private static readonly HashSet<EnemyType> warned = new HashSet<EnemyType>();

        /// <summary>Called by the RefreshEnemiesList patch with the moon's own caps, before the multiplier.</summary>
        internal static void OnNewDay(RoundManager rm)
        {
            baseInsidePower = rm.currentMaxInsidePower;
            baseOutsidePower = rm.currentMaxOutsidePower;
            appliedMultiplier = -1f;
            pending = 0f;
        }

        internal static int Alive(RoundManager rm) =>
            rm.SpawnedEnemies.Count(e => e != null && !e.isEnemyDead);

        internal static void Tick()
        {
            var rm = RoundManager.Instance;
            var sor = StartOfRound.Instance;
            if (rm == null || sor == null || !sor.shipHasLanded || sor.shipIsLeaving || rm.currentLevel == null) { pending = 0f; return; }

            ApplyPowerCap(rm);

            if (Settings.ExtraSpawnsPerMinute <= 0f) { pending = 0f; return; }
            pending += Time.deltaTime * Settings.ExtraSpawnsPerMinute / 60f;
            if (pending < 1f) return;
            pending -= 1f;
            if (Alive(rm) >= Settings.ExtraSpawnMaxAlive) return;
            SpawnOne(rm);
        }

        private static void ApplyPowerCap(RoundManager rm)
        {
            if (baseInsidePower < 0f || Mathf.Approximately(appliedMultiplier, Settings.EnemyPowerMultiplier)) return;
            rm.currentMaxInsidePower = baseInsidePower * Settings.EnemyPowerMultiplier;
            rm.currentMaxOutsidePower = baseOutsidePower * Settings.EnemyPowerMultiplier;
            appliedMultiplier = Settings.EnemyPowerMultiplier;
        }

        private static bool Spawnable(SpawnableEnemyWithRarity e) =>
            e != null && e.rarity > 0 && e.enemyType != null && e.enemyType.enemyPrefab != null && !e.enemyType.requireNestObjectsToSpawn;

        /// <summary>One monster from the moon's indoor, outdoor and daytime lists together, weighted by the spawn table.</summary>
        internal static bool SpawnOne(RoundManager rm)
        {
            var level = rm.currentLevel;
            var pool = new List<(SpawnableEnemyWithRarity entry, bool inside)>();
            pool.AddRange(level.Enemies.Where(Spawnable).Select(e => (e, true)));
            pool.AddRange(level.OutsideEnemies.Concat(level.DaytimeEnemies).Where(Spawnable).Select(e => (e, false)));
            if (pool.Count == 0) return false;

            int roll = Random.Range(0, pool.Sum(p => p.entry.rarity));
            var pick = pool[pool.Count - 1];
            foreach (var p in pool)
            {
                if (roll < p.entry.rarity) { pick = p; break; }
                roll -= p.entry.rarity;
            }

            if (!SpawnPoint(rm, pick.inside, out var pos)) return false;
            var type = pick.entry.enemyType;
            try
            {
                rm.SpawnEnemyGameObject(pos, Random.Range(0f, 360f), -1, type);
                return true;
            }
            catch (System.Exception e)
            {
                if (warned.Add(type)) Plugin.Log.LogWarning($"Spawn rate couldn't spawn {Features.DisplayName(type)}: {e.Message}");
                return false;
            }
        }

        private static bool SpawnPoint(RoundManager rm, bool inside, out Vector3 pos)
        {
            pos = default;
            if (inside)
            {
                var vents = rm.allEnemyVents?.Where(v => v != null && v.floorNode != null).ToArray();
                if (vents != null && vents.Length > 0) { pos = vents[Random.Range(0, vents.Length)].floorNode.position; return true; }
                var nodes = rm.insideAINodes?.Where(n => n != null).ToArray();
                if (nodes == null || nodes.Length == 0) return false;
                pos = rm.GetNavMeshPosition(nodes[Random.Range(0, nodes.Length)].transform.position, default, 5f);
                return true;
            }
            var outside = rm.outsideAINodes?.Where(n => n != null).ToArray();
            if (outside == null || outside.Length == 0) return false;
            pos = rm.GetNavMeshPosition(outside[Random.Range(0, outside.Length)].transform.position, default, 5f);
            return true;
        }
    }
}
