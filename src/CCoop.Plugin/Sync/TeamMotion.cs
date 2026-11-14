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
    /// Helping each other move, all done on the helped player's own machine with the
    /// game's own movement:
    ///
    ///  * Leg-up — a teammate cups their hands; your next jump next to them goes higher.
    ///  * Pull up — a teammate on a ledge reaches down; you climb up to their hand along a
    ///    temporary climbable (the game's rope mechanic), drifting up without input.
    ///  * Snap out of it — a teammate shakes you out of a deep low: a mood boost on top of
    ///    the game's own mood total that fades over a few minutes. The cause is still there.
    ///  * Lift together — help a teammate carry a heavy item (barrel, toolbox, jug, drill):
    ///    you take half its weight, they lose it.
    ///  * Catch — hold the catch key under a falling teammate to soften their landing; a
    ///    long fall hurts the catcher's arms. Land on someone who wasn't ready and you hurt
    ///    them (and come down a little softer yourself).
    /// </summary>
    internal sealed class TeamMotion : CoopModule
    {
        private const float LegUpWindow = 5f;
        private const float LegUpMult = 1.75f;
        internal const float LegUpReach = 1.8f;
        internal const float PullReach = 5f;
        private const float SnapDuration = 180f;
        private const float SnapCooldown = 90f;
        internal const float HeavyItem = 4f;
        internal const float LiftReach = 2.5f;

        // ---- leg-up (helped side)
        private static float _legUpUntil;
        private static byte _legUpFrom;

        // ---- pull (helped side)
        private Climbable _rope;
        private float _ropeUntil;

        // ---- snap (helped side), read by the mood hook
        internal static float SnapBoost;
        internal static float SnapAt = -1000f;
        private readonly Dictionary<byte, float> _snapGiven = new Dictionary<byte, float>();

        // ---- lifting together
        private int _liftingFor = -1;   // helper: whose item we help with
        private float _liftWeight;
        private readonly Dictionary<byte, float> _helpers = new Dictionary<byte, float>();  // holder: who helps, and with what weight

        /// <summary>Change to the local player's carried weight from lifting together.</summary>
        internal static float LiftLoad;

        // ---- catching
        private float _nextReady;
        private readonly Dictionary<byte, float> _readyUntil = new Dictionary<byte, float>();
        private bool _caughtThisFall;

        internal override void Attach(NetSession s) => s.On<TeamMove>(OnMove);

        internal override void Detach()
        {
            DropRope();
            _helpers.Clear();
            _liftingFor = -1;
            LiftLoad = 0f;
            SnapBoost = 0f;
            _legUpUntil = 0f;
            _readyUntil.Clear();
        }

        internal override void OnWorldLost()
        {
            DropRope();
            StopLifting(false);
            _helpers.Clear();
            LiftLoad = 0f;
        }

        internal override void OnPeerLeft(PeerInfo peer)
        {
            if (_liftingFor == peer.Id) StopLifting(false);
            _helpers.Remove(peer.Id);
            _readyUntil.Remove(peer.Id);
        }

        private static RemotePlayer Puppet(byte id) => Coop.Get<PlayerSync>()?.Find(id);

        private static float DistanceTo(byte id)
        {
            var me = GameInfo.LocalBody;
            var at = Puppet(id)?.ShownAt;
            return me == null || !at.HasValue ? float.MaxValue : Vector2.Distance(me.transform.position, at.Value);
        }

        private static void Say(string line)
        {
            try { GameInfo.LocalBody?.talker?.Talk(line, null, true, true); } catch { /* speechless */ }
        }

        // ================================================================== actions (helper side)

        internal void GiveLegUp(byte target)
        {
            if (DistanceTo(target) > LegUpReach) { CoopRuntime.Notice("Stand right next to them."); return; }
            Session.Broadcast(new TeamMove { Kind = MoveKind.LegUp, Target = target, F = LegUpWindow });
            Say("*cups hands for a leg-up*");
            Log.Info("[move] leg-up for " + Coop.Player(target).Name);
        }

        internal void PullUp(byte target)
        {
            var me = GameInfo.LocalBody;
            if (me == null || DistanceTo(target) > PullReach) { CoopRuntime.Notice("They're too far to reach."); return; }
            var hand = me.limbs != null && me.limbs.Length > 5 && me.limbs[5] != null ? (Vector2)me.limbs[5].transform.position : (Vector2)me.transform.position;
            var top = new Vector2(hand.x, Mathf.Max(hand.y, me.transform.position.y) + 0.4f);
            Session.Broadcast(new TeamMove { Kind = MoveKind.Pull, Target = target, X = top.x, Y = top.y });
            Say("*reaches down*");
            Log.Info("[move] reaching down to " + Coop.Player(target).Name);
        }

        internal string CantSnap(byte target)
        {
            if (_snapGiven.TryGetValue(target, out var at) && Time.unscaledTime - at < SnapCooldown)
                return "Give them a minute (" + Mathf.CeilToInt(SnapCooldown - (Time.unscaledTime - at)) + " s).";
            return null;
        }

        internal void Snap(byte target)
        {
            string why = CantSnap(target);
            if (why != null) { CoopRuntime.Notice(why); return; }
            _snapGiven[target] = Time.unscaledTime;
            Session.Broadcast(new TeamMove { Kind = MoveKind.Snap, Target = target });
            Say("*shakes " + Coop.Player(target).Name + " by the shoulders*");
            Log.Info("[move] snapping " + Coop.Player(target).Name + " out of it");
        }

        /// <summary>The heaviest item a teammate holds in a hand, as their puppet shows it.</summary>
        internal static string HeavyHeld(byte id, out float weight)
        {
            weight = 0f;
            var held = Puppet(id)?.HeldIds;
            if (held == null) return null;
            string best = null;
            foreach (var itemId in held)
            {
                if (string.IsNullOrEmpty(itemId)) continue;
                var info = Item.GetItem(itemId);
                if (info != null && info.weight >= HeavyItem && info.weight > weight) { weight = info.weight; best = itemId; }
            }
            return best;
        }

        internal bool LiftingFor(byte id) => _liftingFor == id;

        internal void StartLifting(byte holder)
        {
            var me = GameInfo.LocalBody;
            string item = HeavyHeld(holder, out var weight);
            if (item == null) { CoopRuntime.Notice("They're not holding anything heavy."); return; }
            if (me == null || me.GetItem(me.handSlot) != null) { CoopRuntime.Notice("Empty your hand to help carry it."); return; }
            if (DistanceTo(holder) > LiftReach) { CoopRuntime.Notice("Get closer to help carry it."); return; }
            StopLifting(true);
            _liftingFor = holder;
            _liftWeight = weight;
            LiftLoad = weight * 0.5f;
            Session.Broadcast(new TeamMove { Kind = MoveKind.LiftStart, Target = holder, F = weight });
            CoopRuntime.Notice("Helping " + Coop.Player(holder).Name + " carry the " + Medic.Pretty(item));
            Log.Info("[move] helping " + Coop.Player(holder).Name + " carry " + item + " (" + weight + " u)");
        }

        internal void StopLifting(bool tell)
        {
            if (_liftingFor < 0) return;
            if (tell) Session?.Broadcast(new TeamMove { Kind = MoveKind.LiftStop, Target = (byte)_liftingFor });
            Log.Info("[move] stopped helping " + Coop.Player((byte)_liftingFor).Name + " carry");
            _liftingFor = -1;
            RecomputeLiftLoad();
        }

        private void RecomputeLiftLoad()
        {
            float load = _liftingFor >= 0 ? _liftWeight * 0.5f : 0f;
            foreach (var w in _helpers.Values) load -= w * 0.5f;
            LiftLoad = load;
        }

        // ================================================================== receiving

        private void OnMove(byte origin, TeamMove m)
        {
            if (origin == Coop.LocalId) return;
            string who = Coop.Player(origin).Name;
            var body = GameInfo.LocalBody;
            if (m.Kind == MoveKind.CatchReady)
            {
                _readyUntil[origin] = Time.unscaledTime + m.F;
                return;
            }
            if (m.Target != Coop.LocalId || body == null) return;
            switch (m.Kind)
            {
                case MoveKind.LegUp:
                    _legUpUntil = Time.unscaledTime + m.F;
                    _legUpFrom = origin;
                    CoopRuntime.Notice(who + " is giving you a leg-up: jump!");
                    break;
                case MoveKind.Pull:
                    TakeHand(body, new Vector2(m.X, m.Y), who);
                    break;
                case MoveKind.Snap:
                    if (!body.alive || !body.conscious) return;
                    bool again = Time.time - SnapAt < SnapDuration * 1.5f;
                    SnapBoost = again ? 20f : 40f;  // it works less the second time
                    SnapAt = Time.time;
                    Say("*snaps out of it*");
                    CoopRuntime.Notice(who + " snapped you out of it. For now.");
                    Log.Info("[move] snapped out of it by " + who + " (+" + SnapBoost + " mood, fading)");
                    break;
                case MoveKind.LiftStart:
                    var held = Medic.HeldItem(body);
                    if (held == null || held.totalWeight < HeavyItem * 0.75f) return;
                    _helpers[origin] = m.F;
                    RecomputeLiftLoad();
                    CoopRuntime.Notice(who + " is helping you carry the " + Medic.Pretty(held.id));
                    Log.Info("[move] " + who + " helps carry our " + held.id);
                    break;
                case MoveKind.LiftStop:
                    if (_helpers.Remove(origin)) { RecomputeLiftLoad(); CoopRuntime.Notice(who + " let go of your load"); }
                    if (_liftingFor == origin) { _liftingFor = -1; RecomputeLiftLoad(); }
                    break;
                case MoveKind.Caught:
                    HurtCatcher(body, m.F, who, true);
                    break;
                case MoveKind.LandedOn:
                    HurtCatcher(body, m.F, who, false);
                    break;
            }
        }

        private void TakeHand(Body body, Vector2 hand, string who)
        {
            if (!body.alive || !body.conscious || body.currentClimbable != null) return;
            DropRope();
            try
            {
                var go = new GameObject("CasualtiesCoop.Hand");
                var rope = go.AddComponent<Climbable>();
                rope.points = new List<Vector2> { body.transform.position, hand };
                rope.downwardsVelocity = -7f;  // negative: it carries you up without input
                rope.CalculateTotalLength();
                if (Climbable.allClimbables == null) Climbable.allClimbables = new List<Climbable>();
                _rope = rope;
                _ropeUntil = Time.unscaledTime + 6f;
                body.StartClimbing(rope);
                CoopRuntime.Notice(who + " is pulling you up");
                Log.Info("[move] pulled up by " + who + (body.currentClimbable == rope ? "" : " (couldn't grab)"));
            }
            catch (Exception e) { Log.Warn("[move] pull failed: " + e.Message); }
        }

        private void DropRope()
        {
            if (_rope == null) return;
            var body = GameInfo.LocalBody;
            if (body != null && body.currentClimbable == _rope) body.StopClimbing();
            UnityEngine.Object.Destroy(_rope.gameObject);
            _rope = null;
        }

        private static void HurtCatcher(Body body, float speed, string who, bool caught)
        {
            if (!body.alive) return;
            float js = Mathf.Max(1f, body.jumpSpeed);
            if (caught)
            {
                CoopRuntime.Notice("You caught " + who);
                Log.Info("[move] caught " + who + " falling at " + speed.ToString("F1"));
                if (speed < js * 2.2f) return;  // an easy catch
                // A long fall lands hard in the arms.
                foreach (int i in new[] { 3, 4, 6, 7 })
                    if (i < body.limbs.Length && body.limbs[i] != null) body.limbs[i].ImpactDamage(speed * 0.45f, Vector2.down);
                if (speed > js * 3.5f) body.Ragdoll();
            }
            else
            {
                CoopRuntime.Notice(who + " landed on you");
                Log.Info("[move] " + who + " landed on us at " + speed.ToString("F1"));
                foreach (int i in new[] { 0, 1 })
                    if (i < body.limbs.Length && body.limbs[i] != null) body.limbs[i].ImpactDamage(speed * 0.6f, Vector2.down);
                if (speed > js * 2.5f) body.Ragdoll();
            }
        }

        // ================================================================== every frame

        internal override void Tick()
        {
            var body = GameInfo.LocalBody;
            if (!Coop.WorldLive || body == null) return;
            float now = Time.unscaledTime;

            if (_rope != null && (now > _ropeUntil || body.currentClimbable != _rope)) DropRope();

            if (_liftingFor >= 0)
            {
                if (body.GetItem(body.handSlot) != null) StopLifting(true);
                else if (DistanceTo((byte)_liftingFor) > LiftReach * 1.6f) StopLifting(true);
                else if (HeavyHeld((byte)_liftingFor, out _) == null) StopLifting(true);
            }
            if (_helpers.Count > 0)
            {
                var held = Medic.HeldItem(body);
                if (held == null || held.totalWeight < HeavyItem * 0.75f)
                {
                    foreach (var h in new List<byte>(_helpers.Keys)) Session.Broadcast(new TeamMove { Kind = MoveKind.LiftStop, Target = h });
                    _helpers.Clear();
                    RecomputeLiftLoad();
                }
            }

            // Ready to catch while the key is held (and standing).
            var key = CoopPlugin.Instance.CatchKey.Value;
            if (!InputBlock.Blocking && Input.GetKey(key) && body.standing && body.conscious && now >= _nextReady)
            {
                _nextReady = now + 0.25f;
                Session.Broadcast(new TeamMove { Kind = MoveKind.CatchReady, F = 0.6f });
            }
            if (body.grounded) _caughtThisFall = false;
        }

        /// <summary>Every physics step on the faller's machine: about to land on a teammate?</summary>
        internal void FixedTick()
        {
            var body = GameInfo.LocalBody;
            if (!Coop.WorldLive || body == null || !body.alive || !body.standing || _caughtThisFall) return;
            var rb = body.rb;
            if (rb == null) return;
            float js = Mathf.Max(1f, body.jumpSpeed);
            float fall = -rb.velocity.y;
            if (fall < js * 1.3f) return;

            Vector2 feet = body.transform.position;
            foreach (var r in Coop.Get<PlayerSync>().Remotes)
            {
                var chest = r.LimbPosition(1) ?? r.ShownAt;
                if (!chest.HasValue || !Coop.Player(r.Id).Alive) continue;
                float dx = Mathf.Abs(feet.x - chest.Value.x);
                float dy = feet.y - chest.Value.y;
                if (dx > 0.9f || dy < 0f || dy > 2.4f) continue;

                bool ready = _readyUntil.TryGetValue(r.Id, out var until) && Time.unscaledTime < until;
                if (!ready && fall < js * 1.6f) continue;
                _caughtThisFall = true;
                float soft = ready ? Mathf.Min(fall, js * 0.8f) : fall * 0.6f;
                body.SetVelocity(new Vector2(rb.velocity.x * (ready ? 0.4f : 1f), -soft));
                Session.Broadcast(new TeamMove { Kind = ready ? MoveKind.Caught : MoveKind.LandedOn, Target = r.Id, F = fall });
                CoopRuntime.Notice(ready ? Coop.Player(r.Id).Name + " caught you" : "You landed on " + Coop.Player(r.Id).Name);
                Log.Info("[move] " + (ready ? "caught by " : "landed on ") + Coop.Player(r.Id).Name + " at " + fall.ToString("F1"));
                return;
            }
        }

        // ================================================================== hooks

        /// <summary>Called after the local player's Jump(): a leg-up makes it higher.</summary>
        internal static void AfterJump(Body body)
        {
            if (Time.unscaledTime > _legUpUntil || body.rb == null || body.rb.velocity.y <= 0.5f) return;
            if (DistanceTo(_legUpFrom) > LegUpReach + 0.6f) return;
            _legUpUntil = 0f;
            body.SetVelocity(new Vector2(body.rb.velocity.x, body.rb.velocity.y * LegUpMult));
            body.endedJump = false;
            Log.Info("[move] leg-up jump");
        }

        /// <summary>The snap-out-of-it bonus right now (fades over three minutes).</summary>
        internal static float SnapBonus
        {
            get
            {
                if (SnapBoost <= 0f) return 0f;
                float t = (Time.time - SnapAt) / SnapDuration;
                if (t >= 1f) { SnapBoost = 0f; return 0f; }
                return SnapBoost * (1f - t) * (1f - t);
            }
        }

        internal override void Gui()
        {
            if (Time.unscaledTime < _legUpUntil)
                UI.Overlay.Bottom(Coop.Player(_legUpFrom).Name + " is giving you a leg-up", "Jump next to them to go higher.", 1);
            if (_liftingFor >= 0)
                UI.Overlay.Bottom("Helping " + Coop.Player((byte)_liftingFor).Name + " carry", "You take half the weight. Stay close and keep your hand free.", 2);
        }
    }

    // ====================================================================== hooks into the game's movement

    /// <summary>Carried teammates and shared loads count toward the game's own encumbrance.</summary>
    [HarmonyPatch(typeof(Body), nameof(Body.GetTotalEncumberance))]
    internal static class EncumbrancePatch
    {
        private static void Postfix(Body __instance, ref float __result)
        {
            if (!Coop.WorldLive || !Coop.IsLocalBody(__instance)) return;
            __result = Mathf.Max(0f, __result + CarrySync.LocalLoad + TeamMotion.LiftLoad);
        }
    }

    /// <summary>A teammate on your back or in your arms costs jump height.</summary>
    [HarmonyPatch(typeof(Body), nameof(Body.actualJumpSpeed), MethodType.Getter)]
    internal static class JumpSpeedPatch
    {
        private static void Postfix(Body __instance, ref float __result)
        {
            if (CarrySync.LocalJumpMult >= 1f || !Coop.IsLocalBody(__instance)) return;
            __result *= CarrySync.LocalJumpMult;
        }
    }

    [HarmonyPatch(typeof(Body), nameof(Body.Jump))]
    internal static class LegUpPatch
    {
        private static void Postfix(Body __instance)
        {
            if (Coop.WorldLive && Coop.IsLocalBody(__instance)) TeamMotion.AfterJump(__instance);
        }
    }

    /// <summary>Being snapped out of it: a fading lift on the game's own mood total.</summary>
    [HarmonyPatch(typeof(Body), nameof(Body.totalHappiness), MethodType.Getter)]
    internal static class SnapMoodPatch
    {
        private static void Postfix(Body __instance, ref float __result)
        {
            if (TeamMotion.SnapBoost <= 0f || !Coop.IsLocalBody(__instance)) return;
            __result = Mathf.Clamp(__result + TeamMotion.SnapBonus, -100f, 100f);
        }
    }
}
