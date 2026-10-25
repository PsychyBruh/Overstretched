using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using Msg = Overstretched.StretchedNet.Msg;

namespace Overstretched
{
    /// <summary>
    /// The Stretched. The host runs the brain (states below); every client runs the visuals and the
    /// effects that only make sense for its own player (silence, staring, flashlight, footsteps, breath).
    /// </summary>
    public class StretchedAI : EnemyAI
    {
        public enum State { Roam, Stalk, Hunt, Retreat, Kill, Ceiling, Siege }
        public enum Visual : byte { Floor, Ceiling, Phantom }

        // ------------------------------------------------------------------ shared, synced by messages
        public int MarkedIndex = -1;
        public int Escapes;
        public Visual Mode = Visual.Floor;

        public State CurrentState => (State)currentBehaviourStateIndex;
        public PlayerControllerB Target => target;

        // ------------------------------------------------------------------ visuals (all clients)
        private const float ClipCeiling = 3.4f;  // ceiling height the climb/drop clips were authored for
        private GameObject model;
        private StretchedAnimator anim;
        private Transform head, jaw, eyeL, eyeR, handR;
        private Vector3 headFace, eyeFaceL, eyeFaceR;
        private Quaternion jawOpen;
        private SkinnedMeshRenderer body;
        private Material suitMaterial;
        private float clipLockUntil;
        private bool clipHold;
        private int lastStateSeen = -1;
        private float clipSetTime = -10f;
        private Vector3 lastPosition;
        private float speedEstimate, ceilingHeight = 8f, ceilingTimer, visualCeiling, dropOffset;
        private float twitchAngle, twitchVelocity, nextTwitch;
        private AudioClip voiceClip;
        private float[] voiceEnvelope;
        private float voiceStartTime;

        // ------------------------------------------------------------------ local effects (all clients)
        private float stareTime, stareCooldown, litTime, litCooldown, touchCooldown, noiseCooldown, fearTimer;
        private float footstepTimer, breathForcedUntil, staticUntil, fade;
        private bool phantomHidden;
        private AudioSource breath2D, static2D;

        // ------------------------------------------------------------------ host brain
        private PlayerControllerB target;
        private float stateTimer, flickerTimer, knockCooldown, voiceCooldown, ceilingCooldown = 20f;
        private float stareAnswerAt, retreatImmuneUntil, lastSeenTarget, holdUntil, ceilingWatched;
        private bool powerCutUsed, siegeDone, doorwayMode, doorwayWatched;
        private float doorwayLastSeen, doorwayTimer;
        private readonly NavMeshPath scratchPath = new NavMeshPath();
        private static readonly List<string> loggedErrors = new List<string>();

        private float Mult => (1f + 0.12f * Mathf.Min(Escapes, 5)) * Settings.StretchedSpeed;
        private float Aggro => Mathf.Max(0.25f, Settings.StretchedAggression);
        private static PlayerControllerB[] Players => StartOfRound.Instance.allPlayerScripts;
        private static PlayerControllerB LocalPlayer => GameNetworkManager.Instance != null ? GameNetworkManager.Instance.localPlayerController : null;
        private static int IndexOf(PlayerControllerB p) => p == null ? -1 : Array.IndexOf(Players, p);
        private static PlayerControllerB PlayerAt(int i) => i >= 0 && i < Players.Length ? Players[i] : null;

        // ================================================================== lifecycle

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            StretchedNet.EnsureRegistered();
        }

        public override void Start()
        {
            try { BuildModel(); }
            catch (Exception e) { Plugin.Log.LogError($"The Stretched model failed to build: {e}"); }
            base.Start();
            StretchedNet.EnsureRegistered();
            StretchedAudio.Load();
            lastPosition = transform.position;
            if (IsServer)
            {
                RoundManager.Instance.onPowerSwitch.AddListener(OnPowerSwitch);
                StartCoroutine(HostSpawn());
            }
        }

        public override void OnDestroy()
        {
            if (RoundManager.Instance != null) RoundManager.Instance.onPowerSwitch.RemoveListener(OnPowerSwitch);
            if (Players != null)
                foreach (var p in Players)
                    if (p != null && p.redirectToEnemy == this) p.redirectToEnemy = null;
            base.OnDestroy();
        }

        private void BuildModel()
        {
            model = StretchedModel.Instantiate(transform.position, transform.rotation);
            model.transform.SetParent(transform, true);
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;
            anim = model.GetComponent<StretchedAnimator>();
            body = model.GetComponentInChildren<SkinnedMeshRenderer>();
            var mats = body.materials; // per-instance copies, so suit swaps don't leak to other instances
            suitMaterial = mats.FirstOrDefault(m => m.name.Contains("Suit"));

            var data = StretchedModel.Bones;
            Transform Bone(string n) => anim.Bones[data.Select(b => b.Name).ToList().IndexOf(n)];
            head = Bone("head");
            jaw = Bone("jaw");
            eyeL = Bone("eye.L");
            eyeR = Bone("eye.R");
            handR = Bone("hand.R");
            // Face directions in each bone's local frame, from the bind pose (model forward = +Z).
            Vector3 FaceAxis(string n) =>
                Quaternion.Inverse(data[data.Select(b => b.Name).ToList().IndexOf(n)].Rest.rotation) * Vector3.forward;
            headFace = FaceAxis("head");
            eyeFaceL = FaceAxis("eye.L");
            eyeFaceR = FaceAxis("eye.R");
            var scream = StretchedAnimations.Get("scream");
            int jawIndex = data.Select(b => b.Name).ToList().IndexOf("jaw");
            jawOpen = scream != null ? scream.Rotations[30 * data.Count + jawIndex] : jaw.localRotation;
            anim.PostApply += ProceduralLayer;
            anim.ReturnClip = null; // locomotion picks the next clip
            nextTwitch = Time.time + UnityEngine.Random.Range(2f, 5f);
        }

        private IEnumerator HostSpawn()
        {
            yield return null;
            PlayClip("vent_emerge", 0f, 2.1f);
            yield return new WaitForSeconds(0.5f);
            ChooseMarked();
            SwitchToBehaviourState((int)State.Roam);
        }

        // ================================================================== per frame (all clients)

        public override void Update()
        {
            base.Update();
            if (model == null || StartOfRound.Instance == null) return;
            float dt = Time.deltaTime;
            Tick(ref stareCooldown, dt); Tick(ref litCooldown, dt); Tick(ref touchCooldown, dt); Tick(ref noiseCooldown, dt);

            if (currentBehaviourStateIndex != lastStateSeen)
            {
                // A new state never inherits a frozen pose (flicker snap, doorway stare) from the last one.
                lastStateSeen = currentBehaviourStateIndex;
                // State RPCs and clip messages can arrive in either order: keep a hold that was just set.
                if (CurrentState != State.Kill && Time.time - clipSetTime > 0.75f) clipHold = false;
            }

            var pos = transform.position;
            float v = (pos - lastPosition).magnitude / Mathf.Max(dt, 1e-4f);
            if (v < 25f) speedEstimate = Mathf.Lerp(speedEstimate, v, 1f - Mathf.Exp(-8f * dt));
            lastPosition = pos;

            try
            {
                PlaceModel(dt);
                ChooseLocomotion();
                LocalEffects(dt);
                if (IsServer) HostFrame(dt);
            }
            catch (Exception e) { LogOnce("frame", e); }
        }

        private static void Tick(ref float t, float dt) { if (t > 0f) t -= dt; }

        private void PlaceModel(float dt)
        {
            ceilingTimer -= dt;
            if (ceilingTimer <= 0f)
            {
                ceilingTimer = 0.25f;
                ceilingHeight = MeasureCeiling(transform.position);
            }
            if (Mode == Visual.Ceiling)
            {
                visualCeiling = Mathf.MoveTowards(visualCeiling, ceilingHeight, 6f * dt);
                model.transform.position = transform.position + Vector3.up * visualCeiling;
                model.transform.rotation = transform.rotation * Quaternion.AngleAxis(180f, Vector3.forward);
            }
            else
            {
                dropOffset = Mathf.MoveTowards(dropOffset, 0f, 10f * dt);
                model.transform.localPosition = Vector3.up * dropOffset;
                model.transform.localRotation = Quaternion.identity;
            }
        }

        public static float MeasureCeiling(Vector3 floor)
        {
            var mask = StartOfRound.Instance.collidersAndRoomMaskAndDefault;
            return Physics.Raycast(floor + Vector3.up * 0.4f, Vector3.up, out var hit, 12f, mask, QueryTriggerInteraction.Ignore)
                ? hit.distance + 0.4f
                : 12f;
        }

        private void ChooseLocomotion()
        {
            if (anim == null || clipHold || Time.time < clipLockUntil) return;
            bool moving = speedEstimate > 0.35f;
            string want;
            float natural;
            if (Mode == Visual.Ceiling) { want = moving ? "ceiling_crawl" : "ceiling_hang"; natural = 2f; }
            else if (CurrentState == State.Retreat && moving) { want = "retreat"; natural = 3f; }
            else if (!moving) { want = "idle"; natural = 0f; }
            else if (speedEstimate > 5f && ceilingHeight > 3.8f) { want = "chase_lope"; natural = 7f; }
            else if (ceilingHeight < 2.9f) { want = "deep_duck_walk"; natural = 1.3f; }
            else if (ceilingHeight < 3.8f) { want = "duck_walk"; natural = 1.7f; }
            else { want = "walk"; natural = 2.2f; }
            float speed = natural > 0f ? Mathf.Clamp(speedEstimate / natural, 0.6f, 1.8f) : 1f;
            if (anim.Current == null || anim.Current.Name != want) anim.Play(want, 0.25f, speed);
            else anim.Speed = speed;
        }

        /// <summary>Head look, eyes rolling under the skin, jaw speaking stolen words, random twitches.</summary>
        private void ProceduralLayer()
        {
            if (head == null) return;
            bool free = !clipHold && Time.time >= clipLockUntil && Mode != Visual.Ceiling && !inSpecialAnimation;
            var lookAt = PlayerAt(MarkedIndex);
            if (lookAt == null || lookAt.isPlayerDead) lookAt = LocalPlayer;
            if (lookAt != null && free)
            {
                var targetPos = lookAt.gameplayCamera.transform.position;
                Turn(head, headFace, targetPos, 65f, 0.75f);
            }
            if (lookAt != null)
            {
                var targetPos = lookAt.gameplayCamera.transform.position;
                Turn(eyeL, eyeFaceL, targetPos, 25f, 1f);
                Turn(eyeR, eyeFaceR, targetPos, 25f, 1f);
            }

            if (Time.time > nextTwitch)
            {
                nextTwitch = Time.time + UnityEngine.Random.Range(1.5f, 6f);
                twitchVelocity += UnityEngine.Random.Range(-1f, 1f) * 900f;
            }
            twitchVelocity += (-twitchAngle * 400f - twitchVelocity * 22f) * Time.deltaTime;
            twitchAngle += twitchVelocity * Time.deltaTime;
            head.rotation = Quaternion.AngleAxis(twitchAngle, head.rotation * headFace) * head.rotation;

            if (voiceClip != null && creatureVoice.isPlaying && voiceEnvelope != null)
            {
                int i = Mathf.Clamp((int)((Time.time - voiceStartTime) * 20f), 0, voiceEnvelope.Length - 1);
                jaw.localRotation = Quaternion.Slerp(jaw.localRotation, jawOpen, Mathf.Clamp01(voiceEnvelope[i] * 6f) * 0.7f);
            }
        }

        private static void Turn(Transform bone, Vector3 localFace, Vector3 worldTarget, float maxAngle, float weight)
        {
            var face = bone.rotation * localFace;
            var want = (worldTarget - bone.position).normalized;
            var delta = Quaternion.FromToRotation(face, want);
            delta.ToAngleAxis(out float angle, out var axis);
            if (angle > 180f) angle -= 360f;
            angle = Mathf.Clamp(angle, -maxAngle, maxAngle) * weight;
            if (!float.IsNaN(axis.x)) bone.rotation = Quaternion.AngleAxis(angle, axis) * bone.rotation;
        }

        // ================================================================== local player effects

        private void LocalEffects(float dt)
        {
            var me = LocalPlayer;
            bool inKill = CurrentState == State.Kill;
            if (me == null || me.isPlayerDead || !me.isPlayerControlled || me.isInsideFactory == isOutside)
            {
                FadeLocalAudio(dt, false, false);
                if (phantomHidden) { body.enabled = true; phantomHidden = false; }
                return;
            }
            float d = Vector3.Distance(me.transform.position, transform.position);
            bool marked = IndexOf(me) == MarkedIndex;
            var s = CurrentState;
            bool menacing = s == State.Stalk || s == State.Hunt || s == State.Ceiling || s == State.Siege;
            bool iAmVictim = inKill && me.inAnimationWithEnemy == this;

            // Silence: the world goes quiet around it (the victim keeps hearing their friends, but can't reach them).
            float radius = Settings.StretchedSilenceRadius * (1f + 0.15f * Mathf.Min(Escapes, 5));
            if (d < radius && !iAmVictim)
            {
                float t = 1f - d / radius;
                SoundManager.Instance.SetDiageticMasterVolume(Mathf.Lerp(-4f, -42f, t * t));
            }

            // Fear: heartbeat + screen distortion ride the game's own fear level.
            fearTimer -= dt;
            if (menacing && d < 24f && fearTimer <= 0f)
            {
                fearTimer = 0.5f;
                me.JumpToFearLevel(Mathf.Lerp(0.25f, 1f, 1f - d / 24f) * (marked ? 1f : 0.7f));
            }

            // Staring at it.
            var cam = me.gameplayCamera.transform;
            var chest = VisualChest();
            bool visible = body.enabled && IsInView(cam, chest, 28f, 20f);
            if (visible && menacing)
            {
                stareTime += dt;
                me.insanityLevel = Mathf.Min(me.maxInsanityLevel, me.insanityLevel + dt * 4f);
            }
            else stareTime = Mathf.Max(0f, stareTime - dt * 2f);
            if (stareTime > 3.2f && stareCooldown <= 0f && (s == State.Stalk || s == State.Roam))
            {
                stareCooldown = 12f;
                stareTime = 0f;
                int idx = IndexOf(me);
                StretchedNet.ToHost(this, Msg.Stare, (ref Unity.Netcode.FastBufferWriter w) => w.WriteValueSafe(idx));
            }

            // Flashlight in its face.
            var light = ActiveLight(me);
            bool lit = light != null && IsInView(light.transform, HeadPosition(), 14f, 16f);
            litTime = lit ? litTime + dt : 0f;
            if (litTime > 0.4f && litCooldown <= 0f && menacing)
            {
                litCooldown = 1f;
                int idx = IndexOf(me);
                StretchedNet.ToHost(this, Msg.Lit, (ref Unity.Netcode.FastBufferWriter w) => w.WriteValueSafe(idx));
            }

            // Your flashlight lies: up close it only exists inside a beam.
            if (Mode == Visual.Phantom)
            {
                bool show = d > 12f || (light != null && IsInView(light.transform, chest, 14f, 22f));
                body.enabled = show;
                phantomHidden = !show;
            }
            else if (phantomHidden) { body.enabled = true; phantomHidden = false; }

            // Wrong footsteps: steps right behind the marked player that stop when they stop.
            bool walking = me.thisController.velocity.magnitude > 1.2f;
            if (marked && s == State.Stalk && d < 25f && walking)
            {
                footstepTimer -= dt;
                if (footstepTimer <= 0f)
                {
                    footstepTimer = me.isSprinting ? 0.32f : 0.5f;
                    var clip = StretchedAudio.PlayerStep();
                    if (clip != null) AudioSource.PlayClipAtPoint(clip, me.transform.position - me.transform.forward * 2.4f, 0.85f);
                }
            }
            else footstepTimer = 0.25f;

            bool breathe = (marked && s == State.Stalk && d < 9f) || Time.time < breathForcedUntil;
            FadeLocalAudio(dt, breathe, Time.time < staticUntil && HoldingActiveWalkie(me));
        }

        private void FadeLocalAudio(float dt, bool breathe, bool staticOn)
        {
            if (breath2D == null && StretchedAudio.Breathing != null) breath2D = Make2D("Breath", StretchedAudio.Breathing);
            if (static2D == null && StretchedAudio.Static != null) static2D = Make2D("Static", StretchedAudio.Static);
            Fade(breath2D, breathe ? 0.9f : 0f, dt);
            Fade(static2D, staticOn ? 0.5f : 0f, dt);
        }

        private AudioSource Make2D(string name, AudioClip clip)
        {
            var go = new GameObject("Stretched" + name);
            go.transform.SetParent(transform, false);
            var a = go.AddComponent<AudioSource>();
            a.clip = clip;
            a.loop = true;
            a.spatialBlend = 0f;
            a.volume = 0f;
            a.playOnAwake = false;
            return a;
        }

        private static void Fade(AudioSource a, float target, float dt)
        {
            if (a == null) return;
            a.volume = Mathf.MoveTowards(a.volume, target, dt * 1.5f);
            if (a.volume > 0.001f && !a.isPlaying) a.Play();
            else if (a.volume <= 0.001f && a.isPlaying) a.Stop();
        }

        private static Light ActiveLight(PlayerControllerB p)
        {
            if (p.currentlyHeldObjectServer is FlashlightItem f && f.isBeingUsed && f.flashlightBulb != null && f.flashlightBulb.enabled)
                return f.flashlightBulb;
            if (p.helmetLight != null && p.helmetLight.enabled) return p.helmetLight;
            return null;
        }

        private static bool HoldingActiveWalkie(PlayerControllerB p) =>
            p.currentlyHeldObjectServer is WalkieTalkie w && w.isBeingUsed;

        private static bool IsInView(Transform viewer, Vector3 point, float range, float halfAngle)
        {
            var dir = point - viewer.position;
            if (dir.magnitude > range || Vector3.Angle(viewer.forward, dir) > halfAngle) return false;
            return !Physics.Linecast(viewer.position, point, StartOfRound.Instance.collidersAndRoomMask, QueryTriggerInteraction.Ignore);
        }

        private Vector3 VisualChest() => Mode == Visual.Ceiling && head != null ? head.position : transform.position + Vector3.up * 2.3f;
        private Vector3 HeadPosition() => head != null ? head.position : transform.position + Vector3.up * 3.2f;

        public override Transform GetRadarHeadTransform() => head != null ? head : transform;

        // Fade to black for the player being taken.
        private void OnGUI()
        {
            if (fade <= 0.001f) return;
            var c = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, fade);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = c;
        }

        // ================================================================== vanilla hooks

        public override void OnCollideWithPlayer(Collider other)
        {
            base.OnCollideWithPlayer(other);
            var p = MeetsStandardPlayerCollisionConditions(other, inSpecialAnimation);
            if (p == null || touchCooldown > 0f || Features.IsPet(this)) return;
            touchCooldown = 0.5f;
            int idx = IndexOf(p);
            StretchedNet.ToHost(this, Msg.Touch, (ref Unity.Netcode.FastBufferWriter w) => w.WriteValueSafe(idx));
        }

        public override void DetectNoise(Vector3 noisePosition, float noiseLoudness, int timesPlayedInOneSpot = 0, int noiseID = 0)
        {
            base.DetectNoise(noisePosition, noiseLoudness, timesPlayedInOneSpot, noiseID);
            if (IsServer) { HostNoise(noisePosition, noiseLoudness); return; }
            if (noiseCooldown > 0f) return;
            noiseCooldown = 0.25f;
            StretchedNet.ToHost(this, Msg.Noise, (ref Unity.Netcode.FastBufferWriter w) =>
            {
                w.WriteValueSafe(noisePosition);
                w.WriteValueSafe(noiseLoudness);
            });
        }

        public override void SetEnemyStunned(bool setToStunned, float setToStunTime = 1f, PlayerControllerB setStunnedByPlayer = null)
        {
            base.SetEnemyStunned(setToStunned, setToStunTime, setStunnedByPlayer);
            if (!setToStunned || CurrentState == State.Kill) return;
            if (Mode == Visual.Ceiling) { dropOffset = visualCeiling - ClipCeiling; Mode = Visual.Floor; }
            LocalClip("stunned", 0.1f, 2f, -1, 1f);
            if (IsServer)
            {
                holdUntil = Time.time + 2f;
                if (Mode != Visual.Floor) SetMode(Visual.Floor);
                var by = setStunnedByPlayer ?? target;
                if (by != null) StartCoroutine(After(2.2f, () => BeginHunt(by, scream: true)));
            }
        }

        public override void HitEnemy(int force = 1, PlayerControllerB playerWhoHit = null, bool playHitSFX = false, int hitID = -1)
        {
            base.HitEnemy(force, playerWhoHit, playHitSFX, hitID);
            if (CurrentState == State.Kill) return;
            LocalClip("hit_flinch", 0.05f, 0.5f, -1, 1f);
            if (IsServer && playerWhoHit != null && CurrentState != State.Retreat) BeginHunt(playerWhoHit, scream: true);
        }

        // ================================================================== messages

        public void Receive(ulong sender, Msg type, FastBufferReader r)
        {
            try
            {
                switch (type)
                {
                    case Msg.Flicker:
                        r.ReadValueSafe(out Vector3 fpos); r.ReadValueSafe(out float frad);
                        FlickerLightsNear(fpos, frad);
                        break;
                    case Msg.Mark:
                        r.ReadValueSafe(out int marked); r.ReadValueSafe(out int escapes);
                        MarkedIndex = marked; Escapes = escapes;
                        break;
                    case Msg.Mode:
                        r.ReadValueSafe(out byte mode);
                        ApplyMode((Visual)mode);
                        break;
                    case Msg.Clip:
                        r.ReadValueSafe(out string clip); r.ReadValueSafe(out float fadeIn); r.ReadValueSafe(out float lockFor);
                        r.ReadValueSafe(out int snap); r.ReadValueSafe(out float speed);
                        LocalClip(clip, fadeIn, lockFor, snap, speed);
                        break;
                    case Msg.Knock:
                        r.ReadValueSafe(out Vector3 kpos); r.ReadValueSafe(out int count);
                        StartCoroutine(Knocks(kpos, count));
                        break;
                    case Msg.Voice:
                        r.ReadValueSafe(out int source);
                        PlayStolenVoice(source);
                        break;
                    case Msg.Kill:
                        r.ReadValueSafe(out int victim);
                        StartCoroutine(KillSequence(victim));
                        break;
                    case Msg.Breath:
                        r.ReadValueSafe(out int breathFor);
                        if (breathFor == IndexOf(LocalPlayer)) breathForcedUntil = Time.time + 3.5f;
                        break;
                    case Msg.Suit:
                        r.ReadValueSafe(out int suitOf);
                        WearSuitOf(suitOf);
                        break;
                    case Msg.Teleport:
                        r.ReadValueSafe(out Vector3 tpos); r.ReadValueSafe(out float yaw); r.ReadValueSafe(out bool outside);
                        if (outside != isOutside) SetEnemyOutside(outside);
                        if (!IsServer)
                        {
                            transform.position = tpos;
                            serverPosition = tpos;
                            transform.rotation = Quaternion.Euler(0f, yaw, 0f);
                        }
                        lastPosition = tpos;
                        break;
                    case Msg.Stare:
                        r.ReadValueSafe(out int starer);
                        if (IsServer) HostStare(PlayerAt(starer));
                        break;
                    case Msg.Lit:
                        r.ReadValueSafe(out int litBy);
                        if (IsServer) HostLit(PlayerAt(litBy));
                        break;
                    case Msg.Touch:
                        r.ReadValueSafe(out int toucher);
                        if (IsServer) HostTouch(PlayerAt(toucher));
                        break;
                    case Msg.Noise:
                        r.ReadValueSafe(out Vector3 npos); r.ReadValueSafe(out float loud);
                        if (IsServer) HostNoise(npos, loud);
                        break;
                }
            }
            catch (Exception e) { LogOnce("msg " + type, e); }
        }

        private void LocalClip(string clip, float fadeIn, float lockFor, int snap, float speed)
        {
            if (anim == null) return;
            if (snap >= 0) anim.Snap(clip, snap);
            else anim.Play(clip, fadeIn, speed);
            clipHold = lockFor < 0f;
            clipLockUntil = Time.time + Mathf.Max(0f, lockFor);
            clipSetTime = Time.time;
            switch (clip) // sounds that belong to a clip play wherever the clip plays
            {
                case "scream": OneShot(StretchedAudio.Scream, 1f); break;
                case "flashlight_recoil": OneShot(StretchedAudio.Growl, 0.8f); break;
                case "vent_emerge": OneShot(StretchedAudio.Growl, 0.6f); break;
            }
        }

        private void OneShot(AudioClip clip, float volume)
        {
            if (clip == null || creatureVoice == null) return;
            creatureVoice.PlayOneShot(clip, volume);
            WalkieTalkie.TransmitOneShotAudio(creatureVoice, clip, volume);
        }

        private void ApplyMode(Visual mode)
        {
            if (Mode == Visual.Ceiling && mode != Visual.Ceiling) dropOffset = visualCeiling - ClipCeiling;
            if (Mode != Visual.Ceiling && mode == Visual.Ceiling) visualCeiling = ClipCeiling;
            Mode = mode;
        }

        private static void FlickerLightsNear(Vector3 pos, float radius)
        {
            var lights = RoundManager.Instance.allPoweredLightsAnimators;
            if (lights == null) return;
            foreach (var a in lights)
                if (a != null && (a.transform.position - pos).sqrMagnitude < radius * radius)
                    a.SetTrigger("Flicker");
        }

        private IEnumerator Knocks(Vector3 pos, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (StretchedAudio.Knock != null) AudioSource.PlayClipAtPoint(StretchedAudio.Knock, pos, 1f);
                yield return new WaitForSeconds(0.55f);
            }
        }

        private void PlayStolenVoice(int source)
        {
            var clip = VoiceThief.TakeClip(source);
            if (clip == null || creatureVoice == null) return;
            var samples = new float[clip.samples];
            clip.GetData(samples, 0);
            int window = Mathf.Max(1, clip.frequency / 20);
            voiceEnvelope = new float[samples.Length / window + 1];
            for (int i = 0; i < samples.Length; i++)
                voiceEnvelope[i / window] = Mathf.Max(voiceEnvelope[i / window], Mathf.Abs(samples[i]));
            voiceClip = clip;
            voiceStartTime = Time.time;
            creatureVoice.PlayOneShot(clip, 1.2f);
        }

        private void WearSuitOf(int playerIndex)
        {
            var p = PlayerAt(playerIndex);
            if (p == null || suitMaterial == null || p.thisPlayerModel == null) return;
            var tex = p.thisPlayerModel.sharedMaterial != null ? p.thisPlayerModel.sharedMaterial.mainTexture : null;
            if (tex != null) suitMaterial.SetTexture("_BaseColorMap", tex);
        }

        // ================================================================== the kill (all clients)

        private IEnumerator KillSequence(int victimIndex)
        {
            var victim = PlayerAt(victimIndex);
            if (victim == null) yield break;
            bool local = victim == LocalPlayer;
            inSpecialAnimation = true;
            if (Mode == Visual.Ceiling) ApplyMode(Visual.Floor);
            if (local)
            {
                victim.inAnimationWithEnemy = this;
                victim.inSpecialInteractAnimation = true;
                victim.disableMoveInput = true;
                victim.thisController.enabled = false;
            }
            OneShot(StretchedAudio.Scream, 0.7f);
            LocalClip("lunge_grab", 0.06f, -1f, -1, 1f);
            float t0 = Time.time;
            while (Time.time - t0 < 3.6f)
            {
                float t = Time.time - t0;
                if (t > 0.6f && anim.Current != null && anim.Current.Name == "lunge_grab") LocalClip("lift", 0.12f, -1f, -1, 1f);
                if (t > 1.95f && anim.Current != null && anim.Current.Name == "lift") LocalClip("retract", 0.12f, -1f, -1, 1f);
                if (local && !victim.isPlayerDead)
                {
                    // Dangle from its hand, rising into the dark; the screen fades while friends are still audible.
                    var held = handR.position - Vector3.up * 1.6f;
                    victim.transform.position = Vector3.Lerp(victim.transform.position, held, 10f * Time.deltaTime);
                    fade = Mathf.Clamp01((t - 1.2f) / 2.2f);
                }
                yield return null;
            }
            if (local && !victim.isPlayerDead)
            {
                if (StretchedAudio.NeckCrack != null) creatureSFX.PlayOneShot(StretchedAudio.NeckCrack);
                victim.disableMoveInput = false;
                victim.inSpecialInteractAnimation = false;
                victim.inAnimationWithEnemy = null;
                victim.KillPlayer(Vector3.zero, spawnBody: false, CauseOfDeath.Strangulation);
                if (!victim.isPlayerDead) victim.thisController.enabled = true; // survived (god mode): give control back
            }
            fade = 0f;
            if (victim.isPlayerDead) victim.redirectToEnemy = this; // their name on the ship radar now follows The Stretched
            staticUntil = Time.time + 25f;   // their walkie keeps breathing static
            inSpecialAnimation = false;
            clipHold = false;
            clipLockUntil = 0f;
            if (IsServer) HostAfterKill(victim);
        }

        // ================================================================== host brain

        public override void DoAIInterval()
        {
            base.DoAIInterval();
            if (!IsServer || isEnemyDead || StartOfRound.Instance.allPlayersDead || Settings.IsFrozen(this) || Features.IsPet(this)) return;
            try { Think(AIIntervalTime); }
            catch (Exception e) { LogOnce("think", e); }
        }

        private void HostFrame(float dt)
        {
            Tick(ref knockCooldown, dt); Tick(ref voiceCooldown, dt); Tick(ref ceilingCooldown, dt); Tick(ref flickerTimer, dt);
            stateTimer += dt;
            bool frozen = Settings.IsFrozen(this);
            if (agent != null && agent.enabled && (frozen || Time.time < holdUntil || stunNormalizedTimer > 0f || inSpecialAnimation))
                agent.speed = 0f;
        }

        private void Think(float dt)
        {
            if (!ventAnimationFinished || inSpecialAnimation || stunNormalizedTimer > 0f || Time.time < holdUntil) return;
            ValidateMarked();
            CheckSiege();
            switch (CurrentState)
            {
                case State.Roam: ThinkRoam(); break;
                case State.Stalk: ThinkStalk(dt); break;
                case State.Hunt: ThinkHunt(); break;
                case State.Retreat: ThinkRetreat(); break;
                case State.Ceiling: ThinkCeiling(dt); break;
            }
        }

        private void ThinkRoam()
        {
            agent.speed = 3f * Mult;
            if (Mode != Visual.Floor) SetMode(Visual.Floor);
            if (!currentSearch.inProgress) StartSearch(transform.position);
            var seen = CheckLineOfSightForClosestPlayer(60f, 30, 4);
            if (seen != null) { BeginStalk(seen); return; }
            var marked = PlayerAt(MarkedIndex);
            if (marked != null && Targetable(marked) && stateTimer > 25f / Aggro) BeginStalk(marked); // it always finds them
        }

        // Mid-kill nothing else (late coroutines, noise, lights) may change what it is doing.
        private bool Busy => CurrentState == State.Kill;

        private void BeginStalk(PlayerControllerB p)
        {
            if (Busy) return;
            if (currentSearch.inProgress) StopSearch(currentSearch);
            target = p;
            doorwayMode = false;
            stateTimer = 0f;
            flickerTimer = 1.5f;
            moveTowardsDestination = true;
            SwitchToBehaviourState((int)State.Stalk);
        }

        private void ThinkStalk(float dt)
        {
            if (!Targetable(target))
            {
                target = PlayerAt(MarkedIndex);
                if (!Targetable(target)) { GoRoam(); return; }
            }
            float d = Vector3.Distance(transform.position, target.transform.position);
            int observers = CountObservers(transform.position + Vector3.up * 2.3f, 30f, 40f);
            // Phantom (beam-only) while it creeps close; a doorway stare is meant to be seen.
            SetMode(d < 12f && !doorwayMode ? Visual.Phantom : Visual.Floor);

            if (doorwayMode) { DoorwayStep(d); return; }

            if (observers > 0)
            {
                // Never seen walking: frozen while watched, it closes in during light flickers.
                agent.speed = 0f;
                if (flickerTimer <= 0f)
                {
                    flickerTimer = UnityEngine.Random.Range(1.1f, 2.2f) / (Mult * Aggro);
                    StartCoroutine(FlickerStep());
                }
            }
            else
            {
                agent.speed = 4f * Mult;
                SetDestinationToPosition(target.transform.position - target.transform.forward * 1.8f);
                if (d > 15f && UnityEngine.Random.value < 0.015f * Aggro && TryDoorwayAppear(d)) return;
            }

            if (knockCooldown <= 0f && d < 30f)
            {
                var door = NearestClosedDoor(target.transform.position, 7f);
                if (door != null)
                {
                    knockCooldown = UnityEngine.Random.Range(35f, 60f) / Aggro;
                    var kp = door.transform.position;
                    StretchedNet.ToAll(this, Msg.Knock, (ref FastBufferWriter w) => { w.WriteValueSafe(kp); w.WriteValueSafe(3); });
                }
            }
            if (voiceCooldown <= 0f && d > 9f && d < 32f && !CanPlayerSee(target, HeadPosition()))
            {
                voiceCooldown = UnityEngine.Random.Range(40f, 80f) / Aggro;
                int src = PickVoiceSource();
                if (src >= 0) StretchedNet.ToAll(this, Msg.Voice, (ref FastBufferWriter w) => w.WriteValueSafe(src));
            }
            if (ceilingCooldown <= 0f && observers == 0 && d > 8f && d < 30f)
            {
                float c = MeasureCeiling(transform.position);
                ceilingCooldown = 25f;
                if (c >= 3.3f && c <= 8f && UnityEngine.Random.value < 0.5f * Aggro) { StartCoroutine(ClimbToCeiling()); return; }
            }
            if (d < 3.2f && observers == 0) { BeginHunt(target, scream: false); return; }
            if (stateTimer > UnityEngine.Random.Range(70f, 100f) / Aggro) BeginHunt(target, scream: true);
        }

        private IEnumerator FlickerStep()
        {
            var t = target;
            if (t == null) yield break;
            var chest = transform.position + Vector3.up * 2f;
            StretchedNet.ToAll(this, Msg.Flicker, (ref FastBufferWriter w) => { w.WriteValueSafe(chest); w.WriteValueSafe(16f); });
            yield return new WaitForSeconds(0.12f);
            if (t == null || CurrentState != State.Stalk) yield break;
            if (NavMesh.CalculatePath(transform.position, t.transform.position, agent.areaMask, scratchPath))
            {
                var p = PointAlongPath(scratchPath, UnityEngine.Random.Range(2.5f, 4.5f) * Mult);
                WarpTo(p, t.transform.position);
            }
            PlayClip("flicker_poses", 0f, -1f, UnityEngine.Random.Range(0, 6) * 16 + 8);
        }

        private bool TryDoorwayAppear(float d)
        {
            var spot = FindSpotInView(target, Mathf.Min(d * 0.8f, 25f), 12f, mustBeOutOfFocus: true);
            if (spot == null) return false;
            doorwayMode = true;
            doorwayWatched = false;
            doorwayTimer = 0f;
            WarpTo(spot.Value, target.transform.position);
            agent.speed = 0f;
            PlayClip("doorway_stance", 0.05f, -1f);
            return true;
        }

        private void DoorwayStep(float d)
        {
            agent.speed = 0f;
            doorwayTimer += AIIntervalTime;
            bool watched = CanPlayerSee(target, transform.position + Vector3.up * 2.5f, 25f);
            if (watched) { doorwayWatched = true; doorwayLastSeen = Time.time; }
            if (doorwayWatched && !watched && Time.time - doorwayLastSeen > 1.2f)
            {
                // Looked away: it's gone... and standing in a closer doorway.
                if (d < 9f) { doorwayMode = false; StartCoroutine(AppearBehind(target)); return; }
                var spot = FindSpotInView(target, Mathf.Max(6f, d * 0.6f), 5f, mustBeOutOfFocus: true);
                if (spot != null)
                {
                    WarpTo(spot.Value, target.transform.position);
                    PlayClip("doorway_stance", 0f, -1f);
                    doorwayWatched = false;
                    doorwayTimer = 0f;
                }
            }
            if (doorwayTimer > 15f) { doorwayMode = false; PlayClip("idle", 0.3f, 0f); }
        }

        private void BeginHunt(PlayerControllerB p, bool scream)
        {
            if (p == null || p.isPlayerDead || CurrentState == State.Kill) return;
            if (currentSearch.inProgress) StopSearch(currentSearch);
            target = p;
            doorwayMode = false;
            lastSeenTarget = Time.time;
            stateTimer = 0f;
            moveTowardsDestination = true;
            if (Mode != Visual.Floor) SetMode(Visual.Floor);
            SwitchToBehaviourState((int)State.Hunt);
            if (scream)
            {
                PlayClip("scream", 0.1f, 1.4f);
                holdUntil = Time.time + 1.4f;
                if (!powerCutUsed && UnityEngine.Random.value < 0.35f * Aggro && !isOutside)
                {
                    powerCutUsed = true;
                    RoundManager.Instance.SwitchPower(false); // the breaker becomes your only weapon
                }
            }
        }

        private void ThinkHunt()
        {
            if (!Targetable(target))
            {
                var next = TargetablePlayers().OrderBy(p => Vector3.Distance(p.transform.position, transform.position)).FirstOrDefault();
                if (next == null || Vector3.Distance(next.transform.position, transform.position) > 30f) { GoRoam(); return; }
                target = next;
            }
            agent.speed = 7.2f * Mult;
            if (CanSeeFromEye(target)) lastSeenTarget = Time.time;
            SetDestinationToPosition(target.transform.position);
            float d = Vector3.Distance(transform.position, target.transform.position);
            if (Time.time - lastSeenTarget > 7f && d > 18f)
            {
                // They got away. It remembers, and gets better at this.
                Escapes++;
                BroadcastMark();
                PlayClip("listen", 0.2f, 1.5f);
                BeginStalk(PlayerAt(MarkedIndex) ?? target);
            }
        }

        private void BeginRetreat(Vector3 from)
        {
            if (CurrentState == State.Kill || CurrentState == State.Siege) return;
            retreatImmuneUntil = Time.time + 10f;
            doorwayMode = false;
            if (currentSearch.inProgress) StopSearch(currentSearch);
            if (Mode != Visual.Floor) SetMode(Visual.Floor);
            SwitchToBehaviourState((int)State.Retreat);
            stateTimer = 0f;
            PlayClip("flashlight_recoil", 0.08f, 1f);
            holdUntil = Time.time + 1f;
            var node = ChooseFarthestNodeFromPosition(from, avoidLineOfSight: true);
            if (node != null) SetDestinationToPosition(node.position);
        }

        private void ThinkRetreat()
        {
            agent.speed = 6f * Mult;
            if (stateTimer > 5f)
            {
                var back = Targetable(target) ? target : PlayerAt(MarkedIndex);
                if (Targetable(back)) BeginStalk(back); else GoRoam();
            }
        }

        private IEnumerator ClimbToCeiling()
        {
            SwitchToBehaviourState((int)State.Ceiling);
            stateTimer = 0f;
            ceilingWatched = 0f;
            agent.speed = 0f;
            PlayClip("climb_up", 0.1f, 2f);
            holdUntil = Time.time + 2f;
            yield return new WaitForSeconds(2f);
            if (CurrentState == State.Ceiling) SetMode(Visual.Ceiling);
        }

        private void ThinkCeiling(float dt)
        {
            if (Mode != Visual.Ceiling) return;
            if (!Targetable(target)) { Drop(null); return; }
            agent.speed = 2.6f * Mult;
            // Wait above where they are going, not where they are.
            var ahead = target.transform.position + target.transform.forward * 3f;
            SetDestinationToPosition(ahead, checkForPath: true);
            var flat = target.transform.position - transform.position;
            flat.y = 0f;
            if (flat.magnitude < 2.2f) { Drop(target); return; }
            if (CountObservers(transform.position + Vector3.up * (ceilingHeight - 1f), 25f, 30f) > 0) ceilingWatched += dt;
            if (ceilingWatched > 1.5f || stateTimer > 45f) Drop(ceilingWatched > 1.5f ? target : null);
        }

        private void Drop(PlayerControllerB onto)
        {
            SetMode(Visual.Floor);
            PlayClip("drop_down", 0.05f, 1.3f);
            holdUntil = Time.time + 1.1f;
            if (onto != null) StartCoroutine(After(1f, () => BeginHunt(onto, scream: false)));
            else StartCoroutine(After(1.3f, () => { var m = PlayerAt(MarkedIndex); if (Targetable(m)) BeginStalk(m); else GoRoam(); }));
            SwitchToBehaviourState((int)State.Stalk);
        }

        private void CheckSiege(bool ignoreTime = false)
        {
            var marked = PlayerAt(MarkedIndex);
            if (siegeDone || marked == null || marked.isPlayerDead || !marked.isInHangarShipRoom) return;
            if (!ignoreTime && (TimeOfDay.Instance == null || TimeOfDay.Instance.normalizedTimeOfDay < 0.6f)) return;
            if (CurrentState == State.Kill || StartOfRound.Instance.shipIsLeaving) return;
            var door = FindObjectOfType<HangarShipDoor>();
            if (door == null || !NavMesh.SamplePosition(door.outsideDoorPoint.position, out var hit, 6f, NavMesh.AllAreas)) return;
            siegeDone = true;
            if (currentSearch.inProgress) StopSearch(currentSearch);
            SetEnemyOutside(true);
            WarpTo(hit.position, door.transform.position);
            SwitchToBehaviourState((int)State.Siege);
            StartCoroutine(Siege(door));
        }

        private IEnumerator Siege(HangarShipDoor door)
        {
            agent.speed = 0f;
            var knockAt = door.outsideDoorPoint.position + Vector3.up * 1.5f;
            for (int round = 0; round < 2; round++)
            {
                PlayClip("knock", 0.1f, 1.5f);
                StretchedNet.ToAll(this, Msg.Knock, (ref FastBufferWriter w) => { w.WriteValueSafe(knockAt); w.WriteValueSafe(3); });
                yield return new WaitForSeconds(4f);
            }
            PlayClip("pry_door", 0.2f, -1f);
            float drainedFor = 0f;
            while (StartOfRound.Instance.hangarDoorsClosed && !StartOfRound.Instance.shipIsLeaving && CurrentState == State.Siege)
            {
                agent.speed = 0f;
                door.doorPower = Mathf.Max(0f, door.doorPower - Time.deltaTime * 0.07f * Aggro);
                if (door.doorPower <= 0f && (drainedFor += Time.deltaTime) > 4f)
                {
                    // The vanilla overheat should have thrown the doors open by now; force it.
                    StartOfRound.Instance.SetShipDoorsOverheatServerRpc();
                    StartOfRound.Instance.SetDoorsClosedServerRpc(false);
                    drainedFor = -10f;
                }
                yield return null;
            }
            if (StartOfRound.Instance.shipIsLeaving || CurrentState != State.Siege) yield break;
            var prey = PlayerAt(MarkedIndex);
            if (!Targetable(prey)) prey = TargetablePlayers().FirstOrDefault();
            if (prey != null) BeginHunt(prey, scream: true); else GoRoam();
        }

        private IEnumerator AppearBehind(PlayerControllerB p)
        {
            if (p == null) yield break;
            var behind = p.transform.position - p.transform.forward * 1.8f;
            if (!NavMesh.SamplePosition(behind, out var hit, 2f, agent.areaMask)) yield break;
            var at = p.transform.position;
            StretchedNet.ToAll(this, Msg.Flicker, (ref FastBufferWriter w) => { w.WriteValueSafe(at); w.WriteValueSafe(12f); });
            yield return new WaitForSeconds(0.1f);
            target = p;
            if (CurrentState != State.Stalk) SwitchToBehaviourState((int)State.Stalk);
            SetMode(Visual.Floor);
            WarpTo(hit.position, p.transform.position);
            PlayClip("lean_over", 0.05f, 2.2f);
            int idx = IndexOf(p);
            StretchedNet.ToAll(this, Msg.Breath, (ref FastBufferWriter w) => w.WriteValueSafe(idx));
            holdUntil = Time.time + 2.2f;
            yield return new WaitForSeconds(2.2f);
            if (CurrentState == State.Stalk && Targetable(p)) BeginHunt(p, scream: true);
        }

        // ------------------------------------------------------------------ host reactions

        private void HostStare(PlayerControllerB p)
        {
            if (p == null || Time.time < stareAnswerAt) return;
            if (CurrentState != State.Stalk && CurrentState != State.Roam) return;
            stareAnswerAt = Time.time + 20f / Aggro;
            doorwayMode = false;
            StartCoroutine(AppearBehind(p));
        }

        private void HostLit(PlayerControllerB p)
        {
            if (p == null || Time.time < retreatImmuneUntil) return;
            var s = CurrentState;
            if (s == State.Stalk || s == State.Hunt) BeginRetreat(p.transform.position);
            else if (s == State.Ceiling) Drop(null);
        }

        private void OnPowerSwitch(bool on)
        {
            if (!on || isOutside) return;
            var near = TargetablePlayers().OrderBy(p => Vector3.Distance(p.transform.position, transform.position)).FirstOrDefault();
            BeginRetreat(near != null ? near.transform.position : transform.position);
        }

        private void HostTouch(PlayerControllerB p)
        {
            if (p == null || p.isPlayerDead || inSpecialAnimation || DebugNoKill) return;
            var s = CurrentState;
            if (s != State.Hunt && s != State.Stalk && s != State.Siege) return;
            if (Vector3.Distance(p.transform.position, transform.position) > 4.5f) return;
            if (currentSearch.inProgress) StopSearch(currentSearch);
            target = p;
            SwitchToBehaviourState((int)State.Kill);
            agent.speed = 0f;
            int idx = IndexOf(p);
            StretchedNet.ToAll(this, Msg.Kill, (ref FastBufferWriter w) => w.WriteValueSafe(idx));
        }

        private void HostNoise(Vector3 pos, float loudness)
        {
            var s = CurrentState;
            if (s == State.Kill || s == State.Retreat || s == State.Siege || s == State.Ceiling || inSpecialAnimation) return;
            float d = Vector3.Distance(pos, transform.position);
            if (loudness < 0.35f || d > 30f * loudness) return;
            var maker = TargetablePlayers().OrderBy(p => Vector3.Distance(p.transform.position, pos)).FirstOrDefault();
            if (maker == null || Vector3.Distance(maker.transform.position, pos) > 6f) return;
            if (s == State.Roam) { BeginStalk(maker); SetDestinationToPosition(pos); }
            else if (s == State.Stalk && loudness > 0.6f && d < 15f) BeginHunt(maker, scream: true);
            else if (s == State.Hunt) lastSeenTarget = Time.time;
        }

        private void HostAfterKill(PlayerControllerB victim)
        {
            int idx = IndexOf(victim);
            StretchedNet.ToAll(this, Msg.Suit, (ref FastBufferWriter w) => w.WriteValueSafe(idx));
            var node = ChooseFarthestNodeFromPosition(victim.transform.position, avoidLineOfSight: true);
            if (node != null) WarpTo(node.position, victim.transform.position);
            if (MarkedIndex == idx) ChooseMarked();
            GoRoam(force: true);
        }

        // ------------------------------------------------------------------ host helpers

        private void GoRoam(bool force = false)
        {
            if (Busy && !force) return;
            target = null;
            doorwayMode = false;
            stateTimer = 0f;
            if (Mode != Visual.Floor) SetMode(Visual.Floor);
            SwitchToBehaviourState((int)State.Roam);
        }

        private void ChooseMarked()
        {
            var pool = TargetablePlayers().ToList();
            if (pool.Count == 0) pool = Players.Where(p => p != null && p.isPlayerControlled && !p.isPlayerDead).ToList();
            MarkedIndex = pool.Count > 0 ? IndexOf(pool[UnityEngine.Random.Range(0, pool.Count)]) : -1;
            BroadcastMark();
        }

        private void ValidateMarked()
        {
            var m = PlayerAt(MarkedIndex);
            if (m == null || m.isPlayerDead || !m.isPlayerControlled) ChooseMarked();
        }

        private void BroadcastMark()
        {
            int m = MarkedIndex, e = Escapes;
            StretchedNet.ToAll(this, Msg.Mark, (ref FastBufferWriter w) => { w.WriteValueSafe(m); w.WriteValueSafe(e); });
        }

        private void SetMode(Visual mode)
        {
            if (Mode == mode) return;
            byte b = (byte)mode;
            StretchedNet.ToAll(this, Msg.Mode, (ref FastBufferWriter w) => w.WriteValueSafe(b));
        }

        private void PlayClip(string clip, float fadeIn, float lockFor, int snap = -1, float speed = 1f)
        {
            StretchedNet.ToAll(this, Msg.Clip, (ref FastBufferWriter w) =>
            {
                w.WriteValueSafe(clip);
                w.WriteValueSafe(fadeIn);
                w.WriteValueSafe(lockFor);
                w.WriteValueSafe(snap);
                w.WriteValueSafe(speed);
            });
        }

        private void WarpTo(Vector3 position, Vector3 faceToward)
        {
            agent.Warp(position);
            var flat = faceToward - position;
            flat.y = 0f;
            if (flat.sqrMagnitude > 0.01f) transform.rotation = Quaternion.LookRotation(flat);
            serverPosition = position;
            SyncPositionToClients();
            float yaw = transform.eulerAngles.y;
            bool outside = isOutside;
            StretchedNet.ToAll(this, Msg.Teleport, (ref FastBufferWriter w) =>
            {
                w.WriteValueSafe(position);
                w.WriteValueSafe(yaw);
                w.WriteValueSafe(outside);
            });
        }

        private IEnumerable<PlayerControllerB> TargetablePlayers() =>
            Players.Where(p => p != null && Targetable(p));

        private bool Targetable(PlayerControllerB p) =>
            p != null && p.isPlayerControlled && !p.isPlayerDead && PlayerIsTargetable(p, cannotBeInShip: false, overrideInsideFactoryCheck: CurrentState == State.Siege);

        private int CountObservers(Vector3 point, float range, float halfAngle) =>
            TargetablePlayers().Count(p => IsInView(p.gameplayCamera.transform, point, range, halfAngle));

        private static bool CanPlayerSee(PlayerControllerB p, Vector3 point, float halfAngle = 45f) =>
            p != null && IsInView(p.gameplayCamera.transform, point, 40f, halfAngle);

        private bool CanSeeFromEye(PlayerControllerB p) =>
            p != null && !Physics.Linecast(eye.position, p.gameplayCamera.transform.position, StartOfRound.Instance.collidersAndRoomMask, QueryTriggerInteraction.Ignore);

        private static Vector3 PointAlongPath(NavMeshPath path, float distance)
        {
            var corners = path.corners;
            if (corners.Length == 0) return Vector3.zero;
            for (int i = 1; i < corners.Length; i++)
            {
                float seg = Vector3.Distance(corners[i - 1], corners[i]);
                if (distance <= seg) return Vector3.Lerp(corners[i - 1], corners[i], distance / seg);
                distance -= seg;
            }
            return corners[corners.Length - 1];
        }

        /// <summary>A nav-mesh point the player has a clear view of, roughly `distance` away, that they aren't looking at right now.</summary>
        private Vector3? FindSpotInView(PlayerControllerB p, float distance, float tolerance, bool mustBeOutOfFocus)
        {
            if (p == null || allAINodes == null) return null;
            var cam = p.gameplayCamera.transform;
            Vector3? best = null;
            float bestErr = float.MaxValue;
            foreach (var node in allAINodes)
            {
                if (node == null) continue;
                var pos = node.transform.position;
                float d = Vector3.Distance(pos, p.transform.position);
                float err = Mathf.Abs(d - distance);
                if (err > tolerance || err >= bestErr) continue;
                var head = pos + Vector3.up * 2.5f;
                if (Physics.Linecast(cam.position, head, StartOfRound.Instance.collidersAndRoomMask, QueryTriggerInteraction.Ignore)) continue;
                if (mustBeOutOfFocus && Vector3.Angle(cam.forward, head - cam.position) < 55f) continue;
                best = pos;
                bestErr = err;
            }
            return best;
        }

        private static DoorLock NearestClosedDoor(Vector3 near, float range)
        {
            DoorLock best = null;
            float bestD = range;
            foreach (var door in FindObjectsOfType<DoorLock>())
            {
                if (door == null || door.isLocked) continue;
                var trigger = door.GetComponent<AnimatedObjectTrigger>();
                if (trigger != null && trigger.boolValue) continue; // already open
                float d = Vector3.Distance(door.transform.position, near);
                if (d < bestD) { bestD = d; best = door; }
            }
            return best;
        }

        private int PickVoiceSource()
        {
            var candidates = Players.Where(p => p != null && p != target && (p.isPlayerControlled || p.isPlayerDead)).ToList();
            var dead = candidates.Where(p => p.isPlayerDead).ToList();
            var pool = dead.Count > 0 && UnityEngine.Random.value < 0.6f ? dead : candidates;
            return pool.Count > 0 ? IndexOf(pool[UnityEngine.Random.Range(0, pool.Count)]) : -1;
        }

        private static IEnumerator After(float seconds, Action action)
        {
            yield return new WaitForSeconds(seconds);
            action();
        }

        // ------------------------------------------------------------------ hooks for the automated self-test

        internal bool DebugNoKill;

        internal void DebugStare(PlayerControllerB p) { stareAnswerAt = 0f; BeginStalk(p); HostStare(p); }
        internal void DebugLit(PlayerControllerB p) { retreatImmuneUntil = 0f; BeginHunt(p, scream: false); HostLit(p); }
        internal void DebugNoise(PlayerControllerB p) { GoRoam(); HostNoise(p.transform.position, 0.9f); }
        internal void DebugCeiling(PlayerControllerB p) { BeginStalk(p); StartCoroutine(ClimbToCeiling()); }
        internal void DebugFlicker(PlayerControllerB p) { BeginStalk(p); StartCoroutine(FlickerStep()); }
        internal void DebugDoorway(PlayerControllerB p) { BeginStalk(p); TryDoorwayAppear(Vector3.Distance(p.transform.position, transform.position) + 10f); }
        internal void DebugKnockAndVoice(PlayerControllerB p)
        {
            var kp = p.transform.position;
            StretchedNet.ToAll(this, Msg.Knock, (ref FastBufferWriter w) => { w.WriteValueSafe(kp); w.WriteValueSafe(3); });
            int src = IndexOf(p);
            StretchedNet.ToAll(this, Msg.Voice, (ref FastBufferWriter w) => w.WriteValueSafe(src));
        }
        internal void DebugSiege(PlayerControllerB p)
        {
            MarkedIndex = IndexOf(p);
            BroadcastMark();
            siegeDone = false;
            CheckSiege(ignoreTime: true);
        }

        internal void DebugKill(PlayerControllerB p)
        {
            DebugNoKill = false;
            BeginHunt(p, scream: false);
            if (NavMesh.SamplePosition(p.transform.position + p.transform.forward * 1.5f, out var hit, 3f, agent.areaMask))
                WarpTo(hit.position, p.transform.position);
            HostTouch(p);
        }
        internal string DebugSummary() =>
            $"state={CurrentState} mode={Mode} clip={(anim != null && anim.Current != null ? anim.Current.Name : "-")} " +
            $"pos={transform.position} speed={speedEstimate:0.0} ceiling={ceilingHeight:0.0} target={(target != null ? target.playerUsername : "-")} " +
            $"marked={MarkedIndex} escapes={Escapes} visible={(body != null && body.enabled)} inAnim={inSpecialAnimation}";

        private static void LogOnce(string where, Exception e)
        {
            string key = where + e.GetType().Name + e.Message;
            if (loggedErrors.Contains(key)) return;
            loggedErrors.Add(key);
            Plugin.Log.LogError($"The Stretched [{where}]: {e}");
        }
    }
}
