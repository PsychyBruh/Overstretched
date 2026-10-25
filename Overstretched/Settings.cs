namespace Overstretched
{
    // Live values the menu edits; patches and the menu's per-frame apply step read them.
    public static class Settings
    {
        // Player
        public static float SpeedMultiplier = 1f;
        public static float JumpMultiplier = 1f;
        public static bool InfiniteSprint;
        public static bool GodMode;
        public static bool NoClip;
        public static float NoClipSpeed = 12f;
        public static bool NoFallDamage;

        // World (host)
        public static float TimeSpeed = 1f;
        public static bool FreezeTime;
        public static float ScrapValueMultiplier = 1f;
        public static float ScrapAmountMultiplier = 1f;

        // Monsters (host)
        public static bool FreezeEnemies;
        public static readonly System.Collections.Generic.HashSet<EnemyAI> FrozenEnemies =
            new System.Collections.Generic.HashSet<EnemyAI>();
        public static float EnemyPowerMultiplier = 1f;

        public static bool IsFrozen(EnemyAI enemy) => FreezeEnemies || FrozenEnemies.Contains(enemy);

        // The Stretched (host)
        public static bool StretchedEnabled = true;
        public static int StretchedRarity = 25;
        public static float StretchedSilenceRadius = 15f;
        public static float StretchedAggression = 1f;
        public static float StretchedSpeed = 1f;
    }
}
