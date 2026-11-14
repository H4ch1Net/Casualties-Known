using CCoop.Net.Session;
using CCoop.Plugin.Protocol;
using CCoop.Plugin.Runtime;
using UnityEngine;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// Carrying a downed teammate, three ways:
    ///
    ///  * Drag — one free hand, no slot. Slow (half speed, no jumping).
    ///  * On your back — needs a free back slot (no backpack). You walk nearly as fast,
    ///    but they weigh on you and you can't jump as high.
    ///  * In your arms — both hands free. A little slower, and only small hops.
    ///
    /// The carried player's own machine moves their body (the game has no carrying; its
    /// tutorial claw is the pattern): it keeps the ragdoll down and pins one limb to the
    /// carrier every physics step — the hand when dragged, the chest on the carrier's back,
    /// the hips in the carrier's arms. Waking up ends it. The carrier's cost is applied
    /// to the carrier's own body through the game's encumbrance and jump numbers.
    /// </summary>
    internal sealed class CarrySync : CoopModule
    {
        private const float MaxGap = 12f;
        internal const float BackLoad = 8f;
        internal const float ArmsLoad = 6f;

        // Carrier side
        private int _carrying = -1;
        private CarryMode _mode;
        private bool _setForceWalk;

        // Carried side
        private int _carriedBy = -1;
        private CarryMode _carriedMode;
        private Vector2 _hold;

        internal bool Carrying(byte id) => _carrying == id;
        internal int CarryingId => _carrying;
        internal CarryMode Mode => _mode;

        /// <summary>Extra weight on the local carrier, for the encumbrance hook.</summary>
        internal static float LocalLoad;

        /// <summary>The local carrier's jump multiplier, for the jump hook.</summary>
        internal static float LocalJumpMult = 1f;

        internal override void Attach(NetSession s) => s.On<CarryCommand>(OnCommand);

        internal override void Detach()
        {
            StopCarrying();
            Release("session over");
        }

        internal override void OnWorldLost()
        {
            StopCarrying();
            Release("layer over");
        }

        internal override void OnPeerLeft(PeerInfo peer)
        {
            if (_carrying == peer.Id) StopCarrying();
            if (_carriedBy == peer.Id) Release("carrier left");
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
            if (b == null) return "You need to be in the layer.";
            switch (mode)
            {
                case CarryMode.Drag:
                    return HandFree(b, b.handSlot) ? null : "Empty your hand to drag them.";
                case CarryMode.Back:
                    return b.GetWearableBySlotID("back") == null ? null : "Take off your backpack to carry them on your back.";
                default:
                    return HandFree(b, 0) && HandFree(b, 1) ? null : "Empty both hands to carry them in your arms.";
            }
        }

        internal void Pick(byte target, CarryMode mode)
        {
            string why = Medic.CantTreat(target);
            if (why == null && !Downed(target)) why = Coop.Player(target).Name + " can walk.";
            if (why == null) why = CantCarry(mode);
            if (why != null) { CoopRuntime.Notice(why); return; }
            if (_carrying >= 0) PutDown("switching");
            _carrying = target;
            _mode = mode;
            Session.Broadcast(new CarryCommand { Carrier = Coop.LocalId, Target = target, Start = true, Mode = mode });
            CoopRuntime.Notice(Verb(mode) + " " + Coop.Player(target).Name);
            Log.Info("[carry] " + mode + " " + Coop.Player(target).Name);
        }

        internal static string Verb(CarryMode mode) =>
            mode == CarryMode.Drag ? "Dragging" : mode == CarryMode.Back ? "Carrying on your back:" : "Carrying in your arms:";

        internal void PutDown(string why = "put down")
        {
            if (_carrying < 0) return;
            byte target = (byte)_carrying;
            Log.Info("[carry] letting go of " + Coop.Player(target).Name + ": " + why);
            StopCarrying();
            Session.Broadcast(new CarryCommand { Carrier = Coop.LocalId, Target = target, Start = false });
            CoopRuntime.Notice("Put " + Coop.Player(target).Name + " down");
        }

        private void StopCarrying()
        {
            _carrying = -1;
            LocalLoad = 0f;
            LocalJumpMult = 1f;
            var b = GameInfo.LocalBody;
            if (_setForceWalk && b != null) b.forceWalk = false;
            _setForceWalk = false;
        }

        /// <summary>Where the carrier's own puppet holds the body, as seen on the carried player's machine.</summary>
        private static Vector2? Anchor(RemotePlayer carrier, CarryMode mode)
        {
            var chest = carrier.LimbPosition(1) ?? carrier.ShownAt;
            if (!chest.HasValue) return null;
            float facing = carrier.FacingRight ? 1f : -1f;
            switch (mode)
            {
                case CarryMode.Drag:
                    return (carrier.LimbPosition(5) ?? chest.Value) + new Vector2(-facing * 0.6f, -0.2f);
                case CarryMode.Back:
                    return chest.Value + new Vector2(-facing * 0.45f, 0.25f);
                default:
                    return chest.Value + new Vector2(facing * 0.55f, -0.15f);
            }
        }

        internal override void Tick()
        {
            if (_carrying >= 0) CarrierTick();
            if (_carriedBy >= 0) CarriedTick();
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
            if (!Downed((byte)_carrying)) { PutDown("they're up"); return; }
            if (CantCarry(_mode) != null) { PutDown("hands or back no longer free"); return; }

            // The cost, on our own body.
            switch (_mode)
            {
                case CarryMode.Drag:
                    me.forceWalk = true;  // half speed, no jumping
                    _setForceWalk = true;
                    LocalLoad = 0f;
                    LocalJumpMult = 1f;
                    break;
                case CarryMode.Back:
                    LocalLoad = BackLoad;
                    LocalJumpMult = 0.7f;
                    break;
                default:
                    LocalLoad = ArmsLoad;
                    LocalJumpMult = 0.5f;
                    me.temporarySlowdown = Mathf.Max(me.temporarySlowdown, 0.15f);
                    break;
            }
        }

        // ------------------------------------------------------------------ carried

        private void OnCommand(byte origin, CarryCommand m)
        {
            if (m.Target == Coop.LocalId)
            {
                if (m.Start)
                {
                    _carriedBy = m.Carrier;
                    _carriedMode = m.Mode;
                    var b = GameInfo.LocalBody;
                    _hold = b != null && b.limbs != null && b.limbs.Length > 5 ? (Vector2)b.limbs[PinnedLimb(m.Mode)].transform.position : Vector2.zero;
                    CoopRuntime.Notice(Coop.Player(m.Carrier).Name + (m.Mode == CarryMode.Drag ? " is dragging you" : " is carrying you"));
                    Log.Info("[carry] " + m.Mode + " by " + Coop.Player(m.Carrier).Name);
                }
                else if (_carriedBy == m.Carrier) Release("the carrier let go");
            }
            else if (m.Carrier == Coop.LocalId && !m.Start && _carrying == m.Target)
            {
                StopCarrying();  // they woke up and got down
                CoopRuntime.Notice(Coop.Player(m.Target).Name + " got down");
            }
        }

        private static int PinnedLimb(CarryMode mode) => mode == CarryMode.Drag ? 5 : mode == CarryMode.Back ? 1 : 2;

        private void Release(string why)
        {
            if (_carriedBy >= 0) Log.Info("[carry] released: " + why);
            _carriedBy = -1;
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
            if (body.alive && body.conscious)
            {
                Session.Broadcast(new CarryCommand { Carrier = (byte)_carriedBy, Target = Coop.LocalId, Start = false });
                Release("woke up");
            }
        }

        /// <summary>Carried player's machine, every physics step: keep the ragdoll down and pin it to the carrier.</summary>
        internal void FixedTick()
        {
            if (_carriedBy < 0) return;
            var body = GameInfo.LocalBody;
            var carrier = Coop.Get<PlayerSync>()?.Find((byte)_carriedBy);
            if (body == null || body.limbs == null || body.limbs.Length <= 5 || carrier == null) return;
            var anchor = Anchor(carrier, _carriedMode);
            if (!anchor.HasValue) return;
            var limb = body.limbs[PinnedLimb(_carriedMode)];
            var rb = limb != null ? limb.GetComponent<Rigidbody2D>() : null;
            if (rb == null) return;
            if (Vector2.Distance(rb.position, anchor.Value) > MaxGap * 1.5f) { Release("too far from the carrier"); return; }

            body.shock = Mathf.Max(body.shock, 30f);
            if (body.standing) body.Ragdoll();
            // Glide to the carrier rather than snapping (a carrier's step shouldn't fling the body).
            _hold = Vector2.MoveTowards(_hold, anchor.Value, 16f * Time.fixedDeltaTime);
            rb.velocity = Vector2.zero;
            rb.MovePosition(_hold);
        }

        internal override void Gui()
        {
            if (_carrying < 0) return;
            string cost = _mode == CarryMode.Drag ? "Slow going, and no jumping."
                : _mode == CarryMode.Back ? "They weigh on you: you can't jump as high."
                : "Both arms full: slower, and only small hops.";
            UI.Overlay.Bottom(Verb(_mode) + " " + Coop.Player((byte)_carrying).Name, cost + " Right-click them to put them down.", 2);
        }
    }
}
