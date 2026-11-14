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
    /// Death, spectating and coming back.
    ///
    /// The game's death is just <c>brainHealth</c> reaching 0, and its death screen
    /// starts once the screen has faded to black. In a session we hold the screen open
    /// instead: the corpse stays where it fell, the camera follows a living teammate,
    /// and the player comes back under the host's <see cref="RespawnRule"/>. Reviving
    /// is <c>Body.Heal()</c> on the player's own machine; the game then clears the death
    /// state by itself. When everyone on the layer is dead the host calls a wipe and
    /// every machine shows the normal death screen.
    ///
    /// The host also remembers where leavers were, so leaving and rejoining the same
    /// layer neither moves nor revives anyone.
    /// </summary>
    internal sealed class LifeSync : CoopModule
    {
        internal const float QuickDelay = 15f;
        private const float WipeGrace = 2f;

        /// <summary>The host's configured rule.</summary>
        internal static RespawnRule HostRule
        {
            get
            {
                var v = CoopPlugin.Instance != null ? CoopPlugin.Instance.Respawn.Value : "NextLayer";
                if (!string.IsNullOrEmpty(Dev.DevHarness.RespawnOverride)) v = Dev.DevHarness.RespawnOverride;
                if (string.Equals(v, "Permadeath", StringComparison.OrdinalIgnoreCase)) return RespawnRule.Permadeath;
                if (string.Equals(v, "Quick", StringComparison.OrdinalIgnoreCase)) return RespawnRule.Quick;
                return RespawnRule.NextLayer;
            }
        }

        /// <summary>The rule in force for this session (the host's).</summary>
        internal static RespawnRule Rule => HostRules.Current.Respawn;

        private static bool _wiped;

        /// <summary>The death screen waits while teammates may still carry the run.</summary>
        internal static bool HoldDeathScreen => Coop.InSession && GameInfo.InRunScene && !_wiped;

        internal static bool Spectating
        {
            get
            {
                if (!HoldDeathScreen) return false;
                var body = GameInfo.LocalBody;
                return body != null && !body.alive;
            }
        }

        private int _target = -1;
        private float _diedAt = -1f;
        private bool _wasAlive = true;
        private float _allDeadSince = -1f;
        private readonly Dictionary<byte, bool> _aliveSeen = new Dictionary<byte, bool>();

        private struct Place
        {
            public ushort Epoch;
            public Vector2 Position;
            public bool Dead;
        }

        private readonly Dictionary<string, Place> _places = new Dictionary<string, Place>();

        internal override void Attach(NetSession s)
        {
            s.On<TeamWipe>(OnWipe);
            s.On<RejoinPlace>(OnRejoin);
        }

        internal override void Detach()
        {
            _wiped = false;
            _target = -1;
            _diedAt = -1f;
            _wasAlive = true;
            _allDeadSince = -1f;
            _aliveSeen.Clear();
            _places.Clear();
        }

        // ------------------------------------------------------------------ layer changes

        internal override void OnWorldLive()
        {
            _allDeadSince = -1f;
            var body = GameInfo.LocalBody;
            if (body == null || body.alive || _wiped) return;
            if (Rule == RespawnRule.Permadeath)
            {
                CoopRuntime.Notice("Permadeath: you're spectating for the rest of the run");
                return;
            }
            Revive(body, null, "You're back for the new layer");
        }

        private void Revive(Body body, Vector2? at, string notice)
        {
            try
            {
                body.Heal();
                if (at.HasValue) BodyFreeze.PlaceAt(at.Value);
                _diedAt = -1f;
                _wasAlive = true;
                _target = -1;
                Log.Info("[life] revived" + (at.HasValue ? " at " + at.Value.ToString("F1") : ""));
                CoopRuntime.Notice(notice);
                ConditionFx.Flash(new Color(1f, 1f, 1f, 0.7f), 1.2f);
            }
            catch (Exception e)
            {
                Log.Error("[life] could not revive: " + e);
            }
        }

        // ------------------------------------------------------------------ every frame

        internal override void Tick()
        {
            if (!GameInfo.InRunScene)
            {
                _wiped = false;
                _wasAlive = true;
                _diedAt = -1f;
                return;
            }

            var body = GameInfo.LocalBody;
            bool alive = body == null || body.alive;
            if (_wasAlive && !alive && Coop.WorldLive) OnLocalDeath();
            if (!_wasAlive && alive && Coop.WorldLive && _diedAt >= 0f)
            {
                // Brought back mid-layer (a teammate's defibrillator): gasp.
                _diedAt = -1f;
                ConditionFx.Flash(new Color(1f, 1f, 1f, 0.8f), 1.5f);
                try { body.talker?.Talk("*gasps*", null, true, true); } catch { /* speechless */ }
                CoopRuntime.Notice("You're back!");
            }
            _wasAlive = alive;

            if (Coop.WorldLive) WatchTeammates();

            if (Spectating)
            {
                SpectateInput();
                if (Rule == RespawnRule.Quick && _diedAt >= 0f && Coop.WorldLive && Time.unscaledTime - _diedAt >= QuickDelay)
                {
                    var p = TargetPosition();
                    Revive(body, p.HasValue ? p.Value + Vector2.up : (Vector2?)null, "Back on your feet");
                }
            }

            if (Coop.IsHost) HostWatchWipe(body);
        }

        private void OnLocalDeath()
        {
            _diedAt = Time.unscaledTime;
            _target = -1;
            PickTarget(0);
            Log.Info("[life] local player died; spectating " + (_target >= 0 ? Coop.Player((byte)_target).Name : "nobody"));
            if (!_wiped) CoopRuntime.Notice("You died");
        }

        /// <summary>"X died" / "X is back" for teammates on this layer.</summary>
        private void WatchTeammates()
        {
            foreach (var rec in Coop.Players.Values)
            {
                if (rec.Id == Coop.LocalId || !rec.HasPosition) continue;
                if (_aliveSeen.TryGetValue(rec.Id, out var was) && was != rec.Alive && !_wiped)
                {
                    CoopRuntime.Notice(rec.Name + (rec.Alive ? " is back" : " died"));
                    var me = GameInfo.LocalBody;
                    if (!rec.Alive && me != null && me.alive && Vector2.Distance(me.transform.position, rec.Position) < 25f)
                    {
                        ConditionFx.Flash(new Color(0.6f, 0f, 0f, 0.45f), 0.9f);
                        ConditionFx.Shake(25f);
                    }
                }
                _aliveSeen[rec.Id] = rec.Alive;
            }
        }

        // ------------------------------------------------------------------ spectating

        private List<RemotePlayer> Candidates()
        {
            var list = new List<RemotePlayer>();
            var ps = Coop.Get<PlayerSync>();
            if (ps == null) return list;
            foreach (var r in ps.Remotes)
                if (r.ShownAt.HasValue && Coop.Player(r.Id).Alive) list.Add(r);
            list.Sort((a, b) => a.Id.CompareTo(b.Id));
            return list;
        }

        private void PickTarget(int step)
        {
            var list = Candidates();
            if (list.Count == 0) { _target = -1; return; }
            int at = list.FindIndex(r => r.Id == _target);
            if (at < 0) at = 0;
            else at = ((at + step) % list.Count + list.Count) % list.Count;
            _target = list[at].Id;
        }

        private void SpectateInput()
        {
            int step = 0;
            if (Input.GetKeyDown(KeyCode.D) || Input.GetKeyDown(KeyCode.RightArrow)) step = 1;
            if (Input.GetKeyDown(KeyCode.A) || Input.GetKeyDown(KeyCode.LeftArrow)) step = -1;
            bool valid = Candidates().Exists(r => r.Id == _target);
            if (step != 0 || !valid) PickTarget(valid ? step : 0);
        }

        /// <summary>Where the spectator camera looks, or null to stay on the corpse.</summary>
        internal Vector2? TargetPosition()
        {
            if (_target < 0) return null;
            foreach (var r in Candidates()) if (r.Id == _target) return r.ShownAt;
            return null;
        }

        internal string TargetName => _target >= 0 ? Coop.Player((byte)_target).Name : null;

        // ------------------------------------------------------------------ wipes

        private void HostWatchWipe(Body hostBody)
        {
            if (!Coop.WorldLive || _wiped) return;
            int present = 0;
            bool anyAlive = false;
            if (hostBody != null)
            {
                present++;
                anyAlive |= hostBody.alive;
            }
            foreach (var rec in Coop.Players.Values)
            {
                if (rec.Id == NetSession.HostId || !rec.InWorld || !rec.HasPosition) continue;
                present++;
                anyAlive |= rec.Alive;
            }
            if (present == 0 || anyAlive)
            {
                _allDeadSince = -1f;
                return;
            }
            if (_allDeadSince < 0f) { _allDeadSince = Time.unscaledTime; return; }
            if (Time.unscaledTime - _allDeadSince < WipeGrace) return;

            _wiped = true;
            Session.Broadcast(new TeamWipe { Epoch = Coop.Epoch });
            Log.Info("[life] everyone is dead; the run is over");
            CoopRuntime.Notice("Everyone is dead. The run is over");
        }

        private void OnWipe(byte origin, TeamWipe m)
        {
            if (Coop.IsHost || _wiped) return;
            _wiped = true;
            Log.Info("[life] team wipe on epoch " + m.Epoch);
            CoopRuntime.Notice("Everyone is dead. The run is over");
        }

        internal static bool Wiped => _wiped;

        // ------------------------------------------------------------------ leaving and rejoining

        private static string KeyOf(PeerInfo peer, PlayerRecord rec) =>
            rec.SteamId != 0 ? "steam:" + rec.SteamId : "addr:" + BanList.KeyFor(peer.Address) + "|" + peer.Name;

        internal override void OnPeerLeft(PeerInfo peer)
        {
            _aliveSeen.Remove(peer.Id);
            if (!Coop.IsHost || !Coop.Players.TryGetValue(peer.Id, out var rec)) return;
            if (!rec.InWorld || !rec.HasPosition) return;
            _places[KeyOf(peer, rec)] = new Place { Epoch = Coop.Epoch, Position = rec.Position, Dead = !rec.Alive };
            Log.Info("[life] remembering " + peer.Name + " at " + rec.Position.ToString("F1") + (rec.Alive ? "" : " (dead)"));
        }

        /// <summary>Host: a peer finished loading the current layer. True if it was sent back to its old place.</summary>
        internal bool HostPeerEnteredLayer(byte id)
        {
            if (!Coop.IsHost || _places.Count == 0 || !Session.TryGetPeer(id, out var peer)) return false;
            string key = KeyOf(peer, Coop.Player(id));
            if (!_places.TryGetValue(key, out var place)) return false;
            _places.Remove(key);
            bool sameLayer = place.Epoch == Coop.Epoch;
            bool dead = place.Dead && (sameLayer || Rule == RespawnRule.Permadeath);
            if (!sameLayer && !dead) return false;
            Session.SendTo(id, new RejoinPlace { HasPosition = sameLayer, X = place.Position.x, Y = place.Position.y, Dead = dead });
            Log.Info("[life] " + peer.Name + " rejoined; restoring " + (sameLayer ? "their place" : "") + (dead ? " (dead)" : ""));
            return sameLayer;
        }

        private void OnRejoin(byte origin, RejoinPlace m)
        {
            var body = GameInfo.LocalBody;
            if (body == null || Coop.IsHost) return;
            if (m.HasPosition) BodyFreeze.PlaceAt(new Vector2(m.X, m.Y));
            if (m.Dead && body.alive)
            {
                body.brainHealth = 0f;
                CoopRuntime.Notice("You were dead when you left, so you're spectating");
            }
            else if (m.HasPosition) CoopRuntime.Notice("Back where you left off");
            Log.Info("[life] host restored our place" + (m.Dead ? " (dead)" : ""));
        }

        // ------------------------------------------------------------------ overlay

        internal override void Gui()
        {
            if (!Spectating || !Coop.WorldLive) return;
            string who = TargetName;
            string top = who != null
                ? "You died · watching " + who + (Candidates().Count > 1 ? "   (A / D to switch)" : "")
                : "You died";
            string bottom;
            switch (Rule)
            {
                case RespawnRule.Permadeath:
                    bottom = "Permadeath: you're out for the rest of this run.";
                    break;
                case RespawnRule.Quick:
                    float left = _diedAt >= 0f ? Mathf.Max(0f, QuickDelay - (Time.unscaledTime - _diedAt)) : QuickDelay;
                    bottom = "Back beside a teammate in " + Mathf.CeilToInt(left) + " s.";
                    break;
                default:
                    bottom = "You'll be back when the team goes down to the next layer.";
                    break;
            }
            Overlay.Bottom(top, bottom);
        }
    }

    // ====================================================================== patches

    /// <summary>Held while the session lives on: the spectator keeps watching instead.</summary>
    [HarmonyPatch(typeof(PlayerCamera), "HandleDeathScreen")]
    internal static class HoldDeathScreenPatch
    {
        private static bool Prefix(PlayerCamera __instance) =>
            __instance.didDeathScreen || !LifeSync.HoldDeathScreen;
    }

    /// <summary>A dead spectator's screen stays clear instead of fading to black.</summary>
    [HarmonyPatch(typeof(PlayerCamera), "HandleUnconsciousScreen")]
    internal static class SpectatorScreenPatch
    {
        private static readonly Color Vignette = new Color(1f, 1f, 1f, 0.55f);

        private static void Postfix(PlayerCamera __instance)
        {
            if (!LifeSync.HoldDeathScreen) return;
            var body = __instance.body;
            if (body == null || body.alive) return;
            __instance.blackAmount = 0f;
            __instance.timeHasBeenBlack = 0f;
            if (__instance.consciousnessFade != null) __instance.consciousnessFade.color = Color.clear;
            if (__instance.sleepFill != null) __instance.sleepFill.color = Color.clear;
            if (__instance.consciousnessText != null) __instance.consciousnessText.color = Color.clear;
            if (__instance.consciousnessVignette != null)
                __instance.consciousnessVignette.color = new Color(Vignette.r, Vignette.g, Vignette.b, Vignette.a + 0.08f * Mathf.Sin(Time.unscaledTime * 1.4f));
            if (__instance.consciousnessECG != null)
            {
                __instance.consciousnessECG.active = false;
                var raw = __instance.consciousnessECG.GetComponent<UnityEngine.UI.RawImage>();
                if (raw != null) raw.color = Color.clear;
            }
        }
    }

    /// <summary>
    /// The screen effects are driven by the local body's vitals, so a corpse sees a
    /// smeared, dark, distorted world. A spectator gets a healthy view, slightly
    /// desaturated, with the fog revealed around the teammate being watched.
    /// </summary>
    [HarmonyPatch(typeof(PlayerCamera), "HandleScreenShaders")]
    internal static class SpectatorShadersPatch
    {
        private static void Postfix(PlayerCamera __instance)
        {
            if (!LifeSync.Spectating || __instance.body == null) return;
            Vector2 pos = Coop.Get<LifeSync>()?.TargetPosition() ?? (Vector2)__instance.body.transform.position;
            var main = __instance.mainScreenPass;
            if (main != null)
            {
                main.SetFloat("_Pain", 0f);
                main.SetFloat("_LowOxyAmt", 0f);
                main.SetFloat("_BloodAmount", 1f);
                main.SetFloat("_Stamina", 1f);
                main.SetFloat("_BadFocusAmount", 0f);
                main.SetFloat("_InsanityFX", 0f);
                main.SetFloat("_FrostAmount", 0f);
                main.SetFloat("_OverheatAmount", 0f);
                main.SetFloat("_SaturationMult", 0.4f);
                main.SetFloat("_InsanityFX", 0.12f);
                main.SetVector("_PlayerPos", pos);
            }
            var blur = __instance.blurPass;
            if (blur != null)
            {
                blur.SetFloat("_BlurIntensity", 0f);
                blur.SetFloat("_HungerIntensity", 0f);
                blur.SetFloat("_DropletAmount", 0f);
                blur.SetFloat("_SicknessAmount", 0f);
                blur.SetFloat("_Insanity", 0f);
            }
            var bonus = __instance.bonusPass;
            if (bonus != null)
            {
                bonus.SetFloat("_Intensity", 0f);
                bonus.SetFloat("_Distort", 0f);
                bonus.SetFloat("_FlipScreen", 0f);
            }
            var damage = __instance.damagePass;
            if (damage != null)
            {
                damage.SetFloat("_EyeDamage", 0f);
                damage.SetFloat("_BrainDamage", 0f);
                damage.SetFloat("_Bloodloss", 0f);
                damage.SetFloat("_RadAmount", 0f);
                damage.SetVector("_Pos", pos);
            }
            var fog = WorldGeneration.world != null ? WorldGeneration.world.fogMat : null;
            if (fog != null) fog.SetVector("_PlayerPos", pos);
        }
    }

    /// <summary>A dead brain hears nothing (a 50 Hz low-pass); a spectator should hear the fight.</summary>
    [HarmonyPatch(typeof(PlayerCamera), "HandleAmbientSound")]
    internal static class SpectatorSoundPatch
    {
        private static void Postfix()
        {
            if (!LifeSync.Spectating) return;
            var mixer = WorldGeneration.world != null && WorldGeneration.world.soundMixerGroup != null
                ? WorldGeneration.world.soundMixerGroup.audioMixer : null;
            if (mixer != null) mixer.SetFloat("SoundCutoff", 22000f);
        }
    }

    /// <summary>The spectator camera follows the chosen teammate.</summary>
    [HarmonyPatch(typeof(PlayerCamera), "HandleCameraPosition")]
    internal static class SpectatorCameraPatch
    {
        private static void Prefix(PlayerCamera __instance)
        {
            if (!LifeSync.Spectating) return;
            var p = Coop.Get<LifeSync>()?.TargetPosition();
            if (p.HasValue) __instance.customLookPos = p.Value;
        }
    }
}
