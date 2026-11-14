using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace CCoop.Probe
{
    /// <summary>
    /// Read-only census of everything world generation leaves in the scene.
    ///
    /// Terrain now streams to clients, but generation is suppressed there, so every
    /// non-terrain object it produces is simply missing on a client. Before designing a
    /// manifest we need to know what those objects are and — critically — whether each
    /// can be identified well enough to be recreated from a network message.
    ///
    /// Two identification routes exist:
    ///   * <c>Item.id</c> — an explicit string key that <c>Utils.Create(id, pos, rot)</c>
    ///     consumes directly. Exact.
    ///   * GameObject name minus "(Clone)" — Unity's instantiate naming, which usually
    ///     matches the <c>Resources.Load</c> path. Heuristic, so this report measures how
    ///     often it actually holds.
    /// </summary>
    internal static class WorldEntityReport
    {
        private static bool _written;

        internal static void WriteOnce(WorldGeneration world)
        {
            if (_written) return;
            _written = true;

            try
            {
                var text = Build(world);
                var dir = Path.Combine(Application.persistentDataPath, "CCoop");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir,
                    string.Format("world-entities-{0:yyyyMMdd-HHmmss}.txt", DateTime.Now));
                File.WriteAllText(path, text);
                ProbePlugin.Announce("[entities] census written: " + path);
            }
            catch (Exception e)
            {
                ProbePlugin.AnnounceWarning("[entities] census failed: " + e.Message);
            }
        }

        private static string Build(WorldGeneration world)
        {
            var sb = new StringBuilder();
            sb.AppendLine("CCoop world entity census");
            sb.AppendLine("generated: " + DateTime.Now.ToString("u"));
            sb.AppendLine("layer: " + (world != null ? world.biomeDepth.ToString() : "?"));
            sb.AppendLine();

            var localBody = PlayerCamera.main != null ? PlayerCamera.main.body : null;

            // ---- scene roots: where do generated objects actually live? ----------
            sb.AppendLine("=== scene roots ===");
            var roots = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
            foreach (var r in roots)
                sb.AppendLine("  " + r.name + "  children=" + r.transform.childCount +
                              (r.activeInHierarchy ? "" : "  (inactive)"));
            sb.AppendLine();

            // ---- items loose in the world ---------------------------------------
            // Items carry an explicit id, so these are the ones we can recreate exactly.
            var allItems = UnityEngine.Object.FindObjectsOfType<Item>(true);
            var worldItems = new List<Item>();
            var carriedItems = new List<Item>();

            foreach (var item in allItems)
            {
                if (localBody != null && item.transform.IsChildOf(localBody.transform)) carriedItems.Add(item);
                else worldItems.Add(item);
            }

            sb.AppendLine("=== items ===");
            sb.AppendLine("  total          : " + allItems.Length);
            sb.AppendLine("  carried by body: " + carriedItems.Count);
            sb.AppendLine("  loose in world : " + worldItems.Count);
            sb.AppendLine();

            sb.AppendLine("  -- loose item ids (id x count) --");
            foreach (var g in worldItems.GroupBy(i => SafeId(i)).OrderByDescending(g => g.Count()))
                sb.AppendLine("     " + g.Count().ToString().PadLeft(4) + "  " + g.Key);
            sb.AppendLine();

            // Does the item's own id agree with its GameObject name? If so, the same
            // heuristic is likely safe for non-item prefabs too.
            int idMatchesName = 0;
            foreach (var i in worldItems)
                if (string.Equals(SafeId(i), StripClone(i.name), StringComparison.OrdinalIgnoreCase))
                    idMatchesName++;
            sb.AppendLine("  Item.id matches GameObject name: " + idMatchesName + "/" + worldItems.Count);
            sb.AppendLine();

            // ---- containers and their contents -----------------------------------
            var containers = UnityEngine.Object.FindObjectsOfType<Container>(true);
            sb.AppendLine("=== containers ===");
            sb.AppendLine("  total: " + containers.Length);
            int withContents = 0, totalContained = 0;
            foreach (var c in containers)
            {
                int n = 0;
                foreach (Transform child in c.transform)
                    if (child.GetComponent<Item>() != null) n++;
                if (n > 0) { withContents++; totalContained += n; }
            }
            sb.AppendLine("  non-empty: " + withContents + ", items inside: " + totalContained);
            sb.AppendLine();

            // ---- every other generated behaviour ---------------------------------
            // Grouped by component type so the manifest design can be driven by what is
            // actually present rather than by guesses from the decompiled source.
            sb.AppendLine("=== world objects by component type ===");
            AppendCensus<BuildingEntity>(sb, "BuildingEntity");
            AppendCensus<Container>(sb, "Container");
            AppendCensus<UsableObject>(sb, "UsableObject");
            AppendCensus<Damageable>(sb, "Damageable");
            AppendCensus<Openable>(sb, "Openable");
            AppendCensus<Climbable>(sb, "Climbable");
            AppendCensus<ItemLootPool>(sb, "ItemLootPool");
            AppendCensus<ItemLock>(sb, "ItemLock");
            AppendCensus<BearTrap>(sb, "BearTrap");
            AppendCensus<MineScript>(sb, "MineScript");
            AppendCensus<SpikeStabberScript>(sb, "SpikeStabberScript");
            AppendCensus<SawbladeScript>(sb, "SawbladeScript");
            AppendCensus<StalactiteDropper>(sb, "StalactiteDropper");
            AppendCensus<GeyserScript>(sb, "GeyserScript");
            AppendCensus<RadioactiveObject>(sb, "RadioactiveObject");
            AppendCensus<CrystalBehaviour>(sb, "CrystalBehaviour");
            AppendCensus<SpiderHandler>(sb, "SpiderHandler");
            AppendCensus<CaveTicks>(sb, "CaveTicks");
            AppendCensus<CaveTickSpawner>(sb, "CaveTickSpawner");
            AppendCensus<ScrapEaterScript>(sb, "ScrapEaterScript");
            AppendCensus<GrabberPlant>(sb, "GrabberPlant");
            AppendCensus<TurretScript>(sb, "TurretScript");
            AppendCensus<DroneScript>(sb, "DroneScript");
            AppendCensus<TraderScript>(sb, "TraderScript");
            AppendCensus<CorpseScript>(sb, "CorpseScript");
            AppendCensus<LifepodController>(sb, "LifepodController");
            AppendCensus<TerminalScript>(sb, "TerminalScript");
            AppendCensus<SurvivorNote>(sb, "SurvivorNote");
            AppendCensus<Talker>(sb, "Talker");
            sb.AppendLine();

            // ---- naming quality --------------------------------------------------
            // If most generated objects end in "(Clone)", the name heuristic is viable
            // for prefabs that have no explicit id.
            sb.AppendLine("=== prefab-name viability (non-item world objects) ===");
            var named = new Dictionary<string, int>();
            int cloneSuffixed = 0, total = 0;

            foreach (var mb in UnityEngine.Object.FindObjectsOfType<BuildingEntity>(true)
                         .Select(x => x.gameObject)
                         .Concat(UnityEngine.Object.FindObjectsOfType<UsableObject>(true).Select(x => x.gameObject))
                         .Concat(UnityEngine.Object.FindObjectsOfType<Container>(true).Select(x => x.gameObject))
                         .Distinct())
            {
                total++;
                if (mb.name.EndsWith("(Clone)", StringComparison.Ordinal)) cloneSuffixed++;
                var key = StripClone(mb.name);
                named[key] = named.TryGetValue(key, out var c) ? c + 1 : 1;
            }

            sb.AppendLine("  objects: " + total + ", name ends with (Clone): " + cloneSuffixed);
            sb.AppendLine("  -- distinct names --");
            foreach (var kv in named.OrderByDescending(k => k.Value).Take(60))
                sb.AppendLine("     " + kv.Value.ToString().PadLeft(4) + "  " + kv.Key);
            sb.AppendLine();

            // ---- does Resources.Load actually resolve those names? ---------------
            // The decisive question: a name we cannot load is a thing we cannot recreate.
            sb.AppendLine("=== Resources.Load resolution check ===");
            int resolvable = 0, unresolvable = 0;
            var missing = new List<string>();
            foreach (var key in named.Keys.Take(80))
            {
                bool ok = false;
                try { ok = Resources.Load(key) != null; } catch { }
                if (ok) resolvable++;
                else { unresolvable++; if (missing.Count < 40) missing.Add(key); }
            }
            sb.AppendLine("  resolvable: " + resolvable + "   unresolvable: " + unresolvable);
            if (missing.Count > 0)
            {
                sb.AppendLine("  -- names that did NOT resolve --");
                foreach (var m in missing) sb.AppendLine("     " + m);
            }

            return sb.ToString();
        }

        private static void AppendCensus<T>(StringBuilder sb, string label) where T : Component
        {
            try
            {
                int n = UnityEngine.Object.FindObjectsOfType<T>(true).Length;
                if (n > 0) sb.AppendLine("  " + label.PadRight(22) + n);
            }
            catch (Exception e)
            {
                sb.AppendLine("  " + label.PadRight(22) + "<err: " + e.Message + ">");
            }
        }

        private static string SafeId(Item i)
        {
            try { return string.IsNullOrEmpty(i.id) ? "<no id>" : i.id; }
            catch { return "<err>"; }
        }

        private static string StripClone(string name)
        {
            const string suffix = "(Clone)";
            return name.EndsWith(suffix, StringComparison.Ordinal)
                ? name.Substring(0, name.Length - suffix.Length)
                : name;
        }
    }
}
