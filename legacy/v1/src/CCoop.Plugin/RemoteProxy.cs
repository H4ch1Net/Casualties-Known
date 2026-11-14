using System;
using System.Collections.Generic;
using CCoop.Protocol.Messages;
using UnityEngine;

namespace CCoop.Plugin
{
    /// <summary>
    /// Builds a sprite-only stand-in for a remote player.
    ///
    /// Validated in M1: a stripped proxy adds no <c>Body</c> or <c>Limb</c> to the
    /// scene, leaves the local player animating normally, and produces no engine
    /// errors. The full-<c>Body</c> clone does NOT — it froze the local player's
    /// animation, broke terrain collision and killed dialogue — so clients must never
    /// use one.
    ///
    /// Two construction rules are load-bearing and were each learned from a failure:
    ///
    ///   1. Instantiate under an INACTIVE holder. Cloning a live Body runs its
    ///      Awake/Start before anything can be stripped, and those leave residue that
    ///      outlives the components (an added AudioSource, started coroutines,
    ///      Physics2D.IgnoreCollision calls).
    ///   2. Strip with DestroyImmediate. Destroy defers to end-of-frame, so components
    ///      keep ticking - and keep being counted - for the rest of the frame.
    /// </summary>
    /// <summary>
    /// What a proxy needs to hang an item where the game would.
    ///
    /// Captured from the template's <c>InventorySlot</c> components, which are stripped
    /// from the clone: a live one on a proxy would make a remote player's belongings
    /// pickable out of their hands.
    /// </summary>
    internal struct ProxySlot
    {
        public string Name;
        public string LimbName;
        public int SortOrder;
    }

    internal static class ProxyFactory
    {
        /// <summary>
        /// Creates a proxy from the local body. Returns null if it cannot be built.
        /// </summary>
        internal static RemoteProxy Create(Body template, byte peerId, string displayName)
        {
            if (template == null) return null;

            // Limb GameObject names are unique on this build (Head, UpTorso, DownTorso,
            // UpArmF...), so they survive cloning and let us re-associate the proxy's
            // transforms with Body.limbs order after the Limb components are gone.
            var limbNames = new List<string>();
            if (template.limbs != null)
            {
                foreach (var limb in template.limbs)
                    limbNames.Add(limb != null ? limb.name : null);
            }

            // Slot layout, read off the template before anything is stripped. The
            // InventorySlot components do not survive the strip, but their GameObjects
            // do — so the names are enough to find the same slots on the proxy, exactly
            // as limb names are.
            var slots = new List<ProxySlot>();
            if (template.slots != null)
            {
                foreach (var slot in template.slots)
                {
                    if (slot == null) { slots.Add(new ProxySlot()); continue; }
                    slots.Add(new ProxySlot
                    {
                        Name = slot.name,
                        SortOrder = slot.spriteSortOrder,
                        LimbName = slot.limb != null ? slot.limb.name : null,
                    });
                }
            }

            var holder = new GameObject("CCoop_ProxyHolder");
            holder.SetActive(false);

            GameObject go;
            try
            {
                go = UnityEngine.Object.Instantiate(template.gameObject, holder.transform);
            }
            catch (Exception e)
            {
                UnityEngine.Object.DestroyImmediate(holder);
                CoopPlugin.Warn("proxy instantiate failed: " + e.Message);
                return null;
            }

            go.name = "CCoop_Remote_" + peerId;

            // Joints reference rigidbodies, so joints must go first.
            Strip<Joint2D>(go);
            Strip<Rigidbody2D>(go);
            Strip<Collider2D>(go);

            Strip<Body>(go);
            Strip<Limb>(go);
            Strip<Talker>(go);
            Strip<SelfHarmer>(go);
            Strip<Vomiter>(go);
            Strip<PantSound>(go);
            Strip<LiquidAffect>(go);
            Strip<InventorySlot>(go);
            Strip<BleedParticle>(go);
            Strip<FacialExpression>(go);
            Strip<TailScript>(go);
            Strip<AudioSource>(go);

            // The clone inherited the local player's items; a remote player owns nothing
            // until the host says otherwise (inventory sync is a later milestone).
            foreach (var item in go.GetComponentsInChildren<Item>(true))
                UnityEngine.Object.DestroyImmediate(item.gameObject);

            GiveOwnMaterials(go);

            // Nothing should mistake a proxy for the player.
            foreach (var t in go.GetComponentsInChildren<Transform>(true))
            {
                if (t.gameObject.CompareTag("Player")) t.gameObject.tag = "Untagged";
            }

            var proxy = go.AddComponent<RemoteProxy>();
            proxy.Bind(peerId, displayName, limbNames, slots);

            // Park remote players at the SCENE ROOT, never under the local player's root.
            //
            // The previous code parented to template.transform.parent — /Experiment, the
            // local player's own hierarchy — so every proxy inherited the local player's
            // transform. Remote characters appeared to mirror whatever the local player
            // did rather than what their owner was doing.
            go.transform.SetParent(null, false);
            go.SetActive(true);
            UnityEngine.Object.DestroyImmediate(holder);

            return proxy;
        }

        /// <summary>
        /// Gives the proxy its own copy of every material it is rendering with.
        ///
        /// Without this a remote player wears the LOCAL player's wounds. <c>Limb.Awake</c>
        /// builds a per-limb material and installs it as <c>sharedMaterial</c>:
        ///
        /// <code>
        ///   mat = new Material(GetComponent&lt;SpriteRenderer&gt;().material);
        ///   GetComponent&lt;SpriteRenderer&gt;().sharedMaterial = mat;
        ///   if (bonusRendererToAffect) bonusRendererToAffect.sharedMaterial = mat;
        /// </code>
        ///
        /// Cloning copies the *reference*, and <see cref="Strip{T}"/> then removes the
        /// clone's <c>Limb</c> components — so the proxy never makes a material of its own
        /// and keeps pointing at the original's. Every wound the local player takes is
        /// drawn into that material and appears on the remote character too.
        ///
        /// Reported in play exactly as that predicts, and the asymmetry is the giveaway:
        /// the player who got hurt saw their *partner's* body change, while the partner —
        /// whose own material was untouched — saw nothing wrong on either character.
        ///
        /// Instances are tracked so they can be destroyed with the proxy; Unity does not
        /// collect materials created from script.
        /// </summary>
        private static void GiveOwnMaterials(GameObject root)
        {
            var owned = new List<Material>();
            var replaced = new Dictionary<int, Material>();

            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var source = renderer.sharedMaterial;
                if (source == null) continue;

                // One clone per distinct source material, not per renderer: limbs share a
                // material with their bonus renderer deliberately, and splitting those
                // would stop the two halves of a limb matching each other.
                Material mine;
                int key = source.GetInstanceID();
                if (!replaced.TryGetValue(key, out mine))
                {
                    mine = new Material(source);
                    replaced[key] = mine;
                    owned.Add(mine);
                }
                renderer.sharedMaterial = mine;
            }

            if (owned.Count > 0) _ownedMaterials[root.GetInstanceID()] = owned;
        }

        /// <summary>Materials created for each proxy, so they can be destroyed with it.</summary>
        private static readonly Dictionary<int, List<Material>> _ownedMaterials =
            new Dictionary<int, List<Material>>();

        /// <summary>
        /// Destroys the materials made for a proxy. Unity does not collect materials
        /// created from script, so a session's worth of joins and leaves would leak them.
        /// </summary>
        internal static void ReleaseMaterials(GameObject root)
        {
            if (root == null) return;

            List<Material> owned;
            int key = root.GetInstanceID();
            if (!_ownedMaterials.TryGetValue(key, out owned)) return;

            _ownedMaterials.Remove(key);
            foreach (var m in owned)
                if (m != null) UnityEngine.Object.Destroy(m);
        }

        private static void Strip<T>(GameObject root) where T : Component
        {
            foreach (var c in root.GetComponentsInChildren<T>(true))
                if (c != null) UnityEngine.Object.DestroyImmediate(c);
        }
    }

    /// <summary>
    /// Drives a remote player's stand-in from network snapshots.
    ///
    /// Movement arrives unreliable-sequenced at a fixed send rate, well below frame
    /// rate, so snapshots are buffered and played back slightly in the past. That trades
    /// a small constant delay for smooth motion — extrapolating instead would visibly
    /// overshoot and snap back every time a packet is late or lost, which on a lossy
    /// link is often.
    /// </summary>
    public sealed class RemoteProxy : MonoBehaviour
    {
        private void OnDestroy() => ProxyFactory.ReleaseMaterials(gameObject);

        /// <summary>
        /// How far behind live to render. Must exceed the send interval, or the buffer
        /// runs dry between snapshots and motion stutters.
        /// </summary>
        public const float InterpolationDelaySeconds = 0.12f;

        private const int MaxBufferedSnapshots = 32;

        private struct Snapshot
        {
            public float Time;
            public Vector2 Position;
            public bool FacingRight;
            public LimbPose[] Limbs;
        }

        private readonly List<Snapshot> _buffer = new List<Snapshot>();
        private Transform[] _limbTransforms = new Transform[0];

        public byte PeerId { get; private set; }
        public string DisplayName { get; private set; } = string.Empty;

        private ProxySlot[] _slots = new ProxySlot[0];
        private Transform[] _slotTransforms = new Transform[0];

        /// <summary>Item id currently shown in each slot, so only changes are rebuilt.</summary>
        private string[] _shown = new string[0];

        internal void Bind(byte peerId, string displayName, List<string> limbNames,
                           List<ProxySlot> slots)
        {
            PeerId = peerId;
            DisplayName = displayName ?? string.Empty;

            // Resolve limb transforms by name, in Body.limbs order, so a rotation array
            // received over the wire maps onto the right joints.
            var all = GetComponentsInChildren<Transform>(true);
            var byName = new Dictionary<string, Transform>(all.Length);
            foreach (var t in all)
            {
                if (!byName.ContainsKey(t.name)) byName[t.name] = t;
            }

            _limbTransforms = new Transform[limbNames.Count];
            for (int i = 0; i < limbNames.Count; i++)
            {
                var n = limbNames[i];
                if (n != null && byName.TryGetValue(n, out var t)) _limbTransforms[i] = t;
            }

            _slots = slots != null ? slots.ToArray() : new ProxySlot[0];
            _slotTransforms = new Transform[_slots.Length];
            _shown = new string[_slots.Length];
            for (int i = 0; i < _slots.Length; i++)
            {
                var n = _slots[i].Name;
                if (n != null && byName.TryGetValue(n, out var t)) _slotTransforms[i] = t;
            }
        }

        /// <summary>
        /// Shows what this player is carrying.
        ///
        /// Only changed slots are rebuilt. The message repeats twice a second so that a
        /// proxy created mid-session catches up, and re-instantiating an unchanged item at
        /// that rate would both cost work and visibly flicker.
        ///
        /// Placement is reproduced from <c>Body.PickUpItem</c> rather than transmitted,
        /// because every input it uses is already on both peers: the rotation and the
        /// back-slot quarter-turn come from <c>ItemInfo</c>, the sort order and the limb
        /// from the slot itself.
        /// </summary>
        internal void ApplyEquipment(PlayerEquipmentMessage kit)
        {
            if (kit == null || _slotTransforms.Length == 0) return;

            // Anything not named is empty; start from that and fill in what is.
            var wanted = new string[_slotTransforms.Length];
            foreach (var e in kit.Items)
                if (e.Slot < wanted.Length) wanted[e.Slot] = e.ItemId;

            for (int i = 0; i < wanted.Length; i++)
            {
                if (string.Equals(wanted[i], _shown[i], StringComparison.Ordinal)) continue;

                ClearSlot(i);
                _shown[i] = wanted[i];
                if (!string.IsNullOrEmpty(wanted[i])) BuildSlot(i, wanted[i]);
            }
        }

        private void ClearSlot(int index)
        {
            var parent = _slotTransforms[index];
            if (parent == null) return;

            for (int i = parent.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(parent.GetChild(i).gameObject);
        }

        private void BuildSlot(int index, string itemId)
        {
            var parent = _slotTransforms[index];
            if (parent == null) return;

            try
            {
                var prefab = WorldSync.ResolvePrefabPublic(itemId) as GameObject;
                if (prefab == null) return;

                // Under an inactive holder, so the Item never runs Start and never adds
                // itself to Item.allItems. A live item on a proxy can be picked up out of
                // a remote player's hands, which is why the clone's items are destroyed in
                // the first place.
                var holder = new GameObject("CCoop_ItemHolder");
                holder.SetActive(false);

                var go = UnityEngine.Object.Instantiate(prefab, holder.transform);

                foreach (var c in go.GetComponentsInChildren<Item>(true))
                    UnityEngine.Object.DestroyImmediate(c);
                foreach (var c in go.GetComponentsInChildren<Rigidbody2D>(true))
                    UnityEngine.Object.DestroyImmediate(c);
                foreach (var c in go.GetComponentsInChildren<Collider2D>(true))
                    UnityEngine.Object.DestroyImmediate(c);

                // Straight out of Body.PickUpItem.
                var info = Item.GetItem(itemId);
                float rotation = info != null ? info.slotRotation : 0f;
                if (info != null && info.HasTag("backflip") && index > 2) rotation -= 90f;

                float limbScaleX = 1f;
                var limbName = _slots[index].LimbName;
                if (!string.IsNullOrEmpty(limbName))
                {
                    foreach (var t in _limbTransforms)
                        if (t != null && t.name == limbName) { limbScaleX = t.localScale.x; break; }
                }
                if (Mathf.Approximately(limbScaleX, 0f)) limbScaleX = 1f;

                go.transform.SetParent(parent, false);
                go.transform.localPosition = Vector3.zero;
                go.transform.localEulerAngles = new Vector3(0f, 0f, rotation);
                go.transform.localScale = new Vector3(1f / limbScaleX, 1f, 1f);

                var renderer = go.GetComponent<SpriteRenderer>();
                if (renderer != null) renderer.sortingOrder = _slots[index].SortOrder;

                go.SetActive(true);
                UnityEngine.Object.DestroyImmediate(holder);
            }
            catch (Exception e)
            {
                CoopPlugin.Warn("could not show '" + itemId + "' on a proxy: " + e.Message);
            }
        }

        /// <summary>
        /// Names of the shader properties a limb's material carries, in the order the
        /// wire sends them. Read straight back out of <c>Limb.Update</c>.
        /// </summary>
        private static readonly string[] LimbProperties =
            { "_SkinDamage", "_MuscleDamage", "_InfectionPercent", "_Pain", "_BloodOverlay" };

        private static readonly string[] BodyProperties = { "_Wetness", "_SnowAmount", "_Dirtyness" };

        /// <summary>
        /// Paints this player's wounds, dirt and blood onto the proxy.
        ///
        /// Writes the sender's own shader values rather than re-deriving them from health
        /// figures, so nothing here has to know how the game turns a wound into a picture.
        /// Safe to write directly because the proxy owns its materials — see
        /// <see cref="ProxyFactory"/>, where sharing them was what made a remote player
        /// wear the local player's injuries.
        /// </summary>
        internal void ApplyAppearance(PlayerAppearanceMessage look)
        {
            if (look == null) return;

            float wetness = PlayerAppearanceMessage.ToUnit(look.Wetness);
            float snow = PlayerAppearanceMessage.ToUnit(look.Snow);
            float dirt = PlayerAppearanceMessage.ToUnit(look.Dirt);

            int limbs = Math.Min(look.Limbs.Count, _limbTransforms.Length);
            for (int i = 0; i < limbs; i++)
            {
                var t = _limbTransforms[i];
                if (t == null) continue;

                var renderer = t.GetComponent<Renderer>();
                var mat = renderer != null ? renderer.sharedMaterial : null;
                if (mat == null) continue;

                var l = look.Limbs[i];

                mat.SetFloat(LimbProperties[0],
                             PlayerAppearanceMessage.ToUnit(l.SkinDamage) * PlayerAppearanceMessage.DamageScale);
                mat.SetFloat(LimbProperties[1],
                             PlayerAppearanceMessage.ToUnit(l.MuscleDamage) * PlayerAppearanceMessage.DamageScale);
                mat.SetFloat(LimbProperties[2], PlayerAppearanceMessage.ToUnit(l.Infection));
                mat.SetFloat(LimbProperties[3], PlayerAppearanceMessage.ToUnit(l.Pain));
                mat.SetFloat(LimbProperties[4],
                             PlayerAppearanceMessage.ToUnit(l.Blood) * PlayerAppearanceMessage.BloodScale);

                mat.SetFloat(BodyProperties[0], wetness);
                mat.SetFloat(BodyProperties[1], snow);
                mat.SetFloat(BodyProperties[2], dirt);
            }
        }

        /// <summary>
        /// Reads a body's current appearance straight out of the materials its limbs are
        /// rendering with, which is where <c>Limb.Update</c> has just written it.
        /// </summary>
        internal static PlayerAppearanceMessage Describe(Body body)
        {
            if (body == null || body.limbs == null) return null;

            var msg = new PlayerAppearanceMessage();
            bool tookBodyValues = false;

            foreach (var limb in body.limbs)
            {
                var renderer = limb != null ? limb.GetComponent<Renderer>() : null;
                var mat = renderer != null ? renderer.sharedMaterial : null;
                if (mat == null)
                {
                    msg.Limbs.Add(new LimbAppearance());
                    continue;
                }

                msg.Limbs.Add(new LimbAppearance
                {
                    SkinDamage = PlayerAppearanceMessage.ToByte(
                        mat.GetFloat(LimbProperties[0]) / PlayerAppearanceMessage.DamageScale),
                    MuscleDamage = PlayerAppearanceMessage.ToByte(
                        mat.GetFloat(LimbProperties[1]) / PlayerAppearanceMessage.DamageScale),
                    Infection = PlayerAppearanceMessage.ToByte(mat.GetFloat(LimbProperties[2])),
                    Pain = PlayerAppearanceMessage.ToByte(mat.GetFloat(LimbProperties[3])),
                    Blood = PlayerAppearanceMessage.ToByte(
                        mat.GetFloat(LimbProperties[4]) / PlayerAppearanceMessage.BloodScale),
                });

                // The same on every limb, so read once from whichever limb has a material.
                if (!tookBodyValues)
                {
                    msg.Wetness = PlayerAppearanceMessage.ToByte(mat.GetFloat(BodyProperties[0]));
                    msg.Snow = PlayerAppearanceMessage.ToByte(mat.GetFloat(BodyProperties[1]));
                    msg.Dirt = PlayerAppearanceMessage.ToByte(mat.GetFloat(BodyProperties[2]));
                    tookBodyValues = true;
                }
            }

            return msg;
        }

        /// <summary>
        /// Compact description of the currently applied pose, in the same format as the
        /// runner's local-body diagnostic, so host and client logs can be diffed.
        /// </summary>
        /// <summary>
        /// Draws a swing this player made.
        ///
        /// Anchored to the proxy's own torso rather than to a transmitted position: the
        /// proxy is deliberately rendered ~0.12 s behind to smooth out the network, so an
        /// arc placed at the attacker's real coordinates would appear detached from the
        /// body it belongs to.
        /// </summary>
        internal void PlayAttack(string animName, float rotation, bool facingRight, string sound)
        {
            // Limb 1 is UpTorso, which is where the game itself anchors the arc.
            var origin = _limbTransforms.Length > 1 && _limbTransforms[1] != null
                ? _limbTransforms[1].position
                : transform.position;

            var prefab = Resources.Load(animName);
            if (prefab != null)
            {
                var anim = UnityEngine.Object.Instantiate(prefab) as GameObject;
                if (anim != null)
                {
                    anim.transform.eulerAngles = new Vector3(0f, 0f, rotation);
                    anim.transform.localScale = new Vector3(facingRight ? 1f : -1f, 1f, 1f);
                    anim.transform.position = origin;
                }
            }

            if (!string.IsNullOrEmpty(sound))
            {
                // Positional, and not followed: the arc is a one-shot at a fixed point,
                // and attaching it to a proxy that may be destroyed would leak the source.
                try { Sound.Play(sound, origin); }
                catch { /* a missing clip must not break the proxy */ }
            }
        }

        internal string DescribePose()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("n=").Append(_limbTransforms.Length)
              .Append(" buf=").Append(_buffer.Count).Append(' ');
            sb.Append("pos(").Append(transform.position.x.ToString("F3")).Append(',')
              .Append(transform.position.y.ToString("F3")).Append(") ");

            foreach (int i in new[] { 0, 1, 5 })
            {
                if (i >= _limbTransforms.Length) continue;
                var t = _limbTransforms[i];
                if (t == null) continue;
                sb.Append(t.name).Append('(')
                  .Append(t.localPosition.x.ToString("F3")).Append(',')
                  .Append(t.localPosition.y.ToString("F3")).Append(',')
                  .Append(t.localEulerAngles.z.ToString("F1")).Append(") ");
            }
            return sb.ToString();
        }

        /// <summary>Queues a snapshot received from the network.</summary>
        public void Receive(PlayerTransformMessage m)
        {
            if (m == null) return;

            _buffer.Add(new Snapshot
            {
                Time = Time.time,
                Position = new Vector2(m.PositionX, m.PositionY),
                FacingRight = m.FacingRight,
                Limbs = m.Limbs,
            });

            // Bound the buffer: a peer that floods us must not grow it without limit.
            if (_buffer.Count > MaxBufferedSnapshots)
                _buffer.RemoveRange(0, _buffer.Count - MaxBufferedSnapshots);
        }

        private void Update()
        {
            if (_buffer.Count == 0) return;

            float renderTime = Time.time - InterpolationDelaySeconds;

            // Find the pair of snapshots bracketing renderTime.
            int older = -1;
            for (int i = _buffer.Count - 1; i >= 0; i--)
            {
                if (_buffer[i].Time <= renderTime) { older = i; break; }
            }

            if (older < 0)
            {
                // Everything is still in the future: hold at the oldest known pose
                // rather than snapping to the newest, which would jump ahead.
                Apply(_buffer[0], _buffer[0], 0f);
                return;
            }

            if (older >= _buffer.Count - 1)
            {
                // Ran past the newest snapshot (sender stalled or packets lost). Hold the
                // last known pose instead of extrapolating into a position that will have
                // to be corrected.
                Apply(_buffer[_buffer.Count - 1], _buffer[_buffer.Count - 1], 0f);
                TrimTo(_buffer.Count - 1);
                return;
            }

            var a = _buffer[older];
            var b = _buffer[older + 1];
            float span = b.Time - a.Time;
            float t = span > 0.0001f ? Mathf.Clamp01((renderTime - a.Time) / span) : 0f;

            Apply(a, b, t);
            TrimTo(older);
        }

        private void TrimTo(int keepFrom)
        {
            if (keepFrom > 0) _buffer.RemoveRange(0, keepFrom);
        }

        private void Apply(Snapshot a, Snapshot b, float t)
        {
            transform.position = Vector2.Lerp(a.Position, b.Position, t);

            // Facing IS applied by flipping the root's X scale.
            //
            // An earlier version removed this, on the theory that the synced limb local
            // positions already encoded mirroring. They do not — the game mirrors by
            // scale, and without this every remote player faced permanently right. (The
            // original "proxy mirrors what YOU do" report was the separate parenting bug:
            // proxies were children of the local player's root and inherited its flip.)
            //
            // Facing is a discrete flip, so take the nearer snapshot rather than blending
            // through zero scale.
            bool facing = t < 0.5f ? a.FacingRight : b.FacingRight;
            var scale = transform.localScale;
            float magnitude = Mathf.Abs(scale.x);
            if (magnitude < 0.0001f) magnitude = 1f;
            scale.x = facing ? magnitude : -magnitude;
            transform.localScale = scale;

            if (a.Limbs == null) return;

            for (int i = 0; i < _limbTransforms.Length; i++)
            {
                var limb = _limbTransforms[i];
                if (limb == null || i >= a.Limbs.Length) continue;

                LimbPose poseA = a.Limbs[i];
                LimbPose poseB = (b.Limbs != null && i < b.Limbs.Length) ? b.Limbs[i] : poseA;

                // Position matters as much as rotation here: the limbs are flat siblings
                // driven by joints, not a nested skeleton, so a limb does not follow its
                // parent's rotation. Rotation-only sync made the character float with
                // limbs spinning at their rest offsets.
                limb.localPosition = new Vector3(
                    Mathf.Lerp(poseA.LocalX, poseB.LocalX, t),
                    Mathf.Lerp(poseA.LocalY, poseB.LocalY, t),
                    limb.localPosition.z);

                // LerpAngle takes the short way round, so 359 -> 1 does not spin the
                // limb the long way.
                float rot = Mathf.LerpAngle(poseA.Rotation, poseB.Rotation, t);
                limb.localRotation = Quaternion.Euler(0f, 0f, rot);
            }
        }
    }
}
