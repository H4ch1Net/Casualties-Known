using System;
using System.Collections.Generic;
using CCoop.Net.Session;
using CCoop.Plugin.Protocol;
using CCoop.Plugin.Runtime;
using CCoop.Plugin.UI;
using HarmonyLib;
using UnityEngine;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// Gestures. Hold the gesture key for a wheel: wave, come here, wait, thumbs up, no,
    /// point, help, hug. Most show as the character's own speech bubble ("*waves*" — the
    /// game never slurs a line in asterisks), so they appear above your head on every
    /// screen. Point draws a line to the spot for everyone; Help calls out with a marker
    /// teammates can see from anywhere; Hug lifts both players' mood (the game won't let a
    /// player who's too low treat wounds) and shares warmth. The game's own bark key is
    /// heard by everyone too.
    /// </summary>
    internal sealed class GestureSync : CoopModule
    {
        private enum Kind : byte { Wave, ComeHere, Wait, ThumbsUp, No, Point, Help, Hug, Bark }

        private static readonly (Kind kind, string label, string line)[] Wheel =
        {
            (Kind.Wave, Lang.N("Wave"), Lang.N("*waves*")),
            (Kind.ComeHere, Lang.N("Come here"), Lang.N("*beckons you over*")),
            (Kind.Point, Lang.N("Point"), Lang.N("*points*")),
            (Kind.Help, Lang.N("Help!"), Lang.N("*calls for help!*")),
            (Kind.Hug, Lang.N("Hug"), null),
            (Kind.No, Lang.N("No"), Lang.N("*shakes head*")),
            (Kind.Wait, Lang.N("Wait"), Lang.N("*holds up a hand: wait*")),
            (Kind.ThumbsUp, Lang.N("Thumbs up"), Lang.N("*gives a thumbs up*")),
        };

        private const float HugReach = 1.8f;
        private const float HugCooldown = 60f;
        private const float HugMood = 10f;

        private bool _wheelOpen;
        private Vector2 _wheelCentre;
        private int _choice = -1;
        private readonly Dictionary<byte, float> _lastHug = new Dictionary<byte, float>();

        private struct PointLine
        {
            public byte Owner;
            public Vector2 Target;
            public float At;
        }

        private readonly List<PointLine> _points = new List<PointLine>();
        private static readonly Dictionary<int, List<AudioClip>> BarkClips = new Dictionary<int, List<AudioClip>>();
        private static GUIStyle _wheelStyle, _wheelSel;

        internal override void Attach(NetSession s) => s.On<GestureMsg>(OnGesture);

        internal override void Detach()
        {
            _points.Clear();
            _lastHug.Clear();
            CloseWheel();
        }

        // ------------------------------------------------------------------ doing it

        /// <summary>The wheel stays up after a quick tap of the key, for a click.</summary>
        private bool _sticky;
        private float _openedAt;
        private float _ignoredLogAt = -100f;

        /// <summary>A tapped-open wheel holds the game's own controls (the click picks, not swings).</summary>
        internal static bool HoldingInput;

        internal override void Tick()
        {
            if (!Coop.WorldLive || !CoopPlugin.Ready) { CloseWheel(); return; }
            var key = CoopPlugin.Instance.GestureKey.Value;
            if (!_wheelOpen && Input.GetKeyDown(key))
            {
                if (InputBlock.Blocking || Chat.Typing)
                {
                    if (Time.unscaledTime - _ignoredLogAt > 5f)
                    {
                        _ignoredLogAt = Time.unscaledTime;
                        Log.Info("[gesture] " + key + " ignored: " + (Chat.Typing ? "chat is open" : "a co-op window text field has the keyboard"));
                    }
                }
                else
                {
                    _wheelOpen = true;
                    _sticky = false;
                    _choice = -1;
                    _openedAt = Time.unscaledTime;
                    // Around the mouse, but always whole on screen (the mouse may be at an
                    // edge, or outside the window altogether).
                    float m = 180f * CoopWindow.Scale;
                    Vector2 at = Input.mousePosition;
                    _wheelCentre = new Vector2(Mathf.Clamp(at.x, Mathf.Min(m, Screen.width / 2f), Mathf.Max(Screen.width - m, Screen.width / 2f)),
                                               Mathf.Clamp(at.y, Mathf.Min(m, Screen.height / 2f), Mathf.Max(Screen.height - m, Screen.height / 2f)));
                    Log.Info("[gesture] wheel open");
                }
                return;
            }
            if (!_wheelOpen) return;

            Vector2 d = (Vector2)Input.mousePosition - _wheelCentre;
            _choice = d.magnitude < 30f * CoopWindow.Scale ? -1 : Sector(d);
            for (int i = 0; i < Wheel.Length && i < 9; i++)
                if (Input.GetKeyDown(KeyCode.Alpha1 + i) || Input.GetKeyDown(KeyCode.Keypad1 + i)) { Pick(i); return; }

            if (!_sticky)
            {
                if (Input.GetKey(key)) return;
                // Let go: a pick if the mouse moved onto one; after a quick tap the wheel
                // stays up for a click instead.
                if (_choice >= 0) { Pick(_choice); return; }
                if (Time.unscaledTime - _openedAt < 0.35f) { _sticky = true; HoldingInput = true; return; }
                CloseWheel();
                Log.Info("[gesture] wheel closed without a pick");
                return;
            }

            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(key) || Input.GetMouseButtonDown(1)) { CloseWheel(); return; }
            if (Input.GetMouseButtonDown(0) && _choice >= 0) Pick(_choice);
        }

        private void Pick(int index)
        {
            CloseWheel();
            Do(index);
        }

        private void CloseWheel()
        {
            _wheelOpen = false;
            _sticky = false;
            _choice = -1;
            HoldingInput = false;
        }

        private static int Sector(Vector2 d)
        {
            // Sector 0 points straight up, then clockwise.
            float angle = Mathf.Atan2(d.x, d.y) * Mathf.Rad2Deg;
            if (angle < 0f) angle += 360f;
            return Mathf.RoundToInt(angle / (360f / Wheel.Length)) % Wheel.Length;
        }

        /// <summary>Dev: perform a gesture by its wheel label.</summary>
        internal void DevDo(string label)
        {
            for (int i = 0; i < Wheel.Length; i++) if (Wheel[i].label == label) { Do(i); return; }
        }

        private void Do(int index)
        {
            var body = GameInfo.LocalBody;
            if (body == null || !body.alive || !body.conscious) { CoopRuntime.Notice(Lang.T("You can't do that right now.")); return; }
            var (kind, label, line) = Wheel[index];
            var msg = new GestureMsg { Kind = (byte)kind };

            if (kind == Kind.Hug)
            {
                var target = NearestTeammate(body, HugReach);
                if (target == null) { CoopRuntime.Notice(Lang.T("No one close enough to hug.")); return; }
                msg.Target = target.Id;
                line = Lang.F("*hugs {0}*", Coop.Player(target.Id).Name);
                Hugged(target.Id, body);
            }
            else if (kind == Kind.Point)
            {
                var cam = Camera.main;
                if (cam == null) return;
                Vector2 at = cam.ScreenToWorldPoint(Input.mousePosition);
                msg.X = at.x;
                msg.Y = at.y;
                _points.Add(new PointLine { Owner = Coop.LocalId, Target = at, At = Time.unscaledTime });
            }
            else if (kind == Kind.Help)
            {
                Coop.Get<PingSync>()?.DevPing(body.transform.position);
            }

            if (line != null) Say(body, Lang.T(line));
            Session.Broadcast(msg);
            Log.Info("[gesture] " + label);
        }

        private static void Say(Body body, string line)
        {
            try { body.talker?.Talk(line, null, true, true); }
            catch (Exception e) { Log.Debug("gesture talk: " + e.Message); }
        }

        private static RemotePlayer NearestTeammate(Body body, float reach)
        {
            RemotePlayer best = null;
            float bestDist = reach;
            var ps = Coop.Get<PlayerSync>();
            if (ps == null) return null;
            foreach (var r in ps.Remotes)
            {
                var at = r.ShownAt;
                if (!at.HasValue || !Coop.Player(r.Id).Alive) continue;
                float d = Vector2.Distance(body.transform.position, at.Value);
                if (d < bestDist) { bestDist = d; best = r; }
            }
            return best;
        }

        /// <summary>A hug: mood and warmth, at most once a minute per pair.</summary>
        private void Hugged(byte other, Body body)
        {
            float now = Time.unscaledTime;
            if (_lastHug.TryGetValue(other, out var last) && now - last < HugCooldown) return;
            _lastHug[other] = now;
            body.happiness += HugMood;
            Milestones.Add("hug");
            if (body.temperature < 37f) body.temperature = Mathf.Lerp(body.temperature, 37f, 0.2f);
        }

        /// <summary>The game's own bark key: everyone hears it.</summary>
        internal void LocalBarked(Body body)
        {
            if (!Coop.WorldLive) return;
            Session.Broadcast(new GestureMsg { Kind = (byte)Kind.Bark, Pitch = body.voicePitch });
        }

        // ------------------------------------------------------------------ seeing it

        private void OnGesture(byte origin, GestureMsg m)
        {
            if (!Coop.WorldLive || origin == Coop.LocalId) return;
            var kind = (Kind)m.Kind;
            var puppet = Coop.Get<PlayerSync>()?.Find(origin);
            switch (kind)
            {
                case Kind.Bark:
                    var at = puppet?.ShownAt;
                    if (!at.HasValue) return;
                    var clips = Barks(Coop.Player(origin).Character);
                    if (clips.Count > 0)
                        Sound.Play(clips[UnityEngine.Random.Range(0, clips.Count)], at.Value, false, true, null, 1f, m.Pitch <= 0f ? 1f : m.Pitch);
                    Log.Info("[gesture] " + Coop.Player(origin).Name + " barked (" + clips.Count + " clips)");
                    break;
                case Kind.Point:
                    _points.Add(new PointLine { Owner = origin, Target = new Vector2(m.X, m.Y), At = Time.unscaledTime });
                    break;
                case Kind.Hug:
                    if (m.Target != Coop.LocalId) return;
                    var body = GameInfo.LocalBody;
                    if (body == null || !body.alive) return;
                    float before = body.happiness;
                    Hugged(origin, body);
                    CoopRuntime.Notice(Lang.F("{0} hugged you", Coop.Player(origin).Name));
                    Log.Info("[gesture] hugged by " + Coop.Player(origin).Name + ": mood " + before.ToString("F1") + " -> " + body.happiness.ToString("F1"));
                    break;
            }
        }

        private static List<AudioClip> Barks(int character)
        {
            if (BarkClips.TryGetValue(character, out var list)) return list;
            list = new List<AudioClip>();
            try
            {
                var prefab = Resources.Load<GameObject>("Special/Characters/" + GameInfo.CharacterName(character));
                var pant = prefab != null ? prefab.GetComponentInChildren<PantSound>(true) : null;
                if (pant != null && pant.barkSounds != null) list.AddRange(pant.barkSounds);
            }
            catch (Exception e) { Log.Debug("bark clips: " + e.Message); }
            BarkClips[character] = list;
            return list;
        }

        internal override void Gui()
        {
            DrawPoints();
            if (_wheelOpen) DrawWheel();
        }

        private void DrawPoints()
        {
            if (_points.Count == 0) return;
            var cam = Camera.main;
            if (cam == null) return;
            float now = Time.unscaledTime;
            _points.RemoveAll(p => now - p.At > 4f);
            foreach (var p in _points)
            {
                Vector2 from;
                if (p.Owner == Coop.LocalId) from = GameInfo.LocalBody != null ? (Vector2)GameInfo.LocalBody.transform.position : p.Target;
                else
                {
                    var at = Coop.Get<PlayerSync>()?.Find(p.Owner)?.ShownAt;
                    if (!at.HasValue) continue;
                    from = at.Value;
                }
                var a = cam.WorldToScreenPoint(from);
                var b = cam.WorldToScreenPoint(p.Target);
                if (a.z < 0 || b.z < 0) continue;
                var ga = new Vector2(a.x, Screen.height - a.y);
                var gb = new Vector2(b.x, Screen.height - b.y);
                float alpha = Mathf.Clamp01((4f - (now - p.At)) / 1f);
                var c = PingSync.ColorOf(p.Owner);
                var old = GUI.color;
                GUI.color = new Color(c.r, c.g, c.b, alpha * 0.9f);
                float len = Vector2.Distance(ga, gb);
                float phase = (now * 60f) % 14f;
                for (float t = phase; t < len; t += 14f)
                {
                    var q = Vector2.Lerp(ga, gb, t / len);
                    GUI.DrawTexture(new Rect(q.x - 2.5f, q.y - 2.5f, 5f, 5f), Texture2D.whiteTexture);
                }
                GUI.DrawTexture(new Rect(gb.x - 6f, gb.y - 6f, 12f, 12f), Texture2D.whiteTexture);
                GUI.color = old;
            }
        }

        private void DrawWheel()
        {
            if (_wheelStyle == null)
            {
                _wheelStyle = new GUIStyle(GUI.skin.box) { fontSize = 14, alignment = TextAnchor.MiddleCenter };
                _wheelSel = new GUIStyle(_wheelStyle) { fontStyle = FontStyle.Bold };
                _wheelSel.normal.textColor = new Color(1f, 0.9f, 0.4f);
            }
            float k = CoopWindow.Scale;
            var centre = new Vector2(_wheelCentre.x, Screen.height - _wheelCentre.y);
            float radius = 110f * k;
            var old = GUI.color;
            for (int i = 0; i < Wheel.Length; i++)
            {
                float angle = i * (360f / Wheel.Length) * Mathf.Deg2Rad;
                var at = centre + new Vector2(Mathf.Sin(angle), -Mathf.Cos(angle)) * radius;
                var r = new Rect(at.x - 55f * k, at.y - 16f * k, 110f * k, 32f * k);
                GUI.color = i == _choice ? Color.white : new Color(1f, 1f, 1f, 0.8f);
                GUI.Box(r, (i + 1) + "  " + Lang.T(Wheel[i].label), i == _choice ? _wheelSel : _wheelStyle);
            }
            GUI.color = old;
            string hint = _sticky ? Lang.T("Click a gesture (or press its number). Esc to close")
                                  : Lang.T("Move the mouse onto a gesture and let go");
            var hr = new Rect(centre.x - 160f * k, centre.y + radius + 26f * k, 320f * k, 26f * k);
            GUI.Box(hr, hint, _wheelStyle);
        }
    }

    /// <summary>The local player's bark (the game's bark key) is announced to teammates.</summary>
    [HarmonyPatch(typeof(PantSound), nameof(PantSound.Bark))]
    internal static class BarkPatch
    {
        private static Body BodyOf(PantSound p)
        {
            try
            {
                var t = Traverse.Create(p);
                var b = t.Field("body").GetValue<Body>() ?? t.Property("body").GetValue<Body>();
                return b != null ? b : p.GetComponentInParent<Body>();
            }
            catch { return p.GetComponentInParent<Body>(); }
        }

        private static void Prefix(PantSound __instance, out float __state)
        {
            var b = BodyOf(__instance);
            __state = b != null ? b.eatTime : 1f;
        }

        private static void Postfix(PantSound __instance, float __state)
        {
            var body = BodyOf(__instance);
            // Bark() sets eatTime to 0.5 only when it actually barked.
            if (body == null || !Coop.IsLocalBody(body) || __state > 0f || body.eatTime <= 0f) return;
            Coop.Get<GestureSync>()?.LocalBarked(body);
        }
    }
}
