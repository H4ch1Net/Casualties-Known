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
        }

        // ------------------------------------------------------------------ input

        internal override void Tick()
        {
            if (Typing || !Coop.InSession || CoopPlugin.Instance == null) return;
            if (CoopWindow.TypingInField || ConsoleOpen()) return;
            var alt = CoopPlugin.Instance.AltChatKey.Value;
            if (Input.GetKeyDown(CoopPlugin.Instance.ChatKey.Value) || Input.GetKeyDown(KeyCode.KeypadEnter) ||
                (alt != KeyCode.None && Input.GetKeyDown(alt) && !GameUses(alt)))
            {
                _openedBy = Input.GetKeyDown(alt) ? alt : KeyCode.Return;
                Open();
            }
        }

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

        internal static void DevOpen() => Open();
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
                kind = ChatLine.Spoken;
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
            Show(origin, m);
        }

        private static void Show(byte origin, ChatLine m)
        {
            string name = Coop.Player(origin).Name;
            if (string.IsNullOrEmpty(name)) name = "?";
            var color = m.Kind == ChatLine.Ghost ? new Color(0.7f, 0.75f, 0.9f) : Color.white;
            string text = origin == Coop.LocalId ? m.Text : ConditionFx.AsRead(m.Text, name);
            Add((m.Kind == ChatLine.Ghost ? "(" + Lang.T("dead") + ") " : "") + name + ": " + text, color);
            Log.Info("[chat] " + name + (m.Kind == ChatLine.Ghost ? " (dead)" : "") + ": " + text);
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
                _style = new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = true };
                _field = new GUIStyle(GUI.skin.textField) { fontSize = 14 };
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
                    }
                }

                // Newest at the bottom, just above the input line.
                float now = Time.unscaledTime;
                float lineY = y;
                for (int i = Lines.Count - 1; i >= 0; i--)
                {
                    var l = Lines[i];
                    float age = now - l.At;
                    float alpha = Typing ? 1f : Mathf.Clamp01((FadeAfter - age) / 2f);
                    if (alpha <= 0f) continue;
                    float lh = _style.CalcHeight(new GUIContent(l.Text), width);
                    lineY -= lh;
                    var c = l.Color;
                    _style.normal.textColor = new Color(0f, 0f, 0f, alpha * 0.8f);
                    GUI.Label(new Rect(x + 1, lineY + 1, width, lh), l.Text, _style);
                    _style.normal.textColor = new Color(c.r, c.g, c.b, alpha);
                    GUI.Label(new Rect(x, lineY, width, lh), l.Text, _style);
                }

                if (Typing)
                {
                    // The key that opened chat mustn't also become its first letter.
                    if (Time.frameCount == _openedFrame && _openedBy != KeyCode.Return) _draft = "";
                    GUI.Label(new Rect(x - 52f, y + 2f, 50f, 24f), Lang.T("Say:"), _style);
                    GUI.SetNextControlName("ccoop_chat");
                    _draft = GUI.TextField(new Rect(x, y, width, 26f), _draft ?? "", MaxLength, _field);
                    if (_focusPending)
                    {
                        GUI.FocusControl("ccoop_chat");
                        _focusPending = false;
                        if (Net.SteamServices.OnScreenKeyboard)
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
