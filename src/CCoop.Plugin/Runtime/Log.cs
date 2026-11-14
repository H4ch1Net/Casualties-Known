using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Logging;
using UnityEngine;

namespace CCoop.Plugin.Runtime
{
    /// <summary>
    /// Logs to BepInEx and to a per-process file. Two game instances on one machine
    /// share BepInEx\LogOutput.log and fight over it, so the per-process file is the
    /// one to read when testing co-op locally.
    /// </summary>
    internal static class Log
    {
        private static ManualLogSource _source;
        private static StreamWriter _file;
        private static readonly object Lock = new object();
        private static readonly Queue<string> Recent = new Queue<string>();

        internal static string FilePath { get; private set; }
        internal static bool Verbose;

        internal static void Init(ManualLogSource source)
        {
            _source = source;
            try
            {
                var dir = Path.Combine(Application.persistentDataPath, "CasualtiesCoop");
                Directory.CreateDirectory(dir);
                foreach (var old in Directory.GetFiles(dir, "coop-*.log"))
                {
                    try { if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-3)) File.Delete(old); }
                    catch { /* in use by another instance */ }
                }
                int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                FilePath = Path.Combine(dir, $"coop-{DateTime.Now:yyyyMMdd-HHmmss}-pid{pid}.log");
                _file = new StreamWriter(new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                {
                    AutoFlush = true,
                };
            }
            catch
            {
                _file = null;
            }
        }

        internal static void Info(string s) { _source?.LogInfo(s); Write("INFO ", s); }
        internal static void Warn(string s) { _source?.LogWarning(s); Write("WARN ", s); }
        internal static void Error(string s) { _source?.LogError(s); Write("ERROR", s); }

        internal static void Debug(string s)
        {
            if (!Verbose) return;
            _source?.LogDebug(s);
            Write("DEBUG", s);
        }

        /// <summary>The last few lines, newest last, for the in-game panel.</summary>
        internal static string[] Tail()
        {
            lock (Lock) return Recent.ToArray();
        }

        private static void Write(string level, string msg)
        {
            lock (Lock)
            {
                if (level != "DEBUG")
                {
                    Recent.Enqueue(DateTime.Now.ToString("HH:mm:ss") + " " + msg);
                    while (Recent.Count > 12) Recent.Dequeue();
                }
                if (_file == null) return;
                try { _file.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + level + " " + msg); }
                catch { /* logging must never throw into game code */ }
            }
        }
    }
}
