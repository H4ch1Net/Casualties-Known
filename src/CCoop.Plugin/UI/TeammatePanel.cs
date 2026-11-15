using System.Collections.Generic;
using CCoop.Plugin.Protocol;
using CCoop.Plugin.Runtime;
using CCoop.Plugin.Sync;
using UnityEngine;

namespace CCoop.Plugin.UI
{
    /// <summary>
    /// Hover a teammate to see who it is; right-click them for a small menu beside them
    /// with what you can do (give, ask for, carry, wake, leg-up...). "Health" in it opens
    /// their health panel: their body in the game's own health view, and for the limb you
    /// pick, what's wrong with it and how to treat it (the item in your hand, setting a
    /// joint, taking off a tourniquet or splint), and CPR or a shock for the heart.
    /// Puppets get no colliders (traps and turrets would find them), so hovering is
    /// worked out here from the puppet's limb positions.
    /// </summary>
    internal sealed class TeammatePanel : CoopModule
    {
        private const float HoverReach = 0.8f;
        /// <summary>The panel's width; the figure fills it (it is wider than tall: arms out).</summary>
        private const float PanelWidth = 380f;
        private const float FigureMaxHeight = 320f;

        private int _hovered = -1;
        private int _open = -1;
        private int _limb = -1;
        private int _hoverLimb = -1;
        private float _nextCpr;
        private Rect _rect = new Rect(0, 80, 10, 10);
        private static GUIStyle _small, _warn, _rowSel, _status, _limbTitle, _item, _menuWindow, _menuNote;
        private static Texture2D _inset;

        // The menu beside a teammate.
        private int _menu = -1;
        private int _menuFrame;
        private int _sub;           // 0 none, 1 "Ask for…", 2 "Search them"
        private Rect _menuRect;
        private bool _closing;
        private bool _claims;

        private static TeammatePanel _self;

        internal TeammatePanel() { _self = this; }

        /// <summary>The menu beside a teammate is up (the game's click and pause keys stay off).</summary>
        internal static bool MenuUp => _self != null && _self._menu >= 0;

        /// <summary>The menu or the health panel is up: Esc closes them instead of pausing.</summary>
        internal static bool Captures => _self != null && (_self._menu >= 0 || _self._open >= 0 || _self._closing);

        /// <summary>
        /// This right-click is on a teammate: it opens their menu, and the game doesn't also
        /// act on it (your own body under them would open your inventory).
        /// </summary>
        internal static bool ClaimsPress => _self != null && _self._claims && Input.GetKeyDown(KeyBindsBlockPatch.Raw("iteminteract"));

        /// <summary>
        /// The game's own hover label for a teammate under the mouse, as it shows "You" over
        /// your own body: their name, and the right-click icon with what it does.
        /// </summary>
        internal static bool HoverTip(out (string, string) tip)
        {
            tip = ("", "");
            var p = _self;
            if (p == null || p._hovered < 0 || p._menu == p._hovered || UiMouse.Over || !Coop.WorldLive) return false;
            var id = (byte)p._hovered;
            string desc = "<sprite index=20>" + Lang.T("Interact");
            var v = Coop.Get<VitalsSync>()?.Of(id);
            if (v != null)
            {
                string state = Status(v);
                if (state != Lang.T("ok")) desc = "<color=#" + ColorUtility.ToHtmlStringRGB(StatusColor(v)) + ">" + state + "</color>\n" + desc;
            }
            tip = (Coop.Player(id).Name, desc);
            return true;
        }

        /// <summary>Self-test screenshots: the menu on a teammate (with a sub-list open), or nothing.</summary>
        internal static void DevMenu(int id, int sub)
        {
            if (_self == null) return;
            _self._menu = id;
            _self._sub = sub;
            _self._menuFrame = Time.frameCount;
            if (id < 0) _self._open = -1;
        }

        internal static void Open(byte id)
        {
            var p = Coop.Get<TeammatePanel>();
            if (p == null) return;
            p._open = id;
            p._limb = -1;
            p._menu = -1;
        }

        internal override void Detach()
        {
            _open = -1;
            _hovered = -1;
            _menu = -1;
        }

        internal override void OnPeerLeft(CCoop.Net.Session.PeerInfo peer)
        {
            if (_open == peer.Id) _open = -1;
            if (_menu == peer.Id) _menu = -1;
        }

        internal override void Tick()
        {
            _closing = false;
            _claims = false;
            _hovered = -1;
            if (!Coop.WorldLive) { _menu = -1; return; }
            var cam = Camera.main;
            var ps = Coop.Get<PlayerSync>();
            if (cam == null || ps == null) return;
            Vector2 mouse = cam.ScreenToWorldPoint(Input.mousePosition);
            foreach (var r in ps.Remotes)
                if (r.NearestLimb(mouse, HoverReach) >= 0) { _hovered = r.Id; break; }
            if (_hovered < 0 || InputBlock.HoldGame || PauseHandler.paused || UiMouse.Over) return;

            // Right-click (the game's "interact" key), unless the game has something of its own under the cursor.
            if (Physics2D.OverlapPoint(mouse, LayerMask.GetMask("Item", "Descriptor")) != null) return;
            _claims = true;
            if (Input.GetKeyDown(KeyBindsBlockPatch.Raw("iteminteract")))
            {
                _menu = _hovered;
                _menuFrame = Time.frameCount;
                _sub = 0;
                _menuRect = new Rect(0, 0, 0, 0);
                Log.Info("[care] menu for " + Coop.Player((byte)_menu).Name);
            }
        }

        private static void Styles()
        {
            if (_small != null) return;
            _small = new GUIStyle(GUI.skin.label) { fontSize = UiSkin.Small, wordWrap = true };
            _small.normal.textColor = UiSkin.Dim;
            _warn = new GUIStyle(_small);
            _warn.normal.textColor = UiSkin.Bad;
            _rowSel = new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleLeft, fontSize = UiSkin.Small };
            _status = new GUIStyle(GUI.skin.label) { fontSize = UiSkin.Big };
            _limbTitle = new GUIStyle(GUI.skin.label) { fontSize = UiSkin.Body, wordWrap = true };
            _limbTitle.normal.textColor = UiSkin.Amber;
            _item = new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleLeft, fontSize = UiSkin.Body };
            _item.padding = new RectOffset(10, 8, _item.padding.top, _item.padding.bottom);
            _menuWindow = new GUIStyle(GUI.skin.window);
            _menuWindow.padding = new RectOffset(8, 8, _menuWindow.padding.top, 8);
            _menuNote = new GUIStyle(_small) { padding = new RectOffset(10, 4, 0, 2) };
            _inset = UiSkin.Frame(new Color(0f, 0f, 0f, 0.42f), new Color(1f, 1f, 1f, 0.12f));
        }

        internal override void Gui()
        {
            if (!Coop.WorldLive) { _open = -1; _menu = -1; TeammateView.Hide(); return; }
            if (MedicSession.Active) { TeammateView.Hide(); return; }  // the minigame has the screen
            // The game's own health view open: that one is yours, ours closes.
            if (_open >= 0 && PlayerCamera.main != null && PlayerCamera.main.woundView != null && PlayerCamera.main.woundView.activeSelf) _open = -1;
            Styles();

            // Esc closes the menu, then the panel (the game's pause key is held off while they're up).
            var e = Event.current;
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape && !InputBlock.HoldGame && (_menu >= 0 || _open >= 0))
            {
                if (_menu >= 0) _menu = -1; else _open = -1;
                _closing = true;
                e.Use();
            }
            if (_open < 0) TeammateView.Hide();

            float k = CoopWindow.Scale;
            var old = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(k, k, 1f));
            try
            {
                if (_open >= 0)
                {
                    _rect.x = Screen.width / k - _rect.width - 20f;
                    _rect.width = 0;
                    _rect.height = 0;
                    string name = Coop.Player((byte)_open).Name;
                    _rect = GUILayout.Window(0x0C010, _rect, id => Body((byte)_open), name, UiSkin.Fit(GUI.skin.window, name));
                    UiMouse.Claim(_rect);
                }
                if (_menu >= 0) Menu(k);
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

        // ---------------------------------------------------------------- the health panel

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
            foreach (var w in Warnings(v)) GUILayout.Label(w, _warn, GUILayout.Width(PanelWidth));

            if (TeammateView.Show(id))
            {
                // The game's own figure, drawn here, as wide as the panel.
                float aspect = Mathf.Max(0.3f, TeammateView.Aspect);
                float fh = Mathf.Min(FigureMaxHeight, Mathf.Round(PanelWidth / aspect));
                float fw = Mathf.Round(fh * aspect);
                var row = GUILayoutUtility.GetRect(PanelWidth, fh, GUILayout.Width(PanelWidth), GUILayout.Height(fh));
                var r = new Rect(row.x + Mathf.Round((row.width - fw) / 2f), row.y, fw, fh);
                if (Event.current.type == EventType.Repaint) GUI.DrawTexture(row, _inset);
                TeammateView.Draw(r);
                _limb = TeammateView.Selected;
                _hoverLimb = TeammateView.Hovered;
            }
            else if (BodyFigure.Ready)
            {
                float fh = Mathf.Min(BodyFigure.HeightFor(PanelWidth), FigureMaxHeight);
                var r = GUILayoutUtility.GetRect(PanelWidth, fh, GUILayout.Width(PanelWidth), GUILayout.Height(fh));
                int hover = BodyFigure.Draw(r, v, Coop.Player(id).Character, _limb);
                if (Event.current.type == EventType.Repaint) _hoverLimb = hover;
                if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && r.Contains(Event.current.mousePosition))
                {
                    _limb = _hoverLimb >= 0 && _hoverLimb != _limb ? _hoverLimb : -1;
                    Event.current.Use();
                }
            }
            else LimbList(v);

            GUILayout.Space(6);
            GUILayout.BeginVertical(GUILayout.Width(PanelWidth));
            int shown = _hoverLimb >= 0 ? _hoverLimb : _limb;
            if (shown >= 0 && shown < v.Limbs.Count)
            {
                GUILayout.Label(Medic.LimbTitle(shown), _limbTitle);
                string issues = LimbIssues(v.Limbs[shown]);
                GUILayout.Label(issues.Length > 0 ? issues : Lang.T("nothing wrong you can see"), _small);
                GUILayout.Space(4);
            }
            LimbColumn(id, v, alive);
            GUILayout.Space(6);
            if (GUILayout.Button(Lang.T("Close"))) _open = -1;
            GUILayout.EndVertical();
            GUI.DragWindow();
        }

        /// <summary>Without the game's health view (it shouldn't happen in a run): the limbs as a list.</summary>
        private void LimbList(Vitals v)
        {
            GUILayout.BeginVertical(GUILayout.Width(176f));
            for (int i = 0; i < v.Limbs.Count; i++)
            {
                string label = Medic.LimbTitle(i);
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
                GUILayout.Space(4);
                if (GUILayout.RepeatButton(Lang.T("Hold for CPR"), GUILayout.Height(28)) && Time.unscaledTime >= _nextCpr)
                {
                    _nextCpr = Time.unscaledTime + 0.5f;
                    medic.Cpr(id);
                }
            }

            GUI.enabled = true;
        }

        // ---------------------------------------------------------------- the menu beside them

        /// <summary>The small menu that hangs beside the teammate it was opened on, and follows them.</summary>
        private void Menu(float k)
        {
            var id = (byte)_menu;
            var v = Coop.Get<VitalsSync>()?.Of(id);
            var puppet = Coop.Get<PlayerSync>()?.Find(id);
            var cam = Camera.main;
            Vector2? at = puppet != null ? puppet.LimbPosition(1) : null;
            if (v == null || cam == null || !at.HasValue || PauseHandler.paused) { _menu = -1; return; }

            var sp = cam.WorldToScreenPoint(at.Value);
            float sw = Screen.width / k, sh = Screen.height / k;
            float x = sp.x / k + 36f, y = (Screen.height - sp.y) / k - 40f;
            if (_menuRect.width > 1f && x + _menuRect.width > sw - 6f) x = sp.x / k - 36f - _menuRect.width;  // no room: the other side
            _menuRect.x = Mathf.Clamp(x, 6f, Mathf.Max(6f, sw - _menuRect.width - 6f));
            _menuRect.y = Mathf.Clamp(y, 6f, Mathf.Max(6f, sh - _menuRect.height - 6f));
            _menuRect.width = 0;
            _menuRect.height = 0;

            string name = Coop.Player(id).Name;
            _menuRect = GUILayout.Window(0x0C011, _menuRect, _ => MenuBody(id, v), name, UiSkin.Fit(_menuWindow, name), GUILayout.MinWidth(170));
            GUI.BringWindowToFront(0x0C011);
            UiMouse.Claim(_menuRect);

            // A click anywhere else closes it (and does nothing in the game: its click keys are off).
            var e = Event.current;
            if (e.type == EventType.MouseDown && Time.frameCount != _menuFrame && !_menuRect.Contains(e.mousePosition)) _menu = -1;
        }

        private void MenuBody(byte id, Vitals v)
        {
            bool alive = (v.Signs & BodySigns.Alive) != 0;
            bool awake = alive && (v.Signs & BodySigns.Conscious) != 0;
            var give = Coop.Get<GiveSync>();
            var carry = Coop.Get<CarrySync>();
            var motion = Coop.Get<TeamMotion>();
            var me = GameInfo.LocalBody;
            var rec = Coop.Player(id);
            bool below = me != null && rec.HasPosition && rec.Position.y < me.transform.position.y - 1f &&
                         Vector2.Distance(me.transform.position, rec.Position) <= TeamMotion.PullReach;

            if (Item(Lang.T("Health"))) Open(id);

            string cant = Medic.CantTreat(id);
            if (cant != null)
            {
                // Out of reach, or you can't: what still works from here.
                if (carry != null && carry.Carrying(id) && Item(Lang.T("Put down"))) { carry.PutDown(); _menu = -1; }
                if (motion != null && awake && below && Item(Lang.T("Pull up"))) { motion.PullUp(id); _menu = -1; }
                Note(cant);
                return;
            }

            var held = Medic.HeldItem(me);
            if (held != null && give != null && alive && ItemWithIcon(Lang.T("Give"), held.id)) { give.Offer(id); _menu = -1; }

            // "Ask for": what they visibly carry, as pictures. They choose whether to hand it over.
            var kit = Coop.Get<PlayerSync>()?.Find(id)?.Kit;
            if (give != null && awake && kit != null && HasAny(kit))
            {
                if (give.Asking(id)) Note(Lang.T("Asked. Waiting..."));
                else
                {
                    if (Item(Lang.T("Ask for") + (_sub == 1 ? "" : "  >"))) _sub = _sub == 1 ? 0 : 1;
                    if (_sub == 1)
                    {
                        var ids = new List<string>();
                        var slots = new List<int>();
                        for (int slot = 0; slot < kit.Length; slot++)
                            if (!string.IsNullOrEmpty(kit[slot])) { ids.Add(kit[slot]); slots.Add(slot); }
                        int pick = IconGrid(ids);
                        if (pick >= 0) { give.AskFor(id, slots[pick], ids[pick]); _menu = -1; }
                    }
                }
            }

            // Searching a body that can't stop you.
            var loot = Coop.Get<LootSync>()?.Of(id);
            if (loot != null)
            {
                if (Item(Lang.T("Search") + (_sub == 2 ? "" : "  >"))) _sub = _sub == 2 ? 0 : 2;
                if (_sub == 2)
                {
                    if (loot.Count == 0) Note(Lang.T("Nothing to take."));
                    var ids = new List<string>();
                    foreach (var en in loot) ids.Add(en.ItemId);
                    int pick = IconGrid(ids);
                    if (pick >= 0) Coop.Get<LootSync>().Take(id, loot[pick].Key);
                }
            }

            // Carrying: three ways, each with its own cost. Someone awake is asked first.
            if (carry != null)
            {
                if (carry.Carrying(id)) { if (Item(Lang.T("Put down"))) { carry.PutDown(); _menu = -1; } }
                else if (carry.CarriedBy == id) Note(Lang.T("Carrying you."));
                else if (carry.Offered(id)) Note(Lang.T("Offered. Waiting..."));
                else
                {
                    // Why a way is out, once each (most reasons are the same for all three).
                    var why = new List<string>();
                    CarryItem(carry, id, CarryMode.Drag, Lang.T("Drag"), why);
                    CarryItem(carry, id, CarryMode.Back, Lang.T("Carry on back"), why);
                    CarryItem(carry, id, CarryMode.Arms, Lang.T("Carry in arms"), why);
                    foreach (var w in why) Note(w);
                }
            }

            if (motion != null && alive && !awake)
            {
                bool asleep = (v.Signs & BodySigns.Sleeping) != 0;
                string why = motion.CantWake(id);
                if (Item(asleep ? Lang.T("Wake up") : Lang.T("Shake"), why == null)) { motion.Wake(id); _menu = -1; }
                if (why != null) Note(why);
            }

            if (motion != null && awake)
            {
                if ((v.Signs & BodySigns.Standing) != 0 && Item(Lang.T("Leg-up"))) { motion.GiveLegUp(id); _menu = -1; }
                if (below && Item(Lang.T("Pull up"))) { motion.PullUp(id); _menu = -1; }
                if (v.Mood <= -50)
                {
                    string why = motion.CantSnap(id);
                    if (Item(Lang.T("Snap out of it"), why == null)) { motion.Snap(id); _menu = -1; }
                    if (why != null) Note(why);
                }
                string heavy = TeamMotion.HeavyHeld(id, out _);
                if (motion.LiftingFor(id)) { if (Item(Lang.T("Stop helping"))) { motion.StopLifting(true); _menu = -1; } }
                else if (heavy != null && ItemWithIcon(Lang.T("Help carry"), heavy)) { motion.StartLifting(id); _menu = -1; }
            }
        }

        /// <summary>Items as a grid of their pictures; the one under the mouse is named in the game's hover label. Returns the one clicked, or -1.</summary>
        private int IconGrid(List<string> ids)
        {
            const float cell = 42f;
            const int perRow = 4;
            int picked = -1;
            for (int i = 0; i < ids.Count; i += perRow)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(8);
                for (int j = i; j < Mathf.Min(ids.Count, i + perRow); j++)
                {
                    var r = GUILayoutUtility.GetRect(cell, cell, GUILayout.Width(cell), GUILayout.Height(cell));
                    if (GUI.Button(r, GUIContent.none)) picked = j;
                    if (!ItemIcons.Draw(new Rect(r.x + 5f, r.y + 5f, r.width - 10f, r.height - 10f), ids[j]) && Event.current.type == EventType.Repaint)
                        GUI.Label(r, "?", _menuNote);
                    if (Event.current.type == EventType.Repaint && r.Contains(Event.current.mousePosition)) GameTooltip.Set(Medic.Pretty(ids[j]));
                }
                GUILayout.EndHorizontal();
            }
            return picked;
        }

        /// <summary>A menu line with an item's picture at its right end.</summary>
        private static bool ItemWithIcon(string label, string itemId)
        {
            bool hit = GUILayout.Button(label, UiSkin.Fit(_item, label), GUILayout.Height(30));
            var r = GUILayoutUtility.GetLastRect();
            ItemIcons.Draw(new Rect(r.xMax - 30f, r.y + 3f, 24f, 24f), itemId);
            if (Event.current.type == EventType.Repaint && r.Contains(Event.current.mousePosition)) GameTooltip.Set(Medic.Pretty(itemId));
            return hit;
        }

        private static bool HasAny(string[] kit)
        {
            foreach (var s in kit) if (!string.IsNullOrEmpty(s)) return true;
            return false;
        }

        private static bool Item(string label, bool enabled = true)
        {
            GUI.enabled = enabled;
            bool hit = GUILayout.Button(label, UiSkin.Fit(_item, label), GUILayout.Height(26));
            GUI.enabled = true;
            return hit && enabled;
        }

        private static void Note(string text) => GUILayout.Label(text, _menuNote, GUILayout.MaxWidth(200));

        private void CarryItem(CarrySync carry, byte id, CarryMode mode, string label, List<string> refusals)
        {
            string why = CarrySync.CantCarry(mode);
            if (Item(label, why == null)) { carry.Pick(id, mode); _menu = -1; }
            if (why != null && !refusals.Contains(why)) refusals.Add(why);
        }
    }
}
