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
            (Kind.Wave, "Wave", "*waves*"),
            (Kind.ComeHere, "Come here", "*beckons you over*"),
            (Kind.Point, "Point", "*points*"),
            (Kind.Help, "Help!", "*calls for help!*"),
            (Kind.Hug, "Hug", null),
            (Kind.No, "No", "*shakes head*"),
            (Kind.Wait, "Wait", "*holds up a hand: wait*"),
            (Kind.ThumbsUp, "Thumbs up", "*gives a thumbs up*"),
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
            _wheelOpen = false;
        }

        // ------------------------------------------------------------------ doing it

        internal override void Tick()
        {
            if (!Coop.WorldLive || CoopPlugin.Instance == null) { _wheelOpen = false; return; }
            var key = CoopPlugin.Instance.GestureKey.Value;
            if (!_wheelOpen && !InputBlock.Blocking && Input.GetKeyDown(key))
            {
                _wheelOpen = true;
                _choice = -1;
                _wheelCentre = Input.mousePosition;
            }
            else if (_wheelOpen && !Input.GetKey(key))
            {
                _wheelOpen = false;
                if (_choice >= 0) Do(_choice);
            }
            if (_wheelOpen)
            {
                Vector2 d = (Vector2)Input.mousePosition - _wheelCentre;
                _choice = d.magnitude < 30f ? -1 : Sector(d);
            }
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
            if (body == null || !body.alive || !body.conscious) { CoopRuntime.Notice("You can't do that right now."); return; }
            var (kind, label, line) = Wheel[index];
            var msg = new GestureMsg { Kind = (byte)kind };

            if (kind == Kind.Hug)
            {
                var target = NearestTeammate(body, HugReach);
                if (target == null) { CoopRuntime.Notice("No one close enough to hug."); return; }
                msg.Target = target.Id;
                line = "*hugs " + Coop.Player(target.Id).Name + "*";
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

            if (line != null) Say(body, line);
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
                    CoopRuntime.Notice(Coop.Player(origin).Name + " hugged you");
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
                GUI.Box(r, Wheel[i].label, i == _choice ? _wheelSel : _wheelStyle);
            }
            GUI.color = old;
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
