using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using CCoop.Net.Wire;

namespace CCoop.Net
{
    /// <summary>How a message travels.</summary>
    public enum Delivery : byte
    {
        /// <summary>
        /// Reliable and ordered, on one channel per connection. Everything that changes
        /// shared state goes here, including bulk world data: a single ordered stream is
        /// what guarantees a mutation is never applied before the world it belongs to.
        /// </summary>
        Reliable = 0,

        /// <summary>Unreliable, newest-wins. Continuous state such as movement.</summary>
        Unreliable = 1,
    }

    /// <summary>A wire message. Implementations must be symmetric: Read undoes Write.</summary>
    public interface IMessage
    {
        void Write(PacketWriter w);

        /// <summary>Reads the body. Set <c>r.Fail()</c> on any semantic violation.</summary>
        void Read(ref PacketReader r);
    }

    /// <summary>
    /// Maps message types to wire ids. Both ends must register the same set, so the
    /// registry hashes itself and the handshake compares hashes: a peer built from
    /// different message definitions is rejected up front instead of misreading packets.
    /// </summary>
    public sealed class MessageRegistry
    {
        internal sealed class Entry
        {
            public ushort Id;
            public Type Type;
            public Delivery Delivery;
            public Func<IMessage> Create;
        }

        private readonly Dictionary<ushort, Entry> _byId = new Dictionary<ushort, Entry>();
        private readonly Dictionary<Type, Entry> _byType = new Dictionary<Type, Entry>();
        private string _hash;

        /// <summary>Ids below this are reserved for the session layer.</summary>
        public const ushort FirstUserId = 32;

        public MessageRegistry()
        {
            Session.SessionMessages.RegisterInto(this);
        }

        public MessageRegistry Add<T>(ushort id, Delivery delivery) where T : IMessage, new()
        {
            if (_hash != null) throw new InvalidOperationException("registry is frozen");
            if (_byId.ContainsKey(id)) throw new ArgumentException("duplicate message id " + id);
            if (_byType.ContainsKey(typeof(T))) throw new ArgumentException("duplicate message type " + typeof(T).Name);
            var e = new Entry { Id = id, Type = typeof(T), Delivery = delivery, Create = () => new T() };
            _byId[id] = e;
            _byType[typeof(T)] = e;
            return this;
        }

        internal Entry Get(Type t)
        {
            if (!_byType.TryGetValue(t, out var e))
                throw new InvalidOperationException("message " + t.Name + " is not registered");
            return e;
        }

        internal bool TryGet(ushort id, out Entry e) => _byId.TryGetValue(id, out e);

        public int Count => _byId.Count;

        /// <summary>Fingerprint of every (id, type name, delivery). Freezes the registry.</summary>
        public string Hash
        {
            get
            {
                if (_hash != null) return _hash;
                var ids = new List<ushort>(_byId.Keys);
                ids.Sort();
                var sb = new StringBuilder();
                foreach (var id in ids)
                {
                    var e = _byId[id];
                    sb.Append(id).Append(':').Append(e.Type.FullName).Append(':').Append((int)e.Delivery).Append(';');
                }
                using (var sha = SHA256.Create())
                {
                    var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                    _hash = BitConverter.ToString(bytes, 0, 8).Replace("-", "").ToLowerInvariant();
                }
                return _hash;
            }
        }
    }
}
