using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace Islet
{
    /// <summary>
    /// Polls the foreground window and reports when an app covers the whole
    /// monitor the island lives on (games, F11 browsers, video players,
    /// PowerPoint slide shows, ...).
    /// </summary>
    public sealed class FullscreenWatcher
    {
        readonly DispatcherTimer timer = new()
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };

        readonly Func<IntPtr> ownWindow;

        // Kept in a field so the garbage collector doesn't free it while Windows still calls it
        readonly WinEventProc onForeground;
        IntPtr foregroundHook;

        public string? ProcessName { get; private set; }
        public string? DisplayName { get; private set; }

        public event Action? Changed;

        /// <summary>Another window came to the front.</summary>
        public event Action? ForegroundChanged;

        public FullscreenWatcher(Func<IntPtr> ownWindow)
        {
            this.ownWindow = ownWindow;
            timer.Tick += (s, e) => Poll();
            onForeground = (hook, type, hwnd, idObject, idChild, thread, time) =>
            {
                // React at once instead of on the next poll
                Poll();
                ForegroundChanged?.Invoke();
            };
        }

        public void Start()
        {
            Poll();
            timer.Start();

            // The polling stays for apps that go full-screen without changing the front window (F11)
            if (foregroundHook == IntPtr.Zero)
                foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                    IntPtr.Zero, onForeground, 0, 0, WINEVENT_OUTOFCONTEXT);
        }

        public void Stop()
        {
            timer.Stop();

            if (foregroundHook != IntPtr.Zero)
            {
                UnhookWinEvent(foregroundHook);
                foregroundHook = IntPtr.Zero;
            }
        }

        // The app last found full screen, so it's looked up once rather than on every poll
        uint describedPid;
        (string Process, string Display)? described;

        void Poll()
        {
            string? process = null;
            string? display = null;

            try
            {
                var hwnd = DetectFullscreenWindow();

                if (hwnd != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(hwnd, out uint pid);

                    if (described == null || pid != describedPid)
                    {
                        describedPid = pid;
                        described = DescribeProcess(pid);
                    }

                    (process, display) = described.Value;
                }
                else
                {
                    // Forgotten once nothing is full screen, so a reused process ID can't be mistaken for it
                    described = null;
                }
            }
            catch
            {
                described = null;
            }

            if (process == ProcessName)
                return;

            ProcessName = process;
            DisplayName = display;
            Changed?.Invoke();
        }

        IntPtr DetectFullscreenWindow()
        {
            var hwnd = GetForegroundWindow();
            var own = ownWindow();

            if (hwnd == IntPtr.Zero || hwnd == own || !IsWindowVisible(hwnd))
                return IntPtr.Zero;

            // Islet's own windows (the screensaver covers the whole screen) aren't a game or a film
            GetWindowThreadProcessId(hwnd, out uint owner);

            if (owner == Environment.ProcessId)
                return IntPtr.Zero;

            var className = new StringBuilder(256);
            GetClassName(hwnd, className, className.Capacity);

            switch (className.ToString())
            {
                case "Progman":
                case "WorkerW":
                case "Shell_TrayWnd":
                case "Shell_SecondaryTrayWnd":
                case "Windows.UI.Core.CoreWindow":
                case "XamlExplorerHostIslandWindow":
                    return IntPtr.Zero;
            }

            // Only care about the monitor the island is shown on
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);

            if (own != IntPtr.Zero &&
                monitor != MonitorFromWindow(own, MONITOR_DEFAULTTONEAREST))
                return IntPtr.Zero;

            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };

            if (!GetMonitorInfo(monitor, ref info) || !GetWindowRect(hwnd, out var rect))
                return IntPtr.Zero;

            bool coversMonitor =
                rect.Left <= info.rcMonitor.Left &&
                rect.Top <= info.rcMonitor.Top &&
                rect.Right >= info.rcMonitor.Right &&
                rect.Bottom >= info.rcMonitor.Bottom;

            if (coversMonitor)
            {
                // A normal maximised window with an auto-hiding taskbar also
                // covers the monitor; it still has a caption, so skip it.
                long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
                bool captioned = (style & WS_CAPTION) == WS_CAPTION;

                if (IsZoomed(hwnd) && captioned)
                    return IntPtr.Zero;

                return hwnd;
            }

            // Exclusive-mode D3D games and presentation mode
            if (SHQueryUserNotificationState(out int state) == 0 &&
                (state == QUNS_RUNNING_D3D_FULL_SCREEN || state == QUNS_PRESENTATION_MODE))
                return hwnd;

            return IntPtr.Zero;
        }

        static (string Process, string Display) DescribeProcess(uint pid)
        {
            string name;

            using (var process = Process.GetProcessById((int)pid))
                name = process.ProcessName;

            string display = name;

            try
            {
                if (ProcessImage.PathOf((int)pid) is { } path)
                {
                    var description = FileVersionInfo.GetVersionInfo(path).FileDescription;

                    if (!string.IsNullOrWhiteSpace(description))
                        display = description.Trim();
                }
            }
            catch
            {
                // Protected processes cannot be inspected; the name still says enough
            }

            return (name, display);
        }

        const int MONITOR_DEFAULTTONEAREST = 2;
        const int GWL_STYLE = -16;
        const long WS_CAPTION = 0x00C00000;
        const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
        const int QUNS_PRESENTATION_MODE = 4;
        const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        const uint WINEVENT_OUTOFCONTEXT = 0x0000;

        delegate void WinEventProc(IntPtr hook, uint type, IntPtr hwnd,
            int idObject, int idChild, uint thread, uint time);

        [DllImport("user32.dll")]
        static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module,
            WinEventProc callback, uint process, uint thread, uint flags);

        [DllImport("user32.dll")]
        static extern bool UnhookWinEvent(IntPtr hook);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool IsZoomed(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);

        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        static extern IntPtr MonitorFromWindow(IntPtr hWnd, int flags);

        [DllImport("user32.dll")]
        static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [DllImport("user32.dll")]
        static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

        [DllImport("shell32.dll")]
        static extern int SHQueryUserNotificationState(out int state);
    }
}
