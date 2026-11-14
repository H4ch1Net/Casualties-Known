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
    /// Traps, props and story moments that the game sets off on one machine only.
    ///
    /// Almost every trigger in the game is a physics callback on the local player's own
    /// colliders, so a bear trap snaps, a spike stabs, a door opens or the Blue Heart rises
    /// only where that player is. The damage is already right (it lands on the victim's
    /// machine); what's missing is everyone else's copy. Each hook here notices the moment
    /// on the machine where it happened and sends it on; the others put their copy in the
    /// same state. The host keeps the first of each one-shot (two players stepping on one
    /// trap) and replays them for anyone who joins the layer later.
    /// </summary>
    internal sealed class WorldEvents : CoopModule
    {
        private readonly HashSet<string> _fired = new HashSet<string>();
        private readonly List<WorldEvent> _log = new List<WorldEvent>();
        internal int Sent, Applied;

        internal override void Attach(NetSession s) => WorldNet.Register<WorldEvent>(s, OnEvent, relay: false);
        internal override void Detach() => Clear();
        internal override void OnWorldLost() => Clear();

        private void Clear()
        {
            _fired.Clear();
            _log.Clear();
            Sent = Applied = 0;
        }

        private static bool OneShot(WorldEventKind k) =>
            k != WorldEventKind.CoilShock && k != WorldEventKind.GunmineFired && k != WorldEventKind.ScrapFed && k != WorldEventKind.GuardSpawned;

        /// <summary>Kept for late joiners: anything that leaves the world changed.</summary>
        private static bool Lasting(WorldEventKind k) =>
            k != WorldEventKind.CoilShock && k != WorldEventKind.GunmineFired && k != WorldEventKind.MinePressed;

        private static string KeyOf(WorldEvent m) =>
            m.Id != 0 ? (int)m.Kind + ":" + m.Id + ":" + m.Path
                      : (int)m.Kind + "@" + Mathf.RoundToInt(m.X * 4f) + "," + Mathf.RoundToInt(m.Y * 4f);

        /// <summary>Host: true the first time a one-shot is seen (and records it).</summary>
        private bool Accept(WorldEvent m)
        {
            if (OneShot(m.Kind) && !_fired.Add(KeyOf(m))) return false;
            if (Lasting(m.Kind) && _log.Count < 4096) _log.Add(m);
            return true;
        }

        // ------------------------------------------------------------------ sending

        /// <summary>Something happened to this object here; tell the others.</summary>
        internal static void Fire(WorldEventKind kind, Component c, int arg = 0, float f = 0f)
        {
            if (c == null || !Coop.WorldLive || Coop.Applying) return;
            var self = Coop.Get<WorldEvents>();
            if (self == null) return;
            var at = c.transform.position;
            var m = new WorldEvent { Kind = kind, X = at.x, Y = at.y, Arg = arg, F = f };
            Address(c, out m.Id, out m.Path);
            if (Coop.IsHost && !self.Accept(m)) return;
            self.Sent++;
            WorldNet.Publish(m);
            Log.Info("[world] " + kind + " at " + ((Vector2)at).ToString("F0"));
        }

        private void OnEvent(byte origin, WorldEvent m)
        {
            if (Coop.IsHost)
            {
                if (!Accept(m)) return;
                Apply(m);
                // The sender already has it; everyone else in the layer hears it from the host.
                foreach (var rec in Coop.Players.Values)
                    if (rec.Id != origin && rec.Id != NetSession.HostId) WorldNet.SendToWorldPeer(rec.Id, m);
                return;
            }
            Apply(m);
        }

        /// <summary>Host: a player is getting this layer's snapshot; follow it with what has happened since.</summary>
        internal void SendLogTo(byte peer)
        {
            foreach (var e in _log)
                WorldNet.SendToWorldPeer(peer, new WorldEvent
                {
                    Kind = e.Kind, Id = e.Id, Path = e.Path, X = e.X, Y = e.Y, Arg = e.Arg, F = e.F, Replay = true,
                });
            if (_log.Count > 0) Log.Info("[world] caught " + Coop.Player(peer).Name + " up on " + _log.Count + " world events");
        }

        // ------------------------------------------------------------------ addressing

        /// <summary>The nearest networked ancestor, and the child names from it down to <paramref name="c"/>.</summary>
        private static void Address(Component c, out uint id, out string path)
        {
            var names = new List<string>();
            for (var t = c.transform; t != null; t = t.parent)
            {
                if (EntityRegistry.TryGetId(t.gameObject, out id))
                {
                    names.Reverse();
                    path = string.Join("/", names.ToArray());
                    return;
                }
                names.Add(t.name);
            }
            id = 0;
            path = "";
        }

        private static T Resolve<T>(WorldEvent m) where T : Component
        {
            if (m.Id != 0 && EntityRegistry.TryGet(m.Id, out var go))
            {
                var t = string.IsNullOrEmpty(m.Path) ? go.transform : go.transform.Find(m.Path);
                var c = t != null ? t.GetComponent<T>() : null;
                if (c != null) return c;
                c = go.GetComponentInChildren<T>();
                if (c != null) return c;
            }
            // Not networked (or not found): structure replay is exact, so the same object
            // stands in the same place on every machine.
            T best = null;
            float bestDist = 1.5f;
            var at = new Vector2(m.X, m.Y);
            foreach (var c in UnityEngine.Object.FindObjectsOfType<T>())
            {
                float d = Vector2.Distance(at, c.transform.position);
                if (d < bestDist) { bestDist = d; best = c; }
            }
            return best;
        }

        // ------------------------------------------------------------------ applying

        private static readonly AccessTools.FieldRef<BearTrap, bool> TrapActivated = AccessTools.FieldRefAccess<BearTrap, bool>("activated");
        private static readonly AccessTools.FieldRef<SpikeStabberScript, bool> SpikeActivated = AccessTools.FieldRefAccess<SpikeStabberScript, bool>("activated");
        private static readonly AccessTools.FieldRef<CaveTickSpawner, bool> TicksStarted = AccessTools.FieldRefAccess<CaveTickSpawner, bool>("started");
        private static readonly AccessTools.FieldRef<StalactiteDropper, bool> Dropped = AccessTools.FieldRefAccess<StalactiteDropper, bool>("dropped");
        private static readonly AccessTools.FieldRef<StalactiteDropper, float> LastVel = AccessTools.FieldRefAccess<StalactiteDropper, float>("lastVel");
        private static readonly AccessTools.FieldRef<DrillPod, bool> PodWorking = AccessTools.FieldRefAccess<DrillPod, bool>("working");
        private static readonly AccessTools.FieldRef<ShuttleStartOpen, bool> ShuttleActivated = AccessTools.FieldRefAccess<ShuttleStartOpen, bool>("activated");
        private static readonly AccessTools.FieldRef<BlueHeart, bool> HeartStarted = AccessTools.FieldRefAccess<BlueHeart, bool>("cutsceneStarted");
        private static readonly AccessTools.FieldRef<GunmineScript, bool> GunmineReloaded = AccessTools.FieldRefAccess<GunmineScript, bool>("didReload");
        private static readonly AccessTools.FieldRef<MineScript, bool> MineExploded = AccessTools.FieldRefAccess<MineScript, bool>("exploded");
        private static readonly AccessTools.FieldRef<MedStationScript, bool> MedUsed = AccessTools.FieldRefAccess<MedStationScript, bool>("didHeal");

        private void Apply(WorldEvent m)
        {
            Applied++;
            bool quiet = m.Replay;
            switch (m.Kind)
            {
                case WorldEventKind.Backgroundify:
                {
                    var b = Resolve<BuildingEntity>(m);
                    if (b == null) break;
                    b.Backgroundify();
                    var med = b.GetComponent<MedStationScript>();
                    if (med != null) MedUsed(med) = true;  // one heal per station, as in single-player
                    break;
                }
                case WorldEventKind.BearTrap:
                {
                    var t = Resolve<BearTrap>(m);
                    if (t == null || TrapActivated(t)) break;
                    TrapActivated(t) = true;
                    var sr = t.GetComponent<SpriteRenderer>();
                    if (sr != null && t.closeSprite != null) sr.sprite = t.closeSprite;
                    if (t.transform.childCount > 0) UnityEngine.Object.Destroy(t.transform.GetChild(0).gameObject);
                    if (!quiet) Sound.Play("beartrap", t.transform.position, twoDimensional: false, pitchShift: false);
                    break;
                }
                case WorldEventKind.SpikeStab:
                {
                    var s = Resolve<SpikeStabberScript>(m);
                    if (s == null || SpikeActivated(s)) break;
                    // The stab's own animation event hurts whoever stands on it here, on
                    // their own machine, with the game's own damage.
                    if (!quiet) s.Stab();
                    else
                    {
                        SpikeActivated(s) = true;
                        var be = s.GetComponent<BuildingEntity>();
                        if (be != null) be.description = Locale.GetBuilding("spikestabberdscused");
                    }
                    break;
                }
                case WorldEventKind.CoilShock:
                {
                    var c = Resolve<CoilScript>(m);
                    if (c == null || c.cooldown > 0f) break;
                    c.cooldown = 30f;
                    if (c.light != null) c.light.intensity = 1f;
                    if (c.part != null) c.part.Stop();
                    Sound.Play("zap", c.transform.position);
                    break;
                }
                case WorldEventKind.CaveTicks:
                {
                    var sp = Resolve<CaveTickSpawner>(m);
                    if (sp == null || TicksStarted(sp)) break;
                    TicksStarted(sp) = true;  // the ticks themselves arrive as creatures
                    var ps = sp.GetComponent<ParticleSystem>();
                    if (ps != null) ps.Stop(false, ParticleSystemStopBehavior.StopEmitting);
                    UnityEngine.Object.Destroy(sp.gameObject, quiet ? 0f : 10f);
                    break;
                }
                case WorldEventKind.StalactiteDrop:
                {
                    var d = Resolve<StalactiteDropper>(m);
                    if (d == null || Dropped(d)) break;
                    LastVel(d) = m.F;
                    d.Drop();
                    break;
                }
                case WorldEventKind.PodRepaired:
                {
                    var p = Resolve<DrillPod>(m);
                    if (p == null || PodWorking(p)) break;
                    PodWorking(p) = true;
                    var use = p.GetComponent<UsableObject>();
                    if (use != null) UnityEngine.Object.Destroy(use);
                    var be = p.GetComponent<BuildingEntity>();
                    if (be != null) be.description = Locale.GetBuilding("drillpoddscfixed");
                    if (!quiet) Sound.Play("drillpodrepair", p.transform.position, twoDimensional: false, pitchShift: false);
                    break;
                }
                case WorldEventKind.ScrapFed:
                {
                    var e = Resolve<ScrapEaterScript>(m);
                    if (e == null) break;
                    bool open = e.scrapAmount >= ScrapEaterScript.target;
                    e.scrapAmount += m.F;  // contributions add up, in any order
                    if (!open && e.scrapAmount >= ScrapEaterScript.target) OpenScrapDoors(e, quiet);
                    break;
                }
                case WorldEventKind.GuardSpawned:
                {
                    var g = Resolve<GuardSpawner>(m);
                    if (g == null) break;
                    g.guardsToSpawn = Mathf.Max(0, g.guardsToSpawn - 1);
                    g.openProgress = quiet ? 5f : 0f;
                    if (!quiet) Sound.Play("endlabDoor", g.transform.position);
                    break;
                }
                case WorldEventKind.ShuttleOpen:
                {
                    var so = Resolve<ShuttleStartOpen>(m);
                    if (so == null || ShuttleActivated(so)) break;
                    ShuttleActivated(so) = true;
                    var audio = so.GetComponent<AudioSource>();
                    if (audio != null) audio.Stop();
                    if (!quiet) Sound.Play("shuttleNotice", so.transform.position, twoDimensional: false, pitchShift: false);
                    break;
                }
                case WorldEventKind.BlueHeart:
                    StartBlueHeart(Resolve<BlueHeart>(m) ?? BlueHeart.main, quiet);
                    break;
                case WorldEventKind.GunmineFired:
                {
                    var g = Resolve<GunmineScript>(m);
                    if (g == null) break;
                    g.cooldown = 6f;
                    GunmineReloaded(g) = false;
                    var ps = g.GetComponentInChildren<ParticleSystem>();
                    if (ps != null) ps.Play();  // the shot itself (sound, tracer) arrives as a ShotFired
                    break;
                }
                case WorldEventKind.MinePressed:
                {
                    var mine = Resolve<MineScript>(m);
                    if (mine == null || quiet) break;
                    // Shown, not armed: the presser's machine sets it off, and the host's
                    // blast reaches everyone else.
                    var sr = mine.GetComponent<SpriteRenderer>();
                    if (sr != null && mine.pressedSprite != null) sr.sprite = mine.pressedSprite;
                    Sound.Play("mine", mine.transform.position);
                    break;
                }
            }
        }

        private static void OpenScrapDoors(ScrapEaterScript e, bool quiet)
        {
            if (e.build != null) e.build.Backgroundify();
            foreach (var col in Physics2D.OverlapCircleAll(e.transform.position, 2f))
                if (col != null && col.TryGetComponent<BuildingEntity>(out var b) && b.id == "reinforceddoor") b.Backgroundify();
            if (!quiet) Sound.Play("beep", e.transform.position);
        }

        /// <summary>
        /// The Blue Heart rose for one player: it rises for all, and each player's own
        /// machine embeds their own cargo (every player needs it for the way out).
        /// </summary>
        private static void StartBlueHeart(BlueHeart heart, bool quiet)
        {
            if (heart == null || HeartStarted(heart)) return;
            HeartStarted(heart) = true;
            var body = GameInfo.LocalBody;
            bool endless = WorldGeneration.GetRunSettingBool("endless");
            if (quiet)
            {
                // A late joiner: the heart is long gone; take the cargo that came with it.
                UnityEngine.Object.Destroy(heart.gameObject);
                if (!endless && body != null && !body.hasCargo) body.EmbedCargo();
                return;
            }
            if (body != null) body.forceNoMovement = true;
            PlayerCamera.main?.SetTimeScale(PlayerCamera.SpeedType.Normal, switchSound: false, force: true);
            heart.StartCoroutine(heart.Cutscene());
            if (!endless && body != null) body.talker.Talk(Locale.GetCharacter("cargo2"));
            Sound.Play("blueHeartObtain", Vector2.zero, twoDimensional: true, pitchShift: false, null, 1f, 1f, noReverb: true, ignoreMixer: true);
            CoopRuntime.Notice("The Blue Heart is rising");
        }

        /// <summary>A peer's copy breaks because the host's did: it must not go off a second time.</summary>
        internal static void BeforeRemoteBreak(GameObject go)
        {
            var mine = go != null ? go.GetComponent<MineScript>() : null;
            if (mine != null) MineExploded(mine) = true;
        }

        internal override void Gui() { }
    }

    // ====================================================================== hooks

    [HarmonyPatch(typeof(BuildingEntity), nameof(BuildingEntity.Backgroundify))]
    internal static class BackgroundifyPatch
    {
        private static void Postfix(BuildingEntity __instance) => WorldEvents.Fire(WorldEventKind.Backgroundify, __instance);
    }

    [HarmonyPatch(typeof(BearTrap), "OnTriggerEnter2D")]
    internal static class BearTrapPatch
    {
        private static readonly AccessTools.FieldRef<BearTrap, bool> Activated = AccessTools.FieldRefAccess<BearTrap, bool>("activated");
        private static void Prefix(BearTrap __instance, out bool __state) => __state = Activated(__instance);
        private static void Postfix(BearTrap __instance, bool __state)
        {
            if (!__state && Activated(__instance)) WorldEvents.Fire(WorldEventKind.BearTrap, __instance);
        }
    }

    [HarmonyPatch(typeof(SpikeStabberScript), nameof(SpikeStabberScript.Stab))]
    internal static class SpikeStabPatch
    {
        private static void Postfix(SpikeStabberScript __instance) => WorldEvents.Fire(WorldEventKind.SpikeStab, __instance);
    }

    [HarmonyPatch(typeof(CoilScript), nameof(CoilScript.Shock))]
    internal static class CoilShockPatch
    {
        private static void Prefix(CoilScript __instance, out bool __state) => __state = __instance.cooldown <= 0f;
        private static void Postfix(CoilScript __instance, bool __state)
        {
            if (__state && __instance.cooldown > 0f) WorldEvents.Fire(WorldEventKind.CoilShock, __instance);
        }
    }

    [HarmonyPatch(typeof(CaveTickSpawner), "OnTriggerEnter2D")]
    internal static class CaveTickTriggerPatch
    {
        private static readonly AccessTools.FieldRef<CaveTickSpawner, bool> Started = AccessTools.FieldRefAccess<CaveTickSpawner, bool>("started");
        private static void Prefix(CaveTickSpawner __instance, out bool __state) => __state = Started(__instance);
        private static void Postfix(CaveTickSpawner __instance, bool __state)
        {
            if (!__state && Started(__instance)) WorldEvents.Fire(WorldEventKind.CaveTicks, __instance);
        }
    }

    [HarmonyPatch(typeof(StalactiteDropper), nameof(StalactiteDropper.Drop))]
    internal static class StalactiteDropPatch
    {
        private static readonly AccessTools.FieldRef<StalactiteDropper, float> LastVel = AccessTools.FieldRefAccess<StalactiteDropper, float>("lastVel");
        private static void Postfix(StalactiteDropper __instance) => WorldEvents.Fire(WorldEventKind.StalactiteDrop, __instance, 0, LastVel(__instance));
    }

    [HarmonyPatch(typeof(DrillPod), nameof(DrillPod.OnUse))]
    internal static class DrillPodRepairPatch
    {
        private static readonly AccessTools.FieldRef<DrillPod, bool> Working = AccessTools.FieldRefAccess<DrillPod, bool>("working");
        private static void Prefix(DrillPod __instance, out bool __state) => __state = Working(__instance);
        private static void Postfix(DrillPod __instance, bool __state)
        {
            if (!__state && Working(__instance)) WorldEvents.Fire(WorldEventKind.PodRepaired, __instance);
        }
    }

    [HarmonyPatch(typeof(ScrapEaterScript), nameof(ScrapEaterScript.OnUse))]
    internal static class ScrapEaterPatch
    {
        private static void Prefix(ScrapEaterScript __instance, out float __state) => __state = __instance.scrapAmount;
        private static void Postfix(ScrapEaterScript __instance, float __state)
        {
            float fed = __instance.scrapAmount - __state;
            if (fed > 0f) WorldEvents.Fire(WorldEventKind.ScrapFed, __instance, 0, fed);
        }
    }

    /// <summary>Host: a guard came out (clients never spawn them); everyone's door and count follow.</summary>
    [HarmonyPatch(typeof(GuardSpawner), nameof(GuardSpawner.CreateGuard))]
    internal static class GuardSpawnedPatch
    {
        private static void Postfix(GuardSpawner __instance)
        {
            if (Coop.IsHost) WorldEvents.Fire(WorldEventKind.GuardSpawned, __instance);
        }
    }

    /// <summary>
    /// Host: guard spawners measure the distance to the local player only, so a teammate
    /// walking past one alone would never meet its guard. Check every player.
    /// </summary>
    [HarmonyPatch(typeof(GuardSpawner), "Update")]
    internal static class GuardSpawnerEveryonePatch
    {
        private static void Postfix(GuardSpawner __instance)
        {
            var g = __instance;
            if (!Coop.IsHost || !Coop.WorldLive || g.guardsToSpawn <= 0 || g.openProgress < g.minTimeBetweenSpawn) return;
            Vector2 at = g.transform.position;
            foreach (var rec in Coop.Players.Values)
            {
                if (rec.Id == Coop.LocalId || !rec.HasPosition || !rec.InWorld || !rec.Alive) continue;
                float d = Vector2.Distance(at, rec.Position);
                if (d < g.minDistanceToSpawn || d > g.maxDistanceToSpawn) continue;
                g.CreateGuard();
                return;
            }
        }
    }

    [HarmonyPatch(typeof(ShuttleStartOpen), "OnTriggerEnter2D")]
    internal static class ShuttleOpenPatch
    {
        private static readonly AccessTools.FieldRef<ShuttleStartOpen, bool> Activated = AccessTools.FieldRefAccess<ShuttleStartOpen, bool>("activated");
        private static void Prefix(ShuttleStartOpen __instance, out bool __state) => __state = Activated(__instance);
        private static void Postfix(ShuttleStartOpen __instance, bool __state)
        {
            if (!__state && Activated(__instance)) WorldEvents.Fire(WorldEventKind.ShuttleOpen, __instance);
        }
    }

    [HarmonyPatch(typeof(BlueHeart), "Update")]
    internal static class BlueHeartPatch
    {
        private static readonly AccessTools.FieldRef<BlueHeart, bool> Started = AccessTools.FieldRefAccess<BlueHeart, bool>("cutsceneStarted");
        private static void Prefix(BlueHeart __instance, out bool __state) => __state = Started(__instance);
        private static void Postfix(BlueHeart __instance, bool __state)
        {
            if (!__state && __instance != null && Started(__instance)) WorldEvents.Fire(WorldEventKind.BlueHeart, __instance);
        }
    }

    [HarmonyPatch(typeof(GunmineScript), "OnWillRenderObject")]
    internal static class GunminePatch
    {
        private static void Prefix(GunmineScript __instance, out float __state) => __state = __instance.cooldown;
        private static void Postfix(GunmineScript __instance, float __state)
        {
            if (__state <= 0f && __instance.cooldown >= 5.9f) WorldEvents.Fire(WorldEventKind.GunmineFired, __instance);
        }
    }

    [HarmonyPatch(typeof(MineScript), "OnCollisionEnter2D")]
    internal static class MinePressPatch
    {
        private static readonly AccessTools.FieldRef<MineScript, bool> Pressed = AccessTools.FieldRefAccess<MineScript, bool>("pressed");
        private static void Prefix(MineScript __instance, out bool __state) => __state = Pressed(__instance);
        private static void Postfix(MineScript __instance, bool __state)
        {
            if (!__state && Pressed(__instance)) WorldEvents.Fire(WorldEventKind.MinePressed, __instance);
        }
    }

    /// <summary>
    /// The lifepod shower washes "the player", wherever they are. A teammate's shower
    /// (replayed everywhere) must only wash whoever is actually standing in the pod.
    /// </summary>
    [HarmonyPatch(typeof(LifepodShower), "Update")]
    internal static class ShowerRangePatch
    {
        private static bool Prefix(LifepodShower __instance)
        {
            if (!Coop.WorldLive) return true;
            var body = GameInfo.LocalBody;
            return body == null || Vector2.Distance(body.transform.position, __instance.transform.position) < 5f;
        }
    }
}
