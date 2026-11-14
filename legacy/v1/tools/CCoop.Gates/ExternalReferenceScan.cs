using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace CCoop.Gates
{
    /// <summary>
    /// Finds fields on a cloned hierarchy that still point at objects OUTSIDE it.
    ///
    /// This is the root-cause tool for the full-Body clone problem. Unity's
    /// <c>Instantiate</c> remaps references that point *inside* the copied hierarchy,
    /// but any reference to an external object is copied verbatim — so the clone and the
    /// original end up sharing it.
    ///
    /// The known example: <c>Talker.Start()</c> does
    /// <c>if (!text) text = Instantiate(textPrefab, ...)</c>, creating an UNPARENTED
    /// TextMeshPro. On a clone that field is already non-null (pointing at the
    /// original's), so the clone never makes its own and both write to the same object.
    /// That is why cloning killed the local player's dialogue.
    ///
    /// <c>Body</c> alone has 211 fields, many of them HUD references, so enumerating
    /// them by hand is not viable. This reports every offender at once.
    /// </summary>
    internal static class ExternalReferenceScan
    {
        private const BindingFlags Flags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        internal sealed class Finding
        {
            public string ComponentType;
            public string FieldName;
            public string TargetName;
            public string TargetKind;
            public bool TargetIsAsset;
        }

        /// <summary>
        /// Scans every component under <paramref name="cloneRoot"/> for fields
        /// referencing scene objects outside it.
        ///
        /// Project assets (prefabs, sprites, materials) are excluded: those are shared by
        /// design and harmless. Only live scene objects matter, because those are the
        /// ones the original is also using.
        /// </summary>
        internal static List<Finding> Scan(GameObject cloneRoot, GameObject originalRoot)
        {
            var findings = new List<Finding>();
            if (cloneRoot == null) return findings;

            var inClone = new HashSet<int>();
            foreach (var t in cloneRoot.GetComponentsInChildren<Transform>(true))
            {
                inClone.Add(t.gameObject.GetInstanceID());
                foreach (var c in t.GetComponents<Component>())
                    if (c != null) inClone.Add(c.GetInstanceID());
            }

            foreach (var comp in cloneRoot.GetComponentsInChildren<Component>(true))
            {
                if (comp == null) continue;
                var type = comp.GetType();

                // Only game code; Unity's own components legitimately reference shared state.
                if (type.Assembly != typeof(Body).Assembly) continue;

                foreach (var field in AllFields(type))
                {
                    if (!typeof(UnityEngine.Object).IsAssignableFrom(field.FieldType)) continue;

                    UnityEngine.Object value;
                    try { value = field.GetValue(comp) as UnityEngine.Object; }
                    catch { continue; }
                    if (value == null) continue;

                    GameObject targetGo = value as GameObject ?? (value as Component)?.gameObject;
                    if (targetGo == null) continue; // an asset, not a scene object

                    // Assets have no scene; only live scene objects can be shared.
                    bool isAsset = !targetGo.scene.IsValid();
                    if (isAsset) continue;

                    if (inClone.Contains(targetGo.GetInstanceID())) continue; // correctly remapped

                    findings.Add(new Finding
                    {
                        ComponentType = type.Name,
                        FieldName = field.Name,
                        TargetName = PathOf(targetGo.transform),
                        TargetKind = value.GetType().Name,
                        TargetIsAsset = false,
                    });
                }
            }

            return findings;
        }

        private static IEnumerable<FieldInfo> AllFields(Type type)
        {
            // Walk the hierarchy: GetFields does not return private fields of base types.
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
                foreach (var f in t.GetFields(Flags | BindingFlags.DeclaredOnly))
                    yield return f;
        }

        private static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (var c = t; c != null; c = c.parent) parts.Add(c.name);
            parts.Reverse();
            return "/" + string.Join("/", parts);
        }

        /// <summary>Formats findings, grouped by component, for the gate report.</summary>
        internal static string Describe(List<Finding> findings)
        {
            var sb = new StringBuilder();
            if (findings.Count == 0)
            {
                sb.AppendLine("  none — the clone shares no scene objects with the original");
                return sb.ToString();
            }

            sb.AppendLine("  " + findings.Count + " shared scene reference(s) — each is a field where the");
            sb.AppendLine("  clone and the original point at the SAME live object:");

            foreach (var group in findings.GroupBy(f => f.ComponentType).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                sb.AppendLine("    " + group.Key + ":");
                foreach (var f in group.OrderBy(f => f.FieldName, StringComparer.Ordinal))
                    sb.AppendLine("      ." + f.FieldName + "  (" + f.TargetKind + ")  -> " + f.TargetName);
            }
            return sb.ToString();
        }
    }
}
