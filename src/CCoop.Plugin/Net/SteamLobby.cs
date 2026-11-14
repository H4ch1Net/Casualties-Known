using System;
using System.Collections.Generic;
using CCoop.Plugin.Runtime;
using Steamworks;

namespace CCoop.Plugin.Net
{
    /// <summary>A CasualtiesCoop lobby as seen in the browser.</summary>
    internal sealed class LobbyInfo
    {
        public ulong Id;
        public string Host = "";
        public ulong HostId;
        public int Players;
        public int Max;
        public string Layer = "";
        public bool InRun;
        public bool Locked;
        public string ModVersion = "";
        public string GameBuild = "";
        public uint AppBuild;
        public string PingLocation = "";
        public int Ping = -1;
        public bool ViaFriend;
        public string Rules = "";
        public string FriendName = "";

        /// <summary>Why this lobby can't be joined from here, or null.</summary>
        public string Incompatible
        {
            get
            {
                if (ModVersion != CoopPlugin.Version) return "mod " + ModVersion + " (you have " + CoopPlugin.Version + ")";
                uint mine = SteamServices.AppBuildId;
                if (AppBuild != 0 && mine != 0 && AppBuild != mine) return "game build " + AppBuild + " (you have " + mine + ")";
                if (!string.IsNullOrEmpty(GameBuild) && GameBuild != GameInfo.GameBuild) return "different game version";
                return null;
            }
        }
    }

    /// <summary>
    /// Steam lobbies: how players find a session. The lobby advertises (host, layer,
    /// players, ping location, password lock, versions); game traffic goes over
    /// <see cref="SteamTransport"/> to the lobby owner.
    ///
    /// Joining works four ways, all ending in <see cref="JoinRequested"/> or a direct
    /// <see cref="Join"/>: the in-game browser, an overlay invite, "Join Game" in the
    /// Steam friends list, or launching the game from an invite (+connect_lobby).
    /// </summary>
    internal sealed class SteamLobby
    {
        private const string KeyGame = "ccoop_game";
        private const string GameTag = "casualtiescoop";
        private const string KeyMod = "ccoop";
        private const string KeyBuild = "ccoop_build";
        private const string KeyAppBuild = "ccoop_appbuild";
        private const string KeyHost = "ccoop_host";
        private const string KeyName = "ccoop_name";
        private const string KeyPing = "ccoop_ping";
        private const string KeyLocked = "ccoop_locked";
        private const string KeyPlayers = "ccoop_players";
        private const string KeyMax = "ccoop_max";
        private const string KeyLayer = "ccoop_layer";
        private const string KeyState = "ccoop_state";
        private const string KeyRules = "ccoop_rules";

        private CallResult<LobbyCreated_t> _createResult;
        private CallResult<LobbyEnter_t> _enterResult;
        private CallResult<LobbyMatchList_t> _listResult;
        private Callback<GameLobbyJoinRequested_t> _joinRequested;
        private Callback<GameRichPresenceJoinRequested_t> _richJoin;
        private Callback<LobbyDataUpdate_t> _dataUpdate;

        private Action<bool, string> _createDone;
        private Action<bool, LobbyInfo, string> _joinDone;
        private readonly Dictionary<string, string> _published = new Dictionary<string, string>();
        private readonly Dictionary<ulong, string> _friendLobbies = new Dictionary<ulong, string>();

        internal CSteamID Lobby { get; private set; } = CSteamID.Nil;
        internal bool InLobby => Lobby != CSteamID.Nil;
        internal bool IsOwner { get; private set; }
        internal bool Initialized { get; private set; }

        internal readonly List<LobbyInfo> PublicLobbies = new List<LobbyInfo>();
        internal readonly List<LobbyInfo> FriendLobbies = new List<LobbyInfo>();
        internal bool Searching { get; private set; }

        /// <summary>The player asked (via Steam) to join this lobby.</summary>
        internal event Action<ulong> JoinRequested;

        internal void EnsureInitialized()
        {
            if (Initialized || !GameInfo.SteamReady) return;
            _createResult = CallResult<LobbyCreated_t>.Create(OnCreated);
            _enterResult = CallResult<LobbyEnter_t>.Create(OnEntered);
            _listResult = CallResult<LobbyMatchList_t>.Create(OnList);
            _joinRequested = Callback<GameLobbyJoinRequested_t>.Create(r => JoinRequested?.Invoke(r.m_steamIDLobby.m_SteamID));
            _richJoin = Callback<GameRichPresenceJoinRequested_t>.Create(r =>
            {
                var id = ParseConnect(r.m_rgchConnect);
                if (id != 0) JoinRequested?.Invoke(id);
            });
            _dataUpdate = Callback<LobbyDataUpdate_t>.Create(OnDataUpdate);
            Initialized = true;
        }

        /// <summary>Lobby id from "+connect_lobby &lt;id&gt;" anywhere in the arguments.</summary>
        internal static ulong ParseConnect(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            var parts = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i + 1 < parts.Length; i++)
                if (parts[i] == "+connect_lobby" && ulong.TryParse(parts[i + 1], out var id)) return id;
            return 0;
        }

        internal static ulong LaunchLobby()
        {
            try { return ParseConnect(string.Join(" ", Environment.GetCommandLineArgs())); }
            catch { return 0; }
        }

        // ------------------------------------------------------------------ hosting

        internal void Create(string type, int maxMembers, bool locked, Action<bool, string> done)
        {
            EnsureInitialized();
            if (!Initialized) { done(false, "Steam is not running"); return; }
            _createDone = done;
            _published.Clear();
            var lobbyType = type == "Public" ? ELobbyType.k_ELobbyTypePublic
                : type == "InviteOnly" ? ELobbyType.k_ELobbyTypePrivate
                : ELobbyType.k_ELobbyTypeFriendsOnly;
            _pendingLocked = locked;
            _createResult.Set(SteamMatchmaking.CreateLobby(lobbyType, maxMembers));
        }

        private bool _pendingLocked;

        private void OnCreated(LobbyCreated_t r, bool ioFailure)
        {
            var done = _createDone;
            _createDone = null;
            if (ioFailure || r.m_eResult != EResult.k_EResultOK)
            {
                done?.Invoke(false, "Steam could not create a lobby (" + r.m_eResult + ")");
                return;
            }
            Lobby = new CSteamID(r.m_ulSteamIDLobby);
            IsOwner = true;
            Set(KeyGame, GameTag);
            Set(KeyMod, CoopPlugin.Version);
            Set(KeyBuild, GameInfo.GameBuild);
            Set(KeyAppBuild, SteamServices.AppBuildId.ToString());
            Set(KeyHost, GameInfo.SteamId.ToString());
            Set(KeyName, GameInfo.PlayerName);
            Set(KeyLocked, _pendingLocked ? "1" : "0");
            var ping = SteamServices.PingLocation();
            if (ping != null) Set(KeyPing, ping);
            SetPresence(1);
            Log.Info("[steam] lobby " + Lobby.m_SteamID + " created");
            done?.Invoke(true, null);
        }

        private void Set(string key, string value)
        {
            if (!InLobby || !IsOwner) return;
            value = value ?? "";
            if (_published.TryGetValue(key, out var old) && old == value) return;
            _published[key] = value;
            SteamMatchmaking.SetLobbyData(Lobby, key, value);
        }

        /// <summary>Host: keep the advert current. Cheap to call often; only changes go out.</summary>
        internal void UpdateAdvert(int players, int max, bool inRun, string layer, string rules)
        {
            if (!InLobby || !IsOwner) return;
            Set(KeyPlayers, players.ToString());
            if (!_published.TryGetValue(KeyMax, out var oldMax) || oldMax != max.ToString())
                SteamMatchmaking.SetLobbyMemberLimit(Lobby, max);
            Set(KeyMax, max.ToString());
            Set(KeyRules, rules ?? "");
            Set(KeyLocked, string.IsNullOrEmpty(CoopPlugin.Instance.HostPassword.Value) ? "0" : "1");
            Set(KeyState, inRun ? "run" : "menu");
            Set(KeyLayer, layer ?? "");
            Set(KeyName, GameInfo.PlayerName);
            if (!_published.ContainsKey(KeyPing))
            {
                var ping = SteamServices.PingLocation();
                if (ping != null) Set(KeyPing, ping);
            }
            bool joinable = players < max;
            if (!_published.TryGetValue("joinable", out var j) || j != (joinable ? "1" : "0"))
            {
                _published["joinable"] = joinable ? "1" : "0";
                SteamMatchmaking.SetLobbyJoinable(Lobby, joinable);
            }
            SetPresence(players);
        }

        /// <summary>Dev: our own lobby as a browser would read it.</summary>
        internal string DescribeOwnAdvert()
        {
            if (!InLobby) return "none";
            var l = Read(Lobby);
            if (l == null) return "unreadable";
            return "host=" + l.Host + " players=" + l.Players + "/" + l.Max + " state=" + (l.InRun ? "run " + l.Layer : "menu") +
                   " locked=" + l.Locked + " mod=" + l.ModVersion + " build=" + l.GameBuild + " appbuild=" + l.AppBuild +
                   " ping-location=" + (string.IsNullOrEmpty(l.PingLocation) ? "missing" : "set") +
                   " incompatible=" + (l.Incompatible ?? "no");
        }

        /// <summary>Host: changes who can find the lobby, mid-session.</summary>
        internal void SetType(string type)
        {
            if (!InLobby || !IsOwner) return;
            var t = type == "Public" ? ELobbyType.k_ELobbyTypePublic
                : type == "InviteOnly" ? ELobbyType.k_ELobbyTypePrivate
                : ELobbyType.k_ELobbyTypeFriendsOnly;
            SteamMatchmaking.SetLobbyType(Lobby, t);
        }

        // ------------------------------------------------------------------ joining

        internal void Join(ulong lobby, Action<bool, LobbyInfo, string> done)
        {
            EnsureInitialized();
            if (!Initialized) { done(false, null, "Steam is not running"); return; }
            Leave();
            _joinDone = done;
            _enterResult.Set(SteamMatchmaking.JoinLobby(new CSteamID(lobby)));
        }

        private void OnEntered(LobbyEnter_t r, bool ioFailure)
        {
            var done = _joinDone;
            _joinDone = null;
            if (ioFailure || r.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                done?.Invoke(false, null, "could not enter the lobby (" + (EChatRoomEnterResponse)r.m_EChatRoomEnterResponse + ")");
                return;
            }
            Lobby = new CSteamID(r.m_ulSteamIDLobby);
            IsOwner = false;
            var info = Read(Lobby);
            if (info == null)
            {
                Leave();
                done?.Invoke(false, null, "that lobby is not a CasualtiesCoop session");
                return;
            }
            // Members can see the real owner; connect to that, not to what the advert says.
            ulong owner = SteamMatchmaking.GetLobbyOwner(Lobby).m_SteamID;
            if (owner != 0) info.HostId = owner;
            var why = info.Incompatible;
            if (why != null)
            {
                Leave();
                done?.Invoke(false, info, "the host runs " + why + ". Update to match");
                return;
            }
            SetPresence(info.Players);
            done?.Invoke(true, info, null);
        }

        internal void Invite()
        {
            if (InLobby) SteamFriends.ActivateGameOverlayInviteDialog(Lobby);
        }

        private void SetPresence(int players)
        {
            try
            {
                SteamFriends.SetRichPresence("connect", "+connect_lobby " + Lobby.m_SteamID);
                // These two group party members together in the Steam friends list and
                // need no developer-defined text, unlike custom status lines.
                SteamFriends.SetRichPresence("steam_player_group", Lobby.m_SteamID.ToString());
                SteamFriends.SetRichPresence("steam_player_group_size", Math.Max(1, players).ToString());
            }
            catch { /* presence is cosmetic */ }
        }

        internal void Leave()
        {
            if (!InLobby) return;
            try
            {
                SteamMatchmaking.LeaveLobby(Lobby);
                SteamFriends.ClearRichPresence();
            }
            catch { /* Steam shutting down */ }
            Lobby = CSteamID.Nil;
            IsOwner = false;
            _published.Clear();
        }

        // ------------------------------------------------------------------ browsing

        /// <summary>Refreshes the public list and the friends' games list.</summary>
        internal void Refresh()
        {
            EnsureInitialized();
            if (!Initialized || Searching) return;
            Searching = true;
            PublicLobbies.Clear();
            SteamMatchmaking.AddRequestLobbyListStringFilter(KeyGame, GameTag, ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
            SteamMatchmaking.AddRequestLobbyListResultCountFilter(100);
            _listResult.Set(SteamMatchmaking.RequestLobbyList());
            RefreshFriends();
        }

        private void OnList(LobbyMatchList_t r, bool ioFailure)
        {
            Searching = false;
            if (ioFailure) return;
            for (int i = 0; i < r.m_nLobbiesMatching; i++)
            {
                var id = SteamMatchmaking.GetLobbyByIndex(i);
                if (id == Lobby) continue;
                var info = Read(id);
                if (info != null) PublicLobbies.Add(info);
            }
            PublicLobbies.Sort((a, b) => Order(a).CompareTo(Order(b)));
        }

        private static int Order(LobbyInfo l) => (l.Incompatible != null ? 100000 : 0) + (l.Ping < 0 ? 9999 : l.Ping);

        /// <summary>Friends currently in any lobby of this game; the CasualtiesCoop ones show up once their data arrives.</summary>
        private void RefreshFriends()
        {
            FriendLobbies.Clear();
            _friendLobbies.Clear();
            try
            {
                uint app = SteamUtils.GetAppID().m_AppId;
                int n = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
                for (int i = 0; i < n; i++)
                {
                    var friend = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                    if (!SteamFriends.GetFriendGamePlayed(friend, out var game)) continue;
                    if (game.m_gameID.AppID().m_AppId != app || game.m_steamIDLobby == CSteamID.Nil) continue;
                    ulong lobby = game.m_steamIDLobby.m_SteamID;
                    if (_friendLobbies.ContainsKey(lobby)) continue;
                    _friendLobbies[lobby] = SteamFriends.GetFriendPersonaName(friend);
                    if (!AddFriendLobby(lobby)) SteamMatchmaking.RequestLobbyData(game.m_steamIDLobby);
                }
            }
            catch (Exception e) { Log.Debug("friend lobbies: " + e.Message); }
        }

        private bool AddFriendLobby(ulong lobby)
        {
            var info = Read(new CSteamID(lobby));
            if (info == null) return false;
            info.ViaFriend = true;
            info.FriendName = _friendLobbies.TryGetValue(lobby, out var name) ? name : "";
            FriendLobbies.RemoveAll(l => l.Id == lobby);
            FriendLobbies.Add(info);
            return true;
        }

        private void OnDataUpdate(LobbyDataUpdate_t r)
        {
            if (r.m_bSuccess == 0 || r.m_ulSteamIDLobby != r.m_ulSteamIDMember) return;
            if (_friendLobbies.ContainsKey(r.m_ulSteamIDLobby)) AddFriendLobby(r.m_ulSteamIDLobby);
        }

        /// <summary>The advert of a lobby, or null if it isn't a CasualtiesCoop lobby.</summary>
        private static LobbyInfo Read(CSteamID id)
        {
            string Get(string key) => SteamMatchmaking.GetLobbyData(id, key) ?? "";
            if (Get(KeyGame) != GameTag && string.IsNullOrEmpty(Get(KeyMod))) return null;
            var info = new LobbyInfo
            {
                Id = id.m_SteamID,
                Host = Get(KeyName),
                ModVersion = Get(KeyMod),
                GameBuild = Get(KeyBuild),
                Layer = Get(KeyLayer),
                InRun = Get(KeyState) == "run",
                Locked = Get(KeyLocked) == "1",
                PingLocation = Get(KeyPing),
                Rules = Get(KeyRules),
            };
            ulong.TryParse(Get(KeyHost), out info.HostId);
            if (info.HostId == 0) info.HostId = SteamMatchmaking.GetLobbyOwner(id).m_SteamID;
            uint.TryParse(Get(KeyAppBuild), out info.AppBuild);
            int.TryParse(Get(KeyPlayers), out info.Players);
            if (info.Players <= 0) info.Players = SteamMatchmaking.GetNumLobbyMembers(id);
            if (!int.TryParse(Get(KeyMax), out info.Max) || info.Max <= 0) info.Max = SteamMatchmaking.GetLobbyMemberLimit(id);
            info.Ping = SteamServices.EstimatePing(info.PingLocation);
            if (string.IsNullOrEmpty(info.Host)) info.Host = SteamServices.Name(info.HostId) ?? "Unknown host";
            return info;
        }
    }
}
