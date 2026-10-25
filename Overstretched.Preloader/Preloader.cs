using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using Mono.Cecil;

namespace Overstretched.Preloader
{
    /// <summary>
    /// Applies the mod on/off switches from the F8 menu's Mods tab. The menu writes one line per file to
    /// BepInEx/config/overstretched.pending.txt ("disable|BepInEx/plugins/..." or "enable|..."); here, before
    /// any plugin is loaded, each file is moved between its normal place and BepInEx/Overstretched.Disabled.
    /// </summary>
    public static class OverstretchedPreloader
    {
        public const string DisabledRoot = "BepInEx/Overstretched.Disabled";

        // Required by BepInEx's patcher contract: we don't patch any game assembly.
        public static IEnumerable<string> TargetDLLs { get; } = Array.Empty<string>();
        public static void Patch(AssemblyDefinition assembly) { }

        public static void Initialize()
        {
            var log = Logger.CreateLogSource("Overstretched.Preloader");
            var pending = Path.Combine(Paths.ConfigPath, "overstretched.pending.txt");
            if (!File.Exists(pending)) return;
            int moved = 0, failed = 0;
            foreach (var line in File.ReadAllLines(pending))
            {
                int bar = line.IndexOf('|');
                if (bar <= 0) continue;
                bool disable = line.Substring(0, bar) == "disable";
                var rel = line.Substring(bar + 1).Trim().Replace('\\', '/');
                // Only plugin files: patchers and core libraries are already loaded by now.
                if (!rel.StartsWith("BepInEx/plugins/", StringComparison.OrdinalIgnoreCase) || rel.Contains("..")) continue;
                var normal = Path.Combine(Paths.GameRootPath, rel);
                var parked = Path.Combine(Paths.GameRootPath, DisabledRoot, rel);
                var from = disable ? normal : parked;
                var to = disable ? parked : normal;
                try
                {
                    if (!File.Exists(from)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(to));
                    if (File.Exists(to)) File.Delete(to);
                    File.Move(from, to);
                    moved++;
                }
                catch (Exception e)
                {
                    failed++;
                    log.LogWarning($"Couldn't {(disable ? "disable" : "enable")} {rel}: {e.Message}");
                }
            }
            File.Delete(pending);
            log.LogInfo($"Applied mod switches from the Overstretched menu: {moved} files moved{(failed > 0 ? $", {failed} failed" : "")}");
        }
    }
}
