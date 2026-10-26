using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace OverstretchedInstaller
{
    // ------------------------------------------------------------------ pack.lock.json (written by tools/pack/update_lock.py)

    [DataContract]
    internal sealed class PackLock
    {
        [DataMember(Name = "game")] public string Game;
        [DataMember(Name = "generated")] public string Generated;
        [DataMember(Name = "groups")] public List<PackGroup> Groups = new List<PackGroup>();
        [DataMember(Name = "packages")] public List<PackPackage> Packages = new List<PackPackage>();

        public PackPackage Find(string id) => Packages.FirstOrDefault(p => p.Id == id);

        /// <summary>Every package needed for these groups: their mods plus all dependencies.</summary>
        public List<PackPackage> Resolve(IEnumerable<string> groups)
        {
            var wanted = new HashSet<string>(groups) { "core" };
            var result = new Dictionary<string, PackPackage>();
            var queue = new Queue<PackPackage>(Packages.Where(p => p.Top && wanted.Contains(p.Group)));
            while (queue.Count > 0)
            {
                var p = queue.Dequeue();
                if (result.ContainsKey(p.Id)) continue;
                result[p.Id] = p;
                foreach (var dep in p.Deps ?? new List<string>())
                {
                    var d = Find(dep);
                    if (d != null) queue.Enqueue(d);
                }
            }
            return result.Values.ToList();
        }
    }

    [DataContract]
    internal sealed class PackGroup
    {
        [DataMember(Name = "id")] public string Id;
        [DataMember(Name = "name")] public string Name;
        [DataMember(Name = "description")] public string Description;
        [DataMember(Name = "default")] public bool Default;
        [DataMember(Name = "required")] public bool Required;
    }

    [DataContract]
    internal sealed class PackPackage
    {
        [DataMember(Name = "id")] public string Id;
        [DataMember(Name = "name")] public string Name;
        [DataMember(Name = "author")] public string Author;
        [DataMember(Name = "version")] public string Version;
        [DataMember(Name = "url")] public string Url;
        [DataMember(Name = "page")] public string Page;
        [DataMember(Name = "source")] public string Source;
        [DataMember(Name = "description")] public string Description;
        [DataMember(Name = "group")] public string Group;
        [DataMember(Name = "top")] public bool Top;
        [DataMember(Name = "everyone")] public bool Everyone;
        [DataMember(Name = "license")] public string License;
        [DataMember(Name = "deps")] public List<string> Deps = new List<string>();
        [DataMember(Name = "sha256")] public string Sha256;
        [DataMember(Name = "size")] public long Size;
    }

    // ------------------------------------------------------------------ what we put in the game folder

    [DataContract]
    internal sealed class InstallState
    {
        [DataMember(Name = "groups")] public List<string> Groups = new List<string>();
        [DataMember(Name = "packages")] public List<InstalledPackage> Packages = new List<InstalledPackage>();
        /// <summary>Files that were already there before we overwrote them: game-relative path -> backup path.</summary>
        [DataMember(Name = "backups")] public Dictionary<string, string> Backups = new Dictionary<string, string>();

        public InstalledPackage Find(string id) => Packages.FirstOrDefault(p => p.Id == id);
    }

    [DataContract]
    internal sealed class InstalledPackage
    {
        [DataMember(Name = "id")] public string Id;
        [DataMember(Name = "version")] public string Version;
        [DataMember(Name = "files")] public List<string> Files = new List<string>();
    }

    internal static class Json
    {
        private static readonly DataContractJsonSerializerSettings Settings = new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true };

        public static T Parse<T>(string json)
        {
            using (var s = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                return (T)new DataContractJsonSerializer(typeof(T), Settings).ReadObject(s);
        }

        public static string Write<T>(T value)
        {
            using (var s = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T), Settings).WriteObject(s, value);
                return Encoding.UTF8.GetString(s.ToArray());
            }
        }
    }

    /// <summary>
    /// Installs the third-party mods listed in pack.lock.json. Each is downloaded from its official Thunderstore page,
    /// checked against the pinned SHA-256, and unpacked the way mod managers do. Every file we place is recorded in
    /// installed.json so updates and uninstalls touch exactly our files and nothing else.
    /// </summary>
    internal static class PackInstaller
    {
        public const string DisabledRoot = "BepInEx/Overstretched.Disabled";
        private const string BackupRoot = "BepInEx/Overstretched.Backup";

        public static string StateFile(string game) => Path.Combine(Installer.PluginDir(game), "installed.json");
        public static string LockFile(string game) => Path.Combine(Installer.PluginDir(game), "pack.lock.json");
        private static string Cache => Path.Combine(Path.GetTempPath(), "OverstretchedCache");

        public static InstallState LoadState(string game)
        {
            try { return File.Exists(StateFile(game)) ? Json.Parse<InstallState>(File.ReadAllText(StateFile(game))) : null; }
            catch { return null; }
        }

        private static void SaveState(string game, InstallState state)
        {
            Directory.CreateDirectory(Installer.PluginDir(game));
            File.WriteAllText(StateFile(game), Json.Write(state));
        }

        /// <summary>Packages whose pinned version isn't what's installed (for the selected groups).</summary>
        public static List<PackPackage> PendingChanges(string game, PackLock lockFile, IEnumerable<string> groups)
        {
            var state = LoadState(game) ?? new InstallState();
            return lockFile.Resolve(groups).Where(p => state.Find(p.Id)?.Version != p.Version).ToList();
        }

        public static async Task Sync(string game, PackLock lockFile, List<string> groups, HttpClient http,
                                      Action<string> log, IProgress<int> progress, int from, int to)
        {
            var state = LoadState(game) ?? new InstallState();
            var wanted = lockFile.Resolve(groups);
            var wantedIds = new HashSet<string>(wanted.Select(p => p.Id));

            // Mods we installed before that are no longer wanted (group unticked, or dropped from the pack).
            foreach (var old in state.Packages.Where(p => !wantedIds.Contains(p.Id)).ToList())
            {
                RemoveFiles(game, old.Files, state);
                state.Packages.Remove(old);
                log($"Removed {old.Id}");
            }

            var todo = wanted.Where(p =>
            {
                var have = state.Find(p.Id);
                return have == null || have.Version != p.Version || !have.Files.All(f => Exists(game, f));
            }).ToList();
            long totalBytes = Math.Max(1, todo.Sum(p => p.Size));
            long doneBytes = 0;
            if (todo.Count == 0) log("All pack mods are already up to date.");
            else log($"Installing {todo.Count} mods ({totalBytes / 1048576} MB to download)...");

            Directory.CreateDirectory(Cache);
            foreach (var p in todo)
            {
                var zip = Path.Combine(Cache, $"{p.Id}-{p.Version}.zip");
                if (!File.Exists(zip) || !HashMatches(zip, p.Sha256))
                {
                    log($"Downloading {p.Name} {p.Version} by {p.Author}...");
                    await DownloadWithRetry(http, p.Url, zip);
                    if (!HashMatches(zip, p.Sha256))
                    {
                        File.Delete(zip);
                        throw new Exception($"{p.Id} {p.Version} didn't match its pinned checksum, so it wasn't installed.");
                    }
                }
                var have = state.Find(p.Id);
                if (have != null)
                {
                    RemoveFiles(game, have.Files, state);
                    state.Packages.Remove(have);
                }
                var files = Extract(game, p.Id, zip, state);
                state.Packages.Add(new InstalledPackage { Id = p.Id, Version = p.Version, Files = files });
                SaveState(game, state); // after every mod, so an interrupted install can resume
                doneBytes += p.Size;
                progress.Report(from + (int)((to - from) * doneBytes / totalBytes));
            }

            state.Groups = groups.Distinct().ToList();
            SaveState(game, state);
            File.WriteAllText(LockFile(game), Json.Write(lockFile));
            progress.Report(to);
        }

        // Folders mods in the pack create while the game runs (generated hooks, caches, downloaded cosmetics).
        private static readonly string[] Generated = { "BepInEx/plugins/MMHOOK", "BepInEx/plugins/MoreCompanyCosmetics" };

        public static void UninstallAll(string game, Action<string> log)
        {
            var state = LoadState(game);
            if (state == null) return;
            foreach (var p in state.Packages)
            {
                RemoveFiles(game, p.Files, state);
                // Each mod's own folders are ours entirely, including anything it generated in them.
                foreach (var route in new[] { "plugins", "patchers", "core", "monomod" })
                    foreach (var root in new[] { "BepInEx", DisabledRoot + "/BepInEx" })
                    {
                        var dir = Path.Combine(game, root, route, p.Id);
                        if (Directory.Exists(dir)) Directory.Delete(dir, true);
                    }
            }
            foreach (var dir in Generated.Select(d => Path.Combine(game, d)).Where(Directory.Exists))
                Directory.Delete(dir, true);
            log($"Removed {state.Packages.Count} pack mods.");
            foreach (var backup in state.Backups.ToList())
            {
                // Inside a pack mod's own folder: nobody else's file, and the folder is gone now. Don't recreate it.
                if (state.Packages.Any(p => new[] { "plugins", "patchers", "core", "monomod" }
                        .Any(r => backup.Key.StartsWith($"BepInEx/{r}/{p.Id}/", StringComparison.OrdinalIgnoreCase))))
                    continue;
                var original = Path.Combine(game, backup.Key);
                var saved = Path.Combine(game, backup.Value);
                if (!File.Exists(saved)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(original));
                if (File.Exists(original)) File.Delete(original);
                File.Move(saved, original);
            }
            if (state.Backups.Count > 0) log($"Restored {state.Backups.Count} files that were there before.");
            foreach (var dir in new[] { DisabledRoot, BackupRoot })
            {
                var full = Path.Combine(game, dir);
                if (Directory.Exists(full)) Directory.Delete(full, true);
            }
        }

        // ------------------------------------------------------------------ unpacking

        // Thunderstore's install rules for Lethal Company (the same ones r2modman uses): route, and whether each
        // mod gets its own sub-folder there.
        private static readonly Dictionary<string, (string route, bool perMod)> Routes = new Dictionary<string, (string, bool)>(StringComparer.OrdinalIgnoreCase)
        {
            { "plugins", ("BepInEx/plugins", true) },
            { "core", ("BepInEx/core", true) },
            { "patchers", ("BepInEx/patchers", true) },
            { "monomod", ("BepInEx/monomod", true) },
            { "config", ("BepInEx/config", false) },
        };

        /// <summary>
        /// Where a file from a Thunderstore zip goes, exactly as r2modman lays it out (mods rely on this, e.g. Mirage
        /// loads FSharp.Core.dll from the root of MirageCore's folder):
        /// - the first folder named plugins/core/patchers/monomod/config is copied to that route keeping what's below it;
        /// - every other file is flattened into BepInEx/plugins/&lt;mod&gt;/ (.mm.dll files into monomod).
        /// Returns a path relative to the game folder, or null to skip the file.
        /// </summary>
        public static string Map(string pkgId, string entry)
        {
            var p = entry.Replace('\\', '/').TrimStart('/');
            if (p.Length == 0 || p.EndsWith("/")) return null;
            var parts = p.Split('/');
            if (parts[parts.Length - 1].Equals(".gitignore", StringComparison.OrdinalIgnoreCase)) return null;
            for (int i = 0; i < parts.Length - 1; i++)
                if (Routes.TryGetValue(parts[i], out var r))
                {
                    var rest = string.Join("/", parts.Skip(i + 1));
                    return r.perMod ? $"{r.route}/{pkgId}/{rest}" : $"{r.route}/{rest}";
                }
            var file = parts[parts.Length - 1];
            if (file.EndsWith(".mm.dll", StringComparison.OrdinalIgnoreCase)) return $"BepInEx/monomod/{pkgId}/{file}";
            return $"BepInEx/plugins/{pkgId}/{file}";
        }

        private static List<string> Extract(string game, string pkgId, string zipPath, InstallState state)
        {
            var files = new List<string>();
            var root = Path.GetFullPath(game);
            using (var zip = ZipFile.OpenRead(zipPath))
                foreach (var entry in zip.Entries)
                {
                    var rel = Map(pkgId, entry.FullName);
                    if (rel == null) continue;
                    var dest = Path.GetFullPath(Path.Combine(game, rel));
                    if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // no escaping the game folder
                    bool isConfig = rel.StartsWith("BepInEx/config/", StringComparison.OrdinalIgnoreCase);
                    if (File.Exists(dest))
                    {
                        if (isConfig) continue; // never overwrite someone's settings
                        bool ours = state.Packages.Any(p => p.Files.Contains(rel, StringComparer.OrdinalIgnoreCase));
                        if (!ours && !state.Backups.ContainsKey(rel))
                        {
                            var backup = $"{BackupRoot}/{rel}";
                            var full = Path.Combine(game, backup);
                            Directory.CreateDirectory(Path.GetDirectoryName(full));
                            if (File.Exists(full)) File.Delete(full);
                            File.Move(dest, full);
                            state.Backups[rel] = backup;
                        }
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    entry.ExtractToFile(dest, true);
                    if (!isConfig) files.Add(rel);
                }
            return files;
        }

        private static void RemoveFiles(string game, IEnumerable<string> files, InstallState state)
        {
            foreach (var rel in files)
            {
                // The in-game Mods tab may have moved a disabled mod's files under DisabledRoot.
                foreach (var path in new[] { Path.Combine(game, rel), Path.Combine(game, DisabledRoot, rel) })
                    if (File.Exists(path)) File.Delete(path);
                PruneEmptyDirs(game, Path.GetDirectoryName(Path.Combine(game, rel)));
            }
        }

        private static void PruneEmptyDirs(string game, string dir)
        {
            var stop = Path.GetFullPath(Path.Combine(game, "BepInEx"));
            while (!string.IsNullOrEmpty(dir) && Path.GetFullPath(dir).Length > stop.Length && Directory.Exists(dir) &&
                   !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
                dir = Path.GetDirectoryName(dir);
            }
        }

        private static bool Exists(string game, string rel) =>
            File.Exists(Path.Combine(game, rel)) || File.Exists(Path.Combine(game, DisabledRoot, rel));

        private static bool HashMatches(string path, string sha256)
        {
            if (string.IsNullOrEmpty(sha256)) return true;
            using (var s = File.OpenRead(path))
            using (var h = SHA256.Create())
                return BitConverter.ToString(h.ComputeHash(s)).Replace("-", "").Equals(sha256, StringComparison.OrdinalIgnoreCase);
        }

        private static async Task DownloadWithRetry(HttpClient http, string url, string path)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                    {
                        response.EnsureSuccessStatusCode();
                        var tmp = path + ".part";
                        using (var input = await response.Content.ReadAsStreamAsync())
                        using (var output = File.Create(tmp))
                            await input.CopyToAsync(output);
                        if (File.Exists(path)) File.Delete(path);
                        File.Move(tmp, path);
                        return;
                    }
                }
                catch when (attempt < 4)
                {
                    await Task.Delay(2000 * attempt); // Thunderstore rate-limits bursts
                }
            }
        }
    }
}
