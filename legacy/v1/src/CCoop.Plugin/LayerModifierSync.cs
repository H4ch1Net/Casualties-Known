using System;
using CCoop.Protocol.Messages;

namespace CCoop.Plugin
{
    /// <summary>
    /// Gives a client the layer's modifier, which it would otherwise never get.
    ///
    /// The game rolls one per layer in <c>ApplyLayerModifiers</c>, at the very end of
    /// <c>GenerateWorld</c> — the coroutine clients suppress so they can take the host's
    /// world instead. The result is a client standing in the host's terrain, at the
    /// host's coordinates, in a different climate: no fog on a Foggy layer, no cold on a
    /// Freezing one, and none of the hazards the layer is named after.
    ///
    /// Only the *choice* travels. It cannot be re-rolled, because the roll consumes the
    /// unseeded global RNG and would land somewhere else.
    /// </summary>
    internal static class LayerModifierSync
    {
        /// <summary>
        /// The two modifiers whose effect is scattered world objects rather than state.
        ///
        /// Their objects are created inside <c>ApplyLayerModifiers</c>, which runs before
        /// the host captures the world — so the shadecrawlers and rad barrels are already
        /// in the entity manifest and arrive with everything else. Running
        /// <c>Initialize</c> again on the client would scatter a second, different set on
        /// top of them.
        /// </summary>
        private const int Infested = 8;
        private const int Ionized = 9;

        private static readonly string[] Names =
        {
            "Foggy", "Warm", "Hot", "Cold", "Freezing", "Flooded",
            "Dry", "Rainy", "Infested", "Ionized", "Unchipped",
        };

        internal static string Describe(byte index)
        {
            if (index == LayerPrepareMessage.NoLayerModifier) return "none";
            return index < Names.Length ? Names[index] : "modifier" + index;
        }

        /// <summary>Which modifier this peer currently has active, for diagnostics.</summary>
        internal static byte Current
        {
            get
            {
                try
                {
                    var all = LayerModifier.availableModifiers;
                    if (all == null) return LayerPrepareMessage.NoLayerModifier;
                    foreach (var m in all)
                        if (m != null && m.active) return (byte)m.modifierIndex;
                }
                catch { /* diagnostics must never throw */ }
                return LayerPrepareMessage.NoLayerModifier;
            }
        }

        /// <summary>
        /// Forces a modifier on, for the self-test.
        ///
        /// The game only rolls one with probability <c>layermodifierchance</c>, so most
        /// runs have none and a test that waited for one would usually prove nothing.
        /// Freezing is the choice because its whole effect is a single number —
        /// <c>temperatureOffset</c> — which either crossed to the other peer or did not.
        /// </summary>
        internal const byte TestModifier = 4;   // Freezing

        internal static void ForceForTest(Action<string> log)
        {
            var world = WorldGeneration.world;
            if (world == null || Current != LayerPrepareMessage.NoLayerModifier) return;

            foreach (var m in LayerModifier.availableModifiers)
            {
                if (m == null || m.modifierIndex != TestModifier) continue;
                m.active = true;
                m.Initialize(world);
                log?.Invoke("BLOCKTEST forced layer modifier " + Describe(TestModifier) +
                            ", temperatureOffset now " + world.temperatureOffset.ToString("F0"));
                return;
            }
        }

        /// <summary>
        /// Applies the host's modifier to this client's world.
        ///
        /// Always resets first. <c>LayerModifier.availableModifiers</c> is static and its
        /// <c>active</c> flags outlive the world, and the game's own reset lives in
        /// <c>WorldPreprocess</c> — inside the coroutine clients suppress — so without
        /// this the previous layer's modifier would still be in force underneath.
        /// </summary>
        internal static void Apply(byte index, Action<string> log)
        {
            var world = WorldGeneration.world;
            if (world == null) return;

            try { world.ResetLayerModifiers(); }
            catch (Exception e) { log?.Invoke("[layer] could not clear the previous modifier: " + e.Message); }

            if (index == LayerPrepareMessage.NoLayerModifier)
            {
                log?.Invoke("[layer] no modifier on this layer");
                return;
            }

            LayerModifier modifier = null;
            foreach (var m in LayerModifier.availableModifiers)
            {
                if (m != null && m.modifierIndex == index) { modifier = m; break; }
            }

            if (modifier == null)
            {
                log?.Invoke("[layer] host sent unknown modifier " + index);
                return;
            }

            // Marked active either way, so the next layer's reset disables it properly.
            modifier.active = true;

            if (index == Infested || index == Ionized)
            {
                log?.Invoke("[layer] modifier " + Describe(index) +
                            " active; its objects arrived with the world");
                return;
            }

            try
            {
                modifier.Initialize(world);
                log?.Invoke("[layer] modifier " + Describe(index) + " applied");
            }
            catch (Exception e)
            {
                // Better a layer without its modifier than a client that cannot enter it.
                log?.Invoke("[layer] modifier " + Describe(index) + " failed: " + e.Message);
            }
        }
    }
}
