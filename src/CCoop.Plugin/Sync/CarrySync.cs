using System.Collections.Generic;
using CCoop.Net.Session;
using CCoop.Plugin.Protocol;
using CCoop.Plugin.Runtime;
using UnityEngine;
using CCoop.Plugin.UI;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// Carrying a teammate, three ways:
    ///
    ///  * Drag — one free hand, no slot. Slow (half speed, no jumping). You hold their
    ///    hand (or their collar, if that hand is gone) and they slide along the ground.
    ///  * On your back — needs a free back slot (no backpack). You walk nearly as fast,
    ///    but they weigh on you and you can't jump as high. Their arms go round your
    ///    shoulders, their legs hang either side.
    ///  * In your arms — both hands free. A little slower, and only small hops. They lie
    ///    across your arms, head at your shoulder.
    ///
    /// A teammate who is down (asleep, out cold, dead) is simply picked up. One who is
    /// awake (with no legs, say) is asked first, and can get down with the jump key.
    ///
    /// The carried player's own machine moves their body (the game has no carrying; its
    /// tutorial claw is the pattern): it keeps the ragdoll limp, holds one limb firmly
    /// to the carrier every physics step and pulls a few others into the pose with soft
    /// springs. On the carrier's screen the carried teammate is drawn held by the
    /// carrier's real body, so they don't trail a ping behind. Carrying is work: it
    /// tires the carrier (stamina, more when moving) and slowly builds strength.
    /// </summary>
    internal sealed class CarrySync : CoopModule
    {
        private const float MaxGap = 12f;
        private const float OfferLifetime = 20f;
        internal const float BackLoad = 8f;
        internal const float ArmsLoad = 6f;

        // Carrier side
        private int _carrying = -1;
        private CarryMode _mode;
        private bool _awakeRider;
        private bool _setForceWalk;

        // Carried side
        private int _carriedBy = -1;
        private CarryMode _carriedMode;
        private bool _riding;          // got on awake: waking up doesn't end it
        private Vector2 _hold;
        private int _heldLimb = 1;

        // Offers to carry someone awake: sent (target -> mode) and received (carrier -> mode).
        private readonly Dictionary<byte, KeyValuePair<CarryMode, float>> _offered = new Dictionary<byte, KeyValuePair<CarryMode, float>>();
        private readonly Dictionary<byte, KeyValuePair<CarryMode, float>> _offers = new Dictionary<byte, KeyValuePair<CarryMode, float>>();
        private static GUIStyle _box;

        /// <summary>Self-test: say yes to the next offer to carry us.</summary>
        internal static bool DevAutoAccept;

        internal void DevGetDown() => GetDown("got down");

        internal bool Carrying(byte id) => _carrying == id;
        internal int CarryingId => _carrying;
        internal int CarriedBy => _carriedBy;
        internal CarryMode Mode => _mode;
        internal bool Offered(byte id) => _offered.TryGetValue(id, out var o) && Time.unscaledTime - o.Value < OfferLifetime;

        /// <summary>The local player is carrying, or being carried by, this teammate (no friendly fire then).</summary>
        internal static bool Together(byte id)
        {
            var c = Coop.Get<CarrySync>();
            return c != null && (c._carrying == id || c._carriedBy == id);
        }

        /// <summary>Extra weight on the local carrier, for the encumbrance hook.</summary>
        internal static float LocalLoad;

        /// <summary>The local carrier's jump multiplier, for the jump hook.</summary>
        internal static float LocalJumpMult = 1f;

        internal override void Attach(NetSession s)
        {
            s.On<CarryCommand>(OnCommand);
            s.On<CarryOffer>(OnOffer);
        }

        internal override void Detach()
        {
            StopCarrying();
            Release("session over");
            _offered.Clear();
            _offers.Clear();
        }

        internal override void OnWorldLost()
        {
            StopCarrying();
            Release("layer over");
            _offered.Clear();
            _offers.Clear();
        }

        internal override void OnPeerLeft(PeerInfo peer)
        {
            if (_carrying == peer.Id) StopCarrying();
            if (_carriedBy == peer.Id) Release("carrier left");
            _offered.Remove(peer.Id);
            _offers.Remove(peer.Id);
        }

        // ------------------------------------------------------------------ carrier

        internal static bool Downed(byte id)
        {
            var rec = Coop.Player(id);
            return rec.HasPosition && (!rec.Alive || !rec.Conscious);
        }

        private static bool HandFree(Body b, int slot)
        {
            try { return b.GetItem(slot) == null; }
            catch { return true; }
        }

        /// <summary>Why the local player can't carry this way right now, or null.</summary>
        internal static string CantCarry(CarryMode mode)
        {
            var b = GameInfo.LocalBody;
            if (b == null) return Lang.T("You need to be in the layer.");
            if (!b.alive || !b.conscious) return Lang.T("You can't carry anyone right now.");
            if (Coop.Get<CarrySync>()?._carriedBy >= 0) return Lang.T("You're being carried yourself.");
            switch (mode)
            {
                case CarryMode.Drag:
                    return HandFree(b, b.handSlot) || HandFree(b, 1 - b.handSlot) ? null : Lang.T("Empty a hand to drag them.");
                case CarryMode.Back:
                    return b.GetWearableBySlotID("back") == null ? null : Lang.T("Take off your backpack to carry them on your back.");
                default:
                    return HandFree(b, 0) && HandFree(b, 1) ? null : Lang.T("Empty both hands to carry them in your arms.");
            }
        }

        /// <summary>Pick up a downed teammate, or offer to carry one who is awake.</summary>
        internal void Pick(byte target, CarryMode mode)
        {
            string why = Medic.CantTreat(target);
            if (why == null) why = CantCarry(mode);
            if (why == null && Coop.Get<CarrySync>()?._carriedBy == target) why = Lang.F("{0} is carrying you.", Coop.Player(target).Name);
            if (why != null) { CoopRuntime.Notice(why); return; }
            if (!Downed(target))
            {
                _offered[target] = new KeyValuePair<CarryMode, float>(mode, Time.unscaledTime);
                Session.Broadcast(new CarryOffer { Carrier = Coop.LocalId, Target = target, Mode = mode, Op = CarryOfferOp.Offer });
                CoopRuntime.Notice(Lang.F("Offered to carry {0}", Coop.Player(target).Name));
                Log.Info("[carry] offering " + Coop.Player(target).Name + " a " + mode + " carry");
                return;
            }
            Begin(target, mode, false);
        }

        private void Begin(byte target, CarryMode mode, bool awake)
        {
            if (_carrying >= 0) PutDown("switching");
            _carrying = target;
            _mode = mode;
            _awakeRider = awake;
            Session.Broadcast(new CarryCommand { Carrier = Coop.LocalId, Target = target, Start = true, Mode = mode });
            CoopRuntime.Notice(Lang.F("{0} {1}", Verb(mode), Coop.Player(target).Name));
            Log.Info("[carry] " + mode + " " + Coop.Player(target).Name + (awake ? " (awake)" : ""));
            var r = Coop.Get<PlayerSync>()?.Find(target);
            if (r != null)
            {
                r.PinLimb = HeldLimbOf(target, mode);
                r.PinAnchor = LocalAnchor;
            }
        }

        internal static string Verb(CarryMode mode) =>
            mode == CarryMode.Drag ? Lang.T("Dragging") : mode == CarryMode.Back ? Lang.T("Carrying on your back:") : Lang.T("Carrying in your arms:");

        internal void PutDown(string why = "put down")
        {
            Milestones.NotCarrying();
            if (_carrying < 0) return;
            byte target = (byte)_carrying;
            Log.Info("[carry] letting go of " + Coop.Player(target).Name + ": " + why);
            StopCarrying();
            Session.Broadcast(new CarryCommand { Carrier = Coop.LocalId, Target = target, Start = false });
            CoopRuntime.Notice(Lang.F("Put {0} down", Coop.Player(target).Name));
        }

        private void StopCarrying()
        {
            if (_carrying >= 0)
            {
                var r = Coop.Get<PlayerSync>()?.Find((byte)_carrying);
                if (r != null) { r.PinAnchor = null; r.PinLimb = -1; }
            }
            _carrying = -1;
            _awakeRider = false;
            LocalLoad = 0f;
            LocalJumpMult = 1f;
            var b = GameInfo.LocalBody;
            if (_setForceWalk && b != null) b.forceWalk = false;
            _setForceWalk = false;
        }

        /// <summary>Where a carrier with this chest, facing and hand holds the carried body's held limb.</summary>
        private static Vector2 AnchorFrom(Vector2 chest, Vector2 hand, bool right, CarryMode mode)
        {
            float f = right ? 1f : -1f;
            switch (mode)
            {
                case CarryMode.Drag:
                    return hand + new Vector2(-f * 0.5f, -0.25f);
                case CarryMode.Back:
                    return chest + new Vector2(-f * 0.45f, 0.2f);
                default:
                    return chest + new Vector2(f * 0.55f, -0.2f);
            }
        }

        /// <summary>On the carried player's machine: where the carrier's puppet holds us.</summary>
        private static Vector2? Anchor(RemotePlayer carrier, CarryMode mode)
        {
            var chest = carrier.LimbPosition(1) ?? carrier.ShownAt;
            if (!chest.HasValue) return null;
            var hand = carrier.LimbPosition(5) ?? carrier.LimbPosition(8) ?? chest.Value;
            return AnchorFrom(chest.Value, hand, carrier.FacingRight, mode);
        }

        /// <summary>On the carrier's machine: where our own body holds them (for drawing them held).</summary>
        private Vector2? LocalAnchor()
        {
            var me = GameInfo.LocalBody;
            if (me == null || me.limbs == null || me.limbs.Length < 9 || _carrying < 0) return null;
            Vector2 chest = me.limbs[1].transform.position;
            var handLimb = me.limbs[5] != null && !me.limbs[5].dismembered ? me.limbs[5] : me.limbs[8];
            Vector2 hand = handLimb != null ? (Vector2)handLimb.transform.position : chest;
            return AnchorFrom(chest, hand, me.isRight, _mode);
        }

        internal override void Tick()
        {
            if (_carrying >= 0) CarrierTick();
            if (_carriedBy >= 0) CarriedTick();
            float now = Time.unscaledTime;
            if (_offers.Count > 0)
                foreach (var k in new List<byte>(_offers.Keys))
                    if (now - _offers[k].Value > OfferLifetime) Answer(k, false);
        }

        private void CarrierTick()
        {
            var me = GameInfo.LocalBody;
            var r = Coop.Get<PlayerSync>()?.Find((byte)_carrying);
            var at = r?.LimbPosition(1) ?? r?.ShownAt;
            if (me == null || !me.conscious) { PutDown("we're down"); return; }
            if (!at.HasValue) { PutDown("can't see them"); return; }
            float gap = Vector2.Distance(me.transform.position, at.Value);
            if (gap > MaxGap) { PutDown("too far (" + gap.ToString("F1") + " m)"); return; }
            if (!_awakeRider && !Downed((byte)_carrying)) { PutDown("they're up"); return; }
            if (CantCarry(_mode) != null) { PutDown("hands or back no longer free"); return; }
            Milestones.Carrying(me.transform.position);
            if (r != null && r.PinAnchor == null) { r.PinLimb = HeldLimbOf((byte)_carrying, _mode); r.PinAnchor = LocalAnchor; }

            // The cost, on our own body.
            bool moving = me.rb != null && Mathf.Abs(me.rb.velocity.x) > 1f;
            float tire;
            switch (_mode)
            {
                case CarryMode.Drag:
                    me.forceWalk = true;  // half speed, no jumping
                    _setForceWalk = true;
                    LocalLoad = 0f;
                    LocalJumpMult = 1f;
                    tire = 0.35f;
                    break;
                case CarryMode.Back:
                    LocalLoad = BackLoad;
                    LocalJumpMult = 0.7f;
                    tire = 0.5f;
                    break;
                default:
                    LocalLoad = ArmsLoad;
                    LocalJumpMult = 0.5f;
                    me.temporarySlowdown = Mathf.Max(me.temporarySlowdown, 0.15f);
                    tire = 0.9f;
                    break;
            }
            // Carrying is work, like the game's own workouts: it tires, and it trains strength.
            me.stamina = Mathf.Max(0f, me.stamina - Time.deltaTime * tire * (moving ? 1f : 0.3f));
            try { me.skills.AddExp(0, Time.deltaTime * (moving ? 0.12f : 0.04f)); }
            catch { /* no skills on this character */ }
        }

        /// <summary>The limb of theirs that is held: hand, chest or hips; another if that one is gone.</summary>
        private static int HeldLimbOf(byte id, CarryMode mode)
        {
            var v = Coop.Get<VitalsSync>()?.Of(id);
            bool Gone(int i) => v != null && i < v.Limbs.Count && (v.Limbs[i].Signs & LimbSigns.Missing) != 0;
            switch (mode)
            {
                case CarryMode.Drag: return !Gone(5) ? 5 : !Gone(8) ? 8 : 1;
                case CarryMode.Back: return 1;
                default: return 2;
            }
        }

        private static int HeldLimbOf(Body b, CarryMode mode)
        {
            bool Ok(int i) => b.limbs != null && i < b.limbs.Length && b.limbs[i] != null && !b.limbs[i].dismembered;
            switch (mode)
            {
                case CarryMode.Drag: return Ok(5) ? 5 : Ok(8) ? 8 : 1;
                case CarryMode.Back: return 1;
                default: return Ok(2) ? 2 : 1;
            }
        }

        // ------------------------------------------------------------------ offers (carrying someone awake)

        private void OnOffer(byte origin, CarryOffer m)
        {
            if (origin == Coop.LocalId) return;
            if (m.Op == CarryOfferOp.Offer)
            {
                if (m.Target != Coop.LocalId) return;
                _offers[origin] = new KeyValuePair<CarryMode, float>(m.Mode, Time.unscaledTime);
                CoopRuntime.Notice(Lang.F("{0} offers to carry you", Coop.Player(origin).Name));
                if (DevAutoAccept) Answer(origin, true);
                return;
            }
            if (m.Carrier != Coop.LocalId || !_offered.TryGetValue(origin, out var o)) return;
            _offered.Remove(origin);
            string who = Coop.Player(origin).Name;
            if (m.Op == CarryOfferOp.Decline)
            {
                CoopRuntime.Notice(Lang.F("{0} would rather walk", who));
                Log.Info("[carry] " + who + " turned down the carry");
                return;
            }
            string why = CantCarry(o.Key) ?? Medic.CantTreat(origin);
            if (why != null)
            {
                CoopRuntime.Notice(why);
                Session.Broadcast(new CarryCommand { Carrier = Coop.LocalId, Target = origin, Start = false });
                return;
            }
            Begin(origin, o.Key, true);
        }

        private void Answer(byte carrier, bool yes)
        {
            if (!_offers.TryGetValue(carrier, out var o)) return;
            _offers.Remove(carrier);
            Session.Broadcast(new CarryOffer { Carrier = carrier, Target = Coop.LocalId, Mode = o.Key, Op = yes ? CarryOfferOp.Accept : CarryOfferOp.Decline });
            Log.Info("[carry] " + (yes ? "accepted" : "turned down") + " " + Coop.Player(carrier).Name + "'s offer to carry us");
            // Being carried awake: the carry starts when their CarryCommand arrives.
            if (yes) _riding = true;
        }

        // ------------------------------------------------------------------ carried

        private void OnCommand(byte origin, CarryCommand m)
        {
            if (m.Target == Coop.LocalId)
            {
                if (m.Start)
                {
                    var b = GameInfo.LocalBody;
                    _carriedBy = m.Carrier;
                    _carriedMode = m.Mode;
                    // Awake and not asked? (An old offer, or two carriers at once.) Only if we said yes.
                    _riding = b != null && b.alive && b.conscious && _riding;
                    if (b != null && b.alive && b.conscious && !_riding)
                    {
                        Session.Broadcast(new CarryCommand { Carrier = m.Carrier, Target = Coop.LocalId, Start = false });
                        _carriedBy = -1;
                        return;
                    }
                    _heldLimb = b != null ? HeldLimbOf(b, m.Mode) : 1;
                    _hold = b != null && b.limbs != null && _heldLimb < b.limbs.Length ? (Vector2)b.limbs[_heldLimb].transform.position : Vector2.zero;
                    CoopRuntime.Notice(m.Mode == CarryMode.Drag ? Lang.F("{0} is dragging you", Coop.Player(m.Carrier).Name) : Lang.F("{0} is carrying you", Coop.Player(m.Carrier).Name));
                    Log.Info("[carry] " + m.Mode + " by " + Coop.Player(m.Carrier).Name + (_riding ? " (awake)" : "") + ", held by limb " + _heldLimb);
                }
                else if (_carriedBy == m.Carrier) Release("the carrier let go");
            }
            else if (m.Carrier == Coop.LocalId && !m.Start && _carrying == m.Target)
            {
                StopCarrying();  // they woke up, or got down
                CoopRuntime.Notice(Lang.F("{0} got down", Coop.Player(m.Target).Name));
            }
        }

        private void Release(string why)
        {
            if (_carriedBy >= 0) Log.Info("[carry] released: " + why);
            _carriedBy = -1;
            _riding = false;
        }

        private void GetDown(string why)
        {
            if (_carriedBy < 0) return;
            Session.Broadcast(new CarryCommand { Carrier = (byte)_carriedBy, Target = Coop.LocalId, Start = false });
            Release(why);
        }

        private void CarriedTick()
        {
            var body = GameInfo.LocalBody;
            var carrier = Coop.Get<PlayerSync>()?.Find((byte)_carriedBy);
            if (body == null || !Coop.WorldLive || carrier == null || !carrier.ShownAt.HasValue)
            {
                Release(body == null ? "no body" : !Coop.WorldLive ? "layer offline" : "carrier not visible");
                return;
            }
            if (!_riding && body.alive && body.conscious) { GetDown("woke up"); return; }
            if (_riding)
            {
                if (!body.alive) { _riding = false; return; }  // carried on as a body now
                KeyCode jump = KeyCode.Space;
                try { jump = KeyBinds.GetBind("jump"); } catch { /* default */ }
                if (!InputBlock.HoldGame && jump != KeyCode.None && Input.GetKeyDown(jump)) GetDown("got down");
            }
        }

        /// <summary>The pose: each entry is a limb, where it goes relative to the carrier's chest (facing right), and how firmly.</summary>
        private static readonly Dictionary<CarryMode, (int limb, Vector2 at, float pull)[]> Poses = new Dictionary<CarryMode, (int, Vector2, float)[]>
        {
            // Arms round the carrier's shoulders, head beside theirs, legs hanging.
            [CarryMode.Back] = new[] { (0, new Vector2(-0.25f, 0.85f), 7f), (2, new Vector2(-0.55f, -0.55f), 7f), (5, new Vector2(0.3f, 0.35f), 5f), (8, new Vector2(0.3f, 0.3f), 5f) },
            // Lying across the arms: head at the shoulder, knees over the forearm.
            [CarryMode.Arms] = new[] { (1, new Vector2(0.0f, -0.05f), 8f), (0, new Vector2(-0.45f, 0.2f), 6f), (10, new Vector2(1.05f, -0.25f), 5f), (13, new Vector2(1.05f, -0.25f), 5f) },
            // Dragged by the hand: the rest follows on the ground.
            [CarryMode.Drag] = new (int, Vector2, float)[0],
        };

        /// <summary>Carried player's machine, every physics step: keep the ragdoll limp and hold it in the pose.</summary>
        internal void FixedTick()
        {
            if (_carriedBy < 0) return;
            var body = GameInfo.LocalBody;
            var carrier = Coop.Get<PlayerSync>()?.Find((byte)_carriedBy);
            if (body == null || body.limbs == null || body.limbs.Length <= 5 || carrier == null) return;
            var anchor = Anchor(carrier, _carriedMode);
            if (!anchor.HasValue) return;
            if (_heldLimb >= body.limbs.Length || body.limbs[_heldLimb] == null || body.limbs[_heldLimb].dismembered)
                _heldLimb = HeldLimbOf(body, _carriedMode);
            var limb = body.limbs[_heldLimb];
            var rb = limb != null ? limb.GetComponent<Rigidbody2D>() : null;
            if (rb == null) return;
            if (Vector2.Distance(rb.position, anchor.Value) > MaxGap * 1.5f) { Release("too far from the carrier"); return; }

            // Limp, but someone awake isn't put in shock (just kept off their feet).
            body.shock = Mathf.Max(body.shock, _riding ? 12f : 30f);
            if (body.standing) body.Ragdoll();
            // Glide to the carrier rather than snapping (a carrier's step shouldn't fling the
            // body), and never into rock: pushed into a wall, physics could pop the body out
            // on the far side. Where the hold point is solid, the body waits for open air.
            var next = Vector2.MoveTowards(_hold, anchor.Value, 16f * Time.fixedDeltaTime);
            var w = WorldGeneration.world;
            bool solid = false;
            try { solid = w != null && w.GetBlock(next) != 0; } catch { /* outside the world */ }
            if (!solid) _hold = next;
            rb.velocity = Vector2.zero;
            rb.MovePosition(_hold);

            // The rest of the pose, softly: springs toward each spot, never a snap.
            if (!Poses.TryGetValue(_carriedMode, out var pose) || pose.Length == 0) return;
            var chest = carrier.LimbPosition(1) ?? carrier.ShownAt;
            if (!chest.HasValue) return;
            float f = carrier.FacingRight ? 1f : -1f;
            foreach (var (i, at, pull) in pose)
            {
                if (i == _heldLimb || i >= body.limbs.Length) continue;
                var l = body.limbs[i];
                if (l == null || l.dismembered) continue;
                var lrb = l.GetComponent<Rigidbody2D>();
                if (lrb == null) continue;
                Vector2 target = chest.Value + new Vector2(at.x * f, at.y);
                Vector2 want = (target - lrb.position) * pull;
                if (want.sqrMagnitude > 400f) want = want.normalized * 20f;
                lrb.velocity = Vector2.Lerp(lrb.velocity, want, 0.35f);
            }
        }

        internal override void Gui()
        {
            if (_offers.Count > 0) DrawOffers();
            if (_carriedBy >= 0 && _riding)
            {
                string key = "Space";
                try { key = KeyBinds.GetBind("jump").ToString(); } catch { /* default */ }
                UI.Overlay.Bottom(Lang.F("{0} is carrying you", Coop.Player((byte)_carriedBy).Name), Lang.F("Press {0} to get down.", key), 2);
                return;
            }
            if (_carrying < 0) return;
            string cost = _mode == CarryMode.Drag ? Lang.T("Slow going, and no jumping.")
                : _mode == CarryMode.Back ? Lang.T("They weigh on you: you can't jump as high.")
                : Lang.T("Both arms full: slower, and only small hops.");
            UI.Overlay.Bottom(Verb(_mode) + " " + Coop.Player((byte)_carrying).Name, cost + " " + Lang.T("Right-click them to put them down."), 2);
        }

        private void DrawOffers()
        {
            if (_box == null) _box = new GUIStyle(GUI.skin.box) { fontSize = UiSkin.Body, alignment = TextAnchor.UpperCenter };
            float k = CoopWindow.Scale;
            var old = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(k, k, 1f));
            try
            {
                float w = Screen.width / k;
                float y = 100f;
                foreach (var kv in new List<KeyValuePair<byte, KeyValuePair<CarryMode, float>>>(_offers))
                {
                    string how = kv.Value.Key == CarryMode.Drag ? Lang.T("drag you along") : kv.Value.Key == CarryMode.Back ? Lang.T("carry you on their back") : Lang.T("carry you in their arms");
                    int a = UiSkin.Prompt(_box, w, ref y, Lang.F("{0} offers to {1}", Coop.Player(kv.Key).Name, how), Lang.T("Climb on"), Lang.T("I'll walk"));
                    if (a != 0) Answer(kv.Key, a == 1);
                }
            }
            finally { GUI.matrix = old; }
        }
    }
}
