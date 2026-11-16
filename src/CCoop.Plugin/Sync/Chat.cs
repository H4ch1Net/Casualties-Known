using System;
using System.Collections.Generic;
using CCoop.Net.Session;
using CCoop.Plugin.Protocol;
using CCoop.Plugin.Runtime;
using CCoop.Plugin.UI;
using UnityEngine;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// Text chat. In a layer, your character says the line out loud, so it comes out
    /// through the game's own speech (slurred by brain damage and the like) and shows as
    /// a speech bubble on everyone's screen; the log shows what was actually said. The
    /// unconscious can't speak, and the dead talk only among themselves.
    /// </summary>
    internal sealed class Chat : CoopModule
    {
        private const int MaxLength = 200;
        private const float FadeAfter = 12f;
        private const int Keep = 8;

        /// <summary>The chat box is open: game input is held back.</summary>
        internal static bool Typing { get; private set; }

        private static string _draft = "";
        private static bool _radio;
        private static readonly Color RadioColor = new Color(0.55f, 1f, 0.6f);
        private static bool _focusPending;
        private static int _openedFrame = -1;

        private struct Line
        {
            public string Text;
            public Color Color;
            public float At;
        }

        private static readonly List<Line> Lines = new List<Line>();
        private static GUIStyle _style, _field;

        internal override void Attach(NetSession s) => s.On<ChatLine>(OnLine);

        internal override void Detach()
        {
            Close();
            Lines.Clear();
            foreach (var t in Shown) if (t != null) t.gameObject.SetActive(false);
        }

        // ------------------------------------------------------------------ input

        internal override void Tick()
        {
            if (!Coop.InSession || !CoopPlugin.Ready) return;
            if (Typing)
            {
                // Enter and Esc work from the keyboard state too, so chat never depends
                // on which IMGUI control Unity thinks has focus.
                if (Time.frameCount > _openedFrame + 1 && (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)))
                {
                    string text = _draft;
                    Close();
                    if (string.IsNullOrEmpty((text ?? "").Trim())) Log.Info("[chat] closed (nothing typed)");
                    Send(text);
                }
                else if (Input.GetKeyDown(KeyCode.Escape))
                {
                    Close();
                    Log.Info("[chat] closed");
                }
                return;
            }
            var alt = CoopPlugin.Instance.AltChatKey.Value;
            bool pressed = Input.GetKeyDown(CoopPlugin.Instance.ChatKey.Value) || Input.GetKeyDown(KeyCode.KeypadEnter) ||
                           (alt != KeyCode.None && Input.GetKeyDown(alt) && !GameUses(alt));
            if (!pressed) return;
            if (CoopWindow.TypingInField || ConsoleOpen())
            {
                if (Time.unscaledTime - _ignoredLogAt > 5f)
                {
                    _ignoredLogAt = Time.unscaledTime;
                    Log.Info("[chat] key ignored: " + (ConsoleOpen() ? "the game's console is open" : "a co-op window text field has the keyboard"));
                }
                return;
            }
            _openedBy = alt != KeyCode.None && Input.GetKeyDown(alt) ? alt : KeyCode.Return;
            Open();
            Log.Info("[chat] opened with " + _openedBy);
        }

        private static float _ignoredLogAt = -100f;

        private static KeyCode _openedBy;

        // ---- which keys the game itself uses (its binds can change in settings)

        private static readonly HashSet<KeyCode> Bound = new HashSet<KeyCode>();
        private static float _boundAt = -100f;

        /// <summary>Whether any of the game's own actions is bound to <paramref name="key"/>.</summary>
        internal static bool GameUses(KeyCode key)
        {
            if (Time.unscaledTime - _boundAt > 2f)
            {
                _boundAt = Time.unscaledTime;
                Bound.Clear();
                try
                {
                    var binds = HarmonyLib.Traverse.Create(typeof(KeyBinds)).Field("cachedBinds").GetValue<Dictionary<string, KeyCode>>();
                    if (binds != null) foreach (var k in binds.Values) Bound.Add(k);
                }
                catch { Bound.Add(key); }  // can't tell: assume it's taken
            }
            return Bound.Contains(key);
        }

        private static bool ConsoleOpen() => InputBlock.ConsoleOpen;

        private static void Open()
        {
            Typing = true;
            _draft = "";
            _focusPending = true;
            _openedFrame = Time.frameCount;
        }

        private static void Close()
        {
            Typing = false;
            _draft = "";
            GUIUtility.keyboardControl = 0;
        }

        internal static int Count => Lines.Count;

        // ---- the log in the game's own speech text (in a run; the menu keeps the plain one)

        private static readonly List<TMPro.TextMeshProUGUI> Shown = new List<TMPro.TextMeshProUGUI>();
        private static bool UseGameText => UI.GameText.Template != null;

        internal override void LateTick()
        {
            if (!UseGameText)
            {
                foreach (var t in Shown) if (t != null) t.gameObject.SetActive(false);
                return;
            }
            float now = Time.unscaledTime;
            // Laid out from the bottom up, newest lowest, just above the input line (the
            // canvas is 1080 tall whatever the screen).
            float y = 190f + 30f;
            int used = 0;
            var amber = UI.GameText.Colour;
            for (int i = Lines.Count - 1; i >= 0 && used < 10; i--)
            {
                var l = Lines[i];
                float alpha = Typing ? 1f : Mathf.Clamp01((FadeAfter - (now - l.At)) / 2f);
                if (alpha <= 0f) continue;
                while (Shown.Count <= used)
                {
                    var made = UI.GameText.Screen("Chat" + Shown.Count);
                    if (made == null) return;
                    made.alignment = TMPro.TextAlignmentOptions.BottomLeft;
                    made.fontSize = 26f;
                    var rt = made.rectTransform;
                    rt.anchorMin = rt.anchorMax = Vector2.zero;
                    rt.pivot = Vector2.zero;
                    Shown.Add(made);
                }
                var tmp = Shown[used++];
                if (tmp == null) continue;
                tmp.gameObject.SetActive(true);
                if (tmp.text != l.Text) tmp.text = l.Text;
                // What people say is in the game's speech colour; the mod's own notes keep theirs.
                var c = l.Color == Color.white ? amber : l.Color;
                tmp.color = new Color(c.r, c.g, c.b, alpha);
                var r = tmp.rectTransform;
                r.sizeDelta = new Vector2(620f, 30f);
                float h = Mathf.Max(30f, tmp.GetPreferredValues(l.Text, 620f, 0f).y);
                r.sizeDelta = new Vector2(620f, h);
                r.anchoredPosition = new Vector2(110f, y);
                y += h + 2f;
            }
            for (int i = used; i < Shown.Count; i++) if (Shown[i] != null) Shown[i].gameObject.SetActive(false);
        }

        internal static void DevOpen() => Open();
        internal static void DevRadioMode(bool on) => _radio = on;

        /// <summary>Radio lines this player heard through a radio (self-test).</summary>
        internal static int RadioHeard;

        /// <summary>Self-test: says a line into the radio.</summary>
        internal void DevRadio(string text)
        {
            _radio = true;
            try { Send(text); }
            finally { _radio = false; }
        }
        internal static void DevClose() => Close();

        internal void Send(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return;
            if (text.Length > MaxLength) text = text.Substring(0, MaxLength);

            var body = GameInfo.LocalBody;
            bool inLayer = Coop.WorldLive && body != null;
            byte kind = ChatLine.Plain;
            if (inLayer && !body.alive) kind = ChatLine.Ghost;
            else if (inLayer && !body.conscious)
            {
                Add(Lang.T("You can't speak while unconscious."), new Color(1f, 0.6f, 0.5f));
                return;
            }
            else if (inLayer)
            {
                // Into the radio (still said out loud, so people nearby hear the voice), or just said.
                kind = _radio && Radio.Usable ? ChatLine.Radio : ChatLine.Spoken;
                text = ConditionFx.SpeakThroughJaw(body, text);
                text = InputBlock.Speak(body, text) ?? text;
            }

            var line = new ChatLine { Text = text, Kind = kind };
            Session.Broadcast(line);
            Show(Coop.LocalId, line);
        }

        private void OnLine(byte origin, ChatLine m)
        {
            if (origin == Coop.LocalId) return;
            if (m.Kind == ChatLine.Ghost)
            {
                // Only the dead hear the dead.
                var body = GameInfo.LocalBody;
                if (Coop.WorldLive && body != null && body.alive) return;
            }
            if (m.Kind == ChatLine.Radio && Radio.Usable)
            {
                RadioHeard++;
                Radio.Blip();
                Show(origin, m);
                return;
            }
            if (m.Kind == ChatLine.Spoken || m.Kind == ChatLine.Radio)
            {
                // Said out loud: what reaches you depends on how far they are (Barotrauma's way).
                string heard = Hearing.Heard(origin, m.Text);
                if (heard == null) return;
                m = new ChatLine { Text = heard, Kind = ChatLine.Spoken };
            }
            Show(origin, m);
        }

        private static void Show(byte origin, ChatLine m)
        {
            string name = Coop.Player(origin).Name;
            if (string.IsNullOrEmpty(name)) name = "?";
            var color = m.Kind == ChatLine.Ghost ? new Color(0.7f, 0.75f, 0.9f) : m.Kind == ChatLine.Radio ? RadioColor : Color.white;
            string text = origin == Coop.LocalId ? m.Text : ConditionFx.AsRead(m.Text, name);
            string tag = m.Kind == ChatLine.Ghost ? "(" + Lang.T("dead") + ") " : m.Kind == ChatLine.Radio ? "(" + Lang.T("radio") + ") " : "";
            Add(tag + name + ": " + text, color);
            Log.Info("[chat] " + name + (m.Kind == ChatLine.Ghost ? " (dead)" : m.Kind == ChatLine.Radio ? " (radio)" : "") + ": " + text);
        }

        private static void Add(string text, Color color)
        {
            Lines.Add(new Line { Text = text, Color = color, At = Time.unscaledTime });
            if (Lines.Count > Keep) Lines.RemoveAt(0);
        }

        // ------------------------------------------------------------------ drawing

        internal override void Gui()
        {
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label) { fontSize = UiSkin.Body, wordWrap = true };
                _field = new GUIStyle(GUI.skin.textField) { fontSize = UiSkin.Body };
            }
            float k = CoopWindow.Scale;
            var old = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(k, k, 1f));
            try
            {
                float h = Screen.height / k;
                float x = 110f, width = 520f;
                float y = h - 190f;

                if (Typing)
                {
                    var e = Event.current;
                    if (e.type == EventType.KeyDown && GUI.GetNameOfFocusedControl() == "ccoop_chat")
                    {
                        if ((e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) && Time.frameCount != _openedFrame)
                        {
                            string text = _draft;
                            Close();
                            e.Use();
                            Send(text);
                        }
                        else if (e.keyCode == KeyCode.Escape)
                        {
                            Close();
                            e.Use();
                        }
                        else if (e.keyCode == KeyCode.Tab)
                        {
                            // Say it, or radio it.
                            if (Radio.Usable || _radio) _radio = !_radio;
                            e.Use();
                        }
                    }
                }

                // Newest at the bottom, just above the input line (drawn in the game's
                // speech text instead, in a run: see LateTick).
                float now = Time.unscaledTime;
                if (UseGameText) goto Input;
                float lineY = y;
                for (int i = Lines.Count - 1; i >= 0; i--)
                {
                    var l = Lines[i];
                    float age = now - l.At;
                    float alpha = Typing ? 1f : Mathf.Clamp01((FadeAfter - age) / 2f);
                    if (alpha <= 0f) continue;
                    var ls = UiSkin.Fit(_style, l.Text);
                    float lh = ls.CalcHeight(new GUIContent(l.Text), width);
                    lineY -= lh;
                    var c = l.Color;
                    var keep = ls.normal.textColor;
                    ls.normal.textColor = new Color(0f, 0f, 0f, alpha * 0.85f);
                    GUI.Label(new Rect(x + 1, lineY + 1, width, lh), l.Text, ls);
                    ls.normal.textColor = new Color(c.r, c.g, c.b, alpha);
                    GUI.Label(new Rect(x, lineY, width, lh), l.Text, ls);
                    ls.normal.textColor = keep;
                }

                Input:
                if (Typing)
                {
                    // The key that opened chat mustn't also become its first letter.
                    if (Time.frameCount == _openedFrame && _openedBy != KeyCode.Return) _draft = "";
                    // "Say:" then the field, both clear of the game's buttons on the left.
                    if (_radio && !Radio.Usable) _radio = false;
                    string say = _radio ? Lang.T("Radio:") : Radio.Usable ? Lang.T("Say (Tab: radio):") : Lang.T("Say:");
                    float sw = _style.CalcSize(new GUIContent(say)).x;
                    var sayColor = _style.normal.textColor;
                    if (_radio) _style.normal.textColor = RadioColor;
                    GUI.Label(new Rect(x, y + 3f, sw + 4f, 24f), say, _style);
                    _style.normal.textColor = sayColor;
                    GUI.SetNextControlName("ccoop_chat");
                    _draft = GUI.TextField(new Rect(x + sw + 8f, y, width - sw - 8f, 26f), _draft ?? "", MaxLength, UiSkin.Fit(_field, _draft));
                    if (Event.current.type == EventType.Layout && GUI.GetNameOfFocusedControl() != "ccoop_chat") _focusPending = true;
                    if (_focusPending)
                    {
                        GUI.FocusControl("ccoop_chat");
                        _focusPending = false;
                        if (Net.SteamServices.OnScreenKeyboard && Time.frameCount <= _openedFrame + 2)
                            Net.SteamServices.ShowKeyboard(new Rect(x * k, y * k, width * k, 26f * k));
                    }
                }
            }
            finally
            {
                GUI.matrix = old;
            }
        }
    }
}
