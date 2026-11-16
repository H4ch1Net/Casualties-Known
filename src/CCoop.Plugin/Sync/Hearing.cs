using System.Text;
using CCoop.Plugin.Runtime;
using CCoop.Plugin.World;
using UnityEngine;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// How a teammate's words reach you, by voice or by radio. The two trade off:
    ///
    ///  * Voice is clean up close and fades with distance. A wall muffles it (it costs a few
    ///    metres plus some of its thickness; sound gets round corners), so it isn't cut off
    ///    just because you can't see each other. Past the range nothing gets through. It
    ///    works even when you're down, as a mumble heard only close by.
    ///  * Radio reaches the whole layer, but it always crackles: the static grows with the
    ///    distance and with how much rock is in the way, never quite to silence. It needs
    ///    you awake.
    ///
    /// Either way your own ears count: hearing loss (gunfire, blasts) garbles what you hear.
    /// The lost letters are worked out from the line itself, so the same line comes out the
    /// same in the chat log and in the speech text over the speaker's head.
    /// </summary>
    internal static class Hearing
    {
        /// <summary>Share of the voice range where every word comes through.</summary>
        private const float Clear = 0.4f;
        /// <summary>What a wall costs a voice on top of its thickness, in metres.</summary>
        private const float WallMuffle = 4f;
        /// <summary>The most rock can cost a voice, as a share of the range.</summary>
        private const float WallCap = 0.45f;
        /// <summary>Radio static from distance is at its worst this many voice ranges away.</summary>
        private const float RadioFar = 4f;

        private const string Static = "#~%*";

        internal static float Range => Mathf.Max(10f, CoopPlugin.Instance.VoiceRange.Value * HostRules.Current.VoicePercent / 100f);

        /// <summary>
        /// What you hear of a line said out loud, or null if nothing reaches you.
        /// <paramref name="loudness"/> scales the range (a mumble carries half as far).
        /// </summary>
        internal static string Heard(byte speaker, string text, float loudness = 1f)
        {
            if (string.IsNullOrEmpty(text) || !Where(speaker, out var ear, out var mouth)) return text;
            float range = Range * loudness;
            float d = Vector2.Distance(ear, mouth);
            float rock = RockBetween(ear, mouth);
            if (rock > 0f) d += WallMuffle + Mathf.Min(rock, WallCap * range);
            if (d >= range) return null;
            float clear = Clear * range;
            float loss = d <= clear ? 0f : Mathf.Pow((d - clear) / (range - clear), 1.3f);
            return Garble(speaker, text, Mathf.Clamp01(loss + Deafness() * 0.7f), false);
        }

        /// <summary>What comes through your radio of a line radioed by <paramref name="speaker"/>.</summary>
        internal static string OverRadio(byte speaker, string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            float noise = 0.04f;  // a radio is never perfectly clean
            if (Where(speaker, out var ear, out var mouth))
            {
                noise += Mathf.Clamp01(Vector2.Distance(ear, mouth) / (Range * RadioFar)) * 0.3f;
                noise += Mathf.Min(RockBetween(ear, mouth) * 0.006f, 0.3f);
            }
            return Garble(speaker, text, Mathf.Clamp(noise + Deafness() * 0.5f, 0f, 0.75f), true);
        }

        /// <summary>
        /// A line from someone out cold or asleep: what comes out is a mumble. Some words keep
        /// their first letter, so a listener can sometimes guess.
        /// </summary>
        internal static string Mumble(string text, bool asleep)
        {
            var rnd = new System.Random(text.GetHashCode());
            const string sounds = "mhn";
            var sb = new StringBuilder(asleep ? "*talks in their sleep* " : "*mumbles* ");
            foreach (var word in text.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries))
            {
                int letters = 0;
                foreach (char ch in word) if (char.IsLetter(ch)) letters++;
                if (letters == 0) continue;
                if (rnd.NextDouble() < 0.4) sb.Append(char.ToLowerInvariant(FirstLetter(word)));
                int n = Mathf.Clamp(letters / 2, 1, 3);
                for (int i = 0; i < n; i++) sb.Append(sounds[rnd.Next(sounds.Length)]);
                sb.Append(rnd.NextDouble() < 0.3 ? "... " : " ");
            }
            return sb.ToString().TrimEnd() + "...";
        }

        private static char FirstLetter(string word)
        {
            foreach (char ch in word) if (char.IsLetter(ch)) return ch;
            return 'm';
        }

        /// <summary>Where you hear from (or who you're watching, dead) and where the speaker is, in this layer.</summary>
        private static bool Where(byte speaker, out Vector2 ear, out Vector2 mouth)
        {
            ear = mouth = default;
            if (!Coop.WorldLive || !CoopPlugin.Ready) return false;
            var body = GameInfo.LocalBody;
            if (body == null) return false;
            var rec = Coop.Player(speaker);
            if (!rec.HasPosition || rec.ReportedEpoch != Coop.Epoch) return false;
            ear = LifeSync.Spectating ? (Coop.Get<LifeSync>()?.TargetPosition() ?? (Vector2)body.transform.position) : (Vector2)body.transform.position;
            mouth = rec.Position;
            return true;
        }

        /// <summary>Your hearing loss, 0..1.</summary>
        private static float Deafness()
        {
            var body = GameInfo.LocalBody;
            return body != null && body.alive ? Mathf.Clamp01(body.hearingLoss / 100f) : 0f;
        }

        /// <summary>
        /// Letters lost (to "-" for a voice, to static for a radio) with chance
        /// <paramref name="loss"/>; spaces, punctuation and rich-text tags stay. A radio also
        /// crackles between words.
        /// </summary>
        private static string Garble(byte speaker, string text, float loss, bool radio)
        {
            if (loss < 0.01f) return text;
            var rnd = new System.Random(text.GetHashCode() ^ (speaker * 7919) ^ (radio ? 0x5A5A : 0));
            var sb = new StringBuilder(text.Length + 8);
            bool inTag = false;
            foreach (char ch in text)
            {
                if (ch == '<') inTag = true;
                if (inTag || char.IsPunctuation(ch)) sb.Append(ch);
                else if (char.IsWhiteSpace(ch)) sb.Append(radio && rnd.NextDouble() < loss * 0.35 ? " -kssh- " : ch.ToString());
                else if (rnd.NextDouble() < loss) sb.Append(radio ? Static[rnd.Next(Static.Length)] : '-');
                else sb.Append(ch);
                if (ch == '>') inTag = false;
            }
            return sb.ToString();
        }

        /// <summary>Metres of solid ground on the straight line between the two points.</summary>
        private static float RockBetween(Vector2 a, Vector2 b)
        {
            var w = WorldGeneration.world;
            var grid = WorldAccess.Blocks;
            if (w == null || grid == null) return 0f;
            int width = grid.GetLength(0), height = grid.GetLength(1);
            float len = Vector2.Distance(a, b);
            int steps = Mathf.Clamp(Mathf.CeilToInt(len / 0.5f), 1, 600);
            float step = len / steps;
            int solid = 0;
            for (int i = 1; i < steps; i++)
            {
                var p = w.WorldToBlockPos(Vector2.Lerp(a, b, i / (float)steps));
                if (p.x < 0 || p.y < 0 || p.x >= width || p.y >= height) continue;
                if (grid[p.x, p.y] != 0) solid++;
            }
            return solid * step;
        }
    }
}
