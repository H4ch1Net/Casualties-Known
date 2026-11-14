using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;

namespace CCoop.Probe
{
    /// <summary>
    /// Read-only structural dump of the live player <c>Body</c> hierarchy.
    ///
    /// M1 gates 1–2 hinge on whether a second <c>Body</c> can be instantiated by
    /// cloning the live one (there is no player prefab — <c>WorldPlacePlayer</c> only
    /// repositions the existing scene object). To clone it we must know exactly which
    /// components are singleton-flavoured or view-only and therefore have to be
    /// stripped from the copy: cameras, audio listeners, canvases, event systems, and
    /// anything holding a back-reference to <c>PlayerCamera.main</c>.
    ///
    /// Writes a standalone report file so the output stays readable and does not
    /// swamp the main probe log.
    /// </summary>
    internal static class BodyReport
    {
        private static bool _written;

        /// <summary>Component types that make a clone unsafe until removed.</summary>
        private static readonly string[] SingletonRisk =
        {
            "Camera", "AudioListener", "Canvas", "EventSystem", "CinemachineBrain",
            "PlayerCamera", "PauseHandler", "WoundView", "MoodleManager", "ConsoleScript",
            "UniversalAdditionalCameraData", "AudioSource", "Light"
        };

        internal static void WriteOnce(WorldGeneration world)
        {
            if (_written) return;
            _written = true;

            try
            {
                var report = Build(world);

                var dir = Path.Combine(Application.persistentDataPath, "CCoop");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir,
                    string.Format("body-report-{0:yyyyMMdd-HHmmss}.txt", DateTime.Now));
                File.WriteAllText(path, report);

                ProbePlugin.Announce("[body] structural report written: " + path);
            }
            catch (Exception e)
            {
                ProbePlugin.AnnounceWarning("[body] report failed: " + e);
            }
        }

        private static string Build(WorldGeneration world)
        {
            var sb = new StringBuilder();
            var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;

            sb.AppendLine("CCoop Body structural report");
            sb.AppendLine("generated: " + DateTime.Now.ToString("u"));
            sb.AppendLine("layer (biomeDepth): " + (world != null ? world.biomeDepth.ToString() : "?"));
            sb.AppendLine();

            if (body == null)
            {
                sb.AppendLine("PlayerCamera.main.body is null — nothing to report.");
                return sb.ToString();
            }

            // ---- scene-wide singleton census -------------------------------------
            sb.AppendLine("=== scene singleton census ===");
            AppendCensus<Camera>(sb, "Camera");
            AppendCensus<AudioListener>(sb, "AudioListener");
            AppendCensus<Canvas>(sb, "Canvas");
            AppendCensus<EventSystem>(sb, "EventSystem");
            AppendCensus<Body>(sb, "Body");
            AppendCensus<Limb>(sb, "Limb");
            sb.AppendLine();

            // ---- where does PlayerCamera live relative to Body? ------------------
            sb.AppendLine("=== PlayerCamera.main ===");
            var pc = PlayerCamera.main;
            sb.AppendLine("  gameObject : " + PathOf(pc.transform));
            sb.AppendLine("  components : " + string.Join(", ", ComponentNames(pc.gameObject)));
            sb.AppendLine("  is PlayerCamera under Body? " +
                          (IsDescendantOf(pc.transform, body.transform) ? "YES" : "no"));
            sb.AppendLine("  is Body under PlayerCamera? " +
                          (IsDescendantOf(body.transform, pc.transform) ? "YES" : "no"));
            sb.AppendLine();

            // ---- Body root -------------------------------------------------------
            sb.AppendLine("=== Body root ===");
            sb.AppendLine("  path       : " + PathOf(body.transform));
            sb.AppendLine("  active     : " + body.gameObject.activeInHierarchy);
            sb.AppendLine("  tag/layer  : " + body.gameObject.tag + " / " +
                          LayerMask.LayerToName(body.gameObject.layer));
            sb.AppendLine("  root       : " + PathOf(body.transform.root));
            sb.AppendLine("  childCount : " + body.transform.childCount);
            sb.AppendLine("  components : " + string.Join(", ", ComponentNames(body.gameObject)));
            sb.AppendLine();

            // ---- inventory + limbs shape ----------------------------------------
            sb.AppendLine("=== inventory / anatomy shape ===");
            Safe(sb, "slots.Length", () => body.slots.Length.ToString());
            Safe(sb, "limbs.Length", () => body.limbs.Length.ToString());
            sb.AppendLine();

            sb.AppendLine("=== limbs ===");
            try
            {
                for (int i = 0; i < body.limbs.Length; i++)
                {
                    var limb = body.limbs[i];
                    if (limb == null) { sb.AppendLine("  [" + i + "] <null>"); continue; }
                    sb.AppendLine("  [" + i + "] " + limb.name +
                                  "  path=" + PathOf(limb.transform) +
                                  "  sameBody=" + (limb.body == body));
                    sb.AppendLine("        components: " + string.Join(", ", ComponentNames(limb.gameObject)));
                }
            }
            catch (Exception e) { sb.AppendLine("  <limb enumeration failed: " + e.Message + ">"); }
            sb.AppendLine();

            // ---- singleton-risk components inside the Body subtree ---------------
            sb.AppendLine("=== singleton-risk components inside Body subtree ===");
            sb.AppendLine("(these must be removed or disabled on a cloned body)");
            var risks = new List<string>();
            foreach (var c in body.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                var n = c.GetType().Name;
                if (SingletonRisk.Contains(n))
                    risks.Add("  " + n + "  @ " + PathOf(c.transform));
            }
            if (risks.Count == 0) sb.AppendLine("  none found (clone is structurally clean)");
            else foreach (var r in risks.Distinct().OrderBy(x => x, StringComparer.Ordinal)) sb.AppendLine(r);
            sb.AppendLine();

            // ---- full subtree ----------------------------------------------------
            sb.AppendLine("=== Body subtree (depth-capped) ===");
            int printed = 0;
            AppendTree(sb, body.transform, 0, ref printed, maxDepth: 4, maxNodes: 400);
            if (printed >= 400) sb.AppendLine("  ... truncated at 400 nodes");

            return sb.ToString();
        }

        // ------------------------------------------------------------------ helpers

        private static void AppendCensus<T>(StringBuilder sb, string label) where T : Component
        {
            try
            {
                var found = UnityEngine.Object.FindObjectsOfType<T>(true);
                sb.AppendLine("  " + label.PadRight(14) + " count=" + found.Length +
                              (found.Length > 0 && found.Length <= 6
                                  ? "   [" + string.Join(" | ", found.Select(f => PathOf(f.transform))) + "]"
                                  : ""));
            }
            catch (Exception e)
            {
                sb.AppendLine("  " + label.PadRight(14) + " <failed: " + e.Message + ">");
            }
        }

        private static void Safe(StringBuilder sb, string label, Func<string> f)
        {
            try { sb.AppendLine("  " + label.PadRight(14) + " = " + f()); }
            catch (Exception e) { sb.AppendLine("  " + label.PadRight(14) + " = <err: " + e.Message + ">"); }
        }

        private static IEnumerable<string> ComponentNames(GameObject go)
        {
            return go.GetComponents<Component>()
                     .Select(c => c == null ? "<missing script>" : c.GetType().Name);
        }

        private static bool IsDescendantOf(Transform candidate, Transform ancestor)
        {
            for (var t = candidate; t != null; t = t.parent)
                if (t == ancestor) return true;
            return false;
        }

        private static string PathOf(Transform t)
        {
            if (t == null) return "<null>";
            var parts = new List<string>();
            for (var c = t; c != null; c = c.parent) parts.Add(c.name);
            parts.Reverse();
            return "/" + string.Join("/", parts);
        }

        private static void AppendTree(StringBuilder sb, Transform t, int depth,
                                       ref int printed, int maxDepth, int maxNodes)
        {
            if (printed >= maxNodes) return;
            printed++;

            sb.AppendLine(new string(' ', depth * 2) + "- " + t.name +
                          "  [" + string.Join(", ", ComponentNames(t.gameObject)) + "]" +
                          (t.gameObject.activeSelf ? "" : "  (inactive)"));

            if (depth >= maxDepth)
            {
                if (t.childCount > 0)
                    sb.AppendLine(new string(' ', (depth + 1) * 2) + "... " + t.childCount + " more child(ren)");
                return;
            }

            for (int i = 0; i < t.childCount; i++)
            {
                if (printed >= maxNodes) return;
                AppendTree(sb, t.GetChild(i), depth + 1, ref printed, maxDepth, maxNodes);
            }
        }
    }
}
