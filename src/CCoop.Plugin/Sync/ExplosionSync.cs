using System;
using System.Collections.Generic;
using CCoop.Net.Session;
using CCoop.Plugin.Protocol;
using CCoop.Plugin.Runtime;
using HarmonyLib;
using UnityEngine;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// Explosions with exactly one author.
    ///
    /// <c>CreateExplosion</c> is fully local: it hurts the local body, damages objects and
    /// items in range, and carves a random crater. Mines, turret self-destructs and the
    /// like fire it on every machine, so under amount-based damage sync a blast would
    /// count once per player, and every machine would carve a different crater.
    ///
    /// So the host is the author. Its explosion runs in full and its world effects reach
    /// clients through the usual terrain and damage sync. Every other machine replays the
    /// blast <em>body-only</em>: particles, sound, shake and wounds on its own player, but
    /// no object damage, item damage or crater. A client whose game wants to explode
    /// something asks the host instead; the host drops requests that duplicate a blast it
    /// has just made (a mine destroyed on every machine asks once from each).
    /// </summary>
    internal sealed class ExplosionSync : CoopModule
    {
        /// <summary>Set while replaying a host explosion for the local body only.</summary>
        internal static bool BodyOnly;

        private const float DedupeSeconds = 1.5f;
        private const float DedupeRadius = 2f;

        private struct Recent
        {
            public Vector2 Pos;
            public float Time;
        }

        private static readonly List<Recent> RecentBlasts = new List<Recent>();
        internal int Authored, Replayed, Deduped;

        internal override void Attach(NetSession s)
        {
            WorldNet.Register<ExplosionEvent>(s, (o, m) => Replay(m.Spec));
            WorldNet.Register<ExplosionRequest>(s, (o, m) => OnRequest(m.Spec), relay: false);
        }

        internal override void Detach() => Clear();
        internal override void OnWorldLost() => Clear();

        private void Clear()
        {
            RecentBlasts.Clear();
            Authored = Replayed = Deduped = 0;
        }

        /// <summary>Host: true if this blast duplicates one just made, otherwise records it.</summary>
        internal bool IsDuplicate(Vector2 pos)
        {
            float now = Time.unscaledTime;
            RecentBlasts.RemoveAll(r => now - r.Time > DedupeSeconds);
            foreach (var r in RecentBlasts)
                if (Vector2.Distance(r.Pos, pos) < DedupeRadius) { Deduped++; return true; }
            RecentBlasts.Add(new Recent { Pos = pos, Time = now });
            return false;
        }

        internal void HostAuthored(ExplosionParams p)
        {
            Authored++;
            WorldNet.Publish(new ExplosionEvent { Spec = ToSpec(p) });

            // The blast damaged loose items here; their condition is not otherwise synced,
            // so re-announce their state (a known id updates in place).
            foreach (var col in Physics2D.OverlapCircleAll(p.position, p.range))
            {
                var item = col != null ? col.GetComponent<Item>() : null;
                if (item == null || item.transform.parent != null) continue;
                if (!World.EntityRegistry.TryGetId(item.gameObject, out var id)) continue;
                WorldNet.Publish(new EntitySpawn
                {
                    Id = id, Key = World.EntityRegistry.KeyOf(id) ?? item.id, IsItem = true,
                    X = item.transform.position.x, Y = item.transform.position.y,
                    State = World.ItemCodec.Encode(item),
                });
            }
        }

        private void OnRequest(ExplosionSpec spec)
        {
            if (!Coop.IsHost) return;
            // Runs through the patch, which dedupes and publishes like a host-made blast.
            int saved = Coop.ApplyingRemote;
            Coop.ApplyingRemote = 0;
            try { WorldGeneration.CreateExplosion(FromSpec(spec)); }
            finally { Coop.ApplyingRemote = saved; }
        }

        private void Replay(ExplosionSpec spec)
        {
            if (Coop.IsHost) return;
            BodyOnly = true;
            try
            {
                WorldGeneration.CreateExplosion(FromSpec(spec));
                Replayed++;
            }
            catch (Exception e) { Log.Debug("explosion replay: " + e.Message); }
            finally { BodyOnly = false; }
        }

        internal static ExplosionSpec ToSpec(ExplosionParams p) => new ExplosionSpec
        {
            X = p.position.x, Y = p.position.y, Range = p.range, Velocity = p.velocity, StructuralDamage = p.structuralDamage,
            MuscleMin = p.muscleDamage.min, MuscleMax = p.muscleDamage.max,
            SkinMin = p.skinDamage.min, SkinMax = p.skinDamage.max,
            BleedMin = p.bleedAmount.min, BleedMax = p.bleedAmount.max,
            SkinChance = p.skinDamageChance, BoneChance = p.boneBreakChance, DislocationChance = p.dislocationChance,
            DisfigureChance = p.disfigureChance, BleedChance = p.bleedChance, ShrapnelChance = p.shrapnelChance,
            DismemberChance = p.dismemberChance, Sound = p.sound ?? "explosion",
        };

        internal static ExplosionParams FromSpec(ExplosionSpec s) => new ExplosionParams
        {
            position = new Vector2(s.X, s.Y), range = s.Range, velocity = s.Velocity, structuralDamage = s.StructuralDamage,
            muscleDamage = new RangeF { min = s.MuscleMin, max = s.MuscleMax },
            skinDamage = new RangeF { min = s.SkinMin, max = s.SkinMax },
            bleedAmount = new RangeF { min = s.BleedMin, max = s.BleedMax },
            skinDamageChance = s.SkinChance, boneBreakChance = s.BoneChance, dislocationChance = s.DislocationChance,
            disfigureChance = s.DisfigureChance, bleedChance = s.BleedChance, shrapnelChance = s.ShrapnelChance,
            dismemberChance = s.DismemberChance, sound = string.IsNullOrEmpty(s.Sound) ? "explosion" : s.Sound,
        };
    }

    [HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.CreateExplosion))]
    internal static class CreateExplosionPatch
    {
        private static bool Prefix(ExplosionParams param, out bool __state)
        {
            __state = false;
            if (!Coop.WorldLive || param == null || ExplosionSync.BodyOnly) return true;
            var sync = Coop.Get<ExplosionSync>();
            if (sync == null) return true;
            if (Coop.IsHost)
            {
                if (sync.IsDuplicate(param.position)) return false;
                __state = true;
                return true;
            }
            WorldNet.Publish(new ExplosionRequest { Spec = ExplosionSync.ToSpec(param) });
            return false;
        }

        private static void Postfix(ExplosionParams param, bool __state)
        {
            if (__state) Coop.Get<ExplosionSync>()?.HostAuthored(param);
        }
    }

    /// <summary>During a body-only replay, objects are not damaged: the host's damage arrives on its own.</summary>
    [HarmonyPatch(typeof(BuildingEntity), "set_health")]
    internal static class BodyOnlyHealthPatch
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix() => !ExplosionSync.BodyOnly;
    }

    [HarmonyPatch(typeof(Item), nameof(Item.SetCondition))]
    internal static class BodyOnlyItemPatch
    {
        private static bool Prefix() => !ExplosionSync.BodyOnly;
    }

    /// <summary>No crater from a replay: the host's crater arrives as terrain edits.</summary>
    [HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.GenerateBlockCircle))]
    internal static class BodyOnlyCraterPatch
    {
        private static bool Prefix() => !ExplosionSync.BodyOnly;
    }
}
