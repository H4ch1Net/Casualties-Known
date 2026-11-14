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
    /// is off screen, with a short beep.
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
        }

        private readonly List<Mark> _marks = new List<Mark>();
        private float _nextAllowed;
        private static Texture2D _ring;
        private static GUIStyle _label;

        internal static readonly Color[] Palette =
        {
            new Color(1f, 0.85f, 0.3f), new Color(0.4f, 0.85f, 1f), new Color(0.6f, 1f, 0.5f), new Color(1f, 0.5f, 0.8f),
            new Color(1f, 0.6f, 0.3f), new Color(0.7f, 0.6f, 1f), new Color(0.4f, 1f, 0.85f), new Color(1f, 1f, 1f),
        };

        internal static Color ColorOf(byte id) => Palette[id % Palette.Length];

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

        private void Add(byte owner, Vector2 at)
        {
            if (!Coop.WorldLive) return;
            if (owner != Coop.LocalId) Log.Info("[ping] " + Coop.Player(owner).Name + " marked " + at.ToString("F1"));
            _marks.RemoveAll(m => m.Owner == owner);  // one mark per player
            _marks.Add(new Mark { Owner = owner, Position = at, At = Time.unscaledTime });
            try { PlayerCamera.PlayUISound("scannerbeep", owner == Coop.LocalId ? 1.2f : 1f); }
            catch { /* sound is a nicety */ }
        }

        internal override void Gui()
        {
            if (_marks.Count == 0 || !Coop.WorldLive || ConditionFx.Blind) return;
            var cam = Camera.main;
            if (cam == null) return;
            if (_ring == null) _ring = MakeRing(64, 6);
            if (_label == null)
            {
                _label = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperCenter, fontSize = 13, fontStyle = FontStyle.Bold };
            }
            float now = Time.unscaledTime;
            _marks.RemoveAll(m => now - m.At > Lifetime);

            Vector2 me = GameInfo.LocalBody != null ? (Vector2)GameInfo.LocalBody.transform.position : (Vector2)cam.transform.position;
            foreach (var m in _marks)
            {
                float age = now - m.At;
                float fade = Mathf.Clamp01((Lifetime - age) / 1.5f);
                var color = ColorOf(m.Owner);
                Vector3 sp = cam.WorldToScreenPoint(m.Position);
                float x = sp.x, y = Screen.height - sp.y;
                bool onScreen = sp.z > 0 && x >= 0 && x <= Screen.width && y >= 0 && y <= Screen.height;
                string name = m.Owner == Coop.LocalId ? "You" : Coop.Player(m.Owner).Name;

                var old = GUI.color;
                if (onScreen)
                {
                    float pulse = 1f + Mathf.Sin(age * 6f) * 0.12f;
                    float size = 34f * pulse;
                    GUI.color = new Color(color.r, color.g, color.b, fade);
                    GUI.DrawTexture(new Rect(x - size / 2f, y - size / 2f, size, size), _ring);
                    if (age < 0.6f)
                    {
                        float burst = 34f + age * 160f;
                        GUI.color = new Color(color.r, color.g, color.b, (0.6f - age) * fade);
                        GUI.DrawTexture(new Rect(x - burst / 2f, y - burst / 2f, burst, burst), _ring);
                    }
                    Label(new Rect(x - 100f, y + 20f, 200f, 20f), name, color, fade);
                }
                else
                {
                    // Pinned to the screen edge, toward the mark.
                    float cx = Mathf.Clamp(sp.z > 0 ? x : Screen.width - x, 30f, Screen.width - 30f);
                    float cy = Mathf.Clamp(sp.z > 0 ? y : Screen.height - y, 30f, Screen.height - 50f);
                    GUI.color = new Color(color.r, color.g, color.b, fade);
                    GUI.DrawTexture(new Rect(cx - 12f, cy - 12f, 24f, 24f), _ring);
                    int dist = Mathf.RoundToInt(Vector2.Distance(me, m.Position));
                    Label(new Rect(cx - 100f, cy + 13f, 200f, 20f), name + " · " + dist + " m", color, fade);
                }
                GUI.color = old;
            }
        }

        private static void Label(Rect r, string text, Color color, float alpha)
        {
            var old = GUI.color;
            GUI.color = Color.white;
            _label.normal.textColor = new Color(0f, 0f, 0f, alpha * 0.8f);
            GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), text, _label);
            _label.normal.textColor = new Color(color.r, color.g, color.b, alpha);
            GUI.Label(r, text, _label);
            GUI.color = old;
        }

        private static Texture2D MakeRing(int size, int thickness)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            float c = (size - 1) / 2f, outer = size / 2f - 1f, inner = outer - thickness;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                    float a = Mathf.Clamp01(outer - d + 0.5f) * Mathf.Clamp01(d - inner + 0.5f);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            tex.Apply();
            return tex;
        }
    }
}
