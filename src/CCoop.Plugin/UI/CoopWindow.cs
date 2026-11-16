using System.Collections.Generic;
using CCoop.Net.Session;
using CCoop.Plugin.Net;
using CCoop.Plugin.Runtime;
using CCoop.Plugin.Sync;
using UnityEngine;

namespace CCoop.Plugin.UI
{
    /// <summary>
    /// The co-op menu (F1, or the button on the main menu): host with Steam, find games
    /// (friends' and public lobbies), LAN/IP, and the player list once in a session.
    /// Scaled up on Steam Deck and large screens; text fields bring up Steam's
    /// on-screen keyboard where there is no physical one.
    /// </summary>
    internal static partial class CoopWindow
    {

        private static bool _open;
        private static string _address;
        private static string _lanPassword = "";
        private static string _promptPassword = "";
        private static GUIStyle _small, _right, _bad;

        private enum Tab { Host, Find, Lan }
        private static Tab _tab = Tab.Host;
        private static bool _friendsTab = true;
        private static bool _browsedOnce;

        private static string _lastFocus = "";
        private static readonly Dictionary<string, Rect> FieldRects = new Dictionary<string, Rect>();


        /// <summary>One of the window's text fields has the keyboard (so V, Enter etc. are text, not commands).</summary>
        internal static bool TypingInField => _open && _lastFocus.StartsWith("ccoop_");
        internal static void Show() => _open = true;

        internal static bool IsOpen => _open;

        /// <summary>Dev screenshots: a scale that does not touch the player's config.</summary>
        internal static float DevScale;

        /// <summary>Dev screenshots: 0 Host, 1 Find games, 2 LAN / IP.</summary>
        internal static void DevSelectTab(int tab)
        {
            _open = true;
            _tab = (Tab)Mathf.Clamp(tab, 0, 2);
        }

        internal static void HandleHotkeys(CoopRuntime rt)
        {
            if (!CoopPlugin.Ready) return;
            if (Input.GetKeyDown(CoopPlugin.Instance.MenuKey.Value) || (_open && !TypingInField && Input.GetKeyDown(KeyCode.Escape)))
            {
                _open = !_open;
                if (!_open) { GUIUtility.keyboardControl = 0; _lastFocus = ""; }
                Log.Info("[ui] co-op menu " + (_open ? "opened" : "closed"));
            }
        }

        /// <summary>The configured scale, or one picked from the screen.</summary>
        internal static float Scale
        {
            get
            {
                if (DevScale > 0f) return DevScale;
                float cfg = CoopPlugin.Ready ? CoopPlugin.Instance.UiScale.Value : 0f;
                if (cfg > 0.1f) return Mathf.Clamp(cfg, 0.5f, 3f);
                float s = Screen.height / 1080f;
                if (SteamServices.OnDeckCached) s = Mathf.Max(s, 1.35f);
                return Mathf.Clamp(Mathf.Round(s * 4f) / 4f, 1f, 3f);
            }
        }

        internal static Color PingColor(int rtt, float quality)
        {
            int level = rtt < 0 ? 0 : rtt < 90 ? 0 : rtt < 170 ? 1 : 2;
            if (quality >= 0f && quality < 0.85f) level = 2;
            else if (quality >= 0f && quality < 0.95f) level = Mathf.Max(level, 1);
            return level == 0 ? new Color(0.6f, 1f, 0.6f) : level == 1 ? new Color(1f, 0.9f, 0.45f) : new Color(1f, 0.5f, 0.4f);
        }

        private static void Styles()
        {
            if (_small != null) return;
            _small = new GUIStyle(GUI.skin.label) { fontSize = UiSkin.Small, wordWrap = true };
            _small.normal.textColor = UiSkin.Dim;
            _right = new GUIStyle(_small) { alignment = TextAnchor.MiddleRight, wordWrap = false };
            _bad = new GUIStyle(_small);
            _bad.normal.textColor = UiSkin.Bad;
        }

        internal static void Draw(CoopRuntime rt)
        {
            Styles();
            float scale = Scale;
            var oldMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
            try
            {
                // A visible way in from the main menu, so nobody has to know about F1.
                if (!_open && GameInfo.OnMainMenu && !MenuWalkie.Shown)
                {
                    int n = Coop.InSession ? Coop.Session.PeerCount : 0;
                    string label = n > 0 ? (n == 1 ? Lang.T("Co-op · 1 player  (F1)") : Lang.F("Co-op · {0} players  (F1)", n)) : Lang.T("Co-op  (F1)");
                    var size = GUI.skin.button.CalcSize(new GUIContent(label));
                    var r = new Rect(12, 12, Mathf.Max(150f, size.x + 12f), 34);
                    UiMouse.Claim(r);
                    if (GUI.Button(r, label)) _open = true;
                }
                if (!_open) return;
                Lobby(rt);
            }
            finally
            {
                GUI.matrix = oldMatrix;
            }
        }

        // ------------------------------------------------------------------ offline (Host / Find games / LAN)

        /// <summary>Dev screenshots: 0 Host, 1 Find games, 2 LAN / IP.</summary>
        private static void Offline(CoopRuntime rt)
        {
            GUI.enabled = !rt.Connecting;
            switch (_tab)
            {
                case Tab.Host: HostTab(rt); break;
                case Tab.Find: FindTab(rt); break;
                case Tab.Lan: LanTab(rt); break;
            }
            GUI.enabled = true;
        }

        private static void HostTab(CoopRuntime rt)
        {
            // The rules apply to a LAN host too, so they're here with or without Steam.
            if (!GameInfo.SteamReady)
                GameRows.Info(Lang.T("Steam isn't running: host under LAN / IP."), "", Lang.T("Launch the game through Steam to host with invites."), UiSkin.Amber);
            RulesEditor(rt, null);
            RunPresets.Section();
            SavedRuns();
            Bans();
        }

        /// <summary>Co-op runs saved earlier: pick one, then host to carry on with it.</summary>
        private static void SavedRuns()
        {
            var runs = CoopSaves.List();
            if (runs.Count == 0) return;
            GameRows.Header(Lang.T("Continue a co-op run"));
            foreach (var r in runs)
            {
                bool picked = CoopSaves.PendingContinue == r.Id;
                string label = Lang.F("Layer {0}", r.Layer) + " · " + r.When.ToString("MMM d HH:mm");
                var at = GameRows.Slot(label, r.Players.Length > 0 ? r.Players : null, 220f);
                float bw = (at.width - 6f) / 2f;
                if (GUI.Button(new Rect(at.x, at.y, bw, at.height), picked ? Lang.T("Picked") : Lang.T("Continue"))) CoopSaves.PendingContinue = picked ? null : r.Id;
                if (GUI.Button(new Rect(at.x + bw + 6f, at.y, bw, at.height), Lang.T("Delete"))) { CoopSaves.Delete(r.Id); if (picked) CoopSaves.PendingContinue = null; }
            }
        }

        private static void Bans()
        {
            var bans = BanList.All;
            if (bans.Count == 0) return;
            GameRows.Header(Lang.F("Banned players ({0})", bans.Count));
            foreach (var b in new List<BanList.Entry>(bans))
            {
                var at = GameRows.Slot(string.IsNullOrEmpty(b.Name) ? b.Key : b.Name, Lang.F("Banned {0}", b.When), 110f);
                if (GUI.Button(at, Lang.T("Unban"))) BanList.Unban(b.Key);
            }
        }

        private static void Refresh(CoopRuntime rt)
        {
            _browsedOnce = true;
            rt.Lobby.Refresh();
        }

        private static void FindTab(CoopRuntime rt)
        {
            if (!GameInfo.SteamReady)
            {
                GameRows.Header(Lang.T("Steam isn't running"));
                GameRows.Info(Lang.T("Launch the game through Steam to find games."), "", Lang.T("Or join directly under LAN / IP."));
                return;
            }
            var lobby = rt.Lobby;
            int which = GameRows.Choice("findwhich", Lang.T("Show"), _friendsTab ? 0 : 1, new[] { Lang.T("Friends' games"), Lang.T("Public games") },
                Lang.T("Friends: sessions your Steam friends host. Public: sessions anyone can join."));
            if ((which == 0) != _friendsTab) { _friendsTab = which == 0; Refresh(rt); }

            var list = new List<LobbyInfo>(_friendsTab ? lobby.FriendLobbies : lobby.PublicLobbies);
            if (list.Count == 0)
            {
                GameRows.Info(lobby.Searching ? Lang.T("Looking for sessions...")
                    : _friendsTab ? Lang.T("None of your Steam friends is hosting right now.")
                    : Lang.T("No public sessions found."), "", _friendsTab ? null : Lang.T("Hosts choose Public to be listed here."));
                return;
            }
            foreach (var l in list) LobbyRow(rt, l);
        }

        /// <summary>One session found: the host's picture and name, where they are, the ping, Join.</summary>
        private static void LobbyRow(CoopRuntime rt, LobbyInfo l)
        {
            string why = l.Incompatible;
            bool full = l.Max > 0 && l.Players >= l.Max;
            var r = GUILayoutUtility.GetRect(10f, 72f, GUILayout.ExpandWidth(true));
            if (_lobbyName == null)
            {
                _lobbyName = new GUIStyle(GUI.skin.label) { fontSize = UiSkin.Head, wordWrap = false, clipping = TextClipping.Clip };
                _lobbySub = new GUIStyle(GUI.skin.label) { fontSize = UiSkin.Small, wordWrap = false, clipping = TextClipping.Clip };
                _lobbySub.normal.textColor = UiSkin.Dim;
            }
            if (Event.current.type == EventType.Repaint)
            {
                var tex = SteamServices.Avatar(l.HostId);
                if (tex != null) GUI.DrawTexture(new Rect(r.x + 10f, r.y + 12f, 48f, 48f), tex, ScaleMode.ScaleToFit);
                string hostLine = l.Host + (l.Locked ? "  [" + Lang.T("password") + "]" : "");
                GUI.Label(new Rect(r.x + 70f, r.y + 6f, r.width - 300f, 30f), hostLine, UiSkin.Fit(_lobbyName, hostLine));
                string where = l.InRun ? (string.IsNullOrEmpty(l.Layer) ? Lang.T("In a run") : int.TryParse(l.Layer, out var depth) ? Lang.F("Layer {0}", depth) : l.Layer) : Lang.T("In the menu");
                string sub = where + " · " + Lang.F("{0}/{1} players", l.Players, l.Max);
                if (l.ViaFriend && !string.IsNullOrEmpty(l.FriendName) && l.FriendName != l.Host) sub += " · " + Lang.F("with {0}", l.FriendName);
                var rules = HostRules.Decode(l.Rules);
                if (rules != null) sub += " · " + Lang.F("Dead: {0}", rules.RespawnText);
                var subStyle = _lobbySub;
                if (why != null) { sub = Lang.F("Can't join: {0}", why); subStyle = _bad; }
                GUI.Label(new Rect(r.x + 70f, r.y + 38f, r.width - 300f, 24f), sub, UiSkin.Fit(subStyle, sub));
                var old = GUI.color;
                GUI.color = PingColor(l.Ping, -1f);
                GUI.Label(new Rect(r.xMax - 220f, r.y, 90f, r.height), l.Ping >= 0 ? "~" + l.Ping + " ms" : "? ms", _right);
                GUI.color = old;
                GUI.DrawTexture(new Rect(r.x, r.yMax - 1f, r.width, 1f), GameLook.Solid(new Color(1f, 1f, 1f, 0.22f)));
            }
            bool was = GUI.enabled;
            GUI.enabled = was && why == null && !full;
            if (GUI.Button(new Rect(r.xMax - 120f, r.y + 18f, 110f, 36f), full ? Lang.T("Full") : Lang.T("Join")))
            {
                _promptPassword = "";
                rt.JoinSteamLobby(l.Id);
            }
            GUI.enabled = was;
        }

        private static GUIStyle _lobbyName, _lobbySub;

        private static void LanTab(CoopRuntime rt)
        {
            GameRows.Header(Lang.T("Direct connection"));
            GameRows.Info(Lang.T("Port"), CoopPlugin.Instance.Port.Value.ToString(),
                Lang.F("On a LAN or a VPN (Tailscale, ZeroTier, Radmin) nothing else is needed. Over the internet the host must forward UDP port {0}.", CoopPlugin.Instance.Port.Value));
            var addrAt = GameRows.Slot(Lang.T("Host's address"), Lang.T("The host's IP address (or name) to join."));
            _address = FieldAt(addrAt, "address", _address, 64, false);
            var pwAt = GameRows.Slot(Lang.T("Password"), Lang.T("Leave it empty if the host has none."));
            _lanPassword = FieldAt(pwAt, "lanpw", _lanPassword, 64, true);
            GameRows.Info(Lang.T("Hosting here uses the Host tab's rules."), "");
        }

        private static void PasswordPrompt(CoopRuntime rt)
        {
            var l = rt.PasswordPrompt;
            GameRows.Header(Lang.T("Password needed"));
            GameRows.Info(Lang.F("{0}'s session is locked.", l.Host), "", Lang.T("Ask them for the password."));
            bool submit = Event.current.type == EventType.KeyDown
                          && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)
                          && GUI.GetNameOfFocusedControl() == "ccoop_prompt";
            var at = GameRows.Slot(Lang.T("Password"), null);
            _promptPassword = FieldAt(at, "prompt", _promptPassword, 64, true);
            if (submit && _promptPassword.Length > 0)
            {
                Event.current.Use();
                JoinPrompted(rt);
            }
        }

        private static void JoinPrompted(CoopRuntime rt)
        {
            string pw = _promptPassword;
            _promptPassword = "";
            rt.JoinSteamLobby(rt.PasswordPrompt.Id, pw);
        }

        /// <summary>Not in a session: the current page's own buttons, in the bottom bar.</summary>
        private static void OfflineActions(CoopRuntime rt)
        {
            bool was = GUI.enabled;
            GUI.enabled = was && !rt.Connecting;
            if (rt.PasswordPrompt != null)
            {
                if (GUILayout.Button(Lang.T("Cancel"), GUILayout.MinWidth(120f), GUILayout.ExpandHeight(true))) { rt.PasswordPrompt = null; rt.LastMessage = ""; }
                if (GUILayout.Button(Lang.T("Join"), GUILayout.MinWidth(160f), GUILayout.ExpandHeight(true)) && _promptPassword.Length > 0) JoinPrompted(rt);
            }
            else if (_tab == Tab.Host && GameInfo.SteamReady)
            {
                if (GUILayout.Button(CoopSaves.PendingContinue != null ? Lang.T("Host and continue") : Lang.T("Host with Steam"), GUILayout.MinWidth(180f), GUILayout.ExpandHeight(true))) rt.HostSteam();
            }
            else if (_tab == Tab.Find && GameInfo.SteamReady)
            {
                GUI.enabled = was && !rt.Connecting && !rt.Lobby.Searching;
                if (GUILayout.Button(rt.Lobby.Searching ? Lang.T("Searching...") : Lang.T("Refresh"), GUILayout.MinWidth(140f), GUILayout.ExpandHeight(true))) Refresh(rt);
            }
            else if (_tab == Tab.Lan)
            {
                if (GUILayout.Button(Lang.F("Host on port {0}", CoopPlugin.Instance.Port.Value), GUILayout.MinWidth(180f), GUILayout.ExpandHeight(true))) rt.HostLan();
                if (GUILayout.Button(Lang.T("Join"), GUILayout.MinWidth(120f), GUILayout.ExpandHeight(true))) rt.JoinLan(_address, _lanPassword);
            }
            GUI.enabled = was;
        }

        private static readonly string[] LobbyTypes = { "FriendsOnly", "Public", "InviteOnly" };
        private static readonly string[] RespawnRules = { "NextLayer", "Permadeath", "Quick" };
        private static readonly int[] ExitShares = { 0, 50, 100 };
        private static readonly string[] VoiceModes = { "PushToTalk", "Open", "Off" };

        /// <summary>The open-mic level meter is on screen (the mic is read for it even when not sending).</summary>
        internal static bool MeasuringMic;

        /// <summary>
        /// The host's rules: before hosting (Host tab) and live while hosting. Everything
        /// here is config, read every frame by <see cref="RulesSync"/>. Laid out as the
        /// game's own Custom settings: one line each, explanations on hover. How many
        /// players fit is set on the player places themselves.
        /// </summary>
        private static void RulesEditor(CoopRuntime rt, NetSession live)
        {
            var cfg = CoopPlugin.Instance;
            bool steamLobby = live == null ? GameInfo.SteamReady : rt.TransportKind == "Steam" && rt.Lobby.InLobby;
            var rules = HostRules.FromConfig();

            GameRows.Header(Lang.T("Session"));
            int preset = rules.Preset;
            var presetNames = new List<string>(Lang.All(HostRules.PresetNames));
            if (preset < 0) presetNames.Add(Lang.T("Custom"));
            int pickPreset = GameRows.Choice("difficulty", Lang.T("Difficulty"), preset < 0 ? presetNames.Count - 1 : preset, presetNames.ToArray(),
                Lang.T("Sets all the co-op rules below at once. The game's own run settings stay as they are."));
            if (pickPreset != preset && pickPreset >= 0 && pickPreset < HostRules.PresetNames.Length) HostRules.ApplyPreset(pickPreset);

            if (steamLobby)
            {
                int cur = Mathf.Max(0, System.Array.IndexOf(LobbyTypes, cfg.LobbyType.Value));
                int pick = GameRows.Choice("jointype", Lang.T("Who can join"), cur, new[] { Lang.T("Friends"), Lang.T("Public"), Lang.T("Invite only") },
                    cur == 1 ? Lang.T("Listed for everyone under Find games. Add a password to keep strangers out.")
                    : cur == 2 ? Lang.T("Only people you invite through Steam can join.")
                    : Lang.T("Steam friends can join from Find games, your profile (\"Join Game\") or an invite."));
                if (pick != cur)
                {
                    cfg.LobbyType.Value = LobbyTypes[pick];
                    if (live != null) rt.Lobby.SetType(LobbyTypes[pick]);
                }
            }
            var pwAt = GameRows.Slot(Lang.T("Password"), Lang.T("Players need it to join. Empty: no password."));
            string pw = FieldAt(pwAt, live == null ? "hostpw" : "livepw", cfg.HostPassword.Value, 64, true);
            if (pw != cfg.HostPassword.Value) cfg.HostPassword.Value = pw;

            GameRows.Header(Lang.T("Death and descent"));
            int rule = Mathf.Max(0, System.Array.IndexOf(RespawnRules, cfg.Respawn.Value));
            int newRule = GameRows.Choice("respawn", Lang.T("When you die"), rule, new[] { Lang.T("Next layer"), Lang.T("Permadeath"), Lang.T("Quick") },
                rule == 1 ? Lang.T("The dead spectate for the rest of the run.")
                : rule == 2 ? Lang.F("The dead spectate, then come back beside a teammate after {0} s.", (int)LifeSync.QuickDelay)
                : Lang.T("The dead spectate, then come back healed when the team reaches the next layer."));
            if (newRule != rule) cfg.Respawn.Value = RespawnRules[newRule];

            int share = cfg.ExitShare.Value;
            int shareAt = share <= 0 ? 0 : share >= 100 ? 2 : share == 50 ? 1 : 1;
            int newShare = GameRows.Choice("descend", Lang.T("Descend when"), shareAt, new[] { Lang.T("First arrives"), Lang.T("Half are there"), Lang.T("All are there") },
                Lang.T("How many of the team must be at the exit before everyone goes down."));
            if (newShare != shareAt) cfg.ExitShare.Value = ExitShares[newShare];

            GameRows.Header(Lang.T("Together"));
            cfg.HostArrows.Value = GameRows.Toggle(Lang.T("Teammate arrows"), cfg.HostArrows.Value, Lang.T("Arrows at the screen edge point to teammates out of sight."));
            cfg.HostNameTags.Value = GameRows.Toggle(Lang.T("Name tags"), cfg.HostNameTags.Value, Lang.T("Names above teammates."));
            cfg.SpawnApart.Value = GameRows.Toggle(Lang.T("Spawn apart"), cfg.SpawnApart.Value, Lang.T("Each player starts a layer somewhere else instead of together."));
            cfg.FriendlyFire.Value = GameRows.Toggle(Lang.T("Friendly fire"), cfg.FriendlyFire.Value, Lang.T("Shots and blows hurt teammates."));
            cfg.Sleep.Value = GameRows.Toggle(Lang.T("Sleep"), cfg.Sleep.Value,
                Lang.T("Off: nobody gets tired and nobody can nap. Sleeping pills still work."));
            cfg.Radios.Value = GameRows.Toggle(Lang.T("Radios"), cfg.Radios.Value,
                Lang.T("Everyone has a radio. Press Tab in the chat box to talk on it: it reaches the whole layer, but it crackles more the farther away and the more rock in between. You need to be awake to use it."));
            float voice = GameRows.Slider(Lang.T("Voices carry"), cfg.VoiceRangePercent.Value, 25f, 100f, cfg.VoiceRangePercent.Value + "%",
                Lang.T("How far voices and speech are heard."));
            int pct = Mathf.RoundToInt(voice / 5f) * 5;
            if (pct != cfg.VoiceRangePercent.Value) cfg.VoiceRangePercent.Value = pct;
            cfg.AllowSpeedUp.Value = GameRows.Toggle(Lang.T("Fast-forward"), cfg.AllowSpeedUp.Value,
                Lang.T("Anyone may speed up time. Off: time runs at normal speed (sleep still fast-forwards)."));

            GameRows.Header(Lang.T("Balance"));
            int loot = GameRows.Choice("loot", Lang.T("Loot"), Mathf.Clamp(cfg.LootScale.Value, 0, 1), Lang.All(HostRules.LootNames),
                Lang.T("Shared: one layer's loot for the team. More: each extra player adds some."));
            if (loot != cfg.LootScale.Value) cfg.LootScale.Value = loot;
            int danger = GameRows.Choice("enemies", Lang.T("Enemies"), Mathf.Clamp(cfg.DangerScale.Value, 0, 2), Lang.All(HostRules.DangerNames),
                Lang.T("Enemies get stronger with each extra player."));
            if (danger != cfg.DangerScale.Value) cfg.DangerScale.Value = danger;
        }

        private static void MilestoneList()
        {
            GameRows.Header(Lang.F("Co-op milestones ({0}/{1})", Milestones.Reached, Milestones.All.Length));
            foreach (var d in Milestones.All)
            {
                float n = Milestones.Count(d.Key);
                bool done = n >= d.Goal;
                GameRows.Info(Lang.T(d.Title), done ? Lang.T("done") : Mathf.FloorToInt(n) + "/" + d.Goal + (d.Unit ?? ""), Lang.T(d.Text), done ? (Color?)UiSkin.Good : null);
            }
        }

        /// <summary>A player row's buttons: voice volume, check on them, and (host) kick and ban.</summary>
        private static void RowButtons(CoopRuntime rt, NetSession s, PeerInfo p, PlayerRecord rec, bool me, VoiceSync voice)
        {
            // How loud this teammate's voice is for you (only you): 100% / 50% / muted / 150%.
            if (!me && voice != null && VoiceSync.Available && GUILayout.Button(VoiceSync.VolumeLabel(rec), GUILayout.MinWidth(56), GUILayout.ExpandWidth(false))) VoiceSync.CycleVolume(rec);
            if (!me && Coop.WorldLive && rec.HasPosition && GUILayout.Button(Lang.T("Check"), GUILayout.MinWidth(52), GUILayout.ExpandWidth(false))) TeammatePanel.Open(p.Id);
            if (s.IsHost && !me)
            {
                if (GUILayout.Button(Lang.T("Kick"), GUILayout.MinWidth(42), GUILayout.ExpandWidth(false))) s.Kick(p.Id, Lang.N("Kicked by the host."));
                bool was = GUI.enabled;
                GUI.enabled = was && !string.IsNullOrEmpty(p.Address);
                if (GUILayout.Button(Lang.T("Ban"), GUILayout.MinWidth(40), GUILayout.ExpandWidth(false))) rt.Ban(p.Id);
                GUI.enabled = was;
            }
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>A named text field, remembered so the on-screen keyboard can be placed beside it.</summary>
        private static string Field(string id, string value, int max, bool secret)
        {
            GUI.SetNextControlName("ccoop_" + id);
            string v = secret ? GUILayout.PasswordField(value ?? "", '*', max) : GUILayout.TextField(value ?? "", max);
            if (Event.current.type == EventType.Repaint) FieldRects[id] = GUIUtility.GUIToScreenRect(GUILayoutUtility.GetLastRect());
            return v;
        }

        /// <summary>A named text field at a fixed place (inside a settings row).</summary>
        private static string FieldAt(Rect r, string id, string value, int max, bool secret)
        {
            GUI.SetNextControlName("ccoop_" + id);
            string v = secret ? GUI.PasswordField(r, value ?? "", '*', max) : GUI.TextField(r, value ?? "", max);
            if (Event.current.type == EventType.Repaint) FieldRects[id] = GUIUtility.GUIToScreenRect(r);
            return v;
        }

        /// <summary>On Deck / Big Picture, focusing one of our fields opens Steam's keyboard.</summary>
        private static void KeyboardForFocusedField()
        {
            string focus = GUI.GetNameOfFocusedControl() ?? "";
            if (focus == _lastFocus) return;
            _lastFocus = focus;
            if (!focus.StartsWith("ccoop_") || !SteamServices.OnScreenKeyboard) return;
            if (!FieldRects.TryGetValue(focus.Substring(6), out var screen)) return;
            SteamServices.ShowKeyboard(screen);
        }
    }

    /// <summary>Short-lived messages in the top centre of the screen.</summary>
    internal static class Notices
    {
        private struct Line
        {
            public string Text;
            public float Until;
        }

        private static readonly List<Line> Lines = new List<Line>();
        private static GUIStyle _style;

        private static readonly List<string> Recent = new List<string>();

        /// <summary>The last few notices, for the co-op window.</summary>
        internal static List<string> History => Recent;

        internal static void Push(string text)
        {
            Lines.Add(new Line { Text = text, Until = Time.unscaledTime + 5f });
            if (Lines.Count > 5) Lines.RemoveAt(0);
            Recent.Add(System.DateTime.Now.ToString("HH:mm") + "  " + text);
            if (Recent.Count > 6) Recent.RemoveAt(0);
        }

        private static float _alertAt = -99f;

        /// <summary>
        /// In a run, notices go through the game's own alert line (PlayerCamera.DoAlert), one
        /// after another so a burst doesn't overwrite itself; elsewhere they're drawn here.
        /// </summary>
        private static bool AsGameAlerts()
        {
            var cam = PlayerCamera.main;
            if (!GameInfo.InRunScene || cam == null || cam.body == null || !cam.body.alive) return false;
            float now = Time.unscaledTime;
            Lines.RemoveAll(l => l.Until + 6f < now);  // too late to matter
            float gap = Lines.Count > 2 ? 1.5f : 2.5f;
            if (Lines.Count > 0 && now - _alertAt >= gap)
            {
                try { cam.DoAlert(Lines[0].Text); }
                catch { return false; }
                Lines.RemoveAt(0);
                _alertAt = now;
            }
            return true;
        }

        internal static void Draw()
        {
            if (Lines.Count == 0) return;
            if (Event.current.type == EventType.Repaint && AsGameAlerts()) return;
            if (GameInfo.InRunScene && PlayerCamera.main != null && PlayerCamera.main.body != null && PlayerCamera.main.body.alive) return;
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleCenter, fontSize = UiSkin.Body, wordWrap = false };
                foreach (var st in new[] { _style.normal, _style.hover, _style.active }) st.textColor = UiSkin.Ink;
            }
            float now = Time.unscaledTime;
            Lines.RemoveAll(l => l.Until < now);
            float k = CoopWindow.Scale;
            var oldMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(k, k, 1f));
            try
            {
                // Each notice in its own small block, like the game's pop-up notifications,
                // fading out over its last second.
                float w = Screen.width / k;
                float y = 46f;
                var oldColor = GUI.color;
                foreach (var l in Lines)
                {
                    var style = UiSkin.Fit(_style, l.Text);
                    var size = style.CalcSize(new GUIContent(l.Text));
                    float bw = Mathf.Min(size.x + 4f, w - 40f);
                    GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(l.Until - now));
                    GUI.Box(new Rect(w / 2f - bw / 2f, y, bw, size.y + 2f), l.Text, style);
                    y += size.y + 6f;
                }
                GUI.color = oldColor;
            }
            finally { GUI.matrix = oldMatrix; }
        }
    }
}
