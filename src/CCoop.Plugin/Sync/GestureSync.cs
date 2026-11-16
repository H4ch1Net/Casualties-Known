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
    /// Gestures. Hold the gesture key for a wheel (see GestureWheel): wave, come here,
    /// follow me, help, hug, thumbs up, yes, no, shrug, wait. The character performs them
    /// (see GesturePose); one that can't be performed right now (climbing, lying down)
    /// shows as the character's own speech bubble ("*waves*" — the game never slurs a
    /// line in asterisks) instead. Help calls out with a marker teammates can see from
    /// anywhere; Hug lifts both players' mood (the game won't let a player who's too low
    /// treat wounds) and shares warmth. Pointing at a spot is the middle-click ping's job.
    /// The game's own bark key is heard by everyone too.
    /// </summary>
    internal sealed class GestureSync : CoopModule
    {
        // The byte is what travels, so kinds keep their numbers (Point and LookOut are
        // retired: middle click marks a spot).
        private enum Kind : byte { Wave, ComeHere, Wait, ThumbsUp, No, Point, Help, Hug, Bark, Yes, Shrug, LookOut, FollowMe }

        /// <summary>Clockwise from the top: getting about, the call for help, the hug, answers.</summary>
        private static readonly (Kind kind, string label, string line, string icon)[] Wheel =
        {
            (Kind.Wave, Lang.N("Wave"), Lang.N("*waves*"), "wave"),
            (Kind.ComeHere, Lang.N("Come here"), Lang.N("*beckons you over*"), "come_here"),
            (Kind.FollowMe, Lang.N("Follow me"), Lang.N("*waves you along*"), "follow_me"),
            (Kind.Help, Lang.N("Help!"), Lang.N("Help!"), "help"),
            (Kind.Hug, Lang.N("Hug"), null, "hug"),
            (Kind.ThumbsUp, Lang.N("Thumbs up"), Lang.N("*gives a thumbs up*"), "thumbs_up"),
            (Kind.Yes, Lang.N("Yes"), Lang.N("*nods*"), "yes"),
            (Kind.No, Lang.N("No"), Lang.N("*shakes head*"), "no"),
            (Kind.Shrug, Lang.N("Shrug"), Lang.N("*shrugs*"), "shrug"),
            (Kind.Wait, Lang.N("Wait"), Lang.N("*holds up a hand: wait*"), "wait"),
        };

        /// <summary>The key that picks the n-th gesture, as on a hotbar: 1 to 9, then 0.</summary>
        private static string KeyLabel(int i) => i < 9 ? (i + 1).ToString() : i == 9 ? "0" : "";

        private static bool KeyDown(int i)
        {
            if (i < 9) return Input.GetKeyDown(KeyCode.Alpha1 + i) || Input.GetKeyDown(KeyCode.Keypad1 + i);
            if (i == 9) return Input.GetKeyDown(KeyCode.Alpha0) || Input.GetKeyDown(KeyCode.Keypad0);
            return false;
        }

        private const float HugReach = 1.8f;
        private const float HugCooldown = 60f;
        private const float HugMood = 10f;

        private bool _wheelOpen;
        private Vector2 _wheelCentre;
        private int _choice = -1;
        private readonly Dictionary<byte, float> _lastHug = new Dictionary<byte, float>();
        private static readonly Dictionary<int, List<AudioClip>> BarkClips = new Dictionary<int, List<AudioClip>>();
        private static string[] _labels, _keys, _icons;
        private static string _labelsFor;

        internal override void Attach(NetSession s) => s.On<GestureMsg>(OnGesture);

        internal override void Detach()
        {
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
                    _wheelCentre = Placed(Input.mousePosition);
                    Log.Info("[gesture] wheel open");
                }
                return;
            }
            if (!_wheelOpen) return;

            Vector2 d = (Vector2)Input.mousePosition - _wheelCentre;
            _choice = _devChoice >= 0 ? _devChoice : d.magnitude < GestureWheel.DeadZone ? -1 : Sector(d);
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

        /// <summary>Around the mouse, but always whole on screen (screen pixels, y up), hint and all.</summary>
        private static Vector2 Placed(Vector2 at)
        {
            float r = GestureWheel.Reach, down = GestureWheel.ReachDown;
            return new Vector2(Mathf.Clamp(at.x, Mathf.Min(r, Screen.width / 2f), Mathf.Max(Screen.width - r, Screen.width / 2f)),
                               Mathf.Clamp(at.y, Mathf.Min(down, Screen.height / 2f), Mathf.Max(Screen.height - r, Screen.height / 2f)));
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
            _devChoice = -1;
            HoldingInput = false;
        }

        private static int Sector(Vector2 d)
        {
            // Sector 0 points straight up, then clockwise.
            float angle = Mathf.Atan2(d.x, d.y) * Mathf.Rad2Deg;
            if (angle < 0f) angle += 360f;
            return Mathf.RoundToInt(angle / (360f / Wheel.Length)) % Wheel.Length;
        }

        /// <summary>Screenshots: the wheel open in the middle of the screen (or shut), with <paramref name="choice"/> under the mouse.</summary>
        internal void DevWheel(bool open, int choice = -1)
        {
            if (!open) { CloseWheel(); return; }
            _wheelOpen = true;
            _sticky = true;
            _choice = choice;
            _devChoice = choice;
            _openedAt = Time.unscaledTime;
            _wheelCentre = new Vector2(Screen.width / 2f, Screen.height / 2f);
        }

        /// <summary>Screenshots: the choice to show whatever the real mouse is doing.</summary>
        private int _devChoice = -1;

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
                case Kind.ThumbsUp: return GesturePose.Pose.ThumbsUp;
                case Kind.Wait: return GesturePose.Pose.Wait;
                case Kind.No: return GesturePose.Pose.No;
                case Kind.Help: return GesturePose.Pose.Help;
                case Kind.Hug: return GesturePose.Pose.Hug;
                case Kind.Yes: return GesturePose.Pose.Yes;
                case Kind.Shrug: return GesturePose.Pose.Shrug;
                case Kind.FollowMe: return GesturePose.Pose.FollowMe;
                default: return GesturePose.Pose.None;
            }
        }

        private void Do(int index)
        {
            var body = GameInfo.LocalBody;
            if (body == null || !body.alive || !body.conscious) { CoopRuntime.Notice(Lang.T("You can't do that right now.")); return; }
            var (kind, label, line, _) = Wheel[index];
            var msg = new GestureMsg { Kind = (byte)kind };
            Vector2 toward = default;

            if (kind == Kind.Hug)
            {
                var target = NearestTeammate(body, HugReach);
                if (target == null) { CoopRuntime.Notice(Lang.T("No one close enough to hug.")); return; }
                msg.Target = target.Id;
                Hugged(target.Id, body);
            }
            else if (kind == Kind.FollowMe)
            {
                // Lead the way toward the mouse: turn that way first if it's behind.
                var cam = Camera.main;
                if (cam != null) toward = cam.ScreenToWorldPoint(Input.mousePosition);
            }
            else if (kind == Kind.Help)
            {
                Coop.Get<PingSync>()?.DevPing(body.transform.position);
            }

            // The body does it; a call for help is also said out loud, and a body that can't
            // move its arms just now (climbing, lying down) says what it would have done.
            var pose = PoseOf(kind);
            if (pose != GesturePose.Pose.None) GesturePose.Play(pose, toward);
            if (line != null && (pose == GesturePose.Pose.None || kind == Kind.Help || !body.standing)) Say(body, Lang.T(line));
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
            if (!_wheelOpen) return;
            // Labels in the player's language, looked up once per language.
            if (_labelsFor != Lang.Code || _labels == null)
            {
                _labelsFor = Lang.Code;
                _labels = new string[Wheel.Length];
                _keys = new string[Wheel.Length];
                _icons = new string[Wheel.Length];
                for (int i = 0; i < Wheel.Length; i++)
                {
                    _labels[i] = Lang.T(Wheel[i].label);
                    _keys[i] = KeyLabel(i);
                    _icons[i] = Wheel[i].icon;
                }
            }
            var centre = new Vector2(_wheelCentre.x, Screen.height - _wheelCentre.y);
            GestureWheel.Draw(centre, _labels, _keys, _icons, _choice, _sticky, Time.unscaledTime - _openedAt);
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
