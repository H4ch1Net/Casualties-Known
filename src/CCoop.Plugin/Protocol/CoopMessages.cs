using System.Collections.Generic;
using CCoop.Net;
using CCoop.Net.Wire;

namespace CCoop.Plugin.Protocol
{
    /// <summary>The full gameplay message set. Ids are permanent; never reuse one.</summary>
    internal static class CoopProtocol
    {
        public const ushort BlobLayer = 1;

        /// <summary>Host -> one client: terrain chunks it asked for after a desync.</summary>
        public const ushort BlobChunks = 2;

        public static MessageRegistry Build()
        {
            ushort b = MessageRegistry.FirstUserId;
            var registry = new MessageRegistry()
                // players
                .Add<PlayerProfile>((ushort)(b + 0), Delivery.Reliable)
                .Add<PlayerState>((ushort)(b + 1), Delivery.Unreliable)
                .Add<PlayerLook>((ushort)(b + 2), Delivery.Unreliable)
                .Add<PlayerKit>((ushort)(b + 3), Delivery.Unreliable)
                .Add<PlayerAttack>((ushort)(b + 4), Delivery.Reliable)
                .Add<PlayerTalk>((ushort)(b + 5), Delivery.Reliable)
                .Add<ChatLine>((ushort)(b + 6), Delivery.Reliable)
                // run and layer flow
                .Add<RunInfo>((ushort)(b + 10), Delivery.Reliable)
                .Add<LayerBegin>((ushort)(b + 11), Delivery.Reliable)
                .Add<LayerRequest>((ushort)(b + 12), Delivery.Reliable)
                .Add<LayerStatus>((ushort)(b + 13), Delivery.Reliable)
                // world
                .Add<BlockEdits>((ushort)(b + 20), Delivery.Reliable)
                .Add<BlockCracks>((ushort)(b + 21), Delivery.Reliable)
                .Add<EntityDamage>((ushort)(b + 22), Delivery.Reliable)
                .Add<EntityKill>((ushort)(b + 23), Delivery.Reliable)
                .Add<EntityDespawn>((ushort)(b + 24), Delivery.Reliable)
                .Add<EntitySpawn>((ushort)(b + 25), Delivery.Reliable)
                .Add<EntityMotion>((ushort)(b + 26), Delivery.Unreliable)
                .Add<EntityRest>((ushort)(b + 27), Delivery.Reliable)
                .Add<ItemTake>((ushort)(b + 28), Delivery.Reliable)
                .Add<ItemTaken>((ushort)(b + 29), Delivery.Reliable)
                .Add<CreatureOwners>((ushort)(b + 30), Delivery.Reliable)
                .Add<CreatureStates>((ushort)(b + 31), Delivery.Unreliable)
                .Add<PropUse>((ushort)(b + 32), Delivery.Reliable)
                .Add<ExplosionEvent>((ushort)(b + 33), Delivery.Reliable)
                .Add<ExplosionRequest>((ushort)(b + 34), Delivery.Reliable)
                .Add<EntityAction>((ushort)(b + 35), Delivery.Reliable)
                .Add<EntityGone>((ushort)(b + 36), Delivery.Reliable)
                .Add<WorldClock>((ushort)(b + 37), Delivery.Unreliable)
                // clock
                .Add<TimeScale>((ushort)(b + 40), Delivery.Reliable)
                .Add<Earthquake>((ushort)(b + 41), Delivery.Reliable)
                // life
                .Add<TeamWipe>((ushort)(b + 45), Delivery.Reliable)
                .Add<RejoinPlace>((ushort)(b + 46), Delivery.Reliable)
                .Add<RulesMessage>((ushort)(b + 47), Delivery.Reliable)
                // team
                .Add<VoiceData>((ushort)(b + 50), Delivery.Unreliable)
                .Add<PingMark>((ushort)(b + 51), Delivery.Reliable)
                .Add<CreatureBite>((ushort)(b + 52), Delivery.Reliable)
                .Add<ThreatPing>((ushort)(b + 53), Delivery.Unreliable)
                .Add<ExitStatus>((ushort)(b + 54), Delivery.Reliable)
                // care
                .Add<Vitals>((ushort)(b + 60), Delivery.Unreliable)
                .Add<TreatRequest>((ushort)(b + 61), Delivery.Reliable)
                .Add<TreatResult>((ushort)(b + 62), Delivery.Reliable)
                .Add<GiveOffer>((ushort)(b + 63), Delivery.Reliable)
                .Add<GiveAnswer>((ushort)(b + 64), Delivery.Reliable)
                .Add<GiveDone>((ushort)(b + 65), Delivery.Reliable)
                .Add<CarryCommand>((ushort)(b + 66), Delivery.Reliable)
                .Add<CprPulse>((ushort)(b + 67), Delivery.Reliable)
                .Add<TreatStep>((ushort)(b + 68), Delivery.Reliable)
                .Add<LootList>((ushort)(b + 69), Delivery.Reliable)
                .Add<LootTake>((ushort)(b + 70), Delivery.Reliable)
                .Add<SpawnPoint>((ushort)(b + 71), Delivery.Reliable)
                .Add<GestureMsg>((ushort)(b + 72), Delivery.Reliable)
                .Add<TeamMove>((ushort)(b + 73), Delivery.Reliable)
                // M5: the rest of the world
                .Add<WorldEvent>((ushort)(b + 74), Delivery.Reliable)
                .Add<ShotFired>((ushort)(b + 75), Delivery.Reliable)
                .Add<TraderTrade>((ushort)(b + 76), Delivery.Reliable)
                .Add<WorldFingerprint>((ushort)(b + 77), Delivery.Reliable)
                .Add<ChunkResync>((ushort)(b + 78), Delivery.Reliable)
                .Add<ItemConditions>((ushort)(b + 79), Delivery.Reliable)
                .Add<CraftClaim>((ushort)(b + 80), Delivery.Reliable)
                .Add<GiveAsk>((ushort)(b + 81), Delivery.Reliable)
                .Add<FluidChunks>((ushort)(b + 82), Delivery.Reliable);
            // Other mods' messages (CCoopApi), after ours, so they share the fingerprint.
            return CCoop.CCoopApi.AddTo(registry);
        }
    }

    // ===================================================================== players

    internal sealed class PlayerProfile : IMessage
    {
        public byte Character;
        public ulong SteamId;

        /// <summary>The sender's frame rate (smoothed, capped at 255): a slow host slows everyone.</summary>
        public byte Fps;

        public void Write(PacketWriter w)
        {
            w.WriteByte(Character);
            w.WriteUInt64(SteamId);
            w.WriteByte(Fps);
        }

        public void Read(ref PacketReader r)
        {
            Character = r.ReadByte();
            SteamId = r.ReadUInt64();
            Fps = r.ReadByte();
        }
    }

    /// <summary>A limb relative to its body: position, angle, and whether it is mirrored.</summary>
    internal struct LimbPose
    {
        public float X, Y, Angle;
        public byte Flags;  // 1 = negative x scale, 2 = sprite flipX
    }

    [System.Flags]
    internal enum PlayerFlags : byte
    {
        None = 0,
        InWorld = 1,
        Conscious = 2,
        Alive = 4,
        Right = 8,
        Sleeping = 16,
        BodyFlip = 32,

        /// <summary>Brain dying (the game slows its fast-forward to 3.5x for this).</summary>
        Dying = 64,

        /// <summary>Reached an ending: on the end screen, done with the run.</summary>
        Finished = 128,
    }

    /// <summary>Movement and pose, 20 times a second. Newest wins.</summary>
    internal sealed class PlayerState : IMessage
    {
        public const int MaxLimbs = 24;
        public uint Sequence;
        public ushort Epoch;
        public PlayerFlags Flags;
        public float X, Y, VX, VY, Angle;
        public byte Blood;
        public LimbPose[] Limbs = new LimbPose[0];

        public void Write(PacketWriter w)
        {
            w.WriteUInt32(Sequence);
            w.WriteUInt16(Epoch);
            w.WriteByte((byte)Flags);
            w.WriteSingle(X);
            w.WriteSingle(Y);
            w.WriteQuantized(VX, 128f);
            w.WriteQuantized(VY, 128f);
            w.WriteAngle(Angle);
            w.WriteByte(Blood);
            w.WriteByte((byte)Limbs.Length);
            foreach (var l in Limbs)
            {
                w.WriteQuantized(l.X, 8f);
                w.WriteQuantized(l.Y, 8f);
                w.WriteAngle(l.Angle);
                w.WriteByte(l.Flags);
            }
        }

        public void Read(ref PacketReader r)
        {
            Sequence = r.ReadUInt32();
            Epoch = r.ReadUInt16();
            Flags = (PlayerFlags)r.ReadByte();
            X = r.ReadSingle();
            Y = r.ReadSingle();
            VX = r.ReadQuantized(128f);
            VY = r.ReadQuantized(128f);
            Angle = r.ReadAngle();
            Blood = r.ReadByte();
            int n = r.ReadByte();
            if (n > MaxLimbs) { r.Fail(); return; }
            Limbs = new LimbPose[n];
            for (int i = 0; i < n; i++)
                Limbs[i] = new LimbPose { X = r.ReadQuantized(8f), Y = r.ReadQuantized(8f), Angle = r.ReadAngle(), Flags = r.ReadByte() };
        }
    }

    /// <summary>The shader values that draw wounds, blood, dirt and wetness.</summary>
    internal sealed class PlayerLook : IMessage
    {
        public byte Wet, Snow, Dirt;
        public byte[] Limbs = new byte[0];  // 5 bytes per limb
        public const int PerLimb = 5;

        public void Write(PacketWriter w)
        {
            w.WriteByte(Wet);
            w.WriteByte(Snow);
            w.WriteByte(Dirt);
            w.WriteBlob(Limbs);
        }

        public void Read(ref PacketReader r)
        {
            Wet = r.ReadByte();
            Snow = r.ReadByte();
            Dirt = r.ReadByte();
            Limbs = r.ReadBlob();
            if (Limbs.Length % PerLimb != 0 || Limbs.Length > PerLimb * PlayerState.MaxLimbs) r.Fail();
        }
    }

    /// <summary>What a player is holding and wearing, by slot.</summary>
    internal sealed class PlayerKit : IMessage
    {
        public List<KeyValuePair<byte, string>> Items = new List<KeyValuePair<byte, string>>();

        public void Write(PacketWriter w)
        {
            w.WriteUInt16((ushort)Items.Count);
            foreach (var kv in Items)
            {
                w.WriteByte(kv.Key);
                w.WriteString(kv.Value);
            }
        }

        public void Read(ref PacketReader r)
        {
            int n = r.ReadCount(64);
            Items.Clear();
            for (int i = 0; i < n; i++) Items.Add(new KeyValuePair<byte, string>(r.ReadByte(), r.ReadString()));
        }
    }

    internal sealed class PlayerAttack : IMessage
    {
        public string Anim = "";
        public float Rotation;
        public bool Right;
        public string Sound = "";

        public void Write(PacketWriter w)
        {
            w.WriteString(Anim);
            w.WriteAngle(Rotation);
            w.WriteBool(Right);
            w.WriteString(Sound);
        }

        public void Read(ref PacketReader r)
        {
            Anim = r.ReadString();
            Rotation = r.ReadAngle();
            Right = r.ReadBool();
            Sound = r.ReadString();
        }
    }

    /// <summary>A line of the character's own dialogue (the speech bubble).</summary>
    internal sealed class PlayerTalk : IMessage
    {
        public string Text = "";
        public void Write(PacketWriter w) => w.WriteString(Text);
        public void Read(ref PacketReader r) => Text = r.ReadString();
    }

    /// <summary>A chat line: plain (menus), spoken by the character, or a ghost's (dead, heard only by the dead).</summary>
    internal sealed class ChatLine : IMessage
    {
        public const byte Plain = 0, Spoken = 1, Ghost = 2;

        public string Text = "";
        public byte Kind;

        public void Write(PacketWriter w)
        {
            w.WriteString(Text);
            w.WriteByte(Kind);
        }

        public void Read(ref PacketReader r)
        {
            Text = r.ReadString();
            Kind = r.ReadByte();
        }
    }

    // ===================================================================== run / layer

    internal struct RunSettingValue
    {
        public string Key;
        public byte Kind;  // 0 float, 1 int, 2 bool, 3 string
        public float F;
        public int I;
        public string S;
    }

    /// <summary>Host -> all: whether the host is in a run, and its settings.</summary>
    internal sealed class RunInfo : IMessage
    {
        public bool InRun;
        public ushort Epoch;
        public int Depth;

        /// <summary>The run's id, for co-op saves.</summary>
        public string RunId = "";

        public List<RunSettingValue> Settings = new List<RunSettingValue>();

        public void Write(PacketWriter w)
        {
            w.WriteBool(InRun);
            w.WriteUInt16(Epoch);
            w.WriteInt32(Depth);
            w.WriteString(RunId);
            w.WriteUInt16((ushort)Settings.Count);
            foreach (var s in Settings)
            {
                w.WriteString(s.Key);
                w.WriteByte(s.Kind);
                switch (s.Kind)
                {
                    case 0: w.WriteSingle(s.F); break;
                    case 1: w.WriteInt32(s.I); break;
                    case 2: w.WriteBool(s.I != 0); break;
                    default: w.WriteString(s.S); break;
                }
            }
        }

        public void Read(ref PacketReader r)
        {
            InRun = r.ReadBool();
            Epoch = r.ReadUInt16();
            Depth = r.ReadInt32();
            RunId = r.ReadString();
            int n = r.ReadCount(512);
            Settings.Clear();
            for (int i = 0; i < n; i++)
            {
                var s = new RunSettingValue { Key = r.ReadString(), Kind = r.ReadByte() };
                switch (s.Kind)
                {
                    case 0: s.F = r.ReadSingle(); break;
                    case 1: s.I = r.ReadInt32(); break;
                    case 2: s.I = r.ReadBool() ? 1 : 0; break;
                    case 3: s.S = r.ReadString(); break;
                    default: r.Fail(); return;
                }
                Settings.Add(s);
            }
        }
    }

    /// <summary>Host -> all: the host is building a new layer; follow it.</summary>
    internal sealed class LayerBegin : IMessage
    {
        public ushort Epoch;
        public int Depth;

        /// <summary>The team went down in a drill pod.</summary>
        public bool Pod;

        public void Write(PacketWriter w)
        {
            w.WriteUInt16(Epoch);
            w.WriteInt32(Depth);
            w.WriteBool(Pod);
        }

        public void Read(ref PacketReader r)
        {
            Epoch = r.ReadUInt16();
            Depth = r.ReadInt32();
            Pod = r.ReadBool();
        }
    }

    /// <summary>Client -> host: I reached a way down (exit, drill pod) — please descend.</summary>
    internal sealed class LayerRequest : IMessage
    {
        public bool Twice;
        public int TargetDepth = -1;

        /// <summary>Withdraws this player's vote (left the exit, died).</summary>
        public bool Cancel;

        public void Write(PacketWriter w)
        {
            w.WriteBool(Twice);
            w.WriteInt32(TargetDepth);
            w.WriteBool(Cancel);
        }

        public void Read(ref PacketReader r)
        {
            Twice = r.ReadBool();
            TargetDepth = r.ReadInt32();
            Cancel = r.ReadBool();
        }
    }

    internal enum LayerPhase : byte
    {
        InMenu = 0,
        Loading = 1,
        /// <summary>Empty world scaffold built; send me the snapshot.</summary>
        Waiting = 2,
        Applied = 3,
        Failed = 4,
    }

    /// <summary>Client -> host: where I am in joining the current layer.</summary>
    internal sealed class LayerStatus : IMessage
    {
        public ushort Epoch;
        public LayerPhase Phase;
        public string Detail = "";

        public void Write(PacketWriter w)
        {
            w.WriteUInt16(Epoch);
            w.WriteByte((byte)Phase);
            w.WriteString(Detail);
        }

        public void Read(ref PacketReader r)
        {
            Epoch = r.ReadUInt16();
            Phase = (LayerPhase)r.ReadByte();
            Detail = r.ReadString();
            if ((byte)Phase > 4) r.Fail();
        }
    }

    // ===================================================================== world

    internal struct BlockEdit
    {
        public ushort X, Y, Block;
    }

    /// <summary>
    /// Cells and the block each now holds. Absolute, so applying twice is harmless and
    /// two players digging the same wall converge.
    /// </summary>
    internal sealed class BlockEdits : IMessage
    {
        public const int Max = 4096;
        public List<BlockEdit> Edits = new List<BlockEdit>();

        public void Write(PacketWriter w)
        {
            w.WriteUInt16((ushort)Edits.Count);
            foreach (var e in Edits)
            {
                w.WriteUInt16(e.X);
                w.WriteUInt16(e.Y);
                w.WriteUInt16(e.Block);
            }
        }

        public void Read(ref PacketReader r)
        {
            int n = r.ReadCount(Max);
            Edits.Clear();
            for (int i = 0; i < n; i++) Edits.Add(new BlockEdit { X = r.ReadUInt16(), Y = r.ReadUInt16(), Block = r.ReadUInt16() });
        }
    }

    internal struct BlockCrack
    {
        public ushort X, Y;
        public float Damage;
    }

    /// <summary>Accumulated damage on cells (the crack overlay), as totals.</summary>
    internal sealed class BlockCracks : IMessage
    {
        public const int Max = 1024;
        public List<BlockCrack> Cracks = new List<BlockCrack>();

        public void Write(PacketWriter w)
        {
            w.WriteUInt16((ushort)Cracks.Count);
            foreach (var c in Cracks)
            {
                w.WriteUInt16(c.X);
                w.WriteUInt16(c.Y);
                w.WriteSingle(c.Damage);
            }
        }

        public void Read(ref PacketReader r)
        {
            int n = r.ReadCount(Max);
            Cracks.Clear();
            for (int i = 0; i < n; i++) Cracks.Add(new BlockCrack { X = r.ReadUInt16(), Y = r.ReadUInt16(), Damage = r.ReadSingle() });
        }
    }

    internal struct DamageEntry
    {
        public uint Id;
        public float Amount;
    }

    /// <summary>Damage dealt to world objects, as amounts so two players' hits add up.</summary>
    internal sealed class EntityDamage : IMessage
    {
        public const int Max = 512;
        public List<DamageEntry> Hits = new List<DamageEntry>();

        public void Write(PacketWriter w)
        {
            w.WriteUInt16((ushort)Hits.Count);
            foreach (var h in Hits)
            {
                w.WriteUInt32(h.Id);
                w.WriteSingle(h.Amount);
            }
        }

        public void Read(ref PacketReader r)
        {
            int n = r.ReadCount(Max);
            Hits.Clear();
            for (int i = 0; i < n; i++) Hits.Add(new DamageEntry { Id = r.ReadUInt32(), Amount = r.ReadSingle() });
        }
    }

    /// <summary>Client -> host: my copy of this object just died; please break it for everyone.</summary>
    internal sealed class EntityKill : IMessage
    {
        public uint Id;
        public void Write(PacketWriter w) => w.WriteUInt32(Id);
        public void Read(ref PacketReader r) => Id = r.ReadUInt32();
    }

    internal struct FluidChunk
    {
        public ushort X, Y;
        /// <summary>Run-length pairs (count, liquid) over the 16x16 cells, column by column.</summary>
        public byte[] Cells;
    }

    /// <summary>Liquid patches that changed on the machine simulating them.</summary>
    internal sealed class FluidChunks : IMessage
    {
        public const int Max = 64;
        public List<FluidChunk> Chunks = new List<FluidChunk>();

        public void Write(PacketWriter w)
        {
            w.WriteUInt16((ushort)Chunks.Count);
            foreach (var c in Chunks)
            {
                w.WriteUInt16(c.X);
                w.WriteUInt16(c.Y);
                w.WriteBlob(c.Cells);
            }
        }

        public void Read(ref PacketReader r)
        {
            int n = r.ReadCount(Max);
            Chunks.Clear();
            for (int i = 0; i < n; i++) Chunks.Add(new FluidChunk { X = r.ReadUInt16(), Y = r.ReadUInt16(), Cells = r.ReadBlob() });
        }
    }

    internal struct ConditionEntry
    {
        public uint Id;
        public float Condition;
    }

    /// <summary>Host -> all: loose items whose condition changed (rot, rust), decided by the host.</summary>
    internal sealed class ItemConditions : IMessage
    {
        public const int Max = 400;
        public List<ConditionEntry> Items = new List<ConditionEntry>();

        public void Write(PacketWriter w)
        {
            w.WriteUInt16((ushort)Items.Count);
            foreach (var e in Items)
            {
                w.WriteUInt32(e.Id);
                float c = e.Condition < 0f ? 0f : e.Condition > 1f ? 1f : e.Condition;
                w.WriteUInt16((ushort)(c * 65535f + 0.5f));
            }
        }

        public void Read(ref PacketReader r)
        {
            int n = r.ReadCount(Max);
            Items.Clear();
            for (int i = 0; i < n; i++) Items.Add(new ConditionEntry { Id = r.ReadUInt32(), Condition = r.ReadUInt16() / 65535f });
        }
    }

    internal enum CraftOp : byte
    {
        Ask,
        Granted,
        Denied,
    }

    /// <summary>
    /// Crafting with items on the ground: a client asks the host for them first, so two
    /// players can't use up the same item.
    /// </summary>
    internal sealed class CraftClaim : IMessage
    {
        public const int Max = 32;
        public CraftOp Op;
        public byte Request;
        public List<uint> Ids = new List<uint>();

        public void Write(PacketWriter w)
        {
            w.WriteByte((byte)Op);
            w.WriteByte(Request);
            w.WriteUInt16((ushort)Ids.Count);
            foreach (var id in Ids) w.WriteUInt32(id);
        }

        public void Read(ref PacketReader r)
        {
            Op = (CraftOp)r.ReadByte();
            Request = r.ReadByte();
            int n = r.ReadCount(Max);
            Ids.Clear();
            for (int i = 0; i < n; i++) Ids.Add(r.ReadUInt32());
        }
    }

    /// <summary>An object is gone. Broken plays the game's own break effects.</summary>
    internal sealed class EntityDespawn : IMessage
    {
        public uint Id;
        public bool Broken;

        public void Write(PacketWriter w)
        {
            w.WriteUInt32(Id);
            w.WriteBool(Broken);
        }

        public void Read(ref PacketReader r)
        {
            Id = r.ReadUInt32();
            Broken = r.ReadBool();
        }
    }

    /// <summary>A new world object: dropped or thrown item, loot, a spawned creature.</summary>
    internal sealed class EntitySpawn : IMessage
    {
        public uint Id;
        public string Key = "";
        public bool IsItem;
        public float X, Y, Rotation, VX, VY, Spin;
        public bool Fresh;
        /// <summary>Item state (condition, label, contents...), see ItemCodec.</summary>
        public byte[] State = new byte[0];

        public void Write(PacketWriter w)
        {
            w.WriteUInt32(Id);
            w.WriteString(Key);
            w.WriteBool(IsItem);
            w.WriteSingle(X);
            w.WriteSingle(Y);
            w.WriteAngle(Rotation);
            w.WriteSingle(VX);
            w.WriteSingle(VY);
            w.WriteSingle(Spin);
            w.WriteBool(Fresh);
            w.WriteBlob(State);
        }

        public void Read(ref PacketReader r)
        {
            Id = r.ReadUInt32();
            Key = r.ReadString();
            IsItem = r.ReadBool();
            X = r.ReadSingle();
            Y = r.ReadSingle();
            Rotation = r.ReadAngle();
            VX = r.ReadSingle();
            VY = r.ReadSingle();
            Spin = r.ReadSingle();
            Fresh = r.ReadBool();
            State = r.ReadBlob();
        }
    }

    internal struct BodyMotion
    {
        public uint Id;
        public float X, Y, Rotation, VX, VY, Spin;

        public void Write(PacketWriter w)
        {
            w.WriteUInt32(Id);
            w.WriteSingle(X);
            w.WriteSingle(Y);
            w.WriteAngle(Rotation);
            w.WriteQuantized(VX, 128f);
            w.WriteQuantized(VY, 128f);
            w.WriteQuantized(Spin, 2000f);
        }

        public static BodyMotion Read(ref PacketReader r) => new BodyMotion
        {
            Id = r.ReadUInt32(), X = r.ReadSingle(), Y = r.ReadSingle(), Rotation = r.ReadAngle(),
            VX = r.ReadQuantized(128f), VY = r.ReadQuantized(128f), Spin = r.ReadQuantized(2000f),
        };
    }

    /// <summary>Loose items in motion, from their owner. Newest wins.</summary>
    internal sealed class EntityMotion : IMessage
    {
        public const int Max = 48;
        public List<BodyMotion> Bodies = new List<BodyMotion>();

        public void Write(PacketWriter w)
        {
            w.WriteUInt16((ushort)Bodies.Count);
            foreach (var b in Bodies) b.Write(w);
        }

        public void Read(ref PacketReader r)
        {
            int n = r.ReadCount(Max);
            Bodies.Clear();
            for (int i = 0; i < n; i++) Bodies.Add(BodyMotion.Read(ref r));
        }
    }

    /// <summary>Where loose items came to rest. Reliable: this is what makes peers agree.</summary>
    internal sealed class EntityRest : IMessage
    {
        public const int Max = 256;
        public List<BodyMotion> Bodies = new List<BodyMotion>();

        public void Write(PacketWriter w)
        {
            w.WriteUInt16((ushort)Bodies.Count);
            foreach (var b in Bodies) b.Write(w);
        }

        public void Read(ref PacketReader r)
        {
            int n = r.ReadCount(Max);
            Bodies.Clear();
            for (int i = 0; i < n; i++) Bodies.Add(BodyMotion.Read(ref r));
        }
    }

    /// <summary>Client -> host: may I pick this item up?</summary>
    internal sealed class ItemTake : IMessage
    {
        public uint Id;
        public byte Slot;

        public void Write(PacketWriter w)
        {
            w.WriteUInt32(Id);
            w.WriteByte(Slot);
        }

        public void Read(ref PacketReader r)
        {
            Id = r.ReadUInt32();
            Slot = r.ReadByte();
        }
    }

    /// <summary>Host -> all: who got the item (or that the asker did not).</summary>
    internal sealed class ItemTaken : IMessage
    {
        public uint Id;
        public byte Taker;
        public byte Slot;
        public bool Granted;

        public void Write(PacketWriter w)
        {
            w.WriteUInt32(Id);
            w.WriteByte(Taker);
            w.WriteByte(Slot);
            w.WriteBool(Granted);
        }

        public void Read(ref PacketReader r)
        {
            Id = r.ReadUInt32();
            Taker = r.ReadByte();
            Slot = r.ReadByte();
            Granted = r.ReadBool();
        }
    }

    /// <summary>Host -> all: which peer simulates which creature.</summary>
    internal sealed class CreatureOwners : IMessage
    {
        public const int Max = 2048;
        public List<KeyValuePair<uint, byte>> Owners = new List<KeyValuePair<uint, byte>>();

        public void Write(PacketWriter w)
        {
            w.WriteUInt16((ushort)Owners.Count);
            foreach (var kv in Owners)
            {
                w.WriteUInt32(kv.Key);
                w.WriteByte(kv.Value);
            }
        }

        public void Read(ref PacketReader r)
        {
            int n = r.ReadCount(Max);
            Owners.Clear();
            for (int i = 0; i < n; i++) Owners.Add(new KeyValuePair<uint, byte>(r.ReadUInt32(), r.ReadByte()));
        }
    }

    /// <summary>Creature positions from the peer simulating them. Newest wins.</summary>
    internal sealed class CreatureStates : IMessage
    {
        public const int Max = 48;
        public List<BodyMotion> Bodies = new List<BodyMotion>();

        public void Write(PacketWriter w)
        {
            w.WriteUInt16((ushort)Bodies.Count);
            foreach (var b in Bodies) b.Write(w);
        }

        public void Read(ref PacketReader r)
        {
            int n = r.ReadCount(Max);
            Bodies.Clear();
            for (int i = 0; i < n; i++) Bodies.Add(BodyMotion.Read(ref r));
        }
    }

    /// <summary>A structure prop was used. Addressed by position: props have no network id.</summary>
    internal sealed class PropUse : IMessage
    {
        public float X, Y;
        public byte State;

        public void Write(PacketWriter w)
        {
            w.WriteSingle(X);
            w.WriteSingle(Y);
            w.WriteByte(State);
        }

        public void Read(ref PacketReader r)
        {
            X = r.ReadSingle();
            Y = r.ReadSingle();
            State = r.ReadByte();
        }
    }

    // ===================================================================== life

    /// <summary>What happens to a player who dies while others live on.</summary>
    internal enum RespawnRule : byte
    {
        /// <summary>Spectate, then come back healed when the team descends.</summary>
        NextLayer = 0,

        /// <summary>Spectate for the rest of the run.</summary>
        Permadeath = 1,

        /// <summary>Back beside a living teammate after a short wait.</summary>
        Quick = 2,
    }

    /// <summary>A map marker everyone sees for a few seconds.</summary>
    internal sealed class PingMark : IMessage
    {
        public float X, Y;

        public void Write(PacketWriter w)
        {
            w.WriteSingle(X);
            w.WriteSingle(Y);
        }

        public void Read(ref PacketReader r)
        {
            X = r.ReadSingle();
            Y = r.ReadSingle();
        }
    }

    /// <summary>
    /// Creature owner -> victim: one of my creatures bit you. The victim's machine applies
    /// the creature's own bite to its real limb.
    /// </summary>
    internal sealed class CreatureBite : IMessage
    {
        public uint Creature;
        public byte Victim;
        public byte Limb;

        public void Write(PacketWriter w)
        {
            w.WriteUInt32(Creature);
            w.WriteByte(Victim);
            w.WriteByte(Limb);
        }

        public void Read(ref PacketReader r)
        {
            Creature = r.ReadUInt32();
            Victim = r.ReadByte();
            Limb = r.ReadByte();
        }
    }

    /// <summary>"I'm in a fight": teammates nearby hear the threat music too.</summary>
    internal sealed class ThreatPing : IMessage
    {
        public byte Theme;
        public float Seconds;

        public void Write(PacketWriter w)
        {
            w.WriteByte(Theme);
            w.WriteSingle(Seconds);
        }

        public void Read(ref PacketReader r)
        {
            Theme = r.ReadByte();
            Seconds = r.ReadSingle();
        }
    }

    /// <summary>Host -> all: who is waiting to descend, and how many are needed.</summary>
    internal sealed class ExitStatus : IMessage
    {
        public byte Needed;
        public byte Living;
        public bool Pod;

        /// <summary>For a pod: living players inside it.</summary>
        public byte Inside;

        public List<byte> Voters = new List<byte>();

        /// <summary>Names of players still loading the layer (the descent waits for them).</summary>
        public string Loading = "";

        public void Write(PacketWriter w)
        {
            w.WriteByte(Needed);
            w.WriteByte(Living);
            w.WriteBool(Pod);
            w.WriteByte(Inside);
            w.WriteByte((byte)Voters.Count);
            foreach (var v in Voters) w.WriteByte(v);
            w.WriteString(Loading);
        }

        public void Read(ref PacketReader r)
        {
            Needed = r.ReadByte();
            Living = r.ReadByte();
            Pod = r.ReadBool();
            Inside = r.ReadByte();
            int n = r.ReadByte();
            Voters.Clear();
            for (int i = 0; i < n; i++) Voters.Add(r.ReadByte());
            Loading = r.ReadString();
        }
    }

    // ===================================================================== care (M4)

    [System.Flags]
    internal enum BodySigns : ushort
    {
        None = 0,
        Alive = 1,
        Conscious = 2,
        Arrest = 4,
        Breathing = 8,
        Sleeping = 16,
        Dying = 32,
        Bleeding = 64,
        Fibrillating = 128,
        Hungry = 256,
        Thirsty = 512,
        Cold = 1024,
        Fever = 2048,
        Standing = 4096,
        Climbing = 8192,
        /// <summary>Head in water without scuba gear: the game garbles their speech.</summary>
        Underwater = 16384,
    }

    [System.Flags]
    internal enum LimbSigns : ushort
    {
        None = 0,
        Broken = 1,
        Dislocated = 2,
        Splinted = 4,
        Tourniquet = 8,
        Infected = 16,
        Missing = 32,
        Prosthetic = 64,
        Shrapnel = 128,
        BleedBlocked = 256,
        Bandaged = 512,
    }

    internal struct LimbVitals
    {
        public byte Skin, Muscle, Bleed, Infection, Pain, Shrapnel;
        public LimbSigns Signs;
    }

    /// <summary>A player's own condition, twice a second, for teammates' health panels.</summary>
    internal sealed class Vitals : IMessage
    {
        public byte Blood, Oxygen, Pressure, Heart, Consciousness, Brain, Temperature, Pain, Fibrillation;

        /// <summary>The game's total mood, -100..100.</summary>
        public sbyte Mood;

        public BodySigns Signs;

        /// <summary>Seconds since death (0 while alive), for the revival window.</summary>
        public ushort DeadFor;

        public List<LimbVitals> Limbs = new List<LimbVitals>();

        public void Write(PacketWriter w)
        {
            w.WriteByte(Blood); w.WriteByte(Oxygen); w.WriteByte(Pressure); w.WriteByte(Heart);
            w.WriteByte(Consciousness); w.WriteByte(Brain); w.WriteByte(Temperature); w.WriteByte(Pain);
            w.WriteByte(Fibrillation);
            w.WriteByte((byte)Mood);
            w.WriteUInt16((ushort)Signs);
            w.WriteUInt16(DeadFor);
            w.WriteByte((byte)Limbs.Count);
            foreach (var l in Limbs)
            {
                w.WriteByte(l.Skin); w.WriteByte(l.Muscle); w.WriteByte(l.Bleed); w.WriteByte(l.Infection); w.WriteByte(l.Pain);
                w.WriteByte(l.Shrapnel);
                w.WriteUInt16((ushort)l.Signs);
            }
        }

        public void Read(ref PacketReader r)
        {
            Blood = r.ReadByte(); Oxygen = r.ReadByte(); Pressure = r.ReadByte(); Heart = r.ReadByte();
            Consciousness = r.ReadByte(); Brain = r.ReadByte(); Temperature = r.ReadByte(); Pain = r.ReadByte();
            Fibrillation = r.ReadByte();
            Mood = (sbyte)r.ReadByte();
            Signs = (BodySigns)r.ReadUInt16();
            DeadFor = r.ReadUInt16();
            int n = r.ReadByte();
            Limbs.Clear();
            for (int i = 0; i < n; i++)
                Limbs.Add(new LimbVitals
                {
                    Skin = r.ReadByte(), Muscle = r.ReadByte(), Bleed = r.ReadByte(), Infection = r.ReadByte(), Pain = r.ReadByte(),
                    Shrapnel = r.ReadByte(),
                    Signs = (LimbSigns)r.ReadUInt16(),
                });
        }
    }

    internal enum TreatKind : byte
    {
        /// <summary>Use the treater's held item on the limb.</summary>
        Item = 0,
        SetDislocation = 1,
        RemoveTourniquet = 2,
        RemoveSplint = 3,
        PullShrapnel = 4,
    }

    internal enum TreatOp : byte
    {
        // treater -> patient
        Start = 0,
        Wrap = 1,
        Inject = 2,
        Delta = 3,
        Shock = 4,
        End = 5,
        // patient -> treater
        Progress = 10,
        Refuse = 11,
    }

    /// <summary>
    /// One step of a minigame treatment. The treater plays the game's minigame on their
    /// own screen; its results stream to the patient, whose machine applies them to the
    /// real body; the patient streams back what is left of the item.
    /// </summary>
    internal sealed class TreatStep : IMessage
    {
        public uint Session;

        /// <summary>The patient (treater -> patient) or the treater (patient -> treater).</summary>
        public byte Peer;

        public TreatOp Op;
        public byte Limb;
        public TreatKind Kind;

        // Start: the item. Progress: the item's state now (empty with Consumed when used up).
        public byte[] Item = new byte[0];
        public bool Consumed;

        // Wrap: angle. Inject: amount. Shock: chance. Delta: pain, skin, bleed, muscle, brain.
        public float A, B, C, D, E;

        // Delta: shrapnel count (-1 unchanged), 1 = set the joint back.
        public int Shrapnel = -1;
        public byte Joint;

        public string Text = "";

        public void Write(PacketWriter w)
        {
            w.WriteUInt32(Session); w.WriteByte(Peer); w.WriteByte((byte)Op); w.WriteByte(Limb); w.WriteByte((byte)Kind);
            w.WriteBlob(Item); w.WriteBool(Consumed);
            w.WriteSingle(A); w.WriteSingle(B); w.WriteSingle(C); w.WriteSingle(D); w.WriteSingle(E);
            w.WriteInt32(Shrapnel); w.WriteByte(Joint);
            w.WriteString(Text);
        }

        public void Read(ref PacketReader r)
        {
            Session = r.ReadUInt32(); Peer = r.ReadByte(); Op = (TreatOp)r.ReadByte(); Limb = r.ReadByte(); Kind = (TreatKind)r.ReadByte();
            Item = r.ReadBlob(); Consumed = r.ReadBool();
            A = r.ReadSingle(); B = r.ReadSingle(); C = r.ReadSingle(); D = r.ReadSingle(); E = r.ReadSingle();
            Shrapnel = r.ReadInt32(); Joint = r.ReadByte();
            Text = r.ReadString();
        }
    }

    /// <summary>Treater -> patient: do this to your limb. The patient's machine applies it.</summary>
    internal sealed class TreatRequest : IMessage
    {
        public uint Request;
        public byte Patient;
        public byte Limb;
        public TreatKind Kind;
        public byte[] Item = new byte[0];

        public void Write(PacketWriter w)
        {
            w.WriteUInt32(Request); w.WriteByte(Patient); w.WriteByte(Limb); w.WriteByte((byte)Kind);
            w.WriteBlob(Item);
        }

        public void Read(ref PacketReader r)
        {
            Request = r.ReadUInt32(); Patient = r.ReadByte(); Limb = r.ReadByte(); Kind = (TreatKind)r.ReadByte();
            Item = r.ReadBlob();
        }
    }

    /// <summary>Patient -> treater: how it went, and what is left of the item.</summary>
    internal sealed class TreatResult : IMessage
    {
        public uint Request;
        public byte Treater;
        public bool Ok;
        public bool Consumed;
        public string Message = "";
        public byte[] Item = new byte[0];

        public void Write(PacketWriter w)
        {
            w.WriteUInt32(Request); w.WriteByte(Treater); w.WriteBool(Ok); w.WriteBool(Consumed); w.WriteString(Message);
            w.WriteBlob(Item);
        }

        public void Read(ref PacketReader r)
        {
            Request = r.ReadUInt32(); Treater = r.ReadByte(); Ok = r.ReadBool(); Consumed = r.ReadBool(); Message = r.ReadString();
            Item = r.ReadBlob();
        }
    }

    /// <summary>Giver -> receiver: would you like this? The item stays with the giver until accepted.</summary>
    internal sealed class GiveOffer : IMessage
    {
        public uint Token;
        public byte To;
        public string ItemId = "";

        public void Write(PacketWriter w) { w.WriteUInt32(Token); w.WriteByte(To); w.WriteString(ItemId); }
        public void Read(ref PacketReader r) { Token = r.ReadUInt32(); To = r.ReadByte(); ItemId = r.ReadString(); }
    }

    internal sealed class GiveAnswer : IMessage
    {
        public uint Token;
        public byte Giver;
        public bool Accept;
        public string Reason = "";

        public void Write(PacketWriter w) { w.WriteUInt32(Token); w.WriteByte(Giver); w.WriteBool(Accept); w.WriteString(Reason); }
        public void Read(ref PacketReader r) { Token = r.ReadUInt32(); Giver = r.ReadByte(); Accept = r.ReadBool(); Reason = r.ReadString(); }
    }

    /// <summary>
    /// Asking a teammate for something they carry (from the teammate panel). Refused is
    /// their "keep it"; a yes comes back as an ordinary give offer.
    /// </summary>
    internal sealed class GiveAsk : IMessage
    {
        public byte To;
        public byte Slot;
        public string ItemId = "";
        public bool Refused;

        public void Write(PacketWriter w) { w.WriteByte(To); w.WriteByte(Slot); w.WriteString(ItemId); w.WriteBool(Refused); }
        public void Read(ref PacketReader r) { To = r.ReadByte(); Slot = r.ReadByte(); ItemId = r.ReadString(); Refused = r.ReadBool(); }
    }

    /// <summary>Giver -> receiver: the item itself (empty if it was gone by the time you said yes).</summary>
    internal sealed class GiveDone : IMessage
    {
        public uint Token;
        public byte To;
        public byte[] Item = new byte[0];

        public void Write(PacketWriter w) { w.WriteUInt32(Token); w.WriteByte(To); w.WriteBlob(Item); }
        public void Read(ref PacketReader r) { Token = r.ReadUInt32(); To = r.ReadByte(); Item = r.ReadBlob(); }
    }

    /// <summary>Start or stop carrying a downed teammate (sent by the carrier, or by the carried on waking).</summary>
    internal enum CarryMode : byte { Drag = 0, Back = 1, Arms = 2 }

    internal sealed class CarryCommand : IMessage
    {
        public byte Carrier;
        public byte Target;
        public bool Start;
        public CarryMode Mode;

        public void Write(PacketWriter w) { w.WriteByte(Carrier); w.WriteByte(Target); w.WriteBool(Start); w.WriteByte((byte)Mode); }
        public void Read(ref PacketReader r) { Carrier = r.ReadByte(); Target = r.ReadByte(); Start = r.ReadBool(); Mode = (CarryMode)r.ReadByte(); }
    }

    internal enum MoveKind : byte
    {
        /// <summary>Helper -> target: a leg-up is ready; the target's next jump nearby goes higher.</summary>
        LegUp = 0,

        /// <summary>Helper above -> target: a hand to climb up to (X, Y).</summary>
        Pull = 1,

        /// <summary>Helper -> target: shaken out of a deep low; a mood boost that fades.</summary>
        Snap = 2,

        /// <summary>Helper -> holder: lifting their heavy item with them (F = its weight), or letting go.</summary>
        LiftStart = 3,
        LiftStop = 4,

        /// <summary>Catcher -> everyone: ready to catch (F = seconds the readiness lasts).</summary>
        CatchReady = 5,

        /// <summary>Faller -> catcher: caught (F = fall speed). Landed on someone who wasn't ready: LandedOn.</summary>
        Caught = 6,
        LandedOn = 7,
    }

    /// <summary>Helping each other move: leg-ups, pulls, lifting together, catching.</summary>
    internal sealed class TeamMove : IMessage
    {
        public MoveKind Kind;
        public byte Target;
        public float X, Y, F;

        public void Write(PacketWriter w) { w.WriteByte((byte)Kind); w.WriteByte(Target); w.WriteSingle(X); w.WriteSingle(Y); w.WriteSingle(F); }
        public void Read(ref PacketReader r) { Kind = (MoveKind)r.ReadByte(); Target = r.ReadByte(); X = r.ReadSingle(); Y = r.ReadSingle(); F = r.ReadSingle(); }
    }

    /// <summary>Dead player -> all: what can be taken from their body (empty when alive again).</summary>
    internal sealed class LootList : IMessage
    {
        internal struct Entry
        {
            public int Key;
            public string ItemId;
        }

        public List<Entry> Items = new List<Entry>();

        public void Write(PacketWriter w)
        {
            w.WriteUInt16((ushort)Items.Count);
            foreach (var e in Items) { w.WriteInt32(e.Key); w.WriteString(e.ItemId); }
        }

        public void Read(ref PacketReader r)
        {
            int n = r.ReadCount(256);
            Items.Clear();
            for (int i = 0; i < n; i++) Items.Add(new Entry { Key = r.ReadInt32(), ItemId = r.ReadString() });
        }
    }

    /// <summary>Looter -> dead player: hand me this. Delivered with <see cref="GiveDone"/>.</summary>
    internal sealed class LootTake : IMessage
    {
        public byte Owner;
        public int Key;
        public void Write(PacketWriter w) { w.WriteByte(Owner); w.WriteInt32(Key); }
        public void Read(ref PacketReader r) { Owner = r.ReadByte(); Key = r.ReadInt32(); }
    }

    /// <summary>Half a second of chest compressions on a teammate whose heart has stopped.</summary>
    internal sealed class CprPulse : IMessage
    {
        public byte Patient;
        public void Write(PacketWriter w) => w.WriteByte(Patient);
        public void Read(ref PacketReader r) => Patient = r.ReadByte();
    }

    /// <summary>A few tens of milliseconds of Steam-compressed voice.</summary>
    internal sealed class VoiceData : IMessage
    {
        public byte[] Data = new byte[0];
        public void Write(PacketWriter w) => w.WriteBlob(Data);
        public void Read(ref PacketReader r) => Data = r.ReadBlob();
    }

    /// <summary>Host -> all: the session's rules (sent on join and whenever they change).</summary>
    internal sealed class RulesMessage : IMessage
    {
        public byte Respawn;
        public byte ExitShare;
        public bool SpeedUp;
        public byte MaxPlayers;
        public bool Locked;
        public bool Arrows = true;
        public bool NameTags = true;
        public bool SpawnApart;
        public byte VoicePercent = 100;
        public bool FriendlyFire;
        public byte LootScale;
        public byte DangerScale;

        public void Write(PacketWriter w)
        {
            w.WriteByte(Respawn);
            w.WriteByte(ExitShare);
            w.WriteBool(SpeedUp);
            w.WriteByte(MaxPlayers);
            w.WriteBool(Locked);
            w.WriteBool(Arrows);
            w.WriteBool(NameTags);
            w.WriteBool(SpawnApart);
            w.WriteByte(VoicePercent);
            w.WriteBool(FriendlyFire);
            w.WriteByte(LootScale);
            w.WriteByte(DangerScale);
        }

        public void Read(ref PacketReader r)
        {
            Respawn = r.ReadByte();
            ExitShare = r.ReadByte();
            SpeedUp = r.ReadBool();
            MaxPlayers = r.ReadByte();
            Locked = r.ReadBool();
            Arrows = r.ReadBool();
            NameTags = r.ReadBool();
            SpawnApart = r.ReadBool();
            VoicePercent = r.ReadByte();
            FriendlyFire = r.ReadBool();
            LootScale = r.ReadByte();
            DangerScale = r.ReadByte();
        }
    }

    /// <summary>Host -> all: every player on the layer is dead; the run is lost.</summary>
    internal sealed class TeamWipe : IMessage
    {
        public ushort Epoch;
        public void Write(PacketWriter w) => w.WriteUInt16(Epoch);
        public void Read(ref PacketReader r) => Epoch = r.ReadUInt16();
    }

    /// <summary>A gesture (wave, point, hug, bark...).</summary>
    internal sealed class GestureMsg : IMessage
    {
        public byte Kind;
        public float X, Y;
        public byte Target;
        public float Pitch;

        public void Write(PacketWriter w) { w.WriteByte(Kind); w.WriteSingle(X); w.WriteSingle(Y); w.WriteByte(Target); w.WriteSingle(Pitch); }
        public void Read(ref PacketReader r) { Kind = r.ReadByte(); X = r.ReadSingle(); Y = r.ReadSingle(); Target = r.ReadByte(); Pitch = r.ReadSingle(); }
    }

    /// <summary>Host -> a client arriving on a layer with "spawn apart": start here instead.</summary>
    internal sealed class SpawnPoint : IMessage
    {
        public float X, Y;
        public void Write(PacketWriter w) { w.WriteSingle(X); w.WriteSingle(Y); }
        public void Read(ref PacketReader r) { X = r.ReadSingle(); Y = r.ReadSingle(); }
    }

    /// <summary>
    /// Host -> a returning client: where it was when it left this layer, and whether it
    /// was dead, so leaving and rejoining neither moves nor revives anyone.
    /// </summary>
    internal sealed class RejoinPlace : IMessage
    {
        public bool HasPosition;
        public float X, Y;
        public bool Dead;

        public void Write(PacketWriter w)
        {
            w.WriteBool(HasPosition);
            w.WriteSingle(X);
            w.WriteSingle(Y);
            w.WriteBool(Dead);
        }

        public void Read(ref PacketReader r)
        {
            HasPosition = r.ReadBool();
            X = r.ReadSingle();
            Y = r.ReadSingle();
            Dead = r.ReadBool();
        }
    }

    // ===================================================================== clock

    internal sealed class TimeScale : IMessage
    {
        public byte Speed;
        public void Write(PacketWriter w) => w.WriteByte(Speed);
        public void Read(ref PacketReader r) => Speed = r.ReadByte();
    }

    internal sealed class Earthquake : IMessage
    {
        public float Duration;
        public bool Instant;

        public void Write(PacketWriter w)
        {
            w.WriteSingle(Duration);
            w.WriteBool(Instant);
        }

        public void Read(ref PacketReader r)
        {
            Duration = r.ReadSingle();
            Instant = r.ReadBool();
        }
    }

    // ===================================================================== audit fixes

    /// <summary>The parameters of WorldGeneration.CreateExplosion.</summary>
    internal sealed class ExplosionSpec
    {
        public float X, Y, Range, Velocity, StructuralDamage;
        public float MuscleMin, MuscleMax, SkinMin, SkinMax, BleedMin, BleedMax;
        public float SkinChance, BoneChance, DislocationChance, DisfigureChance, BleedChance, ShrapnelChance, DismemberChance;
        public string Sound = "";

        public void Write(PacketWriter w)
        {
            w.WriteSingle(X); w.WriteSingle(Y); w.WriteSingle(Range); w.WriteSingle(Velocity); w.WriteSingle(StructuralDamage);
            w.WriteSingle(MuscleMin); w.WriteSingle(MuscleMax); w.WriteSingle(SkinMin); w.WriteSingle(SkinMax);
            w.WriteSingle(BleedMin); w.WriteSingle(BleedMax);
            w.WriteSingle(SkinChance); w.WriteSingle(BoneChance); w.WriteSingle(DislocationChance); w.WriteSingle(DisfigureChance);
            w.WriteSingle(BleedChance); w.WriteSingle(ShrapnelChance); w.WriteSingle(DismemberChance);
            w.WriteString(Sound);
        }

        public void Read(ref PacketReader r)
        {
            X = r.ReadSingle(); Y = r.ReadSingle(); Range = r.ReadSingle(); Velocity = r.ReadSingle(); StructuralDamage = r.ReadSingle();
            MuscleMin = r.ReadSingle(); MuscleMax = r.ReadSingle(); SkinMin = r.ReadSingle(); SkinMax = r.ReadSingle();
            BleedMin = r.ReadSingle(); BleedMax = r.ReadSingle();
            SkinChance = r.ReadSingle(); BoneChance = r.ReadSingle(); DislocationChance = r.ReadSingle(); DisfigureChance = r.ReadSingle();
            BleedChance = r.ReadSingle(); ShrapnelChance = r.ReadSingle(); DismemberChance = r.ReadSingle();
            Sound = r.ReadString();
            if (Range < 0f || Range > 200f) r.Fail();
        }
    }

    /// <summary>Host -> all: an explosion happened; apply its effects to your own body.</summary>
    internal sealed class ExplosionEvent : IMessage
    {
        public ExplosionSpec Spec = new ExplosionSpec();
        public void Write(PacketWriter w) => Spec.Write(w);
        public void Read(ref PacketReader r) => Spec.Read(ref r);
    }

    /// <summary>Client -> host: something exploded here; please make it real.</summary>
    internal sealed class ExplosionRequest : IMessage
    {
        public ExplosionSpec Spec = new ExplosionSpec();
        public void Write(PacketWriter w) => Spec.Write(w);
        public void Read(ref PacketReader r) => Spec.Read(ref r);
    }

    internal enum EntityActionKind : byte
    {
        TraderDropInventory = 1,
        FanBreak = 2,
    }

    /// <summary>Client -> host: run this one-off action on your copy (the host makes the loot).</summary>
    internal sealed class EntityAction : IMessage
    {
        public uint Id;
        public EntityActionKind Kind;
        public int Arg;

        public void Write(PacketWriter w)
        {
            w.WriteUInt32(Id);
            w.WriteByte((byte)Kind);
            w.WriteInt32(Arg);
        }

        public void Read(ref PacketReader r)
        {
            Id = r.ReadUInt32();
            Kind = (EntityActionKind)r.ReadByte();
            Arg = r.ReadInt32();
        }
    }

    /// <summary>Client -> host: this object vanished here (eaten, crafted, exploded, decayed).</summary>
    internal sealed class EntityGone : IMessage
    {
        public uint Id;
        public void Write(PacketWriter w) => w.WriteUInt32(Id);
        public void Read(ref PacketReader r) => Id = r.ReadUInt32();
    }

    /// <summary>Host -> all, once a second: layer-wide values that would otherwise drift per machine.</summary>
    internal sealed class WorldClock : IMessage
    {
        public float AmbientTemperature;
        public bool RadiationActive;
        public float RadiationTime;

        /// <summary>The host's time on this layer: a shared clock for things that cycle (pistons).</summary>
        public float LayerTime;

        public void Write(PacketWriter w)
        {
            w.WriteSingle(AmbientTemperature);
            w.WriteBool(RadiationActive);
            w.WriteSingle(RadiationTime);
            w.WriteSingle(LayerTime);
        }

        public void Read(ref PacketReader r)
        {
            AmbientTemperature = r.ReadSingle();
            RadiationActive = r.ReadBool();
            RadiationTime = r.ReadSingle();
            LayerTime = r.ReadSingle();
        }
    }

    // ====================================================================== M5: world events

    internal enum WorldEventKind : byte
    {
        /// <summary>Greyed out and walk-through: bio terminals, scrap eaters, their doors, used med stations.</summary>
        Backgroundify = 1,
        BearTrap = 2,
        SpikeStab = 3,
        CoilShock = 4,
        CaveTicks = 5,
        StalactiteDrop = 6,
        PodRepaired = 7,
        ScrapFed = 8,
        GuardSpawned = 9,
        ShuttleOpen = 10,
        BlueHeart = 11,
        GunmineFired = 12,
        MinePressed = 13,
        GeyserRumble = 14,
        /// <summary>The End lab's escape timer ran out on some machine: the end guards come.</summary>
        EndGuards = 15,
    }

    /// <summary>
    /// Something in the world happened on one machine (a trap went off, a door opened).
    /// Clients send it to the host, which keeps the first of each one-shot, applies it to
    /// its copy and passes it on. <see cref="Replay"/> marks a catch-up for a late joiner:
    /// state only, no sounds or cutscenes.
    /// </summary>
    internal sealed class WorldEvent : IMessage
    {
        public WorldEventKind Kind;
        public uint Id;
        /// <summary>Child names below the registered object, '/'-separated ("" for the object itself).</summary>
        public string Path = "";
        public float X, Y;
        public int Arg;
        public float F;
        public bool Replay;

        public void Write(PacketWriter w)
        {
            w.WriteByte((byte)Kind);
            w.WriteUInt32(Id);
            w.WriteString(Path ?? "");
            w.WriteSingle(X);
            w.WriteSingle(Y);
            w.WriteInt32(Arg);
            w.WriteSingle(F);
            w.WriteBool(Replay);
        }

        public void Read(ref PacketReader r)
        {
            Kind = (WorldEventKind)r.ReadByte();
            Id = r.ReadUInt32();
            Path = r.ReadString();
            X = r.ReadSingle();
            Y = r.ReadSingle();
            Arg = r.ReadInt32();
            F = r.ReadSingle();
            Replay = r.ReadBool();
        }
    }

    /// <summary>
    /// A shot was fired here (a player's gun, a guard, a turret, a gun mine). Everyone else
    /// replays it for their own player only: tracer, sound, puffs, creature reactions, and
    /// wounds if it passes through them. Damage to objects and terrain comes from the
    /// shooter's machine through the usual sync.
    /// </summary>
    internal sealed class ShotFired : IMessage
    {
        public float X, Y, DX, DY;
        public bool IgnoreBody, Tinnitus, ForceHead;
        public float Structure, Animal, PlayerMult;

        /// <summary>The networked object that fired (turret, guard), 0 for a player's gun.</summary>
        public uint Source;

        /// <summary>Fired from a player's gun (friendly fire is a host rule).</summary>
        public bool FromPlayer;

        /// <summary>The first pellet of a trigger pull: it carries the sound.</summary>
        public bool First;
        public string Sound = "";

        /// <summary>Where a guard was aiming: it only hits a body under its reticle.</summary>
        public bool HasAim;
        public float AimX, AimY;

        public void Write(PacketWriter w)
        {
            w.WriteSingle(X); w.WriteSingle(Y); w.WriteSingle(DX); w.WriteSingle(DY);
            w.WriteBool(IgnoreBody); w.WriteBool(Tinnitus); w.WriteBool(ForceHead);
            w.WriteSingle(Structure); w.WriteSingle(Animal); w.WriteSingle(PlayerMult);
            w.WriteUInt32(Source);
            w.WriteBool(FromPlayer);
            w.WriteBool(First);
            w.WriteString(Sound ?? "");
            w.WriteBool(HasAim);
            if (HasAim) { w.WriteSingle(AimX); w.WriteSingle(AimY); }
        }

        public void Read(ref PacketReader r)
        {
            X = r.ReadSingle(); Y = r.ReadSingle(); DX = r.ReadSingle(); DY = r.ReadSingle();
            IgnoreBody = r.ReadBool(); Tinnitus = r.ReadBool(); ForceHead = r.ReadBool();
            Structure = r.ReadSingle(); Animal = r.ReadSingle(); PlayerMult = r.ReadSingle();
            Source = r.ReadUInt32();
            FromPlayer = r.ReadBool();
            First = r.ReadBool();
            Sound = r.ReadString();
            HasAim = r.ReadBool();
            if (HasAim) { AimX = r.ReadSingle(); AimY = r.ReadSingle(); }
        }
    }

    internal enum TraderOp : byte
    {
        /// <summary>Client -> host: may I buy this?</summary>
        BuyRequest = 1,
        BuyGranted = 2,
        BuyDenied = 3,
        /// <summary>Gone from the shelf (someone bought it).</summary>
        Removed = 4,
        /// <summary>Put on the shelf (the free bandage for a bleeding player).</summary>
        Added = 5,
        /// <summary>The trader turned on the team.</summary>
        Hostile = 6,
    }

    /// <summary>One change to a trader's shared shelf or temper. Items are named by id, preference and price.</summary>
    internal sealed class TraderTrade : IMessage
    {
        public uint Trader;
        public TraderOp Op;
        public string Item = "";
        public byte Pref;
        public int Value;

        public void Write(PacketWriter w)
        {
            w.WriteUInt32(Trader);
            w.WriteByte((byte)Op);
            w.WriteString(Item ?? "");
            w.WriteByte(Pref);
            w.WriteInt32(Value);
        }

        public void Read(ref PacketReader r)
        {
            Trader = r.ReadUInt32();
            Op = (TraderOp)r.ReadByte();
            Item = r.ReadString();
            Pref = r.ReadByte();
            Value = r.ReadInt32();
        }
    }

    /// <summary>Host -> all, every 30 s: a fingerprint of each 64x64 terrain chunk.</summary>
    internal sealed class WorldFingerprint : IMessage
    {
        public const int Max = 4096;
        public ushort Epoch;
        public ushort Cols, Rows;
        public ulong[] Chunks = new ulong[0];

        public void Write(PacketWriter w)
        {
            w.WriteUInt16(Epoch);
            w.WriteUInt16(Cols);
            w.WriteUInt16(Rows);
            w.WriteUInt16((ushort)Chunks.Length);
            foreach (var c in Chunks) w.WriteUInt64(c);
        }

        public void Read(ref PacketReader r)
        {
            Epoch = r.ReadUInt16();
            Cols = r.ReadUInt16();
            Rows = r.ReadUInt16();
            int n = r.ReadUInt16();
            if (n > Max) { r.Fail(); return; }
            Chunks = new ulong[n];
            for (int i = 0; i < n; i++) Chunks[i] = r.ReadUInt64();
        }
    }

    /// <summary>Client -> host: these chunks differ from yours, twice in a row; please send them.</summary>
    internal sealed class ChunkResync : IMessage
    {
        public ushort Epoch;
        public List<ushort> Chunks = new List<ushort>();

        public void Write(PacketWriter w)
        {
            w.WriteUInt16(Epoch);
            w.WriteByte((byte)Chunks.Count);
            foreach (var c in Chunks) w.WriteUInt16(c);
        }

        public void Read(ref PacketReader r)
        {
            Epoch = r.ReadUInt16();
            int n = r.ReadByte();
            Chunks = new List<ushort>(n);
            for (int i = 0; i < n; i++) Chunks.Add(r.ReadUInt16());
        }
    }
}
