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
    internal static class CoopWindow
    {
        private const int WindowId = 0x0C00F;
        private const float Width = 490f;

        private static bool _open;
        private static Rect _rect = new Rect(20, 112, Width, 10);  // below the game's held-item box
        private static string _address;
        private static string _lanPassword = "";
        private static string _promptPassword = "";
        private static Vector2 _scroll, _browseScroll, _banScroll;
        private static GUIStyle _title, _small, _box, _right, _bad;

        private enum Tab { Host, Find, Lan }
        private static Tab _tab = Tab.Host;
        private static bool _friendsTab = true;
        private static bool _browsedOnce;
        private static bool _showBans;
        private static bool _showRules;
        private static bool _showRunSettings;
        private static bool _showMilestones;

        private static string _lastFocus = "";
        private static readonly Dictionary<string, Rect> FieldRects = new Dictionary<string, Rect>();

        internal static bool Open => _open;

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
            if (Input.GetKeyDown(CoopPlugin.Instance.MenuKey.Value))
            {
                _open = !_open;
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
            if (_title != null) return;
            _title = new GUIStyle(GUI.skin.label) { fontSize = UiSkin.Head };
            _small = new GUIStyle(GUI.skin.label) { fontSize = UiSkin.Small, wordWrap = true };
            _small.normal.textColor = UiSkin.Dim;
            _right = new GUIStyle(_small) { alignment = TextAnchor.MiddleRight, wordWrap = false };
            _bad = new GUIStyle(_small);
            _bad.normal.textColor = UiSkin.Bad;
            _box = new GUIStyle(GUI.skin.box);
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

                // A click outside the window hands the keyboard back to the game; IMGUI
                // would otherwise keep a text field focused (and game input blocked).
                var e = Event.current;
                if (e.type == EventType.MouseDown && !_rect.Contains(e.mousePosition) && _lastFocus.StartsWith("ccoop_"))
                {
                    GUIUtility.keyboardControl = 0;
                    _lastFocus = "";
                }

                _rect.height = 0;
                _rect = GUILayout.Window(WindowId, _rect, id => Body(rt), "CasualtiesCoop " + CoopPlugin.Version, GUILayout.Width(Width));
                _rect.x = Mathf.Clamp(_rect.x, 0f, Mathf.Max(0f, Screen.width / scale - 80f));
                _rect.y = Mathf.Clamp(_rect.y, 0f, Mathf.Max(0f, Screen.height / scale - 40f));
                UiMouse.Claim(_rect);
            }
            finally
            {
                GUI.matrix = oldMatrix;
            }
        }

        private static void Body(CoopRuntime rt)
        {
            if (_address == null) _address = CoopPlugin.Instance.LastAddress.Value;
            var s = Coop.Session;

            if (s == null && rt.PasswordPrompt != null) PasswordPrompt(rt);
            else if (s == null) Offline(rt);
            else Online(rt, s);

            if (!string.IsNullOrEmpty(rt.LastMessage) && s == null)
            {
                GUILayout.Space(6);
                GUILayout.Label(Lang.Unpack(rt.LastMessage), _small);
            }

            if (CoopPlugin.FailedPatches.Length > 0)
            {
                GUILayout.Space(4);
                GUILayout.Label(Lang.F("Some hooks failed to install ({0}). The game was probably updated; parts of co-op may not sync.",
                                       string.Join(", ", CoopPlugin.FailedPatches)), _bad);
            }

            if (GameCheck.Missing.Count > 0)
            {
                GUILayout.Space(4);
                GUILayout.Label(Lang.F("The game changed under these features: {0}. They may not work until the mod is updated.",
                                       string.Join("; ", GameCheck.Missing.ToArray())), _bad);
            }
            if (!string.IsNullOrEmpty(UpdateCheck.Notice))
            {
                GUILayout.Space(4);
                GUILayout.Label(UpdateCheck.Notice, _small);
            }

            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(new GUIContent(Lang.T("Bug report"), Lang.T("Saves your co-op logs and session details to a folder on your desktop.")), GUILayout.ExpandWidth(false)))
            {
                try { rt.LastMessage = Lang.F("Saved to {0}. Attach the folder's files to your report.", GameCheck.SaveReport()); }
                catch (System.Exception ex) { rt.LastMessage = Lang.F("Couldn't save the report: {0}", ex.Message); }
            }
            if (GUILayout.Button(Lang.T("Close"), GUILayout.ExpandWidth(false))) _open = false;
            GUILayout.EndHorizontal();

            KeyboardForFocusedField();
            GUI.DragWindow();
        }

        // ------------------------------------------------------------------ offline

        private static void Offline(CoopRuntime rt)
        {
            if (rt.Reconnecting)
            {
                GUILayout.BeginHorizontal(_box);
                GUILayout.Label(Lang.T("Lost the host. Reconnecting (your character is saved)..."), _small);
                if (GUILayout.Button(Lang.T("Stop"), GUILayout.MinWidth(60), GUILayout.ExpandWidth(false))) rt.CancelReconnect();
                GUILayout.EndHorizontal();
            }

            GUILayout.Label(Lang.T("Play a run together"), _title);

            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T("Name"), GUILayout.Width(60));
            string name = Field("name", CoopPlugin.Instance.PlayerName.Value, 24, false);
            if (name != CoopPlugin.Instance.PlayerName.Value) CoopPlugin.Instance.PlayerName.Value = name;
            GUILayout.EndHorizontal();
            if (string.IsNullOrEmpty(name)) GUILayout.Label(Lang.F("Using {0}", GameInfo.PlayerName), _small);

            GUILayout.Space(6);
            var tab = (Tab)GUILayout.Toolbar((int)_tab, new[] { Lang.T("Host"), Lang.T("Find games"), Lang.T("LAN / IP") }, GUILayout.Height(26));
            if (tab != _tab)
            {
                _tab = tab;
                if (_tab == Tab.Find && !_browsedOnce) Refresh(rt);
            }
            GUILayout.Space(4);

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
            if (!GameInfo.SteamReady)
            {
                GUILayout.Label(Lang.T("Steam is not available. Launch the game through Steam to host with invites, or use LAN / IP."), _small);
                return;
            }
            RulesEditor(rt, null);

            RunPresets.Section(_box, _small);
            SavedRuns();

            GUILayout.Space(4);
            if (GUILayout.Button(CoopSaves.PendingContinue != null ? Lang.T("Host with Steam and continue") : Lang.T("Host with Steam"), GUILayout.Height(32))) rt.HostSteam();
            GUILayout.Label(Lang.T("Host, then start a run: friends who join are brought into it."), _small);

            var bans = BanList.All;
            if (bans.Count > 0)
            {
                GUILayout.Space(4);
                _showBans = GUILayout.Toggle(_showBans, " " + Lang.F("Banned players ({0})", bans.Count));
                if (_showBans)
                {
                    _banScroll = GUILayout.BeginScrollView(_banScroll, GUILayout.Height(Mathf.Min(30 + bans.Count * 24, 140)));
                    foreach (var b in new List<BanList.Entry>(bans))
                    {
                        GUILayout.BeginHorizontal(_box);
                        GUILayout.Label(string.IsNullOrEmpty(b.Name) ? b.Key : b.Name, GUILayout.Width(170));
                        GUILayout.Label(b.When, _small, GUILayout.Width(80));
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button(Lang.T("Unban"), GUILayout.MinWidth(60), GUILayout.ExpandWidth(false))) BanList.Unban(b.Key);
                        GUILayout.EndHorizontal();
                    }
                    GUILayout.EndScrollView();
                }
            }
        }

        private static readonly string[] LobbyTypes = { "FriendsOnly", "Public", "InviteOnly" };
        private static readonly string[] RespawnRules = { "NextLayer", "Permadeath", "Quick" };
        private static readonly int[] ExitShares = { 0, 50, 100 };

        /// <summary>
        /// The host's rules: before hosting (Host tab) and live while hosting (the Rules
        /// panel). Everything here is config, read every frame by <see cref="RulesSync"/>.
        /// </summary>
        private static void RulesEditor(CoopRuntime rt, NetSession live)
        {
            var cfg = CoopPlugin.Instance;
            bool steamLobby = live == null ? GameInfo.SteamReady : rt.TransportKind == "Steam" && rt.Lobby.InLobby;

            var rules = HostRules.FromConfig();
            int preset = rules.Preset;
            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T("Difficulty"), GUILayout.Width(134));
            int pickPreset = GUILayout.Toolbar(preset, Lang.All(HostRules.PresetNames));
            GUILayout.EndHorizontal();
            if (pickPreset != preset && pickPreset >= 0) HostRules.ApplyPreset(pickPreset);

            if (steamLobby)
            {
                int cur = Mathf.Max(0, System.Array.IndexOf(LobbyTypes, cfg.LobbyType.Value));
                GUILayout.BeginHorizontal();
                GUILayout.Label(Lang.T("Who can join"), GUILayout.Width(134));
                int pick = GUILayout.Toolbar(cur, new[] { Lang.T("Friends"), Lang.T("Public"), Lang.T("Invite only") });
                GUILayout.EndHorizontal();
                if (pick != cur)
                {
                    cfg.LobbyType.Value = LobbyTypes[pick];
                    if (live != null) rt.Lobby.SetType(LobbyTypes[pick]);
                }
                GUILayout.Label(pick == 1
                    ? Lang.T("Listed for everyone under Find games. Add a password to keep strangers out.")
                    : pick == 2
                        ? Lang.T("Only people you invite through Steam can join.")
                        : Lang.T("Steam friends can join from Find games, your profile (\"Join Game\") or an invite."), _small);
            }

            int floor = Mathf.Max(2, live != null ? live.PeerCount : 2);
            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T("Players"), GUILayout.Width(134));
            if (GUILayout.Button(Lang.T("-"), GUILayout.MinWidth(28), GUILayout.ExpandWidth(false)) && cfg.MaxPlayers.Value > floor) cfg.MaxPlayers.Value--;
            GUILayout.Label(cfg.MaxPlayers.Value.ToString(), GUILayout.Width(24));
            if (GUILayout.Button(Lang.T("+"), GUILayout.MinWidth(28), GUILayout.ExpandWidth(false)) && cfg.MaxPlayers.Value < NetSession.MaxPeers) cfg.MaxPlayers.Value++;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T("Password"), GUILayout.Width(134));
            string pw = Field(live == null ? "hostpw" : "livepw", cfg.HostPassword.Value, 64, true);
            if (pw != cfg.HostPassword.Value) cfg.HostPassword.Value = pw;
            GUILayout.EndHorizontal();

            int rule = Mathf.Max(0, System.Array.IndexOf(RespawnRules, cfg.Respawn.Value));
            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T("When you die"), GUILayout.Width(134));
            int newRule = GUILayout.Toolbar(rule, new[] { Lang.T("Next layer"), Lang.T("Permadeath"), Lang.T("Quick") });
            GUILayout.EndHorizontal();
            if (newRule != rule) cfg.Respawn.Value = RespawnRules[newRule];
            GUILayout.Label(newRule == 1 ? Lang.T("The dead spectate for the rest of the run.")
                : newRule == 2 ? Lang.F("The dead spectate, then come back beside a teammate after {0} s.", (int)LifeSync.QuickDelay)
                : Lang.T("The dead spectate, then come back healed when the team reaches the next layer."), _small);

            int share = cfg.ExitShare.Value;
            int shareAt = share <= 0 ? 0 : share >= 100 ? 2 : share == 50 ? 1 : -1;
            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T("Descend when"), GUILayout.Width(134));
            int newShare = GUILayout.Toolbar(shareAt, new[] { Lang.T("First arrives"), Lang.T("Half are there"), Lang.T("All are there") });
            GUILayout.EndHorizontal();
            if (newShare != shareAt && newShare >= 0) cfg.ExitShare.Value = ExitShares[newShare];
            if (shareAt < 0) GUILayout.Label(Lang.F("(Set to {0}% in the config.)", share), _small);

            // One toggle a line once translated text gets long (a German "Teammate arrows" is not short).
            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T("Help"), GUILayout.Width(134));
            GUILayout.BeginVertical();
            bool narrow = Wide(Width - 170f, " " + Lang.T("Teammate arrows"), " " + Lang.T("Name tags"), " " + Lang.T("Spawn apart"));
            if (!narrow) GUILayout.BeginHorizontal();
            cfg.HostArrows.Value = GUILayout.Toggle(cfg.HostArrows.Value, " " + Lang.T("Teammate arrows"), GUILayout.ExpandWidth(false));
            cfg.HostNameTags.Value = GUILayout.Toggle(cfg.HostNameTags.Value, " " + Lang.T("Name tags"), GUILayout.ExpandWidth(false));
            cfg.SpawnApart.Value = GUILayout.Toggle(cfg.SpawnApart.Value, " " + Lang.T("Spawn apart"), GUILayout.ExpandWidth(false));
            if (!narrow) GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T("Danger"), GUILayout.Width(134));
            cfg.FriendlyFire.Value = GUILayout.Toggle(cfg.FriendlyFire.Value, " " + Lang.T("Friendly fire (shots and blows hurt teammates)"));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T("Voices carry"), GUILayout.Width(134));
            int pct = Mathf.RoundToInt(GUILayout.HorizontalSlider(cfg.VoiceRangePercent.Value, 25f, 100f, GUILayout.Width(200)) / 5f) * 5;
            if (pct != cfg.VoiceRangePercent.Value) cfg.VoiceRangePercent.Value = pct;
            GUILayout.Label(Lang.F("{0}%", pct), GUILayout.Width(50));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T("Loot"), GUILayout.Width(134));
            int loot = GUILayout.Toolbar(Mathf.Clamp(cfg.LootScale.Value, 0, 1), Lang.All(HostRules.LootNames));
            if (loot != cfg.LootScale.Value) cfg.LootScale.Value = loot;
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T("Enemies"), GUILayout.Width(134));
            int danger = GUILayout.Toolbar(Mathf.Clamp(cfg.DangerScale.Value, 0, 2), Lang.All(HostRules.DangerNames));
            if (danger != cfg.DangerScale.Value) cfg.DangerScale.Value = danger;
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T("Speed-up"), GUILayout.Width(134));
            cfg.AllowSpeedUp.Value = GUILayout.Toggle(cfg.AllowSpeedUp.Value, " " + (cfg.AllowSpeedUp.Value ? Lang.T("Anyone may fast-forward time") : Lang.T("Time always runs at normal speed (sleep still fast-forwards)")));
            GUILayout.EndHorizontal();
        }

        private static bool _showSaves;

        private static void SavedRuns()
        {
            var runs = CoopSaves.List();
            if (runs.Count == 0) return;
            GUILayout.Space(4);
            _showSaves = GUILayout.Toggle(_showSaves, " " + Lang.F("Continue a co-op run ({0})", runs.Count));
            if (!_showSaves) return;
            foreach (var r in runs)
            {
                GUILayout.BeginHorizontal(_box);
                bool picked = CoopSaves.PendingContinue == r.Id;
                GUILayout.Label(Lang.F("Layer {0}", r.Layer) + " · " + r.When.ToString("MMM d HH:mm") + (r.Players.Length > 0 ? " · " + r.Players : ""), _small, GUILayout.Width(280));
                if (GUILayout.Button(picked ? Lang.T("Picked") : Lang.T("Continue"), GUILayout.MinWidth(70), GUILayout.ExpandWidth(false))) CoopSaves.PendingContinue = picked ? null : r.Id;
                if (GUILayout.Button(Lang.T("Delete"), GUILayout.MinWidth(55), GUILayout.ExpandWidth(false))) { CoopSaves.Delete(r.Id); if (picked) CoopSaves.PendingContinue = null; }
                GUILayout.EndHorizontal();
            }
            if (CoopSaves.PendingContinue != null) GUILayout.Label(Lang.T("Host, then press \"Start the saved run\" when your friends are in."), _small);
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
                GUILayout.Label(Lang.T("Steam is not available. Launch the game through Steam to find games, or use LAN / IP."), _small);
                return;
            }
            var lobby = rt.Lobby;

            GUILayout.BeginHorizontal();
            int which = GUILayout.Toolbar(_friendsTab ? 0 : 1, new[] { Lang.T("Friends"), Lang.T("Public") }, GUILayout.Width(200));
            _friendsTab = which == 0;
            GUILayout.FlexibleSpace();
            GUI.enabled = !lobby.Searching && !rt.Connecting;
            if (GUILayout.Button(lobby.Searching ? Lang.T("Searching...") : Lang.T("Refresh"), GUILayout.MinWidth(100), GUILayout.ExpandWidth(false))) Refresh(rt);
            GUI.enabled = !rt.Connecting;
            GUILayout.EndHorizontal();

            var list = new List<LobbyInfo>(_friendsTab ? lobby.FriendLobbies : lobby.PublicLobbies);
            if (list.Count == 0)
            {
                GUILayout.Label(lobby.Searching ? Lang.T("Looking for sessions...")
                    : _friendsTab ? Lang.T("None of your Steam friends is hosting right now.")
                    : Lang.T("No public sessions found. Hosts choose Public to be listed here."), _small);
                return;
            }

            _browseScroll = GUILayout.BeginScrollView(_browseScroll, GUILayout.Height(Mathf.Min(20 + list.Count * 52, 260)));
            foreach (var l in list) LobbyRow(rt, l);
            GUILayout.EndScrollView();
        }

        private static void LobbyRow(CoopRuntime rt, LobbyInfo l)
        {
            string why = l.Incompatible;
            bool full = l.Max > 0 && l.Players >= l.Max;

            GUILayout.BeginHorizontal(_box);
            AvatarBox(l.HostId, 36);
            GUILayout.BeginVertical();
            string hostLine = l.Host + (l.Locked ? "  [" + Lang.T("password") + "]" : "");
            GUILayout.Label(hostLine, UiSkin.Fit(GUI.skin.label, hostLine), GUILayout.Width(220));
            // The advert carries the layer number (older builds sent "Layer 5").
            string where = l.InRun ? (string.IsNullOrEmpty(l.Layer) ? Lang.T("In a run") : int.TryParse(l.Layer, out var depth) ? Lang.F("Layer {0}", depth) : l.Layer) : Lang.T("In the menu");
            string sub = where + " · " + Lang.F("{0}/{1} players", l.Players, l.Max);
            if (l.ViaFriend && !string.IsNullOrEmpty(l.FriendName) && l.FriendName != l.Host) sub += " · " + Lang.F("with {0}", l.FriendName);
            GUILayout.Label(sub, _small, GUILayout.Width(220));
            var rules = HostRules.Decode(l.Rules);
            if (rules != null) GUILayout.Label(Lang.F("Dead: {0} · {1}", rules.RespawnText, rules.ExitText), _small, GUILayout.Width(220));
            if (why != null) GUILayout.Label(Lang.F("Can't join: {0}", why), _bad, GUILayout.Width(220));
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();

            var old = GUI.color;
            GUI.color = PingColor(l.Ping, -1f);
            GUILayout.Label(l.Ping >= 0 ? "~" + l.Ping + " ms" : "? ms", _right, GUILayout.Width(60), GUILayout.Height(36));
            GUI.color = old;

            bool was = GUI.enabled;
            GUI.enabled = was && why == null && !full;
            if (GUILayout.Button(full ? Lang.T("Full") : Lang.T("Join"), GUILayout.MinWidth(60), GUILayout.ExpandWidth(false), GUILayout.Height(36)))
            {
                _promptPassword = "";
                rt.JoinSteamLobby(l.Id);
            }
            GUI.enabled = was;
            GUILayout.EndHorizontal();
        }

        private static void LanTab(CoopRuntime rt)
        {
            GUILayout.Label(Lang.F("Direct connection over your LAN or a VPN (Tailscale, ZeroTier, Radmin). Over the internet the host must forward UDP port {0}.", CoopPlugin.Instance.Port.Value), _small);
            if (GUILayout.Button(Lang.F("Host on port {0}", CoopPlugin.Instance.Port.Value), GUILayout.Height(28))) rt.HostLan();
            GUILayout.Label(Lang.T("The Host tab's password and respawn rule apply here too."), _small);

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T("Address"), GUILayout.Width(70));
            _address = Field("address", _address, 64, false);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T("Password"), GUILayout.Width(70));
            _lanPassword = Field("lanpw", _lanPassword, 64, true);
            if (GUILayout.Button(Lang.T("Join"), GUILayout.MinWidth(70), GUILayout.ExpandWidth(false), GUILayout.Height(22))) rt.JoinLan(_address, _lanPassword);
            GUILayout.EndHorizontal();
            GUILayout.Label(Lang.T("Leave the password empty if the host has none."), _small);
        }

        private static void PasswordPrompt(CoopRuntime rt)
        {
            var l = rt.PasswordPrompt;
            GUILayout.Label(Lang.T("Password needed"), _title);
            GUILayout.BeginHorizontal();
            AvatarBox(l.HostId, 36);
            GUILayout.Label(Lang.F("{0}'s session is locked. Ask them for the password.", l.Host), _small);
            GUILayout.EndHorizontal();

            bool submit = Event.current.type == EventType.KeyDown
                          && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)
                          && GUI.GetNameOfFocusedControl() == "ccoop_prompt";
            _promptPassword = Field("prompt", _promptPassword, 64, true);

            GUILayout.BeginHorizontal();
            GUI.enabled = !rt.Connecting;
            if (GUILayout.Button(Lang.T("Join"), GUILayout.Height(28)) || (submit && _promptPassword.Length > 0))
            {
                if (submit) Event.current.Use();
                string pw = _promptPassword;
                _promptPassword = "";
                rt.JoinSteamLobby(l.Id, pw);
            }
            GUI.enabled = true;
            if (GUILayout.Button(Lang.T("Cancel"), GUILayout.MinWidth(80), GUILayout.ExpandWidth(false), GUILayout.Height(28)))
            {
                rt.PasswordPrompt = null;
                rt.LastMessage = "";
            }
            GUILayout.EndHorizontal();
        }

        // ------------------------------------------------------------------ online

        private static void Online(CoopRuntime rt, NetSession s)
        {
            string role = s.IsHost ? Lang.T("Hosting") : (s.State == SessionState.Established ? Lang.T("Connected") : Lang.T("Connecting"));
            GUILayout.Label(Lang.F("{0} · {1}", role, rt.TransportKind), _title);

            var flow = Coop.Get<RunFlow>();
            if (flow != null) GUILayout.Label(flow.Describe(), _small);
            GUILayout.Label(HostRules.Current.Shown(), _small);

            if (s.IsHost && rt.TransportKind == "Steam" && rt.Lobby.InLobby)
            {
                var cfg = CoopPlugin.Instance;
                string type = cfg.LobbyType.Value == "Public" ? "Public" : cfg.LobbyType.Value == "InviteOnly" ? "Invite only" : "Friends";
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(Lang.T("Invite Steam friends"), GUILayout.Height(28))) rt.Lobby.Invite();
                GUILayout.Label(Lang.T(type) + (string.IsNullOrEmpty(cfg.HostPassword.Value) ? "" : " · " + Lang.T("password")), _right, GUILayout.Width(130), GUILayout.Height(28));
                GUILayout.EndHorizontal();
            }
            if (s.IsHost)
            {
                _showRules = GUILayout.Toggle(_showRules, " " + Lang.T("Rules (change them any time)"));
                if (_showRules)
                {
                    GUILayout.BeginVertical(_box);
                    RulesEditor(rt, s);
                    GUILayout.EndVertical();
                }
                RunPresets.Section(_box, _small);  // only before the run starts
            }

            if (GameInfo.InRunScene && WorldGeneration.runSettings != null)
            {
                _showRunSettings = GUILayout.Toggle(_showRunSettings, " " + (s.IsHost ? Lang.T("Run settings") : Lang.T("Run settings (the host's)")));
                if (_showRunSettings)
                {
                    GUILayout.BeginVertical(_box);
                    foreach (var line in RunSettingsReadout.Lines(WorldGeneration.runSettings)) GUILayout.Label(line, _small);
                    GUILayout.EndVertical();
                }
            }

            VoiceRow();
            ComfortRow();
            MilestoneList();

            GUILayout.Space(6);
            GUILayout.Label(Lang.F("Players ({0}/{1})", s.PeerCount, HostRules.Current.MaxPlayers));
            _scroll = GUILayout.BeginScrollView(_scroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUILayout.Height(Mathf.Min(12 + s.PeerCount * 64, 260)));
            var ids = new List<byte>();
            foreach (var p in s.Peers) ids.Add(p.Id);
            ids.Sort();
            foreach (var id in ids)
            {
                if (!s.TryGetPeer(id, out var p)) continue;
                PlayerRow(rt, s, p, Coop.Player(id));
            }
            GUILayout.EndScrollView();

            if (s.IsHost && GameInfo.OnMainMenu && CoopSaves.PendingContinue != null)
            {
                if (GUILayout.Button(Lang.T("Start the saved run"), GUILayout.Height(30))) CoopSaves.StartContinue();
                GUILayout.Label(Lang.T("Players who join get their own characters back from this run; newcomers start fresh."), _small);
            }

            if (!s.IsHost && flow != null && flow.CanJoinHostRun)
            {
                if (GUILayout.Button(Lang.T("Join the host's run"), GUILayout.Height(30))) flow.JoinHostRun();
            }

            GUILayout.Space(6);
            if (GUILayout.Button(s.IsHost ? Lang.T("End session") : Lang.T("Leave session"), GUILayout.Height(26))) rt.Leave();

            var history = Notices.History;
            if (history.Count > 0)
            {
                GUILayout.Space(4);
                for (int i = Mathf.Max(0, history.Count - 2); i < history.Count; i++) GUILayout.Label(history[i], _small);
            }
        }

        private static readonly string[] VoiceModes = { "PushToTalk", "Open", "Off" };

        /// <summary>The voice row is showing the mic meter (open mic).</summary>
        internal static bool MeasuringMic;

        private static void VoiceRow()
        {
            var cfg = CoopPlugin.Instance;
            if (!VoiceSync.Available)
            {
                return;
            }
            int cur = Mathf.Max(0, System.Array.IndexOf(VoiceModes, cfg.VoiceMode.Value));
            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T("Voice"), GUILayout.Width(50));
            int pick = GUILayout.Toolbar(cur, new[] { Lang.F("Hold {0}", cfg.VoiceKey.Value), Lang.T("Open mic"), Lang.T("Off") });
            if (pick != cur) cfg.VoiceMode.Value = VoiceModes[pick];
            GUILayout.EndHorizontal();
            MeasuringMic = cfg.VoiceMode.Value == "Open";
            if (MeasuringMic)
            {
                // Open mic threshold, with this player's live mic level beside it.
                GUILayout.BeginHorizontal();
                GUILayout.Label(Lang.T("Send above"), _small, GUILayout.Width(70));
                cfg.VoiceGate.Value = GUILayout.HorizontalSlider(cfg.VoiceGate.Value, 0f, 0.3f, GUILayout.Width(140));
                GUILayout.Label(cfg.VoiceGate.Value <= 0.001f ? Lang.T("everything") : Mathf.RoundToInt(cfg.VoiceGate.Value / 0.3f * 100f) + "%", _small, GUILayout.Width(70));
                var bar = GUILayoutUtility.GetRect(100f, 10f, GUILayout.Width(100f));
                GUI.Box(bar, GUIContent.none);
                float lvl = Mathf.Clamp01(VoiceSync.MicLevel / 0.3f);
                var oldColor = GUI.color;
                GUI.color = VoiceSync.MicLevel >= cfg.VoiceGate.Value ? new Color(0.5f, 1f, 0.5f) : new Color(0.7f, 0.7f, 0.7f);
                GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * lvl, bar.height), Texture2D.whiteTexture);
                GUI.color = oldColor;
                GUILayout.EndHorizontal();
            }
            if (HostRules.Current.Arrows)
                cfg.ShowArrows.Value = GUILayout.Toggle(cfg.ShowArrows.Value, " " + Lang.T("Show arrows to teammates who are off screen"));
        }

        private static void MilestoneList()
        {
            _showMilestones = GUILayout.Toggle(_showMilestones, " " + Lang.F("Co-op milestones ({0}/{1})", Milestones.Reached, Milestones.All.Length));
            if (!_showMilestones) return;
            GUILayout.BeginVertical(_box);
            foreach (var d in Milestones.All)
            {
                float n = Milestones.Count(d.Key);
                bool done = n >= d.Goal;
                GUILayout.BeginHorizontal();
                GUILayout.Label((done ? "✓ " : "   ") + Lang.T(d.Title), GUILayout.Width(150));
                GUILayout.Label(Lang.T(d.Text), _small);
                GUILayout.FlexibleSpace();
                GUILayout.Label(done ? Lang.T("done") : Mathf.FloorToInt(n) + "/" + d.Goal + (d.Unit ?? ""), _small, GUILayout.Width(70));
                GUILayout.EndHorizontal();
            }
            GUILayout.EndVertical();
        }

        /// <summary>Personal display options: these never change anything for anyone else.</summary>
        private static void ComfortRow()
        {
            var cfg = CoopPlugin.Instance;
            bool narrow = Wide(Width - 40f, " " + Lang.T("Reduce flashes and shaking"), " " + Lang.T("Colour-blind safe colours"));
            if (!narrow) GUILayout.BeginHorizontal();
            cfg.ReduceFlashes.Value = GUILayout.Toggle(cfg.ReduceFlashes.Value, " " + Lang.T("Reduce flashes and shaking"), GUILayout.ExpandWidth(false));
            cfg.ColorBlindColors.Value = GUILayout.Toggle(cfg.ColorBlindColors.Value, " " + Lang.T("Colour-blind safe colours"), GUILayout.ExpandWidth(false));
            if (!narrow) GUILayout.EndHorizontal();
        }

        private static void PlayerRow(CoopRuntime rt, NetSession s, PeerInfo p, PlayerRecord rec)
        {
            bool me = p.Id == s.LocalId;
            // Two lines: who they are (and what you can do), then how they're doing.
            GUILayout.BeginHorizontal(_box);
            AvatarBox(rec.SteamId, 32);
            GUILayout.BeginVertical();
            var voice = Coop.Get<VoiceSync>();
            bool talking = voice != null && (me ? voice.Transmitting : voice.IsTalking(p.Id));
            string who = p.Name + (me ? " (" + Lang.T("you") + ")" : "") + (p.IsHost ? " [" + Lang.T("host") + "]" : "") + (talking ? "  ((o))" : "");
            GUILayout.BeginHorizontal();
            GUILayout.Label(who, UiSkin.Fit(GUI.skin.label, who));
            GUILayout.FlexibleSpace();
            RowButtons(rt, s, p, rec, me, voice);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label(GameInfo.CharacterName(rec.Character) + " · " + Lang.T(rec.Status), _small, GUILayout.ExpandWidth(false));
            var fpsColor = GUI.color;
            if (rec.Fps > 0 && rec.Fps < ConnectionBadge.SlowFps) GUI.color = UiSkin.Bad;
            if (rec.Fps > 0) GUILayout.Label("· " + rec.Fps + " fps", _small, GUILayout.ExpandWidth(false));
            GUI.color = fpsColor;
            string ping = "";
            if (!p.IsHost && p.RoundTripMs >= 0)
            {
                ping = "· " + p.RoundTripMs + " ms";
                if (p.Quality >= 0f && p.Quality < 0.95f) ping += " · " + Mathf.RoundToInt((1f - p.Quality) * 100f) + "%";
            }
            var old = GUI.color;
            GUI.color = PingColor(p.RoundTripMs, p.Quality);
            if (ping.Length > 0) GUILayout.Label(ping, _small, GUILayout.ExpandWidth(false));
            GUI.color = old;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
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

        /// <summary>Whether these toggles side by side would be wider than <paramref name="room"/> (then they stack).</summary>
        private static bool Wide(float room, params string[] labels)
        {
            float w = 0f;
            foreach (var l in labels) w += GUI.skin.toggle.CalcSize(new GUIContent(l)).x + 8f;
            return w > room;
        }

        private static void AvatarBox(ulong steamId, float size)
        {
            var r = GUILayoutUtility.GetRect(size, size, GUILayout.Width(size), GUILayout.Height(size));
            var tex = SteamServices.Avatar(steamId);
            if (tex != null) GUI.DrawTexture(r, tex, ScaleMode.ScaleToFit);
        }

        /// <summary>A named text field, remembered so the on-screen keyboard can be placed beside it.</summary>
        private static string Field(string id, string value, int max, bool secret)
        {
            GUI.SetNextControlName("ccoop_" + id);
            string v = secret ? GUILayout.PasswordField(value ?? "", '*', max) : GUILayout.TextField(value ?? "", max);
            if (Event.current.type == EventType.Repaint) FieldRects[id] = GUILayoutUtility.GetLastRect();
            return v;
        }

        /// <summary>On Deck / Big Picture, focusing one of our fields opens Steam's keyboard.</summary>
        private static void KeyboardForFocusedField()
        {
            string focus = GUI.GetNameOfFocusedControl() ?? "";
            if (focus == _lastFocus) return;
            _lastFocus = focus;
            if (!focus.StartsWith("ccoop_") || !SteamServices.OnScreenKeyboard) return;
            if (!FieldRects.TryGetValue(focus.Substring(6), out var local)) return;
            float k = Scale;
            SteamServices.ShowKeyboard(new Rect((_rect.x + local.x) * k, (_rect.y + local.y) * k, local.width * k, local.height * k));
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

        internal static void Draw()
        {
            if (Lines.Count == 0) return;
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
