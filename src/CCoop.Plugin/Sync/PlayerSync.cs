using System;
using System.Collections.Generic;
using CCoop.Net.Session;
using CCoop.Plugin.Protocol;
using CCoop.Plugin.Runtime;
using HarmonyLib;
using UnityEngine;
using CCoop.Plugin.UI;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// The local player out, everyone else in: movement and pose at 20 Hz, wounds and
    /// kit at 2 Hz, swings and speech as they happen. Remote players are puppets built
    /// from their chosen character, shown only while they are on our layer.
    /// </summary>
    internal sealed class PlayerSync : CoopModule
    {
        private const float StateInterval = 1f / 20f;
        private const float SlowInterval = 0.5f;

        // Interest management: full rate to teammates who can see you (or are spectating),
        // a quarter of it to those far away. It keeps 6-8 player sessions affordable.
        private const float InterestRange = 48f;
        private const int FarEvery = 4;
        private readonly Dictionary<int, int> _relayCount = new Dictionary<int, int>();

        private readonly Dictionary<byte, RemotePlayer> _remotes = new Dictionary<byte, RemotePlayer>();
        private float _nextState, _nextSlow, _nextProfile;
        private uint _seq;
        private int _sentIntellect = -2;
        private int _sentCharacter = -1;

        internal IEnumerable<RemotePlayer> Remotes => _remotes.Values;

        internal RemotePlayer Find(byte id) => _remotes.TryGetValue(id, out var r) ? r : null;

        internal override void Attach(NetSession s)
        {
            s.On<PlayerProfile>(OnProfile);
            s.On<PlayerState>(OnState);
            s.On<PlayerLook>((o, m) => Remote(o)?.ApplyLook(m));
            s.On<PlayerKit>((o, m) => Remote(o)?.ApplyKit(m));
            s.On<PlayerAttack>((o, m) => Remote(o)?.PlayAttack(m));
            s.On<PlayerTalk>((o, m) =>
            {
                string heard = Hearing.Heard(o, m.Text);
                if (heard != null) Remote(o)?.Say(heard);
            });
            s.Established += () => _nextProfile = 0;
        }

        internal override void Detach()
        {
            foreach (var r in _remotes.Values) r.Dispose();
            _remotes.Clear();
            _sentCharacter = -1;
            _sentIntellect = -2;
            _relayCount.Clear();
        }

        internal override void OnPeerJoined(PeerInfo peer)
        {
            _nextProfile = 0;  // re-announce so the newcomer learns our character
        }

        internal override void OnPeerLeft(PeerInfo peer)
        {
            if (_remotes.TryGetValue(peer.Id, out var r))
            {
                r.Dispose();
                _remotes.Remove(peer.Id);
            }
        }

        private RemotePlayer Remote(byte id)
        {
            if (id == Coop.LocalId) return null;
            if (!_remotes.TryGetValue(id, out var r))
            {
                r = new RemotePlayer(id);
                _remotes[id] = r;
            }
            return r;
        }

        internal static float LocalFps;

        private void OnProfile(byte origin, PlayerProfile m)
        {
            var rec = Coop.Player(origin);
            rec.Character = m.Character;
            rec.Fps = m.Fps;
            rec.Intellect = m.Intellect == 255 ? -1 : m.Intellect;

            // Over Steam the host knows each client's real Steam id from the connection
            // itself. A client claiming another id is lying (or broken) and is removed, so
            // every id the host relays onward has been checked.
            bool steam = Coop.Runtime != null && Coop.Runtime.TransportKind == "Steam";
            if (steam && Coop.IsHost && origin != NetSession.HostId && Session.TryGetPeer(origin, out var peer)
                && ulong.TryParse(peer.Address, out var verified) && verified != 0)
            {
                if (m.SteamId != 0 && m.SteamId != verified)
                {
                    Log.Warn("[identity] " + peer.Name + " claimed Steam id " + m.SteamId + " but connected as " + verified);
                    Session.Kick(origin, Lang.N("Your Steam identity did not match your connection."));
                    return;
                }
                m.SteamId = verified;
            }

            bool fresh = rec.SteamId != m.SteamId;
            rec.SteamId = m.SteamId;
            if (fresh && steam) Net.SteamServices.PlayedWith(m.SteamId);
            Remote(origin)?.SetCharacter(m.Character);
        }

        private void OnState(byte origin, PlayerState m)
        {
            var rec = Coop.Player(origin);
            rec.Position = new Vector2(m.X, m.Y);
            rec.HasPosition = (m.Flags & PlayerFlags.InWorld) != 0;
            rec.Alive = (m.Flags & PlayerFlags.Alive) != 0;
            rec.Conscious = (m.Flags & PlayerFlags.Conscious) != 0;
            rec.Sleeping = (m.Flags & PlayerFlags.Sleeping) != 0;
            rec.Dying = (m.Flags & PlayerFlags.Dying) != 0;
            bool finished = (m.Flags & PlayerFlags.Finished) != 0;
            if (finished && !rec.Finished)
                CoopRuntime.Notice(origin == NetSession.HostId ? Lang.F("{0} has reached an ending; the run goes on until you finish too", rec.Name) : Lang.F("{0} has reached an ending", rec.Name));
            rec.Finished = finished;
            rec.LastStateTime = Time.unscaledTime;
            rec.ReportedEpoch = m.Epoch;
            if (!Coop.IsHost) rec.Status = rec.HasPosition ? LifeStatus(rec) : Lang.N("menu");
            else if (rec.InWorld && rec.HasPosition) rec.Status = LifeStatus(rec);
            Remote(origin)?.Receive(m);
            if (Coop.IsHost && origin != Coop.LocalId) SendByInterest(origin, m);
        }

        /// <summary>Host: passes a player's state on to everyone else, at a rate set by distance.</summary>
        private void SendByInterest(byte origin, PlayerState m)
        {
            var from = new Vector2(m.X, m.Y);
            bool placed = (m.Flags & PlayerFlags.InWorld) != 0;
            foreach (var rec in Coop.Players.Values)
            {
                if (rec.Id == origin || rec.Id == NetSession.HostId) continue;
                int key = (origin << 8) | rec.Id;
                _relayCount.TryGetValue(key, out var n);
                _relayCount[key] = n + 1;
                bool near = !placed || !rec.HasPosition || !rec.Alive || Vector2.Distance(from, rec.Position) < InterestRange;
                if (!near && n % FarEvery != 0) continue;
                if (origin == Coop.LocalId) Session.SendTo(rec.Id, m);
                else Session.RelayTo(rec.Id, origin, m);
            }
        }

        internal override void Tick()
        {
            if (!Coop.InSession) return;
            float now = Time.unscaledTime;
            var body = GameInfo.LocalBody;

            // A smoothed frame rate for the player list (sent with the profile).
            if (Time.unscaledDeltaTime > 0f) LocalFps = Mathf.Lerp(LocalFps <= 0f ? 60f : LocalFps, 1f / Time.unscaledDeltaTime, 0.02f);
            Coop.Player(Coop.LocalId).Fps = Mathf.RoundToInt(LocalFps);
            int character = body != null ? body.charType : PreRunScript.characterSelected;
            int intellect = body != null && body.skills != null && Coop.WorldLive ? body.skills.INT : -1;
            if (now >= _nextProfile || character != _sentCharacter || intellect != _sentIntellect)
            {
                _nextProfile = now + 10f;
                if (intellect != _sentIntellect && intellect >= 0)
                    Log.Info("[player] playing " + GameInfo.CharacterName(character) + " (character " + character + "), intelligence " + intellect);
                _sentCharacter = character;
                _sentIntellect = intellect;
                Coop.Player(Coop.LocalId).Character = character;
                Coop.Player(Coop.LocalId).Intellect = intellect;
                Coop.Player(Coop.LocalId).SteamId = GameInfo.SteamId;
                Session.Broadcast(new PlayerProfile
                {
                    Character = (byte)Mathf.Clamp(character, 0, 255),
                    SteamId = GameInfo.SteamId,
                    Fps = (byte)Mathf.Clamp(Mathf.RoundToInt(LocalFps), 0, 255),
                    Intellect = intellect < 0 ? (byte)255 : (byte)Mathf.Clamp(intellect, 0, 254),
                });
            }

            if (now >= _nextState)
            {
                _nextState = now + StateInterval;
                var state = BuildState(body);
                // Through the host, which sends it on by distance.
                if (Coop.IsHost) SendByInterest(Coop.LocalId, state);
                else Session.SendToHost(state);
            }

            if (now >= _nextSlow && body != null && Coop.WorldLive)
            {
                _nextSlow = now + SlowInterval;
                var look = BuildLook(body);
                if (look != null) Session.Broadcast(look);
                Session.Broadcast(BuildKit(body));
            }

            bool sameLayer = Coop.WorldLive;
            foreach (var r in _remotes.Values) r.Update(sameLayer, Coop.Epoch);

            var me = Coop.Player(Coop.LocalId);
            if (body != null && Coop.WorldLive)
            {
                me.HasPosition = true;
                me.Alive = body.alive;
                me.Conscious = body.conscious;
                me.Sleeping = body.sleeping;
                me.Position = body.transform.position;
                me.Status = LifeStatus(me);
            }
            else
            {
                me.HasPosition = false;
                me.Status = GameInfo.InRunScene ? "loading" : "menu";
            }
        }

        internal static string LifeStatus(PlayerRecord rec) =>
            !rec.Alive ? Lang.N("dead") : rec.Conscious ? Lang.N("in game") : rec.Sleeping ? Lang.N("asleep") : Lang.N("out cold");

        private PlayerState BuildState(Body body)
        {
            var m = new PlayerState { Sequence = ++_seq, Epoch = Coop.Epoch };
            if (body == null || !Coop.WorldLive) return m;
            var t = body.transform;
            m.Flags = PlayerFlags.InWorld;
            if (body.conscious) m.Flags |= PlayerFlags.Conscious;
            if (body.alive) m.Flags |= PlayerFlags.Alive;
            if (body.isRight) m.Flags |= PlayerFlags.Right;
            if (body.sleeping) m.Flags |= PlayerFlags.Sleeping;
            if (body.alive && body.brainDying) m.Flags |= PlayerFlags.Dying;
            if (UI.TeamReceipt.ReachedEnding) m.Flags |= PlayerFlags.Finished;
            if (t.lossyScale.x < 0) m.Flags |= PlayerFlags.BodyFlip;
            m.X = t.position.x;
            m.Y = t.position.y;
            m.Angle = t.eulerAngles.z;
            if (body.rb != null)
            {
                m.VX = body.rb.velocity.x;
                m.VY = body.rb.velocity.y;
            }
            m.Blood = (byte)Mathf.Clamp(Mathf.RoundToInt(body.bloodVolume), 0, 255);

            var limbs = body.limbs;
            if (limbs != null)
            {
                int n = Mathf.Min(limbs.Length, PlayerState.MaxLimbs);
                m.Limbs = new LimbPose[n];
                var inv = Quaternion.Inverse(t.rotation);
                for (int i = 0; i < n; i++)
                {
                    var l = limbs[i];
                    if (l == null) continue;
                    var lt = l.transform;
                    var rel = t.InverseTransformPoint(lt.position);
                    var rot = (inv * lt.rotation).eulerAngles.z;
                    byte flags = 0;
                    if (lt.lossyScale.x < 0) flags |= 1;
                    var sr = l.GetComponent<SpriteRenderer>();
                    if (sr != null && sr.flipX) flags |= 2;
                    m.Limbs[i] = new LimbPose { X = rel.x, Y = rel.y, Angle = rot, Flags = flags };
                }
            }
            return m;
        }

        private static readonly string[] LimbProps = { "_SkinDamage", "_MuscleDamage", "_InfectionPercent", "_Pain", "_BloodOverlay" };
        // Full-scale value of each property as Limb.Update writes it (skin 0-100,
        // muscle up to 1000 with a prosthetic, infection and pain 0-1, blood 0-10).
        private static readonly float[] LimbScale = { 100f, 1000f, 1f, 1f, 10f };

        internal static string[] LimbShaderProps => LimbProps;
        internal static float[] LimbShaderScale => LimbScale;

        private static PlayerLook BuildLook(Body body)
        {
            if (body.limbs == null) return null;
            var m = new PlayerLook();
            var bytes = new List<byte>();
            bool tookBody = false;
            foreach (var limb in body.limbs)
            {
                var mat = limb != null ? limb.GetComponent<Renderer>()?.sharedMaterial : null;
                for (int k = 0; k < LimbProps.Length; k++)
                {
                    float v = mat != null && mat.HasProperty(LimbProps[k]) ? mat.GetFloat(LimbProps[k]) : 0f;
                    bytes.Add((byte)Mathf.Clamp(Mathf.RoundToInt(v / LimbScale[k] * 255f), 0, 255));
                }
                if (!tookBody && mat != null)
                {
                    tookBody = true;
                    m.Wet = Unit(mat, "_Wetness");
                    m.Snow = Unit(mat, "_SnowAmount");
                    m.Dirt = Unit(mat, "_Dirtyness");
                }
            }
            m.Limbs = bytes.ToArray();
            return m;
        }

        private static byte Unit(Material m, string prop) =>
            m.HasProperty(prop) ? (byte)Mathf.Clamp(Mathf.RoundToInt(m.GetFloat(prop) * 255f), 0, 255) : (byte)0;

        private static PlayerKit BuildKit(Body body)
        {
            var kit = new PlayerKit();
            if (body.slots == null) return kit;
            for (int i = 0; i < body.slots.Length && i < 64; i++)
            {
                Item item = null;
                try { item = body.GetItem(i); } catch { /* slot without item */ }
                if (item != null && !string.IsNullOrEmpty(item.id)) kit.Items.Add(new KeyValuePair<byte, string>((byte)i, item.id));
            }
            return kit;
        }

        internal void AnnounceAttack(Body body, AttackInfo atk)
        {
            if (!Coop.WorldLive || atk == null || atk.attackAnim == null) return;
            float rot = 0f;
            if (body.limbs != null && body.limbs.Length > 1 && body.limbs[1] != null)
            {
                Vector2 from = body.limbs[1].transform.position;
                Vector2 dir = ((Vector2)body.targetLookPos - from).normalized;
                rot = Vector2.SignedAngle(body.isRight ? Vector3.right : Vector3.left, dir);
            }
            string sound = atk.swingSounds != null && atk.swingSounds.Length > 0
                ? atk.swingSounds[UnityEngine.Random.Range(0, atk.swingSounds.Length)] ?? "" : "";
            Session.Broadcast(new PlayerAttack { Anim = atk.attackAnim.name, Rotation = rot, Right = body.isRight, Sound = sound });
        }

        internal void AnnounceTalk(string text)
        {
            if (!Coop.WorldLive || string.IsNullOrEmpty(text)) return;
            if (text.Length > 300) text = text.Substring(0, 300);
            Session.Broadcast(new PlayerTalk { Text = text });
        }

        private static bool NamesShown =>
            Coop.WorldLive && CoopPlugin.Ready && CoopPlugin.Instance.ShowNames.Value && HostRules.Current.NameTags && !ConditionFx.Blind;

        internal override void LateTick()
        {
            // Names and words in the game's own speech text, when the game's is at hand.
            if (UI.GameText.Template == null) return;
            bool names = NamesShown;
            foreach (var r in _remotes.Values) r.UpdateTexts(names);
        }

        internal override void Gui()
        {
            if (UI.GameText.Template != null) return;  // drawn in the world instead
            if (!NamesShown) return;
            var cam = Camera.main;
            if (cam == null) return;
            foreach (var r in _remotes.Values) r.DrawLabel(cam);
        }
    }

    // ====================================================================== patches

    /// <summary>Swings, including clean misses, for the attacker's animation on other screens.</summary>
    [HarmonyPatch(typeof(Body), nameof(Body.Attack))]
    internal static class AttackPatch
    {
        private static void Prefix(Body __instance, out bool __state)
        {
            __state = Coop.WorldLive && Coop.IsLocalBody(__instance) && __instance.conscious && __instance.attackCooldown <= 0f;
        }

        private static void Postfix(Body __instance, AttackInfo atk, bool __state)
        {
            if (!__state) return;
            try
            {
                Coop.Get<PlayerSync>()?.AnnounceAttack(__instance, atk);
                Coop.Get<FriendlyFireSync>()?.Swung(__instance, atk);
            }
            catch (Exception e) { Log.Debug("attack hook: " + e.Message); }
        }
    }

    /// <summary>The local character's speech bubble, for everyone else.</summary>
    [HarmonyPatch(typeof(Talker), nameof(Talker.Talk), new[] { typeof(List<string>), typeof(Limb), typeof(bool), typeof(bool) })]
    internal static class TalkPatch
    {
        internal static readonly AccessTools.FieldRef<Talker, string> Current = AccessTools.FieldRefAccess<Talker, string>("currentString");

        private static void Prefix(Talker __instance, out string __state)
        {
            __state = Coop.WorldLive ? Current(__instance) : null;
        }

        private static void Postfix(Talker __instance, string __state)
        {
            if (!Coop.WorldLive || __instance == null || !Coop.IsLocalBody(__instance.body)) return;
            string now = Current(__instance);
            if (!string.IsNullOrEmpty(now) && !ReferenceEquals(now, __state))
                Coop.Get<PlayerSync>()?.AnnounceTalk(now);
        }
    }
}
