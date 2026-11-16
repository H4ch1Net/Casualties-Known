using CCoop.Plugin.Runtime;
using HarmonyLib;
using UnityEngine;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// Gestures the character actually performs: a wave, a beckon, a thumbs up, a raised
    /// palm, a shaken head, both arms up for help, a hug, a nod, a shrug, and a big "this
    /// way" swing of the arm.
    ///
    /// While standing, the game places every limb each frame from a hidden animation rig
    /// (Body.HandleVisuals); limbs aren't physics then. Right after it, a gesture swings
    /// the arm segments about their own joints (shoulder, elbow, wrist, in the order the
    /// arm hangs together) and nods or shakes the head about the neck, easing in and out.
    /// Nothing else moves the limbs before they're drawn, and teammates' puppets copy the
    /// real limbs, so everyone sees the gesture. A held item stays in the moving hand.
    /// Angles are for a body facing right (0 = hanging down, 90 = forward, 180 = up)
    /// and are mirrored when it faces left.
    /// </summary>
    internal static class GesturePose
    {
        internal enum Pose : byte { None, Wave, Beckon, ThumbsUp, Wait, No, Help, Hug, Yes, Shrug, FollowMe }

        private static Pose _pose;
        private static float _start, _length;

        // Front arm (the main hand) and back arm, upper arm -> forearm -> hand.
        private static readonly int[] Front = { 3, 4, 5 };
        private static readonly int[] Back = { 6, 7, 8 };

        internal static bool Active => _pose != Pose.None && Time.time - _start < _length;

        /// <summary>Starts a gesture on the local character (<paramref name="target"/>: where Follow me leads).</summary>
        internal static void Play(Pose pose, Vector2 target = default)
        {
            var b = GameInfo.LocalBody;
            if (b == null || !b.alive || !b.conscious) return;
            _pose = pose;
            _start = Time.time;
            _length = pose == Pose.Help ? 2.2f : pose == Pose.No || pose == Pose.Yes ? 1.3f : pose == Pose.ThumbsUp ? 1.4f : pose == Pose.Shrug ? 1.5f : 1.8f;
            // Lead the way back the other way: turn round first, as anyone would.
            if (pose == Pose.FollowMe && target != default && b.standing && (target.x - b.transform.position.x) * (b.isRight ? 1f : -1f) < -0.3f)
            {
                try { b.SwitchDir(); } catch { /* can't turn now */ }
            }
        }

        internal static void Stop() => _pose = Pose.None;

        [HarmonyPatch(typeof(Body), "HandleVisuals")]
        internal static class VisualsPatch
        {
            private static void Postfix(Body __instance)
            {
                if (_pose == Pose.None || !Coop.IsLocalBody(__instance)) return;
                var b = __instance;
                float t = Time.time - _start;
                if (t >= _length || !b.alive || !b.conscious || !b.standing || b.limbs == null || b.limbs.Length < 9) { if (t >= _length) _pose = Pose.None; return; }
                try { Apply(b, t); }
                catch (System.Exception e) { _pose = Pose.None; Log.Debug("[gesture] pose: " + e.Message); }
            }
        }

        private static void Apply(Body b, float t)
        {
            b.idleTime = 0f;  // no sitting down mid-wave
            float w = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / 0.2f)) * Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((_length - t) / 0.25f));
            float s = Mathf.Sin(t * 11f);
            switch (_pose)
            {
                case Pose.Wave:
                    Arm(b, Front, w, 158f, 172f + 26f * s, 178f + 34f * s);
                    break;
                case Pose.Beckon:
                    float curl = 0.5f + 0.5f * Mathf.Sin(t * 9f);
                    Arm(b, Front, w, 82f, 88f + 62f * curl, 96f + 80f * curl);
                    break;
                case Pose.ThumbsUp:
                    Arm(b, Front, w, 34f, 118f, 176f);
                    break;
                case Pose.Wait:
                    Arm(b, Front, w, 86f, 96f, 178f);
                    break;
                case Pose.No:
                    Head(b, w * 13f * Mathf.Sin(t * 17f));
                    break;
                case Pose.Help:
                    float c = Mathf.Sin(t * 9f);
                    Arm(b, Front, w, 166f + 14f * c, 178f + 22f * c, 182f + 26f * c);
                    Arm(b, Back, w, 194f - 14f * c, 182f - 22f * c, 178f - 26f * c);
                    Head(b, w * -8f);
                    break;
                case Pose.Hug:
                    Arm(b, Front, w, 78f, 146f, 168f);
                    Arm(b, Back, w, 70f, 140f, 160f);
                    break;
                case Pose.Yes:
                    // Two slow nods, down and back up (No is the quick shake).
                    Head(b, -w * 16f * (0.5f - 0.5f * Mathf.Cos(t * 10f)));
                    break;
                case Pose.Shrug:
                {
                    // Forearms out to either side, palms up, a little lift as they turn up.
                    float lift = 8f * Mathf.Sin(Mathf.Clamp01(t / 0.6f) * Mathf.PI);
                    Arm(b, Front, w, 16f + lift, 98f, 122f);
                    Arm(b, Back, w, -16f - lift, -98f, -122f);
                    Head(b, w * 7f);
                    break;
                }
                case Pose.FollowMe:
                {
                    // "Move out": the straight arm raised overhead and brought down to point
                    // ahead, again and again: this way!
                    float sweep = 0.5f - 0.5f * Mathf.Cos(t * 7f);
                    float a = 165f - 80f * sweep;
                    Arm(b, Front, w, a, a + 6f, a + 12f);
                    b.overrideLookPos = (Vector2)b.transform.position + new Vector2(b.isRight ? 8f : -8f, 1f);
                    b.overrideLookTime = 0.2f;
                    break;
                }
            }
        }

        /// <summary>An arm posed by angles for a right-facing body (mirrored when facing left).</summary>
        private static void Arm(Body b, int[] chain, float w, float upper, float fore, float hand)
        {
            float num = b.isRight ? 1f : -1f;
            ArmWorld(b, chain, w, upper * num, fore * num, hand * num);
        }

        /// <summary>
        /// An arm posed by world angles: each segment turns about the joint it hangs from,
        /// and the next is carried along, so the arm stays in one piece.
        /// </summary>
        private static void ArmWorld(Body b, int[] chain, float w, params float[] angles)
        {
            // Where each lower segment hangs, in its parent's frame, before anything moves.
            var limbs = new Limb[chain.Length];
            var hang = new Vector3[chain.Length];
            for (int i = 0; i < chain.Length; i++)
            {
                limbs[i] = chain[i] < b.limbs.Length ? b.limbs[chain[i]] : null;
                if (limbs[i] == null || limbs[i].dismembered || limbs[i].joint == null) return;
                if (i > 0) hang[i] = limbs[i - 1].transform.InverseTransformPoint(Joint(limbs[i]));
            }
            Vector2 pivot = Joint(limbs[0]);
            for (int i = 0; i < chain.Length; i++)
            {
                if (i > 0) pivot = limbs[i - 1].transform.TransformPoint(hang[i]);
                var tr = limbs[i].transform;
                float a = Mathf.LerpAngle(tr.eulerAngles.z, angles[i], w);
                tr.eulerAngles = new Vector3(0f, 0f, a);
                Vector2 now = Joint(limbs[i]);
                tr.position += (Vector3)(pivot - now);
            }
        }

        /// <summary>The head turned about the neck by <paramref name="degrees"/>.</summary>
        private static void Head(Body b, float degrees)
        {
            var head = b.limbs[0];
            if (head == null || head.joint == null || Mathf.Abs(degrees) < 0.01f) return;
            head.transform.RotateAround(Joint(head), Vector3.forward, degrees * (b.isRight ? 1f : -1f));
        }

        /// <summary>The point a limb hangs from (its joint's anchor), in the world.</summary>
        private static Vector2 Joint(Limb l) => l.transform.TransformPoint(l.joint.anchor);
    }
}
