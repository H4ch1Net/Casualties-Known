using System;
using CCoop.Net.Session;
using CCoop.Plugin.Protocol;
using CCoop.Plugin.Runtime;
using CCoop.Plugin.World;
using HarmonyLib;
using UnityEngine;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// Lets crawling creatures (everything driven by the game's <c>SpiderHandler</c>) hunt
    /// every player, not just the one whose machine runs them.
    ///
    /// The game's crawler looks for anything on the Body/Limb layers when it picks a new
    /// target, and bites through a physical collision. Puppets have no colliders (on
    /// purpose: traps and turrets scan those layers too), so on the owner's machine we add
    /// the puppets to the crawler's choice and do the bite contact by distance. A bite on
    /// a puppet is sent to that player, whose machine runs the creature's own
    /// <c>DamageLimb</c> on the real limb, with the bite sound, scare, speech and threat
    /// music, exactly as if the creature were simulated there.
    /// </summary>
    internal sealed class CreatureHunt : CoopModule
    {
        private static float _nextMissLog;

        internal static readonly AccessTools.FieldRef<SpiderHandler, float> BiteCooldown =
            AccessTools.FieldRefAccess<SpiderHandler, float>("biteCooldown");

        internal override void Attach(NetSession s) => s.On<CreatureBite>(OnBite);

        // ------------------------------------------------------------------ owner side

        /// <summary>After the crawler's own Update, on the machine that simulates it.</summary>
        internal static void AfterThink(SpiderHandler h, float moveTimeBefore)
        {
            var ps = Coop.Get<PlayerSync>();
            if (ps == null || h.stunTime > 0f) return;
            Vector2 at = h.transform.position;
            var local = GameInfo.LocalBody;
            float localDist = local != null && local.alive ? Vector2.Distance(at, local.transform.position) : float.MaxValue;

            RemotePlayer nearest = null;
            float nearestDist = float.MaxValue;
            foreach (var r in ps.Remotes)
            {
                var p = r.ShownAt;
                if (!p.HasValue || !Coop.Player(r.Id).Alive) continue;
                float d = Vector2.Distance(at, p.Value);
                if (d < nearestDist) { nearestDist = d; nearest = r; }
            }
            if (nearest == null) return;

            // 1. Choosing a target: the game just picked one (its move timer was reset).
            if (h.moveTime > moveTimeBefore + 0.0001f && nearestDist <= h.seeDistance && nearestDist < localDist && Visible(h, nearest.ShownAt.Value))
            {
                var bld = h.GetComponent<BuildingEntity>();
                bool retreat = UnityEngine.Random.value > h.aggro || (bld != null && bld.health <= h.retreatHealth);
                Vector2 them = nearest.ShownAt.Value;
                h.target = retreat
                    ? (at - them).normalized * 15f + UnityEngine.Random.insideUnitCircle * 5f + at
                    : them;
                if (!retreat && h.attackSounds != null && h.attackSounds.Count > 0)
                    Sound.Play(h.attackSounds[UnityEngine.Random.Range(0, h.attackSounds.Count)], at);
            }

            // 2. Biting: contact by distance, since puppets have no colliders.
            if (BiteCooldown(h) > 0f || nearestDist > 4f) return;
            float reach = Reach(h);
            int limb = nearest.NearestLimb(at, reach);
            if (limb < 0)
            {
                if (nearestDist < 1.5f && Time.unscaledTime >= _nextMissLog)
                {
                    _nextMissLog = Time.unscaledTime + 2f;
                    Log.Info("[hunt] " + h.name + " is " + nearestDist.ToString("F2") + " from a puppet but no limb within " + reach.ToString("F2"));
                }
                return;
            }

            BiteCooldown(h) = h.biteCoolToSet;
            try { Sound.Play(h.biteSound, at); } catch { /* missing clip */ }
            Vector2 victim = nearest.ShownAt.Value;
            h.target = (at - victim).normalized * 15f + at;
            h.moveTime = h.retreatMoveTime;
            if (EntityRegistry.TryGetId(h.gameObject, out var id))
            {
                Coop.Session.Broadcast(new CreatureBite { Creature = id, Victim = nearest.Id, Limb = (byte)limb });
                Log.Info("[hunt] " + h.name + " bit " + Coop.Player(nearest.Id).Name + " (limb " + limb + ")");
            }
            else Log.Warn("[hunt] " + h.name + " bit a puppet but isn't a registered entity");
        }

        private static bool Visible(SpiderHandler h, Vector2 target)
        {
            foreach (var hit in Physics2D.LinecastAll(h.transform.position, target, LayerMask.GetMask("Ground")))
                if (hit.transform != h.transform) return false;
            return true;
        }

        private static float Reach(SpiderHandler h)
        {
            var col = h.GetComponent<Collider2D>();
            float r = col != null ? Mathf.Max(col.bounds.extents.x, col.bounds.extents.y) : 0.5f;
            return r + 0.45f;
        }

        // ------------------------------------------------------------------ victim side

        private void OnBite(byte origin, CreatureBite m)
        {
            if (m.Victim != Coop.LocalId || !Coop.WorldLive) return;
            var body = GameInfo.LocalBody;
            if (body == null || !body.alive || body.limbs == null || body.limbs.Length == 0) return;
            if (!EntityRegistry.TryGet(m.Creature, out var go) || go == null) return;
            var h = go.GetComponent<SpiderHandler>();
            if (h == null) return;
            var limb = body.limbs[Mathf.Clamp(m.Limb, 0, body.limbs.Length - 1)];
            if (limb == null) return;
            try
            {
                // CheckForLimbDamage, minus the collision: what the game does to a bitten player.
                try { Sound.Play(h.biteSound, go.transform.position); } catch { /* missing clip */ }
                body.eyeScareTime = 5f;
                body.talker?.Talk(Locale.GetCharacter("hitbycreature"), null, false, true);
                body.happiness -= h.happinessLoss;
                h.PlayThreatMusic();
                h.DamageLimb(limb);
                if (h.hitConnected && limb.connectedLimbs != null)
                    foreach (var other in limb.connectedLimbs)
                        if (other != null) h.DamageLimb(other);
                Log.Info("[hunt] bitten by " + go.name + " on limb " + m.Limb);
            }
            catch (Exception e) { Log.Warn("[hunt] could not apply a bite: " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(SpiderHandler), "Update")]
    internal static class SpiderHuntPatch
    {
        private static void Prefix(SpiderHandler __instance, out float __state) => __state = __instance.moveTime;

        private static void Postfix(SpiderHandler __instance, float __state)
        {
            if (!Coop.WorldLive || __instance == null || CreatureSync.IsPuppetObject(__instance.gameObject)) return;
            try { CreatureHunt.AfterThink(__instance, __state); }
            catch (Exception e) { Log.Debug("[hunt] " + e.Message); }
        }
    }
}
