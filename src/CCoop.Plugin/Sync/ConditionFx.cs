using System.Text;
using CCoop.Plugin.Runtime;
using UnityEngine;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// How a player's condition shapes what they say, hear and see in co-op, using the
    /// same kind of distortion the game's own speech uses (letters swapped for its
    /// full-mouth characters, stutters, trailing off):
    ///
    ///  * Speaking: a broken jaw garbles chat on top of the game's slurring (dislocated jaw,
    ///    full mouth, disfigured face are already slurred by the game itself).
    ///  * Reading: brain damage and deep insanity garble what you read; unconscious, you
    ///    can't make it out at all.
    ///  * Hearing: hearing loss quiets voices; a teammate with a hurt jaw sounds muffled.
    ///  * Seeing: with both eyes gone, pings, name tags and arrows disappear.
    ///  * Screen flashes for a teammate dying nearby, and for being brought back.
    /// </summary>
    internal static class ConditionFx
    {
        private static readonly System.Random Rng = new System.Random();

        private static char[] MouthChars
        {
            get
            {
                try { if (Talker.fullMouthChars != null && Talker.fullMouthChars.Length > 0) return Talker.fullMouthChars; }
                catch { /* not loaded yet */ }
                return "mhnf".ToCharArray();
            }
        }

        // ------------------------------------------------------------------ speaking

        /// <summary>What a broken jaw does to a line before the character says it.</summary>
        internal static string SpeakThroughJaw(Body body, string text)
        {
            if (body?.limbs == null || body.limbs.Length == 0 || body.limbs[0] == null || !body.limbs[0].broken) return text;
            if (text.StartsWith("*") || text.StartsWith("(")) return text;  // gestures stay readable
            var chars = MouthChars;
            var sb = new StringBuilder(text);
            for (int i = 0; i < sb.Length; i++)
                if (char.IsLetter(sb[i]) && Rng.NextDouble() < 0.5)
                    sb[i] = char.IsUpper(sb[i]) ? char.ToUpperInvariant(chars[Rng.Next(chars.Length)]) : chars[Rng.Next(chars.Length)];
            return sb.ToString();
        }

        // ------------------------------------------------------------------ reading

        /// <summary>How badly the local player's mind garbles what they read, 0..1.</summary>
        internal static float ReadingImpairment(Body body)
        {
            if (body == null || !body.alive) return 0f;
            float brain = Mathf.Clamp01((70f - body.brainHealth) / 50f);
            float mind = Mathf.Clamp01((-body.totalHappiness - 80f) / 20f);
            return Mathf.Max(brain, mind);
        }

        /// <summary>A received line as the local player manages to read it.</summary>
        internal static string AsRead(string text, string speaker)
        {
            var body = GameInfo.LocalBody;
            if (Coop.WorldLive && body != null && body.alive && !body.conscious) return "(you couldn't make out what " + speaker + " said)";
            float impaired = Coop.WorldLive ? ReadingImpairment(body) : 0f;
            if (impaired <= 0.01f) return text;
            var sb = new StringBuilder(text.Length + 8);
            foreach (char c in text)
            {
                double roll = Rng.NextDouble();
                if (!char.IsLetter(c)) { sb.Append(c == ' ' && roll < impaired * 0.3 ? "... " : c.ToString()); continue; }
                if (roll < impaired * 0.25) continue;                                // dropped
                if (roll < impaired * 0.45) { sb.Append(c).Append(c); continue; }   // doubled, like the game's stutter
                if (roll < impaired * 0.6) { sb.Append((char)('a' + Rng.Next(26))); continue; }  // misread
                sb.Append(c);
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ hearing and seeing

        /// <summary>Voice volume after the listener's hearing loss.</summary>
        internal static float HearingFactor()
        {
            var body = GameInfo.LocalBody;
            if (body == null || !body.alive) return 1f;
            return Mathf.Clamp01(1f - body.hearingLoss * 0.01f);
        }

        /// <summary>The local player can't see markers (both eyes gone).</summary>
        internal static bool Blind
        {
            get
            {
                var body = GameInfo.LocalBody;
                try { return body != null && body.alive && body.bothEyesGone; }
                catch { return false; }
            }
        }

        // ------------------------------------------------------------------ screen flashes

        private static Color _flash;
        private static float _flashAt = -10f, _flashLength = 1f;

        internal static bool Reduced => CoopPlugin.Ready && CoopPlugin.Instance.ReduceFlashes.Value;

        internal static void Flash(Color color, float seconds)
        {
            if (Reduced) { color.a *= 0.2f; seconds *= 1.5f; }  // a gentle tint instead of a flash
            _flash = color;
            _flashAt = Time.unscaledTime;
            _flashLength = Mathf.Max(0.1f, seconds);
        }

        internal static void DrawFlash()
        {
            float t = (Time.unscaledTime - _flashAt) / _flashLength;
            if (t >= 1f || t < 0f) return;
            var old = GUI.color;
            GUI.color = new Color(_flash.r, _flash.g, _flash.b, _flash.a * (1f - t) * (1f - t));
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = old;
        }

        internal static void Shake(float amount)
        {
            if (Reduced) return;
            try { PlayerCamera.main?.shaker?.Shake(amount); }
            catch { /* no camera */ }
        }
    }
}
