using System;
using System.Collections.Generic;
using CCoop.Plugin.Runtime;
using CCoop.Plugin.World;
using UnityEngine;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// Builds a remote player's puppet from the character prefab they chose, so Milky
    /// looks like Milky. Every gameplay script, collider and rigidbody is stripped: the
    /// game's Body is welded to the local camera, input and HUD, so a second live one
    /// breaks the real player. What is left is the rig of sprites, posed over the wire.
    /// </summary>
    internal static class ProxyBuilder
    {
        internal sealed class Rig
        {
            public GameObject Root;
            public Transform BodyRoot;
            public Transform[] Limbs;
            public Renderer[] LimbRenderers;
            public Transform[] Slots;
            public int[] SlotSort;
            public string[] SlotLimbName;
            public float[] LimbScaleX;
            public readonly List<Material> OwnedMaterials = new List<Material>();
        }

        internal static Rig Build(int character, string label)
        {
            var prefab = Resources.Load<GameObject>("Special/Characters/" + GameInfo.CharacterName(character));
            if (prefab == null)
            {
                Log.Warn("[player] character prefab " + GameInfo.CharacterName(character) + " not found");
                return null;
            }

            GameObject holder = null;
            try
            {
                var go = WorldAccess.InstantiateDormant(prefab, out holder);
                go.name = "CCoop_Remote_" + label;

                var body = go.GetComponentInChildren<Body>(true);
                if (body == null)
                {
                    Log.Warn("[player] character prefab has no Body");
                    UnityEngine.Object.DestroyImmediate(holder);
                    return null;
                }

                var rig = new Rig { Root = go, BodyRoot = body.transform };
                var limbs = body.limbs ?? new Limb[0];
                rig.Limbs = new Transform[limbs.Length];
                rig.LimbRenderers = new Renderer[limbs.Length];
                rig.LimbScaleX = new float[limbs.Length];
                for (int i = 0; i < limbs.Length; i++)
                {
                    if (limbs[i] == null) continue;
                    rig.Limbs[i] = limbs[i].transform;
                    rig.LimbRenderers[i] = limbs[i].GetComponent<Renderer>();
                    rig.LimbScaleX[i] = limbs[i].transform.localScale.x;
                }

                var slots = body.slots ?? new InventorySlot[0];
                rig.Slots = new Transform[slots.Length];
                rig.SlotSort = new int[slots.Length];
                rig.SlotLimbName = new string[slots.Length];
                for (int i = 0; i < slots.Length; i++)
                {
                    if (slots[i] == null) continue;
                    rig.Slots[i] = slots[i].transform;
                    rig.SlotSort[i] = slots[i].spriteSortOrder;
                    rig.SlotLimbName[i] = slots[i].limb != null ? slots[i].limb.name : null;
                }

                foreach (var item in go.GetComponentsInChildren<Item>(true))
                    if (item != null) UnityEngine.Object.DestroyImmediate(item.gameObject);

                StripBehaviour(go);
                WorldAccess.Strip<Joint2D>(go);
                WorldAccess.Strip<Collider2D>(go);
                WorldAccess.Strip<Rigidbody2D>(go);
                WorldAccess.Strip<AudioSource>(go);
                WorldAccess.Strip<ParticleSystem>(go);

                // Limb.Awake normally gives each limb its own material for wound drawing.
                // Stripped, the puppet would share the prefab's, so make private copies.
                var replaced = new Dictionary<int, Material>();
                foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                {
                    var src = r.sharedMaterial;
                    if (src == null) continue;
                    if (!replaced.TryGetValue(src.GetInstanceID(), out var mine))
                    {
                        mine = new Material(src);
                        replaced[src.GetInstanceID()] = mine;
                        rig.OwnedMaterials.Add(mine);
                    }
                    r.sharedMaterial = mine;
                }

                foreach (var t in go.GetComponentsInChildren<Transform>(true))
                    if (t.CompareTag("Player")) t.tag = "Untagged";

                go.transform.SetParent(null, false);
                go.SetActive(true);
                UnityEngine.Object.DestroyImmediate(holder);
                return rig;
            }
            catch (Exception e)
            {
                Log.Error("[player] could not build a puppet: " + e);
                if (holder != null) UnityEngine.Object.DestroyImmediate(holder);
                return null;
            }
        }

        /// <summary>
        /// Removes the game's own scripts. Several declare RequireComponent on the
        /// physics parts, so scripts must go before the rigidbodies they depend on.
        /// </summary>
        private static void StripBehaviour(GameObject root)
        {
            var gameAssembly = typeof(Body).Assembly;
            for (int pass = 0; pass < 3; pass++)
            {
                foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (mb == null) continue;
                    if (mb.GetType().Assembly != gameAssembly) continue;
                    try { UnityEngine.Object.DestroyImmediate(mb); }
                    catch { /* a dependency; the next pass gets it */ }
                }
            }
        }

        internal static void Destroy(Rig rig)
        {
            if (rig == null) return;
            if (rig.Root != null) UnityEngine.Object.Destroy(rig.Root);
            foreach (var m in rig.OwnedMaterials) if (m != null) UnityEngine.Object.Destroy(m);
            rig.OwnedMaterials.Clear();
        }

        /// <summary>A sprite-only copy of an item prefab for a puppet's hand or back.</summary>
        internal static GameObject BuildHeldItem(string itemId)
        {
            var prefab = WorldAccess.ResolvePrefab(itemId);
            if (prefab == null) return null;
            var go = WorldAccess.InstantiateDormant(prefab, out var holder);
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                if (mb != null && mb.GetType().Assembly == typeof(Body).Assembly) UnityEngine.Object.DestroyImmediate(mb);
            WorldAccess.Strip<Joint2D>(go);
            WorldAccess.Strip<Collider2D>(go);
            WorldAccess.Strip<Rigidbody2D>(go);
            WorldAccess.Strip<AudioSource>(go);
            go.transform.SetParent(null, false);
            UnityEngine.Object.DestroyImmediate(holder);
            go.name = "CCoop_Held_" + itemId;
            return go;
        }
    }
}
