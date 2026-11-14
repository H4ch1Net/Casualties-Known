using System;
using System.Collections;
using System.Collections.Generic;
using CCoop.Net.Session;
using CCoop.Plugin.Protocol;
using CCoop.Plugin.Runtime;
using CCoop.Plugin.World;
using HarmonyLib;
using UnityEngine;
using CCoop.Plugin.UI;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// Treating a teammate. The patient's body only exists on the patient's machine, so
    /// the treater sends the request with the full state of the item in their hand, and
    /// the patient's machine does the treatment for real: it makes a temporary copy of the
    /// item and runs the game's own <c>useLimbAction</c> on its real limb. Items that
    /// would open a minigame (bandages, syringes, tweezers) have the minigame intercepted
    /// and its result applied directly — a teammate's hands don't need the mini-game. What
    /// is left of the item (used up, a used syringe, a half-spent bandage) goes back to
    /// the treater.
    ///
    /// CPR keeps a stopped heart's circulation going (the base game has no CPR); a
    /// defibrillator can bring back a teammate who died less than <see cref="ReviveWindow"/>
    /// seconds ago.
    /// </summary>
    internal sealed class Medic : CoopModule
    {
        internal const float Reach = 3f;
        internal const float ReviveWindow = 30f;
        private const float ReviveChance = 0.4f;

        private uint _nextRequest = 1;
        private readonly Dictionary<uint, Item> _pending = new Dictionary<uint, Item>();
        private float _pendingSince;
        private int _pendingLimb;
        private float _lastCprNotice;

        internal bool Busy => _pending.Count > 0 && Time.unscaledTime - _pendingSince < 5f;

        internal override void Attach(NetSession s)
        {
            s.On<TreatRequest>(OnRequest);
            s.On<TreatResult>(OnResult);
            s.On<CprPulse>(OnCpr);
            s.On<TreatStep>(OnStep);
        }

        internal override void Detach()
        {
            _pending.Clear();
            MedicSession.End();
            foreach (var p in _patientSessions.Values) p.Dispose();
            _patientSessions.Clear();
        }

        internal override void OnWorldLost()
        {
            MedicSession.End();
            foreach (var p in _patientSessions.Values) p.Dispose();
            _patientSessions.Clear();
        }

        internal override void Tick() => MedicSession.Tick();

        // CPR has to hold the pressure up every frame: in arrest the game drags it back
        // toward zero each frame (like its own auto-pump, which keeps pumping).
        private float _cprUntil;

        internal override void LateTick()
        {
            var body = GameInfo.LocalBody;
            if (Time.unscaledTime >= _cprUntil || body == null || !body.alive) return;
            body.bloodPressure = Mathf.Max(body.bloodPressure, 42f);
            body.bloodOxygen = Mathf.Min(92f, body.bloodOxygen + Time.deltaTime * 4f);
        }

        // ================================================================== treater

        internal static Item HeldItem(Body body)
        {
            if (body == null) return null;
            try { return body.GetItem(body.handSlot); }
            catch { return null; }
        }

        internal static bool IsDefibrillator(Item item) => item != null && (item.id == "aed" || item.id == "manualdefibrillator");

        /// <summary>Why the local player can't treat <paramref name="patient"/> right now, or null.</summary>
        internal static string CantTreat(byte patient) => CantTreat(patient, Reach);

        internal static string CantTreat(byte patient, float reach)
        {
            var body = GameInfo.LocalBody;
            if (body == null || !Coop.WorldLive) return Lang.T("You need to be in the layer.");
            if (!body.alive) return Lang.T("You're dead.");
            if (!body.conscious) return Lang.T("You're not conscious.");
            var r = Coop.Get<PlayerSync>()?.Find(patient);
            var at = r?.ShownAt;
            if (!at.HasValue) return Lang.T("They're not here.");
            float d = Vector2.Distance(body.transform.position, at.Value);
            if (d > reach) return Lang.F("Get closer ({0} m away).", d.ToString("F0"));
            return null;
        }

        /// <summary>Why the held item can't be used on a limb, or null.</summary>
        internal static string CantUse(Item item)
        {
            if (item == null) return Lang.T("Hold a medical item in your hand.");
            var body = GameInfo.LocalBody;
            if (IsDefibrillator(item)) return null;
            if (!item.Stats.ActuallyUsableOnLimb(item)) return Lang.T("That isn't used on a limb.");
            if (body != null && !body.aboveMedicalCutoff && !item.Stats.ignoreDepression) return Lang.T("You can't bring yourself to do it right now.");
            return null;
        }

        internal void Treat(byte patient, int limb, TreatKind kind)
        {
            if (Busy || MedicSession.Active) return;
            string why = CantTreat(patient);
            Item item = null;
            if (why == null && kind == TreatKind.Item)
            {
                item = HeldItem(GameInfo.LocalBody);
                why = CantUse(item);
            }
            if (why != null) { CoopRuntime.Notice(why); return; }

            // The game plays a minigame for these: so does treating a teammate.
            if (kind == TreatKind.SetDislocation || kind == TreatKind.PullShrapnel ||
                (kind == TreatKind.Item && MedicSession.MinigameItems.Contains(item.id)))
            {
                MedicSession.Start(patient, limb, kind, item);
                return;
            }

            uint req = _nextRequest++;
            _pending.Clear();
            _pending[req] = item;
            _pendingLimb = limb;
            _pendingSince = Time.unscaledTime;
            Session.Broadcast(new TreatRequest
            {
                Request = req,
                Patient = patient,
                Limb = (byte)Mathf.Clamp(limb, 0, 255),
                Kind = kind,
                Item = item != null ? ItemCodec.Encode(item) : new byte[0],
            });
            Log.Info("[medic] asking " + Coop.Player(patient).Name + " to take " + (item != null ? item.id : kind.ToString()) + " on limb " + limb);
        }

        private void OnResult(byte origin, TreatResult m)
        {
            if (m.Treater != Coop.LocalId || !_pending.TryGetValue(m.Request, out var item)) return;
            _pending.Remove(m.Request);
            string who = Coop.Player(origin).Name;
            if (!m.Ok)
            {
                if (m.Message == "minigame" && item != null)
                {
                    // An item we didn't know opens a minigame: remember it and play it.
                    MedicSession.MinigameItems.Add(item.id);
                    Log.Info("[medic] " + item.id + " opens a minigame; playing it");
                    MedicSession.Start(origin, _pendingLimb, TreatKind.Item, item);
                    return;
                }
                CoopRuntime.Notice(Lang.F("{0}: {1}", who, Lang.Unpack(m.Message)));
                return;
            }
            if (item != null)
            {
                if (m.Consumed) UnityEngine.Object.Destroy(item.gameObject);
                else
                {
                    var spec = ItemCodec.Decode(m.Item);
                    if (spec != null) Coop.AsRemote(() => ItemCodec.Apply(item, spec));
                }
            }
            CoopRuntime.Notice(string.IsNullOrEmpty(m.Message) ? Lang.F("Treated {0}", who) : Lang.Unpack(m.Message));
            Log.Info("[medic] " + who + " was treated" + (m.Consumed ? " (item used up)" : "") + ": " + m.Message);
        }

        /// <summary>Hold-to-CPR: called every half second while the button is held.</summary>
        internal void Cpr(byte patient)
        {
            string why = CantTreat(patient);
            if (why != null) { if (Time.unscaledTime - _lastCprNotice > 2f) { _lastCprNotice = Time.unscaledTime; CoopRuntime.Notice(why); } return; }
            Session.Broadcast(new CprPulse { Patient = patient });
        }

        // ================================================================== patient

        private void OnCpr(byte origin, CprPulse m)
        {
            var body = GameInfo.LocalBody;
            if (m.Patient != Coop.LocalId || body == null || !body.alive || !Coop.WorldLive) return;
            if (!body.inCardiacArrest && body.bloodPressure >= 30f) return;
            // Compressions push blood, and rescue breaths oxygenate it: enough to keep the
            // brain from dying, not enough to restart the heart. Held for the next 0.8 s.
            _cprUntil = Time.unscaledTime + 0.8f;
            body.bloodPressure = Mathf.Max(body.bloodPressure, 42f);
            if (Time.unscaledTime - _lastCprNotice > 4f)
            {
                _lastCprNotice = Time.unscaledTime;
                CoopRuntime.Notice(Lang.F("{0} is giving you CPR", Coop.Player(origin).Name));
                Log.Info("[medic] CPR from " + Coop.Player(origin).Name + ": pressure " + body.bloodPressure.ToString("F0") + ", oxygen " + body.bloodOxygen.ToString("F0"));
            }
        }

        // ================================================================== patient: minigame sessions

        private sealed class PatientSession : IDisposable
        {
            public uint Id;
            public byte Treater;
            public Limb Limb;
            public Item Item;
            public BandageMinigame Bandage;
            public SyringeMinigame Syringe;
            public float NextReport;

            public void Dispose()
            {
                if (Item != null) UnityEngine.Object.Destroy(Item.gameObject);
                Item = null;
            }
        }

        private readonly Dictionary<long, PatientSession> _patientSessions = new Dictionary<long, PatientSession>();

        private static long Key(byte treater, uint id) => ((long)treater << 32) | id;

        private void OnStep(byte origin, TreatStep m)
        {
            if (m.Op >= TreatOp.Progress)
            {
                if (m.Peer == Coop.LocalId) MedicSession.OnReply(origin, m);
                return;
            }
            if (m.Peer != Coop.LocalId) return;
            var body = GameInfo.LocalBody;
            long key = Key(origin, m.Session);
            _patientSessions.TryGetValue(key, out var ps);
            string who = Coop.Player(origin).Name;
            try
            {
                switch (m.Op)
                {
                    case TreatOp.Start:
                        ps = StartPatientSession(origin, m, body, out var refuse);
                        if (refuse != null) { Reply(origin, m.Session, TreatOp.Refuse, refuse, null); return; }
                        _patientSessions[key] = ps;
                        CoopRuntime.Notice(Lang.F("{0} is treating your {1}", who, LimbName(ps.Limb)));
                        Log.Info("[medic] " + who + " started treating limb " + m.Limb + " (" + m.Kind + ")");
                        break;
                    case TreatOp.Wrap:
                        if (ps?.Bandage == null) return;
                        for (float left = m.A; left > 0.0001f && ps.Item != null && ps.Item.condition > 0f; left -= 1f / 18f)
                            ps.Bandage.OnUse(Mathf.Min(1f / 18f, left));
                        Report(ps, false);
                        break;
                    case TreatOp.Inject:
                        if (ps?.Syringe == null || ps.Item == null) return;
                        ps.Syringe.OnUse(m.A);
                        Report(ps, false);
                        break;
                    case TreatOp.Delta:
                        if (ps?.Limb == null) return;
                        ps.Limb.pain += m.A;
                        ps.Limb.skinHealth += m.B;
                        ps.Limb.bleedAmount += m.C;
                        ps.Limb.muscleHealth += m.D;
                        if (body != null) body.brainHealth += m.E;
                        if (m.Shrapnel >= 0) ps.Limb.shrapnel = m.Shrapnel;
                        if (m.Joint == 1)
                        {
                            ps.Limb.dislocationTimer = 0f;
                            ps.Limb.UnDislocate();
                            CoopRuntime.Notice(Lang.F("{0} set your {1} back in place", who, LimbName(ps.Limb)));
                        }
                        break;
                    case TreatOp.Shock:
                        if (body == null) return;
                        var result = new TreatResult();
                        string why = ShockFrom(body, m.A, who, result);
                        Reply(origin, m.Session, TreatOp.Progress, why ?? result.Message, ps);
                        break;
                    case TreatOp.End:
                        if (ps == null) return;
                        Report(ps, true);
                        ps.Dispose();
                        _patientSessions.Remove(key);
                        Log.Info("[medic] " + who + " finished treating limb " + m.Limb);
                        break;
                }
            }
            catch (Exception e) { Log.Warn("[medic] treatment step " + m.Op + " failed: " + e.Message); }
        }

        private PatientSession StartPatientSession(byte treater, TreatStep m, Body body, out string refuse)
        {
            refuse = null;
            var ps = new PatientSession { Id = m.Session, Treater = treater };
            if (body == null || body.limbs == null || m.Limb >= body.limbs.Length) { refuse = Lang.N("not in the layer"); return ps; }
            ps.Limb = body.limbs[m.Limb];
            if (ps.Limb == null || ps.Limb.dismembered) { refuse = Lang.N("that limb is gone"); return ps; }
            switch (m.Kind)
            {
                case TreatKind.SetDislocation:
                    if (!ps.Limb.dislocated) refuse = Lang.N("that joint isn't dislocated");
                    else if (body.averagePain > 75f) refuse = Lang.N("too much pain to set it");
                    return ps;
                case TreatKind.PullShrapnel:
                    if (ps.Limb.shrapnel <= 0) refuse = Lang.N("there's nothing to pull out");
                    return ps;
            }
            ps.Item = MakeTemporary(m.Item, body.transform.position);
            if (ps.Item == null) { refuse = Lang.N("couldn't make sense of the item"); return ps; }
            if (IsDefibrillator(ps.Item)) return ps;  // shocks arrive on their own
            if (!body.alive) { refuse = Lang.N("they're dead"); return ps; }

            Minigame mg;
            MinigameCapture.Capturing = true;
            try { ps.Item.Stats.useLimbAction(ps.Limb, ps.Item); }
            finally { mg = MinigameCapture.End(); }
            ps.Bandage = mg as BandageMinigame;
            ps.Syringe = mg as SyringeMinigame;
            if ((ps.Bandage != null || ps.Syringe != null) && ps.Limb.prostheticInfo.hasProsthetic)
                refuse = Lang.N("that won't work on a prosthetic");
            return ps;
        }

        private void Report(PatientSession ps, bool force)
        {
            if (ps.Item == null && !force) return;
            if (!force && Time.unscaledTime < ps.NextReport) return;
            ps.NextReport = Time.unscaledTime + 0.2f;
            Reply(ps.Treater, ps.Id, TreatOp.Progress, null, ps);
        }

        private void Reply(byte treater, uint session, TreatOp op, string text, PatientSession ps)
        {
            var step = new TreatStep { Session = session, Peer = treater, Op = op, Text = text ?? "" };
            if (ps != null && op == TreatOp.Progress)
            {
                bool gone = ps.Item == null || (ps.Item.condition <= 0f && ps.Item.Stats.destroyAtZeroCondition);
                step.Consumed = gone;
                if (!gone) step.Item = ItemCodec.Encode(ps.Item);
            }
            Session.Broadcast(step);
        }

        private void OnRequest(byte origin, TreatRequest m)
        {
            if (m.Patient != Coop.LocalId) return;
            Coop.Runtime.StartCoroutine(Apply(origin, m));
        }

        private IEnumerator Apply(byte treater, TreatRequest m)
        {
            var result = new TreatResult { Request = m.Request, Treater = treater };
            var body = GameInfo.LocalBody;
            Limb limb = body != null && body.limbs != null && m.Limb < body.limbs.Length ? body.limbs[m.Limb] : null;
            string who = Coop.Player(treater).Name;
            Item tmp = null;

            string fail = null;
            if (body == null || !Coop.WorldLive) fail = Lang.N("not in the layer");
            else if (limb == null) fail = Lang.N("no such limb");
            else if (limb.dismembered) fail = Lang.N("that limb is gone");

            if (fail == null)
            {
                try
                {
                    switch (m.Kind)
                    {
                        case TreatKind.SetDislocation:
                            if (!limb.dislocated) fail = Lang.N("that joint isn't dislocated");
                            else if (body.averagePain > 75f) fail = Lang.N("too much pain to set it");
                            else
                            {
                                limb.dislocationTimer = 0f;
                                limb.UnDislocate();
                                result.Message = Lang.Pack("You set {0}'s {1} back in place", Coop.Player(Coop.LocalId).Name, LimbName(limb));
                            }
                            break;
                        case TreatKind.RemoveTourniquet:
                        {
                            var t = limb.GetComponent<TourniquetScript>();
                            if (t == null) fail = Lang.N("no tourniquet there");
                            else { t.TakeOff(); result.Message = Lang.Pack("You took off {0}'s tourniquet", Coop.Player(Coop.LocalId).Name); }
                            break;
                        }
                        case TreatKind.RemoveSplint:
                        {
                            var sp = limb.GetComponent<SplintLimb>();
                            if (sp == null) fail = Lang.N("no splint there");
                            else { sp.TakeOff(); result.Message = Lang.Pack("You took off {0}'s splint", Coop.Player(Coop.LocalId).Name); }
                            break;
                        }
                        default:
                            tmp = MakeTemporary(m.Item, body.transform.position);
                            if (tmp == null) fail = Lang.N("couldn't make sense of the item");
                            else fail = UseItem(body, limb, tmp, who, result);
                            break;
                    }
                }
                catch (Exception e)
                {
                    fail = Lang.N("the treatment failed");
                    Log.Warn("[medic] treatment threw: " + e);
                }
            }

            if (fail != null)
            {
                result.Ok = false;
                result.Message = fail;
                if (tmp != null) UnityEngine.Object.Destroy(tmp.gameObject);
                Session.Broadcast(result);
                Log.Info("[medic] refused " + who + "'s treatment: " + fail);
                yield break;
            }

            // Let the item's own Update run once: items at zero condition destroy themselves,
            // tourniquets and splints already did.
            yield return null;
            result.Ok = true;
            result.Consumed = tmp == null || (tmp.condition <= 0f && tmp.Stats.destroyAtZeroCondition);
            if (!result.Consumed && tmp != null) result.Item = ItemCodec.Encode(tmp);
            if (tmp != null) UnityEngine.Object.Destroy(tmp.gameObject);
            if (string.IsNullOrEmpty(result.Message)) result.Message = Lang.Pack("Treated {0}", Coop.Player(Coop.LocalId).Name);
            Session.Broadcast(result);
            CoopRuntime.Notice(Lang.F("{0} treated your {1}", who, LimbName(limb)));
            Log.Info("[medic] " + who + " treated limb " + m.Limb + (result.Consumed ? " (item used up)" : ""));
        }

        private static Item MakeTemporary(byte[] blob, Vector2 near)
        {
            var spec = ItemCodec.Decode(blob);
            if (spec == null || string.IsNullOrEmpty(spec.Id)) return null;
            var prefab = Resources.Load(spec.Id) as GameObject;
            if (prefab == null) return null;
            GameObject go = null;
            Coop.AsRemote(() => go = UnityEngine.Object.Instantiate(prefab, (Vector3)near + Vector3.down * 400f, Quaternion.identity));
            var item = go != null ? go.GetComponent<Item>() : null;
            if (item == null) { if (go != null) UnityEngine.Object.Destroy(go); return null; }
            var rb = go.GetComponent<Rigidbody2D>();
            if (rb != null) rb.simulated = false;
            Coop.AsRemote(() => ItemCodec.Apply(item, spec));
            return item;
        }

        /// <summary>Applies the item to the limb on this (the patient's) machine. Returns a refusal or null.</summary>
        private string UseItem(Body body, Limb limb, Item tmp, string who, TreatResult result)
        {
            if (IsDefibrillator(tmp)) return Shock(body, tmp, who, result);

            if (tmp.Stats.usableOnLimb)
            {
                float before = tmp.condition;
                var limbBefore = Snapshot.Of(limb);
                Minigame mg;
                MinigameCapture.Capturing = true;
                try { tmp.Stats.useLimbAction(limb, tmp); }
                finally { mg = MinigameCapture.End(); }

                bool prosthetic = limb.prostheticInfo.hasProsthetic;
                if (mg is BandageMinigame bandage)
                {
                    if (prosthetic) return Lang.N("you can't bandage a prosthetic");
                    // Three full wraps, or until the dressing runs out.
                    for (float wrapped = 0f; wrapped < 3f && tmp != null && tmp.condition > 0f; wrapped += 1f / 18f)
                        bandage.OnUse(1f / 18f);
                }
                else if (mg is SyringeMinigame syringe)
                {
                    if (prosthetic) return Lang.N("you can't inject into a prosthetic");
                    var wat = tmp.GetComponent<WaterContainerItem>();
                    for (int i = 0; i < 10 && wat != null && wat.CurrentTotal > 0.01f; i++) syringe.OnUse(1f);
                }
                else if (mg is ShrapnelMinigame)
                {
                    if (limb.shrapnel <= 0) return Lang.N("there's nothing to pull out");
                    limb.shrapnel = 0;
                    tmp.condition -= 0.01f;
                }
                else if (mg != null) return "minigame";
                else if (Mathf.Approximately(before, tmp.condition) && limbBefore.SameAs(Snapshot.Of(limb)))
                    return Lang.N("that can't be used there");
                result.Message = Lang.Pack("You used {0} on {1}", Pretty(tmp != null ? tmp.id : "it"), Coop.Player(Coop.LocalId).Name);
                return null;
            }

            if (tmp.TryGetComponent<WaterContainerItem>(out var c) && tmp.Stats.ActuallyUsableOnLimb(tmp))
            {
                if (limb.prostheticInfo.hasProsthetic) return Lang.N("that won't work on a prosthetic");
                c.ApplyToLimb(limb);
                result.Message = Lang.Pack("You applied {0} to {1}", Pretty(tmp.id), Coop.Player(Coop.LocalId).Name);
                return null;
            }
            return Lang.N("that isn't a treatment");
        }

        /// <summary>What a treatment can change, to tell "it worked" from "nothing happened".</summary>
        private struct Snapshot
        {
            private float _skin, _muscle, _bleed, _bandage, _skinHeal, _infection, _boneTimer, _hemothorax;
            private bool _splint, _tourniquet, _chilled;

            internal static Snapshot Of(Limb l) => new Snapshot
            {
                _skin = l.skinHealth, _muscle = l.muscleHealth, _bleed = l.bleedAmount, _bandage = l.bandageSlowAmount,
                _skinHeal = l.skinHealAmount, _infection = l.infectionAmount, _boneTimer = l.boneHealTimer,
                _hemothorax = l.body != null ? l.body.hemothorax : 0f,
                _splint = l.GetComponent<SplintLimb>() != null, _tourniquet = l.GetComponent<TourniquetScript>() != null,
                _chilled = l.GetComponent<ChilledLimb>() != null,
            };

            internal bool SameAs(Snapshot o) =>
                _skin == o._skin && _muscle == o._muscle && _bleed == o._bleed && _bandage == o._bandage && _skinHeal == o._skinHeal &&
                _infection == o._infection && _boneTimer == o._boneTimer && _hemothorax == o._hemothorax &&
                _splint == o._splint && _tourniquet == o._tourniquet && _chilled == o._chilled;
        }

        private string Shock(Body body, Item defib, string who, TreatResult result)
        {
            if (defib.battery == null || defib.battery.GetCharge() < 2f) return Lang.N("the battery is flat");
            string why = ShockFrom(body, defib.id == "aed" ? 1f : 0.6f, who, result);
            if (why == null) defib.battery.DrainCharge(0.14f);
            return why;
        }

        /// <summary>A defibrillator shock from a teammate, applied to the local body. Returns a refusal or null.</summary>
        private string ShockFrom(Body body, float chance, string who, TreatResult result)
        {
            float deadFor = Coop.Get<VitalsSync>()?.LocalDeadFor ?? -1f;
            bool dead = !body.alive;
            if (dead)
            {
                if (deadFor > ReviveWindow) return Lang.Pack("too late: dead for {0} s", Mathf.RoundToInt(deadFor));
                if (UnityEngine.Random.value < ReviveChance)
                {
                    // The brain isn't gone yet: one more chance. The heart still has to be
                    // shocked back (the shock below, or the next one).
                    body.brainHealth = 3f;
                    Log.Info("[medic] revived by " + who + " after " + deadFor.ToString("F0") + " s");
                    Milestones.Add("revived");
                }
                else
                {
                    result.Message = Lang.Pack("Your shock didn't bring {0} back", Coop.Player(Coop.LocalId).Name);
                    Log.Info("[medic] shock from " + who + " didn't bring us back (dead " + deadFor.ToString("F0") + " s)");
                    return null;
                }
            }

            MedicSession.Bypass = true;
            try { DefibItem.Defibrillate(new Item.DefibInfo { chance = chance, limb = body.limbs[1] }); }
            finally { MedicSession.Bypass = false; }
            result.Message = dead ? Lang.Pack("You shocked {0} back to life", Coop.Player(Coop.LocalId).Name) : Lang.Pack("You shocked {0}'s heart", Coop.Player(Coop.LocalId).Name);
            Log.Info("[medic] shocked by " + who + " (chance " + chance.ToString("F2") + "), heart " + body.heartRate.ToString("F0"));
            return null;
        }

        /// <summary>Any item will do: Defibrillate never touches the item it's called on.</summary>
        private static Item _defibItem;

        private static Item DefibItem
        {
            get
            {
                if (_defibItem != null) return _defibItem;
                var prefab = Resources.Load("aed") as GameObject;
                GameObject go = null;
                Coop.AsRemote(() => go = prefab != null ? UnityEngine.Object.Instantiate(prefab, new Vector3(0f, -10000f, 0f), Quaternion.identity) : null);
                if (go != null)
                {
                    var rb = go.GetComponent<Rigidbody2D>();
                    if (rb != null) rb.simulated = false;
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    go.SetActive(false);
                    _defibItem = go.GetComponent<Item>();
                }
                return _defibItem;
            }
        }

        internal static string LimbName(Limb limb) => limb == null ? Lang.T("limb") : Pretty(limb.name);

        internal static string Pretty(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < id.Length; i++)
            {
                char ch = id[i];
                if (i > 0 && char.IsUpper(ch) && !char.IsUpper(id[i - 1])) sb.Append(' ');
                sb.Append(i == 0 ? char.ToUpperInvariant(ch) : char.ToLowerInvariant(ch));
            }
            return sb.ToString().Replace("(clone)", "").Trim();
        }
    }

    /// <summary>
    /// While <see cref="Capturing"/>, a minigame an item tries to start is kept for the
    /// caller instead of being shown: the patient's machine applies its result directly.
    /// </summary>
    [HarmonyPatch(typeof(MinigameBase), nameof(MinigameBase.StartMinigame))]
    internal static class MinigameCapture
    {
        internal static bool Capturing;
        private static Minigame _captured;

        internal static Minigame End()
        {
            Capturing = false;
            var m = _captured;
            _captured = null;
            return m;
        }

        private static bool Prefix(Minigame minigame)
        {
            if (!Capturing) return true;
            _captured = minigame;
            return false;
        }
    }
}
