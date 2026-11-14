using System;
using CCoop.Protocol.Messages;

namespace CCoop.Protocol
{
    /// <summary>
    /// What a host demands of a joining client. Pure data plus pure logic, so the
    /// admission rules are unit-testable without a socket or a running game.
    /// </summary>
    public sealed class HostRequirements
    {
        /// <summary>Host's own mod version. Clients must match exactly.</summary>
        public string ModVersion = string.Empty;

        /// <summary>Expected Steam App ID (4576510 for the demo).</summary>
        public uint GameAppId;

        /// <summary>Host's Assembly-CSharp.dll SHA-256, lowercase hex.</summary>
        public string AssemblyHash = string.Empty;

        /// <summary>Host's plugin manifest, compared when <see cref="RequireIdenticalPlugins"/>.</summary>
        public string PluginManifest = string.Empty;

        /// <summary>
        /// Whether the client's loaded plugin set must match the host's exactly.
        /// Defaults to true: other gameplay mods can change items, world generation and
        /// body simulation, and because clients receive world state rather than
        /// generating it, a mismatch shows up as unexplained desync rather than an
        /// obvious error. Relax only for cosmetic-only mod sets.
        /// </summary>
        public bool RequireIdenticalPlugins = true;

        /// <summary>Maximum simultaneous players including the host.</summary>
        public int MaxPlayers = 4;

        /// <summary>False once a run is underway, until late join is supported.</summary>
        public bool AcceptingNewPlayers = true;
    }

    /// <summary>Outcome of validating a <see cref="HelloMessage"/>.</summary>
    public struct AdmissionResult
    {
        public bool Accepted;
        public RejectReason Reason;

        /// <summary>Detail for the rejected client. Diagnostic only, never secret.</summary>
        public string Detail;

        public static AdmissionResult Accept() => new AdmissionResult { Accepted = true };

        public static AdmissionResult Deny(RejectReason reason, string detail = "") =>
            new AdmissionResult { Accepted = false, Reason = reason, Detail = detail ?? string.Empty };
    }

    /// <summary>
    /// Decides whether a client may join.
    ///
    /// Checks run cheapest and most-fundamental first, and the *first* failure is
    /// reported: telling a player "your game build differs" is actionable, whereas a
    /// list of six consequential errors is not.
    /// </summary>
    public static class HandshakeValidator
    {
        public static AdmissionResult Validate(HelloMessage hello, HostRequirements req,
                                               int currentPlayerCount)
        {
            if (hello == null)
                return AdmissionResult.Deny(RejectReason.MalformedHandshake, "no handshake");
            if (req == null)
                throw new ArgumentNullException(nameof(req));

            if (!req.AcceptingNewPlayers)
                return AdmissionResult.Deny(RejectReason.SessionClosed,
                    "run already in progress");

            // Capacity before compatibility: a full session is a definitive "no", and
            // there is no point reporting a version mismatch nobody can act on.
            if (currentPlayerCount >= req.MaxPlayers)
                return AdmissionResult.Deny(RejectReason.SessionFull,
                    currentPlayerCount + "/" + req.MaxPlayers + " players");

            if (hello.GameAppId != req.GameAppId)
                return AdmissionResult.Deny(RejectReason.UnsupportedGameAppId,
                    "host expects app " + req.GameAppId);

            // The build fingerprint is the check that matters most: clients receive the
            // host's world wholesale, so a differing build desyncs invisibly.
            if (!HashEquals(hello.AssemblyHash, req.AssemblyHash))
                return AdmissionResult.Deny(RejectReason.GameBuildMismatch,
                    "host build " + Abbreviate(req.AssemblyHash));

            if (!string.Equals(hello.ModVersion, req.ModVersion, StringComparison.Ordinal))
                return AdmissionResult.Deny(RejectReason.ModVersionMismatch,
                    "host runs " + req.ModVersion);

            if (req.RequireIdenticalPlugins &&
                !string.Equals(hello.PluginManifest, req.PluginManifest, StringComparison.Ordinal))
                return AdmissionResult.Deny(RejectReason.PluginManifestMismatch,
                    "host mod set differs");

            return AdmissionResult.Accept();
        }

        /// <summary>
        /// Length-independent, case-sensitive comparison. Hashes are required lowercase
        /// by the decoder, so a case difference here means a malformed peer, not a
        /// formatting quirk to paper over.
        /// </summary>
        private static bool HashEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            return string.Equals(a, b, StringComparison.Ordinal);
        }

        private static string Abbreviate(string hash)
        {
            if (string.IsNullOrEmpty(hash)) return "<unknown>";
            return hash.Length <= 12 ? hash : hash.Substring(0, 12) + "...";
        }
    }
}
