using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Overstretched
{
    [BepInPlugin(Guid, Name, Version)]
    // Load after the pack's content loaders so their moons and weathers are registered when we look for them.
    [BepInDependency(Compat.LethalLevelLoader, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(Compat.WeatherRegistry, BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "psychy.overstretched";
        public const string Name = "Overstretched";
        public const string Version = "2.0.0";

        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            Settings.Load();
            ModPack.Load();
            var harmony = new Harmony(Guid);
            harmony.PatchAll();
            EnemyFreeze.Apply(harmony);

            var host = new GameObject("Overstretched");
            DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            host.AddComponent<Features>();
            host.AddComponent<ModMenu>();
            host.AddComponent<PackUpgrade>();
            if (System.IO.File.Exists(AutoTest.FlagFile)) host.AddComponent<AutoTest>();

            try
            {
                StretchedModel.LoadMesh();
                StretchedAnimations.Load();
            }
            catch (System.Exception e)
            {
                Log.LogError($"The Stretched model failed to load: {e}");
            }

            Log.LogInfo($"{Name} {Version} loaded. Press F8 in game to open the menu.");
        }
    }
}
