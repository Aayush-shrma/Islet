using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace Islet
{
    /// <summary>
    /// Where the taskbar's own buttons are across the screen (Start, search, every app's icon, the tray
    /// and clock), so an island docked in the taskbar can keep clear of them.
    ///
    /// Asked through UI Automation, the same way screen readers find them: Windows 11 draws the
    /// taskbar itself, and the old windows its buttons used to live in no longer say where they are
    /// (the apps' one stays the same size however many are open). Only the old way is left as a
    /// fallback for when UI Automation isn't there.
    /// </summary>
    static class TaskbarButtons
    {
        // Buttons closer than this are one group: there's no room for the island between them
        const double Join = 12;

        static readonly HashSet<string> Holders = new() { "Start", "MSTaskSwWClass", "TrayNotifyWnd" };

        /// <summary>
        /// Each group of buttons' left and right, in screen pixels. Null when they can't be seen right
        /// now (the taskbar has slid away), so the last good answer can be kept. Takes about a tenth of
        /// a second, so it's for a background thread.
        /// </summary>
        public static List<(double Left, double Right)>? Read(IntPtr taskbar)
        {
            if (!GetWindowRect(taskbar, out var bar))
                return null;

            List<(double Left, double Right)> buttons;

            try
            {
                buttons = ReadAutomation(taskbar, bar);
            }
            catch
            {
                buttons = ReadWindows(taskbar, bar);
            }

            // Hidden, the taskbar shows almost nothing to UI Automation: not an empty taskbar
            if (buttons.Count < 2)
                return null;

            return Group(buttons);
        }

        static List<(double, double)> ReadAutomation(IntPtr taskbar, Rect bar)
        {
            var found = new List<(double, double)>();
            var root = AutomationElement.FromHandle(taskbar);
            var isButton = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);

            foreach (AutomationElement element in root.FindAll(TreeScope.Descendants, isButton))
            {
                var r = element.Current.BoundingRectangle;

                // Only what's really on the taskbar, not a stray far off it or an empty one
                if (r.IsEmpty || r.Width <= 0 || r.Width > (bar.Right - bar.Left) / 2 || r.Left < bar.Left - 1 || r.Right > bar.Right + 1)
                    continue;

                found.Add((r.Left, r.Right));
            }

            return found;
        }

        // The old way: the windows the buttons used to live in
        static List<(double, double)> ReadWindows(IntPtr taskbar, Rect bar)
        {
            var spans = new List<(double, double)>();
            var name = new StringBuilder(64);

            EnumChildWindows(taskbar, (child, _) =>
            {
                name.Clear();
                GetClassName(child, name, name.Capacity);

                if (Holders.Contains(name.ToString()) && GetWindowRect(child, out var r) &&
                    r.Right > r.Left && r.Left >= bar.Left && r.Right <= bar.Right)
                {
                    spans.Add((r.Left, r.Right));
                }

                return true;
            }, IntPtr.Zero);

            return spans;
        }

        /// <summary>Joins buttons that touch, or nearly, into one span each.</summary>
        internal static List<(double Left, double Right)> Group(IEnumerable<(double Left, double Right)> buttons)
        {
            var groups = new List<(double Left, double Right)>();

            foreach (var (left, right) in buttons.OrderBy(b => b.Left))
            {
                if (groups.Count > 0 && left <= groups[^1].Right + Join)
                    groups[^1] = (groups[^1].Left, Math.Max(groups[^1].Right, right));
                else
                    groups.Add((left, right));
            }

            return groups;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        delegate bool EnumProc(IntPtr window, IntPtr data);

        [DllImport("user32.dll")]
        static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetClassName(IntPtr window, StringBuilder name, int size);

        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr window, out Rect rect);
    }
}
