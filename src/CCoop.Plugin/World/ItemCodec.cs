using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using CCoop.Net.Wire;
using CCoop.Plugin.Runtime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace CCoop.Plugin.World
{
    /// <summary>
    /// An item's full state and, recursively, what is inside it — so an item that changes
    /// hands is the same item on every screen.
    ///
    /// Beyond the basics (id, condition, label, colour, favourite) it carries every
    /// component the game itself marks <c>[Saveable]</c>, serialised exactly the way the
    /// save system does it (Newtonsoft over their <c>[JsonProperty]</c> fields). That is
    /// liquids in containers, battery type and charge, rounds in guns and magazines, a gun's
    /// chamber, safety and slide, blueprint recipes, plush variants — whatever the game
    /// decides to save, including anything a future update adds.
    /// </summary>
    internal static class ItemCodec
    {
        private const byte Version = 2;
        private const int MaxDepth = 4;
        private const int MaxChildren = 128;

        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            Formatting = Formatting.None,
        };

        private static readonly Dictionary<Type, bool> SaveableCache = new Dictionary<Type, bool>();

        private static bool IsSaveable(Type t)
        {
            if (!SaveableCache.TryGetValue(t, out var yes))
            {
                yes = Attribute.GetCustomAttributes(t).Any(a => a is Saveable);
                SaveableCache[t] = yes;
            }
            return yes;
        }

        internal static byte[] Encode(Item item)
        {
            if (item == null) return new byte[0];
            var w = new PacketWriter(128);
            w.WriteByte(Version);
            Write(w, item, 0);
            return w.ToArray();
        }

        private static void Write(PacketWriter w, Item item, int depth)
        {
            w.WriteString(item.id ?? "");
            w.WriteSingle(item.condition);
            w.WriteString(item.label ?? "");
            w.WriteInt32(item.colorSwatch);
            w.WriteBool(item.favourited);

            var parts = new List<KeyValuePair<string, string>>();
            foreach (var c in item.GetComponents<Component>())
            {
                if (c == null || !IsSaveable(c.GetType())) continue;
                try { parts.Add(new KeyValuePair<string, string>(c.GetType().FullName, JsonConvert.SerializeObject(c, Settings))); }
                catch (Exception e) { Log.Debug("item state " + c.GetType().Name + ": " + e.Message); }
            }
            w.WriteUInt16((ushort)parts.Count);
            foreach (var kv in parts)
            {
                w.WriteString(kv.Key);
                w.WriteBlob(Encoding.UTF8.GetBytes(kv.Value));
            }

            // Light modes and similar live in a non-saveable behaviour.
            var custom = item.GetComponent<CustomItemBehaviour>();
            w.WriteBool(custom != null);
            if (custom != null)
            {
                w.WriteInt32(custom.state);
                string data = "";
                try { if (custom.data != null) data = JsonConvert.SerializeObject(custom.data, Settings); }
                catch { data = ""; }
                w.WriteBlob(Encoding.UTF8.GetBytes(data));
            }

            var children = new List<Item>();
            if (depth < MaxDepth && item.GetComponent<Container>() != null)
            {
                foreach (Transform t in item.transform)
                {
                    var child = t.GetComponent<Item>();
                    if (child != null && children.Count < MaxChildren) children.Add(child);
                }
            }
            w.WriteUInt16((ushort)children.Count);
            foreach (var c in children) Write(w, c, depth + 1);
        }

        internal sealed class Spec
        {
            public string Id = "";
            public float Condition = 1f;
            public string Label = "";
            public int Swatch;
            public bool Favourite;
            public List<KeyValuePair<string, string>> Components = new List<KeyValuePair<string, string>>();
            public bool HasCustom;
            public int CustomState;
            public string CustomData = "";
            public List<Spec> Children = new List<Spec>();
        }

        internal static Spec Decode(byte[] data)
        {
            if (data == null || data.Length == 0) return null;
            var r = new PacketReader(data, 0, data.Length);
            if (r.ReadByte() != Version) return null;
            var spec = Read(ref r, 0);
            return r.Failed ? null : spec;
        }

        private static Spec Read(ref PacketReader r, int depth)
        {
            var s = new Spec
            {
                Id = r.ReadString(),
                Condition = r.ReadSingle(),
                Label = r.ReadString(),
                Swatch = r.ReadInt32(),
                Favourite = r.ReadBool(),
            };
            int parts = r.ReadCount(32);
            for (int i = 0; i < parts && !r.Failed; i++)
                s.Components.Add(new KeyValuePair<string, string>(r.ReadString(), Encoding.UTF8.GetString(r.ReadBlob())));
            s.HasCustom = r.ReadBool();
            if (s.HasCustom)
            {
                s.CustomState = r.ReadInt32();
                s.CustomData = Encoding.UTF8.GetString(r.ReadBlob());
            }
            int n = r.ReadCount(MaxChildren);
            if (depth >= MaxDepth && n > 0) { r.Fail(); return s; }
            for (int i = 0; i < n && !r.Failed; i++) s.Children.Add(Read(ref r, depth + 1));
            return s;
        }

        /// <summary>
        /// Applies state to an item, then rebuilds its contents. Call right after the item
        /// is created — after Awake (which resets liquids and batteries to prefab defaults)
        /// and before Start (which derives condition from them).
        /// </summary>
        internal static void Apply(Item item, Spec spec)
        {
            if (item == null || spec == null) return;
            try
            {
                item.condition = spec.Condition;
                item.label = string.IsNullOrEmpty(spec.Label) ? null : spec.Label;
                item.colorSwatch = spec.Swatch;
                item.favourited = spec.Favourite;
            }
            catch (Exception e) { Log.Debug("item basics: " + e.Message); }

            foreach (var kv in spec.Components) ApplyComponent(item.gameObject, kv.Key, kv.Value);

            if (spec.HasCustom)
            {
                var custom = item.GetComponent<CustomItemBehaviour>();
                if (custom != null)
                {
                    custom.state = spec.CustomState;
                    if (!string.IsNullOrEmpty(spec.CustomData))
                    {
                        try { custom.data = Retype(JsonConvert.DeserializeObject<object[]>(spec.CustomData), custom.data); }
                        catch { /* leave the prefab's data */ }
                    }
                }
            }

            // A gun whose slide position differs from its "last seen" one plays the rack
            // and ejects a round on its next Update.
            var gun = item.GetComponent<GunScript>();
            if (gun != null) gun.lastRacked = gun.racked;

            var container = item.GetComponent<Container>();
            if (container == null) return;

            // Whatever the prefab rolled for itself is replaced by what the owner had.
            for (int i = item.transform.childCount - 1; i >= 0; i--)
            {
                var child = item.transform.GetChild(i);
                if (child.GetComponent<Item>() != null) UnityEngine.Object.Destroy(child.gameObject);
            }
            foreach (var c in spec.Children)
            {
                var go = Utils.Create(c.Id, item.transform.position, 0f);
                if (go == null) continue;
                var childItem = go.GetComponent<Item>();
                if (childItem == null) { UnityEngine.Object.Destroy(go); continue; }
                Apply(childItem, c);
                try { container.LoadItem(childItem); }
                catch (Exception e) { Log.Debug("container load: " + e.Message); }
            }
        }

        /// <summary>Mirrors SaveSystem's loader: public fields by name, Newtonsoft conversion.</summary>
        private static void ApplyComponent(GameObject go, string typeName, string json)
        {
            var type = typeof(Item).Assembly.GetType(typeName);
            if (type == null || !IsSaveable(type)) return;
            var component = go.GetComponent(type);
            if (component == null) return;
            JObject obj;
            try { obj = JObject.Parse(json); }
            catch { return; }
            foreach (var prop in obj.Properties())
            {
                var field = type.GetField(prop.Name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field == null) continue;
                try { field.SetValue(component, prop.Value.ToObject(field.FieldType)); }
                catch (Exception e) { Log.Debug("item field " + typeName + "." + prop.Name + ": " + e.Message); }
            }
        }

        /// <summary>
        /// JSON gives back numbers as double and long, but the game's item scripts unbox
        /// their data as float or int ((float)data[0]) and throw on anything else. Each value
        /// takes the type the item's own data had there (or float / int when it had none).
        /// </summary>
        private static object[] Retype(object[] values, object[] template)
        {
            if (values == null) return null;
            for (int i = 0; i < values.Length; i++)
            {
                object want = template != null && i < template.Length ? template[i] : null;
                object v = values[i];
                try
                {
                    if (want is float || (want == null && v is double)) values[i] = Convert.ToSingle(v, System.Globalization.CultureInfo.InvariantCulture);
                    else if (want is int || (want == null && v is long)) values[i] = Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture);
                    else if (want is bool && !(v is bool)) values[i] = Convert.ToBoolean(v, System.Globalization.CultureInfo.InvariantCulture);
                    else if (want is double) values[i] = Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture);
                }
                catch { if (want != null) values[i] = want; }
            }
            return values;
        }
    }
}
