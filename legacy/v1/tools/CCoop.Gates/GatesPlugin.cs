using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace CCoop.Gates
{
    /// <summary>
    /// M1 feasibility gates, second iteration.
    ///
    /// The first iteration asserted that two <c>Body</c> instances don't share state
    /// and reported 34/34 pass — but it never checked that the ORIGINAL player still
    /// worked. In play the original froze in a standing pose, stopped colliding
    /// properly with terrain, and lost dialogue. Reference independence was the wrong
    /// thing to measure.
    ///
    /// This version therefore:
    ///   * captures Unity engine errors (Application.logMessageReceived), which the
    ///     first version was blind to;
    ///   * samples the ORIGINAL body's behaviour before and after the experiment and
    ///     reports whether it is still animating and moving;
    ///   * defaults to a VISUAL-ONLY proxy — sprites with no Body, no physics and no
    ///     simulation — which is what a client-side remote player actually needs.
    ///     The full-Body clone is only required on the host, and is opt-in here.
    /// </summary>
    [BepInPlugin(Guid, "CCoop M1 Gates", "0.2.0")]
    public sealed class GatesPlugin : BaseUnityPlugin
    {
        public const string Guid = "ccoop.gates";

        private static GatesPlugin _self;

        private ConfigEntry<bool> _enabled;
        private ConfigEntry<string> _mode;
        private ConfigEntry<float> _offsetX;
        private ConfigEntry<float> _delaySeconds;

        internal static float OffsetX => _self?._offsetX.Value ?? 3f;
        internal static float Delay => _self?._delaySeconds.Value ?? 2.5f;
        internal static string Mode => _self?._mode.Value ?? "VisualOnly";

        private void Awake()
        {
            _self = this;

            _enabled = Config.Bind("Gates", "Enabled", false,
                "Run the M1 experiment after world generation. Mutates game state — leave false for normal play.");
            _mode = Config.Bind("Gates", "Mode", "VisualOnly",
                "VisualOnly = sprite-only proxy (no Body/physics/simulation), the client-side remote-player model. " +
                "FullBody = clone the entire Body incl. physics and simulation, the host-side model. " +
                "FullBody is known to disturb the local player; use deliberately.");
            _offsetX = Config.Bind("Gates", "CloneOffsetX", 3f, "Horizontal offset for the spawned proxy.");
            _delaySeconds = Config.Bind("Gates", "DelaySeconds", 2.5f, "Delay after generation before running.");

            // Always capture engine errors, even when the experiment is off: this is
            // the instrumentation whose absence hid the previous failure.
            ErrorSink.Install();

            if (!_enabled.Value)
            {
                Logger.LogInfo("M1 experiment disabled (Gates.Enabled=false). Error capture still active.");
                return;
            }

            var host = new GameObject("CCoop.Gates.Runner") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.AddComponent<GateRunner>();
            Logger.LogInfo("M1 experiment armed, mode=" + _mode.Value);
        }

        internal static void Say(string s) => _self?.Logger.LogInfo(s);
        internal static void Warn(string s) => _self?.Logger.LogWarning(s);

        private sealed class GateRunner : MonoBehaviour
        {
            private bool _wasGenerating, _done;

            private void Update()
            {
                if (_done) return;
                try
                {
                    var w = WorldGeneration.world;
                    if (w == null) { _wasGenerating = false; return; }
                    if (!w.generatingWorld && _wasGenerating) { _done = true; StartCoroutine(Run()); }
                    _wasGenerating = w.generatingWorld;
                }
                catch (Exception e) { Warn("watcher failed: " + e.Message); enabled = false; }
            }

            private IEnumerator Run()
            {
                yield return new WaitForSeconds(Delay);
                yield return Experiment.Run();
            }
        }
    }

    /// <summary>
    /// Captures Unity engine log messages (exceptions, errors, assertions). Without
    /// this, an exception storm inside a per-frame Update is invisible in the BepInEx
    /// log and the experiment looks like it passed.
    /// </summary>
    internal static class ErrorSink
    {
        private static readonly List<string> Errors = new List<string>();
        private static bool _installed;

        internal static void Install()
        {
            if (_installed) return;
            _installed = true;
            Application.logMessageReceived += OnLog;
        }

        private static void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            lock (Errors)
            {
                if (Errors.Count < 200)
                    Errors.Add(type + ": " + condition);
            }
        }

        internal static int Count { get { lock (Errors) return Errors.Count; } }

        internal static void Reset() { lock (Errors) Errors.Clear(); }

        internal static IEnumerable<string> Top(int n)
        {
            lock (Errors)
                return Errors.GroupBy(e => e)
                             .OrderByDescending(g => g.Count())
                             .Take(n)
                             .Select(g => "x" + g.Count() + "  " + g.Key)
                             .ToArray();
        }
    }

    /// <summary>A behavioural fingerprint of a body, used to detect "it stopped working".</summary>
    internal struct Vitals
    {
        public Vector2 RootPos;
        public Vector2[] LimbPos;
        public float Consciousness;
        public bool Alive;

        public static Vitals Of(Body b)
        {
            var v = new Vitals
            {
                RootPos = b.transform.position,
                Consciousness = b.consciousness,
                Alive = b.alive,
                LimbPos = new Vector2[b.limbs.Length]
            };
            for (int i = 0; i < b.limbs.Length; i++)
                v.LimbPos[i] = b.limbs[i] != null ? (Vector2)b.limbs[i].transform.position : Vector2.zero;
            return v;
        }

        /// <summary>
        /// Total limb movement relative to the body root. A live character is always
        /// slightly moving (breathing, pose lerp, IK); a frozen one reads ~0.
        /// </summary>
        public static float PoseMotion(Vitals a, Vitals b)
        {
            float sum = 0f;
            int n = Math.Min(a.LimbPos.Length, b.LimbPos.Length);
            for (int i = 0; i < n; i++)
                sum += Vector2.Distance(a.LimbPos[i] - a.RootPos, b.LimbPos[i] - b.RootPos);
            return sum;
        }
    }

    internal static class Experiment
    {
        private static readonly StringBuilder Out = new StringBuilder();
        private static int _pass, _fail;

        internal static IEnumerator Run()
        {
            Out.Length = 0; _pass = _fail = 0;
            Line("CCoop M1 experiment report (v2)");
            Line("generated : " + DateTime.Now.ToString("u"));
            Line("mode      : " + GatesPlugin.Mode);
            Line("");

            var original = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            if (original == null) { Line("ABORT: no local body."); Flush(); yield break; }

            // ---- baseline: is the original animating BEFORE we touch anything? ----
            ErrorSink.Reset();
            var b0 = Vitals.Of(original);
            yield return new WaitForSeconds(1.5f);
            var b1 = Vitals.Of(original);
            float baselineMotion = Vitals.PoseMotion(b0, b1);
            int baselineErrors = ErrorSink.Count;

            Line("=== baseline (no proxy yet, 1.5s sample) ===");
            Line("  pose motion    : " + baselineMotion.ToString("F4"));
            Line("  engine errors  : " + baselineErrors);
            Line("");

            // ---- create the proxy ------------------------------------------------
            ErrorSink.Reset();
            GameObject proxy = null;
            try
            {
                proxy = GatesPlugin.Mode == "FullBody"
                    ? MakeFullBodyClone(original)
                    : MakeVisualProxy(original);
            }
            catch (Exception e)
            {
                Check(false, "proxy creation did not throw — " + e.GetType().Name + ": " + e.Message);
            }

            Check(proxy != null, "proxy object created");
            if (proxy == null) { Summarise(); Flush(); yield break; }

            // ---- did we duplicate any view singleton? ----------------------------
            Line("");
            Line("=== singleton census after proxy ===");
            int cams = Count<Camera>(), listeners = Count<AudioListener>();
            Line("  Camera=" + cams + "  AudioListener=" + listeners +
                 "  Body=" + Count<Body>() + "  Limb=" + Count<Limb>());
            Check(cams == 1, "exactly one Camera still in scene");
            Check(listeners == 1, "exactly one AudioListener still in scene");
            Check(PlayerCamera.main.body == original, "PlayerCamera.main.body still the original");

            // ---- THE CHECK THAT WAS MISSING: is the original still alive+moving? --
            var a0 = Vitals.Of(original);
            yield return new WaitForSeconds(2.5f);
            var a1 = Vitals.Of(original);
            float afterMotion = Vitals.PoseMotion(a0, a1);
            int afterErrors = ErrorSink.Count;

            Line("");
            Line("=== scene census after settling (deferred destroys now applied) ===");
            int bodies2 = Count<Body>(), limbs2 = Count<Limb>();
            Line("  Camera=" + Count<Camera>() + "  AudioListener=" + Count<AudioListener>() +
                 "  Body=" + bodies2 + "  Limb=" + limbs2);
            if (GatesPlugin.Mode == "VisualOnly")
            {
                Check(bodies2 == 1, "still exactly ONE Body in scene (visual proxy adds none)");
                Check(limbs2 == 15, "still exactly 15 Limbs in scene (" + limbs2 + ")");
            }

            Line("");
            Line("=== original body AFTER proxy (2.5s sample) ===");
            Line("  pose motion    : " + afterMotion.ToString("F4") + "   (baseline " + baselineMotion.ToString("F4") + ")");
            Line("  engine errors  : " + afterErrors);
            Line("  alive          : " + a1.Alive);
            Line("  consciousness  : " + a1.Consciousness.ToString("F1"));

            // A frozen character is the exact symptom reported for FullBody mode.
            Check(afterErrors == 0, "no Unity errors/exceptions after creating the proxy");
            Check(a1.Alive, "original body still alive");
            Check(afterMotion > baselineMotion * 0.25f || afterMotion > 0.05f,
                  "original body is still animating (pose still changing)");

            if (afterErrors > 0)
            {
                Line("");
                Line("  top engine errors:");
                foreach (var e in ErrorSink.Top(8)) Line("    " + e);
            }

            Summarise();
            Flush();
        }

        // ---------------------------------------------------------------- proxies

        /// <summary>
        /// The client-side remote-player model: a sprite puppet. Strips Body, all
        /// simulation behaviours, all physics and all interaction hooks, so it cannot
        /// perturb the local player, the physics solver, or the game's systems.
        /// Position and pose will later be driven from network snapshots.
        /// </summary>
        private static GameObject MakeVisualProxy(Body original)
        {
            // Instantiate under an INACTIVE holder so no Awake/Start runs on the copy.
            // Cloning it live would let Body.Awake add an AudioSource, call
            // Physics2D.IgnoreCollision and start coroutines before we could strip it —
            // side effects that outlive the components themselves.
            var holder = new GameObject("CCoop_ProxyHolder");
            holder.SetActive(false);

            var go = UnityEngine.Object.Instantiate(original.gameObject, holder.transform);
            go.name = "CCoop_RemoteProxy";

            // Order matters: joints reference rigidbodies, so joints go first.
            // DestroyImmediate, not Destroy: Destroy is deferred to end-of-frame, so the
            // components would still exist (and be counted, and tick) this frame.
            DestroyAll<Joint2D>(go);
            DestroyAll<Rigidbody2D>(go);
            DestroyAll<Collider2D>(go);

            // Simulation + gameplay behaviours. A proxy must not metabolise, bleed,
            // talk, vomit, hold inventory, or be resolvable as a player.
            DestroyAll<Body>(go);
            DestroyAll<Limb>(go);
            DestroyAll<Talker>(go);
            DestroyAll<SelfHarmer>(go);
            DestroyAll<Vomiter>(go);
            DestroyAll<PantSound>(go);
            DestroyAll<LiquidAffect>(go);
            DestroyAll<InventorySlot>(go);
            DestroyAll<BleedParticle>(go);
            DestroyAll<FacialExpression>(go);
            DestroyAll<TailScript>(go);
            DestroyAll<AudioSource>(go);

            // Items were cloned along with the inventory slots; a fresh remote player
            // owns nothing until the host tells us otherwise.
            foreach (var item in go.GetComponentsInChildren<Item>(true))
                UnityEngine.Object.DestroyImmediate(item.gameObject);

            // Untag so nothing treats it as the player.
            foreach (var t in go.GetComponentsInChildren<Transform>(true))
                if (t.gameObject.CompareTag("Player")) t.gameObject.tag = "Untagged";

            // Verify the strip actually took, while still inactive and cheap to check.
            int residualBody = go.GetComponentsInChildren<Body>(true).Length;
            int residualLimb = go.GetComponentsInChildren<Limb>(true).Length;
            int residualPhys = go.GetComponentsInChildren<Rigidbody2D>(true).Length
                             + go.GetComponentsInChildren<Collider2D>(true).Length
                             + go.GetComponentsInChildren<Joint2D>(true).Length;
            int sprites = go.GetComponentsInChildren<SpriteRenderer>(true).Length;

            Check(residualBody == 0, "proxy has no Body component (" + residualBody + ")");
            Check(residualLimb == 0, "proxy has no Limb components (" + residualLimb + ")");
            Check(residualPhys == 0, "proxy has no physics components (" + residualPhys + ")");
            Check(sprites > 0, "proxy retains sprite renderers (" + sprites + ")");

            // Now place it in the world and bring it to life as a pure visual puppet.
            go.transform.SetParent(original.transform.parent, false);
            go.transform.position = original.transform.position + Vector3.right * GatesPlugin.OffsetX;
            go.transform.rotation = original.transform.rotation;
            go.SetActive(true);
            UnityEngine.Object.DestroyImmediate(holder);

            Line("  visual proxy: instantiated inactive, stripped, then activated");
            return go;
        }

        /// <summary>
        /// The host-side model: a complete second Body. Known to disturb the local
        /// player in this build; retained so the damage can be characterised rather
        /// than guessed at.
        /// </summary>
        private static GameObject MakeFullBodyClone(Body original)
        {
            // Clone the PLAYER ROOT (/Experiment), not just /Experiment/Body.
            //
            // The physics body is only one third of a player: the animation rig lives at
            // /Experiment/Anim and the speech text at /Experiment/TalkText, both OUTSIDE
            // the Body. Limbs follow the rig through Limb.animLimb, and Talker writes to
            // the text. Cloning Body alone copies those references verbatim, so the clone
            // drives the ORIGINAL's rig and text — which is precisely why the local player
            // froze mid-stride and lost dialogue.
            //
            // Cloning the root instead puts rig, text and body inside one copied
            // hierarchy, and Unity remaps internal references for us.
            var playerRoot = original.transform.parent != null
                ? original.transform.parent.gameObject
                : original.gameObject;

            // Instantiate INACTIVE, as M1 established: cloning a live hierarchy runs
            // Awake/Start before anything can be corrected, leaving residue behind.
            var holder = new GameObject("CCoop_BodyHolder");
            holder.SetActive(false);

            var rootClone = UnityEngine.Object.Instantiate(playerRoot, holder.transform);
            rootClone.name = "CCoop_RemotePlayer";

            var clonedBody = rootClone.GetComponentInChildren<Body>(true);
            if (clonedBody == null)
            {
                UnityEngine.Object.DestroyImmediate(holder);
                Check(false, "cloned player root contains a Body");
                return null;
            }

            var go = clonedBody.gameObject;
            Line("  cloned player root '" + playerRoot.name + "' (" +
                 playerRoot.transform.childCount + " children) rather than Body alone");

            // Diagnose BEFORE activating. Scanning the whole cloned ROOT is what matters:
            // any field still pointing outside it is an object the clone shares with the
            // original, and therefore a way for one to corrupt the other.
            var shared = ExternalReferenceScan.Scan(rootClone, playerRoot);
            Line("");
            Line("  -- shared external scene references on the clone --");
            Line(ExternalReferenceScan.Describe(shared).TrimEnd());
            Line("");
            Check(shared.Count == 0,
                  "clone shares no live scene objects with the original (" + shared.Count + " shared)");

            rootClone.transform.SetParent(playerRoot.transform.parent, false);
            rootClone.transform.position =
                playerRoot.transform.position + Vector3.right * GatesPlugin.OffsetX;
            rootClone.SetActive(true);
            UnityEngine.Object.DestroyImmediate(holder);

            foreach (var a in go.GetComponentsInChildren<AudioSource>(true))
            {
                a.Stop(); a.mute = true; a.enabled = false;
            }

            // Two ragdolls on the same layer have 15x15 un-ignored collision pairs,
            // because Body.Awake only ignores collisions among its OWN limbs.
            var clone = go.GetComponent<Body>();
            if (clone != null && clone.limbs != null && original.limbs != null)
            {
                int pairs = 0;
                foreach (var a in clone.limbs)
                foreach (var b in original.limbs)
                {
                    if (a == null || b == null) continue;
                    var ca = a.GetComponent<Collider2D>();
                    var cb = b.GetComponent<Collider2D>();
                    if (ca == null || cb == null) continue;
                    Physics2D.IgnoreCollision(ca, cb, true);
                    pairs++;
                }
                Line("  full-body clone: ignored " + pairs + " cross-body limb collision pairs");
            }

            return go;
        }

        /// <summary>
        /// Removes components synchronously. <c>Destroy</c> defers to end-of-frame,
        /// which would leave the components alive (and ticking, and counted) for the
        /// rest of the current frame — that artefact made an earlier report claim
        /// Body=2/Limb=30 after a supposed strip.
        /// </summary>
        private static void DestroyAll<T>(GameObject root) where T : Component
        {
            foreach (var c in root.GetComponentsInChildren<T>(true))
                if (c != null) UnityEngine.Object.DestroyImmediate(c);
        }

        // ---------------------------------------------------------------- reporting

        private static int Count<T>() where T : Component
        {
            try { return UnityEngine.Object.FindObjectsOfType<T>(true).Length; } catch { return -1; }
        }

        private static void Summarise()
        {
            Line("");
            Line("=================================================");
            Line(string.Format("RESULT: {0} passed, {1} failed", _pass, _fail));
            Line(_fail == 0
                ? "VERDICT: this proxy mode is safe for the local player."
                : "VERDICT: this proxy mode DISTURBS the local player. Do not ship it.");
        }

        private static void Check(bool ok, string what)
        {
            if (ok) _pass++; else _fail++;
            Line((ok ? "  [PASS] " : "  [FAIL] ") + what);
        }

        private static void Line(string s)
        {
            Out.AppendLine(s);
            if (s.Contains("[FAIL]")) GatesPlugin.Warn(s.Trim());
            else if (s.Length > 0) GatesPlugin.Say(s.TrimEnd());
        }

        private static void Flush()
        {
            try
            {
                var dir = Path.Combine(Application.persistentDataPath, "CCoop");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, string.Format("gate2-{0}-{1:yyyyMMdd-HHmmss}.txt",
                    GatesPlugin.Mode, DateTime.Now));
                File.WriteAllText(path, Out.ToString());
                GatesPlugin.Say("[gates] report written: " + path);
            }
            catch (Exception e) { GatesPlugin.Warn("[gates] report write failed: " + e.Message); }
        }
    }
}
