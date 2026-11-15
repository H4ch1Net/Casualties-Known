using System.Text;
using CCoop.Plugin.Runtime;
using CCoop.Plugin.World;
using UnityEngine;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// How well you hear what a teammate says out loud, as Barotrauma does it: clear up
    /// close, then more and more of the letters lost ("H-lp -e -ver h-re") the further away
    /// they are, and nothing at all past the range. Rock between you counts double. The
    /// range is the voice range (the host's "Voices carry" rule). Only speech in the same
    /// layer is affected; the dead, the lobby and the menu hear everything.
    /// </summary>
    internal static class Hearing
    {
        /// <summary>Under this share of the range every word comes through.</summary>
        private const float Clear = 0.3f;
        private const float WallFactor = 2f;

        internal static float Range => Mathf.Max(10f, CoopPlugin.Instance.VoiceRange.Value * HostRules.Current.VoicePercent / 100f);

        /// <summary>0 = heard clearly, 1 = not heard at all.</summary>
        internal static float Loss(byte speaker)
        {
            if (!Coop.WorldLive || !CoopPlugin.Ready) return 0f;
            var body = GameInfo.LocalBody;
            if (body == null) return 0f;
            var rec = Coop.Player(speaker);
            if (!rec.HasPosition || rec.ReportedEpoch != Coop.Epoch) return 0f;
            Vector2 ear = LifeSync.Spectating ? (Coop.Get<LifeSync>()?.TargetPosition() ?? (Vector2)body.transform.position) : (Vector2)body.transform.position;
            float d = Vector2.Distance(ear, rec.Position);
            if (Blocked(ear, rec.Position)) d *= WallFactor;
            return Mathf.Clamp01(d / Range);
        }

        /// <summary>What you hear of <paramref name="text"/>, or null if nothing.</summary>
        internal static string Heard(byte speaker, string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            float loss = Loss(speaker);
            if (loss < Clear) return text;
            if (loss >= 1f) return null;
            // The same letters go for the bubble and the log line.
            var rnd = new System.Random(text.GetHashCode() ^ (speaker * 7919));
            var sb = new StringBuilder(text.Length);
            bool inTag = false;
            foreach (char ch in text)
            {
                if (ch == '<') inTag = true;
                if (inTag || char.IsWhiteSpace(ch) || char.IsPunctuation(ch)) sb.Append(ch);
                else sb.Append(rnd.NextDouble() < loss ? '-' : ch);
                if (ch == '>') inTag = false;
            }
            return sb.ToString();
        }

        /// <summary>Solid ground anywhere on the straight line between the two.</summary>
        private static bool Blocked(Vector2 a, Vector2 b)
        {
            var w = WorldGeneration.world;
            var grid = WorldAccess.Blocks;
            if (w == null || grid == null) return false;
            int width = grid.GetLength(0), height = grid.GetLength(1);
            float len = Vector2.Distance(a, b);
            int steps = Mathf.Clamp(Mathf.CeilToInt(len / 0.5f), 1, 400);
            for (int i = 1; i < steps; i++)
            {
                var p = w.WorldToBlockPos(Vector2.Lerp(a, b, i / (float)steps));
                if (p.x < 0 || p.y < 0 || p.x >= width || p.y >= height) continue;
                if (grid[p.x, p.y] != 0) return true;
            }
            return false;
        }
    }
}
