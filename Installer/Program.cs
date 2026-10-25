using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace OverstretchedInstaller
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12; // GitHub requires TLS 1.2
            string Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
            // Self-update, second half: this is the new installer, copy it over the old one.
            var replace = Arg("--replace");
            if (replace != null) Installer.ReplaceOldInstaller(replace);
            bool upgrade = args.Contains("--upgrade");
            if (args.Any(a => a == "--status" || a == "--install" || a == "--update" || a == "--uninstall")) return RunCommandLine(args);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(upgrade, Arg("--game")));
            return 0;
        }

        /// <summary>
        /// Headless mode: OverstretchedInstaller.exe --status|--install|--update|--uninstall [--game "path"]
        /// [--groups core,monsters,...] [--remove-bepinex]
        /// </summary>
        private static int RunCommandLine(string[] args)
        {
            string Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
            var game = Arg("--game") ?? Installer.FindGame();
            void Out(string s) => Console.WriteLine(s);
            try
            {
                if (!Installer.IsGameFolder(game)) { Out("Lethal Company not found. Pass --game \"<folder>\"."); return 2; }
                Out("Game: " + game);
                if (args.Contains("--status"))
                {
                    var latest = Installer.LatestRelease().GetAwaiter().GetResult();
                    var installed = Installer.InstalledVersion(game);
                    Out($"BepInEx: {(Installer.BepInExInstalled(game) ? "yes" : "no")}  Installed: {installed ?? "none"}  Latest: {latest.Version}  " +
                        $"Update available: {(installed != null && Installer.IsNewer(latest.Version, installed) ? "yes" : "no")}");
                    var pack = Installer.LoadLock(latest).GetAwaiter().GetResult();
                    if (pack != null)
                    {
                        var groups = Installer.ChosenGroups(game, pack, Arg("--groups"));
                        var state = PackInstaller.LoadState(game);
                        Out($"Pack: {pack.Resolve(groups).Count} mods for groups [{string.Join(",", groups)}], " +
                            $"{state?.Packages.Count ?? 0} installed, {PackInstaller.PendingChanges(game, pack, groups).Count} to install or update");
                    }
                }
                if (args.Contains("--install") || args.Contains("--update"))
                {
                    if (Installer.GameRunning()) { Out("Close Lethal Company first."); return 3; }
                    var latest = Installer.LatestRelease().GetAwaiter().GetResult();
                    var pack = Installer.LoadLock(latest).GetAwaiter().GetResult();
                    var groups = pack != null ? Installer.ChosenGroups(game, pack, Arg("--groups")) : new List<string>();
                    Installer.Install(game, latest, pack, groups, Out, new Progress<int>()).GetAwaiter().GetResult();
                }
                if (args.Contains("--uninstall"))
                {
                    if (Installer.GameRunning()) { Out("Close Lethal Company first."); return 3; }
                    Installer.Uninstall(game, args.Contains("--remove-bepinex"), Out);
                }
                return 0;
            }
            catch (Exception ex)
            {
                Out("Error: " + ex.Message);
                return 1;
            }
        }
    }

    /// <summary>Everything that touches the game folder or the internet.</summary>
    internal static class Installer
    {
        public const string Repo = "PsychyBruh/Overstretched";
        public const string ModDll = "Overstretched.dll";
        private const string BepInExUrl = "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip";
        private const string GameExe = "Lethal Company.exe";

        private static readonly HttpClient http = CreateClient();

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("OverstretchedInstaller/1.0");
            return c;
        }

        // ------------------------------------------------------------------ finding the game

        public static string FindGame()
        {
            foreach (var library in SteamLibraries())
            {
                var dir = Path.Combine(library, "steamapps", "common", "Lethal Company");
                if (File.Exists(Path.Combine(dir, GameExe))) return dir;
            }
            return null;
        }

        private static IEnumerable<string> SteamLibraries()
        {
            var roots = new List<string>();
            foreach (var key in new[] { @"HKEY_CURRENT_USER\Software\Valve\Steam", @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam" })
            {
                var value = Registry.GetValue(key, key.StartsWith("HKEY_CURRENT") ? "SteamPath" : "InstallPath", null) as string;
                if (!string.IsNullOrEmpty(value)) roots.Add(value.Replace('/', '\\'));
            }
            roots.Add(@"C:\Program Files (x86)\Steam");
            foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                yield return root;
                var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(vdf)) continue;
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    yield return m.Groups[1].Value.Replace(@"\\", @"\");
            }
        }

        public static bool IsGameFolder(string dir) => !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, GameExe));

        public static bool GameRunning() => Process.GetProcessesByName("Lethal Company").Length > 0;

        // ------------------------------------------------------------------ status

        public static string PluginDir(string game) => Path.Combine(game, "BepInEx", "plugins", "Overstretched");
        private static string VersionFile(string game) => Path.Combine(PluginDir(game), "version.txt");

        public static bool BepInExInstalled(string game) =>
            File.Exists(Path.Combine(game, "winhttp.dll")) && File.Exists(Path.Combine(game, "BepInEx", "core", "BepInEx.dll"));

        public static string InstalledVersion(string game)
        {
            if (!File.Exists(Path.Combine(PluginDir(game), ModDll))) return null;
            return File.Exists(VersionFile(game)) ? File.ReadAllText(VersionFile(game)).Trim() : "unknown";
        }

        public sealed class Release
        {
            public string Version;
            public string DllUrl;
            public string LockUrl; // pack.lock.json; missing on releases before 2.0
            public string InstallerUrl;
            public string Notes;
        }

        public static async Task<Release> LatestRelease()
        {
            var json = await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");
            string Field(string name) => Regex.Match(json, "\"" + name + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"").Groups[1].Value;
            var assets = Regex.Matches(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+)\"")
                .Cast<Match>().Select(m => m.Groups[1].Value).ToList();
            var dll = assets.FirstOrDefault(u => u.EndsWith("/" + ModDll, StringComparison.OrdinalIgnoreCase));
            if (dll == null) throw new Exception("The latest release has no " + ModDll + " attached.");
            return new Release
            {
                Version = Field("tag_name").TrimStart('v', 'V'),
                DllUrl = dll,
                LockUrl = assets.FirstOrDefault(u => u.EndsWith("/pack.lock.json", StringComparison.OrdinalIgnoreCase)),
                InstallerUrl = assets.FirstOrDefault(u => u.EndsWith("/OverstretchedInstaller.exe", StringComparison.OrdinalIgnoreCase)),
                Notes = Regex.Unescape(Field("body")),
            };
        }

        /// <summary>The release's pack list, or null for releases without one. OVERSTRETCHED_LOCK=path overrides it (testing).</summary>
        public static async Task<PackLock> LoadLock(Release release)
        {
            var local = Environment.GetEnvironmentVariable("OVERSTRETCHED_LOCK");
            if (!string.IsNullOrEmpty(local) && File.Exists(local)) return Json.Parse<PackLock>(File.ReadAllText(local));
            if (release?.LockUrl == null) return null;
            return Json.Parse<PackLock>(await http.GetStringAsync(release.LockUrl));
        }

        /// <summary>Groups to install: given on the command line, else what was installed last time, else the pack's defaults.</summary>
        public static List<string> ChosenGroups(string game, PackLock pack, string fromArgs = null)
        {
            var known = new HashSet<string>(pack.Groups.Select(g => g.Id));
            IEnumerable<string> chosen = fromArgs != null ? fromArgs.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim())
                : PackInstaller.LoadState(game)?.Groups is List<string> last && last.Count > 0 ? last
                : pack.Groups.Where(g => g.Default).Select(g => g.Id);
            return chosen.Concat(pack.Groups.Where(g => g.Required).Select(g => g.Id)).Where(known.Contains).Distinct().ToList();
        }

        public static string OwnVersion => typeof(Installer).Assembly.GetName().Version.ToString(3);

        /// <summary>
        /// Self-update, first half: download the release's installer and start it (it replaces this exe, then carries
        /// on with the same job). Returns false when this installer is already the latest.
        /// </summary>
        public static async Task<bool> StartNewerInstaller(Release release, string extraArgs)
        {
            if (release?.InstallerUrl == null || !IsNewer(release.Version, OwnVersion)) return false;
            var tmp = Path.Combine(Path.GetTempPath(), $"OverstretchedInstaller-{release.Version}.exe");
            await Download(release.InstallerUrl, tmp, new Progress<int>(), 0, 100);
            Process.Start(tmp, $"--replace \"{Application.ExecutablePath}\" {extraArgs}".Trim());
            return true;
        }

        public static void ReplaceOldInstaller(string oldPath)
        {
            for (int i = 0; i < 20; i++) // the old one may still be closing
            {
                try { File.Copy(Application.ExecutablePath, oldPath, true); return; }
                catch { System.Threading.Thread.Sleep(500); }
            }
        }

        public static bool IsNewer(string latest, string installed) =>
            installed == null || installed == "unknown" ||
            (Version.TryParse(latest, out var a) && Version.TryParse(installed, out var b) ? a > b : latest != installed);

        // ------------------------------------------------------------------ actions

        public static async Task Install(string game, Release release, PackLock pack, List<string> groups, Action<string> log, IProgress<int> progress)
        {
            if (!BepInExInstalled(game))
            {
                log("BepInEx (the mod loader) isn't installed yet. Downloading it...");
                var zipPath = Path.Combine(Path.GetTempPath(), "BepInEx_Overstretched.zip");
                await Download(BepInExUrl, zipPath, progress, 0, 10);
                log("Installing BepInEx...");
                using (var zip = ZipFile.OpenRead(zipPath))
                    foreach (var entry in zip.Entries)
                    {
                        var dest = Path.GetFullPath(Path.Combine(game, entry.FullName));
                        if (!dest.StartsWith(Path.GetFullPath(game), StringComparison.OrdinalIgnoreCase)) continue;
                        if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(dest); continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        entry.ExtractToFile(dest, true);
                    }
                File.Delete(zipPath);
                log("BepInEx installed.");
            }
            else log("BepInEx is already installed.");

            log($"Downloading Overstretched v{release.Version}...");
            Directory.CreateDirectory(PluginDir(game));
            var tmp = Path.Combine(PluginDir(game), ModDll + ".download");
            await Download(release.DllUrl, tmp, progress, 10, 15);
            var final = Path.Combine(PluginDir(game), ModDll);
            if (File.Exists(final)) File.Delete(final);
            File.Move(tmp, final);
            File.WriteAllText(VersionFile(game), release.Version);
            log($"Overstretched v{release.Version} installed.");

            if (pack == null) { progress.Report(100); return; }
            log($"Mod pack: {pack.Resolve(groups).Count} mods for Lethal Company {pack.Game} (groups: {string.Join(", ", groups)}).");
            await PackInstaller.Sync(game, pack, groups, http, log, progress, 15, 100);
            log("Mod pack installed. Credits for every mod are in the F8 menu > Mods.");
        }

        public static void Uninstall(string game, bool removeBepInEx, Action<string> log)
        {
            PackInstaller.UninstallAll(game, log); // needs installed.json, which lives in the plugin folder
            var preloader = Path.Combine(game, "BepInEx", "patchers", "Overstretched");
            if (Directory.Exists(preloader)) Directory.Delete(preloader, true);
            if (Directory.Exists(PluginDir(game)))
            {
                Directory.Delete(PluginDir(game), true);
                log("Removed Overstretched.");
            }
            else log("Overstretched wasn't installed.");
            // Also clean up the old pre-rename build if it's lying around.
            var legacy = Path.Combine(game, "BepInEx", "plugins", "LethalPlus");
            if (Directory.Exists(legacy)) { Directory.Delete(legacy, true); log("Removed old LethalPlus build."); }
            if (!removeBepInEx) return;
            foreach (var f in new[] { "winhttp.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt" })
            {
                var p = Path.Combine(game, f);
                if (File.Exists(p)) File.Delete(p);
            }
            var bep = Path.Combine(game, "BepInEx");
            if (Directory.Exists(bep)) Directory.Delete(bep, true);
            log("Removed BepInEx (and any other mods in it).");
        }

        private static async Task Download(string url, string path, IProgress<int> progress, int from, int to)
        {
            using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? -1;
                using (var input = await response.Content.ReadAsStreamAsync())
                using (var output = File.Create(path))
                {
                    var buffer = new byte[81920];
                    long done = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await output.WriteAsync(buffer, 0, read);
                        done += read;
                        if (total > 0) progress.Report(from + (int)((to - from) * done / total));
                    }
                }
            }
            progress.Report(to);
        }
    }

    /// <summary>The window: logo, status, game folder, Install / Update / Uninstall, progress and log.</summary>
    internal sealed class MainForm : Form
    {
        private static readonly Color Bg = Color.FromArgb(14, 14, 16);
        private static readonly Color Panel = Color.FromArgb(26, 26, 30);
        private static readonly Color Accent = Color.FromArgb(214, 92, 38);   // company orange
        private static readonly Color Danger = Color.FromArgb(150, 34, 34);
        private static readonly Color Fg = Color.FromArgb(225, 222, 215);
        private static readonly Color Dim = Color.FromArgb(140, 138, 132);

        private readonly TextBox pathBox = new TextBox();
        private readonly Label status = new Label();
        private readonly Label versions = new Label();
        private readonly Button installButton, updateButton, uninstallButton, browseButton;
        private readonly CheckBox removeLoader = new CheckBox();
        private readonly ProgressBar bar = new ProgressBar();
        private readonly TextBox log = new TextBox();
        private readonly CheckedListBox groupList = new CheckedListBox();
        private readonly Label packSummary = new Label();
        private Installer.Release latest;
        private PackLock pack;
        private bool busy, loadingGroups;
        private readonly bool upgrade;
        private readonly string gameArg;

        /// <param name="upgrade">Started by the game (Overstretched 2.0 offering the mod pack): wait for the game to
        /// close, then install by itself.</param>
        public MainForm(bool upgrade = false, string gameArg = null)
        {
            this.upgrade = upgrade;
            this.gameArg = gameArg;
            Text = "Overstretched Installer";
            ClientSize = new Size(980, 640);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Bg;
            ForeColor = Fg;
            Font = new Font("Segoe UI", 10f);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var logo = new PictureBox { Location = new Point(24, 20), Size = new Size(112, 112), SizeMode = PictureBoxSizeMode.Zoom };
            using (var s = typeof(MainForm).Assembly.GetManifestResourceStream("OverstretchedInstaller.logo.png"))
                if (s != null) logo.Image = Image.FromStream(s);
            Controls.Add(logo);
            Controls.Add(new Label
            {
                Text = "OVERSTRETCHED", Location = new Point(152, 30), AutoSize = true,
                Font = new Font("Segoe UI Black", 26f, FontStyle.Bold), ForeColor = Accent,
            });
            Controls.Add(new Label
            {
                Text = "A Lethal Company mod. Something tall is working your shift.", Location = new Point(156, 80),
                AutoSize = true, ForeColor = Dim,
            });
            versions.SetBounds(156, 104, 440, 22);
            versions.ForeColor = Dim;
            Controls.Add(versions);

            Controls.Add(new Label { Text = "Lethal Company folder", Location = new Point(24, 150), AutoSize = true, ForeColor = Dim });
            pathBox.SetBounds(24, 174, 470, 28);
            pathBox.BackColor = Panel;
            pathBox.ForeColor = Fg;
            pathBox.BorderStyle = BorderStyle.FixedSingle;
            pathBox.TextChanged += (s, e) => RefreshStatus();
            Controls.Add(pathBox);
            browseButton = MakeButton("Browse...", new Rectangle(502, 172, 94, 32), Panel);
            browseButton.Click += (s, e) => Browse();

            status.SetBounds(24, 216, 572, 48);
            status.Font = new Font("Segoe UI Semibold", 12f);
            Controls.Add(status);

            installButton = MakeButton("INSTALL", new Rectangle(24, 276, 184, 56), Accent);
            updateButton = MakeButton("UPDATE", new Rectangle(218, 276, 184, 56), Color.FromArgb(52, 92, 140));
            uninstallButton = MakeButton("UNINSTALL", new Rectangle(412, 276, 184, 56), Danger);
            installButton.Click += async (s, e) => await Run(InstallOrUpdate);
            updateButton.Click += async (s, e) => await Run(InstallOrUpdate);
            uninstallButton.Click += async (s, e) => await Run(Uninstall);

            removeLoader.Text = "When uninstalling, also remove BepInEx (removes ALL BepInEx mods)";
            removeLoader.SetBounds(24, 340, 572, 26);
            removeLoader.CheckedChanged += (s, e) => RefreshStatus();
            removeLoader.ForeColor = Dim;
            Controls.Add(removeLoader);

            bar.SetBounds(24, 374, 572, 10);
            bar.Style = ProgressBarStyle.Continuous;
            Controls.Add(bar);

            log.SetBounds(24, 396, 572, 196);
            log.Multiline = true;
            log.ReadOnly = true;
            log.ScrollBars = ScrollBars.Vertical;
            log.BackColor = Panel;
            log.ForeColor = Fg;
            log.BorderStyle = BorderStyle.None;
            log.Font = new Font("Consolas", 9.5f);
            Controls.Add(log);

            var link = new LinkLabel
            {
                Text = "github.com/" + Installer.Repo, Location = new Point(24, 604), AutoSize = true,
                LinkColor = Dim, ActiveLinkColor = Accent,
            };
            link.LinkClicked += (s, e) => Process.Start("https://github.com/" + Installer.Repo);
            Controls.Add(link);
            Controls.Add(new Label
            {
                Text = "Everyone in the lobby needs the same mods.", Location = new Point(320, 604),
                AutoSize = true, ForeColor = Dim,
            });

            // Right column: which parts of the mod pack to install.
            Controls.Add(new Label
            {
                Text = "MOD PACK", Location = new Point(620, 24), AutoSize = true,
                Font = new Font("Segoe UI Black", 14f, FontStyle.Bold), ForeColor = Accent,
            });
            Controls.Add(new Label
            {
                Text = "Other creators' mods, downloaded from their official\nThunderstore pages. Tick what you want.",
                Location = new Point(622, 56), AutoSize = true, ForeColor = Dim,
            });
            groupList.SetBounds(622, 104, 334, 370);
            groupList.BackColor = Panel;
            groupList.ForeColor = Fg;
            groupList.BorderStyle = BorderStyle.None;
            groupList.CheckOnClick = true;
            groupList.Font = new Font("Segoe UI", 10.5f);
            groupList.ItemCheck += (s, e) =>
            {
                if (loadingGroups || pack == null) return;
                if (pack.Groups[e.Index].Required) { e.NewValue = CheckState.Checked; return; } // core can't be unticked
                BeginInvoke(new Action(RefreshStatus));
            };
            Controls.Add(groupList);
            packSummary.SetBounds(622, 482, 334, 66);
            packSummary.ForeColor = Dim;
            Controls.Add(packSummary);
            var credits = MakeButton("CREDITS", new Rectangle(622, 552, 334, 40), Panel);
            credits.Click += (s, e) => Process.Start($"https://github.com/{Installer.Repo}/blob/main/THIRD_PARTY_NOTICES.md");

            Shown += async (s, e) => await Startup();
        }

        private Button MakeButton(string text, Rectangle bounds, Color color)
        {
            var b = new Button
            {
                Text = text, Bounds = bounds, BackColor = color, ForeColor = Color.White, FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI Semibold", 11f), Cursor = Cursors.Hand,
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(color, 0.2f);
            Controls.Add(b);
            return b;
        }

        private void Log(string line)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(Log), line); return; }
            log.AppendText(line + Environment.NewLine);
        }

        private async Task Startup()
        {
            var found = Installer.IsGameFolder(gameArg) ? gameArg : Installer.FindGame();
            if (found != null) { pathBox.Text = found; Log("Found Lethal Company: " + found); }
            else Log("Couldn't find Lethal Company automatically. Click Browse and pick the folder with \"Lethal Company.exe\".");
            Log("Checking GitHub for the latest version...");
            try
            {
                latest = await Installer.LatestRelease();
                Log($"Latest version: v{latest.Version}");
                var extra = (upgrade ? "--upgrade " : "") + (Installer.IsGameFolder(pathBox.Text.Trim()) ? $"--game \"{pathBox.Text.Trim()}\"" : "");
                if (await Installer.StartNewerInstaller(latest, extra))
                {
                    Log("A newer installer is out; switching to it...");
                    Close();
                    return;
                }
                pack = await Installer.LoadLock(latest);
                if (pack != null) Log($"Mod pack: {pack.Packages.Count(p => p.Top)} mods (+{pack.Packages.Count(p => !p.Top)} libraries) for Lethal Company {pack.Game}.");
                FillGroups();
            }
            catch (Exception ex)
            {
                Log("Couldn't reach GitHub: " + ex.Message);
            }
            RefreshStatus();
            if (upgrade) await WaitForGameThenInstall();
        }

        private async Task WaitForGameThenInstall()
        {
            var game = pathBox.Text.Trim();
            if (!Installer.IsGameFolder(game) || latest == null) return;
            if (Installer.GameRunning())
            {
                SetStatus("Close Lethal Company: the mod pack installs as soon as it's closed.", Color.FromArgb(230, 190, 90));
                Log("Waiting for Lethal Company to close...");
                while (Installer.GameRunning()) await Task.Delay(1000);
                await Task.Delay(1500); // let it release its files
            }
            TopMost = true; Activate(); TopMost = false;
            Log("Installing the mod pack...");
            await Run(InstallOrUpdate);
        }

        private void Browse()
        {
            using (var dialog = new FolderBrowserDialog { Description = "Pick your Lethal Company folder (the one with \"Lethal Company.exe\")" })
                if (dialog.ShowDialog(this) == DialogResult.OK) pathBox.Text = dialog.SelectedPath;
        }

        private void FillGroups()
        {
            if (pack == null) { packSummary.Text = "This release has no mod pack."; return; }
            loadingGroups = true;
            groupList.Items.Clear();
            var game = pathBox.Text.Trim();
            var chosen = Installer.IsGameFolder(game) ? Installer.ChosenGroups(game, pack) : pack.Groups.Where(g => g.Default).Select(g => g.Id).ToList();
            foreach (var g in pack.Groups)
            {
                var mods = pack.Resolve(new[] { g.Id }).Where(p => p.Group == g.Id).ToList();
                groupList.Items.Add($"{g.Name}  ({mods.Count} mods, {mods.Sum(p => p.Size) / 1048576} MB){(g.Required ? "  required" : "")}",
                                    chosen.Contains(g.Id));
            }
            loadingGroups = false;
        }

        private List<string> SelectedGroups() =>
            pack == null ? new List<string>() : groupList.CheckedIndices.Cast<int>().Select(i => pack.Groups[i].Id).ToList();

        private void RefreshStatus()
        {
            var game = pathBox.Text.Trim();
            bool valid = Installer.IsGameFolder(game);
            string installed = valid ? Installer.InstalledVersion(game) : null;
            versions.Text = $"Installed: {Show(installed) ?? "none"}    Latest: {(latest != null ? Show(latest.Version) : "?")}";

            int pending = 0, removals = 0;
            if (pack != null)
            {
                var groups = SelectedGroups();
                var wanted = pack.Resolve(groups);
                pending = valid ? PackInstaller.PendingChanges(game, pack, groups).Count : wanted.Count;
                var state = valid ? PackInstaller.LoadState(game) : null;
                removals = state?.Packages.Count(p => wanted.All(w => w.Id != p.Id)) ?? 0;
                packSummary.Text = $"{wanted.Count} mods selected (with libraries), {wanted.Sum(p => p.Size) / 1048576} MB.\n" +
                                   (installed == null ? "" : pending + removals == 0 ? "Pack is up to date." :
                                       $"{pending} to install or update{(removals > 0 ? $", {removals} to remove" : "")}.");
            }
            bool packChanges = installed != null && pending + removals > 0;

            if (!valid) { SetStatus("Pick your Lethal Company folder.", Dim); }
            else if (installed == null) SetStatus("Not installed.", Fg);
            else if (latest != null && Installer.IsNewer(latest.Version, installed))
                SetStatus($"Update available: {Show(installed)} -> {Show(latest.Version)}", Color.FromArgb(120, 170, 230));
            else if (packChanges) SetStatus("Mod pack changes ready: click UPDATE.", Color.FromArgb(120, 170, 230));
            else SetStatus($"Installed and up to date ({Show(installed)}).", Color.FromArgb(120, 200, 120));

            installButton.Enabled = !busy && valid && latest != null && installed == null;
            updateButton.Enabled = !busy && valid && latest != null && installed != null && (Installer.IsNewer(latest.Version, installed) || packChanges);
            groupList.Enabled = !busy;
            uninstallButton.Enabled = !busy && valid && (installed != null || removeLoader.Checked);
            browseButton.Enabled = !busy;
            foreach (var b in new[] { installButton, updateButton, uninstallButton })
                b.BackColor = b.Enabled ? (b == installButton ? Accent : b == updateButton ? Color.FromArgb(52, 92, 140) : Danger) : Color.FromArgb(48, 48, 52);
        }

        private static string Show(string version) => version == null ? null : version == "unknown" ? "unknown version" : "v" + version;

        private void SetStatus(string text, Color color)
        {
            status.Text = text;
            status.ForeColor = color;
        }

        private async Task Run(Func<string, Task> action)
        {
            var game = pathBox.Text.Trim();
            if (Installer.GameRunning())
            {
                MessageBox.Show(this, "Close Lethal Company first, then try again.", "Overstretched", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            busy = true;
            RefreshStatus();
            bar.Value = 0;
            try
            {
                await action(game);
            }
            catch (UnauthorizedAccessException)
            {
                Log("Windows blocked writing to the game folder. Right-click the installer and choose \"Run as administrator\".");
            }
            catch (Exception ex)
            {
                Log("Something went wrong: " + ex.Message);
            }
            finally
            {
                busy = false;
                RefreshStatus();
            }
        }

        private async Task InstallOrUpdate(string game)
        {
            if (latest == null) latest = await Installer.LatestRelease();
            if (pack == null) pack = await Installer.LoadLock(latest);
            var progress = new Progress<int>(v => bar.Value = Math.Max(0, Math.Min(100, v)));
            await Installer.Install(game, latest, pack, SelectedGroups(), Log, progress);
            if (!string.IsNullOrWhiteSpace(latest.Notes)) Log("What's new:" + Environment.NewLine + latest.Notes.Trim());
            Log("Done! Start Lethal Company and press F8 in game for the menu.");
        }

        private Task Uninstall(string game)
        {
            if (removeLoader.Checked &&
                MessageBox.Show(this, "This also deletes BepInEx and EVERY mod installed in it. Continue?", "Remove BepInEx",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return Task.CompletedTask;
            Installer.Uninstall(game, removeLoader.Checked, Log);
            bar.Value = 100;
            return Task.CompletedTask;
        }
    }
}
