using System;
using CCoop.Plugin.Protocol;
using CCoop.Plugin.Runtime;
using HarmonyLib;
using UnityEngine;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// The dust a body kicks up (Body.CreateCloudBig/Small/Mini: steps, jumps, landings,
    /// blows on walls, picking things up). A teammate's puppet has no Body of its own, so
    /// without this nobody else ever saw their dust. The local body's clouds are sent,
    /// a few a second at most, and the others make the same cloud where it happened.
    /// </summary>
    internal sealed class DustSync : CoopModule
    {
        private const float MaxPerSecond = 8f;
        private const float SeenWithin = 40f;
        private static readonly string[] Prefabs = { "DustMini", "DustSmall", "DustBig" };
        private static readonly GameObject[] Loaded = new GameObject[3];

        private static float _budget = MaxPerSecond;
        private static float _lastRefill;
        private static bool _replaying;

        internal override void Attach(CCoop.Net.Session.NetSession s) => WorldNet.Register<DustPuff>(s, (origin, m) => Make(m), queueWhilePending: false);

        internal static void Sent(Body body, byte size, Vector2 pos, Vector2? vel)
        {
            if (_replaying || !Coop.InSession || !Coop.WorldLive) return;
            var me = GameInfo.LocalBody;
            // Only the player's own dust, near them (some game code makes clouds through the player's body for other things).
            if (me == null || !ReferenceEquals(body, me) || Vector2.Distance(pos, me.transform.position) > 4f) return;
            float now = Time.unscaledTime;
            _budget = Mathf.Min(MaxPerSecond, _budget + (now - _lastRefill) * MaxPerSecond);
            _lastRefill = now;
            if (_budget < 1f && size < 2) return;
            _budget -= 1f;
            Vector2 v = vel ?? (body.GetComponent<Rigidbody2D>()?.velocity ?? Vector2.zero);
            WorldNet.Publish(new DustPuff { Size = size, X = pos.x, Y = pos.y, VX = v.x, VY = v.y });
        }

        // ---- splashes: the game's own (LiquidAffect), for a teammate stepping or falling into liquid

        private static readonly AccessTools.FieldRef<float> LastSplash = Ref();
        private static readonly AccessTools.FieldRef<LiquidAffect, Limb> SplashLimb = AccessTools.FieldRefAccess<LiquidAffect, Limb>("limb");
        private static readonly string[] SplashSounds = { "Sounds/Splash/Small", "Sounds/Splash/Medium", "Sounds/Splash/Big" };
        private static readonly AudioClip[][] SplashClips = new AudioClip[3][];

        private static AccessTools.FieldRef<float> Ref()
        {
            try { return AccessTools.StaticFieldRefAccess<float>(AccessTools.Field(typeof(LiquidAffect), "lastSplash")); }
            catch { return null; }
        }

        [HarmonyPatch(typeof(LiquidAffect), "FixedUpdate")]
        internal static class SplashPatch
        {
            private static void Prefix(out float __state) => __state = LastSplash != null ? LastSplash() : 0f;

            private static void Postfix(LiquidAffect __instance, float __state)
            {
                if (LastSplash == null || LastSplash() == __state || _replaying || !Coop.InSession || !Coop.WorldLive) return;
                try
                {
                    var limb = SplashLimb(__instance);
                    var me = GameInfo.LocalBody;
                    if (limb == null || me == null || !ReferenceEquals(limb.body, me)) return;
                    float speed = me.rb != null ? me.rb.velocity.magnitude : 0f;
                    if (speed <= 0.5f) return;
                    byte size = (byte)(speed > 20f ? 5 : speed > 8f ? 4 : 3);
                    Vector2 at = __instance.transform.position;
                    WorldNet.Publish(new DustPuff { Size = size, X = at.x, Y = at.y });
                }
                catch (Exception e) { Log.Debug("splash: " + e.Message); }
            }
        }

        private static void Splash(DustPuff m, Vector2 at)
        {
            int k = m.Size - 3;
            if (SplashClips[k] == null) SplashClips[k] = Resources.LoadAll<AudioClip>(SplashSounds[k]) ?? new AudioClip[0];
            if (SplashClips[k].Length > 0) Sound.Play(SplashClips[k][UnityEngine.Random.Range(0, SplashClips[k].Length)], at);
            int dust = k == 0 ? 1 : 2;
            if (Loaded[dust] == null) Loaded[dust] = Resources.Load<GameObject>(Prefabs[dust]);
            if (Loaded[dust] != null) UnityEngine.Object.Instantiate(Loaded[dust], at, Quaternion.identity);
        }

        private static void Make(DustPuff m)
        {
            try
            {
                var cam = Camera.main;
                var at = new Vector2(m.X, m.Y);
                if (cam == null || Vector2.Distance(cam.transform.position, at) > SeenWithin || m.Size > 5) return;
                if (m.Size >= 3) { _replaying = true; Splash(m, at); return; }
                if (Loaded[m.Size] == null) Loaded[m.Size] = Resources.Load<GameObject>(Prefabs[m.Size]);
                if (Loaded[m.Size] == null) return;
                _replaying = true;
                var go = UnityEngine.Object.Instantiate(Loaded[m.Size], at, Quaternion.identity);
                var ps = go.GetComponent<ParticleSystem>();
                if (ps != null)
                {
                    var main = ps.main;
                    main.emitterVelocity = new Vector2(m.VX, m.VY);
                }
            }
            catch (Exception e) { Log.Debug("dust: " + e.Message); }
            finally { _replaying = false; }
        }

        [HarmonyPatch(typeof(Body), nameof(Body.CreateCloudBig))]
        internal static class BigPatch
        {
            private static void Postfix(Body __instance, Vector2 pos, Vector2? vel) => Sent(__instance, 2, pos, vel);
        }

        [HarmonyPatch(typeof(Body), nameof(Body.CreateCloudSmall))]
        internal static class SmallPatch
        {
            private static void Postfix(Body __instance, Vector2 pos, Vector2? vel) => Sent(__instance, 1, pos, vel);
        }

        [HarmonyPatch(typeof(Body), nameof(Body.CreateCloudMini))]
        internal static class MiniPatch
        {
            private static void Postfix(Body __instance, Vector2 pos, Vector2? vel) => Sent(__instance, 0, pos, vel);
        }
    }
}
