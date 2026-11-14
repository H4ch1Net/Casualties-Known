using System.Collections.Generic;
using CCoop.Net.Wire;

namespace CCoop.Net.Session
{
    /// <summary>Messages owned by the session layer itself (ids 1..31).</summary>
    internal static class SessionMessages
    {
        public const ushort HelloId = 1;
        public const ushort WelcomeId = 2;
        public const ushort RejectId = 3;
        public const ushort PeerJoinedId = 4;
        public const ushort PeerLeftId = 5;
        public const ushort BlobChunkId = 6;
        public const ushort PingTableId = 7;

        public static void RegisterInto(MessageRegistry r)
        {
            r.Add<Hello>(HelloId, Delivery.Reliable)
             .Add<Welcome>(WelcomeId, Delivery.Reliable)
             .Add<Reject>(RejectId, Delivery.Reliable)
             .Add<PeerJoined>(PeerJoinedId, Delivery.Reliable)
             .Add<PeerLeft>(PeerLeftId, Delivery.Reliable)
             .Add<BlobChunk>(BlobChunkId, Delivery.Reliable)
             .Add<PingTable>(PingTableId, Delivery.Unreliable);
        }
    }

    /// <summary>Client -> host: who I am and what I was built from.</summary>
    public sealed class Hello : IMessage
    {
        public string RegistryHash = "";
        public string ModVersion = "";
        public string GameBuild = "";
        public string PlayerName = "";
        public string Password = "";

        public void Write(PacketWriter w)
        {
            w.WriteString(RegistryHash);
            w.WriteString(ModVersion);
            w.WriteString(GameBuild);
            w.WriteString(PlayerName);
            w.WriteString(Password);
        }

        public void Read(ref PacketReader r)
        {
            RegistryHash = r.ReadString();
            ModVersion = r.ReadString();
            GameBuild = r.ReadString();
            PlayerName = r.ReadString();
            Password = r.ReadString();
        }
    }

    /// <summary>Host -> client: accepted; your id and who is already here.</summary>
    public sealed class Welcome : IMessage
    {
        public byte YourId;
        public List<PeerInfo> Peers = new List<PeerInfo>();

        public void Write(PacketWriter w)
        {
            w.WriteByte(YourId);
            w.WriteUInt16((ushort)Peers.Count);
            foreach (var p in Peers)
            {
                w.WriteByte(p.Id);
                w.WriteString(p.Name);
            }
        }

        public void Read(ref PacketReader r)
        {
            YourId = r.ReadByte();
            int n = r.ReadCount(NetSession.MaxPeers);
            Peers.Clear();
            for (int i = 0; i < n; i++) Peers.Add(new PeerInfo(r.ReadByte(), r.ReadString()));
        }
    }

    /// <summary>Host -> client: refused or kicked, with a reason a player can read.</summary>
    public sealed class Reject : IMessage
    {
        public string Reason = "";
        public void Write(PacketWriter w) => w.WriteString(Reason);
        public void Read(ref PacketReader r) => Reason = r.ReadString();
    }

    public sealed class PeerJoined : IMessage
    {
        public byte Id;
        public string Name = "";

        public void Write(PacketWriter w)
        {
            w.WriteByte(Id);
            w.WriteString(Name);
        }

        public void Read(ref PacketReader r)
        {
            Id = r.ReadByte();
            Name = r.ReadString();
        }
    }

    public sealed class PeerLeft : IMessage
    {
        public byte Id;
        public string Reason = "";

        public void Write(PacketWriter w)
        {
            w.WriteByte(Id);
            w.WriteString(Reason);
        }

        public void Read(ref PacketReader r)
        {
            Id = r.ReadByte();
            Reason = r.ReadString();
        }
    }

    /// <summary>One piece of a large payload, reassembled by the receiving session.</summary>
    public sealed class BlobChunk : IMessage
    {
        public uint TransferId;
        public ushort Tag;
        public ushort Index;
        public ushort Count;
        public int TotalLength;
        public byte[] Data = new byte[0];

        public void Write(PacketWriter w)
        {
            w.WriteUInt32(TransferId);
            w.WriteUInt16(Tag);
            w.WriteUInt16(Index);
            w.WriteUInt16(Count);
            w.WriteInt32(TotalLength);
            w.WriteBlob(Data);
        }

        public void Read(ref PacketReader r)
        {
            TransferId = r.ReadUInt32();
            Tag = r.ReadUInt16();
            Index = r.ReadUInt16();
            Count = r.ReadUInt16();
            TotalLength = r.ReadInt32();
            Data = r.ReadBlob();
            if (Count == 0 || Index >= Count || TotalLength < 0 || TotalLength > NetSession.MaxBlobTotal) r.Fail();
        }
    }

    /// <summary>Host -> all: everyone's round trip and link quality, for the player list.</summary>
    public sealed class PingTable : IMessage
    {
        public struct Entry
        {
            public byte Peer;
            public ushort RoundTripMs;

            /// <summary>0..254 = quality 0..1; 255 = unknown.</summary>
            public byte Quality;
        }

        public List<Entry> Entries = new List<Entry>();

        public void Write(PacketWriter w)
        {
            w.WriteUInt16((ushort)Entries.Count);
            foreach (var e in Entries)
            {
                w.WriteByte(e.Peer);
                w.WriteUInt16(e.RoundTripMs);
                w.WriteByte(e.Quality);
            }
        }

        public void Read(ref PacketReader r)
        {
            int n = r.ReadCount(NetSession.MaxPeers);
            Entries.Clear();
            for (int i = 0; i < n; i++)
                Entries.Add(new Entry { Peer = r.ReadByte(), RoundTripMs = r.ReadUInt16(), Quality = r.ReadByte() });
        }
    }
}
