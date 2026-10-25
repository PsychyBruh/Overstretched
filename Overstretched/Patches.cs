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
        private static bool DamagePlayer(PlayerControllerB __instance, bool fallDamage, CauseOfDeath causeOfDeath)
        {
            if (!IsLocal(__instance)) return true;
            LastDamage = $"{causeOfDeath} fallDamage={fallDamage}";
            if (Settings.GodMode) return false;
            // Hard landings pass CauseOfDeath.Gravity without setting fallDamage, so check both.
            return !(Settings.NoFallDamage && (fallDamage || causeOfDeath == CauseOfDeath.Gravity));
        }

        [HarmonyPrefix, HarmonyPatch(nameof(PlayerControllerB.KillPlayer))]
        private static bool KillPlayer(PlayerControllerB __instance) =>
            !(Settings.GodMode && IsLocal(__instance));

        // Landing damage is decided from these when you touch the ground; keep them harmless while the toggle is on.
        [HarmonyPrefix, HarmonyPatch("PlayerHitGroundEffects")]
        private static void PlayerHitGroundEffects(PlayerControllerB __instance)
        {
            if (!Settings.NoFallDamage || !IsLocal(__instance)) return;
            __instance.takingFallDamage = false;
            __instance.fallValueUncapped = Mathf.Max(__instance.fallValueUncapped, -30f);
        }

        internal static string LastDamage = "none"; // for the self-test

        internal static bool IsLocal(PlayerControllerB p) =>
            StartOfRound.Instance != null && p == StartOfRound.Instance.localPlayerController;
    }

    /// <summary>Invisible to monsters: they can't target you and touching them does nothing.</summary>
    [HarmonyPatch(typeof(EnemyAI))]
    internal static class InvisiblePatches
    {
        [HarmonyPostfix, HarmonyPatch(nameof(EnemyAI.PlayerIsTargetable))]
        private static void PlayerIsTargetable(PlayerControllerB playerScript, ref bool __result)
        {
            if (IsInvisible(playerScript)) __result = false;
        }

        [HarmonyPostfix, HarmonyPatch(nameof(EnemyAI.MeetsStandardPlayerCollisionConditions))]
        private static void MeetsStandardPlayerCollisionConditions(ref PlayerControllerB __result)
        {
            if (IsInvisible(__result)) __result = null;
        }

        // Monster AI usually runs on the host, so other modded players' choices arrive over the network.
        internal static bool IsInvisible(PlayerControllerB p) =>
            p != null && (Settings.InvisiblePlayers.Contains(p.playerClientId) || (Settings.Invisible && PlayerPatches.IsLocal(p)));
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

        private static Harmony harmonyInstance;
        private static readonly HashSet<System.Type> patchedTypes = new HashSet<System.Type>();

        public static void Apply(Harmony harmony)
        {
            harmonyInstance = harmony;
            int count = PatchTypes(typeof(EnemyAI).Assembly.GetTypes());
            Plugin.Log.LogInfo($"Freeze hooks on {count} monster methods");
        }

        /// <summary>
        /// Monsters added by other mods live in assemblies that may load after ours, so their overrides get the
        /// same freeze prefix once everything has loaded (called when the game finishes initialising).
        /// </summary>
        public static void PatchModdedMonsters()
        {
            if (harmonyInstance == null) return;
            int count = 0;
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm == typeof(EnemyAI).Assembly || asm == typeof(EnemyFreeze).Assembly || asm.IsDynamic) continue;
                System.Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                catch { continue; }
                try { count += PatchTypes(types); }
                catch (System.Exception e) { Plugin.Log.LogWarning($"Couldn't add freeze hooks to {asm.GetName().Name}: {e.Message}"); }
            }
            if (count > 0) Plugin.Log.LogInfo($"Freeze hooks on {count} modded monster methods");
        }

        private static int PatchTypes(IEnumerable<System.Type> types)
        {
            var prefix = new HarmonyMethod(typeof(EnemyFreeze), nameof(Prefix));
            int count = 0;
            // Open generic types (a mod's shared AI base class) can't be hooked; their monsters are paused another way.
            foreach (var type in types.Where(t => (t == typeof(EnemyAI) || t.IsSubclassOf(typeof(EnemyAI))) && !t.ContainsGenericParameters))
            {
                if (!patchedTypes.Add(type)) continue;
                foreach (var name in Methods)
                {
                    var method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    if (method == null || method.IsAbstract || method.IsGenericMethod || method.GetMethodBody() == null) continue;
                    try { harmonyInstance.Patch(method, prefix: prefix); count++; }
                    catch (System.Exception e) { Plugin.Log.LogWarning($"Couldn't hook {type.Name}.{name}: {e.Message}"); }
                }
            }
            return count;
        }

        [HarmonyPatch(typeof(MenuManager), "Start")]
        private static class AfterLoad
        {
            private static void Postfix() => PatchModdedMonsters();
        }

        private static bool Prefix(EnemyAI __instance)
        {
            if (__instance == null) return true;
            // Pets: their own AI is off and Features walks them after their owner instead.
            if (Features.IsPet(__instance)) return false;
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
