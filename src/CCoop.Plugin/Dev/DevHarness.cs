using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using CCoop.Net.Session;
using CCoop.Plugin.Protocol;
using CCoop.Plugin.Runtime;
using CCoop.Plugin.Sync;
using CCoop.Plugin.World;
using UnityEngine;

namespace CCoop.Plugin.Dev
{
    /// <summary>
    /// Opt-in automation for testing two instances on one machine. Inert unless the
    /// environment variables are set (config is shared between instances; env is not).
    ///
    ///   CCOOP_AUTOSTART=host|join   host on, or join 127.0.0.1 at, the LAN port
    ///   CCOOP_AUTOSTART=steamhost   host an invite-only Steam lobby (needs Steam running)
    ///   CCOOP_AUTORUN=1             host: start a run from the menu once a client joins
    ///   CCOOP_SELFTEST=1            host: mutate terrain, break and damage objects once
    ///                               everyone is in; both sides then log VERIFY lines
    ///   CCOOP_DUMP=1                log the local character's hierarchy once
    ///   CCOOP_STARTDEPTH=n          host: start the run on layer n (1-12), e.g. 8 for crystals
    ///
    /// Touching %LocalAppData%Low\Orsoniks\CasualtiesUnknown\CasualtiesCoop\verify.trigger
    /// makes every running instance log a VERIFY line at the same moment.
    /// </summary>
    internal sealed class DevHarness : CoopModule
    {
        private static string Env(string name)
        {
            try { return (Environment.GetEnvironmentVariable(name) ?? "").Trim().ToLowerInvariant(); }
            catch { return ""; }
        }

        private readonly string _autostart = Env("CCOOP_AUTOSTART");
        private readonly bool _autorun = Env("CCOOP_AUTORUN") == "1";
        private readonly bool _selftest = Env("CCOOP_SELFTEST") == "1";

        /// <summary>A self-test is running in this instance (it must not write the player's own files).</summary>
        internal static readonly bool SelfTestActive = Env("CCOOP_SELFTEST") == "1";
        private readonly bool _dump = Env("CCOOP_DUMP") == "1";

        /// <summary>Host: force this layer modifier (its modifierIndex), or -1.</summary>
        internal static readonly int ForceModifier = int.TryParse(Env("CCOOP_MODIFIER"), out var m) ? m : -1;

        /// <summary>Layer number (1-12) the host's run starts on, or 0.</summary>
        /// <summary>CCOOP_RESPAWN=NextLayer|Permadeath|Quick: the host's rule, without touching the config.</summary>
        internal static readonly string RespawnOverride = Env("CCOOP_RESPAWN");

        /// <summary>CCOOP_EXITSHARE=0..100: the host's exit rule, without touching the config.</summary>
        internal static readonly int ExitShareOverride = int.TryParse(Env("CCOOP_EXITSHARE"), out var share) ? share : -1;

        /// <summary>Loot and danger rules for tests, without touching the player's config.</summary>
        internal static readonly int LootOverride = int.TryParse(Env("CCOOP_LOOT"), out var loot) ? loot : -1;
        internal static readonly int DangerOverride = int.TryParse(Env("CCOOP_DANGER"), out var danger) ? danger : -1;

        /// <summary>Seconds between desync checks (tests use a short one).</summary>
        internal static readonly float CheckEveryOverride = float.TryParse(Env("CCOOP_CHECKEVERY"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var every) ? every : -1f;

        internal static readonly int StartLayer = int.TryParse(Env("CCOOP_STARTDEPTH"), out var d) ? d : 0;

        private bool _started, _ranRun, _dumped;
        private float _startAt = -1f;
        private int _stage;
        private ushort _descentEpoch;
        private float _stageAt;
        private DateTime _lastTrigger = DateTime.MinValue;
        private float _nextTriggerPoll;

        internal DevHarness()
        {
            if (_autostart == "host" || _autostart == "join" || _autostart == "steamhost") _startAt = 4f;
        }

        // Ticks even without a session: it is what starts one.
        internal void Pump()
        {
            if (!_started && _startAt >= 0 && Time.realtimeSinceStartup > _startAt && GameInfo.OnMainMenu)
            {
                if (_autostart == "steamhost")
                {
                    if (!GameInfo.SteamReady || !Coop.Runtime.Lobby.Initialized) return;
                    _started = true;
                    Log.Info("DEV steam as " + GameInfo.SteamName + "; hosting an invite-only lobby");
                    Coop.Runtime.HostSteam("InviteOnly");  // not written to the player's config
                    return;
                }
                _started = true;
                if (_autostart == "host") Coop.Runtime.HostLan();
                else Coop.Runtime.JoinLan("127.0.0.1");
            }
            if (_autostart == "steamhost" && _started && !_steamReported) SteamReport();
            if (!string.IsNullOrEmpty(_shots)) Shots();
            if (_dump && !_dumped && GameInfo.LocalBody != null && Coop.WorldLive)
            {
                _dumped = true;
                DumpCharacter();
            }
            PollTrigger();
        }

        // CCOOP_SHOTS=<dir>: capture the co-op window's screens for layout review.
        private readonly string _shots = Env("CCOOP_SHOTS");
        private int _shotStage;
        private float _shotAt = -1f;

        private void Shots()
        {
            if (_shotStage >= 99) return;
            float now = Time.realtimeSinceStartup;
            if (_shotAt < 0f)
            {
                if (now < 8f || !GameInfo.OnMainMenu) return;
                _shotAt = now;
            }
            if (now < _shotAt) return;
            // Even steps set a screen up; odd steps capture it on a later frame.
            string[] names = { "1-host", "2-find", "3-lan", "4-password", "5-online", "6-online-150" };
            int step = _shotStage / 2;
            if (step >= names.Length)
            {
                UI.CoopWindow.DevScale = 0f;
                Coop.Runtime.PasswordPrompt = null;
                Log.Info("DEV shots saved to " + _shots);
                _shotStage = 99;
                return;
            }
            if (_shotStage % 2 == 1)
            {
                ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(_shots, names[step] + ".png"));
                _shotStage++;
                _shotAt = now + 0.5f;
                return;
            }
            switch (step)
            {
                case 0: UI.CoopWindow.DevSelectTab(0); break;
                case 1: UI.CoopWindow.DevSelectTab(1); Coop.Runtime.Lobby.Refresh(); break;
                case 2: UI.CoopWindow.DevSelectTab(2); break;
                case 3:
                    Coop.Runtime.PasswordPrompt = new Net.LobbyInfo { Host = GameInfo.PlayerName, HostId = GameInfo.SteamId, Locked = true, Players = 2, Max = 4 };
                    Coop.Runtime.LastMessage = GameInfo.PlayerName + "'s session needs a password.";
                    break;
                case 4: Coop.Runtime.PasswordPrompt = null; Coop.Runtime.HostLan(); break;
                case 5: UI.CoopWindow.DevScale = 1.5f; break;
            }
            _shotStage++;
            _shotAt = now + 2f;
        }

        private bool _steamReported;
        private float _steamReportAt = -1f;

        /// <summary>
        /// Steam smoke test: once the lobby has been up a while, read back what the M1
        /// features produced (advert, ping location, build id, avatar, browser query).
        /// </summary>
        private void SteamReport()
        {
            var lobby = Coop.Runtime.Lobby;
            if (!lobby.InLobby) return;
            if (_steamReportAt < 0f)
            {
                _steamReportAt = Time.realtimeSinceStartup + 12f;
                lobby.Refresh();
                return;
            }
            if (Time.realtimeSinceStartup < _steamReportAt || lobby.Searching) return;
            _steamReported = true;
            var info = lobby.DescribeOwnAdvert();
            Log.Info("STEAM advert " + info);
            string loc = Net.SteamServices.PingLocation();
            Log.Info("STEAM ping location " + (loc == null ? "unknown" : loc.Length + " chars") +
                     ", estimate to self " + Net.SteamServices.EstimatePing(loc) + " ms");
            Log.Info("STEAM app build " + Net.SteamServices.AppBuildId + ", deck " + Net.SteamServices.OnDeckCached +
                     ", on-screen keyboard " + Net.SteamServices.OnScreenKeyboard);
            var avatar = Net.SteamServices.Avatar(GameInfo.SteamId);
            Log.Info("STEAM own avatar " + (avatar != null ? avatar.width + "x" + avatar.height : "not loaded yet"));
            Log.Info("STEAM browser: " + lobby.PublicLobbies.Count + " public, " + lobby.FriendLobbies.Count + " friends' lobbies");
            Log.Info("STEAM ui scale " + UI.CoopWindow.Scale);
        }

        internal override void Tick()
        {
            if (_autorun && !_ranRun && Coop.IsHost && GameInfo.OnMainMenu && Coop.Session.PeerCount > 1 && PreRunScript.instance != null)
            {
                _ranRun = true;
                Log.Info("DEV autorun: starting a run");
                SaveSystem.loadedRun = false;
                WorldGeneration.runSettings = PreRunScript.instance.runSettings;
                PreRunScript.instance.StartCoroutine(PreRunScript.instance.WaitLoad());
            }
            if (_selftest) SelfTest();
        }

        private void SelfTest()
        {
            if (!Coop.WorldLive) return;
            HoldAsleep();
            if ((_stage >= 200 && _stage < 211) || (_stage >= 220 && _stage < 230))
            {
                CareStages();
                return;
            }
            if (_stage == 0)
            {
                if (Coop.IsHost)
                {
                    bool allIn = Coop.Session.PeerCount > 1;
                    foreach (var r in Coop.Players.Values) if (r.Id != NetSession.HostId && !r.InWorld) allIn = false;
                    if (!allIn) return;
                }
                _stage = 1;
                _stageAt = Time.unscaledTime + 6f;
                Log.Info("DEV selftest armed");
                return;
            }
            if (Time.unscaledTime < _stageAt) return;
            if (_stage == 1)
            {
                _stage = 2;
                _stageAt = Time.unscaledTime + 5f;
                Verify("before");
                // Both sides act, on different cells and objects, so both directions of
                // every path are exercised: host->client and client->host->others.
                string who = Coop.IsHost ? "host" : "client";
                var w = WorldGeneration.world;
                int x0 = Coop.IsHost ? 100 : 200;
                for (int x = x0; x < x0 + 5; x++) for (int y = 100; y < 105; y++) w.SetBlock(new Vector2Int(x, y), 1);
                Log.Info("DEV " + who + " wrote 25 blocks");
                ItemTest();
                ExplosionTest();
                var ids = new List<uint>(EntityRegistry.Ids);
                ids.Sort();
                if (!Coop.IsHost) ids.Reverse();
                bool broke = false, hurt = false;
                foreach (var id in ids)
                {
                    if (!EntityRegistry.TryGet(id, out var go)) continue;
                    var b = go.GetComponent<BuildingEntity>();
                    if (b == null || b.animal || b.indestructible || b.health < 60f) continue;
                    if (!broke && (b.guaranteedDropAmount > 0 || (b.alwaysDrop != null && b.alwaysDrop.Length > 0)))
                    {
                        broke = true;
                        Log.Info("DEV " + who + " breaks " + id + " (" + EntityRegistry.KeyOf(id) + ")");
                        b.health = 0f;
                        continue;
                    }
                    if (!hurt)
                    {
                        hurt = true;
                        Log.Info("DEV " + who + " damages " + id + " (" + EntityRegistry.KeyOf(id) + ") by 25");
                        b.health -= 25f;
                    }
                    if (broke && hurt) break;
                }
                return;
            }
            if (_stage == 2)
            {
                _stage = 3;
                _stageAt = Time.unscaledTime + 3f;
                _descentEpoch = Coop.Epoch;
                Verify("after");
                return;
            }
            if (_stage == 3)
            {
                // M2: the client dies. It must spectate (no death screen, screen clear,
                // camera on the host) while the host plays on.
                _stage = 4;
                // Under the Quick rule the client must be back, beside the host, before this check.
                _stageAt = Time.unscaledTime + (LifeSync.Rule == Protocol.RespawnRule.Quick ? LifeSync.QuickDelay + 4f : 6f);
                if (Coop.IsClient)
                {
                    Log.Info("DEV client dies");
                    GameInfo.LocalBody.brainHealth = 0f;
                }
                return;
            }
            if (_stage == 4)
            {
                _stage = 5;
                _stageAt = Time.unscaledTime + 1f;
                Verify("dead");
                if (Coop.IsClient) Shot("dead");
                return;
            }
            if (_stage == 5)
            {
                // The host descends (the dead can't vote): everyone must land on the same
                // next layer, and the client must come back alive there.
                _stage = 6;
                if (Coop.IsHost)
                {
                    Log.Info("DEV host requests descent");
                    var w = WorldGeneration.world;
                    w.StartCoroutine(w.RegenerateWorld(false));
                }
                return;
            }
            if (_stage == 6 && Coop.Epoch != _descentEpoch)
            {
                _stage = 7;
                _stageAt = Time.unscaledTime + 6f;
                return;
            }
            if (_stage == 7)
            {
                _stage = 8;
                _stageAt = Time.unscaledTime + 6f;
                Verify("descended");
                // Both fall asleep: the shared clock must race only once both are out.
                _forceSleep = true;
                Log.Info("DEV " + (Coop.IsHost ? "host" : "client") + " falls asleep");
                return;
            }
            if (_stage == 8)
            {
                _stage = 9;
                _stageAt = Time.unscaledTime + 4f;
                Verify("asleep");
                return;
            }
            if (_stage == 9)
            {
                _stage = 100;
                _stageAt = Time.unscaledTime + 5f;
                _forceSleep = false;
                var b = GameInfo.LocalBody;
                if (b != null)
                {
                    b.WakeUp();
                    b.consciousness = 100f;
                }
                Log.Info("DEV " + (Coop.IsHost ? "host" : "client") + " wakes up");
                return;
            }
            if (_stage == 100)
            {
                // M3: chat, ping, input block, an exit vote that must wait, and a crawler
                // put onto the client's puppet (its bite must land on the client's body).
                _stage = 101;
                _stageAt = Time.unscaledTime + 5f;
                Verify("awake");
                _descentEpoch = Coop.Epoch;  // before anyone votes: the descent may come before stage 101
                _waitUntil = Time.unscaledTime + 25f;
                M3Actions();
                return;
            }
            if (_stage == 101)
            {
                // The client can be seconds behind (it wakes up later): the host votes only
                // once the client's own actions (its chat line first) have arrived.
                if (Coop.IsHost && Chat.Count == 0 && Time.unscaledTime < _waitUntil) return;
                _stage = 102;
                Verify("team");
                if (Coop.IsHost)
                {
                    Log.Info("DEV host votes to descend too");
                    var w = WorldGeneration.world;
                    w.StartCoroutine(w.RegenerateWorld(false));
                }
                return;
            }
            if (_stage == 102 && Coop.Epoch != _descentEpoch)
            {
                _stage = 103;
                _stageAt = Time.unscaledTime + 6f;
                return;
            }
            if (_stage == 103)
            {
                _stage = 300;
                _stageAt = Time.unscaledTime + 3f;
                Verify("exited");
                return;
            }
            if (_stage == 300)
            {
                // M10: the client's connection drops as a network failure would. It must
                // save, go back to the menu, reconnect on its own and rejoin the run where
                // it was. Then the care tests go on.
                _stage = 301;
                _sawDrop = false;
                _waitUntil = Time.unscaledTime + 90f;
                if (Coop.IsClient)
                {
                    Log.Info("DEV client's connection drops");
                    Coop.Session.SimulateDrop();
                }
                return;
            }
            if (_stage == 301)
            {
                if (Coop.IsHost)
                {
                    bool present = false, back = false;
                    foreach (var r in Coop.Players.Values)
                    {
                        if (r.Id == NetSession.HostId) continue;
                        if (r.InWorld) present = true;
                        if (r.InWorld && r.WorldEpoch == Coop.Epoch && r.HasPosition) back = true;
                    }
                    if (!_sawDrop)
                    {
                        // The client is a couple of seconds behind: first see it go.
                        if (present && Time.unscaledTime < _waitUntil) return;
                        _sawDrop = true;
                        Log.Info("DEV host sees the client drop");
                        return;
                    }
                    if (!back && Time.unscaledTime < _waitUntil) return;
                    Log.Info("DEV host sees the client " + (back ? "back in the layer" : "not back in time"));
                }
                else if (!Coop.WorldLive && Time.unscaledTime < _waitUntil) return;

                _stage = 200;
                _stageAt = Time.unscaledTime + 3f;
                _waitUntil = Time.unscaledTime + 25f;
                Verify("reconnected");
                Steady();
                M4Setup();
                return;
            }
            if (_stage == 211)
            {
                // The host dies: its world must keep running for the client, and the
                // creatures must move to the living client's machine.
                _stage = 11;
                _stageAt = Time.unscaledTime + 6f;
                Verify("revived");
                if (Coop.IsHost)
                {
                    Log.Info("DEV host dies");
                    GameInfo.LocalBody.brainHealth = 0f;
                }
                return;
            }
            if (_stage == 11)
            {
                // Then the client: everybody is dead, so the host calls a wipe and both
                // get the normal death screen.
                _stage = 12;
                _stageAt = Time.unscaledTime + 8f;
                Verify("hostdead");
                if (Coop.IsHost) Shot("hostdead");
                if (Coop.IsClient)
                {
                    Log.Info("DEV client dies too");
                    GameInfo.LocalBody.brainHealth = 0f;
                }
                return;
            }
            if (_stage == 12)
            {
                _stage = 13;
                Verify("wiped");
                if (Coop.IsClient) Shot("wiped");
                Log.Info("DEV selftest complete");
            }
        }

        private bool _forceSleep;
        private bool _forceOut;
        private float _cprUntil, _nextDevCpr, _nextShock;
        private int _shocks;

        // ------------------------------------------------------------------ M4: care

        /// <summary>
        /// Client: silently changes blocks far from anyone, as a desync would. The desync
        /// alarm must find the chunk and the host's copy must repair it.
        /// </summary>
        private static void CorruptChunk()
        {
            var g = WorldAccess.Blocks;
            if (g == null) return;
            int size = WorldGeneration.CHUNKSIZE;
            int cx = 1, cy = 1;
            int changed = 0;
            for (int x = cx * size + 10; x < cx * size + 20 && x < g.GetLength(0); x++)
            for (int y = cy * size + 10; y < cy * size + 20 && y < g.GetLength(1); y++)
            {
                g[x, y] = (ushort)(g[x, y] == 0 ? 1 : 0);
                changed++;
            }
            Log.Info("DEV client corrupts " + changed + " blocks in chunk " + cx + "," + cy + " (as a desync would)");
        }

        /// <summary>Awake and on its feet, whatever the random layer did (a trap, a fall).</summary>
        private static void Steady()
        {
            var b = GameInfo.LocalBody;
            if (b == null || !b.alive) return;
            b.consciousness = 100f;
            b.shock = 0f;
            b.Stand(true);
            Log.Info("DEV " + (Coop.IsHost ? "host" : "client") + " steadies itself before the care tests");
        }

        private static byte OtherPlayer()
        {
            foreach (var id in Coop.Players.Keys) if (id != Coop.LocalId) return id;
            return NetSession.HostId;
        }

        private void M4Setup()
        {
            if (Coop.IsClient)
            {
                var limb = GameInfo.LocalBody.limbs[3];
                limb.Dislocate();
                limb.bleedAmount = 4f;
                limb.skinHealth = 50f;
                GiveSync.DevAutoAccept = true;
                Log.Info("DEV client hurts " + limb.name + " (bleeding, dislocated)");
                CorruptChunk();
                return;
            }
            WalkOver();
        }

        private static bool WalkOver()
        {
            var r = Coop.Get<PlayerSync>().Find(OtherPlayer());
            var me = GameInfo.LocalBody;
            if (r?.ShownAt == null || me == null) return false;
            if (Vector2.Distance(me.transform.position, r.ShownAt.Value) < 2f) return true;
            BodyFreeze.PlaceAt(r.ShownAt.Value + new Vector2(1.2f, 0.4f));
            Log.Info("DEV host walks over to the client");
            return true;
        }

        private static Item GiveHand(string id)
        {
            var body = GameInfo.LocalBody;
            var held = Medic.HeldItem(body);
            if (held != null)
            {
                body.DropItem(held);
                UnityEngine.Object.Destroy(held.gameObject);
            }
            var prefab = Resources.Load(id) as GameObject;
            if (prefab == null) { Log.Info("DEV no item " + id); return null; }
            var item = UnityEngine.Object.Instantiate(prefab, body.transform.position, Quaternion.identity).GetComponent<Item>();
            body.PickUpItem(item, body.handSlot, true);
            return item;
        }

        private int _simSteps;
        private float _catchReadyUntil, _nextReadyDev;
        private float _waitUntil;
        private bool _sawDrop;
        private float _simEndAt;

        /// <summary>Plays the running treatment minigame's "hand" for the test: wraps, pushes, shocks.</summary>
        private void SimulateMinigame()
        {
            var mg = MinigameBase.main != null ? MinigameBase.main.currentMinigame : null;
            if (mg == null || !MedicSession.Active) return;
            if (_simSteps > 0)
            {
                _simSteps--;
                if (mg is BandageMinigame b) b.OnUse(1f / 18f);
                else if (mg is SyringeMinigame sy) sy.OnUse(0.25f);
            }
            if (_simEndAt > 0f && Time.unscaledTime >= _simEndAt)
            {
                _simEndAt = 0f;
                MinigameBase.main.EndMinigame();
            }
        }

        private void CareStages()
        {
            float now = Time.unscaledTime;
            byte other = OtherPlayer();
            var medic = Coop.Get<Medic>();

            if (_forceOut && GameInfo.LocalBody != null) GameInfo.LocalBody.consciousness = Mathf.Min(GameInfo.LocalBody.consciousness, 5f);
            if (Coop.IsHost && now < _catchReadyUntil && now >= _nextReadyDev)
            {
                _nextReadyDev = now + 0.25f;
                Coop.Session.Broadcast(new TeamMove { Kind = MoveKind.CatchReady, F = 0.6f });
            }
            if (Coop.IsHost)
            {
                SimulateMinigame();
                if (now < _cprUntil && now >= _nextDevCpr)
                {
                    _nextDevCpr = now + 0.5f;
                    WalkOver();
                    medic.Cpr(other);
                }
                if (_stage == 210 && now >= _nextShock && _shocks < 10)
                {
                    var v = Coop.Get<VitalsSync>().Of(other);
                    bool dead = v != null && (v.Signs & BodySigns.Alive) == 0;
                    if (dead || _shocks == 0)
                    {
                        _nextShock = now + 1.3f;
                        if (!MedicSession.Active) medic.Treat(other, 1, TreatKind.Item);
                        var item = MinigameBase.main != null ? MinigameBase.main.currentItem : null;
                        if (MedicSession.Active && item != null && MedicSession.StandIn != null)
                        {
                            _shocks++;
                            item.Defibrillate(new Item.DefibInfo { chance = 1f, limb = MedicSession.StandIn.body.limbs[1] });
                            Log.Info("DEV host presses shock (" + _shocks + "), fibrillation seen by the AED " +
                                     MedicSession.Fibrillation(MedicSession.StandIn.body).ToString("F0"));
                        }
                    }
                    else if (MedicSession.Active) MinigameBase.main.EndMinigame();
                }
            }
            if (now < _stageAt) return;

            switch (_stage)
            {
                case 200:
                    if (Coop.IsHost)
                    {
                        // The client may still be loading the layer: wait until it's here and hurt.
                        var cv = Coop.Get<VitalsSync>().Of(other);
                        if ((!WalkOver() || cv == null || (cv.Limbs.Count > 3 && (cv.Limbs[3].Signs & LimbSigns.Dislocated) == 0)) && now < _waitUntil) return;
                        GiveHand("bandage");
                        medic.Treat(other, 3, TreatKind.Item);
                        _simSteps = 36;
                        _simEndAt = now + 2.5f;
                        Log.Info("DEV host wraps the client's arm (minigame " + (MinigameBase.main?.currentMinigame?.GetType().Name ?? "none") + ")");
                    }
                    _stage = 201; _stageAt = now + 4f;
                    break;
                case 201:
                    Verify("bandaged");
                    if (Coop.IsHost)
                    {
                        WalkOver();
                        medic.Treat(other, 3, TreatKind.SetDislocation);
                        MedicSession.DevAct = limb => limb.UnDislocate();
                        _simEndAt = now + 1.5f;
                        Log.Info("DEV host sets the joint (minigame " + (MinigameBase.main?.currentMinigame?.GetType().Name ?? "none") + ")");
                    }
                    if (Coop.IsClient) { GameInfo.LocalBody.brainHealth = 45f; Log.Info("DEV client's brain is damaged"); }
                    _stage = 202; _stageAt = now + 3f;
                    break;
                case 202:
                    Verify("jointset");
                    if (Coop.IsHost)
                    {
                        Coop.Get<Chat>()?.Send("can you read this");
                        WalkOver();
                        GiveHand("morphine");
                        medic.Treat(other, 3, TreatKind.Item);
                        _simSteps = 8;
                        _simEndAt = now + 2f;
                        Log.Info("DEV host injects morphine (minigame " + (MinigameBase.main?.currentMinigame?.GetType().Name ?? "none") + ")");
                    }
                    _stage = 203; _stageAt = now + 3.5f;
                    break;
                case 203:
                    Verify("injected");
                    if (Coop.IsClient)
                    {
                        GameInfo.LocalBody.brainHealth = 100f;
                        WalkOverClient();
                        Coop.Get<GestureSync>()?.DevDo("Hug");
                        var pant = GameInfo.LocalBody.GetComponentInChildren<PantSound>();
                        GameInfo.LocalBody.eatTime = 0f;
                        pant?.Bark();
                        Log.Info("DEV client hugs the host and barks");
                    }
                    if (Coop.IsHost) { GiveHand("adhesivebandage"); Coop.Get<GiveSync>().Offer(other); Log.Info("DEV host offers an adhesive bandage"); }
                    _stage = 204; _stageAt = now + 3f;
                    break;
                case 204:
                    Verify("given");
                    _stage = 220; _stageAt = now + 2f;
                    break;

                // ---- moving as a team
                case 220:
                    if (Coop.IsHost) { WalkOver(); Coop.Get<TeamMotion>().GiveLegUp(other); Log.Info("DEV host gives a leg-up"); }
                    _stage = 221; _stageAt = now + 1.5f;
                    break;
                case 221:
                    if (Coop.IsClient)
                    {
                        var b = GameInfo.LocalBody;
                        b.Jump();
                        Log.Info("DEV client jumps: vy " + b.rb.velocity.y.ToString("F1") + " (jump speed " + b.jumpSpeed.ToString("F1") + ")");
                        b.happiness = -95f;
                        Log.Info("DEV client sinks into a deep low: mood " + b.totalHappiness.ToString("F0"));
                    }
                    _stage = 222; _stageAt = now + 2f;
                    break;
                case 222:
                    Verify("legup");
                    if (Coop.IsHost) { Coop.Get<TeamMotion>().Snap(other); Log.Info("DEV host snaps the client out of it"); }
                    _stage = 223; _stageAt = now + 2f;
                    break;
                case 223:
                    Verify("snapped");
                    if (Coop.IsClient)
                    {
                        var b = GameInfo.LocalBody;
                        b.happiness = 0f;
                        for (int i = 0; i < 2; i++) { var it = b.GetItem(i); if (it != null) b.DropItem(it); }
                    }
                    if (Coop.IsHost) { GiveHand("heavydrill"); Log.Info("DEV host picks up a heavy drill"); }
                    _stage = 224; _stageAt = now + 2f;
                    break;
                case 224:
                    if (Coop.IsClient) { WalkOverClient(); Coop.Get<TeamMotion>().StartLifting(NetSession.HostId); Log.Info("DEV client helps carry it"); }
                    _stage = 225; _stageAt = now + 2f;
                    _waitUntil = now + 8f;
                    break;
                case 225:
                    if (Coop.IsHost && TeamMotion.LiftLoad >= 0f && now < _waitUntil) return;
                    Verify("lifting");
                    if (Coop.IsClient) Coop.Get<TeamMotion>().StopLifting(true);
                    if (Coop.IsHost)
                    {
                        var held = Medic.HeldItem(GameInfo.LocalBody);
                        if (held != null) { GameInfo.LocalBody.DropItem(held); UnityEngine.Object.Destroy(held.gameObject); }
                    }
                    _stage = 226; _stageAt = now + 1f;
                    break;
                case 226:
                    if (Coop.IsHost) { Coop.Get<TeamMotion>().PullUp(other); Log.Info("DEV host reaches down to the client"); }
                    _stage = 227; _stageAt = now + 2.5f;
                    break;
                case 227:
                    Verify("pulled");
                    // Let go of the hand, and give the host time to stand still before anyone falls.
                    if (Coop.IsClient) GameInfo.LocalBody?.StopClimbing();
                    if (Coop.IsHost) { WalkOver(); _catchReadyUntil = now + 10f; Log.Info("DEV host gets ready to catch"); }
                    _stage = 229; _stageAt = now + 2.5f;
                    _waitUntil = now + 20f;
                    break;
                case 229:
                    // The two sides' stage clocks drift: fall only once the host is ready.
                    if (Coop.IsClient && !Coop.Get<TeamMotion>().ReadyToCatch(NetSession.HostId) && now < _waitUntil) return;
                    if (Coop.IsClient)
                    {
                        // On its feet and awake whatever the layer did to it (a coil, a fall).
                        var me = GameInfo.LocalBody;
                        if (me != null)
                        {
                            me.StopClimbing();
                            me.consciousness = 100f;
                            me.shock = 0f;
                            me.Stand(true);
                        }
                        var host = Coop.Get<PlayerSync>().Find(NetSession.HostId);
                        var chest = host?.LimbPosition(1);
                        if (chest.HasValue)
                        {
                            var b = GameInfo.LocalBody;
                            BodyFreeze.PlaceAt(chest.Value + new Vector2(0f, 2f));  // inside the catch band: no ceiling in the way
                            float hard = TeamMotion.HardLanding(b);
                            b.SetVelocity(new Vector2(0f, -hard * 1.1f));
                            Log.Info("DEV client falls onto the host at " + (hard * 1.1f).ToString("F0") + " (hard landing " + hard.ToString("F0") + ")");
                        }
                    }
                    _stage = 228; _stageAt = now + 4f;
                    break;
                case 228:
                    Verify("caught");
                    if (Coop.IsClient) { _forceOut = true; Log.Info("DEV client passes out"); }
                    _stage = 205; _stageAt = now + 2f;
                    _waitUntil = now + 10f;
                    break;
                case 205:
                    if (Coop.IsHost)
                    {
                        if (!CarrySync.Downed(other) && now < _waitUntil) return;  // wait for the client to pass out
                        WalkOver();
                        Coop.Get<CarrySync>().Pick(other, CarryMode.Back);
                        Log.Info("DEV host picks the client up");
                    }
                    _stage = 206; _stageAt = now + 1.5f;
                    break;
                case 206:
                    if (Coop.IsHost) { BodyFreeze.PlaceAt((Vector2)GameInfo.LocalBody.transform.position + new Vector2(3f, 0f)); Log.Info("DEV host walks 3 m with the client"); }
                    _stage = 207; _stageAt = now + 4f;
                    break;
                case 207:
                    Verify("carried");
                    if (Coop.IsHost)
                    {
                        Coop.Get<CarrySync>().PutDown();
                        WalkOver();
                        _cprUntil = now + 6f;  // starts once the client's heart has stopped (it's a little behind)
                        Log.Info("DEV host starts CPR");
                    }
                    if (Coop.IsClient)
                    {
                        _forceOut = false;
                        var b = GameInfo.LocalBody;
                        b.heartRate = 0f;
                        b.fibrillationProgress = 100f;
                        b.bloodPressure = 0f;
                        b.bloodOxygen = 60f;
                        Log.Info("DEV client's heart stops");
                    }
                    _stage = 208; _stageAt = now + 4f;
                    break;
                case 208:
                    Verify("cpr");
                    if (Coop.IsClient)
                    {
                        var b = GameInfo.LocalBody;
                        var rag = UnityEngine.Object.Instantiate(Resources.Load("rag") as GameObject, b.transform.position, Quaternion.identity).GetComponent<Item>();
                        for (int i = 0; i < b.slots.Length; i++)
                            if (!b.slots[i].isHand && i != 2 && b.GetItem(i) == null) { b.PickUpItem(rag, i, true); break; }
                        b.brainHealth = 0f;
                        Log.Info("DEV client pockets a rag and dies");
                    }
                    if (Coop.IsHost) GiveHand("aed");
                    _stage = 209; _stageAt = now + 3f;
                    _waitUntil = now + 12f;
                    break;
                case 209:
                    if (Coop.IsHost)
                    {
                        var vit = Coop.Get<VitalsSync>().Of(other);
                        if ((vit == null || (vit.Signs & BodySigns.Alive) != 0) && now < _waitUntil) return;  // wait for the death
                        WalkOver();
                        var loot = Coop.Get<LootSync>().Of(other);
                        Log.Info("DEV host searches the body: " + (loot == null ? "nothing listed" : loot.Count + " items"));
                        if (loot != null && loot.Count > 0) Coop.Get<LootSync>().Take(other, loot[0].Key);
                        _nextShock = now + 1f;
                    }
                    _stage = 210; _stageAt = now + 14f;
                    break;
                case 210:
                    if (Coop.IsHost) Log.Info("DEV host shocked " + _shocks + " times");
                    if (MedicSession.Active) MinigameBase.main.EndMinigame();
                    _stage = 211; _stageAt = now + 1f;
                    break;
            }
        }

        private static void WalkOverClient()
        {
            var r = Coop.Get<PlayerSync>().Find(NetSession.HostId);
            var me = GameInfo.LocalBody;
            if (r?.ShownAt == null || me == null) return;
            if (Vector2.Distance(me.transform.position, r.ShownAt.Value) > 1.5f)
                BodyFreeze.PlaceAt(r.ShownAt.Value + new Vector2(-0.9f, 0.3f));
        }

        private static string CareLine()
        {
            var sb = new System.Text.StringBuilder();
            if (Coop.IsHost)
            {
                var v = Coop.Get<VitalsSync>()?.Of(OtherPlayer());
                if (v == null) return "no vitals";
                var l3 = v.Limbs.Count > 3 ? v.Limbs[3] : default(LimbVitals);
                var held = Medic.HeldItem(GameInfo.LocalBody);
                sb.Append("client alive=").Append((v.Signs & BodySigns.Alive) != 0)
                  .Append(" conscious=").Append((v.Signs & BodySigns.Conscious) != 0)
                  .Append(" arrest=").Append((v.Signs & BodySigns.Arrest) != 0)
                  .Append(" pressure=").Append(v.Pressure).Append(" oxygen=").Append(v.Oxygen)
                  .Append(" | limb3 bleed=").Append(l3.Bleed).Append(" skin=").Append(l3.Skin).Append(" signs=").Append(l3.Signs)
                  .Append(" | held=").Append(held != null ? held.id + "@" + held.condition.ToString("F2") : "nothing")
                  .Append(" | mood=").Append(GameInfo.LocalBody.happiness.ToString("F1"))
                  .Append(" load=").Append(GameInfo.LocalBody.GetTotalEncumberance().ToString("F1"))
                  .Append(" | rag=").Append(GameInfo.LocalBody.FindByIdThorough("rag", out _));
                var r = Coop.Get<PlayerSync>()?.Find(OtherPlayer());
                if (r?.ShownAt != null) sb.Append(" | gap=").Append(Vector2.Distance(GameInfo.LocalBody.transform.position, r.ShownAt.Value).ToString("F1"));
            }
            else
            {
                var b = GameInfo.LocalBody;
                var limb = b.limbs[3];
                bool has = b.FindByIdThorough("adhesivebandage", out _);
                var host = Coop.Get<PlayerSync>()?.Find(NetSession.HostId);
                sb.Append("self alive=").Append(b.alive).Append(" conscious=").Append(b.conscious)
                  .Append(" pressure=").Append(b.bloodPressure.ToString("F0")).Append(" oxygen=").Append(b.bloodOxygen.ToString("F0"))
                  .Append(" brain=").Append(b.brainHealth.ToString("F1"))
                  .Append(" | limb3 bleed=").Append(limb.bleedAmount.ToString("F2")).Append(" bandage=").Append(limb.bandageSlowAmount.ToString("F1"))
                  .Append(" dislocated=").Append(limb.dislocated)
                  .Append(" | has adhesivebandage=").Append(has)
                  .Append(" | opiate=").Append((b.GetComponent<Painkillers>()?.opiateAmount ?? 0f).ToString("F1"))
                  .Append(" totalmood=").Append(b.totalHappiness.ToString("F0"))
                  .Append(" load=").Append(b.GetTotalEncumberance().ToString("F1"))
                  .Append(" climbing=").Append(b.currentClimbable != null)
                  .Append(" y=").Append(b.transform.position.y.ToString("F1"))
                  .Append(" mood=").Append(b.happiness.ToString("F1"));
                if (host?.ShownAt != null) sb.Append(" | gap=").Append(Vector2.Distance(b.transform.position, host.ShownAt.Value).ToString("F1"));
            }
            return sb.ToString();
        }

        private static void M3Actions()
        {
            if (Coop.IsHost)
            {
                // M5: a harmless shot straight down past the client (no damage, no wound):
                // the client must replay it.
                var client = Coop.Get<PlayerSync>()?.Remotes;
                foreach (var r in client ?? (IEnumerable<RemotePlayer>)new RemotePlayer[0])
                {
                    var shotAt = r.ShownAt;
                    if (!shotAt.HasValue) continue;
                    TurretScript.Shoot(new FireInfo
                    {
                        pos = shotAt.Value + new Vector2(1.5f, 4f), dir = Vector2.down, ignoreBody = true, doTinnitus = false,
                        structureDamage = 0f, animalDamage = 0f, playerDamageMultiplier = 0f,
                    });
                    Log.Info("DEV host fires a test shot past " + Coop.Player(r.Id).Name);
                    break;
                }
            }
            if (Coop.IsClient)
            {
                var chat = Coop.Get<Chat>();
                chat?.Send("hello from the client");
                Log.Info("DEV client said a chat line");

                Chat.DevOpen();
                Log.Info("VERIFY team inputblock left=" + KeyBinds.GetBind("left") + " jump=" + KeyBinds.GetBind("jump"));
                Chat.DevClose();
                Log.Info("VERIFY team inputfree left=" + KeyBinds.GetBind("left"));

                var host = Coop.Player(NetSession.HostId);
                Coop.Get<PingSync>()?.DevPing(host.Position + Vector2.up * 2f);
                Log.Info("DEV client pinged near the host");

                Log.Info("DEV client votes to descend");
                var w = WorldGeneration.world;
                w.StartCoroutine(w.RegenerateWorld(false));
                return;
            }

            // Host: put one of its own crawlers on the client's puppet.
            RemotePlayer target = null;
            foreach (var r in Coop.Get<PlayerSync>().Remotes) if (r.ShownAt.HasValue) { target = r; break; }
            if (target == null) { Log.Info("DEV host sees no client puppet for the bite test"); return; }
            SpiderHandler best = null;
            float bestDist = float.MaxValue;
            foreach (var h in UnityEngine.Object.FindObjectsOfType<SpiderHandler>())
            {
                if (CreatureSync.IsPuppetObject(h.gameObject)) continue;
                var bld = h.GetComponent<BuildingEntity>();
                if (bld != null && bld.health <= 0f) continue;
                float d = Vector2.Distance(h.transform.position, target.ShownAt.Value);
                if (d < bestDist) { bestDist = d; best = h; }
            }
            if (best == null) { Log.Info("DEV host has no crawler for the bite test"); return; }
            Vector2 at = target.LimbPosition(1) ?? target.ShownAt.Value;
            best.transform.position = at;
            var rb = best.GetComponent<Rigidbody2D>();
            if (rb != null) { rb.position = at; rb.velocity = Vector2.zero; }
            best.stunTime = 0f;
            CreatureHunt.BiteCooldown(best) = 0f;
            Log.Info("DEV host puts " + best.name + " on the client's puppet");
        }

        private static string TeamLine()
        {
            var cam = PlayerCamera.main;
            bool warming = false;
            var ps = Coop.Get<PlayerSync>();
            if (ps != null) foreach (var r in ps.Remotes) warming |= r.Warming;
            return "rules=" + HostRules.Current.Encode() + " chat=" + Chat.Count +
                   " pings=" + (Coop.Get<PingSync>()?.Count ?? 0) +
                   " exit=" + (Coop.Get<ExitVote>()?.Describe() ?? "-") +
                   " threat=" + (cam != null ? cam.threatMusicTime.ToString("F1") : "-") +
                   " warmth=" + warming + " voice=" + VoiceSync.Mode;
        }

        /// <summary>Holds the local body asleep (the game would wake a rested body at once).</summary>
        private void HoldAsleep()
        {
            if (!_forceSleep) return;
            var b = GameInfo.LocalBody;
            if (b == null || !b.alive) return;
            b.consciousness = Mathf.Min(b.consciousness, 10f);
            b.sleeping = true;
        }

        private static void Shot(string name)
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(Log.FilePath ?? "");
                if (string.IsNullOrEmpty(dir)) return;
                string path = System.IO.Path.Combine(dir, "shot-" + name + "-pid" + System.Diagnostics.Process.GetCurrentProcess().Id + ".png");
                ScreenCapture.CaptureScreenshot(path);
                Log.Info("DEV screenshot " + path);
            }
            catch (Exception e) { Log.Warn("screenshot failed: " + e.Message); }
        }

        private static string LifeLine()
        {
            var body = GameInfo.LocalBody;
            var cam = PlayerCamera.main;
            var life = Coop.Get<LifeSync>();
            var sb = new System.Text.StringBuilder();
            sb.Append("local alive=").Append(body != null && body.alive)
              .Append(" conscious=").Append(body != null && body.conscious)
              .Append(" spectating=").Append(LifeSync.Spectating)
              .Append(" watching=").Append(life?.TargetName ?? "-")
              .Append(" deathscreen=").Append(cam != null && cam.didDeathScreen)
              .Append(" black=").Append(cam != null ? cam.blackAmount.ToString("F2") : "-")
              .Append(" wiped=").Append(LifeSync.Wiped)
              .Append(" sharedsleep=").Append(ClockSync.SharedSleep)
              .Append(" rule=").Append(LifeSync.Rule)
              .Append(" | players");
            var ids = new List<byte>(Coop.Players.Keys);
            ids.Sort();
            foreach (var id in ids)
            {
                var r = Coop.Player(id);
                sb.Append(' ').Append(id).Append(':').Append(r.Alive ? "alive" : "dead").Append(r.Conscious ? "" : "/out");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Host drops whatever it holds (spawn path); client picks up the nearest loose
        /// item (host-arbitrated take path). Entity counts must move together.
        /// </summary>
        private static void ItemTest()
        {
            var body = GameInfo.LocalBody;
            if (body == null) return;
            if (Coop.IsHost)
            {
                // Items whose state lives in [Saveable] components: liquid and a gun.
                foreach (var idName in new[] { "waterbottle", "pistol" })
                {
                    var made = Utils.Create(idName, (Vector2)body.transform.position + Vector2.right * 2f, 0f);
                    Log.Info("DEV host made " + idName + (made != null ? "" : " (unknown id)"));
                }
                for (int i = 0; i < body.slots.Length; i++)
                {
                    var held = body.GetItem(i);
                    if (held == null) continue;
                    Log.Info("DEV host drops " + held.id + " from slot " + i);
                    body.DropItem(held);
                    return;
                }
                Log.Info("DEV host holds nothing to drop");
                return;
            }
            Item best = null;
            float bestDist = 60f;
            foreach (var kv in EntityRegistry.All)
            {
                var item = kv.Value != null ? kv.Value.GetComponent<Item>() : null;
                if (item == null || kv.Value.transform.parent != null) continue;
                float d = Vector2.Distance(item.transform.position, body.transform.position);
                if (d < bestDist) { bestDist = d; best = item; }
            }
            if (best == null) { Log.Info("DEV client found no loose item nearby"); return; }
            EntityRegistry.TryGetId(best.gameObject, out var id);
            Log.Info("DEV client picks up " + best.id + " (" + id + ") " + bestDist.ToString("F1") + " away");
            body.AutoPickUpItem(best);
        }

        /// <summary>
        /// Each side blows up a different object far from both players. The host's blast
        /// is authored directly; the client's goes through a request. Either way the
        /// world must end up the same on both machines.
        /// </summary>
        private static void ExplosionTest()
        {
            var ids = new List<uint>(EntityRegistry.Ids);
            ids.Sort();
            if (!Coop.IsHost) ids.Reverse();
            foreach (var id in ids)
            {
                if (!EntityRegistry.TryGet(id, out var go)) continue;
                var b = go.GetComponent<BuildingEntity>();
                if (b == null || b.animal || b.indestructible || b.health < 60f) continue;
                Vector2 at = go.transform.position;
                bool farFromPlayers = true;
                foreach (var rec in Coop.Players.Values)
                    if (rec.HasPosition && Vector2.Distance(rec.Position, at) < 40f) farFromPlayers = false;
                var body = GameInfo.LocalBody;
                if (body != null && Vector2.Distance(body.transform.position, at) < 40f) farFromPlayers = false;
                if (!farFromPlayers) continue;
                Log.Info("DEV " + (Coop.IsHost ? "host" : "client") + " explodes at " + id + " (" + EntityRegistry.KeyOf(id) + ")");
                WorldGeneration.CreateExplosion(new ExplosionParams { position = at });
                return;
            }
        }

        private void PollTrigger()
        {
            if (Time.unscaledTime < _nextTriggerPoll) return;
            _nextTriggerPoll = Time.unscaledTime + 0.5f;
            try
            {
                var path = Path.Combine(Path.Combine(Application.persistentDataPath, "CasualtiesCoop"), "verify.trigger");
                if (!File.Exists(path)) return;
                var stamp = File.GetLastWriteTimeUtc(path);
                if (stamp <= _lastTrigger) return;
                bool first = _lastTrigger == DateTime.MinValue;
                _lastTrigger = stamp;
                if (!first) Verify("trigger");
            }
            catch { /* best effort */ }
        }

        /// <summary>A fingerprint of the live world; equal lines on both peers mean the same world.</summary>
        /// <summary>A short hash of the current layer's terrain, as VERIFY prints it.</summary>
        internal static string TerrainHash()
        {
            var terrain = WorldAccess.CaptureTerrain();
            if (terrain == null) return "-";
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(terrain), 0, 8).Replace("-", "").ToLowerInvariant();
        }

        internal static void Verify(string tag)
        {
            if (!WorldAccess.Settled) { Log.Info("VERIFY " + tag + " no world"); return; }
            var terrain = WorldAccess.CaptureTerrain();
            string th;
            using (var sha = SHA256.Create()) th = BitConverter.ToString(sha.ComputeHash(terrain), 0, 8).Replace("-", "").ToLowerInvariant();
            int count = 0, runtime = 0;
            double health = 0;
            foreach (var kv in EntityRegistry.All)
            {
                if (kv.Value == null) continue;
                count++;
                if (EntityRegistry.IsRuntime(kv.Key)) runtime++;
                var b = kv.Value.GetComponent<BuildingEntity>();
                if (b != null && !b.animal) health += b.health;
            }
            var ts = Coop.Get<TerrainSync>();
            Log.Info("VERIFY " + tag + " " + (Coop.IsHost ? "host" : "client") + " epoch " + Coop.Epoch +
                     " depth " + (WorldGeneration.world.biomeDepth + 1) + " terrain " + th +
                     " entities " + count + " runtime " + runtime + " health " + health.ToString("F0") +
                     " timescale " + Time.timeScale.ToString("0.##") +
                     " blocks " + (ts != null ? ts.Sent + "/" + ts.Applied : "-"));
            var cs = Coop.Get<CreatureSync>();
            if (cs != null) Log.Info("VERIFY " + tag + " creatures " + cs.Describe());
            Log.Info("VERIFY " + tag + " items " + ItemsFingerprint() + " crystals " + CrystalsFingerprint());
            Log.Info("VERIFY " + tag + " life " + LifeLine());
            Log.Info("VERIFY " + tag + " team " + TeamLine());
            var dh = Coop.Get<DevHarness>();
            if (dh != null && dh._stage >= 200) Log.Info("VERIFY " + tag + " care " + CareLine());
            var ex = Coop.Get<ExplosionSync>();
            var es = Coop.Get<EntitySync>();
            Log.Info("VERIFY " + tag + " explosions authored/replayed/deduped " +
                     (ex != null ? ex.Authored + "/" + ex.Replayed + "/" + ex.Deduped : "-") +
                     " vanished " + (es != null ? es.Vanished : 0) + " gravity " + Physics2D.gravity.y.ToString("F1") +
                     " temp " + WorldGeneration.world.ambientTemperature.ToString("F1"));
            if (tag == "team") Log.Info("VERIFY " + tag + " scaling enemy strength " + SpiderHandler.strengthMult.ToString("F2") + " (run setting " +
                                         (WorldGeneration.runSettings != null && WorldGeneration.runSettings.TryGetValue("enemystrength", out var es0) ? es0 : "-") + ")");
            if (tag == "team" && WorldGeneration.runSettings != null)
                Log.Info("VERIFY " + tag + " runsettings " + string.Join(" | ", UI.RunSettingsReadout.Lines(WorldGeneration.runSettings).ToArray()));
            var we = Coop.Get<WorldEvents>();
            var ss = Coop.Get<ShotSync>();
            Log.Info("VERIFY " + tag + " world events sent/applied " + (we != null ? we.Sent + "/" + we.Applied : "-") +
                     " shots sent/replayed/deduped " + (ss != null ? ss.Sent + "/" + ss.Replayed + "/" + ss.Deduped : "-") +
                     " spawned/destroyed " + (es != null ? es.Spawned + "/" + es.Destroyed : "-") +
                     " desync checks/mismatched/repaired " + (Coop.Get<WorldCheck>() is WorldCheck wc ? wc.Checks + "/" + wc.Mismatched + "/" + wc.Repaired : "-") +
                     " milestones " + Milestones.Reached + " (carry " + Milestones.Count("carry").ToString("F0") + " catch " + Milestones.Count("catch") +
                     " legup " + Milestones.Count("legup") + " hug " + Milestones.Count("hug") + " treat " + Milestones.Count("treat") +
                     " revived " + Milestones.Count("revived") + " layer " + Milestones.Count("layer") + ")");
            var body = GameInfo.LocalBody;
            var ps = Coop.Get<PlayerSync>();
            if (ps != null && body != null)
                foreach (var r in ps.Remotes)
                    Log.Info("VERIFY " + tag + " puppet " + r.Describe() + " local body at " + ((Vector2)body.transform.position).ToString("F1"));
        }

        internal override void OnWorldLive()
        {
            // Stand the client a few steps from the host so its puppet is visible on its own.
            if (_selftest && Coop.IsClient && GameInfo.LocalBody != null)
                BodyFreeze.PlaceAt((Vector2)GameInfo.LocalBody.transform.position + new Vector2(4f, 0.5f));
        }

        /// <summary>Count and hash of loose runtime items and their full codec state.</summary>
        private static string ItemsFingerprint()
        {
            var parts = new List<string>();
            foreach (var kv in EntityRegistry.All)
            {
                if (kv.Value == null || !EntityRegistry.IsRuntime(kv.Key)) continue;
                var item = kv.Value.GetComponent<Item>();
                if (item == null) continue;
                var spec = ItemCodec.Decode(ItemCodec.Encode(item));
                if (spec == null) continue;
                var sb = new StringBuilder();
                sb.Append(kv.Key).Append(':').Append(spec.Id).Append(':').Append(spec.Condition.ToString("F1"));
                foreach (var c in spec.Components) sb.Append('|').Append(c.Key).Append('=').Append(c.Value);
                parts.Add(sb.ToString());
            }
            parts.Sort(StringComparer.Ordinal);
            string joined = string.Join(";", parts.ToArray());
            using (var sha = SHA256.Create())
                return parts.Count + ":" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(joined)), 0, 6).Replace("-", "").ToLowerInvariant();
        }

        private static readonly HarmonyLib.AccessTools.FieldRef<CrystalBehaviour, List<CrystalEffect>> CrystalEffects =
            HarmonyLib.AccessTools.FieldRefAccess<CrystalBehaviour, List<CrystalEffect>>("effects");

        /// <summary>Crystals with behaviour, and a hash of their effect types by network id.</summary>
        private static string CrystalsFingerprint()
        {
            var parts = new List<string>();
            int broken = 0;
            foreach (var kv in EntityRegistry.All)
            {
                var cb = kv.Value != null ? kv.Value.GetComponent<CrystalBehaviour>() : null;
                if (cb == null) continue;
                var fx = CrystalEffects(cb);
                if (fx == null) { broken++; continue; }
                var names = new List<string>();
                foreach (var e in fx) names.Add(e.GetType().Name);
                parts.Add(kv.Key + "=" + string.Join(",", names.ToArray()));
            }
            parts.Sort(StringComparer.Ordinal);
            using (var sha = SHA256.Create())
                return parts.Count + " ok, " + broken + " without effects, " +
                       BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join(";", parts.ToArray()))), 0, 6).Replace("-", "").ToLowerInvariant();
        }

        private static void DumpCharacter()
        {
            var body = GameInfo.LocalBody;
            var root = body.transform.root;
            var sb = new StringBuilder("DEV character hierarchy:\n");
            Walk(root, 0, sb);
            Log.Info(sb.ToString());
        }

        private static void Walk(Transform t, int depth, StringBuilder sb)
        {
            if (depth > 6) return;
            sb.Append(new string(' ', depth * 2)).Append(t.name)
              .Append(" lp=").Append(t.localPosition.ToString("F2"))
              .Append(" ls=").Append(t.localScale.ToString("F2")).Append(" [");
            foreach (var c in t.GetComponents<Component>()) if (c != null) sb.Append(c.GetType().Name).Append(' ');
            sb.Append("]\n");
            foreach (Transform child in t) Walk(child, depth + 1, sb);
        }
    }

    /// <summary>CCOOP_STARTDEPTH: the host's run starts on a chosen layer.</summary>
    [HarmonyLib.HarmonyPatch(typeof(WorldGeneration), "Start")]
    internal static class DevStartDepthPatch
    {
        private static void Prefix(WorldGeneration __instance)
        {
            if (DevHarness.StartLayer < 1 || !Coop.IsHost) return;
            __instance.debugStartDepth = Mathf.Clamp(DevHarness.StartLayer - 1, 0, 11);
            Log.Info("DEV starting on layer " + DevHarness.StartLayer);
        }
    }

    /// <summary>CCOOP_MODIFIER: the host's layer always gets this modifier.</summary>
    [HarmonyLib.HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.ApplyLayerModifiers))]
    internal static class DevForceModifierPatch
    {
        private static bool Prefix(WorldGeneration __instance)
        {
            if (DevHarness.ForceModifier < 0 || !Coop.IsHost) return true;
            var all = LayerModifier.availableModifiers;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null || all[i].modifierIndex != DevHarness.ForceModifier) continue;
                all[i].Initialize(__instance);
                all[i].active = true;
                Log.Info("DEV forced modifier " + all[i].GetType().Name + ", gravity now " + Physics2D.gravity.y.ToString("F1"));
                return false;
            }
            return true;
        }
    }
}
