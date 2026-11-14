using System;
using System.Runtime.InteropServices;
using System.Text;

namespace CCoop.Plugin
{
    /// <summary>
    /// Stamps the session role onto the OS window title.
    ///
    /// The IMGUI overlay draws the role too, but the game's own canvas appears to cover
    /// it, and with two identical windows side by side there is otherwise no way to tell
    /// the host from the client. The title bar and taskbar entry are unmissable and
    /// survive whatever the game renders.
    /// </summary>
    internal static class WindowTitle
    {
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool SetWindowTextW(IntPtr hWnd, string text);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);

        private static string _current;
        private static string _note = string.Empty;

        /// <summary>
        /// Appends a marker to the title, replacing any previous one, until it is
        /// replaced again.
        ///
        /// This is the only channel that reliably reaches the player: the IMGUI overlay
        /// is drawn over by the game's canvas, so anything printed there is invisible.
        /// Used by F9 to put the fields being compared where a human can read both
        /// windows side by side.
        /// </summary>
        internal static void SetNote(string note)
        {
            _note = note ?? string.Empty;
            _current = null;   // force the next Set() to rewrite the title
        }

        /// <summary>
        /// Sets the title to "&lt;role&gt; — CasualtiesUnknown (pid N)", plus any note.
        /// Cheap to call repeatedly: it no-ops unless the text actually changed.
        /// </summary>
        internal static void Set(string role)
        {
            try
            {
                int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                var title = "[" + role + "] CasualtiesUnknown  (pid " + pid + ")" +
                            (_note.Length > 0 ? "   " + _note : string.Empty);
                if (title == _current) return;
                _current = title;

                IntPtr found = IntPtr.Zero;
                EnumWindows((hWnd, _) =>
                {
                    if (!IsWindowVisible(hWnd)) return true;

                    GetWindowThreadProcessId(hWnd, out uint owner);
                    if (owner != (uint)pid) return true;

                    // Several windows can belong to the process; take the first visible
                    // one that already has a title, which is the real game window.
                    var sb = new StringBuilder(256);
                    if (GetWindowTextW(hWnd, sb, sb.Capacity) > 0)
                    {
                        found = hWnd;
                        return false;
                    }
                    return true;
                }, IntPtr.Zero);

                if (found != IntPtr.Zero) SetWindowTextW(found, title);
            }
            catch (Exception e)
            {
                // Cosmetic only; never let it disturb the game.
                CoopPlugin.Warn("could not set window title: " + e.Message);
            }
        }
    }
}
