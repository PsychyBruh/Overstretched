using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Overstretched
{
    /// <summary>One baked animation clip (from Stretched.lpa, exported by tools/stretched2/10_export_anims.py).</summary>
    public sealed class StretchedClip
    {
        public string Name;
        public int FrameCount;
        public float Fps;
        public bool Loop;
        /// <summary>Authored on the floor but meant to play upside down on the ceiling.</summary>
        public bool Ceiling;
        public Vector3[] Root;          // root bone local position per frame
        public Quaternion[] Rotations;  // [frame * boneCount + bone] local rotations
        public float Duration => (FrameCount - 1) / Fps;

        public void Sample(float time, int boneCount, Quaternion[] rotations, out Vector3 root)
        {
            float f = Mathf.Clamp(time * Fps, 0f, FrameCount - 1);
            int i0 = Mathf.Min((int)f, FrameCount - 1);
            int i1 = Mathf.Min(i0 + 1, FrameCount - 1);
            float a = f - i0;
            root = Vector3.Lerp(Root[i0], Root[i1], a);
            int o0 = i0 * boneCount, o1 = i1 * boneCount;
            for (int b = 0; b < boneCount; b++)
                rotations[b] = Quaternion.Slerp(Rotations[o0 + b], Rotations[o1 + b], a);
        }
    }

    public static class StretchedAnimations
    {
        private static Dictionary<string, StretchedClip> clips;
        public static IReadOnlyDictionary<string, StretchedClip> Clips => clips;

        public static StretchedClip Get(string name) =>
            clips != null && clips.TryGetValue(name, out var clip) ? clip : null;

        public static void Load()
        {
            if (clips != null) return;
            StretchedModel.LoadMesh();
            var bones = StretchedModel.Bones;
            using (var stream = typeof(StretchedAnimations).Assembly.GetManifestResourceStream("Overstretched.Assets.Stretched.Stretched.lpa"))
            {
                if (stream == null) throw new FileNotFoundException("Embedded Stretched.lpa missing");
                var reader = new BinaryReader(stream, Encoding.UTF8);
                if (new string(reader.ReadChars(4)) != "LPA1") throw new InvalidDataException("Stretched.lpa: bad magic");
                reader.ReadInt32(); // version

                int boneCount = reader.ReadInt32();
                if (boneCount != bones.Count)
                    throw new InvalidDataException($"Stretched.lpa has {boneCount} bones, model has {bones.Count}");
                for (int b = 0; b < boneCount; b++)
                {
                    string name = ReadString(reader);
                    if (name != bones[b].Name)
                        throw new InvalidDataException($"Bone {b} is '{name}' in the clips but '{bones[b].Name}' in the model");
                }

                int clipCount = reader.ReadInt32();
                clips = new Dictionary<string, StretchedClip>(clipCount);
                for (int c = 0; c < clipCount; c++)
                {
                    var clip = new StretchedClip { Name = ReadString(reader), FrameCount = reader.ReadInt32(), Fps = reader.ReadSingle() };
                    int flags = reader.ReadInt32();
                    clip.Loop = (flags & 1) != 0;
                    clip.Ceiling = (flags & 2) != 0;
                    clip.Root = new Vector3[clip.FrameCount];
                    clip.Rotations = new Quaternion[clip.FrameCount * boneCount];
                    for (int f = 0; f < clip.FrameCount; f++)
                    {
                        clip.Root[f] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                        for (int b = 0; b < boneCount; b++)
                        {
                            var q = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                            // Keep each bone on one hemisphere so interpolation never takes the long way round.
                            if (f > 0 && Quaternion.Dot(clip.Rotations[(f - 1) * boneCount + b], q) < 0f)
                                q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
                            clip.Rotations[f * boneCount + b] = q;
                        }
                    }
                    clips[clip.Name] = clip;
                }
            }
            Plugin.Log.LogInfo($"The Stretched animations loaded: {clips.Count} clips");
        }

        private static string ReadString(BinaryReader reader) =>
            Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32()));
    }

    /// <summary>
    /// Plays baked clips on one Stretched instance: crossfades, looping, and one-shots that hand back
    /// to <see cref="ReturnClip"/> when they finish.
    /// </summary>
    public class StretchedAnimator : MonoBehaviour
    {
        public Transform[] Bones;
        public float Speed = 1f;
        /// <summary>Clip to fade into when a one-shot ends; null holds the last frame.</summary>
        public string ReturnClip = "idle";

        public StretchedClip Current { get; private set; }
        public float Time { get; private set; }
        public bool Finished { get; private set; }
        /// <summary>Raised after the pose is written each frame, for procedural layers (head look, jaw, twitches).</summary>
        public event Action PostApply;

        private int rootIndex;
        private Quaternion[] sample, from;
        private Vector3 fromRoot;
        private float fade, fadeDuration;

        public void Init(Transform[] bones)
        {
            Bones = bones;
            sample = new Quaternion[bones.Length];
            from = new Quaternion[bones.Length];
            var data = StretchedModel.Bones;
            for (int i = 0; i < data.Count; i++)
                if (data[i].Parent < 0) rootIndex = i;
        }

        public bool Play(string clipName, float crossfade = 0.15f, float speed = 1f)
        {
            var clip = StretchedAnimations.Get(clipName);
            if (clip == null) return false;
            for (int b = 0; b < Bones.Length; b++) from[b] = Bones[b].localRotation;
            fromRoot = Bones[rootIndex].localPosition;
            Current = clip;
            Speed = speed;
            Time = 0f;
            Finished = false;
            fade = 0f;
            fadeDuration = Mathf.Max(0f, crossfade);
            return true;
        }

        /// <summary>Jump straight to one frame and hold it (used for the flicker snap poses).</summary>
        public bool Snap(string clipName, int frame)
        {
            if (!Play(clipName, 0f, 0f)) return false;
            Time = Mathf.Clamp(frame, 0, Current.FrameCount - 1) / Current.Fps;
            return true;
        }

        private void LateUpdate()
        {
            if (Current == null || Bones == null) return;
            float dt = UnityEngine.Time.deltaTime;
            Time += dt * Speed;
            if (Current.Loop)
            {
                Time = Mathf.Repeat(Time, Current.Duration);
            }
            else if (Time >= Current.Duration && !Finished)
            {
                Time = Current.Duration;
                Finished = true;
                if (ReturnClip != null && ReturnClip != Current.Name && Play(ReturnClip, 0.3f))
                    return;
            }

            Current.Sample(Time, Bones.Length, sample, out var root);
            if (fade < fadeDuration)
            {
                fade += dt;
                float w = Mathf.Clamp01(fade / fadeDuration);
                for (int b = 0; b < Bones.Length; b++) sample[b] = Quaternion.Slerp(from[b], sample[b], w);
                root = Vector3.Lerp(fromRoot, root, w);
            }
            for (int b = 0; b < Bones.Length; b++) Bones[b].localRotation = sample[b];
            Bones[rootIndex].localPosition = root;
            PostApply?.Invoke();
        }
    }
}
