using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using CCoop.Plugin.Protocol;
using CCoop.Plugin.Runtime;
using CCoop.Plugin.World;
using HarmonyLib;
using UnityEngine;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// Treating a teammate by playing the game's own minigame.
    ///
    /// The patient's body only exists on the patient's machine, so the treater's minigame
    /// runs on a stand-in: the treater's own limb of the same kind, briefly given the
    /// patient's dislocation and shrapnel so the minigame behaves as it would on them.
    /// Everything the minigame does is redirected:
    ///
    ///  * Bandage wraps and syringe pushes (the item's own callbacks) go to the patient,
    ///    whose machine runs the same callbacks with a copy of the item on the real limb.
    ///  * Anything the minigame writes to the stand-in limb (pain, a slipped needle,
    ///    shrapnel pulled, the joint popping back) is measured around the minigame's
    ///    update, sent as a delta, and undone on the stand-in.
    ///  * A defibrillator shock goes to the patient, and the defibrillator minigames read
    ///    the patient's heart (fibrillation, arrest) instead of the treater's.
    ///
    /// The patient reports what is left of the item, so a bandage runs out and a syringe
    /// empties on the treater's screen as the minigame is played. When the minigame ends,
    /// the stand-in limb is put back exactly as it was.
    /// </summary>
    internal static class MedicSession
    {
        /// <summary>Items whose use opens a minigame (learned when a patient reports another).</summary>
        internal static readonly HashSet<string> MinigameItems = new HashSet<string>
        {
            "bandage", "rippeddressing", "bruisekit", "analgesicgauze", "alginate", "rag", "sterilizedbandage", "plasticbandage", "musharm",
            "syringe", "morphine", "opium", "heroin", "naloxone", "fentanyl", "ceftriaxone", "saline", "ringer",
            "aed", "manualdefibrillator", "tweezers",
        };

        // ================================================================== treater

        private sealed class Session
        {
            public uint Id;
            public byte Patient;
            public int LimbIndex;
            public Limb StandIn;
            public LimbState Saved;
            public Item Item;
            public float Wrap, Inject;
            public float NextFlush;
            public bool Started;
        }

        private static Session _active;
        private static uint _nextId = 1;
        internal static bool Active => _active != null;

        /// <summary>The body whose heart readings the defibrillator minigames should see as the patient's.</summary>
        internal static Body StandInBody => _active != null && _active.StandIn != null ? _active.StandIn.body : null;
        internal static byte Patient => _active != null ? _active.Patient : (byte)0;

        internal static void Start(byte patient, int limbIndex, TreatKind kind, Item item)
        {
            var mg = MinigameBase.main;
            var body = GameInfo.LocalBody;
            if (mg == null || body == null || body.limbs == null || limbIndex < 0 || limbIndex >= body.limbs.Length) return;
            if (_active != null || mg.currentMinigame != null) { CoopRuntime.Notice("Finish what you're doing first."); return; }
            var v = Coop.Get<VitalsSync>()?.Of(patient);
            if (v == null || limbIndex >= v.Limbs.Count) return;

            var standIn = body.limbs[limbIndex];
            var s = new Session
            {
                Id = _nextId++,
                Patient = patient,
                LimbIndex = limbIndex,
                StandIn = standIn,
                Saved = LimbState.Capture(standIn),
                Item = item,
            };

            // Show the minigame what the patient's limb is like.
            var lv = v.Limbs[limbIndex];
            standIn.shrapnel = lv.Shrapnel;
            if ((lv.Signs & LimbSigns.Dislocated) != 0) { standIn.dislocated = true; standIn.dislocationTimer = 90f; }

            _active = s;
            Coop.Session.Broadcast(new TreatStep
            {
                Session = s.Id, Peer = patient, Op = TreatOp.Start, Limb = (byte)limbIndex, Kind = kind,
                Item = item != null ? ItemCodec.Encode(item) : new byte[0],
            });

            Redirect = true;
            try
            {
                switch (kind)
                {
                    case TreatKind.SetDislocation: mg.StartMinigame(new DislocationMinigame(standIn), null); break;
                    case TreatKind.PullShrapnel: mg.StartMinigame(new ShrapnelMinigame(standIn), null); break;
                    default: item.Stats.useLimbAction(standIn, item); break;
                }
            }
            catch (Exception e) { Log.Warn("[medic] minigame start failed: " + e.Message); }
            finally { Redirect = false; }

            s.Started = mg.currentMinigame != null;
            if (!s.Started) End("nothing to do there");
            else Log.Info("[medic] treating " + Coop.Player(patient).Name + "'s " + Medic.LimbName(standIn) + " (" + (item != null ? item.id : kind.ToString()) + ")");
        }

        /// <summary>Restores the stand-in and tells the patient it's over.</summary>
        internal static void End(string why = null)
        {
            var s = _active;
            if (s == null) return;
            _active = null;
            Flush(s, true);
            if (s.StandIn != null) s.Saved.Restore(s.StandIn);
            Coop.Session?.Broadcast(new TreatStep { Session = s.Id, Peer = s.Patient, Op = TreatOp.End, Limb = (byte)s.LimbIndex });
            if (why != null) CoopRuntime.Notice(why);
            Log.Info("[medic] treatment of " + Coop.Player(s.Patient).Name + " ended" + (why != null ? ": " + why : ""));
        }

        /// <summary>Every frame: send batched wraps/pushes, and notice the minigame closing.</summary>
        internal static void Tick()
        {
            var s = _active;
            if (s == null) return;
            if (!Coop.WorldLive || MinigameBase.main == null || MinigameBase.main.currentMinigame == null) { End(); return; }
            // A patient may roll a little while being worked on: only a real gap ends it.
            if (Medic.CantTreat(s.Patient, Medic.Reach + 1.5f) is string why)
            {
                MinigameBase.main.EndMinigame();
                End(why);
                return;
            }
            if (Time.unscaledTime >= s.NextFlush) Flush(s, false);
        }

        private static void Flush(Session s, bool force)
        {
            s.NextFlush = Time.unscaledTime + 0.15f;
            if (s.Wrap > 0f)
            {
                Coop.Session?.Broadcast(new TreatStep { Session = s.Id, Peer = s.Patient, Op = TreatOp.Wrap, A = s.Wrap });
                s.Wrap = 0f;
            }
            if (s.Inject > 0f)
            {
                Coop.Session?.Broadcast(new TreatStep { Session = s.Id, Peer = s.Patient, Op = TreatOp.Inject, A = s.Inject });
                s.Inject = 0f;
            }
        }

        /// <summary>Patient -> treater: what's left of the item, or a refusal.</summary>
        internal static void OnReply(byte origin, TreatStep m)
        {
            var s = _active;
            if (s == null || m.Session != s.Id || origin != s.Patient) return;
            if (m.Op == TreatOp.Refuse)
            {
                if (m.Text == "minigame") return;
                MinigameBase.main?.EndMinigame();
                End(Coop.Player(origin).Name + ": " + m.Text);
                return;
            }
            if (!string.IsNullOrEmpty(m.Text)) CoopRuntime.Notice(m.Text);
            if (s.Item == null) return;
            if (m.Consumed)
            {
                UnityEngine.Object.Destroy(s.Item.gameObject);  // the minigame ends: no item left
                return;
            }
            var spec = ItemCodec.Decode(m.Item);
            if (spec != null) Coop.AsRemote(() => ItemCodec.Apply(s.Item, spec));
        }

        // ---- redirection hooks

        internal static bool Redirect;

        internal static void WrapMinigame(Minigame m)
        {
            var s = _active;
            if (s == null) return;
            if (m is BandageMinigame b) b.OnUse = angle => s.Wrap += angle;
            else if (m is SyringeMinigame sy) sy.OnUse = mult => s.Inject += mult;
        }

        private static LimbState _before;

        /// <summary>Dev: acts on the stand-in limb inside the next minigame update, as a player's hand would.</summary>
        internal static Action<Limb> DevAct;

        internal static void BeforeMinigameUpdate()
        {
            if (_active?.StandIn == null) return;
            _before = LimbState.Capture(_active.StandIn);
            var act = DevAct;
            DevAct = null;
            act?.Invoke(_active.StandIn);
        }

        internal static Limb StandIn => _active?.StandIn;

        /// <summary>What the minigame did to the stand-in this frame goes to the patient, then is undone here.</summary>
        internal static void AfterMinigameUpdate()
        {
            var s = _active;
            if (s?.StandIn == null || _before == null) return;
            var now = LimbState.Capture(s.StandIn);
            var d = now.Minus(_before);
            bool joint = _before.Dislocated && !now.Dislocated;
            bool shrapnel = now.Shrapnel != _before.Shrapnel;
            if (d.Any || joint || shrapnel)
            {
                Coop.Session?.Broadcast(new TreatStep
                {
                    Session = s.Id, Peer = s.Patient, Op = TreatOp.Delta,
                    A = d.Pain, B = d.Skin, C = d.Bleed, D = d.Muscle, E = d.Brain,
                    Shrapnel = shrapnel ? now.Shrapnel : -1,
                    Joint = (byte)(joint ? 1 : 0),
                });
            }
            // Wounds stay with the patient: the stand-in keeps only the minigame's own
            // progress (shrapnel left, the joint), everything else goes back.
            _before.RestoreHealth(s.StandIn);
            _before = null;
        }

        /// <summary>Set while the patient's machine applies a shock it received (not redirected).</summary>
        internal static bool Bypass;

        /// <summary>A shock during a teammate's treatment goes to the teammate.</summary>
        internal static bool Shock(Item.DefibInfo info)
        {
            if (Bypass) return false;
            var s = _active;
            if (s == null || info.limb == null || info.limb.body != s.StandIn.body) return false;
            Coop.Session?.Broadcast(new TreatStep { Session = s.Id, Peer = s.Patient, Op = TreatOp.Shock, A = info.chance });
            Log.Info("[medic] shock sent to " + Coop.Player(s.Patient).Name + " (chance " + info.chance.ToString("F2") + ")");
            return true;
        }

        // ---- heart readings for the defibrillator minigames

        private static Vitals PatientVitals => _active != null ? Coop.Get<VitalsSync>()?.Of(_active.Patient) : null;

        internal static float Fibrillation(Body b)
        {
            var v = b != null && b == StandInBody ? PatientVitals : null;
            return v != null ? v.Fibrillation : b.fibrillationProgress;
        }

        internal static float HeartRate(Body b)
        {
            var v = b != null && b == StandInBody ? PatientVitals : null;
            return v != null ? v.Heart : b.heartRate;
        }

        internal static bool InArrest(Body b)
        {
            var v = b != null && b == StandInBody ? PatientVitals : null;
            return v != null ? (v.Signs & BodySigns.Arrest) != 0 : b.inCardiacArrest;
        }

        internal static bool Alive(Body b)
        {
            var v = b != null && b == StandInBody ? PatientVitals : null;
            return v != null ? (v.Signs & BodySigns.Alive) != 0 : b.alive;
        }
    }

    /// <summary>The limb fields a treatment minigame touches, to measure and undo it on a stand-in.</summary>
    internal sealed class LimbState
    {
        public float Pain, Skin, Bleed, Muscle, Brain, Happiness, DislocationTimer;
        public int Shrapnel;
        public bool Dislocated;

        internal struct Delta
        {
            public float Pain, Skin, Bleed, Muscle, Brain;
            public bool Any => Mathf.Abs(Pain) + Mathf.Abs(Skin) + Mathf.Abs(Bleed) + Mathf.Abs(Muscle) + Mathf.Abs(Brain) > 0.0001f;
        }

        internal static LimbState Capture(Limb l) => new LimbState
        {
            Pain = l.pain, Skin = l.skinHealth, Bleed = l.bleedAmount, Muscle = l.muscleHealth,
            Brain = l.body != null ? l.body.brainHealth : 0f, Happiness = l.body != null ? l.body.happiness : 0f,
            DislocationTimer = l.dislocationTimer, Shrapnel = l.shrapnel, Dislocated = l.dislocated,
        };

        internal Delta Minus(LimbState o) => new Delta
        {
            Pain = Pain - o.Pain, Skin = Skin - o.Skin, Bleed = Bleed - o.Bleed, Muscle = Muscle - o.Muscle, Brain = Brain - o.Brain,
        };

        /// <summary>Health back to this state; minigame progress (shrapnel, joint) left as is.</summary>
        internal void RestoreHealth(Limb l)
        {
            l.pain = Pain; l.skinHealth = Skin; l.bleedAmount = Bleed; l.muscleHealth = Muscle;
            if (l.body != null) { l.body.brainHealth = Brain; l.body.happiness = Happiness; }
        }

        internal void Restore(Limb l)
        {
            RestoreHealth(l);
            l.shrapnel = Shrapnel;
            l.dislocated = Dislocated;
            l.dislocationTimer = DislocationTimer;
        }
    }

    // ====================================================================== patches

    /// <summary>While a teammate's treatment starts, the minigame's item callbacks are redirected.</summary>
    [HarmonyPatch(typeof(MinigameBase), nameof(MinigameBase.StartMinigame))]
    internal static class MinigameRedirectPatch
    {
        [HarmonyPriority(Priority.Last)]
        private static void Prefix(Minigame minigame)
        {
            if (MedicSession.Redirect && minigame != null) MedicSession.WrapMinigame(minigame);
        }
    }

    [HarmonyPatch(typeof(MinigameBase), "ActiveMinigameUpdate")]
    internal static class MinigameUpdatePatch
    {
        private static void Prefix() { if (MedicSession.Active) MedicSession.BeforeMinigameUpdate(); }
        private static void Postfix() { if (MedicSession.Active) MedicSession.AfterMinigameUpdate(); }
    }

    [HarmonyPatch(typeof(Item), nameof(Item.Defibrillate))]
    internal static class DefibRedirectPatch
    {
        private static bool Prefix(Item.DefibInfo info) => !(MedicSession.Active && MedicSession.Shock(info));
    }

    /// <summary>
    /// The defibrillator minigames read the heart of the body they're attached to. During
    /// a teammate's treatment that's the treater's own body, so those reads are swapped
    /// for the teammate's streamed readings.
    /// </summary>
    [HarmonyPatch]
    internal static class DefibVitalsTranspiler
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var t in new[] { typeof(AEDMinigame), typeof(ManualDefibMinigame) })
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (!m.IsAbstract && m.GetMethodBody() != null) yield return m;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
        {
            var fib = AccessTools.Field(typeof(Body), nameof(Body.fibrillationProgress));
            var heart = AccessTools.Field(typeof(Body), nameof(Body.heartRate));
            var arrest = AccessTools.PropertyGetter(typeof(Body), nameof(Body.inCardiacArrest));
            var alive = AccessTools.PropertyGetter(typeof(Body), nameof(Body.alive));
            foreach (var ins in code)
            {
                if (ins.opcode == OpCodes.Ldfld && Equals(ins.operand, fib))
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(MedicSession), nameof(MedicSession.Fibrillation))).WithLabels(ins.labels);
                else if (ins.opcode == OpCodes.Ldfld && Equals(ins.operand, heart))
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(MedicSession), nameof(MedicSession.HeartRate))).WithLabels(ins.labels);
                else if ((ins.opcode == OpCodes.Call || ins.opcode == OpCodes.Callvirt) && Equals(ins.operand, arrest))
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(MedicSession), nameof(MedicSession.InArrest))).WithLabels(ins.labels);
                else if ((ins.opcode == OpCodes.Call || ins.opcode == OpCodes.Callvirt) && Equals(ins.operand, alive))
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(MedicSession), nameof(MedicSession.Alive))).WithLabels(ins.labels);
                else yield return ins;
            }
        }
    }
}
