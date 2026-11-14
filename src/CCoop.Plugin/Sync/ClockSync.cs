using System;
using CCoop.Net.Session;
using CCoop.Plugin.Protocol;
using CCoop.Plugin.Runtime;
using HarmonyLib;
using UnityEngine;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// One clock for everybody. <c>Time.timeScale</c> drives physics, AI and fluid
    /// together, so a peer at a different speed diverges in every system at once.
    ///
    /// Deliberate speeds (normal, fast, super fast) are the session's and the host
    /// arbitrates them. Pause and the death screen stay local (they pass force).
    ///
    /// Sleeping together: the game races time at 25x while you sleep or lie unconscious
    /// (3.5x while the brain is dying). One sleeper can't race everyone's world, so the
    /// host does it only once every living player is asleep or out cold, and stops the
    /// moment anyone wakes. The unconscious and the dead never steer the clock.
    ///
    /// Earthquakes: only the schedule travels. Each peer shakes, stumbles and loses
    /// blocks around its own player, which is exactly right when both are in the quake.
    /// </summary>
    internal sealed class ClockSync : CoopModule
    {
        private const float ClientQuakePin = 100000f;
        private float _lastQuake;
        internal static bool Applying;

        /// <summary>The whole team is asleep or out cold and time is racing.</summary>
        internal static bool SharedSleep;

        private const float SleepGrace = 2f;
        private float _allOutSince = -1f;

        internal override void Attach(NetSession s)
        {
            s.On<TimeScale>(OnTimeScale);
            WorldNet.Register<Earthquake>(s, OnQuake);
            WorldNet.Register<WorldClock>(s, (o, m) => OnClock(m), queueWhilePending: false);
        }

        internal override void Detach()
        {
            _lastQuake = 0f;
            HostAmbient = null;
            SharedSleep = false;
            _allOutSince = -1f;
        }

        internal override void OnWorldLost()
        {
            HostAmbient = null;
            _allOutSince = -1f;
            if (!SharedSleep) return;
            SharedSleep = false;
            if (Coop.IsHost)
            {
                Apply(PlayerCamera.SpeedType.Normal);
                Session.Broadcast(new TimeScale { Speed = (byte)PlayerCamera.SpeedType.Normal });
            }
        }

        // ---- layer clock: values each machine would otherwise compute differently ----

        private float _nextClock;

        /// <summary>Client: the host's ambient temperature, while fresh.</summary>
        internal static float? HostAmbient;
        internal static float HostAmbientAt;

        private static readonly AccessTools.FieldRef<RadiationLine, float> RadTime = AccessTools.FieldRefAccess<RadiationLine, float>("timeGone");

        private void SendClock(WorldGeneration w)
        {
            if (Time.unscaledTime < _nextClock) return;
            _nextClock = Time.unscaledTime + 1f;
            var line = RadiationLine.line;
            WorldNet.Publish(new WorldClock
            {
                // The temperature curve is sampled by time since *this machine* loaded the
                // scene, so every machine would feel a different temperature.
                AmbientTemperature = w.ambientTemperature,
                // The radiation line advances per machine, slower while the local player is
                // unconscious, so it drifts apart.
                RadiationActive = line != null && line.active,
                RadiationTime = line != null ? RadTime(line) : 0f,
            });
        }

        private static void OnClock(WorldClock m)
        {
            if (Coop.IsHost) return;
            HostAmbient = m.AmbientTemperature;
            HostAmbientAt = Time.unscaledTime;
            var line = RadiationLine.line;
            if (line == null) return;
            if (m.RadiationActive)
            {
                if (!line.active) line.Activate();
                RadTime(line) = m.RadiationTime;
            }
            else if (line.active) line.Deactivate();
        }

        internal static bool Shareable(PlayerCamera.SpeedType s) =>
            s == PlayerCamera.SpeedType.Normal || s == PlayerCamera.SpeedType.Fast || s == PlayerCamera.SpeedType.SuperFast;

        /// <summary>Returns whether the local SetTimeScale may proceed.</summary>
        internal bool OnLocalChange(PlayerCamera cam, PlayerCamera.SpeedType speed, bool force)
        {
            if (Applying || force) return true;
            // The unconscious and the dead don't steer the shared clock (and their screen
            // asks for normal speed every frame, which would cancel everyone's fast-forward).
            if (cam.body != null && !cam.body.conscious) return false;
            if (SharedSleep) return false;
            if (!Shareable(speed)) return false;
            if (cam.curTimeScale == speed) return true;
            if (Coop.IsHost)
            {
                if (speed != PlayerCamera.SpeedType.Normal && !CoopPlugin.Instance.AllowSpeedUp.Value) return false;
                Session.Broadcast(new TimeScale { Speed = (byte)speed });
                return true;
            }
            Session.SendToHost(new TimeScale { Speed = (byte)speed });
            return false;
        }

        private void OnTimeScale(byte origin, TimeScale m)
        {
            var speed = (PlayerCamera.SpeedType)m.Speed;
            if (!Coop.IsHost && origin == NetSession.HostId && IsSleepSpeed(speed))
            {
                if (!SharedSleep) Log.Info("[clock] the host is racing time while everyone sleeps (" + speed + ")");
                SharedSleep = true;
                Apply(speed);
                return;
            }
            if (!Shareable(speed)) return;
            if (Coop.IsHost)
            {
                if (SharedSleep) return;  // ends by itself as soon as someone is awake
                if (speed != PlayerCamera.SpeedType.Normal && !CoopPlugin.Instance.AllowSpeedUp.Value)
                {
                    Session.SendTo(origin, new TimeScale { Speed = (byte)PlayerCamera.SpeedType.Normal });
                    return;
                }
                Apply(speed);
                Session.Broadcast(new TimeScale { Speed = m.Speed });
                return;
            }
            SharedSleep = false;
            Apply(speed);
        }

        private static bool IsSleepSpeed(PlayerCamera.SpeedType s) =>
            s == PlayerCamera.SpeedType.UnconsciousFast || s == PlayerCamera.SpeedType.DyingFast;

        /// <summary>Host: race time only while every living player is asleep or out cold.</summary>
        private void HostSleepRule()
        {
            bool anyLiving = false, allOut = true, anyDying = false;
            var body = GameInfo.LocalBody;
            if (body != null && body.alive)
            {
                anyLiving = true;
                allOut &= !body.conscious;
                anyDying |= body.brainDying;
            }
            foreach (var rec in Coop.Players.Values)
            {
                if (rec.Id == NetSession.HostId || !rec.InWorld || !rec.HasPosition || !rec.Alive) continue;
                anyLiving = true;
                allOut &= !rec.Conscious;
                anyDying |= rec.Dying;
            }
            bool asleep = anyLiving && allOut;
            var want = anyDying ? PlayerCamera.SpeedType.DyingFast : PlayerCamera.SpeedType.UnconsciousFast;
            if (!asleep)
            {
                _allOutSince = -1f;
                if (!SharedSleep) return;
                SharedSleep = false;
                Apply(PlayerCamera.SpeedType.Normal);
                Session.Broadcast(new TimeScale { Speed = (byte)PlayerCamera.SpeedType.Normal });
                Log.Info("[clock] someone woke up; normal speed");
                return;
            }
            if (_allOutSince < 0f) _allOutSince = Time.unscaledTime;
            if (Time.unscaledTime - _allOutSince < SleepGrace) return;
            var cam = PlayerCamera.main;
            if (SharedSleep && cam != null && cam.curTimeScale == want) return;
            SharedSleep = true;
            Apply(want);
            Session.Broadcast(new TimeScale { Speed = (byte)want });
            Log.Info("[clock] everyone is asleep or out cold; " + (anyDying ? "dying speed (3.5x)" : "sleep speed (25x)"));
        }

        internal override void Gui()
        {
            if (!Coop.WorldLive || SharedSleep) return;
            var body = GameInfo.LocalBody;
            if (body == null || !body.alive || body.conscious) return;
            bool othersAwake = false;
            foreach (var rec in Coop.Players.Values)
                if (rec.Id != Coop.LocalId && rec.HasPosition && rec.Alive && rec.Conscious) othersAwake = true;
            if (!othersAwake) return;
            UI.Overlay.Bottom(body.sleeping ? "Sleeping" : "Out cold",
                "Time speeds up only once every living player is asleep or out cold.");
        }

        private static void Apply(PlayerCamera.SpeedType speed)
        {
            var cam = PlayerCamera.main;
            if (cam == null) return;
            Applying = true;
            try { cam.SetTimeScale(speed); }
            finally { Applying = false; }
        }

        internal override void Tick()
        {
            var w = WorldGeneration.world;
            if (w == null || !Coop.WorldLive) return;
            if (!Coop.IsHost)
            {
                w.earthquakeDelay = ClientQuakePin;
                return;
            }
            SendClock(w);
            HostSleepRule();
            float remaining = w.earthquakeTime;
            if (remaining > _lastQuake + 0.01f && remaining > 0.5f)
            {
                WorldNet.Publish(new Earthquake { Duration = remaining });
                Log.Info("[clock] earthquake for " + remaining.ToString("F0") + " s");
                // The game drops timeScale to 1 by assigning the field directly, around
                // SetTimeScale; put the shared clock back in step.
                SharedSleep = false;
                _allOutSince = -1f;
                Applying = true;
                try { PlayerCamera.main?.SetTimeScale(PlayerCamera.SpeedType.Normal, switchSound: false); }
                finally { Applying = false; }
                Session.Broadcast(new TimeScale { Speed = (byte)PlayerCamera.SpeedType.Normal });
            }
            _lastQuake = remaining;
        }

        private static void OnQuake(byte origin, Earthquake m)
        {
            var w = WorldGeneration.world;
            if (w == null || Coop.IsHost) return;
            if (w.allowEarthquakes) w.earthquakeTime = m.Duration;
        }
    }

    [HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.SetTimeScale))]
    internal static class SetTimeScalePatch
    {
        private static bool Prefix(PlayerCamera __instance, PlayerCamera.SpeedType speed, bool force)
        {
            if (!Coop.InSession || !GameInfo.InRunScene) return true;
            try
            {
                var sync = Coop.Get<ClockSync>();
                return sync == null || sync.OnLocalChange(__instance, speed, force);
            }
            catch (Exception e)
            {
                Log.Debug("timescale hook: " + e.Message);
                return true;
            }
        }
    }

    /// <summary>Client: the host's ambient temperature replaces the locally sampled one.</summary>
    [HarmonyPatch(typeof(WorldGeneration), "Update")]
    internal static class AmbientTemperaturePatch
    {
        private static void Postfix(WorldGeneration __instance)
        {
            if (!Coop.IsClient || !Coop.WorldLive || !ClockSync.HostAmbient.HasValue) return;
            if (Time.unscaledTime - ClockSync.HostAmbientAt > 5f) return;
            __instance.ambientTemperature = ClockSync.HostAmbient.Value;
        }
    }
}
