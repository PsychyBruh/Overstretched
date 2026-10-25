using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;
using UnityEngine;

namespace Overstretched
{
    /// <summary>
    /// The installed mod pack as the game sees it: pack.lock.json and installed.json (written by the installer next
    /// to Overstretched.dll), which plugins each package loaded, and the next-launch on/off switches.
    /// </summary>
    public static class ModPack
    {
        [Serializable] public class Lock { public string game; public string generated; public Group[] groups; public Package[] packages; }
        [Serializable] public class Group { public string id; public string name; public string description; public bool required; }
        [Serializable]
        public class Package
        {
            public string id, name, author, version, page, source, description, group, license;
            public bool top, everyone;
            public string[] deps;
            public long size;
        }
        [Serializable] public class State { public string[] groups; public Installed[] packages; }
        [Serializable] public class Installed { public string id; public string version; public string[] files; }

        public const string DisabledRoot = "BepInEx/Overstretched.Disabled";
        private static string PluginDir => Path.GetDirectoryName(typeof(ModPack).Assembly.Location);
        private static string PendingFile => Path.Combine(Paths.ConfigPath, "overstretched.pending.txt");

        public static Lock Pack { get; private set; }
        public static Dictionary<string, Installed> InstalledFiles { get; } = new Dictionary<string, Installed>();
        public static bool Present => Pack != null && Pack.packages != null;

        // Switches made this session, applied by the preloader at the next launch: package id -> enable?
        private static readonly Dictionary<string, bool> pending = new Dictionary<string, bool>();

        public static void Load()
        {
            try
            {
                var lockPath = Path.Combine(PluginDir, "pack.lock.json");
                if (File.Exists(lockPath)) Pack = JsonUtility.FromJson<Lock>(File.ReadAllText(lockPath));
                var statePath = Path.Combine(PluginDir, "installed.json");
                if (File.Exists(statePath))
                    foreach (var p in JsonUtility.FromJson<State>(File.ReadAllText(statePath)).packages ?? new Installed[0])
                        InstalledFiles[p.id] = p;
                if (File.Exists(PendingFile)) ReadPending();
                if (Present) Plugin.Log.LogInfo($"Mod pack: {Pack.packages.Length} packages for Lethal Company {Pack.game}, {InstalledFiles.Count} installed");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Couldn't read the mod pack files: " + e.Message); }
            InstallPreloader();
        }

        /// <summary>Writes the embedded preloader patcher into BepInEx/patchers so mod switches can be applied.</summary>
        private static void InstallPreloader()
        {
            try
            {
                using (var s = typeof(ModPack).Assembly.GetManifestResourceStream("Overstretched.Preloader.dll"))
                {
                    if (s == null) return;
                    var bytes = new byte[s.Length];
                    s.Read(bytes, 0, bytes.Length);
                    var dir = Path.Combine(Paths.PatcherPluginPath, "Overstretched");
                    var path = Path.Combine(dir, "Overstretched.Preloader.dll");
                    if (File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(bytes)) return;
                    Directory.CreateDirectory(dir);
                    File.WriteAllBytes(path, bytes);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Couldn't install the Overstretched preloader: " + e.Message); }
        }

        public static Package Find(string id) => Present ? Pack.packages.FirstOrDefault(p => p.id == id) : null;

        public static string GroupName(string id) =>
            Pack?.groups?.FirstOrDefault(g => g.id == id)?.name ?? (id == "library" ? "Library" : id);

        /// <summary>BepInEx plugins that were loaded from this package's folder.</summary>
        public static List<PluginInfo> PluginsOf(Package p)
        {
            var marker = Path.DirectorySeparatorChar + p.id + Path.DirectorySeparatorChar;
            return Chainloader.PluginInfos.Values
                .Where(i => i.Location != null && i.Location.Replace('/', Path.DirectorySeparatorChar).IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
        }

        private static IEnumerable<string> PluginFiles(Package p) =>
            InstalledFiles.TryGetValue(p.id, out var inst) && inst.files != null
                ? inst.files.Where(f => f.StartsWith("BepInEx/plugins/", StringComparison.OrdinalIgnoreCase))
                : Enumerable.Empty<string>();

        /// <summary>Disabled right now (its plugin files are parked), regardless of pending switches.</summary>
        public static bool IsDisabledOnDisk(Package p)
        {
            var files = PluginFiles(p).ToList();
            return files.Count > 0 && files.All(f => !File.Exists(Path.Combine(Paths.GameRootPath, f)) &&
                                                     File.Exists(Path.Combine(Paths.GameRootPath, DisabledRoot, f)));
        }

        /// <summary>What the switch shows: the pending choice if there is one, else what's on disk.</summary>
        public static bool WillBeEnabled(Package p) => pending.TryGetValue(p.id, out bool on) ? on : !IsDisabledOnDisk(p);
        public static bool HasPending(Package p) => pending.ContainsKey(p.id) && pending[p.id] == IsDisabledOnDisk(p);
        public static bool CanToggle(Package p) => PluginFiles(p).Any();

        /// <summary>Enabled packages that need this one (directly or through other libraries).</summary>
        public static List<Package> NeededBy(Package target) =>
            Pack.packages.Where(p => p != target && WillBeEnabled(p) && DependsOn(p, target.id, new HashSet<string>())).ToList();

        private static bool DependsOn(Package p, string id, HashSet<string> seen)
        {
            if (p.deps == null || !seen.Add(p.id)) return false;
            return p.deps.Any(d => d == id || (Find(d) is Package dep && DependsOn(dep, id, seen)));
        }

        /// <summary>Switch a package on or off for the next launch. Turning one on also turns on what it needs.</summary>
        public static string SetEnabled(Package p, bool on)
        {
            if (!on)
            {
                var users = NeededBy(p);
                if (users.Count > 0) return $"{p.name} is needed by {string.Join(", ", users.Take(3).Select(u => u.name))}";
                pending[p.id] = false;
            }
            else
            {
                pending[p.id] = true;
                foreach (var d in p.deps ?? new string[0])
                    if (Find(d) is Package dep && !WillBeEnabled(dep)) SetEnabled(dep, true);
            }
            WritePending();
            return null;
        }

        private static void WritePending()
        {
            var lines = new List<string>();
            foreach (var pair in pending)
            {
                var p = Find(pair.Key);
                if (p == null || pair.Value != IsDisabledOnDisk(p)) continue; // no change from what's on disk
                lines.AddRange(PluginFiles(p).Select(f => (pair.Value ? "enable|" : "disable|") + f));
            }
            if (lines.Count == 0) { if (File.Exists(PendingFile)) File.Delete(PendingFile); return; }
            File.WriteAllLines(PendingFile, lines);
        }

        private static void ReadPending()
        {
            foreach (var line in File.ReadAllLines(PendingFile))
            {
                int bar = line.IndexOf('|');
                if (bar <= 0) continue;
                var rel = line.Substring(bar + 1);
                var owner = InstalledFiles.Values.FirstOrDefault(i => i.files != null && i.files.Contains(rel));
                if (owner != null) pending[owner.id] = line.StartsWith("enable");
            }
        }

        public static bool AnyPending => Present && Pack.packages.Any(HasPending);

        // ------------------------------------------------------------------ lobby check

        /// <summary>"guid@version" for every loaded plugin everyone in a lobby must share (client-only mods skipped).</summary>
        public static List<string> SharedPluginList()
        {
            var clientOnly = new HashSet<string>(Present ? Pack.packages.Where(p => !p.everyone).Select(p => p.id) : Enumerable.Empty<string>());
            return Chainloader.PluginInfos.Values
                .Where(i => !clientOnly.Any(id => i.Location != null && i.Location.IndexOf(Path.DirectorySeparatorChar + id + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0))
                .Select(i => $"{i.Metadata.GUID}@{i.Metadata.Version}")
                .OrderBy(s => s).ToList();
        }
    }
}
