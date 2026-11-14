using System;
using System.Collections;
using System.Collections.Generic;
using CCoop.Net.Session;
using CCoop.Plugin.Protocol;
using CCoop.Plugin.Runtime;
using CCoop.Plugin.World;
using HarmonyLib;
using UnityEngine;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// Keeps everyone in the same run and on the same layer.
    ///
    /// The host's game drives: when it builds a layer it announces a new epoch, and
    /// clients follow by building an empty scaffold for that layer (running every
    /// presentation step of generation — backdrop, music, lighting, starting supplies —
    /// but none of the content steps), then wait for the host's snapshot of the real
    /// content. There is no world seed, so content can only ever come from the host.
    ///
    /// Clients never descend on their own. Reaching an exit or using a drill pod asks
    /// the host, whose descent then takes everybody.
    /// </summary>
    internal sealed class RunFlow : CoopModule
    {
        /// <summary>Client: generation is building a scaffold; content steps are skipped.</summary>
        internal static bool ClientGenerating;

        /// <summary>Client: our own call to RegenerateWorld, which must not be redirected.</summary>
        internal static bool BypassRegen;

        /// <summary>Client: the layer depth the next scaffold must be built at.</summary>
        internal static int TargetDepth = -1;

        private RunInfo _hostRun;
        private bool _autoJoined;
        private ushort _builtEpoch;
        private bool _wasInRun;
        private HashSet<int> _preGenRoots;
        private float _regenCooldown;

        internal bool HostInRun => _hostRun != null && _hostRun.InRun;
        internal bool CanJoinHostRun => Coop.IsClient && HostInRun && GameInfo.OnMainMenu;

        internal override void Attach(NetSession s)
        {
            s.On<RunInfo>(OnRunInfo);
            s.On<LayerBegin>(OnLayerBegin);
            s.On<LayerRequest>(OnLayerRequest);
            s.On<LayerStatus>(OnLayerStatus);
            _wasInRun = GameInfo.InRunScene;
        }

        internal override void Detach()
        {
            _hostRun = null;
            _autoJoined = false;
            _builtEpoch = 0;
            ClientGenerating = false;
            TargetDepth = -1;
            BodyFreeze.Set(false);
        }

        internal string Describe()
        {
            if (Coop.Session == null) return "";
            if (Coop.IsHost)
            {
                if (!GameInfo.InRunScene) return "In the menu. Start a run when everyone has joined.";
                var w = WorldGeneration.world;
                return Coop.WorldLive ? "Layer " + (w.biomeDepth + 1) + " · players join straight into your run" : "Building the layer...";
            }
            if (!Coop.InSession) return "Handshaking...";
            if (!HostInRun) return "Waiting for the host to start a run.";
            if (GameInfo.OnMainMenu) return "The host is in a run.";
            if (Coop.WorldLive) return "Layer " + (WorldGeneration.world.biomeDepth + 1);
            if (LayerSync.Applying) return "Receiving the host's world... " + Mathf.RoundToInt(LayerSync.Progress * 100f) + "%";
            return "Loading the layer...";
        }

        // ------------------------------------------------------------------ host

        internal RunInfo BuildRunInfo()
        {
            var info = new RunInfo
            {
                InRun = GameInfo.InRunScene && WorldGeneration.world.biomeOverride == WorldGeneration.OverrideSceneType.None,
                Epoch = Coop.Epoch,
                Depth = WorldGeneration.world != null ? WorldGeneration.world.biomeDepth : 0,
                RunId = CoopSaves.RunId ?? "",
            };
            var settings = WorldGeneration.runSettings;
            if (settings != null)
            {
                foreach (var kv in settings)
                {
                    var v = new RunSettingValue { Key = kv.Key };
                    switch (kv.Value)
                    {
                        case float f: v.Kind = 0; v.F = f; break;
                        case double d: v.Kind = 0; v.F = (float)d; break;
                        case int i: v.Kind = 1; v.I = i; break;
                        case long l: v.Kind = 1; v.I = (int)l; break;
                        case bool b: v.Kind = 2; v.I = b ? 1 : 0; break;
                        default: v.Kind = 3; v.S = kv.Value?.ToString() ?? ""; break;
                    }
                    info.Settings.Add(v);
                }
            }
            return info;
        }

        internal override void OnPeerJoined(PeerInfo peer)
        {
            if (Coop.IsHost) Session.SendTo(peer.Id, BuildRunInfo());
        }

        /// <summary>Host: InstantiateWorld is starting a new layer.</summary>
        internal static void HostLayerStarting(WorldGeneration world)
        {
            if (!Coop.IsHost) return;
            Coop.SetWorldLive(false);
            Coop.Epoch++;
            foreach (var rec in Coop.Players.Values)
            {
                if (rec.Id == NetSession.HostId) continue;
                rec.WorldEpoch = 0;
                rec.InWorld = false;
            }
            int depth = Mathf.Min(world.biomeDepth, world.amountOfLayers - 1);
            var flow = Coop.Get<RunFlow>();
            Coop.Session.Broadcast(flow.BuildRunInfo());
            Coop.Session.Broadcast(new LayerBegin { Epoch = Coop.Epoch, Depth = depth, Pod = ExitVote.PodDescent });
            ExitVote.PodDescent = false;
            Log.Info("[run] building layer " + (depth + 1) + " as epoch " + Coop.Epoch);
        }

        private void OnLayerRequest(byte origin, LayerRequest m)
        {
            Coop.Get<ExitVote>()?.HostVote(origin, m);
        }

        private void OnLayerStatus(byte origin, LayerStatus m)
        {
            if (!Coop.IsHost) return;
            var rec = Coop.Player(origin);
            rec.ReportedEpoch = m.Epoch;
            switch (m.Phase)
            {
                case LayerPhase.InMenu:
                    rec.InWorld = false;
                    rec.WorldEpoch = 0;
                    rec.WaitingForEpoch = 0;
                    rec.Status = "menu";
                    break;
                case LayerPhase.Loading:
                    rec.InWorld = false;
                    rec.Status = "loading";
                    break;
                case LayerPhase.Waiting:
                    rec.InWorld = false;
                    rec.Status = "loading";
                    if (m.Epoch == Coop.Epoch && Coop.WorldLive) Coop.Get<LayerSync>().SendSnapshot(origin);
                    else rec.WaitingForEpoch = m.Epoch;
                    break;
                case LayerPhase.Applied:
                    rec.InWorld = m.Epoch == Coop.Epoch && rec.WorldEpoch == m.Epoch;
                    rec.Status = rec.InWorld ? "in game" : "loading";
                    Log.Info("[run] " + rec.Name + " is in the layer (" + m.Detail + ")");
                    if (rec.InWorld && Coop.Get<LifeSync>()?.HostPeerEnteredLayer(origin) != true)
                        Coop.Get<SpawnSpread>()?.HostPeerEnteredLayer(origin);
                    break;
                case LayerPhase.Failed:
                    rec.InWorld = false;
                    rec.Status = "failed";
                    Log.Warn("[run] " + rec.Name + " could not load the layer: " + m.Detail);
                    break;
            }
        }

        // ------------------------------------------------------------------ client

        private void OnRunInfo(byte origin, RunInfo m)
        {
            if (Coop.IsHost) return;
            bool hostWasInRun = HostInRun;
            _hostRun = m;
            if (!string.IsNullOrEmpty(m.RunId)) CoopSaves.RunId = m.RunId;
            if (m.Epoch > Coop.Epoch || !m.InRun) Coop.Epoch = m.Epoch;
            TargetDepth = m.Depth;

            if (!m.InRun && hostWasInRun && GameInfo.InRunScene)
            {
                CoopRuntime.Notice("The host left the run");
                ReturnToMenu();
            }
        }

        private void OnLayerBegin(byte origin, LayerBegin m)
        {
            if (Coop.IsHost) return;
            Coop.Epoch = m.Epoch;
            TargetDepth = m.Depth;
            if (!GameInfo.InRunScene) return;
            // Everyone was in the drill pod: everyone gets its after-effects.
            if (m.Pod && WorldGeneration.world != null) WorldGeneration.world.doPod = true;
            Coop.SetWorldLive(false);
            WorldNet.Clear();
            if (!WorldAccess.Regenerating && !ClientGenerating) StartClientRegen();
        }

        internal void JoinHostRun()
        {
            if (!CanJoinHostRun) return;
            var pre = PreRunScript.instance;
            if (pre == null) return;
            _autoJoined = true;
            try
            {
                WorldGeneration.runSettings = ToSettings(_hostRun.Settings);
                SaveSystem.loadedRun = false;
                TargetDepth = _hostRun.Depth;
                // Rejoining a run we've played before: bring our own character back.
                int saved = string.IsNullOrEmpty(_hostRun.RunId) ? -1 : CoopSaves.PrepareLoad(_hostRun.RunId);
                if (saved >= 0)
                {
                    PreRunScript.characterSelected = saved;
                    CoopRuntime.Notice("Welcome back: your character from this run is restored");
                }
                Log.Info("[run] joining the host's run on layer " + (TargetDepth + 1) + " as " + GameInfo.CharacterName(PreRunScript.characterSelected));
                pre.StartCoroutine(pre.WaitLoad());
                Session.SendToHost(new LayerStatus { Epoch = Coop.Epoch, Phase = LayerPhase.Loading });
            }
            catch (Exception e)
            {
                Log.Error("[run] could not start the run: " + e);
                CoopRuntime.Notice("Could not join the run: " + e.Message);
            }
        }

        private static Dictionary<string, object> ToSettings(List<RunSettingValue> list)
        {
            var d = new Dictionary<string, object>();
            foreach (var s in list)
            {
                switch (s.Kind)
                {
                    case 0: d[s.Key] = s.F; break;
                    case 1: d[s.Key] = s.I; break;
                    case 2: d[s.Key] = s.I != 0; break;
                    default: d[s.Key] = s.S; break;
                }
            }
            return d;
        }

        private void StartClientRegen()
        {
            var w = WorldGeneration.world;
            if (w == null || Time.unscaledTime < _regenCooldown) return;
            _regenCooldown = Time.unscaledTime + 2f;
            Log.Info("[run] following the host to layer " + (TargetDepth + 1));
            BypassRegen = true;
            try { w.StartCoroutine(w.RegenerateWorld(false)); }
            finally { BypassRegen = false; }
        }

        internal static void ReturnToMenu()
        {
            try
            {
                HostLeaveGuardPatch.Bypass = true;
                if (PlayerCamera.main != null) PlayerCamera.main.ToMainMenu();
            }
            catch (Exception e) { Log.Warn("[run] could not return to the menu: " + e.Message); }
            finally { HostLeaveGuardPatch.Bypass = false; }
        }

        /// <summary>Client: InstantiateWorld is starting; build a scaffold for the host's layer.</summary>
        internal void ClientLayerStarting(WorldGeneration world)
        {
            if (TargetDepth >= 0) world.biomeDepth = TargetDepth;
            _builtEpoch = Coop.Epoch;
            ClientGenerating = true;
            Coop.SetWorldLive(false);
            WorldNet.Clear();
            EntityRegistry.Clear();
            _preGenRoots = new HashSet<int>();
            foreach (var go in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
                _preGenRoots.Add(go.GetInstanceID());
            BodyFreeze.Set(true);
            Session?.SendToHost(new LayerStatus { Epoch = _builtEpoch, Phase = LayerPhase.Loading });
            Log.Info("[run] building an empty scaffold for layer " + (world.biomeDepth + 1) + " (epoch " + _builtEpoch + ")");
        }

        internal override void Tick()
        {
            if (Coop.IsHost) HostTick();
            else if (Coop.IsClient) ClientTick();
        }

        private bool _hostWasInRun;

        private void HostTick()
        {
            bool inRun = GameInfo.InRunScene;
            if (inRun != _hostWasInRun)
            {
                _hostWasInRun = inRun;
                if (!inRun) Coop.SetWorldLive(false);
                Session.Broadcast(BuildRunInfo());
            }
            if (!inRun || Coop.WorldLive) return;
            if (!WorldAccess.Settled || WorldAccess.Regenerating) return;
            if (WorldGeneration.world.biomeOverride != WorldGeneration.OverrideSceneType.None) return;

            // Hosting started mid-run: the layer predates the session.
            if (Coop.Epoch == 0)
            {
                Coop.Epoch = 1;
                Session.Broadcast(new LayerBegin { Epoch = Coop.Epoch, Depth = WorldGeneration.world.biomeDepth });
            }
            Coop.Get<LayerSync>().HostRegisterWorld();
            Coop.SetWorldLive(true);
            Session.Broadcast(BuildRunInfo());
            foreach (var rec in Coop.Players.Values)
            {
                if (rec.Id == NetSession.HostId || rec.WaitingForEpoch != Coop.Epoch) continue;
                rec.WaitingForEpoch = 0;
                Coop.Get<LayerSync>().SendSnapshot(rec.Id);
            }
        }

        private void ClientTick()
        {
            bool inRun = GameInfo.InRunScene;
            if (_wasInRun && !inRun)
            {
                Coop.SetWorldLive(false);
                ClientGenerating = false;
                BodyFreeze.Set(false);
                Session.SendToHost(new LayerStatus { Epoch = Coop.Epoch, Phase = LayerPhase.InMenu });
            }
            _wasInRun = inRun;

            if (!inRun)
            {
                if (!_autoJoined && CanJoinHostRun) JoinHostRun();
                return;
            }

            if (ClientGenerating)
            {
                if (!WorldAccess.Settled || WorldAccess.Regenerating) return;
                ClientGenerating = false;
                int removed = RemoveGeneratedContent();
                WorldNet.BeginAwaiting();
                Log.Info("[run] scaffold ready (" + removed + " local objects removed); waiting for the host's world");
                Session.SendToHost(new LayerStatus { Epoch = _builtEpoch, Phase = LayerPhase.Waiting });
                return;
            }

            // Out of step with the host (joined mid-run, or a descent arrived while
            // loading): rebuild for the current epoch.
            if (!Coop.WorldLive && !LayerSync.Applying && WorldAccess.Settled && !WorldAccess.Regenerating &&
                HostInRun && Coop.Epoch != 0 && _builtEpoch != Coop.Epoch)
                StartClientRegen();
        }

        /// <summary>
        /// Destroys world content the client's own generation made despite the content
        /// steps being skipped (the lifepod at layer 1, seasonal props, drill pod wrecks).
        /// The host's versions arrive in the snapshot.
        /// </summary>
        private int RemoveGeneratedContent()
        {
            if (_preGenRoots == null) return 0;
            int removed = 0;
            foreach (var go in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (go == null || _preGenRoots.Contains(go.GetInstanceID())) continue;
                if (WorldAccess.IsPlayerObject(go) || go.name.StartsWith(WorldAccess.SpawnedPrefix, StringComparison.Ordinal)) continue;
                if (go.GetComponentInChildren<Item>(true) == null && go.GetComponentInChildren<BuildingEntity>(true) == null &&
                    go.GetComponentInChildren<Collider2D>(true) == null && go.GetComponentInChildren<UnityEngine.Tilemaps.Tilemap>(true) == null)
                    continue;
                UnityEngine.Object.Destroy(go);
                removed++;
            }
            _preGenRoots = null;
            return removed;
        }

        internal void ClientLayerApplied(ushort epoch, bool ok, string detail)
        {
            Session?.SendToHost(new LayerStatus { Epoch = epoch, Phase = ok ? LayerPhase.Applied : LayerPhase.Failed, Detail = detail ?? "" });
        }
    }

    /// <summary>Holds the local ragdoll still while a client's world is empty.</summary>
    internal static class BodyFreeze
    {
        private static bool _frozen;

        internal static void Set(bool frozen)
        {
            var body = GameInfo.LocalBody;
            if (body == null) { _frozen = false; return; }
            if (_frozen == frozen) return;
            _frozen = frozen;
            foreach (var rb in body.transform.root.GetComponentsInChildren<Rigidbody2D>(true))
            {
                if (rb == null) continue;
                rb.velocity = Vector2.zero;
                rb.angularVelocity = 0f;
                rb.isKinematic = frozen;
            }
        }

        internal static void PlaceAt(Vector2 pos)
        {
            var body = GameInfo.LocalBody;
            if (body == null) return;
            var bodyT = body.transform;
            var delta = (Vector3)pos - bodyT.position;
            delta.z = 0;
            // Limbs are children of Body: moving Body carries them. Only rigidbodies
            // outside that hierarchy need moving themselves, or they would move twice.
            bodyT.position += delta;
            foreach (var rb in bodyT.root.GetComponentsInChildren<Rigidbody2D>(true))
            {
                if (rb.transform != bodyT && !rb.transform.IsChildOf(bodyT)) rb.transform.position += delta;
                rb.velocity = Vector2.zero;
                rb.angularVelocity = 0f;
            }
            Physics2D.SyncTransforms();
            if (PlayerCamera.main != null)
                PlayerCamera.main.transform.position = new Vector3(pos.x, pos.y, PlayerCamera.main.transform.position.z);
        }
    }

    // ====================================================================== patches

    [HarmonyPatch(typeof(WorldGeneration), "InstantiateWorld")]
    internal static class InstantiateWorldPatch
    {
        private static void Prefix(WorldGeneration __instance)
        {
            try
            {
                StructureRecorder.Clear();
                if (Coop.IsHost) RunFlow.HostLayerStarting(__instance);
                else if (Coop.IsClient) Coop.Get<RunFlow>()?.ClientLayerStarting(__instance);
            }
            catch (Exception e) { Log.Error("InstantiateWorld hook: " + e); }
        }
    }

    /// <summary>Clients never descend on their own: they ask the host.</summary>
    [HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.RegenerateWorld))]
    internal static class RegenerateWorldPatch
    {
        private static bool Prefix(bool twice, ref IEnumerator __result)
        {
            // The old layer stops being shared the moment a descent starts: RegenerateWorld
            // darkens the screen and destroys every object before the new layer begins, and
            // none of that may be published as gameplay.
            if ((Coop.IsHost && (ExitVote.HostDescending || !Coop.WorldLive)) || (Coop.IsClient && RunFlow.BypassRegen))
            {
                if (Coop.WorldLive) Coop.SetWorldLive(false);
                return true;
            }
            if (!Coop.InSession) return true;
            // Any way down (exit, drill pod, cargo pod, cutscene) is a vote; the host
            // descends once enough of the team has voted, and takes everyone.
            var vote = Coop.Get<ExitVote>();
            if (Coop.WorldLive && vote != null && vote.LocalWantsToDescend(twice))
            {
                Coop.SetWorldLive(false);
                return true;
            }
            __result = Skip.Empty();
            return false;
        }
    }

    internal static class Skip
    {
        internal static IEnumerator Empty() { yield break; }
        internal static bool Content => RunFlow.ClientGenerating && Coop.IsClient;
    }

    [HarmonyPatch(typeof(WorldGeneration), "WorldGenerateTerrain")]
    internal static class SkipTerrain
    {
        private static bool Prefix(ref IEnumerator __result) { if (!Skip.Content) return true; __result = Skip.Empty(); return false; }
    }

    [HarmonyPatch(typeof(WorldGeneration), "WorldGenerateWorldBorders")]
    internal static class SkipBorders
    {
        private static bool Prefix(ref IEnumerator __result) { if (!Skip.Content) return true; __result = Skip.Empty(); return false; }
    }

    [HarmonyPatch(typeof(WorldGeneration), "WorldPlaceEntities")]
    internal static class SkipEntities
    {
        private static bool Prefix(ref IEnumerator __result) { if (!Skip.Content) return true; __result = Skip.Empty(); return false; }
    }

    [HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.WorldPlaceChip))]
    internal static class SkipChip
    {
        private static bool Prefix(ref IEnumerator __result) { if (!Skip.Content) return true; __result = Skip.Empty(); return false; }
    }

    [HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.DistributeMiniBarrels))]
    internal static class SkipMiniBarrels
    {
        private static bool Prefix() => !Skip.Content;
    }

    [HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.ApplyLayerModifiers))]
    internal static class SkipModifierRoll
    {
        private static bool Prefix() => !Skip.Content;
    }

    [HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.GenerateEntityAtPos))]
    internal static class StructurePlacementPatch
    {
        /// <summary>
        /// The game's method, line for line, plus a record of what it created. The original
        /// gives no way to find the parts it instantiates at the scene root (an End lab's
        /// whole interior is one of them), so it is replaced rather than observed.
        /// Client scaffolds skip it: the host's parts arrive in the snapshot.
        /// </summary>
        private static bool Prefix(WorldGeneration __instance, Vector2 pos, GameObject basObj)
        {
            if (Skip.Content || basObj == null) return false;
            var created = new List<GameObject>();
            Vector2 at = pos - Vector2.one * 0.5f;
            foreach (Transform child in basObj.transform)
            {
                if (!StructureRecorder.Spawns(child)) continue;
                var go = UnityEngine.Object.Instantiate(child.gameObject, at + (Vector2)child.localPosition, child.localRotation);
                if (go.GetComponent<UnityEngine.Tilemaps.Tilemap>() != null) go.transform.SetParent(__instance.worldGrid.transform);
                created.Add(go);
            }
            StructureRecorder.Record(basObj.name, pos, created);
            return false;
        }
    }

    /// <summary>
    /// Structure placements made during generation, with every part each one created.
    /// Parts are networked by (placement, part index), and a client rebuilds each from the
    /// same child of the same structure prefab — parts are sub-objects of a prefab and have
    /// no prefab of their own to load by name.
    /// </summary>
    internal static class StructureRecorder
    {
        /// <summary>The children GenerateEntityAtPos instantiates.</summary>
        internal static bool Spawns(Transform child) =>
            child.GetComponent<Item>() != null || child.GetComponent<BuildingEntity>() != null || child.gameObject.name == "DOSPAWN";

        /// <summary>The <paramref name="index"/>-th spawning child of a structure prefab.</summary>
        internal static GameObject Part(GameObject prefab, int index)
        {
            int c = 0;
            foreach (Transform child in prefab.transform)
            {
                if (!Spawns(child)) continue;
                if (c == index) return child.gameObject;
                c++;
            }
            return null;
        }

        internal sealed class Placement
        {
            public string Prefab;
            public Vector2 Pos;
            public List<GameObject> Children;
        }

        internal static readonly List<Placement> Placements = new List<Placement>();

        internal static void Clear() => Placements.Clear();

        internal static void Record(string prefab, Vector2 pos, List<GameObject> children)
        {
            Placements.Add(new Placement { Prefab = prefab, Pos = pos, Children = children });
        }
    }
}
