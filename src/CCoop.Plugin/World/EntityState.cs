using System;
using System.Collections.Generic;
using System.Reflection;
using CCoop.Net.Wire;
using CCoop.Plugin.Runtime;
using HarmonyLib;
using UnityEngine;

namespace CCoop.Plugin.World
{
    /// <summary>
    /// Per-object state the game rolls for itself when an object is generated, carried in
    /// the layer snapshot so a client's copy is the host's object and not a fresh roll.
    ///
    /// Crystals are the case that matters today: their effects are rolled in a generation
    /// step clients skip, so without this a client's crystal has no effect list and throws
    /// on every touch. More object types (piston phase, fan speed, geyser liquid) can add a
    /// section here.
    /// </summary>
    internal static class EntityState
    {
        private const byte SectionCrystal = 1;
        private const byte SectionTrader = 2;
        private const byte SectionClimbable = 3;
        private const byte SectionEnd = 0;

        private static readonly AccessTools.FieldRef<CrystalBehaviour, List<CrystalEffect>> Effects =
            AccessTools.FieldRefAccess<CrystalBehaviour, List<CrystalEffect>>("effects");

        /// <summary>Extra state for one object, or null when it has none worth sending.</summary>
        internal static byte[] Capture(GameObject go)
        {
            var w = new PacketWriter(256);
            bool any = false;

            var trader = go.GetComponent<TraderScript>();
            if (trader != null)
            {
                w.WriteByte(SectionTrader);
                var section = new PacketWriter(256);
                Sync.TraderSync.Write(section, trader);
                w.WriteBlob(section.ToArray());
                any = true;
            }

            var crystal = go.GetComponent<CrystalBehaviour>();
            var effects = crystal != null ? Effects(crystal) : null;
            if (effects != null)
            {
                w.WriteByte(SectionCrystal);
                var c = crystal.sprite != null ? crystal.sprite.color : Color.white;
                w.WriteSingle(c.r); w.WriteSingle(c.g); w.WriteSingle(c.b); w.WriteSingle(c.a);
                w.WriteSingle(crystal.light != null ? crystal.light.intensity : 1f);
                w.WriteUInt16((ushort)effects.Count);
                foreach (var e in effects)
                {
                    w.WriteString(e.GetType().FullName);
                    FieldState.Write(w, e, typeof(CrystalEffect));
                }
                var heater = go.GetComponent<Heater>();
                w.WriteBool(heater != null);
                if (heater != null) FieldState.Write(w, heater, typeof(MonoBehaviour));
                any = true;
            }

            // Ropes and vines: the game works out where they hang (a raycast to the ground)
            // and stretches the sprite to fit when it makes them. A copy from the prefab
            // has no climb path and the prefab's size: one block nobody can climb.
            var climb = go.GetComponent<Climbable>();
            if (climb != null && climb.points != null && climb.points.Count >= 2)
            {
                w.WriteByte(SectionClimbable);
                int n = Mathf.Min(climb.points.Count, 64);
                w.WriteUInt16((ushort)n);
                for (int i = 0; i < n; i++) { w.WriteSingle(climb.points[i].x); w.WriteSingle(climb.points[i].y); }
                w.WriteSingle(climb.downwardsVelocity);
                var sr = go.GetComponent<SpriteRenderer>();
                w.WriteBool(sr != null);
                if (sr != null)
                {
                    w.WriteSingle(sr.size.x); w.WriteSingle(sr.size.y);
                    var c = sr.color;
                    w.WriteSingle(c.r); w.WriteSingle(c.g); w.WriteSingle(c.b); w.WriteSingle(c.a);
                    w.WriteBool(sr.flipX);
                }
                bool child = go.transform.childCount > 0;
                w.WriteBool(child);
                if (child)
                {
                    var ls = go.transform.GetChild(0).localScale;
                    w.WriteSingle(ls.x); w.WriteSingle(ls.y); w.WriteSingle(ls.z);
                }
                any = true;
            }

            if (!any) return null;
            w.WriteByte(SectionEnd);
            return w.ToArray();
        }

        /// <summary>
        /// Applies captured state to a freshly instantiated copy. Call before the snapshot's
        /// transform and health are applied: some effects scale the crystal or set its
        /// health in Start, and the host's values must win.
        /// </summary>
        internal static void Apply(GameObject go, byte[] data)
        {
            var crystal = go.GetComponent<CrystalBehaviour>();
            bool crystalSeen = false;
            if (data != null && data.Length > 0)
            {
                var r = new PacketReader(data, 0, data.Length);
                while (!r.Failed && r.Remaining > 0)
                {
                    byte section = r.ReadByte();
                    if (section == SectionEnd) break;
                    if (section == SectionTrader)
                    {
                        var bytes = r.ReadBlob();
                        // Start rolls a fresh stock after this; the host's goes in after Start.
                        if (!r.Failed) Sync.TraderSync.Stash(go, bytes);
                        continue;
                    }
                    if (section == SectionCrystal) { ApplyCrystal(go, crystal, ref r); crystalSeen = true; continue; }
                    if (section == SectionClimbable) { ApplyClimbable(go, ref r); continue; }
                    r.Fail();
                }
            }
            if (crystal != null && !crystalSeen)
            {
                // The host's crystal rolled no effects and removed its behaviour; do the same,
                // or this copy's empty effect list would throw on the first touch.
                var usable = go.GetComponent<UsableObject>();
                if (usable != null) UnityEngine.Object.Destroy(usable);
                UnityEngine.Object.Destroy(crystal);
            }
        }

        private static void ApplyClimbable(GameObject go, ref PacketReader r)
        {
            int n = r.ReadCount(64);
            var points = new List<Vector2>(n);
            for (int i = 0; i < n && !r.Failed; i++) points.Add(new Vector2(r.ReadSingle(), r.ReadSingle()));
            float down = r.ReadSingle();
            bool hasSprite = r.ReadBool();
            Vector2 size = default;
            Color color = Color.white;
            bool flip = false;
            if (hasSprite)
            {
                size = new Vector2(r.ReadSingle(), r.ReadSingle());
                color = new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                flip = r.ReadBool();
            }
            bool hasChild = r.ReadBool();
            Vector3 childScale = Vector3.one;
            if (hasChild) childScale = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            if (r.Failed) return;
            try
            {
                var climb = go.GetComponent<Climbable>();
                if (climb != null && points.Count >= 2)
                {
                    climb.autoSet = false;
                    climb.localPoints = false;  // the host's points are in world space
                    climb.points = points;
                    climb.downwardsVelocity = down;
                    climb.CalculateTotalLength();
                }
                var sr = go.GetComponent<SpriteRenderer>();
                if (hasSprite && sr != null)
                {
                    sr.size = size;
                    sr.color = color;
                    sr.flipX = flip;
                }
                if (hasChild && go.transform.childCount > 0) go.transform.GetChild(0).localScale = childScale;
            }
            catch (Exception e) { Log.Debug("climbable state: " + e.Message); }
        }

        private static void ApplyCrystal(GameObject go, CrystalBehaviour crystal, ref PacketReader r)
        {
            var color = new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            float intensity = r.ReadSingle();
            int n = r.ReadCount(32);
            var effects = new List<CrystalEffect>();
            for (int i = 0; i < n && !r.Failed; i++)
            {
                string typeName = r.ReadString();
                var type = typeof(CrystalEffect).Assembly.GetType(typeName);
                CrystalEffect effect = null;
                if (type != null && typeof(CrystalEffect).IsAssignableFrom(type) && crystal != null)
                {
                    try
                    {
                        effect = (CrystalEffect)Activator.CreateInstance(type);
                        effect.crystal = crystal;
                        effect.Start();  // adds the visuals, audio and components it owns
                    }
                    catch (Exception e)
                    {
                        Log.Debug("crystal effect " + typeName + ": " + e.Message);
                        effect = null;
                    }
                }
                FieldState.Read(ref r, effect);  // the host's rolls replace this Start's
                if (effect != null) effects.Add(effect);
            }
            bool hasHeater = r.ReadBool();
            if (hasHeater) FieldState.Read(ref r, go.GetComponent<Heater>());

            if (crystal == null) return;
            Effects(crystal) = effects;
            if (crystal.sprite != null) crystal.sprite.color = color;
            if (crystal.light != null)
            {
                crystal.light.color = color;
                crystal.light.intensity = intensity;
            }
        }
    }

    /// <summary>
    /// Writes and reads an object's simple-typed instance fields by name: numbers, bools,
    /// strings, vectors, colours, enums. Unknown or mismatched fields are skipped, so the
    /// two sides only need to agree on names, not layout.
    /// </summary>
    internal static class FieldState
    {
        private const byte TFloat = 1, TInt = 2, TBool = 3, TString = 4, TVec2 = 5, TVec3 = 6, TColor = 7, TByte = 8, TEnum = 9;

        private static IEnumerable<FieldInfo> Fields(Type type, Type stopAt)
        {
            for (var t = type; t != null && t != stopAt && t != typeof(object); t = t.BaseType)
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (!f.IsLiteral && !f.IsInitOnly && Code(f.FieldType) != 0) yield return f;
        }

        private static byte Code(Type t)
        {
            if (t == typeof(float)) return TFloat;
            if (t == typeof(int)) return TInt;
            if (t == typeof(bool)) return TBool;
            if (t == typeof(string)) return TString;
            if (t == typeof(Vector2)) return TVec2;
            if (t == typeof(Vector3)) return TVec3;
            if (t == typeof(Color)) return TColor;
            if (t == typeof(byte)) return TByte;
            if (t.IsEnum) return TEnum;
            return 0;
        }

        internal static void Write(PacketWriter w, object obj, Type stopAt)
        {
            var fields = new List<FieldInfo>(Fields(obj.GetType(), stopAt));
            w.WriteUInt16((ushort)fields.Count);
            foreach (var f in fields)
            {
                byte code = Code(f.FieldType);
                w.WriteString(f.Name);
                w.WriteByte(code);
                object v = f.GetValue(obj);
                switch (code)
                {
                    case TFloat: w.WriteSingle(Sanitize((float)v)); break;
                    case TInt: w.WriteInt32((int)v); break;
                    case TBool: w.WriteBool((bool)v); break;
                    case TString: w.WriteString((string)v ?? ""); break;
                    case TVec2: { var x = (Vector2)v; w.WriteSingle(Sanitize(x.x)); w.WriteSingle(Sanitize(x.y)); break; }
                    case TVec3: { var x = (Vector3)v; w.WriteSingle(Sanitize(x.x)); w.WriteSingle(Sanitize(x.y)); w.WriteSingle(Sanitize(x.z)); break; }
                    case TColor: { var x = (Color)v; w.WriteSingle(x.r); w.WriteSingle(x.g); w.WriteSingle(x.b); w.WriteSingle(x.a); break; }
                    case TByte: w.WriteByte((byte)v); break;
                    case TEnum: w.WriteInt32(Convert.ToInt32(v)); break;
                }
            }
        }

        /// <summary>Reads fields written by <see cref="Write"/>; with a null target it only skips them.</summary>
        internal static void Read(ref PacketReader r, object target)
        {
            int n = r.ReadCount(256);
            var type = target?.GetType();
            for (int i = 0; i < n && !r.Failed; i++)
            {
                string name = r.ReadString();
                byte code = r.ReadByte();
                object v;
                switch (code)
                {
                    case TFloat: v = r.ReadSingle(); break;
                    case TInt: v = r.ReadInt32(); break;
                    case TBool: v = r.ReadBool(); break;
                    case TString: v = r.ReadString(); break;
                    case TVec2: v = new Vector2(r.ReadSingle(), r.ReadSingle()); break;
                    case TVec3: v = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); break;
                    case TColor: v = new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); break;
                    case TByte: v = r.ReadByte(); break;
                    case TEnum: v = r.ReadInt32(); break;
                    default: r.Fail(); return;
                }
                if (type == null) continue;
                var f = AccessTools.Field(type, name);
                if (f == null || f.IsInitOnly || Code(f.FieldType) != code) continue;
                try { f.SetValue(target, code == TEnum ? Enum.ToObject(f.FieldType, (int)v) : v); }
                catch { /* a field the other build changed; skip it */ }
            }
        }

        private static float Sanitize(float f) => float.IsNaN(f) || float.IsInfinity(f) ? 0f : f;
    }
}
