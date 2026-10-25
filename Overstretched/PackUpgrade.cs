using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using BepInEx;
using UnityEngine;
using UnityEngine.Networking;
using static Overstretched.UiKit;

namespace Overstretched
{
    /// <summary>
    /// Upgrade path for people who updated with the old 1.x installer: it only fetches Overstretched.dll, so this
    /// DLL offers the mod pack itself. On the main menu (and in the Mods tab) it downloads the current installer
    /// and starts it in upgrade mode, which waits for the game to close and then installs the pack.
    /// </summary>
    public class PackUpgrade : MonoBehaviour
    {
        private const string InstallerUrl = "https://github.com/PsychyBruh/Overstretched/releases/latest/download/OverstretchedInstaller.exe";

        public static PackUpgrade Instance { get; private set; }

        private enum Stage { Idle, Downloading, Launched, Failed }
        private static Stage stage;
        private static float progress;
        private static string error = "";
        private bool hiddenThisSession;
        private Rect window;

        private void Awake() => Instance = this;

        /// <summary>Show the main-menu offer: pack missing, not dismissed for good, on the main menu.</summary>
        private bool ShouldOffer =>
            !ModPack.Present && !Settings.PackPromptDismissed && !hiddenThisSession && StartOfRound.Instance == null &&
            FindObjectOfType<MenuManager>() != null;

        public static string StatusText => stage switch
        {
            Stage.Downloading => $"Downloading the installer... {progress * 100f:0}%",
            Stage.Launched => "The installer is open. Close Lethal Company and it installs the mod pack by itself.",
            Stage.Failed => "Couldn't download the installer: " + error,
            _ => null,
        };

        public static bool Busy => stage == Stage.Downloading;
        public static bool Launched => stage == Stage.Launched;

        public static void Begin()
        {
            if (Instance == null || stage == Stage.Downloading) return;
            Instance.StartCoroutine(Instance.DownloadAndLaunch());
        }

        private IEnumerator DownloadAndLaunch()
        {
            stage = Stage.Downloading;
            progress = 0f;
            var path = Path.Combine(Path.GetTempPath(), "OverstretchedInstaller.exe");
            using (var req = UnityWebRequest.Get(InstallerUrl))
            {
                req.downloadHandler = new DownloadHandlerFile(path) { removeFileOnAbort = true };
                var op = req.SendWebRequest();
                while (!op.isDone) { progress = req.downloadProgress; yield return null; }
                if (req.result != UnityWebRequest.Result.Success)
                {
                    error = req.error;
                    stage = Stage.Failed;
                    yield break;
                }
            }
            try
            {
                // Upgrade mode: the installer waits for this game to close, then installs the pack.
                Process.Start(new ProcessStartInfo(path, $"--upgrade --game \"{Paths.GameRootPath}\"") { UseShellExecute = true });
                stage = Stage.Launched;
            }
            catch (Exception e)
            {
                error = e.Message;
                stage = Stage.Failed;
            }
        }

        private void OnGUI()
        {
            if (!ShouldOffer && stage != Stage.Launched) return;
            if (StartOfRound.Instance != null) return;
            Init();
            float w = 560f, h = stage == Stage.Idle ? 172f : 150f;
            window = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
            GUI.depth = -1000;
            RoundRect(new Rect(window.x - 6f, window.y - 4f, window.width + 12f, window.height + 12f), new Color(0f, 0f, 0f, 0.35f), 18f);
            RoundRect(window, Bg, 14f);
            RoundOutline(window, Alpha(Accent, 0.7f), 14f);
            GUILayout.BeginArea(new Rect(window.x + 22f, window.y + 18f, window.width - 44f, window.height - 32f));
            GUILayout.Label($"<color={Rich(Accent)}>OVER</color>STRETCHED 2.0", Title);
            GUILayout.Space(6);
            if (stage == Stage.Idle)
            {
                GUILayout.Label("Overstretched now comes with a mod pack: Tolian's moons, Mirage, new monsters, items, " +
                                "bigger lobbies, emotes and fixes. Your friends need it too to play together.", LabelDim);
                GUILayout.Space(10);
                GUILayout.BeginHorizontal();
                if (Button("Install mod pack", Kind.Accent, GUILayout.Height(38))) Begin();
                if (Button("Not now", Kind.Normal, GUILayout.Height(38), GUILayout.Width(110))) hiddenThisSession = true;
                if (Button("Don't ask again", Kind.Ghost, GUILayout.Height(38), GUILayout.Width(150)))
                {
                    Settings.PackPromptDismissed = true;
                    Settings.Save();
                }
                GUILayout.EndHorizontal();
                GUILayout.Label("You can always install it later from F8 > Mods.", LabelSmall);
            }
            else
            {
                GUILayout.Label(StatusText, LabelDim);
                GUILayout.Space(10);
                GUILayout.BeginHorizontal();
                if (stage == Stage.Launched && Button("Close the game now", Kind.Accent, GUILayout.Height(38))) Application.Quit();
                if (stage == Stage.Failed && Button("Try again", Kind.Accent, GUILayout.Height(38))) Begin();
                if (stage != Stage.Downloading && Button("Later", Kind.Normal, GUILayout.Height(38), GUILayout.Width(110)))
                {
                    hiddenThisSession = true;
                    if (stage == Stage.Launched) stage = Stage.Idle;
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndArea();
        }
    }
}
