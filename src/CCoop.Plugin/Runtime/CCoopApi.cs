using System;
using System.Collections.Generic;
using CCoop.Net;
using CCoop.Net.Session;
using CCoop.Plugin.Runtime;

namespace CCoop
{
    /// <summary>
    /// A small API for other BepInEx mods that want their own state shared in a
    /// CasualtiesCoop session.
    ///
    /// Register each message type once, from your plugin's Awake, with your mod's id:
    /// <code>
    /// CCoopApi.Register&lt;MyMessage&gt;("my.mod.id", Delivery.Reliable, (from, m) => { ... });
    /// </code>
    /// then send it with <see cref="Broadcast{T}"/>, <see cref="SendTo{T}"/> or
    /// <see cref="SendToHost{T}"/>. Messages implement <see cref="IMessage"/> from
    /// CCoop.Net.dll; Read must undo Write exactly.
    ///
    /// Every player needs the same mods: each registered type (by mod id and full type
    /// name) is part of the protocol fingerprint, so a player with a different set is
    /// refused when they join, with a version mismatch, instead of misreading packets.
    /// A type registered while a session is running takes effect from the next one.
    /// Handlers run on Unity's main thread.
    /// </summary>
    public static class CCoopApi
    {
        /// <summary>Bumped when this API changes in a way that breaks callers.</summary>
        public const int Version = 1;

        /// <summary>Mod messages get wire ids from here up, in a fixed order (by mod id, then type name).</summary>
        public const ushort FirstModMessageId = 1024;

        private sealed class ModMessage
        {
            public string Mod;
            public Type Type;
            public Action<MessageRegistry, ushort> AddTo;
            public Action<NetSession> Attach;
        }

        private static readonly List<ModMessage> Messages = new List<ModMessage>();

        /// <summary>Registers a message type for your mod. Call once per type, before hosting or joining.</summary>
        public static void Register<T>(string modId, Delivery delivery, Action<byte, T> onReceive) where T : IMessage, new()
        {
            if (string.IsNullOrEmpty(modId)) throw new ArgumentException("a mod id is required", nameof(modId));
            if (onReceive == null) throw new ArgumentNullException(nameof(onReceive));
            foreach (var m in Messages)
                if (m.Type == typeof(T)) throw new ArgumentException(typeof(T).FullName + " is already registered by " + m.Mod);
            Messages.Add(new ModMessage
            {
                Mod = modId,
                Type = typeof(T),
                AddTo = (reg, id) => reg.Add<T>(id, delivery),
                Attach = s => s.On<T>((from, msg) =>
                {
                    try { onReceive(from, msg); }
                    catch (Exception e) { Log.Warn("[api] " + modId + ": handling " + typeof(T).Name + " failed: " + e.Message); }
                }),
            });
            Log.Info("[api] " + modId + " registered " + typeof(T).FullName + (Coop.InSession ? " (from the next session on)" : ""));
        }

        /// <summary>The mod ids and message types registered so far, as "mod/type".</summary>
        public static IList<string> Registered
        {
            get
            {
                var list = new List<string>();
                foreach (var m in Sorted()) list.Add(m.Mod + "/" + m.Type.FullName);
                return list;
            }
        }

        // ------------------------------------------------------------------ session state

        /// <summary>Hosting or joined (even on the menu, before the run).</summary>
        public static bool InSession => Coop.InSession;

        /// <summary>In a run, with the shared layer ready.</summary>
        public static bool InWorld => Coop.WorldLive;

        public static bool IsHost => Coop.IsHost;

        /// <summary>This player's id. The host is always 0.</summary>
        public static byte LocalId => Coop.LocalId;

        /// <summary>Every player in the session (this one included), by id.</summary>
        public static IList<byte> PlayerIds
        {
            get
            {
                var ids = new List<byte>(Coop.Players.Keys);
                ids.Sort();
                return ids;
            }
        }

        public static string PlayerName(byte id) => Coop.Players.ContainsKey(id) ? Coop.Player(id).Name : "";

        // ------------------------------------------------------------------ sending

        /// <summary>To every other player (a client's goes through the host).</summary>
        public static void Broadcast<T>(T msg) where T : IMessage => Coop.Session?.Broadcast(msg);

        public static void SendTo<T>(byte player, T msg) where T : IMessage => Coop.Session?.SendTo(player, msg);

        /// <summary>To the host; on the host itself it arrives in its own handler.</summary>
        public static void SendToHost<T>(T msg) where T : IMessage => Coop.Session?.SendToHost(msg);

        // ------------------------------------------------------------------ internal

        private static List<ModMessage> Sorted()
        {
            var sorted = new List<ModMessage>(Messages);
            sorted.Sort((a, b) => string.CompareOrdinal(a.Mod + "/" + a.Type.FullName, b.Mod + "/" + b.Type.FullName));
            return sorted;
        }

        internal static MessageRegistry AddTo(MessageRegistry registry)
        {
            ushort id = FirstModMessageId;
            foreach (var m in Sorted()) m.AddTo(registry, id++);
            return registry;
        }

        internal static void Attach(NetSession session)
        {
            foreach (var m in Messages) m.Attach(session);
        }
    }
}
