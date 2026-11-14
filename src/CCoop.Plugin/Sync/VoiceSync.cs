using System;
using System.Collections.Generic;
using CCoop.Net.Session;
using CCoop.Plugin.Net;
using CCoop.Plugin.Protocol;
using CCoop.Plugin.Runtime;
using Steamworks;
using UnityEngine;
using CCoop.Plugin.UI;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// Proximity voice over Steam's voice API: Steam records, filters silence and
    /// compresses; we relay the packets through the session and play each speaker from
    /// their own source, quieter with distance and panned toward them. Playback goes
    /// through the game's sound mixer, so a listener underwater or passing out hears
    /// voices muffled like every other sound.
    ///
    /// Who can be heard follows the game: an unconscious player can't speak, and the
    /// dead talk only among themselves. Outside a layer (menus, loading) everyone hears
    /// everyone at full volume.
    /// </summary>
    internal sealed class VoiceSync : CoopModule
    {
        private const float FullVolumeWithin = 5f;
        private const float TalkingHold = 0.35f;

        private bool _recording;
        private bool _transmitting;
        private float _lastSent = -10f;
        private float _lastLoud = -10f;
        private byte[] _ownPcm = new byte[22050 * 2];

        /// <summary>The local mic's recent loudness (0..1), for the threshold meter.</summary>
        internal static float MicLevel;

        /// <summary>Open mic keeps sending this long after the last loud frame, so word endings aren't cut.</summary>
        private const float GateHold = 0.4f;
        private uint _rate;
        private byte[] _compressed = new byte[8 * 1024];
        private byte[] _pcm = new byte[64 * 1024];
        private float[] _samples = new float[32 * 1024];
        private readonly Dictionary<byte, VoicePlayer> _players = new Dictionary<byte, VoicePlayer>();

        internal static bool Available => SteamServices.Ready;

        internal static string Mode => CoopPlugin.Instance != null ? CoopPlugin.Instance.VoiceMode.Value : "Off";

        /// <summary>The local player is sending voice right now.</summary>
        internal bool Transmitting => _transmitting && Time.unscaledTime - _lastSent < TalkingHold;

        internal override void Attach(NetSession s)
        {
            s.On<VoiceData>(OnVoice);
        }

        internal override void Detach()
        {
            StopRecording();
            foreach (var p in _players.Values) p.Dispose();
            _players.Clear();
        }

        internal override void OnPeerLeft(PeerInfo peer)
        {
            if (_players.TryGetValue(peer.Id, out var p))
            {
                p.Dispose();
                _players.Remove(peer.Id);
            }
        }

        internal bool IsTalking(byte id) =>
            _players.TryGetValue(id, out var p) && Time.unscaledTime - p.LastHeard < TalkingHold;

        // ------------------------------------------------------------------ sending

        /// <summary>Whether the local player is trying to talk (key held, or open mic).</summary>
        private static bool WantsToTalk()
        {
            string mode = Mode;
            if (mode == "Off" || Chat.Typing || UI.CoopWindow.TypingInField) return false;
            if (mode == "Open") return true;
            return Input.GetKey(CoopPlugin.Instance.VoiceKey.Value);
        }

        /// <summary>The game's rule: the unconscious can't speak (the dead still can, to the dead).</summary>
        private static bool CanSpeak()
        {
            var body = GameInfo.LocalBody;
            return body == null || !body.alive || body.conscious;
        }

        internal override void Tick()
        {
            if (!Available || !Coop.InSession) { StopRecording(); return; }

            MicLevel = Mathf.MoveTowards(MicLevel, 0f, Time.unscaledDeltaTime * 0.3f);  // Steam sends nothing in silence
            _transmitting = WantsToTalk() && CanSpeak();
            if (_transmitting && !_recording)
            {
                SteamUser.StartVoiceRecording();
                _recording = true;
            }
            else if (!_transmitting && _recording) StopRecording();

            // Keep draining after release: Steam hands over the tail of the last words.
            try
            {
                for (int guard = 0; guard < 8; guard++)
                {
                    if (SteamUser.GetAvailableVoice(out uint size) != EVoiceResult.k_EVoiceResultOK || size == 0) break;
                    if (size > _compressed.Length) _compressed = new byte[size * 2];
                    var r = SteamUser.GetVoice(true, _compressed, (uint)_compressed.Length, out uint written);
                    if (r != EVoiceResult.k_EVoiceResultOK || written == 0) break;
                    var data = new byte[written];
                    Buffer.BlockCopy(_compressed, 0, data, 0, (int)written);
                    if (!PassesGate(data)) continue;
                    Session.Broadcast(new VoiceData { Data = data });
                    _lastSent = Time.unscaledTime;
                }
            }
            catch (Exception e) { Log.Debug("voice capture: " + e.Message); }
        }

        /// <summary>
        /// Open mic with a threshold: this player's own frame is decoded to measure it, and
        /// only sent if something loud was heard within the last <see cref="GateHold"/> s.
        /// </summary>
        private bool PassesGate(byte[] frame)
        {
            float gate = CoopPlugin.Instance.VoiceGate.Value;
            bool open = Mode == "Open";
            if (!open && !UI.CoopWindow.MeasuringMic) return true;
            float level = 0f;
            try
            {
                if (_rate == 0) _rate = SteamUser.GetVoiceOptimalSampleRate();
                var r = SteamUser.DecompressVoice(frame, (uint)frame.Length, _ownPcm, (uint)_ownPcm.Length, out uint written, _rate);
                if (r == EVoiceResult.k_EVoiceResultOK)
                {
                    int n = (int)written / 2;
                    double sum = 0;
                    for (int i = 0; i < n; i++)
                    {
                        float x = (short)(_ownPcm[2 * i] | (_ownPcm[2 * i + 1] << 8)) / 32768f;
                        sum += x * x;
                    }
                    level = n > 0 ? (float)Math.Sqrt(sum / n) : 0f;
                }
            }
            catch { return true; }  // can't measure: don't drop speech
            MicLevel = Mathf.Max(level, MicLevel * 0.8f);
            if (!open || gate <= 0f) return true;
            float now = Time.unscaledTime;
            if (level >= gate) _lastLoud = now;
            return now - _lastLoud <= GateHold;
        }

        private void StopRecording()
        {
            if (!_recording) return;
            _recording = false;
            try { SteamUser.StopVoiceRecording(); }
            catch { /* Steam shutting down */ }
        }

        private static GUIStyle _mic;

        internal override void Gui()
        {
            if (!Available || Mode == "Off") return;
            bool trying = WantsToTalk() && (Mode != "Open" || Transmitting || !CanSpeak());
            if (!trying) return;
            if (_mic == null)
            {
                _mic = new GUIStyle(GUI.skin.box) { fontSize = 13, alignment = TextAnchor.MiddleCenter };
                _mic.normal.textColor = Color.white;
            }
            bool speaking = CanSpeak();
            var old = GUI.color;
            GUI.color = speaking ? new Color(0.6f, 1f, 0.6f) : new Color(1f, 0.6f, 0.5f);
            GUI.Box(new Rect(Screen.width - 230, Screen.height - 120, 220, 26),
                    speaking ? (Mode == "Open" ? Lang.T("Open mic") : Lang.T("Talking")) : Lang.T("You can't speak while unconscious"), _mic);
            GUI.color = old;
        }

        // ------------------------------------------------------------------ receiving

        private void OnVoice(byte origin, VoiceData m)
        {
            if (origin == Coop.LocalId || !Available || Mode == "Off" || m.Data == null || m.Data.Length == 0) return;
            try
            {
                if (_rate == 0) _rate = SteamUser.GetVoiceOptimalSampleRate();
                uint written;
                var r = SteamUser.DecompressVoice(m.Data, (uint)m.Data.Length, _pcm, (uint)_pcm.Length, out written, _rate);
                if (r == EVoiceResult.k_EVoiceResultBufferTooSmall)
                {
                    _pcm = new byte[Math.Max(_pcm.Length * 2, (int)written)];
                    r = SteamUser.DecompressVoice(m.Data, (uint)m.Data.Length, _pcm, (uint)_pcm.Length, out written, _rate);
                }
                if (r != EVoiceResult.k_EVoiceResultOK || written < 2) return;

                int n = (int)written / 2;
                if (_samples.Length < n) _samples = new float[n];
                for (int i = 0; i < n; i++) _samples[i] = (short)(_pcm[2 * i] | (_pcm[2 * i + 1] << 8)) / 32768f;

                if (!_players.TryGetValue(origin, out var p))
                {
                    p = new VoicePlayer(origin, (int)_rate);
                    _players[origin] = p;
                }
                p.Push(_samples, n);
            }
            catch (Exception e) { Log.Debug("voice playback: " + e.Message); }
        }

        internal override void LateTick()
        {
            if (_players.Count == 0) return;
            float master = Mathf.Clamp(CoopPlugin.Instance.VoiceVolume.Value, 0f, 2f);
            float range = Mathf.Max(FullVolumeWithin + 1f, CoopPlugin.Instance.VoiceRange.Value * HostRules.Current.VoicePercent / 100f);

            var body = GameInfo.LocalBody;
            bool listenerDead = body != null && !body.alive;
            Vector2? ear = null;
            if (Coop.WorldLive && body != null)
                ear = LifeSync.Spectating ? (Coop.Get<LifeSync>()?.TargetPosition() ?? (Vector2)body.transform.position) : (Vector2)body.transform.position;
            var mixer = Coop.WorldLive && WorldGeneration.world != null ? WorldGeneration.world.soundMixerGroup : null;

            foreach (var kv in _players)
            {
                var rec = Coop.Player(kv.Key);
                float volume, pan = 0f;
                bool sameLayer = Coop.WorldLive && rec.HasPosition && rec.ReportedEpoch == Coop.Epoch;
                if (!rec.Alive && sameLayer)
                {
                    // Ghost talk: the dead hear each other anywhere; the living don't hear them.
                    volume = listenerDead ? 1f : 0f;
                }
                else if (ear.HasValue && sameLayer)
                {
                    float d = Vector2.Distance(ear.Value, rec.Position);
                    volume = 1f - Mathf.Clamp01((d - FullVolumeWithin) / (range - FullVolumeWithin));
                    pan = Mathf.Clamp((rec.Position.x - ear.Value.x) / (range * 0.5f), -1f, 1f) * 0.7f;
                }
                else if (Coop.WorldLive && rec.HasPosition)
                {
                    volume = 0f;  // on another layer
                }
                else
                {
                    volume = 1f;  // menus and loading screens: everyone hears everyone
                }
                var vit = Coop.Get<VitalsSync>()?.Of(kv.Key);
                bool jaw = vit != null && vit.Limbs.Count > 0 &&
                           (vit.Limbs[0].Signs & (Protocol.LimbSigns.Broken | Protocol.LimbSigns.Dislocated)) != 0;
                // Rock between you muffles a voice (and takes some of its volume).
                bool walled = ear.HasValue && sameLayer && rec.Alive && Walled(kv.Key, ear.Value, rec.Position);
                if (walled) volume *= 0.75f;
                float cutoff = jaw ? 900f : walled ? 1400f : 22000f;
                // The speaker's own condition, as the game garbles their words: under water
                // their voice is a deep burble, and brain damage roughens it.
                float rough = 0f;
                if (vit != null && rec.Alive)
                {
                    if ((vit.Signs & Protocol.BodySigns.Underwater) != 0)
                    {
                        cutoff = Mathf.Min(cutoff, 450f);
                        volume *= 0.7f;
                    }
                    if (vit.Brain < BrainDamaged) rough = Mathf.Lerp(0.25f, 0.75f, 1f - vit.Brain / BrainDamaged);
                }
                kv.Value.Set(volume * volume * master * ConditionFx.HearingFactor() * PlayerVolume(rec), pan, sameLayer && !listenerDead ? mixer : null, cutoff, rough);
            }
        }

        /// <summary>The game's "brain damaged" line for speech (Talker.brainDamaged).</summary>
        private const float BrainDamaged = 82.5f;

        // ================================================================== walls and personal volume

        private readonly Dictionary<byte, KeyValuePair<float, bool>> _wallCache = new Dictionary<byte, KeyValuePair<float, bool>>();

        private bool Walled(byte id, Vector2 ear, Vector2 mouth)
        {
            float now = Time.unscaledTime;
            if (_wallCache.TryGetValue(id, out var c) && now - c.Key < 0.2f) return c.Value;
            bool walled = false;
            try { walled = Physics2D.Linecast(ear + Vector2.up, mouth + Vector2.up, LayerMask.GetMask("Ground")); }
            catch { /* no physics yet */ }
            _wallCache[id] = new KeyValuePair<float, bool>(now, walled);
            return walled;
        }

        /// <summary>Volume levels a player can pick for each teammate: 100%, 50%, muted, 150%.</summary>
        internal static readonly float[] Levels = { 1f, 0.5f, 0f, 1.5f };

        private static Dictionary<string, float> _volumes;

        private static string VolumePath => System.IO.Path.Combine(System.IO.Path.Combine(Application.persistentDataPath, "CasualtiesCoop"), "voice-volumes.txt");

        /// <summary>Who a volume belongs to: their Steam id, or their name on LAN.</summary>
        private static string VolumeKey(PlayerRecord rec) => rec.SteamId != 0 ? "steam" + rec.SteamId : "name:" + rec.Name;

        private static Dictionary<string, float> Volumes
        {
            get
            {
                if (_volumes != null) return _volumes;
                _volumes = new Dictionary<string, float>();
                try
                {
                    if (System.IO.File.Exists(VolumePath))
                        foreach (var line in System.IO.File.ReadAllLines(VolumePath))
                        {
                            int eq = line.LastIndexOf('=');
                            if (eq > 0 && float.TryParse(line.Substring(eq + 1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
                                _volumes[line.Substring(0, eq)] = Mathf.Clamp(v, 0f, 2f);
                        }
                }
                catch (Exception e) { Log.Debug("voice volumes: " + e.Message); }
                return _volumes;
            }
        }

        internal static float PlayerVolume(PlayerRecord rec) =>
            rec != null && Volumes.TryGetValue(VolumeKey(rec), out var v) ? v : 1f;

        /// <summary>Steps a teammate's volume to the next level and remembers it.</summary>
        internal static void CycleVolume(PlayerRecord rec)
        {
            if (rec == null) return;
            float cur = PlayerVolume(rec);
            int i = Array.FindIndex(Levels, l => Mathf.Abs(l - cur) < 0.01f);
            float next = Levels[(i + 1) % Levels.Length];
            Volumes[VolumeKey(rec)] = next;
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(VolumePath));
                var lines = new List<string>();
                foreach (var kv in Volumes)
                    if (Mathf.Abs(kv.Value - 1f) > 0.01f) lines.Add(kv.Key + "=" + kv.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                System.IO.File.WriteAllLines(VolumePath, lines.ToArray());
            }
            catch (Exception e) { Log.Debug("voice volumes: " + e.Message); }
        }

        internal static string VolumeLabel(PlayerRecord rec)
        {
            float v = PlayerVolume(rec);
            return v <= 0.001f ? "Muted" : Mathf.RoundToInt(v * 100f) + "%";
        }

        // ================================================================== a speaker

        /// <summary>One remote speaker: a jitter buffer feeding a streaming clip.</summary>
        private sealed class VoicePlayer : IDisposable
        {
            private readonly object _lock = new object();
            private readonly float[] _ring;
            private readonly int _prime;
            private readonly int _maxLatency;
            private int _read, _count;
            private bool _primed;
            private readonly GameObject _go;
            private readonly AudioSource _source;

            internal float LastHeard;

            internal VoicePlayer(byte id, int rate)
            {
                _ring = new float[rate * 2];
                _prime = rate / 10;        // 100 ms before starting, to ride out jitter
                _maxLatency = rate * 4 / 10;  // never fall more than 400 ms behind
                _go = new GameObject("CasualtiesCoop.Voice." + id);
                UnityEngine.Object.DontDestroyOnLoad(_go);
                _source = _go.AddComponent<AudioSource>();
                _source.clip = AudioClip.Create("voice" + id, rate, 1, rate, true, Read);
                _source.loop = true;
                _source.spatialBlend = 0f;
                _source.volume = 0f;
                _source.Play();
            }

            internal void Push(float[] src, int n)
            {
                LastHeard = Time.unscaledTime;
                lock (_lock)
                {
                    for (int i = 0; i < n; i++)
                    {
                        int at = (_read + _count) % _ring.Length;
                        _ring[at] = src[i];
                        if (_count < _ring.Length) _count++;
                        else _read = (_read + 1) % _ring.Length;
                    }
                    if (_count > _maxLatency)
                    {
                        int drop = _count - _prime;
                        _read = (_read + drop) % _ring.Length;
                        _count -= drop;
                    }
                }
            }

            // Audio thread.
            private void Read(float[] data)
            {
                lock (_lock)
                {
                    if (!_primed && _count < _prime)
                    {
                        Array.Clear(data, 0, data.Length);
                        return;
                    }
                    _primed = true;
                    for (int i = 0; i < data.Length; i++)
                    {
                        if (_count > 0)
                        {
                            data[i] = _ring[_read];
                            _read = (_read + 1) % _ring.Length;
                            _count--;
                        }
                        else
                        {
                            data[i] = 0f;
                            _primed = false;
                        }
                    }
                }
            }

            private AudioLowPassFilter _muffle;
            private AudioDistortionFilter _rough;

            internal void Set(float volume, float pan, UnityEngine.Audio.AudioMixerGroup mixer, float cutoff, float rough)
            {
                if (_source == null) return;
                _source.volume = Mathf.Clamp01(volume);
                _source.panStereo = pan;
                if (_source.outputAudioMixerGroup != mixer) _source.outputAudioMixerGroup = mixer;
                if (_muffle == null) _muffle = _go.AddComponent<AudioLowPassFilter>();
                // Glide, so walking round a corner doesn't click.
                _muffle.cutoffFrequency = Mathf.Lerp(_muffle.cutoffFrequency, cutoff, 0.25f);
                if (rough > 0f && _rough == null) _rough = _go.AddComponent<AudioDistortionFilter>();
                if (_rough != null)
                {
                    _rough.distortionLevel = Mathf.Lerp(_rough.distortionLevel, rough, 0.1f);
                    _rough.enabled = _rough.distortionLevel > 0.01f;
                }
            }

            public void Dispose()
            {
                if (_go != null) UnityEngine.Object.Destroy(_go);
            }
        }
    }
}
