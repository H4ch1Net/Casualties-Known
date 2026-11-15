using System.Collections.Generic;
using CCoop.Plugin.Protocol;
using CCoop.Plugin.Runtime;
using CCoop.Plugin.Sync;
using UnityEngine;

namespace CCoop.Plugin.UI
{
    /// <summary>
    /// Hover a teammate to see who it is; right-click to open their panel: vitals, every
    /// limb's wounds and treatments, and what you can do — use the item in your hand on a
    /// limb, set a dislocation, take off a tourniquet or splint, CPR, give, carry.
    /// Puppets get no colliders (traps and turrets would find them), so hovering is
    /// worked out here from the puppet's limb positions.
    /// </summary>
    internal sealed class TeammatePanel : CoopModule
    {
        private const float HoverReach = 0.8f;

        private int _hovered = -1;
        private int _open = -1;
        private int _limb = -1;
        private float _nextCpr;
        private Rect _rect = new Rect(0, 80, 400, 10);
        private Vector2 _scroll;
        private static GUIStyle _small, _warn, _title, _hint, _rowSel, _status, _limbTitle;

        internal static void Open(byte id)
        {
            var p = Coop.Get<TeammatePanel>();
            if (p == null) return;
            p._open = id;
            p._limb = -1;
        }

        internal override void Detach()
        {
            _open = -1;
            _hovered = -1;
        }

        internal override void OnPeerLeft(CCoop.Net.Session.PeerInfo peer)
        {
            if (_open == peer.Id) _open = -1;
        }

        internal override void Tick()
        {
            _hovered = -1;
            if (!Coop.WorldLive) return;
            var cam = Camera.main;
            var ps = Coop.Get<PlayerSync>();
            if (cam == null || ps == null) return;
            Vector2 mouse = cam.ScreenToWorldPoint(Input.mousePosition);
            foreach (var r in ps.Remotes)
                if (r.NearestLimb(mouse, HoverReach) >= 0) { _hovered = r.Id; break; }
            if (_hovered < 0 || InputBlock.Blocking || PauseHandler.paused) return;

            // Right-click (the game's "interact" key), unless the game has something of its own under the cursor.
            if (Input.GetKeyDown(KeyBinds.GetBind("iteminteract")) &&
                Physics2D.OverlapPoint(mouse, LayerMask.GetMask("Item", "Descriptor")) == null)
            {
                _open = _hovered;
                _limb = -1;
                Log.Info("[care] opened " + Coop.Player((byte)_open).Name + "'s panel");
            }
        }

        private static void Styles()
        {
            if (_small != null) return;
            _small = new GUIStyle(GUI.skin.label) { fontSize = UiSkin.Small, wordWrap = true };
            _small.normal.textColor = UiSkin.Dim;
            _warn = new GUIStyle(_small);
            _warn.normal.textColor = UiSkin.Bad;
            _title = new GUIStyle(GUI.skin.label) { fontSize = UiSkin.Head };
            _hint = new GUIStyle(GUI.skin.box) { fontSize = UiSkin.Small };
            _rowSel = new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleLeft, fontSize = UiSkin.Small };
            _status = new GUIStyle(GUI.skin.label) { fontSize = UiSkin.Big };
            _limbTitle = new GUIStyle(GUI.skin.label) { fontSize = UiSkin.Body };
            _limbTitle.normal.textColor = UiSkin.Amber;
        }

        internal override void Gui()
        {
            if (!Coop.WorldLive) { _open = -1; return; }
            if (MedicSession.Active) return;  // the minigame has the screen
            Styles();
            float k = CoopWindow.Scale;
            var old = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(k, k, 1f));
            try
            {
                if (_hovered >= 0 && _open != _hovered)
                {
                    var m = Event.current.mousePosition;
                    var v = Coop.Get<VitalsSync>()?.Of((byte)_hovered);
                    string state = v == null ? "" : " · " + Status(v);
                    string hint = Coop.Player((byte)_hovered).Name + state;
                    var hs = UiSkin.Fit(_hint, hint);
                    var size = hs.CalcSize(new GUIContent(hint));
                    GUI.Box(new Rect(m.x + 18f, m.y + 10f, size.x + 4f, size.y + 2f), hint, hs);
                }
                if (_open < 0) return;
                _rect.x = Screen.width / k - 440f;
                _rect.height = 0;
                string name = Coop.Player((byte)_open).Name;
                _rect = GUILayout.Window(0x0C010, _rect, id => Body((byte)_open), name, UiSkin.Fit(GUI.skin.window, name), GUILayout.Width(420));
                UiMouse.Claim(_rect);
            }
            finally { GUI.matrix = old; }
        }

        private static string Status(Vitals v)
        {
            if ((v.Signs & BodySigns.Alive) == 0) return Lang.F("dead {0} s", v.DeadFor);
            if ((v.Signs & BodySigns.Arrest) != 0) return Lang.T("heart stopped");
            if ((v.Signs & BodySigns.Dying) != 0) return Lang.T("dying");
            if ((v.Signs & BodySigns.Conscious) == 0) return (v.Signs & BodySigns.Sleeping) != 0 ? Lang.T("asleep") : Lang.T("out cold");
            if ((v.Signs & BodySigns.Bleeding) != 0) return Lang.T("bleeding");
            return Lang.T("ok");
        }

        private int _hoverLimb = -1;

        private void Body(byte id)
        {
            var v = Coop.Get<VitalsSync>()?.Of(id);
            var puppet = Coop.Get<PlayerSync>()?.Find(id);
            if (v == null)
            {
                GUILayout.Label(Lang.T("They're not on this layer."), _small);
                if (GUILayout.Button(Lang.T("Close"))) _open = -1;
                GUI.DragWindow();
                return;
            }

            bool alive = (v.Signs & BodySigns.Alive) != 0;
            // What anyone can see at a glance; the numbers are the chip's, and only theirs.
            _status.normal.textColor = StatusColor(v);
            GUILayout.Label(Status(v), _status);
            foreach (var w in Warnings(v)) GUILayout.Label(w, _warn);

            if (BodyFigure.Ready)
            {
                const float fw = 380f;
                float fh = Mathf.Min(BodyFigure.HeightFor(fw), 330f);
                var r = GUILayoutUtility.GetRect(fw, fh, GUILayout.ExpandWidth(true), GUILayout.Height(fh));
                int hover = BodyFigure.Draw(r, v, Coop.Player(id).Character, _limb);
                if (Event.current.type == EventType.Repaint) _hoverLimb = hover;
                if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && r.Contains(Event.current.mousePosition))
                {
                    _limb = _hoverLimb >= 0 && _hoverLimb != _limb ? _hoverLimb : -1;
                    Event.current.Use();
                }
            }
            else LimbList(v, puppet);
            GUILayout.BeginVertical();
            int shown = _hoverLimb >= 0 ? _hoverLimb : _limb;
            if (shown >= 0 && shown < v.Limbs.Count)
            {
                GUILayout.Label(puppet != null ? puppet.LimbName(shown) : Lang.F("Limb {0}", shown), _limbTitle);
                string issues = LimbIssues(v.Limbs[shown]);
                GUILayout.Label(issues.Length > 0 ? issues : Lang.T("nothing wrong you can see"), _small);
                GUILayout.Space(6);
            }
            LimbColumn(id, v, alive);
            GUILayout.EndVertical();

            GUILayout.Space(4);
            Actions(id, v, alive);

            GUILayout.Space(4);
            if (GUILayout.Button(Lang.T("Close"))) _open = -1;
            GUI.DragWindow();
        }

        /// <summary>Without the game's health view (it shouldn't happen in a run): the limbs as a list.</summary>
        private void LimbList(Vitals v, RemotePlayer puppet)
        {
            GUILayout.BeginVertical(GUILayout.Width(176f));
            for (int i = 0; i < v.Limbs.Count; i++)
            {
                string label = (puppet != null ? puppet.LimbName(i) : Lang.F("Limb {0}", i));
                if (LimbIssues(v.Limbs[i]).Length > 0) label += " *";
                if (GUILayout.Toggle(_limb == i, label, _rowSel, GUILayout.Height(20)) && _limb != i) _limb = i;
            }
            GUILayout.EndVertical();
        }

        private static Color StatusColor(Vitals v)
        {
            var s = v.Signs;
            if ((s & BodySigns.Alive) == 0 || (s & BodySigns.Arrest) != 0 || (s & BodySigns.Dying) != 0) return UiSkin.Bad;
            if ((s & BodySigns.Conscious) == 0 || (s & BodySigns.Bleeding) != 0) return UiSkin.Amber;
            return UiSkin.Ink;
        }

        private static List<string> Warnings(Vitals v)
        {
            var list = new List<string>();
            var s = v.Signs;
            if ((s & BodySigns.Alive) == 0)
            {
                int left = Mathf.RoundToInt(Medic.ReviveWindow) - v.DeadFor;
                list.Add(left > 0 ? Lang.F("A defibrillator might still bring them back ({0} s).", left) : Lang.T("Too late to bring back on this layer."));
                return list;
            }
            if ((s & BodySigns.Arrest) != 0) list.Add(Lang.T("Heart stopped: hold CPR and defibrillate."));
            return list;
        }

        private static string LimbIssues(LimbVitals l)
        {
            var parts = new List<string>();
            var s = l.Signs;
            if ((s & LimbSigns.Missing) != 0) return Lang.T("missing");
            if (l.Bleed > 0 && (s & LimbSigns.BleedBlocked) == 0) parts.Add(Lang.T("bleeding"));
            if ((s & LimbSigns.Broken) != 0) parts.Add(Lang.T("broken"));
            if ((s & LimbSigns.Dislocated) != 0) parts.Add(Lang.T("dislocated"));
            if ((s & LimbSigns.Infected) != 0) parts.Add(Lang.T("infected"));
            if ((s & LimbSigns.Shrapnel) != 0) parts.Add(Lang.T("shrapnel"));
            if ((s & LimbSigns.Tourniquet) != 0) parts.Add(Lang.T("tourniquet"));
            if ((s & LimbSigns.Splinted) != 0) parts.Add(Lang.T("splint"));
            if ((s & LimbSigns.Bandaged) != 0) parts.Add(Lang.T("bandaged"));
            if ((s & LimbSigns.Prosthetic) != 0) parts.Add(Lang.T("prosthetic"));
            return string.Join(", ", parts.ToArray());
        }

        /// <summary>Beside the figure: what you can do for the chosen limb (and the heart).</summary>
        private void LimbColumn(byte id, Vitals v, bool alive)
        {
            var medic = Coop.Get<Medic>();
            string cant = Medic.CantTreat(id);
            if (cant != null) { GUILayout.Label(cant, _small); return; }
            var held = Medic.HeldItem(GameInfo.LocalBody);
            if (held != null) GUILayout.Label(Lang.F("In your hand: {0}", Medic.Pretty(held.id)), _small);
            bool busy = medic != null && medic.Busy;
            GUI.enabled = !busy;

            if (held != null && Medic.IsDefibrillator(held))
            {
                if (GUILayout.Button(alive ? Lang.F("Shock their heart with the {0}", Medic.Pretty(held.id)) : Lang.T("Shock them (try to bring them back)"), GUILayout.Height(26)))
                    medic.Treat(id, 1, TreatKind.Item);
            }
            else if (_limb >= 0 && _limb < v.Limbs.Count)
            {
                var l = v.Limbs[_limb];
                string limbName = Coop.Get<PlayerSync>()?.Find(id)?.LimbName(_limb) ?? Lang.T("limb");
                string cantUse = Medic.CantUse(held);
                if (held != null)
                {
                    GUI.enabled = !busy && cantUse == null && alive;
                    if (GUILayout.Button(Lang.F("Use {0}", Medic.Pretty(held.id)), GUILayout.Height(26))) medic.Treat(id, _limb, TreatKind.Item);
                    GUI.enabled = !busy;
                    if (cantUse != null) GUILayout.Label(cantUse, _small);
                }
                if ((l.Signs & LimbSigns.Dislocated) != 0 && GUILayout.Button(Lang.T("Set the joint"))) medic.Treat(id, _limb, TreatKind.SetDislocation);
                if ((l.Signs & LimbSigns.Shrapnel) != 0 && (held == null || held.id != "tweezers") && GUILayout.Button(Lang.T("Pull out the shrapnel")))
                    medic.Treat(id, _limb, TreatKind.PullShrapnel);
                if ((l.Signs & LimbSigns.Tourniquet) != 0 && GUILayout.Button(Lang.T("Take off the tourniquet"))) medic.Treat(id, _limb, TreatKind.RemoveTourniquet);
                if ((l.Signs & LimbSigns.Splinted) != 0 && GUILayout.Button(Lang.T("Take off the splint"))) medic.Treat(id, _limb, TreatKind.RemoveSplint);
            }
            else GUILayout.Label(Lang.T("Click a limb to treat it."), _small);

            if (alive && ((v.Signs & BodySigns.Arrest) != 0 || v.Pressure < 30))
            {
                if (GUILayout.RepeatButton(Lang.T("Hold for CPR"), GUILayout.Height(28)) && Time.unscaledTime >= _nextCpr)
                {
                    _nextCpr = Time.unscaledTime + 0.5f;
                    medic.Cpr(id);
                }
            }

            GUI.enabled = true;
        }

        private void Actions(byte id, Vitals v, bool alive)
        {
            var medic = Coop.Get<Medic>();
            var give = Coop.Get<GiveSync>();
            var carry = Coop.Get<CarrySync>();
            var motion = Coop.Get<TeamMotion>();
            string cant = Medic.CantTreat(id);
            if (cant != null)
            {
                if (carry != null && carry.Carrying(id) && GUILayout.Button(Lang.T("Put down"))) carry.PutDown();
                // A teammate below a ledge can still be reached for.
                var me = GameInfo.LocalBody;
                var rec = Coop.Player(id);
                if (motion != null && me != null && alive && (v.Signs & BodySigns.Conscious) != 0 && rec.HasPosition &&
                    rec.Position.y < me.transform.position.y - 1f &&
                    Vector2.Distance(me.transform.position, rec.Position) <= TeamMotion.PullReach &&
                    GUILayout.Button(Lang.T("Reach down and pull them up"))) motion.PullUp(id);
                return;
            }

            var loot = Coop.Get<LootSync>()?.Of(id);
            if (loot != null)
            {
                GUILayout.Label(loot.Count == 0 ? Lang.T("Nothing on them to take.") : Lang.T("On their body (not hands or mouth):"), _small);
                foreach (var e in loot)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(Medic.Pretty(e.ItemId), _small);
                    if (GUILayout.Button(Lang.T("Take"), GUILayout.MinWidth(60), GUILayout.ExpandWidth(false))) Coop.Get<LootSync>().Take(id, e.Key);
                    GUILayout.EndHorizontal();
                }
                GUILayout.Space(4);
            }

            var held = Medic.HeldItem(GameInfo.LocalBody);
            if (held != null && give != null && alive && GUILayout.Button(Lang.F("Give {0}", Medic.Pretty(held.id)))) give.Offer(id);
            AskRow(id, give, alive && (v.Signs & BodySigns.Conscious) != 0);

            // Carrying: three ways, each with its own cost. Someone awake is asked first.
            if (carry != null)
            {
                if (carry.Carrying(id))
                {
                    if (GUILayout.Button(Lang.T("Put them down"), GUILayout.Height(26))) carry.PutDown();
                }
                else if (carry.CarriedBy == id)
                {
                    GUILayout.Label(Lang.T("They're carrying you."), _small);
                }
                else if (carry.Offered(id))
                {
                    GUILayout.Label(Lang.F("Waiting for {0} to climb on...", Coop.Player(id).Name), _small);
                }
                else
                {
                    bool down = CarrySync.Downed(id);
                    GUILayout.Label(down ? Lang.T("Carry them:") : Lang.T("Offer to carry them (they can say no):"), _small);
                    GUILayout.BeginHorizontal();
                    CarryButton(carry, id, CarryMode.Drag, Lang.T("Drag"));
                    CarryButton(carry, id, CarryMode.Back, Lang.T("On your back"));
                    CarryButton(carry, id, CarryMode.Arms, Lang.T("In your arms"));
                    GUILayout.EndHorizontal();
                }
            }

            // Waking someone, instead of hitting them.
            if (motion != null && alive && (v.Signs & BodySigns.Conscious) == 0)
            {
                bool asleep = (v.Signs & BodySigns.Sleeping) != 0;
                string wakeWhy = motion.CantWake(id);
                GUI.enabled = wakeWhy == null;
                if (GUILayout.Button(asleep ? Lang.T("Wake them up") : Lang.T("Shake them"))) motion.Wake(id);
                GUI.enabled = true;
                GUILayout.Label(wakeWhy ?? (asleep ? Lang.T("They wake as if they'd slept (a short sleep is a poor one).")
                                                   : Lang.T("Helps them come round, if their body lets them.")), _small);
            }

            // Moving together.
            if (motion != null && alive && (v.Signs & BodySigns.Conscious) != 0)
            {
                GUILayout.BeginHorizontal();
                if ((v.Signs & BodySigns.Standing) != 0 && GUILayout.Button(Lang.T("Give a leg-up"))) motion.GiveLegUp(id);
                var me = GameInfo.LocalBody;
                var rec = Coop.Player(id);
                if (me != null && rec.Position.y < me.transform.position.y - 1f && GUILayout.Button(Lang.T("Pull them up"))) motion.PullUp(id);
                GUILayout.EndHorizontal();

                if (v.Mood <= -50)
                {
                    string snapWhy = motion.CantSnap(id);
                    GUI.enabled = snapWhy == null;
                    if (GUILayout.Button(Lang.F("Snap them out of it (mood {0})", v.Mood))) motion.Snap(id);
                    GUI.enabled = true;
                    GUILayout.Label(snapWhy ?? Lang.T("Lifts them for a few minutes. It won't fix what's wrong."), _small);
                }

                string heavy = TeamMotion.HeavyHeld(id, out var weight);
                if (motion.LiftingFor(id))
                {
                    if (GUILayout.Button(Lang.T("Stop helping carry"))) motion.StopLifting(true);
                }
                else if (heavy != null && GUILayout.Button(Lang.F("Help carry their {0} ({1} u)", Medic.Pretty(heavy), weight.ToString("0"))))
                    motion.StartLifting(id);
            }
        }

        private static bool _showAsk;

        /// <summary>"Ask for…": what they visibly carry, one button each. They choose whether to hand it over.</summary>
        private static void AskRow(byte id, GiveSync give, bool awake)
        {
            var kit = Coop.Get<PlayerSync>()?.Find(id)?.Kit;
            if (give == null || !awake || kit == null) return;
            bool any = false;
            foreach (var k in kit) if (!string.IsNullOrEmpty(k)) { any = true; break; }
            if (!any) return;
            if (give.Asking(id))
            {
                GUILayout.Label(Lang.T("Waiting for them to answer…"), _small);
                return;
            }
            _showAsk = GUILayout.Toggle(_showAsk, " " + Lang.T("Ask them for…"));
            if (!_showAsk) return;
            for (int slot = 0; slot < kit.Length; slot++)
            {
                if (string.IsNullOrEmpty(kit[slot])) continue;
                if (GUILayout.Button(Medic.Pretty(kit[slot]))) { give.AskFor(id, slot, kit[slot]); _showAsk = false; }
            }
        }

        private static void CarryButton(CarrySync carry, byte id, CarryMode mode, string label)
        {
            string why = CarrySync.CantCarry(mode);
            GUI.enabled = why == null;
            if (GUILayout.Button(new GUIContent(label, why ?? ""))) carry.Pick(id, mode);
            GUI.enabled = true;
        }
    }
}
