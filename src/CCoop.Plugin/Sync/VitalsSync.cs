using System.Collections.Generic;
using CCoop.Net.Session;
using CCoop.Plugin.Protocol;
using CCoop.Plugin.Runtime;
using UnityEngine;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// Each player streams a compact picture of their own body (blood, heart, brain, and
    /// every limb's wounds and treatments) twice a second, so teammates can see who needs
    /// what in the health panel. Read-only: treatment goes through <see cref="Medic"/>.
    /// </summary>
    internal sealed class VitalsSync : CoopModule
    {
        private const float Interval = 0.5f;
        private float _next;
        private float _diedAt = -1f;
        private readonly Dictionary<byte, Vitals> _latest = new Dictionary<byte, Vitals>();
        private readonly Dictionary<byte, float> _at = new Dictionary<byte, float>();

        internal override void Attach(NetSession s) => s.On<Vitals>((o, m) =>
        {
            _latest[o] = m;
            _at[o] = Time.unscaledTime;
        });

        internal override void Detach()
        {
            _latest.Clear();
            _at.Clear();
            _diedAt = -1f;
        }

        internal override void OnPeerLeft(PeerInfo peer)
        {
            _latest.Remove(peer.Id);
            _at.Remove(peer.Id);
        }

        /// <summary>A teammate's latest vitals while fresh (and on our layer), or null.</summary>
        internal Vitals Of(byte id)
        {
            if (!_latest.TryGetValue(id, out var v) || Time.unscaledTime - _at[id] > 3f) return null;
            var rec = Coop.Player(id);
            return rec.HasPosition && rec.ReportedEpoch == Coop.Epoch ? v : null;
        }

        /// <summary>Seconds the local player has been dead, or -1.</summary>
        internal float LocalDeadFor => _diedAt < 0f ? -1f : Time.unscaledTime - _diedAt;

        internal override void OnWorldLive() => _diedAt = -1f;

        internal override void Tick()
        {
            var body = GameInfo.LocalBody;
            if (!Coop.WorldLive || body == null) return;
            if (body.alive) _diedAt = -1f;
            else if (_diedAt < 0f) _diedAt = Time.unscaledTime;

            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + Interval;
            Session.Broadcast(Read(body, LocalDeadFor));
        }

        private static byte B(float v) => (byte)Mathf.Clamp(Mathf.RoundToInt(v), 0, 255);

        internal static Vitals Read(Body b, float deadFor)
        {
            var v = new Vitals
            {
                Blood = B(b.bloodVolume),
                Oxygen = B(b.bloodOxygen),
                Pressure = B(b.bloodPressure),
                Heart = B(b.heartRate),
                Consciousness = B(b.consciousness),
                Brain = B(b.brainHealth),
                Temperature = B((b.temperature - 20f) * 10f),
                Pain = B(b.averagePain),
                Fibrillation = B(b.fibrillationProgress),
                Mood = (sbyte)Mathf.Clamp(Mathf.RoundToInt(b.totalHappiness), -100, 100),
                DeadFor = (ushort)Mathf.Clamp(Mathf.RoundToInt(deadFor < 0f ? 0f : deadFor), 0, 65535),
            };
            var s = BodySigns.None;
            if (b.alive) s |= BodySigns.Alive;
            if (b.conscious) s |= BodySigns.Conscious;
            if (b.inCardiacArrest) s |= BodySigns.Arrest;
            if (b.breathing) s |= BodySigns.Breathing;
            if (b.sleeping) s |= BodySigns.Sleeping;
            if (b.isDying) s |= BodySigns.Dying;
            if (b.totalBleedSpeed > 0.02f) s |= BodySigns.Bleeding;
            if (b.fibrillationProgress > 0f) s |= BodySigns.Fibrillating;
            if (b.hunger < 25f) s |= BodySigns.Hungry;
            if (b.thirst < 25f) s |= BodySigns.Thirsty;
            if (b.temperature < 35f) s |= BodySigns.Cold;
            if (b.temperature > 38.5f) s |= BodySigns.Fever;
            if (b.standing) s |= BodySigns.Standing;
            if (b.currentClimbable != null) s |= BodySigns.Climbing;
            v.Signs = s;

            if (b.limbs != null)
                foreach (var l in b.limbs)
                {
                    if (l == null) { v.Limbs.Add(new LimbVitals { Signs = LimbSigns.Missing }); continue; }
                    var ls = LimbSigns.None;
                    if (l.broken) ls |= LimbSigns.Broken;
                    if (l.dislocated) ls |= LimbSigns.Dislocated;
                    if (l.splinted) ls |= LimbSigns.Splinted;
                    if (l.GetComponent<TourniquetScript>() != null) ls |= LimbSigns.Tourniquet;
                    if (l.infected) ls |= LimbSigns.Infected;
                    if (l.dismembered) ls |= LimbSigns.Missing;
                    if (l.prostheticInfo.hasProsthetic) ls |= LimbSigns.Prosthetic;
                    if (l.shrapnel > 0) ls |= LimbSigns.Shrapnel;
                    if (l.blockedBleeding) ls |= LimbSigns.BleedBlocked;
                    if (l.bandageSlowAmount > 0f) ls |= LimbSigns.Bandaged;
                    v.Limbs.Add(new LimbVitals
                    {
                        Skin = B(l.skinHealth),
                        Muscle = B(Mathf.Min(l.muscleHealth, 255f)),
                        Bleed = B(l.bleedAmount * 20f),
                        Infection = B(l.infectionAmount),
                        Pain = B(l.pain),
                        Shrapnel = B(l.shrapnel),
                        Signs = ls,
                    });
                }
            return v;
        }
    }
}
