using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Overstretched
{
    // Live values the menu edits; patches and the per-frame apply steps read them.
    public static class Settings
    {
        // Player
        public static float SpeedMultiplier = 1f;
        public static float JumpMultiplier = 1f;
        public static bool InfiniteSprint;
        public static bool InfiniteBattery;
        public static bool InfiniteItemUses;
        public static bool SuperStrength;
        public static bool GodMode;
        [Transient] public static bool NoClip;
        public static float NoClipSpeed = 12f;
        public static bool NoFallDamage;
        public static bool Invisible;

        // Vision
        public static bool NightVision;
        public static float NightVisionStrength = 0.5f;
        public static bool MonsterEsp;
        public static bool PlayerEsp;
        public static bool ScrapEsp;
        public static bool TrapEsp;
        public static float EspRange = 120f;
        public static int ScrapEspMinValue;

        // World (host)
        public static float TimeSpeed = 1f;
        [Transient] public static bool FreezeTime;
        public static float ScrapValueMultiplier = 1f;
        public static float ScrapAmountMultiplier = 1f;

        // Monsters (host)
        [Transient] public static bool FreezeEnemies;
        public static readonly HashSet<EnemyAI> FrozenEnemies = new HashSet<EnemyAI>();
        public static float EnemyPowerMultiplier = 1f;

        public static bool IsFrozen(EnemyAI enemy) => FreezeEnemies || FrozenEnemies.Contains(enemy);

        // Players who turned on "invisible to monsters", by client id, shared over the network so the
        // machine running each monster's AI (usually the host) knows to ignore them.
        public static readonly HashSet<ulong> InvisiblePlayers = new HashSet<ulong>();

        // The Stretched (host)
        public static bool StretchedEnabled = true;
        public static int StretchedRarity = 25;
        public static float StretchedSilenceRadius = 15f;
        public static float StretchedAggression = 1f;
        public static float StretchedSpeed = 1f;

        // Menu
        public static float UiScale = 1f;
        public static float UiOpacity = 0.97f;
        public static int SpawnAllBatches = 1;
        public static float SpamInterval = 0.25f;
        public static int HordeSize = 8;
        public static float ChaosSeconds = 30f;

        // ------------------------------------------------------------------ saving

        /// <summary>Not written to disk: dangerous or momentary toggles that should start off every launch.</summary>
        [AttributeUsage(AttributeTargets.Field)]
        private sealed class TransientAttribute : Attribute { }

        private static string FilePath => Path.Combine(BepInEx.Paths.ConfigPath, "psychy.overstretched.menu.cfg");

        private static IEnumerable<FieldInfo> Saved => typeof(Settings)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => !f.IsInitOnly && !f.IsLiteral && f.GetCustomAttribute<TransientAttribute>() == null)
            .Where(f => f.FieldType == typeof(bool) || f.FieldType == typeof(int) || f.FieldType == typeof(float));

        private static readonly Dictionary<string, object> defaults =
            Saved.ToDictionary(f => f.Name, f => f.GetValue(null));

        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var fields = Saved.ToDictionary(f => f.Name);
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0 || line.StartsWith("#")) continue;
                    if (!fields.TryGetValue(line.Substring(0, eq).Trim(), out var field)) continue;
                    string value = line.Substring(eq + 1).Trim();
                    if (field.FieldType == typeof(bool) && bool.TryParse(value, out bool b)) field.SetValue(null, b);
                    else if (field.FieldType == typeof(int) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)) field.SetValue(null, i);
                    else if (field.FieldType == typeof(float) && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)) field.SetValue(null, f);
                }
                Plugin.Log.LogInfo("Menu settings loaded");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Couldn't load menu settings: " + e.Message); }
        }

        public static void Save()
        {
            try
            {
                var lines = new List<string> { "# Overstretched menu settings, saved automatically when the menu closes" };
                foreach (var f in Saved)
                    lines.Add($"{f.Name} = {Convert.ToString(f.GetValue(null), CultureInfo.InvariantCulture)}");
                File.WriteAllLines(FilePath, lines);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Couldn't save menu settings: " + e.Message); }
        }

        public static void ResetToDefaults()
        {
            foreach (var f in Saved)
                if (defaults.TryGetValue(f.Name, out var value)) f.SetValue(null, value);
        }
    }
}
