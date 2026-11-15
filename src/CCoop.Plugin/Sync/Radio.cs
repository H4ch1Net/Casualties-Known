using CCoop.Plugin.Runtime;
using UnityEngine;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// Radios, as Barotrauma's crew headsets: what you say into the radio reaches everyone
    /// else on the layer, clearly, wherever they are, while people standing by still hear
    /// your voice (and lose it with distance). With the host's Radios rule on, everyone has
    /// one (the game has no radio item outside its tutorial). Talking and hearing need you
    /// awake and alive. In the chat box, Tab switches between saying and radioing.
    /// </summary>
    internal static class Radio
    {
        internal static bool On => Coop.InSession && HostRules.Current.Radios;

        /// <summary>The local player can talk into the radio, and hear it, right now.</summary>
        internal static bool Usable
        {
            get
            {
                var body = GameInfo.LocalBody;
                return On && Coop.WorldLive && body != null && body.alive && body.conscious;
            }
        }

        /// <summary>The receiving radio's blip.</summary>
        internal static void Blip()
        {
            try { Sound.Play("beep", Vector2.zero, twoDimensional: true, pitchShift: false, follow: null, volume: 0.35f, pitch: 1.4f); }
            catch { /* no sound */ }
        }
    }
}
