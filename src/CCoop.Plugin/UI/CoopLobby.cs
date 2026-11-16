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
        private const float AddW = 64f;
        private static Vector2 _lobbyScroll;
        private static GUIStyle _bigName, _tag, _value, _slotNote, _heading, _tabStyle, _arrow, _charName, _charText;

        private enum LobbyTab { Run, Rules, Options }
        private static LobbyTab _lobbyTab = LobbyTab.Run;

        internal static void DevClose() => _open = false;

        /// <summary>Screenshots: scroll the settings side down by <paramref name="y"/>.</summary>
        internal static void DevScroll(float y) => _lobbyScroll = new Vector2(0f, y);

        internal static void DevSelectLobbyTab(int tab)
        {
            _open = true;
            _lobbyTab = (LobbyTab)Mathf.Clamp(tab, 0, 2);
            _lobbyScroll = Vector2.zero;
        }

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
            _tabStyle = new GUIStyle(GUI.skin.label) { fontSize = UiSkin.Big, alignment = TextAnchor.MiddleCenter, wordWrap = false };
            _arrow = new GUIStyle(GUI.skin.label) { fontSize = UiSkin.Big, alignment = TextAnchor.MiddleCenter };
            _charName = new GUIStyle(GUI.skin.label) { fontSize = UiSkin.Big, wordWrap = true };
            _charText = new GUIStyle(GUI.skin.label) { fontSize = UiSkin.Body, wordWrap = true };
        }

        private static void Lobby(CoopRuntime rt)
        {
            LobbyStyles();
            if (_address == null) _address = CoopPlugin.Instance.LastAddress.Value;
            var s = Coop.Session;
            float w = Screen.width / Scale, h = Screen.height / Scale;
            var all = new Rect(0f, 0f, w, h);
            GUI.DrawTexture(all, GameLook.Solid(new Color(0f, 0f, 0f, GameInfo.InRunScene ? 0.96f : 1f)));
            UiMouse.Claim(all);

            // ---- title bar
            GUI.Label(new Rect(0f, 0f, w, Bar), Lang.T("Co-op"), _heading);
            string status = s == null ? (rt.Reconnecting ? Lang.T("Reconnecting...") : Lang.T("Not in a session"))
                : (s.IsHost ? Lang.T("Hosting") : s.State == SessionState.Established ? Lang.T("Connected") : Lang.T("Connecting")) + " · " + rt.TransportKind;
            GUI.Label(new Rect(18f, 0f, w * 0.3f, Bar), status, _tag);
            var ver = new GUIStyle(_tag) { alignment = TextAnchor.MiddleRight };
            GUI.Label(new Rect(w * 0.7f - 18f, 0f, w * 0.3f, Bar), "CasualtiesCoop " + CoopPlugin.Version, ver);
            Line(new Rect(0f, Bar, w, 1f));

            // ---- bottom bar (drawn first: in IMGUI the first control under the mouse takes the click,
            // so nothing scrolled out of sight in the lists above can take it)
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

            // ---- the players
            float slotsH = Mathf.Round((h - Bar * 2f) * 0.46f);
            Slots(rt, s, new Rect(0f, Bar + 1f, w, slotsH - 1f));
            Line(new Rect(0f, Bar + slotsH, w, 1f));

            // ---- facts | settings
            float lowY = Bar + slotsH + 1f, lowH = h - Bar * 2f - slotsH - 1f;
            float leftW = Mathf.Round(w * 0.34f);
            GUILayout.BeginArea(new Rect(18f, lowY + 10f, leftW - 30f, lowH - 16f));
            if (GameInfo.OnMainMenu) YourCharacter(rt, s);
            else Facts(rt, s);
            GUILayout.EndArea();
            Line(new Rect(leftW, lowY, 1f, lowH));
            float rightW = w - leftW - 28f;
            float tabsH = s != null || rt.PasswordPrompt == null ? 44f : 0f;
            if (tabsH > 0f) Tabs(new Rect(leftW + 14f, lowY + 4f, rightW, tabsH), s, rt);
            float viewH = lowH - 12f - tabsH;
            GUILayout.BeginArea(new Rect(leftW + 14f, lowY + 8f + tabsH, rightW, viewH));
            _lobbyScroll = GUILayout.BeginScrollView(_lobbyScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar);
            GameRows.Clip = new Rect(_lobbyScroll.x, _lobbyScroll.y, rightW, viewH);
            if (s == null && rt.PasswordPrompt != null) PasswordPrompt(rt);
            else if (s == null) Offline(rt);
            else Settings(rt, s, rightW - 24f);
            GameRows.Clip = null;
            GUILayout.EndScrollView();
            GUILayout.EndArea();

            KeyboardForFocusedField();
            // Buttons' own hover text, in the game's label.
            if (Event.current.type == EventType.Repaint && !string.IsNullOrEmpty(GUI.tooltip)) GameTooltip.Set(GUI.tooltip);
        }

        private static void Line(Rect r) => GUI.DrawTexture(r, GameLook.Solid(new Color(1f, 1f, 1f, 0.85f)));

        // ------------------------------------------------------------------ player slots

        private static void Slots(CoopRuntime rt, NetSession s, Rect area)
        {
            var cfg = CoopPlugin.Instance;
            var ids = new List<byte>();
            if (s != null) { foreach (var p in s.Peers) ids.Add(p.Id); ids.Sort(); }
            bool canEdit = s == null || s.IsHost;
            int allowed = canEdit ? cfg.MaxPlayers.Value : HostRules.Current.MaxPlayers;
            int places = Mathf.Clamp(Mathf.Max(allowed, ids.Count), 2, NetSession.MaxPeers);
            int floor = Mathf.Max(2, ids.Count);

            // One more place: a thin column at the end.
            bool canAdd = canEdit && places < NetSession.MaxPeers;
            if (canAdd)
            {
                var add = new Rect(area.xMax - AddW, area.y, AddW, area.height);
                Line(new Rect(add.x, add.y, 1f, add.height));
                bool hover = add.Contains(Event.current.mousePosition);
                if (Event.current.type == EventType.Repaint)
                {
                    var old = GUI.color;
                    GUI.color = hover ? Color.white : new Color(1f, 1f, 1f, 0.45f);
                    GUI.Label(add, "+", _heading);
                    GUI.color = old;
                }
                if (GUI.Button(add, new GUIContent("", Lang.T("Add a place")), GUIStyle.none)) { cfg.MaxPlayers.Value = places + 1; Click(); }
                area.width -= AddW;
            }

            float colW = area.width / places;
            for (int i = 0; i < places; i++)
            {
                var r = new Rect(area.x + i * colW, area.y, colW, area.height);
                if (i > 0) Line(new Rect(r.x, r.y, 1f, r.height));
                bool canClose = canEdit && places > floor && i == places - 1;
                if (s == null)
                {
                    if (i == 0) Slot(r, CharacterPick.Current, GameInfo.PlayerName, Lang.T("you"), true, null, GameInfo.OnMainMenu);
                    else Slot(r, i % 3, null, Lang.T("Open"), false, canClose ? () => CloseButton(cfg, places) : (System.Action)null);
                }
                else if (i < ids.Count && s.TryGetPeer(ids[i], out var peer))
                {
                    var rec = Coop.Player(ids[i]);
                    bool me = peer.Id == s.LocalId;
                    Slot(r, me && GameInfo.OnMainMenu ? CharacterPick.Current : rec.Character, peer.Name, SlotNote(s, peer, rec, me), true, () =>
                    {
                        GUILayout.BeginHorizontal();
                        GUILayout.FlexibleSpace();
                        RowButtons(rt, s, peer, rec, me, Coop.Get<VoiceSync>());
                        GUILayout.FlexibleSpace();
                        GUILayout.EndHorizontal();
                    }, me && GameInfo.OnMainMenu);
                }
                else
                {
                    bool canInvite = s.IsHost && rt.TransportKind == "Steam" && rt.Lobby.InLobby;
                    Slot(r, i % 3, null, Lang.T("Open"), false, canInvite || canClose ? () =>
                    {
                        GUILayout.BeginHorizontal();
                        GUILayout.FlexibleSpace();
                        if (canInvite && GUILayout.Button(Lang.T("Invite"), GUILayout.MinWidth(90f))) rt.Lobby.Invite();
                        if (canClose && GUILayout.Button(new GUIContent(Lang.T("Close"), Lang.T("One place fewer")), GUILayout.MinWidth(70f))) cfg.MaxPlayers.Value = places - 1;
                        GUILayout.FlexibleSpace();
                        GUILayout.EndHorizontal();
                    } : (System.Action)null);
                }
            }
        }

        private static void CloseButton(CoopPlugin cfg, int places)
        {
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(new GUIContent(Lang.T("Close"), Lang.T("One place fewer")), GUILayout.MinWidth(70f))) cfg.MaxPlayers.Value = places - 1;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        private static void Click()
        {
            try { PlayerCamera.PlayUISound("click"); } catch { /* no sound */ }
        }

        /// <summary>One place: the character's portrait (or a grey outline when open), the name, a note, buttons; arrows to change your own character.</summary>
        private static void Slot(Rect r, int character, string name, string note, bool taken, System.Action buttons, bool pick = false)
        {
            float textH = 78f + (buttons != null ? 34f : 0f);
            var pic = new Rect(r.x + 12f, r.y + 14f, r.width - 24f, r.height - textH - 18f);
            if (pick)
            {
                // Your own: arrows at both sides, as the game steps through its characters.
                var left = new Rect(r.x + 6f, pic.y, 40f, pic.height);
                var right = new Rect(r.xMax - 46f, pic.y, 40f, pic.height);
                if (Event.current.type == EventType.Repaint)
                {
                    GUI.Label(left, "<", _arrow);
                    GUI.Label(right, ">", _arrow);
                }
                if (GUI.Button(left, new GUIContent("", Lang.T("Your character")), GUIStyle.none)) CharacterPick.Step(-1);
                if (GUI.Button(right, new GUIContent("", Lang.T("Your character")), GUIStyle.none)) CharacterPick.Step(1);
                pic = new Rect(pic.x + 34f, pic.y, pic.width - 68f, pic.height);
            }
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

        /// <summary>On the menu: your character, as the game's character screen describes it.</summary>
        private static Vector2 _charScroll;

        private static void YourCharacter(CoopRuntime rt, NetSession s)
        {
            CharacterPick.Ensure();
            int c = CharacterPick.Current;
            // The character's sheet scrolls when the panel is short (a big UI scale, the Deck):
            // what's under it (your name, messages) always stays in view.
            _charScroll = GUILayout.BeginScrollView(_charScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUILayout.ExpandHeight(true));
            GUILayout.Label(CharacterPick.Name(c), UiSkin.Fit(_charName, CharacterPick.Name(c)));
            var st = CharacterPick.Stats(c);
            if (st != null && st.Length >= 3) GUILayout.Label("[STR] " + st[0] + "   [RES] " + st[1] + "   [INT] " + st[2], _tag);
            GUILayout.Space(4);
            string notes = CharacterPick.Text(c, "desc");
            if (notes.Length > 0) GUILayout.Label(notes, UiSkin.Fit(_charText, notes));
            string quirks = CharacterPick.Text(c, "quirks");
            if (quirks.Length > 0) { GUILayout.Space(4); GUILayout.Label(quirks, UiSkin.Fit(_charText, quirks)); }
            GUILayout.EndScrollView();
            GUILayout.Space(6);
            if (s == null)
            {
                // Your name, for the others (set before hosting or joining).
                var cfg = CoopPlugin.Instance;
                GUILayout.BeginHorizontal();
                GUILayout.Label("[" + Lang.T("NAME") + "]", _tag, GUILayout.ExpandWidth(false));
                string name = Field("name", cfg.PlayerName.Value, 24, false);
                if (name != cfg.PlayerName.Value) cfg.PlayerName.Value = name;
                GUILayout.EndHorizontal();
                if (string.IsNullOrEmpty(cfg.PlayerName.Value)) GUILayout.Label(Lang.T("Empty: your Steam name."), _small);
                GUILayout.Space(6);
            }
            Problems();
            var flow = Coop.Get<RunFlow>();
            if (s != null && flow != null) GUILayout.Label(flow.Describe(), _small);
            if (s == null && !string.IsNullOrEmpty(rt.LastMessage)) GUILayout.Label(Lang.Unpack(rt.LastMessage), _small);
            var history = Notices.History;
            for (int i = Mathf.Max(0, history.Count - 2); i < history.Count; i++) GUILayout.Label(history[i], _small);
        }

        /// <summary>The tabs over the right-hand side: in a session its pages, out of one Host / Find games / LAN.</summary>
        private static void Tabs(Rect r, NetSession s, CoopRuntime rt)
        {
            string[] names = s != null
                ? new[] { Lang.T("Run settings"), Lang.T("Co-op rules"), Lang.T("Options") }
                : new[] { Lang.T("Host"), Lang.T("Find games"), Lang.T("LAN / IP") };
            int current = s != null ? (int)_lobbyTab : (int)_tab;
            float tw = r.width / names.Length;
            for (int i = 0; i < names.Length; i++)
            {
                var t = new Rect(r.x + i * tw, r.y, tw, r.height);
                bool on = current == i;
                if (Event.current.type == EventType.Repaint)
                {
                    var old = GUI.color;
                    GUI.color = on ? Color.white : t.Contains(Event.current.mousePosition) ? new Color(1f, 1f, 1f, 0.8f) : new Color(1f, 1f, 1f, 0.5f);
                    GUI.Label(t, names[i], UiSkin.Fit(_tabStyle, names[i]));
                    if (on) GUI.DrawTexture(new Rect(t.x + 16f, t.yMax - 3f, t.width - 32f, 2f), Texture2D.whiteTexture);
                    GUI.color = old;
                }
                if (GUI.Button(t, GUIContent.none, GUIStyle.none) && !on)
                {
                    if (s != null) _lobbyTab = (LobbyTab)i;
                    else
                    {
                        _tab = (Tab)i;
                        if (_tab == Tab.Find && !_browsedOnce) Refresh(rt);
                    }
                    _lobbyScroll = Vector2.zero;
                    Click();
                }
            }
        }

        /// <summary>The host's rules, read only (for everyone but the host).</summary>
        private static void RulesReadout()
        {
            var r = HostRules.Current;
            bool was = GUI.enabled;
            GUI.enabled = false;
            GameRows.Header(Lang.T("Session"));
            GameRows.Choice("ro.difficulty", Lang.T("Difficulty"), 0, new[] { Lang.T(r.PresetName) });
            GameRows.Choice("ro.players", Lang.T("Players"), 0, new[] { r.MaxPlayers.ToString() });
            GameRows.Header(Lang.T("Death and descent"));
            GameRows.Choice("ro.respawn", Lang.T("When you die"), (int)r.Respawn, new[] { Lang.T("Next layer"), Lang.T("Permadeath"), Lang.T("Quick") });
            GameRows.Choice("ro.descend", Lang.T("Descend when"), r.ExitShare <= 0 ? 0 : r.ExitShare >= 100 ? 2 : 1, new[] { Lang.T("First arrives"), Lang.T("Half are there"), Lang.T("All are there") });
            GameRows.Header(Lang.T("Together"));
            GameRows.Toggle(Lang.T("Teammate arrows"), r.Arrows);
            GameRows.Toggle(Lang.T("Name tags"), r.NameTags);
            GameRows.Toggle(Lang.T("Spawn apart"), r.SpawnApart);
            GameRows.Toggle(Lang.T("Friendly fire"), r.FriendlyFire);
            GameRows.Toggle(Lang.T("Radios"), r.Radios);
            GameRows.Toggle(Lang.T("Sleep"), r.Sleep);
            GameRows.Slider(Lang.T("Voices carry"), r.VoicePercent, 25f, 100f, r.VoicePercent + "%");
            GameRows.Header(Lang.T("Balance"));
            GameRows.Choice("ro.loot", Lang.T("Loot"), Mathf.Clamp(r.LootScale, 0, 1), Lang.All(HostRules.LootNames));
            GameRows.Choice("ro.enemies", Lang.T("Enemies"), Mathf.Clamp(r.DangerScale, 0, 2), Lang.All(HostRules.DangerNames));
            GUI.enabled = was;
        }

        /// <summary>Your own options: these never change anything for anyone else.</summary>
        private static void OptionsTab()
        {
            var cfg = CoopPlugin.Instance;
            if (VoiceSync.Available)
            {
                GameRows.Header(Lang.T("Voice"));
                int cur = Mathf.Max(0, System.Array.IndexOf(VoiceModes, cfg.VoiceMode.Value));
                int pick = GameRows.Choice("voice", Lang.T("Voice chat"), cur, new[] { Lang.F("Hold {0}", cfg.VoiceKey.Value), Lang.T("Open mic"), Lang.T("Off") });
                if (pick != cur) cfg.VoiceMode.Value = VoiceModes[pick];
                MeasuringMic = cfg.VoiceMode.Value == "Open";
                if (MeasuringMic)
                {
                    cfg.VoiceGate.Value = GameRows.Slider(Lang.T("Send above"), cfg.VoiceGate.Value, 0f, 0.3f,
                        cfg.VoiceGate.Value <= 0.001f ? Lang.T("all") : Mathf.RoundToInt(cfg.VoiceGate.Value / 0.3f * 100f) + "%",
                        Lang.T("Open mic sends only what's louder than this. The bar under it is your mic now."));
                    var bar = GUILayoutUtility.GetRect(100f, 6f, GUILayout.ExpandWidth(true));
                    float lvl = Mathf.Clamp01(VoiceSync.MicLevel / 0.3f);
                    var old = GUI.color;
                    GUI.color = VoiceSync.MicLevel >= cfg.VoiceGate.Value ? new Color(0.5f, 1f, 0.5f) : new Color(0.7f, 0.7f, 0.7f);
                    GUI.DrawTexture(new Rect(bar.x + 10f, bar.y, (bar.width - 100f) * lvl, bar.height), Texture2D.whiteTexture);
                    GUI.color = old;
                }
            }
            GameRows.Header(Lang.T("Display"));
            if (HostRules.Current.Arrows)
                cfg.ShowArrows.Value = GameRows.Toggle(Lang.T("Teammate arrows"), cfg.ShowArrows.Value, Lang.T("Arrows at the screen edge to teammates out of sight."));
            cfg.ReduceFlashes.Value = GameRows.Toggle(Lang.T("Reduce flashes and shaking"), cfg.ReduceFlashes.Value);
            cfg.ColorBlindColors.Value = GameRows.Toggle(Lang.T("Colour-blind safe colours"), cfg.ColorBlindColors.Value);
            cfg.Tips.Value = GameRows.Toggle(Lang.T("Key hints when a run starts"), cfg.Tips.Value,
                Lang.T("The first run after you start the game shows the co-op keys on the game's alert line."));
            Controls();
            MilestoneList();
        }

        // ------------------------------------------------------------------ controls

        /// <summary>The co-op keys, to change like the game's own (click, then press the key), and the ones that can't be changed.</summary>
        private static void Controls()
        {
            var cfg = CoopPlugin.Instance;
            GameRows.Header(Lang.T("Controls"));
            KeyRow("k.menu", Lang.T("Co-op menu"), cfg.MenuKey, null);
            KeyRow("k.chat", Lang.T("Chat"), cfg.ChatKey, HostRules.Current.Radios
                ? Lang.T("Opens the chat box. Tab in it switches between saying it out loud and the radio.")
                : Lang.T("Opens the chat box."));
            if (VoiceSync.Available && cfg.VoiceMode.Value == "PushToTalk") KeyRow("k.voice", Lang.T("Push to talk"), cfg.VoiceKey, null);
            KeyRow("k.gesture", Lang.T("Gesture wheel"), cfg.GestureKey,
                Lang.T("Hold it, move the mouse onto a gesture and let go. A quick tap leaves the wheel open for a click."));
            KeyRow("k.catch", Lang.T("Catch"), cfg.CatchKey,
                Lang.T("Hold it under a teammate who's falling: you soften their landing, but a long fall hurts your arms."));
            GameRows.Info(Lang.T("Ping"), UiSkin.KeyName(KeyCode.Mouse2), Lang.T("Marks the spot under the mouse for everyone."));
            GameRows.Info(Lang.T("Teammate's menu"), UiSkin.KeyName(KeyBindsBlockPatch.Raw("iteminteract")),
                Lang.T("Point at a teammate and press the game's interact key: carry, drag, give, ask for, treat."));
        }

        private static void KeyRow(string id, string label, BepInEx.Configuration.ConfigEntry<KeyCode> entry, string tip)
        {
            var key = GameRows.Key(id, label, entry.Value, tip);
            if (key != entry.Value) entry.Value = key;
            string clash = Clash(entry, key);
            if (clash == null && DevClash != null && DevClash.Value.Item1 == id) clash = DevClash.Value.Item2;
            if (clash != null) GameRows.Note(clash, UiSkin.Amber);
        }

        /// <summary>Screenshots: a clash note under one key row, without touching the player's keys.</summary>
        internal static (string, string)? DevClash;

        /// <summary>What else answers to <paramref name="key"/>: another co-op key, or one of the game's.</summary>
        private static string Clash(BepInEx.Configuration.ConfigEntry<KeyCode> entry, KeyCode key)
        {
            if (key == KeyCode.None) return null;
            var cfg = CoopPlugin.Instance;
            var mine = new (BepInEx.Configuration.ConfigEntry<KeyCode>, string)[]
            {
                (cfg.MenuKey, Lang.T("Co-op menu")), (cfg.ChatKey, Lang.T("Chat")), (cfg.GestureKey, Lang.T("Gesture wheel")),
                (cfg.CatchKey, Lang.T("Catch")), (cfg.VoiceMode.Value == "PushToTalk" ? cfg.VoiceKey : null, Lang.T("Push to talk")),
            };
            foreach (var (other, name) in mine)
                if (other != null && other != entry && other.Value == key) return Lang.F("{0} is also the key for: {1}.", UiSkin.KeyName(key), name);
            string game = Chat.GameAction(key);
            return game != null ? Lang.F("The game uses {0} too: {1}.", UiSkin.KeyName(key), game) : null;
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
            Problems();
        }

        /// <summary>What's wrong with this install (shown wherever the lobby is).</summary>
        private static void Problems()
        {
            if (GameCheck.OtherCopies.Count > 0)
                GUILayout.Label(Lang.F("Another copy of the co-op mod is installed ({0}) and will clash with this one. Close the game and run the installer from this download again: it removes old copies.", string.Join(", ", GameCheck.OtherCopies.ToArray())), _bad);
            if (CoopPlugin.FailedPatches.Length > 0)
                GUILayout.Label(Lang.F("Some hooks failed to install ({0}). The game was probably updated; parts of co-op may not sync.", string.Join(", ", CoopPlugin.FailedPatches)), _bad);
            if (GameCheck.Missing.Count > 0)
                GUILayout.Label(Lang.F("The game changed under these features: {0}. They may not work until the mod is updated.", string.Join("; ", GameCheck.Missing.ToArray())), _bad);
            if (!string.IsNullOrEmpty(UpdateCheck.Notice)) GUILayout.Label(UpdateCheck.Notice, _small);
        }

        // ------------------------------------------------------------------ settings and actions

        /// <summary>In a session, by tab: the run's settings, the co-op rules (the host's to change), your own options.</summary>
        private static void Settings(CoopRuntime rt, NetSession s, float width)
        {
            switch (_lobbyTab)
            {
                case LobbyTab.Run:
                    if (GameInfo.InRunScene) RunSetup.Draw(width, false, WorldGeneration.runSettings);
                    else if (s.IsHost)
                    {
                        RunSetup.Draw(width, PreRunScript.instance != null, PreRunScript.instance != null ? PreRunScript.instance.runSettings : null);
                        GUILayout.Space(8);
                        RunPresets.Section();
                    }
                    else RunSetup.Draw(width, false, Coop.Get<RunFlow>()?.HostSettings);
                    break;
                case LobbyTab.Rules:
                    if (s.IsHost) RulesEditor(rt, s);
                    else RulesReadout();
                    break;
                default:
                    OptionsTab();
                    break;
            }
        }

        /// <summary>A client in a run goes back to the menu as it leaves (it would be left alone in the host's layer).</summary>
        private static void LeaveSession(CoopRuntime rt, NetSession s)
        {
            if (!s.IsHost && GameInfo.InRunScene) { _open = false; LeaveRun.Leave(); }
            else rt.Leave();
        }

        /// <summary>The bottom bar's own buttons, beside Back.</summary>
        private static void Actions(CoopRuntime rt, NetSession s)
        {
            if (s == null) { OfflineActions(rt); return; }
            var flow = Coop.Get<RunFlow>();
            // The host starts the run as in the game: its own run setup, from the menu.
            // The host starts the run from here, with the settings and characters picked here.
            if (s.IsHost && GameInfo.OnMainMenu && CoopSaves.PendingContinue == null && PreRunScript.instance != null && PreRunScript.instance.runSettingsScreen != null &&
                GUILayout.Button(Lang.T("Start the run"), GUILayout.MinWidth(160f), GUILayout.ExpandHeight(true)))
            {
                _open = false;
                Click();
                try
                {
                    PreRunScript.instance.runSettingsScreen.SetActive(true);  // the game's own warnings show there
                    PreRunScript.instance.StartRun();
                }
                catch (System.Exception e) { Log.Warn("[ui] start run: " + e.Message); }
            }
            if (s.IsHost && rt.TransportKind == "Steam" && rt.Lobby.InLobby && GUILayout.Button(Lang.T("Invite Steam friends"), GUILayout.MinWidth(160f), GUILayout.ExpandHeight(true))) rt.Lobby.Invite();
            if (s.IsHost && GameInfo.OnMainMenu && CoopSaves.PendingContinue != null && GUILayout.Button(Lang.T("Start the saved run"), GUILayout.MinWidth(160f), GUILayout.ExpandHeight(true))) CoopSaves.StartContinue();
            if (!s.IsHost && flow != null && flow.CanJoinHostRun && GUILayout.Button(Lang.T("Join the host's run"), GUILayout.MinWidth(160f), GUILayout.ExpandHeight(true))) flow.JoinHostRun();
            if (GUILayout.Button(s.IsHost ? Lang.T("End session") : Lang.T("Leave session"), GUILayout.MinWidth(150f), GUILayout.ExpandHeight(true))) LeaveSession(rt, s);
        }
    }
}
