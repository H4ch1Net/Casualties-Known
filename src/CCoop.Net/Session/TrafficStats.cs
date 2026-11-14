using System.Collections.Generic;
using System.Text;

namespace CCoop.Net.Session
{
    /// <summary>
    /// Bytes and messages sent and received, by message type, since the last
    /// <see cref="Take"/>. Counts what this machine puts on the wire (a host broadcast
    /// to three clients counts three times), which is what a bandwidth budget needs.
    /// </summary>
    public sealed class TrafficStats
    {
        private sealed class Counter
        {
            public long SentBytes, ReceivedBytes;
            public int Sent, Received;
        }

        private readonly Dictionary<ushort, Counter> _byType = new Dictionary<ushort, Counter>();
        private readonly object _lock = new object();

        public long SentBytes { get; private set; }
        public long ReceivedBytes { get; private set; }

        private Counter Of(ushort id)
        {
            if (!_byType.TryGetValue(id, out var c)) _byType[id] = c = new Counter();
            return c;
        }

        internal void CountSent(ushort typeId, int bytes)
        {
            lock (_lock)
            {
                var c = Of(typeId);
                c.Sent++;
                c.SentBytes += bytes;
                SentBytes += bytes;
            }
        }

        internal void CountReceived(ushort typeId, int bytes)
        {
            lock (_lock)
            {
                var c = Of(typeId);
                c.Received++;
                c.ReceivedBytes += bytes;
                ReceivedBytes += bytes;
            }
        }

        public struct Line
        {
            public ushort TypeId;
            public string Name;
            public long SentBytes, ReceivedBytes;
            public int Sent, Received;
        }

        /// <summary>The counts so far, biggest first, and a fresh start.</summary>
        public List<Line> Take(System.Func<ushort, string> name)
        {
            var lines = new List<Line>();
            lock (_lock)
            {
                foreach (var kv in _byType)
                    lines.Add(new Line
                    {
                        TypeId = kv.Key, Name = name != null ? name(kv.Key) : kv.Key.ToString(),
                        SentBytes = kv.Value.SentBytes, ReceivedBytes = kv.Value.ReceivedBytes,
                        Sent = kv.Value.Sent, Received = kv.Value.Received,
                    });
                _byType.Clear();
                SentBytes = ReceivedBytes = 0;
            }
            lines.Sort((a, b) => (b.SentBytes + b.ReceivedBytes).CompareTo(a.SentBytes + a.ReceivedBytes));
            return lines;
        }

        /// <summary>"up 12.3 KB/s, down 4.5 KB/s; PlayerState 8.1 up / 2.0 down, ..." over <paramref name="seconds"/>.</summary>
        public static string Describe(List<Line> lines, float seconds, int top = 6)
        {
            long up = 0, down = 0;
            foreach (var l in lines) { up += l.SentBytes; down += l.ReceivedBytes; }
            float s = seconds <= 0f ? 1f : seconds;
            var sb = new StringBuilder();
            sb.Append("up ").Append((up / 1024f / s).ToString("F1")).Append(" KB/s, down ").Append((down / 1024f / s).ToString("F1")).Append(" KB/s");
            for (int i = 0; i < lines.Count && i < top; i++)
            {
                var l = lines[i];
                sb.Append(i == 0 ? "; " : ", ").Append(l.Name).Append(' ')
                  .Append((l.SentBytes / 1024f / s).ToString("F1")).Append('/').Append((l.ReceivedBytes / 1024f / s).ToString("F1"));
            }
            return sb.ToString();
        }
    }
}
