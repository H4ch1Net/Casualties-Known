using System;
using CCoop.Protocol.Messages;
using HarmonyLib;
using UnityEngine;

namespace CCoop.Plugin
{
    /// <summary>
    /// Publishes the local player's swings so other peers can draw them.
    ///
    /// <c>Body.Attack</c> instantiates <c>AttackInfo.attackAnim</c> and plays a swing
    /// sound directly, both local-only — so on every other screen an arm moved and
    /// nothing else happened. The damage was never the problem: that already crosses the
    /// wire as terrain edits and entity damage. This is the feedback that told you
    /// somebody swung.
    ///
    /// Guarded on the game's own entry condition, captured in a prefix — NOT on the
    /// return value. <c>Attack</c> returns true only when the swing <em>connected</em>:
    /// it sets its <c>flag</c> local when the raycast damages a block or a
    /// <c>BuildingEntity</c>, and returns false otherwise. So a clean miss runs the whole
    /// visible half of the method — it instantiates <c>attackAnim</c> and plays the swing
    /// sound — and then answers false.
    ///
    /// Guarding on the result therefore published exactly the swings that hit and
    /// swallowed every miss, which is what a player saw: arcs appeared when their partner
    /// connected with something and never when they swung at air.
    /// <c>conscious &amp;&amp; attackCooldown &lt;= 0f</c> is the condition wrapping the
    /// entire body of <c>Attack</c>, so it is true exactly when a swing is about to be
    /// drawn, hit or miss. It has to be read in a prefix because the method assigns
    /// <c>attackCooldown</c> partway through.
    /// </summary>
    [HarmonyPatch(typeof(Body), nameof(Body.Attack))]
    internal static class AttackPatch
    {
        private static void Prefix(Body __instance, out bool __state)
        {
            __state = false;
            try { __state = __instance.conscious && __instance.attackCooldown <= 0f; }
            catch (Exception e) { CoopPlugin.Warn("attack prefix error: " + e.Message); }
        }

        private static void Postfix(Body __instance, AttackInfo atk, bool __state)
        {
            try
            {
                if (!__state || atk == null || atk.attackAnim == null) return;

                var runner = CoopRunner.Active;
                if (runner == null || !runner.InSession) return;

                // Only our own player's swings are ours to announce.
                var localBody = PlayerCamera.main != null ? PlayerCamera.main.body : null;
                if (__instance != localBody) return;

                runner.AnnounceAttack(__instance, atk);
            }
            catch (Exception e)
            {
                // A cosmetic broadcast must never interfere with hitting things.
                CoopPlugin.Warn("attack patch error: " + e.Message);
            }
        }
    }

    /// <summary>Works out what a swing looked like, in the terms the wire carries.</summary>
    internal static class AttackSync
    {
        /// <summary>
        /// Reproduces the arc rotation <c>Body.Attack</c> computed.
        ///
        /// Recomputed rather than read back, because the game keeps it in a local and
        /// throws it away. Same inputs, same frame: aim and limb positions have not moved
        /// since, so the two agree to well within a degree — and this is a swing arc, not
        /// a hitbox, so even a degree out would be invisible.
        /// </summary>
        internal static float ArcRotation(Body body)
        {
            if (body.limbs == null || body.limbs.Length < 2 || body.limbs[1] == null) return 0f;

            Vector2 from = body.limbs[1].transform.position;
            Vector2 direction = ((Vector2)body.targetLookPos - from).normalized;

            return Vector2.SignedAngle(body.isRight ? Vector3.right : Vector3.left, direction);
        }

        /// <summary>Picks a swing sound the way the game does, or empty if it has none.</summary>
        internal static string SwingSound(AttackInfo atk)
        {
            if (atk.swingSounds == null || atk.swingSounds.Length == 0) return string.Empty;
            return atk.swingSounds[UnityEngine.Random.Range(0, atk.swingSounds.Length)] ?? string.Empty;
        }
    }
}
