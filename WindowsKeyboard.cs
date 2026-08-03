using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SR160PowerConfig
{
    // One row in the "type into" picker. ToString drives what the combo box
    // shows, so it doubles as the display text.
    internal sealed class WindowEntry
    {
        public readonly IntPtr Handle;
        public readonly string Title;
        public readonly string ProcessName;

        public WindowEntry(IntPtr handle, string title, string processName)
        {
            Handle = handle;
            Title = title ?? string.Empty;
            ProcessName = processName ?? string.Empty;
        }

        public override string ToString()
        {
            return ProcessName.Length > 0 ? Title + "  —  " + ProcessName : Title;
        }
    }

    // Delivery for the external-output relay. Deliberately does NOT steal
    // system foreground focus (no SetForegroundWindow/SendInput) — after an
    // earlier version injected scan data into whatever window happened to be
    // focused (including this app's own chat window), delivery now only
    // ever targets a window the user explicitly picked beforehand, and reads
    // that specific window's OWN internally-focused control via its own
    // thread's GUI state — which works whether or not that window is the
    // one currently in front — then posts WM_CHAR/WM_KEYDOWN/WM_KEYUP
    // directly to it. This can never touch any window other than the one
    // picked, and never moves focus away from whatever you're actually
    // doing right now.
    internal static class WindowsKeyboard
    {
        private struct GUITHREADINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hwndActive;
            public IntPtr hwndFocus;
            public IntPtr hwndCapture;
            public IntPtr hwndMenuOwner;
            public IntPtr hwndMoveSize;
            public IntPtr hwndCaret;
            public int rcCaretLeft;
            public int rcCaretTop;
            public int rcCaretRight;
            public int rcCaretBottom;
        }

        private const int WM_CHAR = 0x0102;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        public const int VK_RETURN = 0x0D;

        // Visible top-level windows with a non-empty title, excluding the
        // given window (this app's own). The owning process name is captured
        // alongside the title because window handles are meaningless after a
        // restart and titles move around (Excel retitles itself per
        // workbook), so the process is the durable half of the identity.
        public static List<WindowEntry> EnumerateTopLevelWindows(IntPtr excludeWindow)
        {
            Dictionary<int, string> processNames = new Dictionary<int, string>();
            try
            {
                foreach (Process proc in Process.GetProcesses())
                {
                    try { processNames[proc.Id] = proc.ProcessName; }
                    catch { }
                    proc.Dispose();
                }
            }
            catch { }

            List<WindowEntry> results = new List<WindowEntry>();
            EnumWindows(delegate (IntPtr hWnd, IntPtr lParam)
            {
                if (hWnd == excludeWindow || !IsWindowVisible(hWnd)) return true;
                int len = GetWindowTextLength(hWnd);
                if (len == 0) return true;
                StringBuilder sb = new StringBuilder(len + 1);
                GetWindowText(hWnd, sb, sb.Capacity);
                string title = sb.ToString();
                if (title.Length == 0) return true;

                uint pid = 0;
                GetWindowThreadProcessIdOut(hWnd, out pid);
                string owner;
                processNames.TryGetValue((int)pid, out owner);

                results.Add(new WindowEntry(hWnd, title, owner));
                return true;
            }, IntPtr.Zero);
            return results;
        }

        public static bool IsValidWindow(IntPtr hWnd)
        {
            return hWnd != IntPtr.Zero && IsWindow(hWnd);
        }

        // Resolves the control currently focused WITHIN the given window's
        // own thread — this works even when that window is not the system's
        // foreground window, since GUI thread state is per-thread, not
        // global. Falls back to the window itself if no specific child has
        // focus.
        public static IntPtr GetFocusedControlInWindow(IntPtr window)
        {
            if (window == IntPtr.Zero || !IsWindow(window)) return IntPtr.Zero;
            uint threadId = GetWindowThreadProcessId(window, IntPtr.Zero);
            GUITHREADINFO info = new GUITHREADINFO();
            info.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));
            if (GetGUIThreadInfo(threadId, ref info) && info.hwndFocus != IntPtr.Zero)
                return info.hwndFocus;
            return window;
        }

        public static void SendTextLine(IntPtr targetWindow, string text)
        {
            if (targetWindow == IntPtr.Zero || !IsWindow(targetWindow))
                throw new InvalidOperationException("Target window no longer exists.");

            IntPtr control = GetFocusedControlInWindow(targetWindow);
            if (control == IntPtr.Zero) control = targetWindow;

            IntPtr lParam = new IntPtr(1);
            foreach (char c in text)
            {
                if (!PostMessage(control, WM_CHAR, (IntPtr)c, lParam))
                    throw new InvalidOperationException("PostMessage WM_CHAR failed.");
            }
            PostMessage(control, WM_KEYDOWN, (IntPtr)VK_RETURN, lParam);
            PostMessage(control, WM_KEYUP, (IntPtr)VK_RETURN, lParam);

            // Settle time before the caller sends the next line. The target
            // is busy committing this row and moving the caret right after
            // Enter, and a character posted into that gap gets swallowed —
            // seen as an EPC arriving one character short, always missing
            // its FIRST character. 20ms was not enough for Excel.
            Thread.Sleep(120);
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

        [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowThreadProcessId")]
        private static extern uint GetWindowThreadProcessIdOut(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);
    }
}
