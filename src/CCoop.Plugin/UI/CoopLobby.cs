using System.Collections.Generic;
using CCoop.Net.Session;
using CCoop.Plugin.Runtime;
using CCoop.Plugin.Sync;
using UnityEngine;

namespace CCoop.Plugin.UI
{
    /// <summary>
    /// The co-op lobby: a full-screen menu laid out like the game's own (its run-setup
    /// and character screens): black, divided by thin white lines, a title bar, the
    /// players as their characters' portraits along the top (an open place is the grey
    /// outline the game uses for a locked character), the session's facts in the game's
    /// bracketed terminal style, the settings beside them, and Back with the session's
    /// actions along the bottom. In a run it covers the screen like a pause menu (the
    /// world goes on: co-op can't pause), and Esc or F1 closes it.
    /// </summary>
    internal static partial class CoopWindow
    {
        private const float Bar = 58f;
        private static Vector2 _lobbyScroll;
        private static GUIStyle _bigName, _tag, _value, _slotNote, _heading;

        private static void LobbyStyles()
        {
            if (_bigName != null) return;
            _bigName = new GUIStyle(GUI.skin.label) { fontSize = UiSkin.Big, alignment = TextAnchor.MiddleCenter, wordWrap = false, clipping = TextClipping.Clip };
            _heading = new GUIStyle(GUI.skin.label) { fontSize = UiSkin.Big, alignment = TextAnchor.MiddleCenter, wordWrap = false };
            _tag = new GUIStyle(GUI.skin.label) { fontSize = UiSkin.Body, wordWrap = false };
            _tag.normal.textColor = UiSkin.Dim;
            _value = new GUIStyle(GUI.skin.label) { fontSize = UiSkin.Body, wordWrap = true };
            _slotNote = new GUIStyle(GUI.skin.label) { fontSize = UiSkin.Small, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            _slotNote.normal.textColor = UiSkin.Dim;
        }

        private static void Lobby(CoopRuntime rt)
        {
            LobbyStyles();
            if (_address == null) _address = CoopPlugin.Instance.LastAddress.Value;
            var s = Coop.Session;
            float w = Screen.width / Scale, h = Screen.height / Scale;
            var all = new Rect(0f, 0f, w, h);
            GUI.DrawTexture(all, GameLook.Solid(new Color(0f, 0f, 0f, GameInfo.InRunScene ? 0.9f : 0.96f)));
            UiMouse.Claim(all);

            // ---- title bar
            GUI.Label(new Rect(0f, 0f, w, Bar), Lang.T("Co-op"), _heading);
            string status = s == null ? (rt.Reconnecting ? Lang.T("Reconnecting...") : Lang.T("Not in a session"))
                : (s.IsHost ? Lang.T("Hosting") : s.State == SessionState.Established ? Lang.T("Connected") : Lang.T("Connecting")) + " · " + rt.TransportKind;
            GUI.Label(new Rect(18f, 0f, w * 0.3f, Bar), status, _tag);
            var ver = new GUIStyle(_tag) { alignment = TextAnchor.MiddleRight };
            GUI.Label(new Rect(w * 0.7f - 18f, 0f, w * 0.3f, Bar), "CasualtiesCoop " + CoopPlugin.Version, ver);
            Line(new Rect(0f, Bar, w, 1f));

            // ---- the players
            float slotsH = Mathf.Round((h - Bar * 2f) * 0.46f);
            Slots(rt, s, new Rect(0f, Bar + 1f, w, slotsH - 1f));
            Line(new Rect(0f, Bar + slotsH, w, 1f));

            // ---- facts | settings
            float lowY = Bar + slotsH + 1f, lowH = h - Bar * 2f - slotsH - 1f;
            float leftW = Mathf.Round(w * 0.36f);
            GUILayout.BeginArea(new Rect(18f, lowY + 10f, leftW - 30f, lowH - 16f));
            Facts(rt, s);
            GUILayout.EndArea();
            Line(new Rect(leftW, lowY, 1f, lowH));
            GUILayout.BeginArea(new Rect(leftW + 14f, lowY + 8f, w - leftW - 28f, lowH - 12f));
            _lobbyScroll = GUILayout.BeginScrollView(_lobbyScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar);
            if (s == null && rt.PasswordPrompt != null) PasswordPrompt(rt);
            else if (s == null) Offline(rt);
            else Settings(rt, s);
            GUILayout.EndScrollView();
            GUILayout.EndArea();

            // ---- bottom bar
            Line(new Rect(0f, h - Bar, w, 1f));
            GUILayout.BeginArea(new Rect(14f, h - Bar + 10f, w - 28f, Bar - 18f));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Lang.T("Back"), GUILayout.MinWidth(120f), GUILayout.ExpandHeight(true))) _open = false;
            GUILayout.FlexibleSpace();
            Actions(rt, s);
            if (GUILayout.Button(new GUIContent(Lang.T("Bug report"), Lang.T("Saves your co-op logs and session details to a folder on your desktop.")), GUILayout.MinWidth(120f), GUILayout.ExpandHeight(true)))
            {
                try { rt.LastMessage = Lang.F("Saved to {0}. Attach the folder's files to your report.", GameCheck.SaveReport()); }
                catch (System.Exception ex) { rt.LastMessage = Lang.F("Couldn't save the report: {0}", ex.Message); }
            }
            GUILayout.EndHorizontal();
            GUILayout.EndArea();

            KeyboardForFocusedField();
        }

        private static void Line(Rect r) => GUI.DrawTexture(r, GameLook.Solid(new Color(1f, 1f, 1f, 0.85f)));

        // ------------------------------------------------------------------ player slots

        private static void Slots(CoopRuntime rt, NetSession s, Rect area)
        {
            var ids = new List<byte>();
            if (s != null) { foreach (var p in s.Peers) ids.Add(p.Id); ids.Sort(); }
            int places = Mathf.Clamp(s != null ? Mathf.Max(HostRules.Current.MaxPlayers, ids.Count) : CoopPlugin.Instance.MaxPlayers.Value, 2, 4);
            float colW = area.width / places;
            for (int i = 0; i < places; i++)
            {
                var r = new Rect(area.x + i * colW, area.y, colW, area.height);
                if (i > 0) Line(new Rect(r.x, r.y, 1f, r.height));
                if (s == null)
                {
                    if (i == 0) Slot(r, LocalCharacter(), GameInfo.PlayerName, Lang.T("you"), true, null);
                    else Slot(r, i % 3, null, Lang.T("Open"), false, null);
                }
                else if (i < ids.Count && s.TryGetPeer(ids[i], out var peer))
                {
                    var rec = Coop.Player(ids[i]);
                    bool me = peer.Id == s.LocalId;
                    Slot(r, rec.Character, peer.Name, SlotNote(s, peer, rec, me), true, () =>
                    {
                        GUILayout.BeginHorizontal();
                        GUILayout.FlexibleSpace();
                        RowButtons(rt, s, peer, rec, me, Coop.Get<VoiceSync>());
                        GUILayout.FlexibleSpace();
                        GUILayout.EndHorizontal();
                    });
                }
                else
                {
                    bool canInvite = s.IsHost && rt.TransportKind == "Steam" && rt.Lobby.InLobby;
                    Slot(r, i % 3, null, Lang.T("Open"), false, canInvite ? () =>
                    {
                        GUILayout.BeginHorizontal();
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button(Lang.T("Invite"), GUILayout.MinWidth(90f))) rt.Lobby.Invite();
                        GUILayout.FlexibleSpace();
                        GUILayout.EndHorizontal();
                    } : (System.Action)null);
                }
            }
        }

        /// <summary>One place: the character's portrait (or a grey outline when open), the name, a note, buttons.</summary>
        private static void Slot(Rect r, int character, string name, string note, bool taken, System.Action buttons)
        {
            float textH = 78f + (buttons != null ? 34f : 0f);
            var pic = new Rect(r.x + 12f, r.y + 14f, r.width - 24f, r.height - textH - 18f);
            // As the game's character screen draws them: the white silhouette (charNoutline)
            // with the character over it, in colour for a player, in black for an open place
            // (which leaves just the thin grey outline of a locked character).
            int c = Mathf.Clamp(character, 0, 2) + 1;
            var outline = GameLook.Texture("char" + c + "outline");
            var figure = GameLook.Texture("char" + c);
            var basis = outline ?? figure;
            if (basis != null)
            {
                float k = Mathf.Min(pic.width / basis.width, pic.height / basis.height);
                var size = new Vector2(basis.width * k, basis.height * k);
                var at = new Rect(pic.center.x - size.x / 2f, pic.yMax - size.y, size.x, size.y);
                var old = GUI.color;
                if (outline != null)
                {
                    GUI.color = taken ? Color.white : new Color(0.45f, 0.45f, 0.45f, 1f);
                    GUI.DrawTexture(at, outline);
                }
                if (figure != null)
                {
                    var inner = outline != null
                        ? new Rect(at.center.x - figure.width * k / 2f, at.center.y - figure.height * k / 2f, figure.width * k, figure.height * k)
                        : at;
                    GUI.color = taken ? Color.white : Color.black;
                    GUI.DrawTexture(inner, figure);
                }
                GUI.color = old;
            }
            float y = r.yMax - textH;
            if (!string.IsNullOrEmpty(name)) GUI.Label(new Rect(r.x + 6f, y, r.width - 12f, 34f), name, UiSkin.Fit(_bigName, name));
            GUI.Label(new Rect(r.x + 6f, y + 34f, r.width - 12f, 40f), note, _slotNote);
            if (buttons != null)
            {
                GUILayout.BeginArea(new Rect(r.x + 4f, r.yMax - 38f, r.width - 8f, 34f));
                buttons();
                GUILayout.EndArea();
            }
        }

        private static string SlotNote(NetSession s, PeerInfo p, PlayerRecord rec, bool me)
        {
            var parts = new List<string>();
            if (p.IsHost) parts.Add(Lang.T("host"));
            if (me) parts.Add(Lang.T("you"));
            parts.Add(GameInfo.CharacterName(rec.Character));
            if (!string.IsNullOrEmpty(rec.Status)) parts.Add(Lang.T(rec.Status));
            if (rec.Fps > 0) parts.Add(rec.Fps + " fps");
            if (!p.IsHost && p.RoundTripMs >= 0) parts.Add(p.RoundTripMs + " ms");
            return string.Join(" · ", parts.ToArray());
        }

        /// <summary>The character last picked on the game's character screen.</summary>
        private static int LocalCharacter()
        {
            if (GameInfo.LocalBody != null) return GameInfo.LocalBody.charType;
            try { return Mathf.Clamp(PlayerPrefs.GetInt("lastchar"), 0, 2); }
            catch { return 0; }
        }

        // ------------------------------------------------------------------ facts (terminal style)

        private static void Fact(string tag, string value, Color? color = null)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("[" + tag + "]", _tag, GUILayout.ExpandWidth(false));
            var st = _value;
            if (color.HasValue) { st = new GUIStyle(_value); st.normal.textColor = color.Value; }
            GUILayout.Label(value, UiSkin.Fit(st, value));
            GUILayout.EndHorizontal();
        }

        private static void Facts(CoopRuntime rt, NetSession s)
        {
            var cfg = CoopPlugin.Instance;
            if (s == null)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("[" + Lang.T("NAME") + "]", _tag, GUILayout.ExpandWidth(false));
                string name = Field("name", cfg.PlayerName.Value, 24, false);
                if (name != cfg.PlayerName.Value) cfg.PlayerName.Value = name;
                GUILayout.EndHorizontal();
                Fact(Lang.T("STEAM"), GameInfo.SteamReady ? "\"" + Lang.T("ready") + "\"" : "\"" + Lang.T("not running") + "\"", GameInfo.SteamReady ? (Color?)null : UiSkin.Bad);
                if (rt.Reconnecting)
                {
                    Fact(Lang.T("HOST"), "\"" + Lang.T("lost: reconnecting, your character is saved") + "\"", UiSkin.Amber);
                    if (GUILayout.Button(Lang.T("Stop"), GUILayout.ExpandWidth(false))) rt.CancelReconnect();
                }
                if (!string.IsNullOrEmpty(rt.LastMessage)) Fact(Lang.T("LAST"), "\"" + Lang.Unpack(rt.LastMessage) + "\"", UiSkin.Amber);
            }
            else
            {
                var rules = HostRules.Current;
                var flow = Coop.Get<RunFlow>();
                if (flow != null) Fact(Lang.T("RUN"), "\"" + flow.Describe() + "\"");
                Fact(Lang.T("PLAYERS"), "\"" + s.PeerCount + " / " + rules.MaxPlayers + "\"");
                string respawn = rules.Respawn == Protocol.RespawnRule.Permadeath ? Lang.T("permadeath")
                    : rules.Respawn == Protocol.RespawnRule.Quick ? Lang.F("back after {0} s", (int)LifeSync.QuickDelay) : Lang.T("back on the next layer");
                Fact(Lang.T("DEATH"), "\"" + respawn + "\"");
                Fact(Lang.T("EXIT"), "\"" + (rules.ExitShare == 0 ? Lang.T("the first at an exit takes everyone") : rules.ExitShare >= 100 ? Lang.T("everyone must be at the exit") : Lang.T("half must be at the exit")) + "\"");
                Fact(Lang.T("FRIENDLY_FIRE"), "\"" + (rules.FriendlyFire ? Lang.T("on") : Lang.T("off")) + "\"", rules.FriendlyFire ? (Color?)UiSkin.Amber : null);
                if (s.IsHost && rt.TransportKind == "Steam")
                {
                    string type = cfg.LobbyType.Value == "Public" ? Lang.T("Public") : cfg.LobbyType.Value == "InviteOnly" ? Lang.T("Invite only") : Lang.T("Friends");
                    Fact(Lang.T("JOIN"), "\"" + type + (string.IsNullOrEmpty(cfg.HostPassword.Value) ? "" : " · " + Lang.T("password")) + "\"");
                }
                GUILayout.Space(8);
                var history = Notices.History;
                for (int i = Mathf.Max(0, history.Count - 4); i < history.Count; i++) GUILayout.Label(history[i], _small);
            }
            if (CoopPlugin.FailedPatches.Length > 0)
                GUILayout.Label(Lang.F("Some hooks failed to install ({0}). The game was probably updated; parts of co-op may not sync.", string.Join(", ", CoopPlugin.FailedPatches)), _bad);
            if (GameCheck.Missing.Count > 0)
                GUILayout.Label(Lang.F("The game changed under these features: {0}. They may not work until the mod is updated.", string.Join("; ", GameCheck.Missing.ToArray())), _bad);
            if (!string.IsNullOrEmpty(UpdateCheck.Notice)) GUILayout.Label(UpdateCheck.Notice, _small);
        }

        // ------------------------------------------------------------------ settings and actions

        /// <summary>In a session: the rules (the host's to change), the run's settings, your own options.</summary>
        private static void Settings(CoopRuntime rt, NetSession s)
        {
            if (s.IsHost)
            {
                GUILayout.Label(Lang.T("Rules (change them any time)"), _title);
                RulesEditor(rt, s);
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
            GUILayout.Space(6);
            VoiceRow();
            ComfortRow();
            MilestoneList();
        }

        /// <summary>The bottom bar's own buttons, beside Back.</summary>
        private static void Actions(CoopRuntime rt, NetSession s)
        {
            if (s == null) return;
            var flow = Coop.Get<RunFlow>();
            // The host starts the run as in the game: its own run setup, from the menu.
            if (s.IsHost && GameInfo.OnMainMenu && CoopSaves.PendingContinue == null && PreRunScript.instance != null && PreRunScript.instance.runSettingsScreen != null &&
                GUILayout.Button(Lang.T("Start a run"), GUILayout.MinWidth(160f), GUILayout.ExpandHeight(true)))
            {
                _open = false;
                PreRunScript.instance.runSettingsScreen.SetActive(true);
                try { PlayerCamera.PlayUISound("click"); } catch { /* no sound */ }
            }
            if (s.IsHost && rt.TransportKind == "Steam" && rt.Lobby.InLobby && GUILayout.Button(Lang.T("Invite Steam friends"), GUILayout.MinWidth(160f), GUILayout.ExpandHeight(true))) rt.Lobby.Invite();
            if (s.IsHost && GameInfo.OnMainMenu && CoopSaves.PendingContinue != null && GUILayout.Button(Lang.T("Start the saved run"), GUILayout.MinWidth(160f), GUILayout.ExpandHeight(true))) CoopSaves.StartContinue();
            if (!s.IsHost && flow != null && flow.CanJoinHostRun && GUILayout.Button(Lang.T("Join the host's run"), GUILayout.MinWidth(160f), GUILayout.ExpandHeight(true))) flow.JoinHostRun();
            if (GUILayout.Button(s.IsHost ? Lang.T("End session") : Lang.T("Leave session"), GUILayout.MinWidth(150f), GUILayout.ExpandHeight(true))) rt.Leave();
        }
    }
}
