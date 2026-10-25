using System.Collections.Generic;
using System.Linq;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;

namespace Overstretched
{
    /// <summary>
    /// Sounds for The Stretched. Vanilla clips are borrowed at runtime from the game's own enemy prefabs
    /// (nothing is redistributed); knocks and walkie static are synthesized.
    /// </summary>
    public static class StretchedAudio
    {
        public static AudioClip Scream, Breathing, NeckCrack, Growl, Knock, Static;
        public static AudioClip[] MonsterSteps;
        private static bool loaded;

        public static void Load()
        {
            if (loaded) return;
            loaded = true;
            var prefabs = NetworkManager.Singleton != null
                ? NetworkManager.Singleton.NetworkConfig.Prefabs.Prefabs.Select(p => p.Prefab).Where(p => p != null).ToList()
                : new List<GameObject>();
            T Find<T>() where T : Component => prefabs.Select(p => p.GetComponent<T>()).FirstOrDefault(c => c != null);

            var puma = Find<PumaAI>();
            var dog = Find<MouthDogAI>();
            var girl = Find<DressGirlAI>();
            var bracken = Find<FlowermanAI>();
            var dweller = Find<CaveDwellerAI>();
            var butler = Find<ButlerEnemyAI>();
            Scream = puma != null && puma.attackScream != null ? puma.attackScream : dog != null ? dog.screamSFX : null;
            Breathing = girl != null && girl.breathingSFX != null ? girl.breathingSFX : dog != null ? dog.breathingSFX : null;
            NeckCrack = bracken != null ? bracken.crackNeckSFX : null;
            Growl = dweller != null ? dweller.growlSFX : null;
            MonsterSteps = butler != null && butler.footsteps != null && butler.footsteps.Length > 0 ? butler.footsteps : null;
            Knock = SynthKnock();
            Static = SynthStatic();
            Plugin.Log.LogInfo($"The Stretched audio: scream={Scream != null} breathing={Breathing != null} crack={NeckCrack != null} growl={Growl != null} steps={MonsterSteps != null}");
        }

        public static AudioClip PlayerStep()
        {
            var surfaces = StartOfRound.Instance != null ? StartOfRound.Instance.footstepSurfaces : null;
            if (surfaces == null || surfaces.Length == 0 || surfaces[0].clips == null || surfaces[0].clips.Length == 0) return null;
            return surfaces[0].clips[Random.Range(0, surfaces[0].clips.Length)];
        }

        /// <summary>A heavy knuckle rap on a metal door: low thump plus a short bright knock.</summary>
        private static AudioClip SynthKnock()
        {
            const int rate = 44100;
            int n = (int)(rate * 0.32f);
            var data = new float[n];
            var rng = new System.Random(7);
            float lp = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)rate;
                float thump = Mathf.Sin(2f * Mathf.PI * (95f - 40f * t) * t) * Mathf.Exp(-t * 26f);
                float ring = Mathf.Sin(2f * Mathf.PI * 410f * t) * Mathf.Exp(-t * 38f) * 0.35f;
                float noise = (float)(rng.NextDouble() * 2 - 1);
                lp += (noise - lp) * 0.25f;
                float click = lp * Mathf.Exp(-t * 90f) * 0.8f;
                data[i] = Mathf.Clamp((thump + ring + click) * 0.9f, -1f, 1f);
            }
            var clip = AudioClip.Create("StretchedKnock", n, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        /// <summary>Loopable walkie-talkie static with crackle bursts.</summary>
        private static AudioClip SynthStatic()
        {
            const int rate = 22050;
            int n = rate * 3;
            var data = new float[n];
            var rng = new System.Random(3);
            float lp = 0f, crackle = 0f;
            for (int i = 0; i < n; i++)
            {
                float noise = (float)(rng.NextDouble() * 2 - 1);
                lp += (noise - lp) * 0.45f;
                if (rng.NextDouble() < 0.0006) crackle = 1f;
                crackle *= 0.995f;
                float swell = 0.55f + 0.45f * Mathf.Sin(i / (float)rate * 2.3f);
                data[i] = Mathf.Clamp(lp * 0.32f * swell + noise * crackle * 0.6f, -1f, 1f);
            }
            var clip = AudioClip.Create("StretchedStatic", n, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }

    /// <summary>
    /// Records what each other player says over voice chat (on every client, from the audio it already
    /// plays), so The Stretched can replay a friend's real words from the dark.
    /// </summary>
    public static class VoiceThief
    {
        private static readonly Dictionary<int, VoiceRecorder> recorders = new Dictionary<int, VoiceRecorder>();
        private static float nextScan;

        public static void Tick()
        {
            if (Time.time < nextScan || StartOfRound.Instance == null || GameNetworkManager.Instance == null) return;
            nextScan = Time.time + 2f;
            var local = GameNetworkManager.Instance.localPlayerController;
            var players = StartOfRound.Instance.allPlayerScripts;
            for (int i = 0; i < players.Length; i++)
            {
                var p = players[i];
                if (p == null || p == local || p.currentVoiceChatAudioSource == null) continue;
                var go = p.currentVoiceChatAudioSource.gameObject;
                if (recorders.TryGetValue(i, out var rec) && rec != null && rec.gameObject == go) continue;
                rec = go.GetComponent<VoiceRecorder>() ?? go.AddComponent<VoiceRecorder>();
                recorders[i] = rec;
            }
        }

        public static bool HasVoice(int playerIndex) =>
            recorders.TryGetValue(playerIndex, out var r) && r != null && r.Count > 0;

        public static AudioClip TakeClip(int playerIndex)
        {
            if (!recorders.TryGetValue(playerIndex, out var rec) || rec == null) return null;
            var samples = rec.TakeRandom(out int rate);
            if (samples == null) return null;
            var clip = AudioClip.Create("StolenVoice", samples.Length, 1, rate, false);
            clip.SetData(samples, 0);
            return clip;
        }
    }

    public class VoiceRecorder : MonoBehaviour
    {
        private readonly object gate = new object();
        private readonly List<float> current = new List<float>(48000 * 5);
        private readonly List<float[]> utterances = new List<float[]>();
        private int sampleRate, silentFrames;
        private const float Threshold = 0.01f;

        public int Count { get { lock (gate) return utterances.Count; } }

        private void Awake() => sampleRate = AudioSettings.outputSampleRate;

        // Runs on the audio thread after Dissonance has written this player's voice into the buffer.
        private void OnAudioFilterRead(float[] data, int channels)
        {
            int frames = data.Length / channels;
            float sum = 0f;
            for (int f = 0; f < frames; f++)
            {
                float s = data[f * channels];
                sum += s * s;
            }
            bool loud = Mathf.Sqrt(sum / Mathf.Max(1, frames)) > Threshold;
            lock (gate)
            {
                if (loud || current.Count > 0)
                    for (int f = 0; f < frames; f++) current.Add(data[f * channels]);
                silentFrames = loud ? 0 : silentFrames + frames;
                bool ended = current.Count > 0 && silentFrames > sampleRate * 0.4f;
                if (ended || current.Count > sampleRate * 5)
                {
                    if (current.Count > sampleRate * 0.6f)
                    {
                        utterances.Add(current.ToArray());
                        if (utterances.Count > 8) utterances.RemoveAt(0);
                    }
                    current.Clear();
                }
            }
        }

        public float[] TakeRandom(out int rate)
        {
            rate = sampleRate;
            lock (gate)
            {
                if (utterances.Count == 0) return null;
                // Favour the most recent things they said.
                int i = Mathf.Clamp(utterances.Count - 1 - Random.Range(0, 3), 0, utterances.Count - 1);
                return utterances[i];
            }
        }
    }
}
