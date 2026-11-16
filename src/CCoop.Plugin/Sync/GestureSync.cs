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
    /// Gestures. Hold the gesture key for a wheel: wave, come here, follow me, point, look
    /// out, help, hug, thumbs up, yes, no, shrug, wait. The character performs them (see
    /// GesturePose); one that can't be performed right now (climbing, lying down) shows
    /// as the character's own speech bubble ("*waves*" — the game never slurs a line in
    /// asterisks) instead. Point draws a line to the spot for everyone; Look out shouts
    /// and leaves a warning mark on the spot; Help calls out with a marker teammates can
    /// see from anywhere; Hug lifts both players' mood (the game won't let a player who's
    /// too low treat wounds) and shares warmth. The game's own bark key is heard by
    /// everyone too.
    /// </summary>
    internal sealed class GestureSync : CoopModule
    {
        // New kinds go at the end: the byte is what travels.
        private enum Kind : byte { Wave, ComeHere, Wait, ThumbsUp, No, Point, Help, Hug, Bark, Yes, Shrug, LookOut, FollowMe }

        /// <summary>Clockwise from the top: getting about, alarms, the hug, answers.</summary>
        private static readonly (Kind kind, string label, string line)[] Wheel =
        {
            (Kind.Wave, Lang.N("Wave"), Lang.N("*waves*")),
            (Kind.ComeHere, Lang.N("Come here"), Lang.N("*beckons you over*")),
            (Kind.FollowMe, Lang.N("Follow me"), Lang.N("*waves you along*")),
            (Kind.Point, Lang.N("Point"), Lang.N("*points*")),
            (Kind.LookOut, Lang.N("Look out!"), Lang.N("Look out!")),
            (Kind.Help, Lang.N("Help!"), Lang.N("Help!")),
            (Kind.Hug, Lang.N("Hug"), null),
            (Kind.ThumbsUp, Lang.N("Thumbs up"), Lang.N("*gives a thumbs up*")),
            (Kind.Yes, Lang.N("Yes"), Lang.N("*nods*")),
            (Kind.No, Lang.N("No"), Lang.N("*shakes head*")),
            (Kind.Shrug, Lang.N("Shrug"), Lang.N("*shrugs*")),
            (Kind.Wait, Lang.N("Wait"), Lang.N("*holds up a hand: wait*")),
        };

        /// <summary>Said out loud as well as done: these are shouts.</summary>
        private static bool Shouted(Kind k) => k == Kind.Help || k == Kind.LookOut;

        /// <summary>The key that picks the n-th gesture, as on a hotbar: 1 to 9, 0, - and =.</summary>
        private static string KeyLabel(int i) => i < 9 ? (i + 1).ToString() : i == 9 ? "0" : i == 10 ? "-" : i == 11 ? "=" : "";

        private static bool KeyDown(int i)
        {
            if (i < 9) return Input.GetKeyDown(KeyCode.Alpha1 + i) || Input.GetKeyDown(KeyCode.Keypad1 + i);
            if (i == 9) return Input.GetKeyDown(KeyCode.Alpha0) || Input.GetKeyDown(KeyCode.Keypad0);
            if (i == 10) return Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.KeypadMinus);
            if (i == 11) return Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.KeypadPlus);
            return false;
        }

        private const float HugReach = 1.8f;
        private const float HugCooldown = 60f;
        private const float HugMood = 10f;

        private bool _wheelOpen;
        private static float _boxW;
        private static string _boxFor;
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
                    float m = 220f * CoopWindow.Scale;
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
            for (int i = 0; i < Wheel.Length; i++)
                if (KeyDown(i)) { Pick(i); return; }

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
            // Sector 0 points straight up, then clockwise, measured on the wheel's oval
            // (squashed back to a circle) so the mouse picks the box it's heading for.
            d = new Vector2(d.x / _rx, d.y / _ry);
            float angle = Mathf.Atan2(d.x, d.y) * Mathf.Rad2Deg;
            if (angle < 0f) angle += 360f;
            return Mathf.RoundToInt(angle / (360f / Wheel.Length)) % Wheel.Length;
        }

        /// <summary>Screenshots: the wheel open in the middle of the screen (or shut).</summary>
        internal void DevWheel(bool open)
        {
            if (!open) { CloseWheel(); return; }
            _wheelOpen = true;
            _sticky = true;
            _choice = -1;
            _openedAt = Time.unscaledTime;
            _wheelCentre = new Vector2(Screen.width / 2f, Screen.height / 2f);
        }

        /// <summary>Dev: perform a gesture by its wheel label.</summary>
        internal void DevDo(string label)
        {
            for (int i = 0; i < Wheel.Length; i++) if (Wheel[i].label == label) { Do(i); return; }
        }

        private static GesturePose.Pose PoseOf(Kind k)
        {
            switch (k)
            {
                case Kind.Wave: return GesturePose.Pose.Wave;
                case Kind.ComeHere: return GesturePose.Pose.Beckon;
                case Kind.Point: return GesturePose.Pose.Point;
                case Kind.ThumbsUp: return GesturePose.Pose.ThumbsUp;
                case Kind.Wait: return GesturePose.Pose.Wait;
                case Kind.No: return GesturePose.Pose.No;
                case Kind.Help: return GesturePose.Pose.Help;
                case Kind.Hug: return GesturePose.Pose.Hug;
                case Kind.Yes: return GesturePose.Pose.Yes;
                case Kind.Shrug: return GesturePose.Pose.Shrug;
                case Kind.LookOut: return GesturePose.Pose.Point;
                case Kind.FollowMe: return GesturePose.Pose.FollowMe;
                default: return GesturePose.Pose.None;
            }
        }

        private void Do(int index)
        {
            var body = GameInfo.LocalBody;
            if (body == null || !body.alive || !body.conscious) { CoopRuntime.Notice(Lang.T("You can't do that right now.")); return; }
            var (kind, label, line) = Wheel[index];
            var msg = new GestureMsg { Kind = (byte)kind };
            Vector2 pointAt = default;

            if (kind == Kind.Hug)
            {
                var target = NearestTeammate(body, HugReach);
                if (target == null) { CoopRuntime.Notice(Lang.T("No one close enough to hug.")); return; }
                msg.Target = target.Id;
                Hugged(target.Id, body);
            }
            else if (kind == Kind.Point || kind == Kind.LookOut || kind == Kind.FollowMe)
            {
                // At the spot under the mouse (Follow me only turns that way).
                var cam = Camera.main;
                if (cam == null) return;
                Vector2 at = cam.ScreenToWorldPoint(Input.mousePosition);
                pointAt = at;
                msg.X = at.x;
                msg.Y = at.y;
                if (kind != Kind.FollowMe) _points.Add(new PointLine { Owner = Coop.LocalId, Target = at, At = Time.unscaledTime });
                if (kind == Kind.LookOut) Coop.Get<PingSync>()?.Warn(Coop.LocalId, at);
            }
            else if (kind == Kind.Help)
            {
                Coop.Get<PingSync>()?.DevPing(body.transform.position);
            }

            // The body does it; shouts are also said out loud, and a body that can't move
            // its arms just now (climbing, lying down) says what it would have done.
            var pose = PoseOf(kind);
            if (pose != GesturePose.Pose.None) GesturePose.Play(pose, pointAt);
            if (line != null && (pose == GesturePose.Pose.None || Shouted(kind) || !body.standing)) Say(body, Lang.T(line));
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
                case Kind.LookOut:
                    _points.Add(new PointLine { Owner = origin, Target = new Vector2(m.X, m.Y), At = Time.unscaledTime });
                    Coop.Get<PingSync>()?.Warn(origin, new Vector2(m.X, m.Y));
                    Log.Info("[gesture] " + Coop.Player(origin).Name + " warns of " + new Vector2(m.X, m.Y).ToString("F1"));
                    break;
                case Kind.Hug:
                    if (m.Target != Coop.LocalId) return;
                    var body = GameInfo.LocalBody;
                    if (body == null || !body.alive) return;
                    float before = body.happiness;
                    Hugged(origin, body);
                    GesturePose.Play(GesturePose.Pose.Hug);  // hugging back
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
                float gap = 14f * CoopWindow.Scale;
                var dot = PixelMarks.Size(PixelMarks.Dot);
                float phase = (now * 60f * CoopWindow.Scale) % gap;
                for (float t = phase; t < len; t += gap)
                {
                    var q = Vector2.Lerp(ga, gb, t / len);
                    GUI.DrawTexture(new Rect(Mathf.Round(q.x - dot.x / 2f), Mathf.Round(q.y - dot.y / 2f), dot.x, dot.y), PixelMarks.Dot);
                }
                var end = PixelMarks.Size(PixelMarks.Ring);
                GUI.DrawTexture(new Rect(Mathf.Round(gb.x - end.x / 2f), Mathf.Round(gb.y - end.y / 2f), end.x, end.y), PixelMarks.Ring);
                GUI.color = old;
            }
        }

        private const float BoxH = 30f, BoxGap = 10f;

        /// <summary>The wheel's half width and half height (scaled units); its boxes sit on this oval.</summary>
        private static float _rx = 125f, _ry = 125f;

        /// <summary>Where the n-th box sits from the centre: clockwise from the top.</summary>
        private static Vector2 Spot(int i)
        {
            float angle = i * (360f / Wheel.Length) * Mathf.Deg2Rad;
            return new Vector2(Mathf.Sin(angle) * _rx, -Mathf.Cos(angle) * _ry);
        }

        /// <summary>
        /// The smallest oval on which no two boxes overlap: wider first (it's the boxes'
        /// width that crowds them), taller once it would leave the screen.
        /// </summary>
        private static void Oval(float screenW)
        {
            float maxRx = Mathf.Max(125f, (screenW - _boxW) / 2f - 12f);
            for (_ry = 125f; _ry <= 400f; _ry += 10f)
                for (_rx = _ry; _rx <= maxRx; _rx += 8f)
                    if (!Crowded()) return;
            _rx = maxRx;
        }

        private static bool Crowded()
        {
            for (int i = 0; i < Wheel.Length; i++)
                for (int j = i + 1; j < Wheel.Length; j++)
                {
                    var d = Spot(i) - Spot(j);
                    if (Mathf.Abs(d.x) < _boxW + BoxGap && Mathf.Abs(d.y) < BoxH + BoxGap) return true;
                }
            return false;
        }

        private void DrawWheel()
        {
            if (_wheelStyle == null)
            {
                _wheelStyle = new GUIStyle(GUI.skin.box) { fontSize = UiSkin.Body, alignment = TextAnchor.MiddleCenter, wordWrap = false };
                // The pick is inverted, as a selected choice is everywhere in the overlay.
                _wheelSel = new GUIStyle(GUI.skin.button) { fontSize = UiSkin.Body, alignment = TextAnchor.MiddleCenter, wordWrap = false };
                _wheelSel.normal.background = _wheelSel.onNormal.background;
                _wheelSel.normal.textColor = _wheelSel.onNormal.textColor;
            }
            float k = CoopWindow.Scale;
            var oldMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(k, k, 1f));
            try
            {
                float sw = Screen.width / k, sh = Screen.height / k;
                // Every box as wide as the widest label (in the player's language), on an
                // oval just wide enough that no two boxes touch.
                if (_boxFor != Lang.Code + sw)
                {
                    _boxFor = Lang.Code + sw;
                    _boxW = 0f;
                    for (int i = 0; i < Wheel.Length; i++)
                        _boxW = Mathf.Max(_boxW, _wheelSel.CalcSize(new GUIContent(KeyLabel(i) + "  " + Lang.T(Wheel[i].label))).x);
                    _boxW += 10f;
                    Oval(sw);
                }
                string hint = _sticky ? Lang.T("Click a gesture (or press its key). Esc to close")
                                      : Lang.T("Move the mouse onto a gesture and let go");
                var hs = _wheelStyle.CalcSize(new GUIContent(hint));

                // Whole on screen, hint and all (the mouse may be near an edge). The picking
                // in Tick goes by this same centre.
                float halfW = _rx + _boxW / 2f + 6f, up = _ry + BoxH / 2f + 6f, down = _ry + BoxH + hs.y + 8f;
                var centre = new Vector2(_wheelCentre.x, Screen.height - _wheelCentre.y) / k;
                centre.x = Mathf.Clamp(centre.x, Mathf.Min(halfW, sw / 2f), Mathf.Max(sw - halfW, sw / 2f));
                centre.y = Mathf.Clamp(centre.y, Mathf.Min(up, sh / 2f), Mathf.Max(sh - down, sh / 2f));
                _wheelCentre = new Vector2(centre.x * k, Screen.height - centre.y * k);

                var old = GUI.color;
                for (int i = 0; i < Wheel.Length; i++)
                {
                    var at = centre + Spot(i);
                    var r = new Rect(Mathf.Round(at.x - _boxW / 2f), Mathf.Round(at.y - BoxH / 2f), _boxW, BoxH);
                    GUI.color = i == _choice ? Color.white : new Color(1f, 1f, 1f, 0.85f);
                    GUI.Box(r, KeyLabel(i) + "  " + Lang.T(Wheel[i].label), i == _choice ? _wheelSel : _wheelStyle);
                }
                GUI.color = old;
                GUI.Box(new Rect(Mathf.Round(centre.x - hs.x / 2f), Mathf.Round(centre.y + _ry + BoxH), hs.x, hs.y + 2f), hint, _wheelStyle);
            }
            finally { GUI.matrix = oldMatrix; }
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
