using System.Collections.Generic;
using CCoop.Net.Session;
using CCoop.Plugin.Protocol;
using CCoop.Plugin.Runtime;
using UnityEngine;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// Middle mouse marks a spot (unused by the game). Everyone on the layer sees a ring
    /// with the player's name for a few seconds, pointed at from the screen edge when it
    /// is off screen, with a short beep. The "Look out!" gesture leaves a warning mark
    /// instead: red, blinking, labelled as a warning, with a higher beep.
    /// </summary>
    internal sealed class PingSync : CoopModule
    {
        private const float Lifetime = 7f;
        private const float Cooldown = 0.4f;

        private struct Mark
        {
            public byte Owner;
            public Vector2 Position;
            public float At;
            public bool Warn;
        }

        /// <summary>A warning's colour: the overlay's red, whatever the player's colour.</summary>
        private static Color WarnColor => UI.UiSkin.Bad;

        private readonly List<Mark> _marks = new List<Mark>();
        private float _nextAllowed;

        internal static readonly Color[] Palette =
        {
            new Color(1f, 0.85f, 0.3f), new Color(0.4f, 0.85f, 1f), new Color(0.6f, 1f, 0.5f), new Color(1f, 0.5f, 0.8f),
            new Color(1f, 0.6f, 0.3f), new Color(0.7f, 0.6f, 1f), new Color(0.4f, 1f, 0.85f), new Color(1f, 1f, 1f),
        };

        /// <summary>Okabe and Ito's palette: distinct under the common forms of colour blindness.</summary>
        internal static readonly Color[] SafePalette =
        {
            new Color(0.90f, 0.62f, 0f), new Color(0.34f, 0.71f, 0.91f), new Color(0f, 0.62f, 0.45f), new Color(0.94f, 0.89f, 0.26f),
            new Color(0f, 0.45f, 0.70f), new Color(0.84f, 0.37f, 0f), new Color(0.80f, 0.47f, 0.65f), new Color(1f, 1f, 1f),
        };

        internal static Color ColorOf(byte id)
        {
            var p = CoopPlugin.Ready && CoopPlugin.Instance.ColorBlindColors.Value ? SafePalette : Palette;
            return p[id % p.Length];
        }

        internal override void Attach(NetSession s) => s.On<PingMark>((o, m) => Add(o, new Vector2(m.X, m.Y)));

        internal override void Detach() => _marks.Clear();

        internal override void OnWorldLost() => _marks.Clear();

        internal override void Tick()
        {
            if (!Coop.WorldLive || InputBlock.Blocking || Time.unscaledTime < _nextAllowed) return;
            if (!Input.GetMouseButtonDown(2)) return;
            var cam = Camera.main;
            if (cam == null) return;
            Vector2 at = cam.ScreenToWorldPoint(Input.mousePosition);
            _nextAllowed = Time.unscaledTime + Cooldown;
            Session.Broadcast(new PingMark { X = at.x, Y = at.y });
            Add(Coop.LocalId, at);
        }

        internal int Count => _marks.Count;

        /// <summary>Dev: ping a spot as if middle-clicked.</summary>
        internal void DevPing(Vector2 at)
        {
            Session.Broadcast(new PingMark { X = at.x, Y = at.y });
            Add(Coop.LocalId, at);
        }

        /// <summary>Screenshots: a mark as if <paramref name="owner"/> placed it (not sent).</summary>
        internal void DevMark(byte owner, Vector2 at, bool warn = false) => Add(owner, at, warn);

        /// <summary>A "Look out!" mark (from the gesture, which carries the spot itself).</summary>
        internal void Warn(byte owner, Vector2 at) => Add(owner, at, true);

        internal int Warnings => _marks.FindAll(m => m.Warn).Count;

        private void Add(byte owner, Vector2 at, bool warn = false)
        {
            if (!Coop.WorldLive) return;
            if (owner != Coop.LocalId) Log.Info("[ping] " + Coop.Player(owner).Name + (warn ? " warned of " : " marked ") + at.ToString("F1"));
            _marks.RemoveAll(m => m.Owner == owner);  // one mark per player
            _marks.Add(new Mark { Owner = owner, Position = at, At = Time.unscaledTime, Warn = warn });
            try { PlayerCamera.PlayUISound("scannerbeep", warn ? 1.7f : owner == Coop.LocalId ? 1.2f : 1f); }
            catch { /* sound is a nicety */ }
        }

        internal override void Gui()
        {
            if (_marks.Count == 0 || !Coop.WorldLive || ConditionFx.Blind) return;
            var cam = Camera.main;
            if (cam == null) return;
            float now = Time.unscaledTime;
            _marks.RemoveAll(m => now - m.At > Lifetime);

            var ring = UI.PixelMarks.Ring;
            var normal = UI.PixelMarks.Size(ring);
            var big = UI.PixelMarks.Size(ring, true);
            float px = UI.PixelMarks.Px;
            Vector2 me = GameInfo.LocalBody != null ? (Vector2)GameInfo.LocalBody.transform.position : (Vector2)cam.transform.position;
            foreach (var m in _marks)
            {
                float age = now - m.At;
                float fade = Mathf.Clamp01((Lifetime - age) / 1.5f);
                var color = m.Warn ? WarnColor : ColorOf(m.Owner);
                var size = m.Warn ? big : normal;
                Vector3 sp = cam.WorldToScreenPoint(m.Position);
                float x = sp.x, y = Screen.height - sp.y;
                bool onScreen = sp.z > 0 && x >= 0 && x <= Screen.width && y >= 0 && y <= Screen.height;
                string name = m.Owner == Coop.LocalId ? UI.Lang.T("You") : Coop.Player(m.Owner).Name;
                if (m.Warn) name = UI.Lang.F("Look out! ({0})", name);

                var old = GUI.color;
                if (!onScreen)
                {
                    // Pinned to the screen edge, toward the mark, with how far it is.
                    x = Mathf.Clamp(sp.z > 0 ? x : Screen.width - x, UI.PixelMarks.EdgeLeft, Screen.width - UI.PixelMarks.EdgeRight);
                    y = Mathf.Clamp(sp.z > 0 ? y : Screen.height - y, UI.PixelMarks.EdgeTop, Screen.height - UI.PixelMarks.EdgeBottom);
                    name += " · " + Mathf.RoundToInt(Vector2.Distance(me, m.Position)) + " m";
                }
                // A blink rather than a throb: pixel art doesn't scale smoothly.
                float blink = ConditionFx.Reduced || age > (m.Warn ? 3.5f : 1.2f) || Mathf.Repeat(age, 0.3f) < 0.18f ? 1f : 0.35f;
                GUI.color = new Color(color.r, color.g, color.b, fade * blink);
                GUI.DrawTexture(new Rect(Mathf.Round(x - size.x / 2f), Mathf.Round(y - size.y / 2f), size.x, size.y), ring);
                if (onScreen && age < 0.6f && !ConditionFx.Reduced)
                {
                    // A ring that grows out of it, in whole pixel steps.
                    float step = Mathf.Floor(1f + age * 5f) * px * ring.width;
                    GUI.color = new Color(color.r, color.g, color.b, (0.6f - age) * fade);
                    GUI.DrawTexture(new Rect(Mathf.Round(x - step / 2f), Mathf.Round(y - step / 2f), step, step), ring);
                }
                GUI.color = old;
                UI.PixelMarks.Label(new Vector2(x, y + size.y / 2f + 2f), name, color, fade);
            }
        }
    }
}
