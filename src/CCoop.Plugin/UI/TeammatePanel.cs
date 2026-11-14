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
        private static GUIStyle _small, _warn, _title, _hint, _rowSel;

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
            _small = new GUIStyle(GUI.skin.label) { fontSize = 11, wordWrap = true };
            _small.normal.textColor = new Color(0.85f, 0.85f, 0.85f);
            _warn = new GUIStyle(_small) { fontStyle = FontStyle.Bold };
            _warn.normal.textColor = new Color(1f, 0.55f, 0.45f);
            _title = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold };
            _hint = new GUIStyle(GUI.skin.box) { fontSize = 12 };
            _rowSel = new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleLeft, fontSize = 11 };
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
                    GUI.Box(new Rect(m.x + 18f, m.y + 10f, 280f, 24f), Coop.Player((byte)_hovered).Name + state + "  (right-click)", _hint);
                }
                if (_open < 0) return;
                _rect.x = Screen.width / k - 420f;
                _rect.height = 0;
                _rect = GUILayout.Window(0x0C010, _rect, id => Body((byte)_open), Coop.Player((byte)_open).Name, GUILayout.Width(400));
            }
            finally { GUI.matrix = old; }
        }

        private static string Status(Vitals v)
        {
            if ((v.Signs & BodySigns.Alive) == 0) return "dead " + v.DeadFor + " s";
            if ((v.Signs & BodySigns.Arrest) != 0) return "heart stopped";
            if ((v.Signs & BodySigns.Dying) != 0) return "dying";
            if ((v.Signs & BodySigns.Conscious) == 0) return (v.Signs & BodySigns.Sleeping) != 0 ? "asleep" : "out cold";
            if ((v.Signs & BodySigns.Bleeding) != 0) return "bleeding";
            return "ok";
        }

        private void Body(byte id)
        {
            var v = Coop.Get<VitalsSync>()?.Of(id);
            var puppet = Coop.Get<PlayerSync>()?.Find(id);
            if (v == null)
            {
                GUILayout.Label("No reading: they're not on this layer.", _small);
                if (GUILayout.Button("Close")) _open = -1;
                GUI.DragWindow();
                return;
            }

            bool alive = (v.Signs & BodySigns.Alive) != 0;
            GUILayout.Label(Status(v), _title);
            GUILayout.Label("Blood " + v.Blood + "% · Oxygen " + v.Oxygen + "% · Pulse " + v.Heart + " · Pressure " + v.Pressure +
                            " · Temp " + (20f + v.Temperature / 10f).ToString("F1") + "°C · Brain " + v.Brain + "% · Pain " + v.Pain, _small);
            foreach (var w in Warnings(v)) GUILayout.Label(w, _warn);

            // Limbs: worst first is less useful than body order; mark the troubled ones.
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(Mathf.Min(24f * v.Limbs.Count + 6f, 230f)));
            for (int i = 0; i < v.Limbs.Count; i++)
            {
                var l = v.Limbs[i];
                string issues = LimbIssues(l);
                string label = (puppet != null ? puppet.LimbName(i) : "Limb " + i) + "  ·  skin " + l.Skin + " · muscle " + l.Muscle +
                               (issues.Length > 0 ? "  ·  " + issues : "");
                var c = GUI.color;
                if (issues.Length > 0) GUI.color = new Color(1f, 0.8f, 0.7f);
                if (GUILayout.Toggle(_limb == i, label, _rowSel, GUILayout.Height(22)) && _limb != i) _limb = i;
                GUI.color = c;
            }
            GUILayout.EndScrollView();

            GUILayout.Space(4);
            Actions(id, v, alive);

            GUILayout.Space(4);
            if (GUILayout.Button("Close")) _open = -1;
            GUI.DragWindow();
        }

        private static List<string> Warnings(Vitals v)
        {
            var list = new List<string>();
            var s = v.Signs;
            if ((s & BodySigns.Alive) == 0)
            {
                int left = Mathf.RoundToInt(Medic.ReviveWindow) - v.DeadFor;
                list.Add(left > 0 ? "Dead. A defibrillator might still bring them back (" + left + " s left)." : "Dead. Too late to bring back on this layer.");
                return list;
            }
            if ((s & BodySigns.Arrest) != 0) list.Add("Heart stopped: hold CPR to keep the brain alive, and defibrillate.");
            else if ((s & BodySigns.Fibrillating) != 0) list.Add("Heart fibrillating (" + v.Fibrillation + "%): an AED can reset it.");
            if ((s & BodySigns.Bleeding) != 0) list.Add("Bleeding.");
            if ((s & BodySigns.Dying) != 0) list.Add("Dying.");
            if ((s & BodySigns.Cold) != 0) list.Add("Hypothermic.");
            if ((s & BodySigns.Fever) != 0) list.Add("Feverish.");
            if ((s & BodySigns.Thirsty) != 0) list.Add("Thirsty.");
            if ((s & BodySigns.Hungry) != 0) list.Add("Hungry.");
            return list;
        }

        private static string LimbIssues(LimbVitals l)
        {
            var parts = new List<string>();
            var s = l.Signs;
            if ((s & LimbSigns.Missing) != 0) return "missing";
            if (l.Bleed > 0 && (s & LimbSigns.BleedBlocked) == 0) parts.Add("bleeding");
            if ((s & LimbSigns.Broken) != 0) parts.Add("broken");
            if ((s & LimbSigns.Dislocated) != 0) parts.Add("dislocated");
            if ((s & LimbSigns.Infected) != 0) parts.Add("infected");
            if ((s & LimbSigns.Shrapnel) != 0) parts.Add("shrapnel");
            if ((s & LimbSigns.Tourniquet) != 0) parts.Add("tourniquet");
            if ((s & LimbSigns.Splinted) != 0) parts.Add("splint");
            if ((s & LimbSigns.Bandaged) != 0) parts.Add("bandaged");
            if ((s & LimbSigns.Prosthetic) != 0) parts.Add("prosthetic");
            return string.Join(", ", parts.ToArray());
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
                GUILayout.Label(cant, _small);
                if (carry != null && carry.Carrying(id) && GUILayout.Button("Put down")) carry.PutDown();
                // A teammate below a ledge can still be reached for.
                var me = GameInfo.LocalBody;
                var rec = Coop.Player(id);
                if (motion != null && me != null && alive && (v.Signs & BodySigns.Conscious) != 0 && rec.HasPosition &&
                    rec.Position.y < me.transform.position.y - 1f &&
                    Vector2.Distance(me.transform.position, rec.Position) <= TeamMotion.PullReach &&
                    GUILayout.Button("Reach down and pull them up")) motion.PullUp(id);
                return;
            }

            var loot = Coop.Get<LootSync>()?.Of(id);
            if (loot != null)
            {
                GUILayout.Label(loot.Count == 0 ? "Nothing on them to take." : "On their body (not hands or mouth):", _small);
                foreach (var e in loot)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(Medic.Pretty(e.ItemId), _small);
                    if (GUILayout.Button("Take", GUILayout.Width(60))) Coop.Get<LootSync>().Take(id, e.Key);
                    GUILayout.EndHorizontal();
                }
                GUILayout.Space(4);
            }

            var held = Medic.HeldItem(GameInfo.LocalBody);
            GUILayout.Label("In your hand: " + (held != null ? Medic.Pretty(held.id) : "nothing"), _small);
            bool busy = medic != null && medic.Busy;
            GUI.enabled = !busy;

            if (held != null && Medic.IsDefibrillator(held))
            {
                if (GUILayout.Button(alive ? "Shock their heart with the " + Medic.Pretty(held.id) : "Shock them (try to bring them back)", GUILayout.Height(26)))
                    medic.Treat(id, 1, TreatKind.Item);
            }
            else if (_limb >= 0 && _limb < v.Limbs.Count)
            {
                var l = v.Limbs[_limb];
                string limbName = Coop.Get<PlayerSync>()?.Find(id)?.LimbName(_limb) ?? "limb";
                string cantUse = Medic.CantUse(held);
                if (held != null)
                {
                    GUI.enabled = !busy && cantUse == null && alive;
                    if (GUILayout.Button("Use " + Medic.Pretty(held.id) + " on " + limbName, GUILayout.Height(26))) medic.Treat(id, _limb, TreatKind.Item);
                    GUI.enabled = !busy;
                    if (cantUse != null) GUILayout.Label(cantUse, _small);
                }
                if ((l.Signs & LimbSigns.Dislocated) != 0 && GUILayout.Button("Set the dislocated " + limbName)) medic.Treat(id, _limb, TreatKind.SetDislocation);
                if ((l.Signs & LimbSigns.Shrapnel) != 0 && (held == null || held.id != "tweezers") && GUILayout.Button("Pull the shrapnel out by hand"))
                    medic.Treat(id, _limb, TreatKind.PullShrapnel);
                if ((l.Signs & LimbSigns.Tourniquet) != 0 && GUILayout.Button("Take off the tourniquet")) medic.Treat(id, _limb, TreatKind.RemoveTourniquet);
                if ((l.Signs & LimbSigns.Splinted) != 0 && GUILayout.Button("Take off the splint")) medic.Treat(id, _limb, TreatKind.RemoveSplint);
            }
            else GUILayout.Label("Pick a limb above to treat it.", _small);

            if (alive && ((v.Signs & BodySigns.Arrest) != 0 || v.Pressure < 30))
            {
                if (GUILayout.RepeatButton("Hold for CPR", GUILayout.Height(28)) && Time.unscaledTime >= _nextCpr)
                {
                    _nextCpr = Time.unscaledTime + 0.5f;
                    medic.Cpr(id);
                }
            }

            GUI.enabled = true;
            if (held != null && give != null && alive && GUILayout.Button("Give " + Medic.Pretty(held.id))) give.Offer(id);
            AskRow(id, give, alive && (v.Signs & BodySigns.Conscious) != 0);

            // Carrying the downed: three ways, each with its own cost.
            if (carry != null)
            {
                if (carry.Carrying(id))
                {
                    if (GUILayout.Button("Put them down", GUILayout.Height(26))) carry.PutDown();
                }
                else if (CarrySync.Downed(id))
                {
                    GUILayout.Label("Carry them:", _small);
                    GUILayout.BeginHorizontal();
                    CarryButton(carry, id, CarryMode.Drag, "Drag");
                    CarryButton(carry, id, CarryMode.Back, "On your back");
                    CarryButton(carry, id, CarryMode.Arms, "In your arms");
                    GUILayout.EndHorizontal();
                }
            }

            // Moving together.
            if (motion != null && alive && (v.Signs & BodySigns.Conscious) != 0)
            {
                GUILayout.BeginHorizontal();
                if ((v.Signs & BodySigns.Standing) != 0 && GUILayout.Button("Give a leg-up")) motion.GiveLegUp(id);
                var me = GameInfo.LocalBody;
                var rec = Coop.Player(id);
                if (me != null && rec.Position.y < me.transform.position.y - 1f && GUILayout.Button("Pull them up")) motion.PullUp(id);
                GUILayout.EndHorizontal();

                if (v.Mood <= -50)
                {
                    string snapWhy = motion.CantSnap(id);
                    GUI.enabled = snapWhy == null;
                    if (GUILayout.Button("Snap them out of it (mood " + v.Mood + ")")) motion.Snap(id);
                    GUI.enabled = true;
                    GUILayout.Label(snapWhy ?? "Lifts them for a few minutes. It won't fix what's wrong.", _small);
                }

                string heavy = TeamMotion.HeavyHeld(id, out var weight);
                if (motion.LiftingFor(id))
                {
                    if (GUILayout.Button("Stop helping carry")) motion.StopLifting(true);
                }
                else if (heavy != null && GUILayout.Button("Help carry their " + Medic.Pretty(heavy) + " (" + weight.ToString("0") + " u)"))
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
                GUILayout.Label("Waiting for them to answer…", _small);
                return;
            }
            _showAsk = GUILayout.Toggle(_showAsk, " Ask them for…");
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
