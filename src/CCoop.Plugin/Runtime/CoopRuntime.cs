using System;
using System.Collections.Generic;
using CCoop.Net.Session;
using CCoop.Net.Transport;
using CCoop.Plugin.Dev;
using CCoop.Plugin.Net;
using CCoop.Plugin.Protocol;
using CCoop.Plugin.Sync;
using CCoop.Plugin.UI;
using UnityEngine;

namespace CCoop.Plugin.Runtime
{
    /// <summary>
    /// The persistent driver. Owns the session and the Steam lobby, pumps the network
    /// on the main thread, and fans lifecycle events out to the sync modules.
    /// </summary>
    public sealed class CoopRuntime : MonoBehaviour
    {
        private readonly List<CoopModule> _modules = new List<CoopModule>();
        private readonly Dictionary<Type, CoopModule> _byType = new Dictionary<Type, CoopModule>();

        internal SteamLobby Lobby { get; } = new SteamLobby();
        internal string LastMessage = "";
        internal string TransportKind = "";
        internal bool Connecting;
        private ulong _pendingLaunchLobby;
        private ulong _pendingJoinLobby;
        private string _joinPassword = "";
        private LobbyInfo _joiningLobby;

        // ---- reconnect after a dropped connection (M10)
        private sealed class ReconnectTarget
        {
            public string Address;      // LAN/IP, or null for Steam
            public ulong Lobby;         // Steam lobby, or 0
            public string Password = "";
            public float Until, NextTry;
            public int Attempts;
        }

        private ReconnectTarget _reconnect;
        private string _lastLanAddress;

        /// <summary>Seconds a dropped client keeps trying to get back in.</summary>
        internal const float ReconnectWindow = 60f;

        internal bool Reconnecting => _reconnect != null;
        private float _nextAdvert;

        /// <summary>A locked lobby the player is being asked the password for.</summary>
        internal LobbyInfo PasswordPrompt;

        private void Awake()
        {
            Coop.Runtime = this;
            Add(new TrafficLog());
            Add(new RunFlow());
            Add(new LayerSync());
            Add(new PlayerSync());
            Add(new LifeSync());
            Add(new RulesSync());
            Add(new VoiceSync());
            Add(new Chat());
            Add(new ExitVote());
            Add(new PingSync());
            Add(new CreatureHunt());
            Add(new TeamSense());
            Add(new VitalsSync());
            Add(new Medic());
            Add(new GiveSync());
            Add(new CarrySync());
            Add(new LootSync());
            Add(new SpawnSpread());
            Add(new ArrivalGate());
            Add(new TeammateArrows());
            Add(new GestureSync());
            Add(new TeamMotion());
            Add(new CoopSaves());
            Add(new TeammatePanel());
            Add(new TerrainSync());
            Add(new EntitySync());
            Add(new ItemSync());
            Add(new CreatureSync());
            Add(new ClockSync());
            Add(new PropSync());
            Add(new WorldEvents());
            Add(new ShotSync());
            Add(new TraderSync());
            Add(new WorldCheck());
            Add(new Milestones());
            Add(new ItemDecay());
            Add(new CraftSync());
            Add(new FluidSync());
            Add(new FriendlyFireSync());
            Add(new TeamReceipt());
            Add(new ExplosionSync());
            Add(new SteamMoments());
            Add(new DevHarness());

            StartCoroutine(UpdateCheck.Run());
            _pendingLaunchLobby = SteamLobby.LaunchLobby();
            if (_pendingLaunchLobby != 0) Log.Info("[steam] launched to join lobby " + _pendingLaunchLobby);
            Lobby.JoinRequested += id =>
            {
                Log.Info("[steam] join requested for lobby " + id);
                _pendingJoinLobby = id;
            };
        }

        private void Add(CoopModule m)
        {
            _modules.Add(m);
            _byType[m.GetType()] = m;
        }

        internal T Get<T>() where T : CoopModule => _byType.TryGetValue(typeof(T), out var m) ? (T)m : null;

        // ------------------------------------------------------------------ starting

        /// <summary>Host: the live session config, so password and player cap can change mid-session.</summary>
        internal SessionConfig HostConfig { get; private set; }

        private SessionConfig Config(bool host)
        {
            var c = NewConfig(host);
            HostConfig = host ? c : null;
            return c;
        }

        private SessionConfig NewConfig(bool host) => new SessionConfig
        {
            ModVersion = CoopPlugin.Version,
            GameBuild = GameInfo.GameBuild,
            PlayerName = GameInfo.PlayerName,
            MaxPlayers = CoopPlugin.Ready ? CoopPlugin.Instance.MaxPlayers.Value : 4,
            Password = host ? CoopPlugin.Instance.SessionPassword : (_joinPassword ?? ""),
            ExtraAdmission = host ? Admission : (Func<Hello, string, string>)null,
        };

        /// <summary>Host: last word on who gets in, with the transport-verified address.</summary>
        private static string Admission(Hello hello, string address)
        {
            if (BanList.IsBanned(address)) return Lang.N("You are banned from this host's sessions.");
            return null;
        }

        /// <param name="lobbyType">Overrides the configured visibility without saving it (dev harness).</param>
        internal void HostSteam(string lobbyType = null)
        {
            if (Coop.Session != null || Connecting) return;
            if (!GameInfo.SteamReady) { LastMessage = Lang.N("Steam is not running. Start the game from Steam, or use LAN/IP."); return; }
            Connecting = true;
            LastMessage = Lang.N("Creating Steam lobby...");
            int max = CoopPlugin.Instance.MaxPlayers.Value;
            bool locked = !string.IsNullOrEmpty(CoopPlugin.Instance.SessionPassword);
            Lobby.Create(lobbyType ?? CoopPlugin.Instance.LobbyType.Value, max, locked, (ok, error) =>
            {
                Connecting = false;
                if (!ok) { LastMessage = error; Log.Warn("[steam] " + error); return; }
                try
                {
                    var t = new SteamTransport();
                    t.StartHost(NetSession.MaxPeers + 1);
                    Begin(NetSession.CreateHost(t, CoopProtocol.Build(), Config(true)), "Steam");
                    _nextAdvert = 0;
                    LastMessage = Lang.N("Hosting. Invite friends from the co-op menu or the Steam overlay.");
                }
                catch (Exception e)
                {
                    Lobby.Leave();
                    LastMessage = Lang.Pack("Could not host: {0}", e.Message);
                    Log.Error(Lang.Plain(LastMessage));
                }
            });
        }

        internal void HostLan()
        {
            if (Coop.Session != null || Connecting) return;
            try
            {
                var t = new LiteNetTransport();
                int port = CoopPlugin.Instance.Port.Value;
                t.StartHost(port, NetSession.MaxPeers + 1);
                Begin(NetSession.CreateHost(t, CoopProtocol.Build(), Config(true)), "LAN/IP");
                LastMessage = Lang.Pack("Hosting on UDP port {0}. Others join with your IP address.", port);
            }
            catch (Exception e)
            {
                LastMessage = Lang.Pack("Could not host: {0}", e.Message);
                Log.Error(Lang.Plain(LastMessage));
            }
        }

        internal void JoinLan(string address, string password = "")
        {
            if (Coop.Session != null || Connecting) return;
            _joinPassword = password ?? "";
            _joiningLobby = null;
            string host = (address ?? "").Trim();
            int port = CoopPlugin.Instance.Port.Value;
            int colon = host.LastIndexOf(':');
            if (colon > 0 && int.TryParse(host.Substring(colon + 1), out var p)) { port = p; host = host.Substring(0, colon); }
            if (host.Length == 0) { LastMessage = Lang.N("Enter the host's address."); return; }
            _lastLanAddress = address.Trim();
            try
            {
                CoopPlugin.Instance.LastAddress.Value = address.Trim();
                var t = new LiteNetTransport();
                t.StartClient(host, port);
                Begin(NetSession.CreateClient(t, CoopProtocol.Build(), Config(false)), "LAN/IP");
                LastMessage = Lang.Pack("Connecting to {0}:{1}...", host, port);
            }
            catch (Exception e)
            {
                LastMessage = Lang.Pack("Could not join: {0}", e.Message);
                Log.Error(Lang.Plain(LastMessage));
            }
        }

        /// <summary>
        /// Joins through a Steam lobby. A locked lobby without a password turns into a
        /// password prompt in the co-op window instead of a doomed connection attempt.
        /// </summary>
        internal void JoinSteamLobby(ulong lobby, string password = null)
        {
            if (Coop.Session != null || Connecting) return;
            Connecting = true;
            PasswordPrompt = null;
            LastMessage = Lang.N("Joining Steam lobby...");
            Lobby.Join(lobby, (ok, info, error) =>
            {
                Connecting = false;
                if (!ok) { LastMessage = Lang.Pack("Could not join: {0}", error); Log.Warn("[steam] could not join: " + error); return; }
                ulong owner = info.HostId;
                if (owner == GameInfo.SteamId) { Lobby.Leave(); LastMessage = Lang.N("That is your own lobby."); return; }
                if (info.Locked && password == null)
                {
                    Lobby.Leave();
                    PasswordPrompt = info;
                    LastMessage = Lang.Pack("{0}'s session needs a password.", info.Host);
                    CoopWindow.Show();
                    return;
                }
                _joinPassword = password ?? "";
                _joiningLobby = info;
                try
                {
                    var t = new SteamTransport();
                    t.Connect(owner);
                    Begin(NetSession.CreateClient(t, CoopProtocol.Build(), Config(false)), "Steam");
                    LastMessage = Lang.N("Connecting to the host...");
                }
                catch (Exception e)
                {
                    Lobby.Leave();
                    LastMessage = Lang.Pack("Could not join: {0}", e.Message);
                    Log.Error(Lang.Plain(LastMessage));
                }
            });
        }

        private void Begin(NetSession session, string kind)
        {
            Coop.Reset();
            Coop.Session = session;
            TransportKind = kind;
            session.Log += s => Log.Info("[net] " + s);
            session.Established += OnEstablished;
            session.Ended += OnEnded;
            session.PeerJoinedEvent += OnPeerJoined;
            session.PeerLeftEvent += OnPeerLeft;

            foreach (var m in _modules) Safe(() => m.Attach(session), m, "attach");
            try { CCoop.CCoopApi.Attach(session); }
            catch (Exception e) { Log.Warn("[api] could not attach mod messages: " + e.Message); }

            if (session.IsHost)
            {
                var me = Coop.Player(session.LocalId);
                me.Name = session.LocalPeer?.Name ?? GameInfo.PlayerName;
                OnEstablished();
            }
            Log.Info("[session] " + (session.IsHost ? "hosting" : "joining") + " over " + kind);
        }

        private void OnEstablished()
        {
            var s = Coop.Session;
            if (s == null) return;
            if (_reconnect != null)
            {
                Log.Info("[session] reconnected after " + _reconnect.Attempts + " attempt(s)");
                Notice(Lang.T("Reconnected"));
                _reconnect = null;
            }
            var me = Coop.Player(s.LocalId);
            me.Name = s.LocalPeer?.Name ?? GameInfo.PlayerName;
            if (!s.IsHost) LastMessage = Lang.Pack("Connected as {0}.", me.Name);
            Log.Info("[session] established; local peer " + s.LocalId);
        }

        private void OnPeerJoined(PeerInfo p)
        {
            var rec = Coop.Player(p.Id);
            rec.Name = p.Name;
            Log.Info("[session] " + p.Name + " joined");
            Notice(Lang.F("{0} joined", p.Name));
            foreach (var m in _modules) Safe(() => m.OnPeerJoined(p), m, "join");
        }

        private void OnPeerLeft(PeerInfo p, string reason)
        {
            Log.Info("[session] " + p.Name + " left (" + reason + ")");
            Notice(Lang.F("{0} left", p.Name));
            foreach (var m in _modules) Safe(() => m.OnPeerLeft(p), m, "leave");
            Coop.Players.Remove(p.Id);
        }

        private void OnEnded(string reason)
        {
            Log.Info("[session] ended: " + reason);
            LastMessage = reason;
            bool wrongPassword = reason != null && reason.IndexOf("Wrong password", StringComparison.OrdinalIgnoreCase) >= 0;
            var retry = _joiningLobby;
            // A client that lost the host (not kicked, not the host closing on purpose)
            // tries to get back in: its character is saved below, it goes back to the
            // menu, and the normal join brings it back into the run where it was.
            // The host ending the session closes the connection; a crash or a network
            // failure times out instead. Only the latter is worth waiting for.
            bool hostClosed = reason != null && reason.IndexOf("closed by the other side", StringComparison.OrdinalIgnoreCase) >= 0;
            bool dropped = Coop.Session != null && !Coop.Session.IsHost && reason != null && !hostClosed &&
                           reason.StartsWith("lost connection to the host", StringComparison.OrdinalIgnoreCase);
            ReconnectTarget target = null;
            if (dropped && (_lastLanAddress != null || retry != null))
                target = new ReconnectTarget
                {
                    Address = TransportKind == "LAN/IP" ? _lastLanAddress : null,
                    Lobby = TransportKind == "Steam" && retry != null ? retry.Id : 0,
                    Password = _joinPassword ?? "",
                    Until = Time.unscaledTime + ReconnectWindow,
                    NextTry = Time.unscaledTime + 3f,
                };
            bool inRun = GameInfo.InRunScene;
            bool wasClient = Coop.Session != null && !Coop.Session.IsHost;
            Teardown();
            // The host ended the session mid-run: anyone can carry the run on as the new host.
            if (inRun && wasClient && hostClosed)
            {
                Notice(Lang.T("The host ended the session"));
                CoopSaves.OfferToCarryOn();
                RunFlow.ReturnToMenu();  // the character was saved in Teardown
            }
            if (target != null && (target.Address != null || target.Lobby != 0))
            {
                _reconnect = target;
                Notice(Lang.T("Lost the host. Reconnecting..."));
                Log.Info("[session] lost the host; reconnecting for " + (int)ReconnectWindow + " s");
                if (inRun) RunFlow.ReturnToMenu();
            }
            if (wrongPassword && retry != null)
            {
                PasswordPrompt = retry;
                CoopWindow.Show();
            }
        }

        /// <summary>Host: bans the player's verified Steam id or IP, then removes them.</summary>
        internal void Ban(byte peer)
        {
            var s = Coop.Session;
            if (s == null || !s.IsHost || !s.TryGetPeer(peer, out var p)) return;
            if (string.IsNullOrEmpty(p.Address)) { Notice(Lang.F("Cannot ban {0}: no verified address.", p.Name)); return; }
            BanList.Ban(p.Address, p.Name);
            s.Kick(peer, Lang.N("Banned by the host."));
            Notice(Lang.F("{0} was banned", p.Name));
        }

        /// <summary>The session layer's refusals, English on the wire, translated where shown (they're listed here for the translation template).</summary>
        private static readonly string[] SessionReasons =
        {
            Lang.N("Wrong password."), Lang.N("The session is full."), Lang.N("left the session"),
            Lang.N("Your game version differs from the host's. Both players must be on the same Steam build."),
        };

        internal void Leave()
        {
            if (Coop.Session == null) return;
            Coop.Session.Leave();
        }

        private bool _tearingDown;

        private void Teardown()
        {
            if (_tearingDown) return;
            _tearingDown = true;
            try
            {
                if (Coop.WorldLive)
                {
                    // Before the layer goes offline: the save needs a live layer.
                    try { CoopSaves.SaveNow("leaving the session"); }
                    catch (Exception e) { Log.Warn("[save] " + e.Message); }
                    Coop.SetWorldLive(false);
                }
                foreach (var m in _modules) Safe(() => m.Detach(), m, "detach");
                Lobby.Leave();
                WorldNet.Clear();
                Coop.Reset();
                TransportKind = "";
                _joiningLobby = null;
            }
            finally
            {
                _tearingDown = false;
            }
        }

        internal void NotifyWorld(bool live)
        {
            foreach (var m in _modules)
            {
                if (live) Safe(() => m.OnWorldLive(), m, "world-live");
                else Safe(() => m.OnWorldLost(), m, "world-lost");
            }
        }

        // ------------------------------------------------------------------ loop

        private void Update()
        {
            try
            {
                EnsureGuiHost();
                Lobby.EnsureInitialized();
                HandlePendingJoins();
                TickReconnect();
                CoopWindow.HandleHotkeys(this);
                Get<DevHarness>()?.Pump();

                var s = Coop.Session;
                if (s == null) return;
                s.Poll();
                if (Coop.Session == null) return;  // ended during poll
                foreach (var m in _modules) Safe(m.Tick, m, "tick");
                UpdateAdvert(s);
            }
            catch (Exception e)
            {
                Log.Error("runtime: " + e);
            }
        }

        private void TickReconnect()
        {
            var r = _reconnect;
            if (r == null || Coop.Session != null || Connecting) return;
            float now = Time.unscaledTime;
            if (now > r.Until)
            {
                _reconnect = null;
                Notice(Lang.T("Could not reach the host again"));
                CoopSaves.OfferToCarryOn();
                Log.Info("[session] gave up reconnecting after " + r.Attempts + " attempt(s)");
                return;
            }
            if (now < r.NextTry || !GameInfo.OnMainMenu) return;
            r.NextTry = now + 5f;
            r.Attempts++;
            Log.Info("[session] reconnect attempt " + r.Attempts);
            if (r.Address != null) JoinLan(r.Address, r.Password);
            else JoinSteamLobby(r.Lobby, r.Password.Length > 0 ? r.Password : null);
        }

        /// <summary>Stops trying to reconnect (the player chose something else).</summary>
        internal void CancelReconnect() => _reconnect = null;

        private void FixedUpdate()
        {
            if (Coop.Session == null) return;
            try
            {
                Get<CarrySync>()?.FixedTick();
                Get<TeamMotion>()?.FixedTick();
            }
            catch (Exception e) { Log.Debug("fixed tick: " + e.Message); }
        }

        private void LateUpdate()
        {
            if (Coop.Session == null) return;
            foreach (var m in _modules) Safe(m.LateTick, m, "late-tick");
        }

        /// <summary>Host: keeps the Steam lobby's advert (browser row, friends list) current.</summary>
        private void UpdateAdvert(NetSession s)
        {
            if (!s.IsHost || !Lobby.InLobby || !Lobby.IsOwner) return;
            float now = Time.unscaledTime;
            if (now < _nextAdvert) return;
            _nextAdvert = now + 2f;
            var w = WorldGeneration.world;
            bool inRun = GameInfo.InRunScene && w != null;
            string layer = inRun ? (w.biomeDepth + 1).ToString() : "";  // a number: each browser words it in its own language
            try { Lobby.UpdateAdvert(s.PeerCount, CoopPlugin.Instance.MaxPlayers.Value, inRun, layer, HostRules.Current.Encode()); }
            catch (Exception e) { Log.Debug("advert: " + e.Message); }
        }

        private void HandlePendingJoins()
        {
            if (!Lobby.Initialized || Connecting) return;
            if (_pendingLaunchLobby != 0 && GameInfo.OnMainMenu && Coop.Session == null)
            {
                _pendingJoinLobby = _pendingLaunchLobby;
                _pendingLaunchLobby = 0;
            }
            if (_pendingJoinLobby == 0) return;
            ulong id = _pendingJoinLobby;
            _pendingJoinLobby = 0;
            if (Coop.Session != null)
            {
                Notice(Lang.T("Leave your current session before joining another."));
                return;
            }
            JoinSteamLobby(id);
        }

        private bool _guiSeen;
        private GameObject _guiHost;

        /// <summary>
        /// Unity does not call OnGUI on our HideAndDontSave runtime object, and an
        /// ordinary object made at plugin load is destroyed with the first scene. So the
        /// overlay lives on its own object, created once a scene is up and re-created if
        /// anything ever destroys it.
        /// </summary>
        private void EnsureGuiHost()
        {
            if (_guiHost != null) return;
            _guiHost = new GameObject("CasualtiesCoop.UI");
            DontDestroyOnLoad(_guiHost);
            _guiHost.AddComponent<CoopGui>().Runtime = this;
        }

        internal void DrawGui()
        {
            if (!_guiSeen)
            {
                _guiSeen = true;
                Log.Info("[ui] overlay is drawing");
            }
            try
            {
                UiSkin.Apply();
                if (Coop.Session != null)
                    foreach (var m in _modules) Safe(m.Gui, m, "gui");
                ConditionFx.DrawFlash();
                CoopWindow.Draw(this);
                ConnectionBadge.Draw();
                Notices.Draw();
                UiMouse.EndFrame();
            }
            catch (Exception e)
            {
                Log.Error("gui: " + e.Message);
            }
        }

        private void OnApplicationQuit() => Leave();

        internal sealed class CoopGui : MonoBehaviour
        {
            internal CoopRuntime Runtime;
            private void OnGUI() => Runtime?.DrawGui();
        }

        internal static void Notice(string text) => Notices.Push(text);

        private static readonly HashSet<string> Reported = new HashSet<string>();

        private static void Safe(Action a, CoopModule m, string phase)
        {
            try { a(); }
            catch (Exception e)
            {
                string key = m.GetType().Name + phase + e.GetType().Name;
                if (Reported.Add(key)) Log.Error(m.GetType().Name + " " + phase + ": " + e);
            }
        }
    }
}
