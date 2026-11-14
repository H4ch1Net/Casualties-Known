using CCoop.Protocol;
using CCoop.Protocol.Messages;
using Xunit;

namespace CCoop.Transport.Tests
{
    public class HandshakeValidatorTests
    {
        private const string HostHash =
            "fd0ad2851f6bae4ab2589634643b92e16696c0e36f090bdd3f4c41e8f1c97095";
        private const string OtherHash =
            "0000000000000000000000000000000000000000000000000000000000000000";

        private static HostRequirements Req() => new HostRequirements
        {
            ModVersion = "0.1.0",
            GameAppId = 4576510,
            AssemblyHash = HostHash,
            PluginManifest = "ccoop.plugin@0.1.0",
            RequireIdenticalPlugins = true,
            MaxPlayers = 4,
            AcceptingNewPlayers = true,
        };

        private static HelloMessage Hello() => new HelloMessage
        {
            ModVersion = "0.1.0",
            GameAppId = 4576510,
            AssemblyHash = HostHash,
            UnityVersion = "2022.3.62f3",
            PluginManifest = "ccoop.plugin@0.1.0",
            PlayerName = "guest",
        };

        [Fact]
        public void Matching_client_is_accepted()
        {
            var r = HandshakeValidator.Validate(Hello(), Req(), currentPlayerCount: 1);
            Assert.True(r.Accepted);
        }

        [Fact]
        public void Different_game_build_is_rejected()
        {
            var hello = Hello();
            hello.AssemblyHash = OtherHash;

            var r = HandshakeValidator.Validate(hello, Req(), 1);

            Assert.False(r.Accepted);
            Assert.Equal(RejectReason.GameBuildMismatch, r.Reason);
            // Detail must help without dumping the whole hash at the player.
            Assert.Contains("fd0ad2851f6b", r.Detail);
        }

        [Fact]
        public void Different_mod_version_is_rejected()
        {
            var hello = Hello();
            hello.ModVersion = "0.2.0";

            var r = HandshakeValidator.Validate(hello, Req(), 1);

            Assert.False(r.Accepted);
            Assert.Equal(RejectReason.ModVersionMismatch, r.Reason);
        }

        [Fact]
        public void Different_app_id_is_rejected()
        {
            var hello = Hello();
            hello.GameAppId = 4576490; // the paid game rather than the demo

            var r = HandshakeValidator.Validate(hello, Req(), 1);

            Assert.False(r.Accepted);
            Assert.Equal(RejectReason.UnsupportedGameAppId, r.Reason);
        }

        [Fact]
        public void Different_plugin_manifest_is_rejected_when_strict()
        {
            var hello = Hello();
            hello.PluginManifest = "ccoop.plugin@0.1.0,someothermod@1.0.0";

            var r = HandshakeValidator.Validate(hello, Req(), 1);

            Assert.False(r.Accepted);
            Assert.Equal(RejectReason.PluginManifestMismatch, r.Reason);
        }

        [Fact]
        public void Different_plugin_manifest_is_allowed_when_lenient()
        {
            var hello = Hello();
            hello.PluginManifest = "ccoop.plugin@0.1.0,cosmeticmod@1.0.0";

            var req = Req();
            req.RequireIdenticalPlugins = false;

            Assert.True(HandshakeValidator.Validate(hello, req, 1).Accepted);
        }

        [Fact]
        public void Full_session_is_rejected()
        {
            var r = HandshakeValidator.Validate(Hello(), Req(), currentPlayerCount: 4);

            Assert.False(r.Accepted);
            Assert.Equal(RejectReason.SessionFull, r.Reason);
            Assert.Contains("4/4", r.Detail);
        }

        [Fact]
        public void Closed_session_is_rejected()
        {
            var req = Req();
            req.AcceptingNewPlayers = false;

            var r = HandshakeValidator.Validate(Hello(), req, 1);

            Assert.False(r.Accepted);
            Assert.Equal(RejectReason.SessionClosed, r.Reason);
        }

        [Fact]
        public void Null_hello_is_rejected_as_malformed()
        {
            var r = HandshakeValidator.Validate(null, Req(), 1);

            Assert.False(r.Accepted);
            Assert.Equal(RejectReason.MalformedHandshake, r.Reason);
        }

        [Fact]
        public void Capacity_is_reported_before_version_problems()
        {
            // A full session is definitive; reporting a version mismatch the player
            // cannot act on would be worse guidance.
            var hello = Hello();
            hello.ModVersion = "9.9.9";
            hello.AssemblyHash = OtherHash;

            var r = HandshakeValidator.Validate(hello, Req(), currentPlayerCount: 4);

            Assert.Equal(RejectReason.SessionFull, r.Reason);
        }

        [Fact]
        public void Build_mismatch_is_reported_before_mod_version()
        {
            // The game build is the more fundamental problem and the harder one to
            // diagnose from symptoms, so it should surface first.
            var hello = Hello();
            hello.AssemblyHash = OtherHash;
            hello.ModVersion = "9.9.9";

            var r = HandshakeValidator.Validate(hello, Req(), 1);

            Assert.Equal(RejectReason.GameBuildMismatch, r.Reason);
        }

        [Fact]
        public void Every_rejection_produces_actionable_player_text()
        {
            var cases = new[]
            {
                RejectReason.GameBuildMismatch, RejectReason.ModVersionMismatch,
                RejectReason.SessionFull, RejectReason.SessionClosed,
                RejectReason.PluginManifestMismatch, RejectReason.UnsupportedGameAppId,
            };

            foreach (var reason in cases)
            {
                var text = new RejectMessage { Reason = reason, Detail = "x" }.Describe();
                Assert.False(string.IsNullOrWhiteSpace(text));
                Assert.True(text.Length > 15, "too terse to be useful: " + text);
            }
        }
    }
}
