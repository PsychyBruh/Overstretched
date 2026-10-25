using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;

namespace Overstretched
{
    [HarmonyPatch(typeof(PlayerControllerB))]
    internal static class PlayerPatches
    {
        [HarmonyPrefix, HarmonyPatch(nameof(PlayerControllerB.DamagePlayer))]
        private static bool DamagePlayer(PlayerControllerB __instance, bool fallDamage)
        {
            if (!IsLocal(__instance)) return true;
            if (Settings.GodMode) return false;
            return !(fallDamage && Settings.NoFallDamage);
        }

        [HarmonyPrefix, HarmonyPatch(nameof(PlayerControllerB.KillPlayer))]
        private static bool KillPlayer(PlayerControllerB __instance) =>
            !(Settings.GodMode && IsLocal(__instance));

        private static bool IsLocal(PlayerControllerB p) =>
            StartOfRound.Instance != null && p == StartOfRound.Instance.localPlayerController;
    }

    [HarmonyPatch(typeof(TimeOfDay), nameof(TimeOfDay.MoveGlobalTime))]
    internal static class TimePatch
    {
        private static bool Prefix() => !Settings.FreezeTime;
    }

    /// <summary>
    /// Freezing. Most vanilla monsters override Update / DoAIInterval / OnCollideWithPlayer with their own
    /// logic, so patching EnemyAI alone isn't enough: every override in the game assembly gets the prefix.
    /// </summary>
    internal static class EnemyFreeze
    {
        private static readonly string[] Methods = { "Update", "LateUpdate", "FixedUpdate", "DoAIInterval", "OnCollideWithPlayer" };
        private static readonly HashSet<EnemyAI> stoppedByUs = new HashSet<EnemyAI>();

        public static void Apply(Harmony harmony)
        {
            var prefix = new HarmonyMethod(typeof(EnemyFreeze), nameof(Prefix));
            int count = 0;
            var types = typeof(EnemyAI).Assembly.GetTypes().Where(t => t == typeof(EnemyAI) || t.IsSubclassOf(typeof(EnemyAI)));
            foreach (var type in types)
                foreach (var name in Methods)
                {
                    var method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    if (method == null || method.IsAbstract || method.GetMethodBody() == null) continue;
                    harmony.Patch(method, prefix: prefix);
                    count++;
                }
            Plugin.Log.LogInfo($"Freeze hooks on {count} monster methods");
        }

        private static bool Prefix(EnemyAI __instance)
        {
            if (__instance == null) return true;
            var agent = __instance.agent;
            if (!Settings.IsFrozen(__instance))
            {
                if (stoppedByUs.Remove(__instance) && agent != null && agent.enabled && agent.isOnNavMesh) agent.isStopped = false;
                return true;
            }
            if (agent != null && agent.enabled && agent.isOnNavMesh)
            {
                agent.isStopped = true;
                agent.velocity = Vector3.zero;
                stoppedByUs.Add(__instance);
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(RoundManager))]
    internal static class RoundPatches
    {
        // Applied as each moon generates so the multipliers affect that day's spawns.
        [HarmonyPrefix, HarmonyPatch(nameof(RoundManager.SpawnScrapInLevel))]
        private static void SpawnScrapInLevel(RoundManager __instance)
        {
            __instance.scrapValueMultiplier = Settings.ScrapValueMultiplier;
            __instance.scrapAmountMultiplier = Settings.ScrapAmountMultiplier;
        }

        [HarmonyPostfix, HarmonyPatch("RefreshEnemiesList")]
        private static void RefreshEnemiesList(RoundManager __instance)
        {
            __instance.currentMaxInsidePower *= Settings.EnemyPowerMultiplier;
            __instance.currentMaxOutsidePower *= Settings.EnemyPowerMultiplier;
        }
    }
}
