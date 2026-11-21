using System.Runtime.InteropServices;
using System.Text;

namespace Islet
{
    /// <summary>
    /// Hears from Windows when something happens to a window, rather than looking again and again.
    /// Used for the taskbar sliding in and out, and for a notification pop-up appearing: both used
    /// to be checked many times a second, and now cost nothing until they happen.
    ///
    /// A pop-up the island is taking over is also made see-through the moment it appears, since
    /// Windows only lists the notification (so it can be closed properly) half a second later.
    /// It's made visible again as soon as Windows hides it, ready for the next one.
    /// </summary>
    public sealed class WinEventWatcher : IDisposable
    {
        const uint EventObjectLocationChange = 0x800B;
        const uint EventObjectCloaked = 0x8017;
        const uint EventObjectUncloaked = 0x8018;
        const uint OutOfContext = 0;

        delegate void WinEventProc(IntPtr hook, uint evt, IntPtr window, int idObject, int idChild, uint thread, uint time);

        readonly WinEventProc proc;
        readonly Action<IntPtr> moved;
        readonly Func<bool> toastAppeared;
        IntPtr locationHook;
        IntPtr uncloakHook;
        IntPtr taskbar;
        IntPtr hiddenToast;

        /// <summary>True while the events asked for are being heard.</summary>
        public bool Listening { get; private set; }

        /// <param name="taskbarMoved">Called, on this thread, as the taskbar moves or resizes.</param>
        /// <param name="toastAppeared">Called when Windows shows a notification pop-up; true to keep the pop-up out of sight.</param>
        public WinEventWatcher(Action taskbarMoved, Func<bool> toastAppeared)
        {
            moved = _ => taskbarMoved();
            this.toastAppeared = toastAppeared;
            proc = OnEvent;

            // Left see-through if Islet was stopped mid-way, which would hide every pop-up after
            RevealStrayToast();
        }

        /// <summary>Starts (or restarts) listening. Must be called on a thread that pumps messages, like the UI's.</summary>
        public bool Start(bool watchTaskbar, bool watchToasts)
        {
            Stop();

            if (watchTaskbar)
            {
                taskbar = FindWindow("Shell_TrayWnd", null);

                if (taskbar != IntPtr.Zero)
                {
                    uint thread = GetWindowThreadProcessId(taskbar, out uint process);

                    // Only that one thread's changes are sent over: nothing else on the machine
                    locationHook = SetWinEventHook(EventObjectLocationChange, EventObjectLocationChange, IntPtr.Zero, proc, process, thread, OutOfContext);
                }
            }

            if (watchToasts)
                uncloakHook = SetWinEventHook(EventObjectCloaked, EventObjectUncloaked, IntPtr.Zero, proc, 0, 0, OutOfContext);

            Listening = (!watchTaskbar || locationHook != IntPtr.Zero) && (!watchToasts || uncloakHook != IntPtr.Zero);
            return Listening;
        }

        public void Stop()
        {
            RevealToast();

            if (locationHook != IntPtr.Zero)
                UnhookWinEvent(locationHook);

            if (uncloakHook != IntPtr.Zero)
                UnhookWinEvent(uncloakHook);

            locationHook = uncloakHook = IntPtr.Zero;
            taskbar = IntPtr.Zero;
            Listening = false;
        }

        /// <summary>False once Explorer has restarted and the taskbar is a different window than the one watched.</summary>
        public bool TaskbarStillWatched => taskbar != IntPtr.Zero && FindWindow("Shell_TrayWnd", null) == taskbar;

        void OnEvent(IntPtr hook, uint evt, IntPtr window, int idObject, int idChild, uint thread, uint time)
        {
            // Of everything that happens to a window, only the window itself
            if (idObject != 0)
                return;

            if (evt == EventObjectLocationChange)
            {
                if (window == taskbar)
                    moved(window);
            }
            else if (evt == EventObjectUncloaked)
            {
                if (IsToastWindow(window) && toastAppeared())
                    HideToast(window);
            }
            else if (evt == EventObjectCloaked && window == hiddenToast)
            {
                // Windows has put it away: visible again for the next pop-up
                RevealToast();
            }
        }

        void HideToast(IntPtr window)
        {
            long style = GetWindowLongPtr(window, ExStyle).ToInt64();

            if (SetWindowLongPtr(window, ExStyle, new IntPtr(style | Layered)) == IntPtr.Zero && Marshal.GetLastWin32Error() != 0)
                return;

            if (SetLayeredWindowAttributes(window, 0, 0, LayeredAlpha))
                hiddenToast = window;
            else
                SetWindowLongPtr(window, ExStyle, new IntPtr(style & ~Layered));
        }

        /// <summary>Makes the pop-up made see-through visible again: it wasn't one the island took over after all.</summary>
        public void RevealToast()
        {
            if (hiddenToast == IntPtr.Zero)
                return;

            Reveal(hiddenToast);
            hiddenToast = IntPtr.Zero;
        }

        static void Reveal(IntPtr window)
        {
            long style = GetWindowLongPtr(window, ExStyle).ToInt64();

            if ((style & Layered) != 0)
                SetWindowLongPtr(window, ExStyle, new IntPtr(style & ~Layered));
        }

        static void RevealStrayToast()
        {
            var window = FindWindow("Windows.UI.Core.CoreWindow", "New notification");

            if (window != IntPtr.Zero)
                Reveal(window);
        }

        /// <summary>The window Windows shows a notification in.</summary>
        static bool IsToastWindow(IntPtr window)
        {
            var title = new StringBuilder(64);

            if (GetWindowText(window, title, title.Capacity) == 0 || title.ToString() != "New notification")
                return false;

            var name = new StringBuilder(64);
            return GetClassName(window, name, name.Capacity) > 0 && name.ToString() == "Windows.UI.Core.CoreWindow";
        }

        public void Dispose() => Stop();

        const int ExStyle = -20;
        const long Layered = 0x80000;
        const uint LayeredAlpha = 0x2;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

        [DllImport("user32.dll")]
        static extern bool SetLayeredWindowAttributes(IntPtr window, uint key, byte alpha, uint flags);

        [DllImport("user32.dll")]
        static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint process, uint thread, uint flags);

        [DllImport("user32.dll")]
        static extern bool UnhookWinEvent(IntPtr hook);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindow(string className, string? windowName);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetWindowText(IntPtr window, StringBuilder text, int max);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetClassName(IntPtr window, StringBuilder text, int max);
    }
}
