using System;

namespace CCoop.Protocol.Messages
{
    /// <summary>
    /// Client -> host. The compatibility fingerprint.
    ///
    /// Everything here is checked before the client is allowed anywhere near world
    /// state. The game has no world seed, so a client's world comes entirely from the
    /// host — meaning a mismatched build cannot be "mostly fine": it will desync in
    /// ways that look like gameplay bugs. Failing loudly at handshake is the only
    /// honest option.
    /// </summary>
    public sealed class HelloMessage
    {
        /// <summary>Our mod version, e.g. "0.1.0".</summary>
        public string ModVersion = string.Empty;

        /// <summary>Steam App ID of the running game (4576510 for the demo).</summary>
        public uint GameAppId;

        /// <summary>Lowercase hex SHA-256 of Assembly-CSharp.dll — the real build identity.</summary>
        public string AssemblyHash = string.Empty;

        /// <summary>Unity version string, e.g. "2022.3.62f3".</summary>
        public string UnityVersion = string.Empty;

        /// <summary>
        /// Ordered "guid@version" list of every loaded BepInEx plugin. Other gameplay
        /// mods can change item, world and body behaviour, so the host must be able to
        /// require an identical set.
        /// </summary>
        public string PluginManifest = string.Empty;

        /// <summary>Requested display name (host may substitute).</summary>
        public string PlayerName = string.Empty;

        /// <summary>Feature bits for forward-compatible negotiation.</summary>
        public uint FeatureFlags;

        public byte[] Encode(uint sequence)
        {
            var w = new PacketWriter(512);
            int lp = PacketHeader.Write(w, MessageType.Hello, ProtocolConstants.HostPeerId,
                                       sessionId: 0, sequence: sequence, tick: 0);
            w.WriteString(ModVersion);
            w.WriteUInt32(GameAppId);
            w.WriteString(AssemblyHash);
            w.WriteString(UnityVersion);
            w.WriteString(PluginManifest);
            w.WriteString(PlayerName);
            w.WriteUInt32(FeatureFlags);
            PacketHeader.PatchPayloadLength(w, lp);
            return w.ToArray();
        }

        public static bool TryDecode(ref PacketReader r, out HelloMessage msg)
        {
            msg = new HelloMessage
            {
                ModVersion = r.ReadString(),
                GameAppId = r.ReadUInt32(),
                AssemblyHash = r.ReadString(),
                UnityVersion = r.ReadString(),
                PluginManifest = r.ReadString(),
                PlayerName = r.ReadString(),
                FeatureFlags = r.ReadUInt32(),
            };

            // Semantic validation. A structurally valid packet can still be nonsense,
            // and trailing bytes mean the sender disagrees with us about the format.
            if (r.Failed || !r.FullyConsumed) { msg = null; return false; }
            if (msg.AssemblyHash.Length != ProtocolConstants.Sha256HexLength) { msg = null; return false; }
            if (!IsLowerHex(msg.AssemblyHash)) { msg = null; return false; }
            if (msg.ModVersion.Length == 0) { msg = null; return false; }

            return true;
        }

        private static bool IsLowerHex(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!ok) return false;
            }
            return true;
        }
    }

    /// <summary>Host -> client. Accepted into the session.</summary>
    public sealed class WelcomeMessage
    {
        public uint SessionId;
        public byte AssignedPeerId;
        public byte HostPeerCount;
        public uint ServerTick;
        public string HostPlayerName = string.Empty;

        public byte[] Encode(uint sequence)
        {
            var w = new PacketWriter(128);
            int lp = PacketHeader.Write(w, MessageType.Welcome, ProtocolConstants.HostPeerId,
                                       SessionId, sequence, ServerTick);
            w.WriteByte(AssignedPeerId);
            w.WriteByte(HostPeerCount);
            w.WriteUInt32(ServerTick);
            w.WriteString(HostPlayerName);
            PacketHeader.PatchPayloadLength(w, lp);
            return w.ToArray();
        }

        public static bool TryDecode(ref PacketReader r, PacketHeader header, out WelcomeMessage msg)
        {
            msg = new WelcomeMessage
            {
                SessionId = header.SessionId,
                AssignedPeerId = r.ReadByte(),
                HostPeerCount = r.ReadByte(),
                ServerTick = r.ReadUInt32(),
                HostPlayerName = r.ReadString(),
            };

            if (r.Failed || !r.FullyConsumed) { msg = null; return false; }

            // A client must never be told it is the host, nor given an out-of-range id.
            if (msg.AssignedPeerId == ProtocolConstants.HostPeerId) { msg = null; return false; }
            if (msg.AssignedPeerId >= ProtocolConstants.MaxPeers) { msg = null; return false; }
            if (msg.HostPeerCount == 0 || msg.HostPeerCount > ProtocolConstants.MaxPeers) { msg = null; return false; }

            return true;
        }
    }

    /// <summary>Machine-readable rejection reasons, each with a user-facing message.</summary>
    public enum RejectReason : byte
    {
        Unknown = 0,
        ProtocolVersionMismatch = 1,
        ModVersionMismatch = 2,
        GameBuildMismatch = 3,
        UnsupportedGameAppId = 4,
        PluginManifestMismatch = 5,
        SessionFull = 6,
        SessionClosed = 7,
        Banned = 8,
        MalformedHandshake = 9,
        AlreadyInSession = 10,
    }

    /// <summary>Host -> client. Refused, with a reason the player can act on.</summary>
    public sealed class RejectMessage
    {
        public RejectReason Reason;

        /// <summary>Optional specifics, e.g. the host's expected hash. Never secrets.</summary>
        public string Detail = string.Empty;

        public byte[] Encode(uint sequence)
        {
            var w = new PacketWriter(128);
            int lp = PacketHeader.Write(w, MessageType.Reject, ProtocolConstants.HostPeerId,
                                       sessionId: 0, sequence: sequence, tick: 0);
            w.WriteByte((byte)Reason);
            w.WriteString(Detail);
            PacketHeader.PatchPayloadLength(w, lp);
            return w.ToArray();
        }

        public static bool TryDecode(ref PacketReader r, out RejectMessage msg)
        {
            byte reason = r.ReadByte();
            string detail = r.ReadString();

            if (r.Failed || !r.FullyConsumed) { msg = null; return false; }
            if (!IsDefinedReason(reason)) { msg = null; return false; }

            msg = new RejectMessage { Reason = (RejectReason)reason, Detail = detail };
            return true;
        }

        private static bool IsDefinedReason(byte v) =>
            v <= (byte)RejectReason.AlreadyInSession;

        /// <summary>A message suitable for showing directly to the player.</summary>
        public string Describe()
        {
            string baseText;
            switch (Reason)
            {
                case RejectReason.ProtocolVersionMismatch:
                    baseText = "The host is running a different multiplayer protocol version."; break;
                case RejectReason.ModVersionMismatch:
                    baseText = "The host requires a different version of the co-op mod."; break;
                case RejectReason.GameBuildMismatch:
                    baseText = "Your game build differs from the host's. Both players need the same game version."; break;
                case RejectReason.UnsupportedGameAppId:
                    baseText = "The host is running a different edition of the game."; break;
                case RejectReason.PluginManifestMismatch:
                    baseText = "Your installed mods differ from the host's."; break;
                case RejectReason.SessionFull:
                    baseText = "The session is full."; break;
                case RejectReason.SessionClosed:
                    baseText = "The host is not accepting new players."; break;
                case RejectReason.Banned:
                    baseText = "You are not allowed to join this session."; break;
                case RejectReason.MalformedHandshake:
                    baseText = "The host could not understand the connection request."; break;
                case RejectReason.AlreadyInSession:
                    baseText = "You are already connected to this session."; break;
                default:
                    baseText = "The host refused the connection."; break;
            }

            return string.IsNullOrEmpty(Detail) ? baseText : baseText + " (" + Detail + ")";
        }
    }
}
