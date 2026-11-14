using CCoop.Net.Session;
using CCoop.Plugin.Protocol;
using CCoop.Plugin.Runtime;
using UnityEngine;
using CCoop.Plugin.UI;

namespace CCoop.Plugin.Sync
{
    /// <summary>The host's rules for the session, as every machine sees them.</summary>
    internal sealed class HostRules
    {
        public RespawnRule Respawn = RespawnRule.NextLayer;

        /// <summary>Percent of living players who must be at an exit; 0 = the first one takes everyone.</summary>
        public byte ExitShare = 50;

        public bool SpeedUp = true;
        public byte MaxPlayers = 4;
        public bool Locked;
        public bool Arrows = true;
        public bool NameTags = true;
        public bool SpawnApart;
        public byte VoicePercent = 100;

        /// <summary>Players' guns hurt teammates (in the game itself a player's shot never hits a body).</summary>
        public bool FriendlyFire;

        /// <summary>0: one set of loot for the team; 1: more loot for more players.</summary>
        public byte LootScale;

        /// <summary>0: enemies as the run settings say; 1: tougher with more players; 2: much tougher.</summary>
        public byte DangerScale;

        internal static readonly string[] LootNames = { Lang.N("Shared"), Lang.N("More for more players") };
        internal static readonly string[] DangerNames = { Lang.N("As set"), Lang.N("Tougher"), Lang.N("Much tougher") };

        /// <summary>Layer loot density multiplier for this many players.</summary>
        internal float LootFactor(int players) => LootScale == 0 ? 1f : 1f + 0.35f * Mathf.Max(0, players - 1);

        /// <summary>Enemy strength multiplier for this many players.</summary>
        internal float DangerFactor(int players) =>
            DangerScale == 0 ? 1f : 1f + (DangerScale == 1 ? 0.1f : 0.25f) * Mathf.Max(0, players - 1);

        internal static HostRules Current = new HostRules();

        // ---- difficulty presets: they set the co-op rules only (the game's own run
        // settings stay the game's difficulty)

        internal static readonly string[] PresetNames = { Lang.N("Friendly"), Lang.N("Standard"), Lang.N("Hard"), Lang.N("Brutal") };

        private static readonly HostRules[] Presets =
        {
            new HostRules { Respawn = RespawnRule.Quick, ExitShare = 0, Arrows = true, NameTags = true, SpawnApart = false, VoicePercent = 100, LootScale = 1 },
            new HostRules { Respawn = RespawnRule.NextLayer, ExitShare = 50, Arrows = true, NameTags = true, SpawnApart = false, VoicePercent = 100 },
            new HostRules { Respawn = RespawnRule.NextLayer, ExitShare = 100, Arrows = false, NameTags = true, SpawnApart = true, VoicePercent = 100, FriendlyFire = true, DangerScale = 1 },
            new HostRules { Respawn = RespawnRule.Permadeath, ExitShare = 100, Arrows = false, NameTags = false, SpawnApart = true, VoicePercent = 50, FriendlyFire = true, DangerScale = 2 },
        };

        /// <summary>Which preset these rules match, or -1 for custom.</summary>
        internal int Preset
        {
            get
            {
                for (int i = 0; i < Presets.Length; i++)
                {
                    var p = Presets[i];
                    if (p.Respawn == Respawn && p.ExitShare == ExitShare && p.Arrows == Arrows && p.NameTags == NameTags &&
                        p.SpawnApart == SpawnApart && p.VoicePercent == VoicePercent && p.FriendlyFire == FriendlyFire &&
                        p.LootScale == LootScale && p.DangerScale == DangerScale) return i;
                }
                return -1;
            }
        }

        internal string PresetName => Preset >= 0 ? PresetNames[Preset] : Lang.N("Custom");

        /// <summary>Host: writes a preset's rules into the config (the window and RulesSync pick them up).</summary>
        internal static void ApplyPreset(int index)
        {
            if (index < 0 || index >= Presets.Length) return;
            var p = Presets[index];
            var c = CoopPlugin.Instance;
            c.Respawn.Value = p.Respawn == RespawnRule.Permadeath ? "Permadeath" : p.Respawn == RespawnRule.Quick ? "Quick" : "NextLayer";
            c.ExitShare.Value = p.ExitShare;
            c.HostArrows.Value = p.Arrows;
            c.HostNameTags.Value = p.NameTags;
            c.SpawnApart.Value = p.SpawnApart;
            c.VoiceRangePercent.Value = p.VoicePercent;
            c.FriendlyFire.Value = p.FriendlyFire;
            c.LootScale.Value = p.LootScale;
            c.DangerScale.Value = p.DangerScale;
        }

        internal static HostRules FromConfig()
        {
            var c = CoopPlugin.Instance;
            return new HostRules
            {
                Respawn = LifeSync.HostRule,
                ExitShare = (byte)Mathf.Clamp(Dev.DevHarness.ExitShareOverride >= 0 ? Dev.DevHarness.ExitShareOverride : c.ExitShare.Value, 0, 100),
                SpeedUp = c.AllowSpeedUp.Value,
                MaxPlayers = (byte)Mathf.Clamp(c.MaxPlayers.Value, 2, NetSession.MaxPeers),
                Locked = !string.IsNullOrEmpty(c.HostPassword.Value),
                Arrows = c.HostArrows.Value,
                NameTags = c.HostNameTags.Value,
                SpawnApart = c.SpawnApart.Value,
                VoicePercent = (byte)Mathf.Clamp(c.VoiceRangePercent.Value, 25, 100),
                FriendlyFire = Dev.DevHarness.FfOverride >= 0 ? Dev.DevHarness.FfOverride == 1 : c.FriendlyFire.Value,
                LootScale = (byte)Mathf.Clamp(Dev.DevHarness.LootOverride >= 0 ? Dev.DevHarness.LootOverride : c.LootScale.Value, 0, 1),
                DangerScale = (byte)Mathf.Clamp(Dev.DevHarness.DangerOverride >= 0 ? Dev.DevHarness.DangerOverride : c.DangerScale.Value, 0, 2),
            };
        }

        internal bool SameAs(HostRules o) =>
            o != null && o.Respawn == Respawn && o.ExitShare == ExitShare && o.SpeedUp == SpeedUp &&
            o.MaxPlayers == MaxPlayers && o.Locked == Locked && o.Arrows == Arrows && o.NameTags == NameTags &&
            o.SpawnApart == SpawnApart && o.VoicePercent == VoicePercent && o.FriendlyFire == FriendlyFire &&
            o.LootScale == LootScale && o.DangerScale == DangerScale;

        internal string RespawnText =>
            Respawn == RespawnRule.Permadeath ? "permadeath"
            : Respawn == RespawnRule.Quick ? "back after " + (int)LifeSync.QuickDelay + " s"
            : "back on the next layer";

        internal string ExitText =>
            ExitShare == 0 ? "the first at an exit takes everyone"
            : ExitShare >= 100 ? "everyone must be at the exit"
            : ExitShare == 50 ? "half must be at the exit"
            : ExitShare + "% must be at the exit";

        /// <summary>One line for the co-op window.</summary>
        internal string Describe() =>
            PresetName + ": dead players " + RespawnText + " · " + ExitText + (SpeedUp ? "" : " · no speed-up") +
            (Arrows ? "" : " · no arrows") + (NameTags ? "" : " · no name tags") + (SpawnApart ? " · spawn apart" : "") +
            (VoicePercent < 100 ? " · voices carry " + VoicePercent + "%" : "") + (FriendlyFire ? " · friendly fire" : "") +
            (LootScale > 0 ? " · more loot for more players" : "") + (DangerScale > 0 ? " · enemies " + DangerNames[DangerScale].ToLowerInvariant() + " with more players" : "");

        /// <summary>The same line in the player's language, for the co-op window and notices.</summary>
        internal string Shown()
        {
            var parts = new System.Collections.Generic.List<string>();
            string respawn = Respawn == RespawnRule.Permadeath ? Lang.T("permadeath")
                : Respawn == RespawnRule.Quick ? Lang.F("back after {0} s", (int)LifeSync.QuickDelay)
                : Lang.T("back on the next layer");
            parts.Add(Lang.F("{0}: dead players {1}", Lang.T(PresetName), respawn));
            parts.Add(ExitShare == 0 ? Lang.T("the first at an exit takes everyone")
                : ExitShare >= 100 ? Lang.T("everyone must be at the exit")
                : ExitShare == 50 ? Lang.T("half must be at the exit")
                : Lang.F("{0}% must be at the exit", ExitShare));
            if (!SpeedUp) parts.Add(Lang.T("no speed-up"));
            if (!Arrows) parts.Add(Lang.T("no arrows"));
            if (!NameTags) parts.Add(Lang.T("no name tags"));
            if (SpawnApart) parts.Add(Lang.T("spawn apart"));
            if (VoicePercent < 100) parts.Add(Lang.F("voices carry {0}%", VoicePercent));
            if (FriendlyFire) parts.Add(Lang.T("friendly fire"));
            if (LootScale > 0) parts.Add(Lang.T("more loot for more players"));
            if (DangerScale == 1) parts.Add(Lang.T("enemies tougher with more players"));
            if (DangerScale >= 2) parts.Add(Lang.T("enemies much tougher with more players"));
            return string.Join(" · ", parts.ToArray());
        }

        /// <summary>Compact form for the lobby advert: "r0e50s1".</summary>
        internal string Encode() => "r" + (int)Respawn + "e" + ExitShare + "s" + (SpeedUp ? 1 : 0) + "a" + (Arrows ? 1 : 0) +
                                       "n" + (NameTags ? 1 : 0) + "p" + (SpawnApart ? 1 : 0) + "v" + VoicePercent + "f" + (FriendlyFire ? 1 : 0) + "l" + LootScale + "d" + DangerScale;

        internal static HostRules Decode(string s)
        {
            var r = new HostRules();
            if (string.IsNullOrEmpty(s)) return null;
            try
            {
                int i = 0;
                while (i < s.Length)
                {
                    char k = s[i++];
                    int start = i;
                    while (i < s.Length && char.IsDigit(s[i])) i++;
                    if (!int.TryParse(s.Substring(start, i - start), out var v)) return null;
                    if (k == 'r') r.Respawn = (RespawnRule)Mathf.Clamp(v, 0, 2);
                    else if (k == 'e') r.ExitShare = (byte)Mathf.Clamp(v, 0, 100);
                    else if (k == 's') r.SpeedUp = v != 0;
                    else if (k == 'a') r.Arrows = v != 0;
                    else if (k == 'n') r.NameTags = v != 0;
                    else if (k == 'p') r.SpawnApart = v != 0;
                    else if (k == 'v') r.VoicePercent = (byte)Mathf.Clamp(v, 25, 100);
                    else if (k == 'f') r.FriendlyFire = v != 0;
                    else if (k == 'l') r.LootScale = (byte)Mathf.Clamp(v, 0, 1);
                    else if (k == 'd') r.DangerScale = (byte)Mathf.Clamp(v, 0, 2);
                }
                return r;
            }
            catch { return null; }
        }

        internal RulesMessage ToMessage() => new RulesMessage
        {
            Respawn = (byte)Respawn,
            ExitShare = ExitShare,
            SpeedUp = SpeedUp,
            MaxPlayers = MaxPlayers,
            Locked = Locked,
            Arrows = Arrows,
            NameTags = NameTags,
            SpawnApart = SpawnApart,
            VoicePercent = VoicePercent,
            FriendlyFire = FriendlyFire,
            LootScale = LootScale,
            DangerScale = DangerScale,
        };

        internal static HostRules FromMessage(RulesMessage m) => new HostRules
        {
            Respawn = (RespawnRule)Mathf.Clamp(m.Respawn, 0, 2),
            ExitShare = (byte)Mathf.Clamp(m.ExitShare, 0, 100),
            SpeedUp = m.SpeedUp,
            MaxPlayers = m.MaxPlayers,
            Locked = m.Locked,
            Arrows = m.Arrows,
            NameTags = m.NameTags,
            SpawnApart = m.SpawnApart,
            VoicePercent = (byte)Mathf.Clamp(m.VoicePercent, 25, 100),
            FriendlyFire = m.FriendlyFire,
            LootScale = (byte)Mathf.Clamp(m.LootScale, 0, 1),
            DangerScale = (byte)Mathf.Clamp(m.DangerScale, 0, 2),
        };
    }

    /// <summary>
    /// Keeps <see cref="HostRules.Current"/> the same everywhere. The host reads its
    /// config every frame (rules can change mid-session from the co-op window), applies
    /// password and player cap to the live session, and tells everyone when anything
    /// changes.
    /// </summary>
    internal sealed class RulesSync : CoopModule
    {
        internal override void Attach(NetSession s)
        {
            s.On<RulesMessage>((o, m) =>
            {
                if (Coop.IsHost) return;
                var old = HostRules.Current;
                HostRules.Current = HostRules.FromMessage(m);
                if (old != null && !old.SameAs(HostRules.Current) && Coop.InSession)
                    CoopRuntime.Notice(Lang.F("Host rules: {0}", HostRules.Current.Shown()));
            });
            if (s.IsHost) HostRules.Current = HostRules.FromConfig();
        }

        internal override void Detach() => HostRules.Current = new HostRules();

        internal override void OnPeerJoined(PeerInfo peer)
        {
            if (Coop.IsHost) Session.SendTo(peer.Id, HostRules.Current.ToMessage());
        }

        internal override void Tick()
        {
            if (!Coop.IsHost) return;
            var cfg = CoopPlugin.Instance;
            var live = Coop.Runtime?.HostConfig;
            if (live != null)
            {
                live.Password = cfg.HostPassword.Value ?? "";
                live.MaxPlayers = Mathf.Clamp(cfg.MaxPlayers.Value, 2, NetSession.MaxPeers);
            }
            var now = HostRules.FromConfig();
            if (now.SameAs(HostRules.Current)) return;
            HostRules.Current = now;
            Session.Broadcast(now.ToMessage());
            Log.Info("[rules] " + now.Describe() + ", max " + now.MaxPlayers + (now.Locked ? ", password" : ""));
        }
    }
}
