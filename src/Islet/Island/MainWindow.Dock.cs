using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Islet
{
    public partial class MainWindow
    {
        // Gap kept between the island and the screen's sides
        const double DockInset = 10;
        const double IslandTop = 10;

        // Dropped this close to the middle, it centres itself exactly
        const double CentreSnap = 18;

        // Hold this long to pick the island up; moving first counts as an ordinary press
        readonly DispatcherTimer holdTimer = new() { Interval = TimeSpan.FromSeconds(1) };

        // The screen x the island is centred on. Near a side, that side pushes it inward
        // as it grows, so it always opens fully on screen.
        readonly Spring dockX = new(0, 0.5, 0.8);

        // How far the island's middle was from the pointer when picked up; closes to 0
        // so the island eases under the pointer instead of jumping there
        readonly Spring grab = new(0, 0.28, 0.9);

        readonly Spring dragLift = new(0, 0.3, 0.7);
        readonly Spring dragPop = new(1, 0.3, 0.55);

        // Squash and stretch from how fast it moves, wobbling like jelly when it stops,
        // and a lean into the direction it's going. Little damping, so they overshoot.
        readonly Spring stretch = new(0, 0.32, 0.36);
        readonly Spring lean = new(0, 0.3, 0.42);

        readonly Spring backdropLevel = new(0, 0.22, 1);
        DragBackdrop? backdrop;

        bool pressing;
        bool dragging;
        bool centred;
        Point pressPoint;
        double lastPointerX = double.NaN;
        double dragVelocity;

        // Worked out each frame: the island's layout left inside the window, and the
        // point it grows and scales from (0 its left side, 1 its right side)
        double islandLeft;
        double islandOrigin = 0.5;

        // The stretch and lean cost a few transforms each frame, so Low mode goes without
        static bool DragFlair => Profile.Glow;

        static double ScreenWidth => SystemParameters.PrimaryScreenWidth;
        static double ScreenHeight => SystemParameters.PrimaryScreenHeight;

        // The window's top on screen: 0 at the top edge, just above the taskbar at the bottom.
        // While carried it follows the pointer up and down too.
        readonly Spring dockY = new(0, 0.5, 0.8);

        // Whether the island is laid out from the bottom of its window, growing upwards
        bool atBottom;

        // How far below the window's top the pointer was when it picked the island up
        double grabY;

        // The window's top when the island sits on an edge
        double WindowTopFor(IslandEdge edge) =>
            edge == IslandEdge.Bottom ? Math.Max(0, BottomEdge() - Height) : 0;

        // Windows says when the taskbar moves (see WinEventWatcher), so nothing looks at it meanwhile.
        // This slow check is only a safety net, for Explorer restarting and the like.
        readonly DispatcherTimer taskbarTimer = new() { Interval = TimeSpan.FromSeconds(2) };
        double lastBottomEdge = double.NaN;
        WinEventWatcher winEvents = null!;

        // The taskbar has moved: the island goes to just above it, or back to the edge once it's gone
        void OnTaskbarMoved()
        {
            if (dragging || Settings.IslandEdge != IslandEdge.Bottom)
                return;

            double edge = BottomEdge();

            if (double.IsNaN(lastBottomEdge) || Math.Abs(edge - lastBottomEdge) > 1)
            {
                lastBottomEdge = edge;

                // Quick enough to keep pace with the taskbar's own slide
                dockY.Configure(0.22, 0.9);
                dockY.Target = WindowTopFor(IslandEdge.Bottom);
                EnsureRendering();
            }
        }

        // Listens for the taskbar sliding (at the bottom edge) and for notification pop-ups
        // (while they're being cleared), and for nothing otherwise
        void SyncWinEvents()
        {
            bool taskbar = Settings.IslandEdge == IslandEdge.Bottom;
            bool toasts = notificationsRequested && Settings.ShowNotifications && Settings.HideWindowsPopups;

            if (!taskbar && !toasts)
            {
                winEvents.Stop();
                return;
            }

            if (!winEvents.Start(taskbar, toasts))
                Log.Info("Windows didn't take up the event listening; the slower checks do the job");

            notifications.EventsAvailable = toasts && winEvents.Listening;
        }

        /// <summary>
        /// Where the bottom of the usable screen is right now: just above the taskbar while it's
        /// along the bottom and showing. One set to hide itself slides almost wholly off the screen
        /// (a couple of pixels stay, to catch the pointer), and back up for the pointer, the Windows
        /// key or a notification; its window really moves, so following it covers every one of those,
        /// rising with it and settling only once it has gone.
        /// </summary>
        double BottomEdge()
        {
            double bottom = SystemParameters.WorkArea.Bottom;
            var taskbar = FindWindow("Shell_TrayWnd", null);

            if (taskbar == IntPtr.Zero || !GetWindowRect(taskbar, out var r) || PresentationSource.FromVisual(this) is not { CompositionTarget: { } target })
                return bottom;

            var top = target.TransformFromDevice.Transform(new Point(r.Left, r.Top));
            var bottomRight = target.TransformFromDevice.Transform(new Point(r.Right, r.Bottom));

            // Along the bottom: wider than it is tall, and in the lower half of the screen
            bool alongBottom = bottomRight.X - top.X > (bottomRight.Y - top.Y) * 2 && top.Y > ScreenHeight / 2;

            return alongBottom ? Math.Min(bottom, top.Y) : bottom;
        }

        void FollowTaskbar()
        {
            if (Settings.IslandEdge != IslandEdge.Bottom)
            {
                taskbarTimer.Stop();
                lastBottomEdge = double.NaN;
                return;
            }

            if (!taskbarTimer.IsEnabled)
                taskbarTimer.Start();
        }

        // The island's top, in the window
        double IslandTopInWindow(double h) =>
            atBottom ? Height - IslandContainer.Margin.Bottom - h : IslandContainer.Margin.Top;

        /// <summary>Where a band of the island's small row starts, in the window, to line things up beside it.</summary>
        double BandTop(double band) =>
            atBottom ? Height - IslandContainer.Margin.Bottom - band : IslandContainer.Margin.Top;

        /// <summary>The nearer edge for a point on screen: the top half or the bottom half.</summary>
        internal static IslandEdge EdgeFor(double y, double screenHeight) =>
            y > screenHeight / 2 ? IslandEdge.Bottom : IslandEdge.Top;

        /// <summary>
        /// Lays the island out from the bottom of its window (growing upwards), or the top, keeping
        /// it where it is on screen meanwhile, so the switch itself can't be seen.
        /// </summary>
        void LayOutFrom(bool bottom)
        {
            if (bottom == atBottom)
                return;

            double h = Math.Max(1, height.Value);
            double screenTop = Top + IslandTopInWindow(h);

            atBottom = bottom;
            IslandContainer.VerticalAlignment = bottom ? VerticalAlignment.Bottom : VerticalAlignment.Top;

            // Each view sits against the screen's edge, so it opens away from it
            foreach (UIElement child in IslandContent.Children)
            {
                if (child is FrameworkElement view && view.VerticalAlignment != VerticalAlignment.Stretch)
                {
                    view.VerticalAlignment = bottom ? VerticalAlignment.Bottom : VerticalAlignment.Top;

                    if (view.RenderTransformOrigin.Y is 0 or 1)
                        view.RenderTransformOrigin = new Point(view.RenderTransformOrigin.X, bottom ? 1 : 0);
                }
            }

            IslandContainer.Margin = bottom
                ? new Thickness(IslandContainer.Margin.Left, 0, 0, IslandTop)
                : new Thickness(IslandContainer.Margin.Left, IslandTop, 0, 0);

            // The window moves by however far the island moved inside it, and so does where
            // a carried island is held from
            double moved = (Top + IslandTopInWindow(h)) - screenTop;
            dockY.Snap(dockY.Value - moved);
            dockY.Target = dockY.Value;
            grabY += moved;
            Top = dockY.Value;
        }

        void SetUpDock()
        {
            // A taskbar sliding up, or back down, moves the island with it
            taskbarTimer.Tick += (s, e) =>
            {
                // Explorer restarting gives a new taskbar window, which the listening has to be pointed at again
                if (!winEvents.TaskbarStillWatched)
                    SyncWinEvents();

                OnTaskbarMoved();
            };

            holdTimer.Tick += (s, e) =>
            {
                holdTimer.Stop();

                if (pressing && !seeking && Mouse.LeftButton == MouseButtonState.Pressed)
                    StartDrag();
            };

            IslandContainer.PreviewMouseLeftButtonDown += Dock_PressDown;
            IslandContainer.PreviewMouseMove += Dock_MouseMove;
            IslandContainer.PreviewMouseLeftButtonUp += Dock_PressUp;
            IslandContainer.LostMouseCapture += (s, e) =>
            {
                if (dragging)
                    DropIsland();
            };

            // Its spot is a share of the screen's width, so it stays put across resolutions
            EventHandler displayChanged = (s, e) => Dispatcher.BeginInvoke(() =>
            {
                backdrop?.FitScreen();
                ApplyDock(snap: true);
            });
            SystemEvents.DisplaySettingsChanged += displayChanged;

            Closed += (s, e) =>
            {
                SystemEvents.DisplaySettingsChanged -= displayChanged;
                backdrop?.Close();
            };
        }

        /// <summary>The island's left edge: centred on x, but pushed in from the screen's sides.</summary>
        internal static double IslandLeftFor(double x, double islandWidth, double screenWidth) =>
            Math.Clamp(x - islandWidth / 2, DockInset, Math.Max(DockInset, screenWidth - DockInset - islandWidth));

        /// <summary>The window is narrower than the screen, so it's centred on x too, staying on screen.</summary>
        internal static double WindowLeftFor(double x, double windowWidth, double screenWidth) =>
            Math.Clamp(x - windowWidth / 2, 0, Math.Max(0, screenWidth - windowWidth));

        internal static bool NearCentre(double x, double screenWidth) =>
            Math.Abs(x - screenWidth / 2) <= CentreSnap;

        // Called from ApplySettings, so a drop, the tray menu and a new resolution all move it the same way
        void ApplyDock(bool snap = false)
        {
            if (dragging)
                return;

            double x = Math.Clamp(Settings.IslandX, 0, 1) * ScreenWidth;
            var edge = Settings.IslandEdge;

            LayOutFrom(edge == IslandEdge.Bottom);
            FollowTaskbar();

            if (snap)
            {
                dockX.Snap(x);
                dockY.Snap(WindowTopFor(edge));
            }
            else
            {
                dockX.Configure(0.55, 0.78);
                dockX.Target = x;

                // Across the screen to the other edge, with a little overshoot as it arrives
                dockY.Configure(0.5, 0.72);
                dockY.Target = WindowTopFor(edge);
            }

            EnsureRendering();
        }

        // Called from ApplyShape: moves the window along the top edge and the island inside it,
        // and shapes it while it moves
        void PlaceIsland(double w)
        {
            double x = dockX.Value + grab.Value;
            double left = IslandLeftFor(x, w, ScreenWidth);
            double windowLeft = WindowLeftFor(x, Width, ScreenWidth);

            // While dragged, only the window moves; the island keeps its place inside it,
            // so nothing has to be laid out again
            if (Math.Abs(Left - windowLeft) > 0.01)
                Left = windowLeft;

            if (Math.Abs(Top - dockY.Value) > 0.01)
                Top = dockY.Value;

            islandLeft = left - windowLeft;
            islandOrigin = Math.Clamp((x - left) / w, 0, 1);

            // Lifted away from whichever edge it sits on
            var margin = atBottom
                ? new Thickness(islandLeft, 0, 0, IslandTop + dragLift.Value)
                : new Thickness(islandLeft, IslandTop + dragLift.Value, 0, 0);

            if (IslandContainer.Margin != margin)
                IslandContainer.Margin = margin;

            IslandContainer.RenderTransformOrigin = new Point(islandOrigin, atBottom ? 1 : 0);

            // Faster means longer and thinner, and the bottom trails behind the top
            double speed = dragging ? dragVelocity : dockX.Velocity;
            stretch.Target = DragFlair ? Math.Min(0.1, Math.Abs(speed) / 18000) : 0;
            lean.Target = DragFlair ? Math.Clamp(-speed / 320, -7, 7) : 0;

            double scale = islandScale.Value * dragPop.Value;
            IslandScale.ScaleX = scale * (1 + stretch.Value);
            IslandScale.ScaleY = scale * (1 - 0.6 * stretch.Value);
            IslandSkew.AngleX = lean.Value;

            backdrop?.SetLevel(backdropLevel.Value);
        }

        // Called from the frame loop: follows the pointer itself, read once per frame,
        // rather than mouse events that arrive at their own pace
        bool StepDrag(double dt)
        {
            if (!dragging)
                return false;

            if (Mouse.LeftButton != MouseButtonState.Pressed)
            {
                DropIsland();
                return true;
            }

            var pointer = PointerPosition();
            double x = Math.Clamp(pointer.X, 0, ScreenWidth);

            if (!double.IsNaN(lastPointerX) && dt > 0)
                dragVelocity += ((x - lastPointerX) / dt - dragVelocity) * Math.Min(1, dt * 14);

            lastPointerX = x;

            // A soft magnet at the middle, with a little bump as it catches
            bool nearCentre = NearCentre(x, ScreenWidth);

            if (nearCentre != centred)
            {
                centred = nearCentre;
                backdrop?.ShowCentre(centred);

                if (centred && DragFlair)
                    dragPop.Velocity += 0.9;
            }

            dockX.Target = centred ? ScreenWidth / 2 : x;

            // Up and down it follows the pointer, laid out from whichever edge is nearer, so it
            // can be carried all the way to either; kept wholly on screen
            LayOutFrom(EdgeFor(pointer.Y, ScreenHeight) == IslandEdge.Bottom);

            double h = Math.Max(1, height.Value);
            double inWindow = IslandTopInWindow(h);
            dockY.Target = Math.Clamp(pointer.Y - grabY, -inWindow, Math.Max(-inWindow, ScreenHeight - inWindow - h));
            dragLift.Target = 0;

            // Keep every frame coming while it's held
            settleFrames = 2;
            return true;
        }

        Point PointerPosition()
        {
            GetCursorPos(out var point);
            var source = PresentationSource.FromVisual(this);

            return source?.CompositionTarget != null
                ? source.CompositionTarget.TransformFromDevice.Transform(new Point(point.X, point.Y))
                : new Point(point.X, point.Y);
        }

        // Back to normal when a press turns out not to be a hold
        void CancelHold()
        {
            pressing = false;
            holdTimer.Stop();

            if (!dragging)
            {
                dragPop.Configure(0.3, 0.55);
                dragPop.Target = 1;
                EnsureRendering();
            }
        }

        void Dock_PressDown(object sender, MouseButtonEventArgs e)
        {
            // An alert being dismissed, a click on a hidden island, or a file on the shelf
            if (e.Handled || surface is Surface.Hidden or Surface.Shelf or Surface.Palette or Surface.Locks)
                return;

            pressing = true;
            pressPoint = e.GetPosition(this);
            holdTimer.Stop();
            holdTimer.Start();

            // Ready before it's needed, so the pick-up doesn't stall on creating a window
            backdrop ??= new DragBackdrop(() => new WindowInteropHelper(this).Handle);
            backdrop.Prepare();

            // Sinks in slowly while held, so the pick-up a second later feels coming;
            // too slowly for a quick click to notice
            if (DragFlair)
            {
                dragPop.Configure(1.0, 1);
                dragPop.Target = 0.96;
                EnsureRendering();
            }
        }

        void Dock_MouseMove(object sender, MouseEventArgs e)
        {
            if (dragging)
            {
                // Nothing under the island reacts while it's carried
                e.Handled = true;
                return;
            }

            // Moving before the hold ends is a normal drag, like seeking
            if (pressing && (e.GetPosition(this) - pressPoint).Length > 4)
                CancelHold();
        }

        void Dock_PressUp(object sender, MouseButtonEventArgs e)
        {
            if (!dragging)
            {
                CancelHold();
                return;
            }

            // The drop isn't also a click on whatever was under the pointer
            e.Handled = true;
            DropIsland();
        }

        void StartDrag()
        {
            pressing = false;
            dragging = true;

            // Picked up as the small island
            hovering = false;
            hoverTimer.Stop();
            collapseTimer.Stop();

            var pointer = PointerPosition();
            double w = Math.Max(1, width.Value);
            double middle = Left + islandLeft + w / 2;

            dockX.Configure(0.07, 1);
            dockX.Snap(pointer.X);
            grab.Snap(middle - pointer.X);
            grab.Target = 0;

            dockY.Configure(0.07, 1);
            dockY.Snap(Top);
            grabY = pointer.Y - Top;

            lastPointerX = double.NaN;
            dragVelocity = 0;
            centred = false;

            // Pops up out of the press while the screen behind dims
            dragLift.Configure(0.3, 0.7);
            dragPop.Configure(0.34, 0.5);
            dragPop.Target = 1.08;
            backdropLevel.Target = 1;

            backdrop?.ShowCentre(false);

            Mouse.OverrideCursor = Cursors.SizeAll;
            IslandContainer.CaptureMouse();

            UpdateSurface();
            EnsureRendering();
        }

        void DropIsland()
        {
            dragging = false;
            Mouse.OverrideCursor = null;

            if (IslandContainer.IsMouseCaptured)
                IslandContainer.ReleaseMouseCapture();

            // Set down with a bounce: it springs back up to the edge, settles a touch
            // below full size, and squashes for a moment as it lands
            dragPop.Configure(0.42, 0.5);
            dragPop.Target = 1;
            dragLift.Configure(0.4, 0.45);
            dragLift.Target = 0;
            backdropLevel.Target = 0;

            if (DragFlair)
                stretch.Velocity += 1.2;

            // It stays where it was let go along the edge, without carrying on past it
            double x = Math.Clamp(dockX.Target, 0, ScreenWidth);
            dockX.Velocity *= 0.15;
            dockY.Velocity *= 0.3;

            // Up or down, it goes to the nearer edge: never left in between
            Settings.IslandEdge = EdgeFor(PointerPosition().Y, ScreenHeight);

            // Saving applies the settings, which settles it there
            Settings.IslandX = ScreenWidth > 0 ? x / ScreenWidth : 0.5;
            Settings.Save();

            UpdateSurface();
            EnsureRendering();
        }

        [StructLayout(LayoutKind.Sequential)]
        struct CursorPoint
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        static extern bool GetCursorPos(out CursorPoint point);

        [StructLayout(LayoutKind.Sequential)]
        struct WindowRect
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindow(string className, string? windowName);


        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr window, out WindowRect rect);
    }
}
