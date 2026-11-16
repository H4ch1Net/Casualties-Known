using System;
using System.Collections.Generic;
using CCoop.Plugin.Protocol;
using CCoop.Plugin.Runtime;
using UnityEngine;

namespace CCoop.Plugin.Sync
{
    /// <summary>One remote player: buffered, interpolated puppet plus name and speech.</summary>
    internal sealed class RemotePlayer
    {
        private const float Delay = 0.12f;
        private const int MaxBuffer = 32;

        private struct Sample
        {
            public float Time;
            public PlayerState State;
        }

        private readonly byte _id;
        private readonly List<Sample> _buffer = new List<Sample>();
        private ProxyBuilder.Rig _rig;
        private int _character = -1;
        private int _builtCharacter = -1;
        private uint _lastSeq;
        private PlayerState _latest;
        private string[] _shownKit = new string[0];
        private readonly Dictionary<int, GameObject> _heldItems = new Dictionary<int, GameObject>();
        private string _speech;
        private float _speechUntil;
        private bool _visible;

        internal RemotePlayer(byte id) { _id = id; }

        internal Vector2 Position => _latest != null ? new Vector2(_latest.X, _latest.Y) : Vector2.zero;
        internal bool Visible => _visible && _rig != null;

        /// <summary>Dev pose shots: teammates out of the picture.</summary>
        internal static bool DevHidden;
        internal byte Id => _id;

        /// <summary>Where the puppet stands, for the spectator camera; null while hidden.</summary>
        internal Vector2? ShownAt => Visible && _rig.BodyRoot != null ? (Vector2)_rig.BodyRoot.position : (Vector2?)null;

        /// <summary>A readable name for limb <paramref name="i"/>, from the character's own limb objects.</summary>
        internal string LimbName(int i) => Medic.LimbTitle(i);

        internal bool FacingRight => _latest != null && (_latest.Flags & PlayerFlags.Right) != 0;

        /// <summary>The item ids in the player's two hand slots (0 and 1), as last seen.</summary>
        internal string[] HeldIds => _shownKit.Length >= 2 ? new[] { _shownKit[0], _shownKit[1] } : new string[0];

        /// <summary>Every item seen on them, by slot (hands, back, belt...); empty slots are null.</summary>
        internal string[] Kit => _shownKit;

        internal int LimbCount => Visible && _rig.Limbs != null ? _rig.Limbs.Length : 0;

        internal Vector2? LimbPosition(int i) =>
            Visible && _rig.Limbs != null && i >= 0 && i < _rig.Limbs.Length && _rig.Limbs[i] != null ? (Vector2)_rig.Limbs[i].position : (Vector2?)null;

        /// <summary>Index of the limb closest to <paramref name="at"/> within <paramref name="reach"/>, or -1.</summary>
        internal int NearestLimb(Vector2 at, float reach)
        {
            if (!Visible || _rig.Limbs == null) return -1;
            int best = -1;
            float bestDist = reach;
            for (int i = 0; i < _rig.Limbs.Length; i++)
            {
                var l = _rig.Limbs[i];
                if (l == null) continue;
                float d = Vector2.Distance(at, l.position);
                if (d < bestDist) { bestDist = d; best = i; }
            }
            return best;
        }

        private Heater _warmth;

        internal bool Warming => _warmth != null && _warmth.enabled;

        /// <summary>
        /// A living teammate is a small heat source, using the game's own Heater (it warms
        /// the local body only when it is close and colder than body temperature).
        /// </summary>
        private void AttachWarmth()
        {
            _warmth = null;
            if (_rig == null || _rig.Limbs == null) return;
            foreach (var limb in _rig.Limbs)
            {
                if (limb == null || limb.GetComponent<Renderer>() == null) continue;
                _warmth = limb.gameObject.AddComponent<Heater>();
                _warmth.maxDistance = 2.5f;
                _warmth.desiredTemp = 37f;
                _warmth.tempLerpStrength = 0.006f;
                _warmth.cooker = false;
                break;
            }
        }

        internal void SetCharacter(int character) => _character = character;

        internal void Receive(PlayerState m)
        {
            if (_lastSeq != 0 && m.Sequence <= _lastSeq && _lastSeq - m.Sequence < 1000000) return;
            _lastSeq = m.Sequence;
            _latest = m;
            _buffer.Add(new Sample { Time = Time.unscaledTime, State = m });
            if (_buffer.Count > MaxBuffer) _buffer.RemoveRange(0, _buffer.Count - MaxBuffer);
        }

        internal void Update(bool localInWorld, ushort epoch)
        {
            bool shouldShow = !DevHidden && localInWorld && _latest != null && (_latest.Flags & PlayerFlags.InWorld) != 0 &&
                              _latest.Epoch == epoch && Time.unscaledTime - _buffer[_buffer.Count - 1].Time < 5f;

            // The puppet is a scene object; a scene change destroys it under us.
            if (_rig != null && _rig.Root == null) Release();

            if (shouldShow && (_rig == null || _builtCharacter != EffectiveCharacter))
            {
                Release();
                _rig = ProxyBuilder.Build(EffectiveCharacter, _id.ToString());
                _builtCharacter = EffectiveCharacter;
                AttachWarmth();
                Log.Info("[player] showing " + Coop.Player(_id).Name + " as " + GameInfo.CharacterName(EffectiveCharacter) +
                         (_rig != null ? " (" + _rig.Limbs.Length + " limbs, " + _rig.Slots.Length + " slots)" : " FAILED"));
                _shownKit = new string[0];
            }
            if (_rig == null) return;
            if (_rig.Root.activeSelf != shouldShow) _rig.Root.SetActive(shouldShow);
            _visible = shouldShow;
            if (_warmth != null) _warmth.enabled = shouldShow && Coop.Player(_id).Alive;
            if (!shouldShow) return;
            Interpolate();
            HoldToAnchor();
        }

        /// <summary>
        /// Carrying, on the carrier's own screen: the carried teammate is drawn held by
        /// our real body, not where their last update (a ping ago) put them, so they don't
        /// trail behind every step. <see cref="PinLimb"/> is the limb that is held.
        /// </summary>
        internal Func<Vector2?> PinAnchor;
        internal int PinLimb = -1;

        private void HoldToAnchor()
        {
            if (PinAnchor == null || PinLimb < 0 || _rig == null || PinLimb >= _rig.Limbs.Length) return;
            var limb = _rig.Limbs[PinLimb];
            var at = PinAnchor();
            if (limb == null || !at.HasValue) return;
            Vector3 delta = (Vector3)(at.Value - (Vector2)limb.position);
            if (delta.sqrMagnitude > 64f) return;  // far off: something else is wrong; draw them where they are
            var body = _rig.BodyRoot;
            body.position += delta;
            foreach (var l in _rig.Limbs)
                if (l != null && !l.IsChildOf(body)) l.position += delta;
        }

        private int EffectiveCharacter => _character >= 0 ? _character : 0;

        private void Interpolate()
        {
            float renderTime = Time.unscaledTime - Delay;
            int older = -1;
            for (int i = _buffer.Count - 1; i >= 0; i--)
                if (_buffer[i].Time <= renderTime) { older = i; break; }

            if (older < 0) { Apply(_buffer[0].State, _buffer[0].State, 0f); return; }
            if (older >= _buffer.Count - 1)
            {
                Apply(_buffer[_buffer.Count - 1].State, _buffer[_buffer.Count - 1].State, 0f);
                if (_buffer.Count > 1) _buffer.RemoveRange(0, _buffer.Count - 1);
                return;
            }
            var a = _buffer[older];
            var b = _buffer[older + 1];
            float span = b.Time - a.Time;
            float t = span > 0.0001f ? Mathf.Clamp01((renderTime - a.Time) / span) : 0f;
            Apply(a.State, b.State, t);
            if (older > 0) _buffer.RemoveRange(0, older);
        }

        private void Apply(PlayerState a, PlayerState b, float t)
        {
            var body = _rig.BodyRoot;
            body.position = new Vector3(Mathf.Lerp(a.X, b.X, t), Mathf.Lerp(a.Y, b.Y, t), body.position.z);
            body.rotation = Quaternion.Euler(0f, 0f, Mathf.LerpAngle(a.Angle, b.Angle, t));
            var near = t < 0.5f ? a : b;
            var s = body.localScale;
            float mag = Mathf.Abs(s.x) < 0.0001f ? 1f : Mathf.Abs(s.x);
            bool flip = (near.Flags & PlayerFlags.BodyFlip) != 0;
            body.localScale = new Vector3(flip ? -mag : mag, s.y, s.z);

            int n = Mathf.Min(_rig.Limbs.Length, Mathf.Min(a.Limbs.Length, b.Limbs.Length));
            for (int i = 0; i < n; i++)
            {
                var limb = _rig.Limbs[i];
                if (limb == null) continue;
                var pa = a.Limbs[i];
                var pb = b.Limbs[i];
                var rel = new Vector3(Mathf.Lerp(pa.X, pb.X, t), Mathf.Lerp(pa.Y, pb.Y, t), 0f);
                var world = body.TransformPoint(rel);
                limb.position = new Vector3(world.x, world.y, limb.position.z);
                limb.rotation = body.rotation * Quaternion.Euler(0f, 0f, Mathf.LerpAngle(pa.Angle, pb.Angle, t));

                byte f = (t < 0.5f ? pa : pb).Flags;
                bool wantNeg = (f & 1) != 0;
                if ((limb.lossyScale.x < 0) != wantNeg)
                {
                    var ls = limb.localScale;
                    limb.localScale = new Vector3(-ls.x, ls.y, ls.z);
                }
                var sr = _rig.LimbRenderers[i] as SpriteRenderer;
                if (sr != null) sr.flipX = (f & 2) != 0;
            }
        }

        internal void ApplyLook(PlayerLook m)
        {
            if (_rig == null || m == null) return;
            var props = PlayerSync.LimbShaderProps;
            var scale = PlayerSync.LimbShaderScale;
            int limbs = Mathf.Min(_rig.LimbRenderers.Length, m.Limbs.Length / PlayerLook.PerLimb);
            for (int i = 0; i < limbs; i++)
            {
                var mat = _rig.LimbRenderers[i] != null ? _rig.LimbRenderers[i].sharedMaterial : null;
                if (mat == null) continue;
                for (int k = 0; k < props.Length; k++)
                    if (mat.HasProperty(props[k])) mat.SetFloat(props[k], m.Limbs[i * PlayerLook.PerLimb + k] / 255f * scale[k]);
                if (mat.HasProperty("_Wetness")) mat.SetFloat("_Wetness", m.Wet / 255f);
                if (mat.HasProperty("_SnowAmount")) mat.SetFloat("_SnowAmount", m.Snow / 255f);
                if (mat.HasProperty("_Dirtyness")) mat.SetFloat("_Dirtyness", m.Dirt / 255f);
            }
        }

        internal void ApplyKit(PlayerKit kit)
        {
            if (_rig == null || kit == null) return;
            int slots = _rig.Slots.Length;
            var want = new string[slots];
            foreach (var kv in kit.Items) if (kv.Key < slots) want[kv.Key] = kv.Value;
            if (_shownKit.Length != slots) _shownKit = new string[slots];
            for (int i = 0; i < slots; i++)
            {
                if (string.Equals(want[i], _shownKit[i], StringComparison.Ordinal)) continue;
                if (_heldItems.TryGetValue(i, out var old) && old != null) UnityEngine.Object.Destroy(old);
                _heldItems.Remove(i);
                _shownKit[i] = want[i];
                if (string.IsNullOrEmpty(want[i]) || _rig.Slots[i] == null) continue;
                var go = ProxyBuilder.BuildHeldItem(want[i]);
                if (go == null) continue;
                PlaceInSlot(go, i, want[i]);
                _heldItems[i] = go;
            }
        }

        /// <summary>How Body.PickUpItem seats an item in a slot.</summary>
        private void PlaceInSlot(GameObject go, int slot, string itemId)
        {
            var info = Item.GetItem(itemId);
            float rot = info != null ? info.slotRotation : 0f;
            if (info != null && info.HasTag("backflip") && slot > 2) rot -= 90f;
            go.transform.SetParent(_rig.Slots[slot], false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localEulerAngles = new Vector3(0f, 0f, rot);
            var sr = go.GetComponent<SpriteRenderer>();
            if (sr != null) sr.sortingOrder = _rig.SlotSort[slot];
            go.SetActive(true);
        }

        internal static int MuzzleFlashes;

        /// <summary>
        /// This teammate fired from <paramref name="at"/>: their held gun flashes. The held
        /// copy keeps the gun's particle systems (only its scripts are stripped), and the
        /// muzzle is the one nearest the shot's start.
        /// </summary>
        internal void FlashMuzzle(Vector2 at)
        {
            if (!Visible) return;
            ParticleSystem best = null;
            float bestDist = float.MaxValue;
            foreach (var go in _heldItems.Values)
            {
                if (go == null) continue;
                foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true))
                {
                    float d = Vector2.Distance(ps.transform.position, at);
                    if (d < bestDist) { bestDist = d; best = ps; }
                }
            }
            if (best == null || bestDist > 4f) return;
            try
            {
                best.Play(true);
                MuzzleFlashes++;
            }
            catch (Exception e) { Log.Debug("muzzle flash: " + e.Message); }
        }

        internal void PlayAttack(PlayerAttack m)
        {
            if (!Visible) return;
            var origin = _rig.Limbs.Length > 1 && _rig.Limbs[1] != null ? _rig.Limbs[1].position : _rig.BodyRoot.position;
            try
            {
                var prefab = Resources.Load<GameObject>(m.Anim);
                if (prefab != null)
                {
                    var anim = UnityEngine.Object.Instantiate(prefab);
                    anim.transform.eulerAngles = new Vector3(0f, 0f, m.Rotation);
                    anim.transform.localScale = new Vector3(m.Right ? 1f : -1f, 1f, 1f);
                    anim.transform.position = origin;
                }
                if (!string.IsNullOrEmpty(m.Sound)) Sound.Play(m.Sound, origin);
            }
            catch (Exception e) { Log.Debug("remote attack: " + e.Message); }
        }

        internal void Say(string text)
        {
            _speech = text;
            _speechUntil = Time.unscaledTime + Mathf.Clamp(1.5f + text.Length * 0.06f, 2f, 8f);
            _talkAt = Time.unscaledTime;
            _talkShown = 0;
        }

        // ---- words and name in the game's own speech text (world space, like the game's)

        private TMPro.TextMeshPro _nameText, _talkText;
        private float _talkAt;
        private int _talkShown;

        /// <summary>
        /// Every frame: the name above the head (when shown) and, while they speak, their
        /// words typed out above it at the game's pace, 18 letters a second with the
        /// game's letter sound, held for three seconds after the last.
        /// </summary>
        internal void UpdateTexts(bool showName)
        {
            if (!Visible || _rig.Limbs.Length == 0 || _rig.Limbs[0] == null)
            {
                if (_nameText != null) _nameText.gameObject.SetActive(false);
                if (_talkText != null) _talkText.gameObject.SetActive(false);
                return;
            }
            Vector3 head = _rig.Limbs[0].position;
            var rec = Coop.Player(_id);
            if (showName)
            {
                if (_nameText == null) _nameText = UI.GameText.World("Name" + _id);
                if (_nameText != null)
                {
                    var t = UI.GameText.Template;
                    _nameText.gameObject.SetActive(true);
                    // The game's own speech size: any smaller and the world's coarse pixels eat the letters.
                    _nameText.fontSize = t != null ? t.fontSize : 8f;
                    string state = !rec.Alive ? UI.Lang.T("dead") : rec.Conscious ? "" : rec.Sleeping ? UI.Lang.T("asleep") : UI.Lang.T("out cold");
                    var voice = Coop.Get<VoiceSync>();
                    string label = rec.Name + (state.Length > 0 ? " (" + state + ")" : "") + (voice != null && voice.IsTalking(_id) ? " ((o))" : "");
                    if (_nameText.text != label) _nameText.text = label;
                    var c = UI.GameText.Colour;
                    _nameText.color = rec.Alive ? new Color(c.r, c.g, c.b, 0.85f) : new Color(0.6f, 0.6f, 0.6f, 0.85f);
                    _nameText.transform.position = new Vector3(head.x, head.y + 2.2f, head.z - 2.6f);
                }
            }
            else if (_nameText != null) _nameText.gameObject.SetActive(false);

            float since = Time.unscaledTime - _talkAt;
            bool talking = !string.IsNullOrEmpty(_speech) && since <= _speech.Length / 18f + 3f;
            if (!talking)
            {
                if (_talkText != null) _talkText.gameObject.SetActive(false);
                return;
            }
            if (_talkText == null) _talkText = UI.GameText.World("Talk" + _id);
            if (_talkText == null) return;
            _talkText.gameObject.SetActive(true);
            int n = Mathf.Min(_speech.Length, (int)(since * 18f));
            if (n > _talkShown)
            {
                // The game's letter sound for each new letter, not for "*waves*" or "(thinks)".
                bool quiet = _speech.StartsWith("*") || _speech.StartsWith("(");
                if (!quiet)
                    for (int i = _talkShown; i < n; i++)
                        if (char.IsLetterOrDigit(_speech[i]))
                        {
                            try { Sound.Play(UI.GameText.LetterSound(EffectiveCharacter), head); } catch { /* no sound */ }
                            break;  // one sound a frame, as the game does
                        }
                _talkShown = n;
                _talkText.text = _speech.Substring(0, n);
            }
            _talkText.transform.position = new Vector3(head.x, head.y + 4f, head.z - 2.6f);
        }

        private void DropTexts()
        {
            if (_nameText != null) UnityEngine.Object.Destroy(_nameText.gameObject);
            if (_talkText != null) UnityEngine.Object.Destroy(_talkText.gameObject);
            _nameText = _talkText = null;
        }

        private static GUIStyle _name, _talk;

        internal void DrawLabel(Camera cam)
        {
            if (!Visible) return;
            if (_name == null)
            {
                _name = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = UI.UiSkin.Small, wordWrap = false };
                _talk = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleCenter, fontSize = UI.UiSkin.Small, wordWrap = true };
            }
            var head = _rig.Limbs.Length > 0 && _rig.Limbs[0] != null ? _rig.Limbs[0].position : _rig.BodyRoot.position;
            var sp = cam.WorldToScreenPoint(head + Vector3.up * 1.3f);
            if (sp.z < 0) return;
            float x = sp.x, y = Screen.height - sp.y;
            var rec = Coop.Player(_id);
            string label = rec.Name + (!rec.Alive ? " (dead)" : rec.Conscious ? "" : rec.Sleeping ? " (asleep)" : " (out cold)");
            var voice = Coop.Get<VoiceSync>();
            if (voice != null && voice.IsTalking(_id)) label += "  ((o))";
            // White on a one-pixel black outline, as the game writes over its world.
            var nameStyle = UI.UiSkin.Fit(_name, label);
            var old = nameStyle.normal.textColor;
            nameStyle.normal.textColor = Color.black;
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    if (dx != 0 || dy != 0) GUI.Label(new Rect(x - 100 + dx, y - 10 + dy, 200, 20), label, nameStyle);
            nameStyle.normal.textColor = rec.Alive ? UI.UiSkin.Ink : UI.UiSkin.Bad;
            GUI.Label(new Rect(x - 100, y - 10, 200, 20), label, nameStyle);
            nameStyle.normal.textColor = old;

            var avatar = Net.SteamServices.Avatar(rec.SteamId);
            if (avatar != null)
            {
                float half = nameStyle.CalcSize(new GUIContent(label)).x / 2f;
                GUI.DrawTexture(new Rect(x - half - 22, y - 9, 18, 18), avatar);
            }

            if (_speech != null && Time.unscaledTime < _speechUntil)
                GUI.Box(new Rect(x - 130, y - 52, 260, 38), _speech, UI.UiSkin.Fit(_talk, _speech));
        }

        private void Release()
        {
            DropTexts();
            foreach (var go in _heldItems.Values) if (go != null) UnityEngine.Object.Destroy(go);
            _heldItems.Clear();
            ProxyBuilder.Destroy(_rig);
            _rig = null;
            _visible = false;
        }

        internal void Dispose() => Release();

        internal string Describe()
        {
            if (_rig == null) return Coop.Player(_id).Name + " no puppet";
            var head = _rig.Limbs.Length > 0 && _rig.Limbs[0] != null ? (Vector2)_rig.Limbs[0].position : Vector2.zero;
            return Coop.Player(_id).Name + " visible=" + Visible + " body=" + ((Vector2)_rig.BodyRoot.position).ToString("F1") +
                   " reported=" + Position.ToString("F1") + " head=" + head.ToString("F1");
        }
    }
}
