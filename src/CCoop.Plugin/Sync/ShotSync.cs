using System;
using System.Collections.Generic;
using CCoop.Net.Session;
using CCoop.Plugin.Protocol;
using CCoop.Plugin.Runtime;
using CCoop.Plugin.World;
using HarmonyLib;
using UnityEngine;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// Gunfire. Every shot in the game, from a player's gun, a guard, a wall or ceiling
    /// turret or a gun mine, goes through <c>TurretScript.Shoot</c>: one ray, then tracer,
    /// puffs, object damage, block damage and wounds. On the shooter's machine it runs as
    /// usual and its object and terrain damage reach everyone through the normal sync.
    /// Everyone else replays the same ray body-only, like an explosion: they see the
    /// tracer and hear the shot, creatures they simulate react, and if the ray passes
    /// through their own player, it wounds them with the game's own damage.
    ///
    /// A player's shot never hits a body in the game itself; with the host's
    /// friendly-fire rule it does hit teammates.
    /// </summary>
    internal sealed class ShotSync : CoopModule
    {
        internal static bool Replaying;
        internal int Sent, Replayed, Deduped;

        // What is firing right now, set by the callers of Shoot.
        internal static string ContextSound;
        internal static bool ContextActive, ContextFromPlayer, ContextHasAim;
        internal static Vector2 ContextAim;
        private static int _pellet;

        private struct LocalShot
        {
            public Vector2 Pos, Dir;
            public float Time;
        }

        private static readonly List<LocalShot> Recent = new List<LocalShot>();

        internal override void Attach(NetSession s) => WorldNet.Register<ShotFired>(s, OnShot);
        internal override void Detach() => Clear();
        internal override void OnWorldLost() => Clear();

        private void Clear()
        {
            Recent.Clear();
            Sent = Replayed = Deduped = 0;
        }

        /// <summary>A player's gun is named by its item id; its sound is the prefab's.</summary>
        internal const string GunSoundPrefix = "gun:";

        private static readonly Dictionary<string, AudioClip> GunSounds = new Dictionary<string, AudioClip>();

        private static AudioClip GunSound(string id)
        {
            if (GunSounds.TryGetValue(id, out var clip)) return clip;
            try
            {
                var prefab = Resources.Load<GameObject>(id);
                var gun = prefab != null ? prefab.GetComponent<GunScript>() : null;
                clip = gun != null ? gun.fireSound : null;
            }
            catch { clip = null; }
            GunSounds[id] = clip;
            return clip;
        }

        internal static void Begin(string sound, bool fromPlayer, bool hasAim = false, Vector2 aim = default)
        {
            ContextActive = true;
            ContextSound = sound;
            ContextFromPlayer = fromPlayer;
            ContextHasAim = hasAim;
            ContextAim = aim;
            _pellet = 0;
        }

        internal static void End() => ContextActive = false;

        /// <summary>A shot is being fired here.</summary>
        internal static void OnLocalShot(FireInfo info)
        {
            if (info == null || !Coop.WorldLive || Coop.Applying || Replaying || ExplosionSync.BodyOnly) return;
            var self = Coop.Get<ShotSync>();
            if (self == null) return;
            uint source = 0;
            if (info.ignoreTrans != null)
                for (var t = info.ignoreTrans; t != null && source == 0; t = t.parent)
                    EntityRegistry.TryGetId(t.gameObject, out source);
            bool active = ContextActive;
            bool first = !active || _pellet == 0;
            _pellet++;
            var m = new ShotFired
            {
                X = info.pos.x, Y = info.pos.y, DX = info.dir.x, DY = info.dir.y,
                IgnoreBody = info.ignoreBody, Tinnitus = info.doTinnitus, ForceHead = info.forceHead,
                Structure = info.structureDamage, Animal = info.animalDamage, PlayerMult = info.playerDamageMultiplier,
                Source = source,
                FromPlayer = active && ContextFromPlayer,
                First = first,
                // Turrets and gun mines all fire with the rifle sound.
                Sound = first ? (active ? ContextSound ?? "" : "rifleshot") : "",
                HasAim = active && ContextHasAim,
                AimX = ContextAim.x, AimY = ContextAim.y,
            };
            float now = Time.unscaledTime;
            Recent.RemoveAll(r => now - r.Time > 0.4f);
            if (Recent.Count < 64) Recent.Add(new LocalShot { Pos = info.pos, Dir = info.dir.normalized, Time = now });
            self.Sent++;
            WorldNet.Publish(m);
        }

        private void OnShot(byte origin, ShotFired m)
        {
            var pos = new Vector2(m.X, m.Y);
            var dir = new Vector2(m.DX, m.DY);
            if (dir.sqrMagnitude < 1e-6f) return;

            // Turrets run on every machine and fire at whoever they see there: if this
            // machine's copy just fired the same shot, don't replay it on top.
            float now = Time.unscaledTime;
            var nd = dir.normalized;
            foreach (var r in Recent)
                if (now - r.Time < 0.4f && Vector2.Distance(r.Pos, pos) < 0.6f && Vector2.Dot(r.Dir, nd) > 0.995f) { Deduped++; return; }

            var info = new FireInfo
            {
                pos = pos,
                dir = dir,
                ignoreBody = m.IgnoreBody,
                doTinnitus = m.Tinnitus,
                structureDamage = m.Structure,
                animalDamage = m.Animal,
                forceHead = m.ForceHead,
                playerDamageMultiplier = m.PlayerMult,
            };
            if (m.Source != 0 && EntityRegistry.TryGet(m.Source, out var src)) info.ignoreTrans = src.transform;

            if (m.FromPlayer)
            {
                // A shot at oneself (ignoreBody false) only ever hits the shooter; any
                // other player shot hits teammates only by the host's rule.
                info.ignoreBody = !m.IgnoreBody || !HostRules.Current.FriendlyFire;
            }
            else if (m.HasAim)
            {
                // A guard only hurts a body its reticle is on; here that's this machine's player.
                info.ignoreBody = !Physics2D.OverlapPoint(new Vector2(m.AimX, m.AimY), LayerMask.GetMask("Limb", "Body"));
            }

            if (m.First && m.FromPlayer) Coop.Get<PlayerSync>()?.Find(origin)?.FlashMuzzle(pos);

            if (m.First && !string.IsNullOrEmpty(m.Sound))
            {
                try
                {
                    if (m.Sound.StartsWith(GunSoundPrefix, StringComparison.Ordinal))
                    {
                        var clip = GunSound(m.Sound.Substring(GunSoundPrefix.Length));
                        if (clip != null) Sound.Play(clip, pos, twoDimensional: false, pitchShift: false);
                    }
                    else Sound.Play(m.Sound, pos, twoDimensional: false, pitchShift: false);
                }
                catch (Exception e) { Log.Debug("shot sound: " + e.Message); }
            }

            var body = GameInfo.LocalBody;
            float before = body != null ? body.limbs[1].skinHealth + body.limbs[0].skinHealth : 0f;
            Replaying = true;
            ExplosionSync.BodyOnly = true;
            try
            {
                TurretScript.Shoot(info);
                Replayed++;
            }
            catch (Exception e) { Log.Debug("shot replay: " + e.Message); }
            finally
            {
                ExplosionSync.BodyOnly = false;
                Replaying = false;
            }
            if (m.FromPlayer && !info.ignoreBody && body != null && body.limbs[1].skinHealth + body.limbs[0].skinHealth < before)
            {
                Log.Info("[shot] hit by " + Coop.Player(origin).Name + "'s shot");
                CoopRuntime.Notice(Coop.Player(origin).Name + " shot you");
            }
        }
    }

    // ====================================================================== hooks

    [HarmonyPatch(typeof(TurretScript), nameof(TurretScript.Shoot))]
    internal static class ShootPatch
    {
        private static void Prefix(FireInfo info)
        {
            try { ShotSync.OnLocalShot(info); }
            catch (Exception e) { Log.Debug("shot hook: " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(GunScript), nameof(GunScript.Fire))]
    internal static class GunFireContextPatch
    {
        private static void Prefix(GunScript __instance)
        {
            var item = __instance.GetComponent<Item>();
            ShotSync.Begin(item != null ? ShotSync.GunSoundPrefix + item.id : "", true);
        }
        private static void Finalizer() => ShotSync.End();
    }

    [HarmonyPatch(typeof(GuardScript), nameof(GuardScript.Fire))]
    internal static class GuardFireContextPatch
    {
        private static readonly AccessTools.FieldRef<GuardScript, Vector2> Reticle = AccessTools.FieldRefAccess<GuardScript, Vector2>("reticlePos");
        private static void Prefix(GuardScript __instance) => ShotSync.Begin("pistolshot", false, true, Reticle(__instance));
        private static void Finalizer() => ShotSync.End();
    }

    /// <summary>Gun mines: their sound and cooldown travel with the world event, so the shot is silent.</summary>
    [HarmonyPatch(typeof(GunmineScript), "OnWillRenderObject")]
    internal static class GunmineContextPatch
    {
        private static void Prefix() => ShotSync.Begin("", false);
        private static void Finalizer() => ShotSync.End();
    }

    // During any body-only replay (explosions and shots), nothing but this machine's
    // player is changed: block damage and damage numbers come from the author.

    [HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.DamageBlock), new[] { typeof(Vector2), typeof(float), typeof(bool), typeof(bool) })]
    internal static class BodyOnlyBlockPatch
    {
        private static bool Prefix() => !ExplosionSync.BodyOnly;
    }

    [HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.DamageBlock), new[] { typeof(Vector2Int), typeof(float), typeof(bool), typeof(bool), typeof(bool) })]
    internal static class BodyOnlyBlockIntPatch
    {
        private static bool Prefix() => !ExplosionSync.BodyOnly;
    }

    [HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.CreateDamageNumber))]
    internal static class BodyOnlyNumberPatch
    {
        private static bool Prefix() => !ExplosionSync.BodyOnly;
    }
}
