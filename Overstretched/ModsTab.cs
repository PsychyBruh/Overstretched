using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using UnityEngine;
using static Overstretched.UiKit;

namespace Overstretched
{
    /// <summary>The Mods tab: every mod in the pack with credits, a next-launch on/off switch and a settings editor.</summary>
    public partial class ModMenu
    {
        private string modSearch = "";
        private int modGroup; // 0 = all
        private bool showLibraries;
        private readonly HashSet<string> openMods = new HashSet<string>();
        private readonly HashSet<string> openSections = new HashSet<string>();
        private readonly Dictionary<ConfigEntryBase, string> numberDrafts = new Dictionary<ConfigEntryBase, string>();

        private void DrawMods()
        {
            var groups = ModPack.Present ? ModPack.Pack.groups.Where(g => ModPack.Pack.packages.Any(p => p.group == g.id)).ToList() : new List<ModPack.Group>();
            BeginCard();
            if (!ModPack.Present)
            {
                Callout("The mod pack isn't installed yet. Install it here: the installer opens, and once you close the game it " +
                        "adds the pack by itself. Every loaded BepInEx mod is listed below anyway.", Blue);
                GUILayout.BeginHorizontal();
                if (!PackUpgrade.Busy && Button(PackUpgrade.Launched ? "Close the game to finish" : "Install the mod pack",
                                                Kind.Accent, GUILayout.Height(36), GUILayout.Width(240)))
                {
                    if (PackUpgrade.Launched) Application.Quit();
                    else PackUpgrade.Begin();
                }
                if (PackUpgrade.StatusText != null) { GUILayout.Space(8); GUILayout.Label(PackUpgrade.StatusText, LabelSmall); }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }
            else
            {
                var pkgs = ModPack.Pack.packages;
                int loaded = pkgs.Count(p => ModPack.PluginsOf(p).Count > 0);
                int off = pkgs.Count(p => ModPack.IsDisabledOnDisk(p));
                GUILayout.BeginHorizontal();
                GUILayout.Label($"<b>{pkgs.Count(p => p.top)}</b> mods + {pkgs.Count(p => !p.top)} libraries · built for Lethal Company {ModPack.Pack.game}", UiKit.Label);
                GUILayout.FlexibleSpace();
                Badge($"{Chainloader.PluginInfos.Count} plugins loaded", Green);
                if (off > 0) { GUILayout.Space(4); Badge($"{off} off", Dim); }
                GUILayout.EndHorizontal();
                if (ModPack.AnyPending) Callout("Your on/off changes apply the next time you start the game.", Yellow);
            }
            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            modSearch = TextField(modSearch, "Search mods, authors, settings...", GUILayout.Width(UiKit.ContentWidth * 0.62f));
            GUILayout.Space(8);
            showLibraries = Switch("Libraries", showLibraries);
            GUILayout.EndHorizontal();
            if (groups.Count > 0)
            {
                GUILayout.Space(4);
                var names = new List<string> { "All" };
                names.AddRange(groups.Select(g => g.name));
                // Chips sized to their text, wrapping onto new rows.
                int index = 0;
                while (index < names.Count)
                {
                    GUILayout.BeginHorizontal();
                    float used = 0f;
                    while (index < names.Count)
                    {
                        float w = ButtonText.CalcSize(new GUIContent(names[index])).x + 18f;
                        if (used > 0f && used + w + 6f > UiKit.ContentWidth) break;
                        if (Button(names[index], index == modGroup ? Kind.Accent : Kind.Ghost, GUILayout.Height(26), GUILayout.Width(w)))
                            modGroup = index;
                        used += w + 6f;
                        index++;
                    }
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();
                }
            }
            EndCard();

            scrolls[tab] = GUILayout.BeginScrollView(scrolls[tab]);
            if (ModPack.Present)
            {
                string groupId = modGroup > 0 && modGroup <= groups.Count ? groups[modGroup - 1].id : null;
                var list = ModPack.Pack.packages
                    .Where(p => showLibraries || p.top || groupId == "library")
                    .Where(p => groupId == null || p.group == groupId)
                    .Where(p => Matches(p.name, p.author, p.description, p.id))
                    .OrderBy(p => !p.top).ThenBy(p => p.name).ToList();
                if (list.Count == 0) Note("No mods match.");
                foreach (var p in list) DrawPackage(p);
            }
            else
            {
                foreach (var info in Chainloader.PluginInfos.Values.OrderBy(i => i.Metadata.Name))
                    if (Matches(info.Metadata.Name, info.Metadata.GUID)) DrawLoosePlugin(info);
            }
            GUILayout.Space(50);
            GUILayout.EndScrollView();
        }

        private bool Matches(params string[] fields) =>
            string.IsNullOrEmpty(modSearch) || fields.Any(f => f != null && f.IndexOf(modSearch, StringComparison.OrdinalIgnoreCase) >= 0);

        private void DrawPackage(ModPack.Package p)
        {
            var plugins = ModPack.PluginsOf(p);
            bool disabled = ModPack.IsDisabledOnDisk(p);
            bool willBeOn = ModPack.WillBeEnabled(p);
            bool isOpen = openMods.Contains(p.id);

            BeginCard();
            GUILayout.BeginHorizontal();
            var head = GUILayoutUtility.GetRect(10f, 30f, GUILayout.ExpandWidth(true));
            GUI.Label(new Rect(head.x, head.y, 20f, head.height), isOpen ? "▾" : "▸", LabelBold);
            GUI.Label(new Rect(head.x + 20f, head.y, head.width - 20f, head.height),
                $"{p.name}  <size=12><color={Rich(Dim)}>v{p.version} · {p.author}</color></size>", LabelBold);
            if (GUI.Button(head, GUIContent.none, GUIStyle.none)) { if (!openMods.Remove(p.id)) openMods.Add(p.id); }
            if (ModPack.HasPending(p)) Badge(willBeOn ? "ON NEXT LAUNCH" : "OFF NEXT LAUNCH", Yellow);
            else if (disabled) Badge("OFF", Dim);
            else if (plugins.Count > 0) Badge("LOADED", Green);
            else Badge(p.top ? "INSTALLED" : "LIBRARY", Blue);
            if (p.everyone) { GUILayout.Space(4); Badge("everyone", Purple); }
            GUILayout.EndHorizontal();

            if (isOpen)
            {
                if (!string.IsNullOrEmpty(p.description)) Note(p.description);
                GUILayout.Label($"License: {p.license}   ·   {ModPack.GroupName(p.group)}{(p.everyone ? "   ·   everyone in the lobby needs it" : "   ·   only you need it")}", LabelSmall);
                GUILayout.Space(4);
                GUILayout.BeginHorizontal();
                if (!string.IsNullOrEmpty(p.page) && Button("Thunderstore page", Kind.Ghost, GUILayout.Height(28), GUILayout.Width(160))) Application.OpenURL(p.page);
                if (!string.IsNullOrEmpty(p.source) && Button("Source", Kind.Ghost, GUILayout.Height(28), GUILayout.Width(90))) Application.OpenURL(p.source);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                if (ModPack.CanToggle(p))
                {
                    bool want = Switch("Turned on", willBeOn, "Changes apply the next time you start the game");
                    if (want != willBeOn)
                    {
                        var refused = ModPack.SetEnabled(p, want);
                        Toast(refused ?? $"{p.name} will be {(want ? "on" : "off")} next launch");
                    }
                }
                if (!p.top)
                {
                    var users = ModPack.Pack.packages.Where(u => u.deps != null && u.deps.Contains(p.id)).Select(u => u.name).ToList();
                    if (users.Count > 0) GUILayout.Label("Needed by: " + string.Join(", ", users), LabelSmall);
                }
                foreach (var info in plugins) DrawConfig(info, plugins.Count > 1);
            }
            EndCard();
        }

        private void DrawLoosePlugin(PluginInfo info)
        {
            bool isOpen = openMods.Contains(info.Metadata.GUID);
            BeginCard();
            var head = GUILayoutUtility.GetRect(10f, 30f, GUILayout.ExpandWidth(true));
            GUI.Label(head, $"{(isOpen ? "▾" : "▸")}  {info.Metadata.Name}  <size=12><color={Rich(Dim)}>v{info.Metadata.Version} · {info.Metadata.GUID}</color></size>", LabelBold);
            if (GUI.Button(head, GUIContent.none, GUIStyle.none)) { if (!openMods.Remove(info.Metadata.GUID)) openMods.Add(info.Metadata.GUID); }
            if (isOpen) DrawConfig(info, false);
            EndCard();
        }

        // ------------------------------------------------------------------ generic BepInEx config editor

        private void DrawConfig(PluginInfo info, bool showName)
        {
            var cfg = info.Instance != null ? info.Instance.Config : null;
            if (cfg == null || cfg.Count == 0) return;
            GUILayout.Space(6);
            SectionHeader(showName ? $"{info.Metadata.Name} settings" : "Settings");
            foreach (var section in cfg.Keys.Select(k => k.Section).Distinct())
            {
                var entries = cfg.Keys.Where(k => k.Section == section).Select(k => cfg[k])
                    .Where(e => Matches(e.Definition.Key, e.Description?.Description, section, info.Metadata.Name)).ToList();
                if (entries.Count == 0) continue;
                string key = info.Metadata.GUID + "/" + section;
                bool sectionOpen = openSections.Contains(key) || !string.IsNullOrEmpty(modSearch);
                if (Button($"{(sectionOpen ? "▾" : "▸")}  {section}  ({entries.Count})", Kind.Ghost, GUILayout.Height(26)))
                {
                    if (!openSections.Remove(key)) openSections.Add(key);
                }
                if (!sectionOpen) continue;
                foreach (var e in entries) DrawEntry(e);
                GUILayout.Space(4);
            }
        }

        private void DrawEntry(ConfigEntryBase e)
        {
            string label = e.Definition.Key;
            string hint = e.Description?.Description;
            if (!string.IsNullOrEmpty(hint))
            {
                hint = hint.Split('\n')[0];
                if (hint.Length > 90) hint = hint.Substring(0, 87) + "...";
            }
            var type = e.SettingType;
            var value = e.BoxedValue;
            object result = value;
            try
            {
                if (type == typeof(bool))
                    result = Switch(label, (bool)value, hint);
                else if ((type == typeof(int) || type == typeof(float)) && RangeOf(e, out float min, out float max))
                {
                    float f = Slider(label, Convert.ToSingle(value), min, max, type == typeof(int) ? "0" : "0.##", "");
                    result = type == typeof(int) ? (object)Mathf.RoundToInt(f) : f;
                }
                else if (type.IsEnum)
                {
                    var values = Enum.GetValues(type);
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(label, UiKit.Label, GUILayout.Width(UiKit.ContentWidth * 0.45f));
                    if (Button(value.ToString(), Kind.Normal, GUILayout.Height(28)))
                        result = values.GetValue((Array.IndexOf(values, value) + 1) % values.Length);
                    GUILayout.EndHorizontal();
                }
                else if (type == typeof(string) && e.Description?.AcceptableValues is AcceptableValueList<string> options)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(label, UiKit.Label, GUILayout.Width(UiKit.ContentWidth * 0.45f));
                    if (Button((string)value, Kind.Normal, GUILayout.Height(28)))
                        result = options.AcceptableValues[(Array.IndexOf(options.AcceptableValues, (string)value) + 1) % options.AcceptableValues.Length];
                    GUILayout.EndHorizontal();
                }
                else if (type == typeof(string) || type == typeof(int) || type == typeof(float) || type == typeof(double) || type == typeof(long))
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(label, UiKit.Label, GUILayout.Width(UiKit.ContentWidth * 0.45f));
                    if (!numberDrafts.TryGetValue(e, out var draft)) draft = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
                    var typed = TextField(draft, "", GUILayout.ExpandWidth(true));
                    numberDrafts[e] = typed;
                    if (typed != draft)
                    {
                        try { result = Convert.ChangeType(typed, type, System.Globalization.CultureInfo.InvariantCulture); } catch { }
                    }
                    GUILayout.EndHorizontal();
                }
                else
                {
                    GUILayout.Label($"{label}: <color={Rich(Dim)}>{value} (edit in BepInEx/config)</color>", UiKit.Label);
                }
            }
            catch (Exception ex) { GUILayout.Label($"{label}: <color={Rich(Red)}>{ex.Message}</color>", LabelSmall); }
            if (!Equals(result, value))
            {
                try { e.BoxedValue = result; }
                catch (Exception ex) { Toast($"Couldn't set {label}: {ex.Message}"); }
            }
        }

        private static bool RangeOf(ConfigEntryBase e, out float min, out float max)
        {
            min = max = 0f;
            var range = e.Description?.AcceptableValues;
            if (range == null) return false;
            var t = range.GetType();
            var lo = t.GetProperty("MinValue")?.GetValue(range);
            var hi = t.GetProperty("MaxValue")?.GetValue(range);
            if (lo == null || hi == null) return false;
            min = Convert.ToSingle(lo);
            max = Convert.ToSingle(hi);
            return max > min;
        }
    }
}
