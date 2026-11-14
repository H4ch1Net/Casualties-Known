using System;
using CCoop.Net.Session;
using CCoop.Plugin.Protocol;
using CCoop.Plugin.Runtime;
using CCoop.Plugin.UI;
using CCoop.Plugin.World;
using HarmonyLib;
using UnityEngine;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// Friendly fire for blows, under the host's friendly-fire rule
    /// (guns are in <see cref="ShotSync"/>). The game's own melee only ever hits
    /// creatures and blocks, and a teammate is a drawn puppet on the attacker's screen,
    /// so a swing is checked there against the teammate's limbs, using the game's own
    /// swing shape (a 1 x 3.5 box along the swing, as far as the weapon reaches, not
    /// through rock). The hit lands on the victim's machine through the game's own impact
    /// code (<c>Limb.ImpactDamage</c>: bruising, dislocations, broken bones, a knock to the
    /// head), at a force from the weapon's damage and the attacker's strength. Cutting
    /// weapons also cut. A heavy blow knocks a standing player off their feet.
    ///
    /// Thrown things are left alone: in the game an item in flight passes through bodies
    /// and hurts nothing (only falling crates do), so it doesn't hurt teammates either.
    /// </summary>
    internal sealed class FriendlyFireSync : CoopModule
    {
        /// <summary>Weapon damage (after strength) to impact force: claws ~36, a machete ~60, a sledgehammer ~72+.</summary>
        private const float ForcePerDamage = 1.2f;
        private const float SwingHalfWidth = 1.75f;

        internal static int HitsSent, HitsTaken, HitsRefused;

        /// <summary>The limb the last blow landed on here (for the self-test), or -1.</summary>
        internal static int LastLimb = -1;

        private static readonly AccessTools.FieldRef<Body, float> BonusStrength = AccessTools.FieldRefAccess<Body, float>("bonusStrength");

        internal override void Attach(NetSession s) => WorldNet.Register<PlayerHit>(s, OnHit);

        // ------------------------------------------------------------------ the attacker

        /// <summary>After a local swing: did it reach a teammate?</summary>
        internal void Swung(Body b, AttackInfo atk)
        {
            if (!HostRules.Current.FriendlyFire || !Coop.WorldLive || b == null || atk == null || !atk.physicalSwing) return;
            var ps = Coop.Get<PlayerSync>();
            if (ps == null || b.limbs == null || b.limbs.Length < 2) return;
            Vector2 origin = b.limbs[1].transform.position;
            Vector2 dir = (Vector2)b.targetLookPos - origin;
            if (dir.sqrMagnitude < 1e-4f) return;
            dir.Normalize();
            float reach = atk.distance + 0.5f;
            int ground = LayerMask.GetMask("Ground");

            RemotePlayer victim = null;
            int limb = -1;
            float best = float.MaxValue;
            Vector2 at = origin;
            foreach (var r in ps.Remotes)
            {
                var rec = Coop.Player(r.Id);
                if (!rec.Alive || !Coop.InLayer(rec) || !r.Visible) continue;
                for (int i = 0; i < 16; i++)
                {
                    var p = r.LimbPosition(i);
                    if (!p.HasValue) { if (i > 0) break; continue; }
                    Vector2 d = p.Value - origin;
                    float along = Vector2.Dot(d, dir);
                    if (along < 0f || along > reach || along >= best) continue;
                    if (Mathf.Abs(d.x * dir.y - d.y * dir.x) > SwingHalfWidth) continue;
                    if (Physics2D.Linecast(origin, p.Value, ground)) continue;
                    best = along;
                    victim = r;
                    limb = i;
                    at = p.Value;
                }
            }
            if (victim == null) return;

            float damage = atk.damage * Power(b, atk);
            var held = Medic.HeldItem(b);
            bool cutting = atk.piercing || (held != null && held.Stats?.qualities != null && held.Stats.qualities.Exists(q => q != null && q.id == "cutting"));
            var hit = new PlayerHit
            {
                Victim = victim.Id,
                Limb = (byte)limb,
                Force = damage * ForcePerDamage,
                Cut = cutting ? damage * 0.4f : atk.unarmed ? damage * 0.15f : 0f,
                DX = dir.x,
                DY = dir.y,
                Heavy = atk.knockBack * Power(b, atk) >= 600f,
                ItemId = held != null ? held.id : "",
            };
            WorldNet.Publish(hit);
            HitsSent++;
            try { Sound.Play(hit.Cut > 0f ? "gore" : "bodyFall" + UnityEngine.Random.Range(1, 6), at); }
            catch { /* no sound */ }
            Log.Info("[ff] hit " + Coop.Player(victim.Id).Name + "'s limb " + limb + " (force " + hit.Force.ToString("F0") + ", cut " + hit.Cut.ToString("F0") + ")");
        }

        /// <summary>The game's own swing power (Body.Attack): arm, strength, off hand, claw wear.</summary>
        private static float Power(Body b, AttackInfo atk)
        {
            float p = 1f;
            try
            {
                int slot = b.handSlot;
                p = b.slots[slot].armPowerMult * (1f + BonusStrength(b) * 0.166f);
                if (slot == 1) p *= 0.75f;
                p *= 1f + b.skills.STRFrom10 * 0.0334f;
                if (atk.unarmed && !b.GetAttackingLimb().prostheticInfo.hasProsthetic) p *= b.clawDamageCurve.Evaluate(b.clawHealth);
            }
            catch (Exception e) { Log.Debug("[ff] power: " + e.Message); }
            return Mathf.Clamp(p, 0.1f, 3f);
        }

        // ------------------------------------------------------------------ the victim

        private static void OnHit(byte origin, PlayerHit m)
        {
            if (m.Victim != Coop.LocalId) return;
            if (!HostRules.Current.FriendlyFire) { HitsRefused++; return; }
            var body = GameInfo.LocalBody;
            if (body == null || !body.alive || body.limbs == null || m.Limb >= body.limbs.Length) return;
            var limb = body.limbs[m.Limb];
            if (limb == null || limb.dismembered) return;
            var dir = new Vector2(m.DX, m.DY);
            // The game's impact code also chips the block behind the limb; a blow shouldn't.
            bool saved = ExplosionSync.BodyOnly;
            ExplosionSync.BodyOnly = true;
            try
            {
                limb.ImpactDamage(Mathf.Clamp(m.Force, 0f, 250f), dir);
                if (m.Cut > 0f)
                {
                    float armor = limb.GetArmorReduction();
                    float cut = Mathf.Clamp(m.Cut, 0f, 80f);
                    limb.skinHealth -= cut / armor;
                    limb.bleedAmount += cut * 0.15f / armor;
                    limb.pain += cut * 0.4f / armor;
                    limb.DamageWearables(cut * 0.004f);
                    body.DoGoreSound();
                }
                if (limb.rb != null) limb.rb.velocity += dir * Mathf.Min(12f, m.Force * 0.12f);
                if (m.Heavy && body.standing) body.Ragdoll();
            }
            catch (Exception e) { Log.Warn("[ff] applying a hit failed: " + e.Message); }
            finally { ExplosionSync.BodyOnly = saved; }
            HitsTaken++;
            LastLimb = m.Limb;
            string who = Coop.Player(origin).Name;
            CoopRuntime.Notice(string.IsNullOrEmpty(m.ItemId) ? Lang.F("{0} hit you", who) : Lang.F("{0} hit you with the {1}", who, Medic.Pretty(m.ItemId)));
            Log.Info("[ff] hit by " + who + " on limb " + m.Limb + " (force " + m.Force.ToString("F0") + ", cut " + m.Cut.ToString("F0") + ")");
        }
    }
}
