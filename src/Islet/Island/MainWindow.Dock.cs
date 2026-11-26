using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using Rectangle = System.Windows.Shapes.Rectangle;

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
        double WindowTopFor(IslandEdge edge)
        {
            if (edge == IslandEdge.Top)
                return 0;

            if (InTaskbarMode && taskbarBand is { } band)
                return TaskbarWindowTop(band);

            return Math.Max(0, BottomEdge() - Height);
        }

        // Windows says when the taskbar moves (see WinEventWatcher), so nothing looks at it meanwhile.
        // This slow check is only a safety net, for Explorer restarting and the like.
        readonly DispatcherTimer taskbarTimer = new() { Interval = TimeSpan.FromSeconds(2) };
        double lastBottomEdge = double.NaN;
        WinEventWatcher winEvents = null!;

        // ================= In the taskbar =================

        // The taskbar's strip on screen while it runs along the bottom, as last seen; null otherwise
        (double Top, double Bottom)? taskbarBand;

        // 1 sitting in the taskbar's strip, 0 just above the taskbar. Hovering drains the island up
        // out of the strip, opening above it, and it sinks back in as it closes; the window's place
        // and the island's gap in it both follow this one spring, so the move is a single motion.
        // Slow and fully damped, so it flows out and settles back without any bounce.
        readonly Spring mountLevel = new(1, 0.45, 1);

        // In the taskbar the window reaches down into the taskbar's strip and then holds still: moving a
        // see-through window every frame is what made the flow choppy, so only the island moves inside it.
        // The window's usual height, and whether it's stretched into the strip right now.
        double baseHeight = double.NaN;
        bool tallWindow;

        // How long the island opened out of the taskbar stays open once the pointer has left it,
        // so a pointer that strays off for a moment doesn't close it
        static readonly TimeSpan TaskbarCloseDelay = TimeSpan.FromSeconds(1.1);

        // Eases the island into the taskbar from wherever it was (the other edge, or a drop),
        // rather than jumping there; the taskbar's own moves are followed exactly, on top of it
        readonly Spring dockArrive = new(0, 0.5, 0.78);

        // The taskbar's buttons across the screen (left and right, in the window's units), as last read:
        // the island sits in a gap between them
        List<(double Left, double Right)> taskbarSpans = new();

        // A thin glowing line left at the screen's bottom edge while an auto-hiding taskbar has taken
        // the island away with it, so it's still there to find (and to hover open)
        readonly Spring sliverLevel = new(0, 0.3, 1);

        // ================= Its own look in the taskbar =================

        // The one look it has resting in the taskbar, for music and idle alike: shaped and tinted like
        // a taskbar button rather than the island's black capsule
        readonly Panel dockedMusicPanel, dockedIdlePanel;
        readonly Rectangle[] dockedBars;

        // 1 wearing the taskbar's look, 0 the island's own; the two cross-fade as it drains out and back
        readonly Spring dockBlend = new(0, 0.35, 1);

        // Square-ish corners like the taskbar's buttons
        const double DockedRadius = 7;

        bool? dockedLight;

        /// <summary>Sitting in the taskbar right now, where it takes the docked look.</summary>
        bool DockedNow => Settings.IslandEdge == IslandEdge.Taskbar && !dragging && (taskbarBand ??= ReadTaskbarBand()) != null;

        /// <summary>
        /// About the music pill's width with room for its controls, or idle, a small square-ish button
        /// (a little wider with the weather in it); as tall as the taskbar allows, with a margin.
        /// </summary>
        (double Width, double Height) DockedSize(bool idle = false)
        {
            double strip = taskbarBand is { } band ? band.Bottom - band.Top : 48;
            double height = Math.Clamp(strip - 8, 24, 40);

            double music = DockedMusicWidths[Math.Min((int)dockFit, DockedMusicWidths.Length - 1)];

            // Idle, as small as the music button may be, and never wider
            if (idle)
                return (Math.Min(weather != null ? 72 : 48, music), height);

            return (music, height);
        }

        // ================= Opened from the taskbar =================

        // Opened by a click on the docked button; it then closes on a click anywhere else, or once the
        // pointer has been away from it for a moment
        bool openedFromTaskbar;
        DateTime lastInsideOpen;
        readonly DispatcherTimer openWatch = new() { Interval = TimeSpan.FromMilliseconds(80) };

        void Docked_Click(object sender, MouseButtonEventArgs e)
        {
            if (dragging || e.Handled)
                return;

            e.Handled = true;
            OpenFromTaskbar();
        }

        void DockedExpand_Click(object sender, RoutedEventArgs e) => OpenFromTaskbar();

        void OpenFromTaskbar()
        {
            if (!InTaskbarMode || hovering)
                return;

            hoverTimer.Stop();
            collapseTimer.Stop();
            ShowDockedHover(false);

            hovering = true;
            openedFromTaskbar = true;
            lastInsideOpen = DateTime.UtcNow;

            if (!openWatchSet)
            {
                openWatchSet = true;
                openWatch.Tick += (s, e) => WatchOpen();
            }

            openWatch.Start();
            UpdateSurface();
        }

        bool openWatchSet;

        void WatchOpen()
        {
            // Closed some other way (picked up, a panel opened, the setting changed)
            if (!openedFromTaskbar || !hovering || !InTaskbarMode)
            {
                CloseFromTaskbar(stillOpen: hovering && openedFromTaskbar);
                return;
            }

            bool inside = IslandContainer.IsMouseOver || IslandContainer.ContextMenu?.IsOpen == true || seeking;

            if (inside)
            {
                lastInsideOpen = DateTime.UtcNow;
                return;
            }

            static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
            bool pressed = Down(0x01) || Down(0x02) || Down(0x04);

            if (pressed || DateTime.UtcNow - lastInsideOpen > TaskbarCloseDelay)
                CloseFromTaskbar(stillOpen: true);
        }

        void CloseFromTaskbar(bool stillOpen)
        {
            openWatch.Stop();
            openedFromTaskbar = false;

            if (!stillOpen)
                return;

            hovering = false;

            // A smaller layout put off while it was open can happen now
            if (shrinkHeld)
                ApplyLyricsLayout();

            UpdateSurface();
        }

        // Lit like a hovered taskbar button, with the song's controls in place of the bars
        void ShowDockedHover(bool on)
        {
            on &= InTaskbarMode && !hovering && surface is Surface.DockedMusic or Surface.DockedIdle;

            if (dockedHovered == on)
                return;

            dockedHovered = on;

            var duration = TimeSpan.FromMilliseconds(140);
            bool controls = on && surface == Surface.DockedMusic;

            DockedControls.IsHitTestVisible = controls;
            DockedControls.BeginAnimation(OpacityProperty, new DoubleAnimation(controls ? 1 : 0, duration));
            DockedBars.BeginAnimation(OpacityProperty, new DoubleAnimation(controls ? 0 : 1, duration));
            DockedExpand.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 1 : 0.55, duration));

            if (dockedRestBrush != null)
                DockedShape.Background = on ? dockedHoverBrush : dockedRestBrush;
        }

        bool dockedHovered;
        Brush? dockedRestBrush;
        Brush? dockedHoverBrush;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int key);

        // ================= Looking like Windows =================

        // Opened out of the taskbar it looks like Windows' own flyouts (the volume panel, the calendar):
        // dark grey, a hairline edge and small corners, rather than the island's black capsule
        const double FlyoutRadius = 14;

        // The card's edge, drawn over everything in it so nothing paints over it: light catching the
        // top, fading down the sides, the way Windows 11's own panels are edged
        readonly System.Windows.Controls.Border flyoutEdge = new()
        {
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
            Opacity = 0,
            BorderBrush = Freeze(new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromArgb(0x4C, 0xFF, 0xFF, 0xFF), 0),
                    new(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF), 0.35),
                    new(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF), 1)
                }, new Point(0, 0), new Point(0, 1)))
        };

        // A soft shadow under the open card, lifting it off what's behind (smoothest mode only)
        readonly System.Windows.Media.Effects.DropShadowEffect flyoutShadow = new()
        {
            BlurRadius = 26,
            ShadowDepth = 5,
            Direction = 270,
            Color = Colors.Black,
            Opacity = 0,
            RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance
        };
        static readonly Brush FlyoutBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xF5, 0x20, 0x20, 0x20)));
        bool? flyoutLook;

        static Brush Freeze(Brush brush)
        {
            brush.Freeze();
            return brush;
        }

        // Windows' accent colour, in the shade its own taskbar uses on a light or dark taskbar
        static Color WindowsAccent(bool lightTaskbar)
        {
            try
            {
                var settings = new Windows.UI.ViewManagement.UISettings();
                var c = settings.GetColorValue(lightTaskbar
                    ? Windows.UI.ViewManagement.UIColorType.AccentDark1
                    : Windows.UI.ViewManagement.UIColorType.AccentLight2);
                return Color.FromRgb(c.R, c.G, c.B);
            }
            catch
            {
                return lightTaskbar ? Color.FromRgb(0x00, 0x5F, 0xB8) : Color.FromRgb(0x99, 0xEB, 0xFF);
            }
        }


        // Light text on a dark taskbar, dark on a light one, and a tint like that taskbar's hovered buttons
        void ApplyDockedTheme()
        {
            bool light = false;

            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                light = key?.GetValue("SystemUsesLightTheme") is int value && value == 1;

            if (dockedLight == light)
                return;

            dockedLight = light;

            Brush Frozen(byte a, byte rgb)
            {
                var brush = new SolidColorBrush(Color.FromArgb(a, rgb, rgb, rgb));
                brush.Freeze();
                return brush;
            }

            dockedRestBrush = light ? Frozen(0x12, 0) : Frozen(0x1A, 0xFF);
            dockedHoverBrush = light ? Frozen(0x22, 0) : Frozen(0x2C, 0xFF);
            DockedShape.Background = dockedHovered ? dockedHoverBrush : dockedRestBrush;
            DockedShape.BorderBrush = light ? Frozen(0x14, 0) : Frozen(0x16, 0xFF);

            var main = light ? Frozen(0xE6, 0) : Brushes.White;
            var soft = light ? Frozen(0x99, 0) : Frozen(0x99, 0xFF);

            DockedTitle.Foreground = main;
            DockedArtist.Foreground = soft;
            DockedTemp.Foreground = main;
            DockedIdleMark.Background = main;

            foreach (var button in new[] { DockedPrevious, DockedPlayPause, DockedNext, DockedExpand })
                button.Foreground = main;

            // The bars, and the line left while the taskbar is hidden, in Windows' accent colour
            var accent = new SolidColorBrush(WindowsAccent(light));
            accent.Freeze();

            foreach (var bar in dockedBars)
                bar.Fill = accent;

            if (TaskbarSliver.Child is System.Windows.Controls.Border line)
                line.Background = accent;
        }

        // Each frame: the island's background and corners, between its own look and the taskbar's
        double ApplyDockBlend(double w, double h)
        {
            double blend = Math.Clamp(dockBlend.Value, 0, 1);

            // Closing back into the taskbar, the card stays solid until it has nearly shrunk to the
            // button, and only then takes the taskbar's look, so it never fades while still large
            if (Choreographed && dockBlend.Target > 0.5)
                blend *= ContentReveal();

            bool flyout = InTaskbarMode;

            if (flyoutLook != flyout)
            {
                flyoutLook = flyout;
                IslandShape.Background = flyout ? FlyoutBrush : Brushes.Black;
                IslandShape.BorderBrush = null;
                IslandShape.BorderThickness = new Thickness(0);
            }

            double radius = flyout ? Math.Min(FlyoutRadius, Math.Min(w, h) / 2) : GlyphPattern.IslandRadius(w, h);
            radius += (Math.Min(DockedRadius, Math.Min(w, h) / 2) - radius) * blend;

            if (Choreographed)
            {
                // Opaque almost at once, so nothing shows through the card as it opens or closes
                double own = Math.Clamp((1 - blend) / 0.35, 0, 1);
                IslandShape.Opacity = own * own * (3 - 2 * own);
            }
            else
            {
                IslandShape.Opacity = 1 - blend;
            }

            DockedShape.Opacity = blend;
            DockedShape.CornerRadius = new CornerRadius(radius);

            // The edge and the shadow belong to the open card, not to the button in the taskbar
            double open = flyout ? 1 - blend : 0;
            flyoutEdge.CornerRadius = new CornerRadius(radius);
            flyoutEdge.Opacity = open;
            flyoutShadow.Opacity = 0.5 * open;

            // Only attached while it shows: even an invisible shadow costs drawing every frame
            var shadow = Choreographed && open > 0.01 ? flyoutShadow : null;

            if (!ReferenceEquals(IslandShape.Effect, shadow))
                IslandShape.Effect = shadow;

            return radius;
        }

        // Carried over the taskbar: an outline of where it will land, sliding between the gaps
        readonly Spring slotLevel = new(0, 0.2, 1);
        readonly Spring slotX = new(0, 0.28, 0.86);
        (double Top, double Bottom)? dragBand;

        /// <summary>Set to sit in the taskbar, and the taskbar is along the bottom where it can.</summary>
        bool InTaskbarMode => Settings.IslandEdge == IslandEdge.Taskbar && taskbarBand != null && !dragging;

        // An auto-hiding taskbar slid away: only a couple of pixels of it are left on screen
        bool TaskbarHidden => taskbarBand is { } band && band.Top >= ScreenHeight - 6;

        // What shouldn't be missed: while the taskbar is hidden, these come up to the screen's
        // bottom edge to show, instead of staying down with it
        bool ShowsAboveHiddenTaskbar => surface is Surface.Volume or Surface.StatusSmall or Surface.StatusRing or Surface.TimerPill;

        /// <summary>
        /// Whether the island belongs in the taskbar's strip right now: its small looks fit in it and
        /// stay there; anything bigger (opened by hovering, a song peek, a notification) comes up out
        /// of it to open just above the taskbar.
        /// </summary>
        bool RestsInTaskbar(double islandHeight) =>
            InTaskbarMode && taskbarBand is { } band &&
            islandHeight <= band.Bottom - band.Top - 6 &&
            width.Target <= FreeWidthAt(dockX.Target, taskbarSpans, ScreenWidth) + 0.5 &&
            !(TaskbarHidden && ShowsAboveHiddenTaskbar);

        // Where the window's top goes in the taskbar mode: its bottom on the taskbar's bottom
        double TaskbarWindowTop((double Top, double Bottom) band) => band.Bottom - Height;

        /// <summary>
        /// Stretches the window down into the taskbar's strip in the taskbar mode, and back, keeping the
        /// island exactly where it is on screen meanwhile so the change itself can't be seen.
        /// </summary>
        void SyncWindowFit()
        {
            if (double.IsNaN(baseHeight))
                baseHeight = Height;

            bool tall = InTaskbarMode && taskbarBand != null;
            double wanted = tall && taskbarBand is { } b ? baseHeight + b.Bottom - b.Top + IslandTop : baseHeight;

            if (tall == tallWindow && Math.Abs(Height - wanted) < 0.5)
                return;

            double oldTop = Top;
            double islandBottom = Top + Height - IslandContainer.Margin.Bottom;

            tallWindow = tall;
            Height = wanted;

            if (!atBottom)
                return;

            double gap = EdgeGap(Math.Max(1, height.Value)) + dragLift.Value;
            IslandContainer.Margin = new Thickness(IslandContainer.Margin.Left, 0, 0, gap);

            double top = islandBottom + gap - Height;

            if (tall && taskbarBand is { } band)
            {
                dockArrive.Snap(top - TaskbarWindowTop(band));
                dockArrive.Target = 0;
            }

            dockY.Snap(top);
            grabY -= top - oldTop;
            Top = top;
        }

        /// <summary>
        /// The gap between the island and the window's edge it sits against. Sunk into the taskbar,
        /// a small island is centred in the strip.
        /// </summary>
        double EdgeGap(double islandHeight)
        {
            // Also while it's undocked or carried off, until the spring has let it go, so leaving the
            // taskbar flows the same way as coming out of it
            if (!atBottom || taskbarBand is not { } band)
                return IslandTop;

            double level = Math.Clamp(mountLevel.Value, 0, 1);
            double centred = Math.Max(3, (band.Bottom - band.Top - islandHeight) / 2);

            if (!tallWindow)
                return IslandTop + (centred - IslandTop) * level;

            // The window reaches the taskbar's bottom: opened, the island sits just above the taskbar
            double above = band.Bottom - Math.Min(SystemParameters.WorkArea.Bottom, band.Top) + IslandTop;
            return above + (centred - above) * level;
        }

        /// <summary>
        /// Points the drain-out spring at where the island belongs for what it shows now: it flows up
        /// out of the taskbar as it opens, and pours back in as it closes, shrinking on the way, so the
        /// small look is never left waiting above the taskbar.
        /// </summary>
        void SyncMount()
        {
            if (Settings.IslandEdge != IslandEdge.Taskbar)
            {
                sliverLevel.Target = 0;
                return;
            }

            taskbarBand = ReadTaskbarBand();
            bool rests = RestsInTaskbar(height.Target);

            if (Choreographed)
            {
                if (rests && mountLevel.Target < 0.5)
                    mountLevel.Configure(0.42, 1);
                else if (!rests && mountLevel.Target > 0.5)
                    mountLevel.Configure(0.34, 0.84);

                dockBlend.Configure(0.24, 1);
            }
            else
            {
                mountLevel.Configure(0.45, 1);
                dockBlend.Configure(0.35, 1);
            }

            mountLevel.Target = rests ? 1 : 0;
            sliverLevel.Target = rests && TaskbarHidden && surface != Surface.Hidden ? 1 : 0;

            // Music starting or stopping changes the width: kept to the same edge
            if (rests && surface is Surface.DockedMusic or Surface.DockedIdle && placedForWidth != RestingWidth)
            {
                placedForWidth = RestingWidth;
                PlaceDocked();
            }

            EnsureRendering();
        }

        /// <summary>
        /// Where the island's middle goes along the taskbar: where it was put, or if that covers the
        /// taskbar's buttons, the nearest gap between them that it fits in.
        /// </summary>
        internal static double AvoidSpans(double x, double width, IReadOnlyList<(double Left, double Right)> taken,
            double screenWidth, double pad = 6) =>
            FitIn(x, width, taken, screenWidth, pad) ?? x;

        /// <summary>The same, but null when there's no gap it fits in at all.</summary>
        internal static double? FitIn(double x, double width, IReadOnlyList<(double Left, double Right)> taken,
            double screenWidth, double pad = 6)
        {
            if (taken.Count == 0)
                return x;

            double best = x;
            double bestDistance = double.MaxValue;
            double start = DockInset;

            void Consider(double from, double to)
            {
                if (to - from < width)
                    return;

                double centre = Math.Clamp(x, from + width / 2, to - width / 2);
                double distance = Math.Abs(centre - x);

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = centre;
                }
            }

            foreach (var span in taken.OrderBy(s => s.Left))
            {
                Consider(start, span.Left - pad);
                start = Math.Max(start, span.Right + pad);
            }

            Consider(start, screenWidth - DockInset);

            return bestDistance == double.MaxValue ? null : best;
        }

        /// <summary>
        /// The widest of the given sizes that fits somewhere near x, and where: its index in the list, or
        /// the list's length when not even the smallest fits.
        /// </summary>
        internal static (double X, int Fit) FitDocked(double x, IReadOnlyList<double> widths,
            IReadOnlyList<(double Left, double Right)> taken, double screenWidth)
        {
            for (int i = 0; i < widths.Count; i++)
                if (FitIn(x, widths[i], taken, screenWidth) is double placed)
                    return (placed, i);

            return (x, widths.Count);
        }

        /// <summary>
        /// How wide the free stretch of taskbar around x is, between the buttons either side (0 when x is
        /// on a button). Everything resting in the taskbar has to fit in it.
        /// </summary>
        internal static double FreeWidthAt(double x, IReadOnlyList<(double Left, double Right)> taken,
            double screenWidth, double pad = 6)
        {
            double from = DockInset, to = screenWidth - DockInset;

            foreach (var span in taken)
            {
                if (span.Right + pad <= x)
                    from = Math.Max(from, span.Right + pad);
                else if (span.Left - pad >= x)
                    to = Math.Min(to, span.Left - pad);
                else
                    return 0;
            }

            return Math.Max(0, to - from);
        }

        // ================= Room in the taskbar =================

        // How much of the button there's room for. Never over the taskbar's own buttons: in a narrower
        // gap it drops the title, then shows only the cover, and with no room at all it sits just
        // above the taskbar instead.
        enum DockFit { Full, Small, Icon, None }

        DockFit dockFit = DockFit.Full;

        // The music button's width at each: everything; the cover, bars (or controls) and ^; the cover
        static readonly double[] DockedMusicWidths = { 248, 140, 40 };

        void SetDockFit(DockFit fit)
        {
            if (fit == dockFit)
                return;

            dockFit = fit;
            Log.Info(fit == DockFit.None
                ? "No room between the taskbar's buttons: the island sits just above the taskbar"
                : $"Room in the taskbar for the {fit.ToString().ToLowerInvariant()} button");

            ApplyDockFit();

            // A docked look takes its new width at once; with no room at all it takes the island's usual looks
            if (surface is Surface.DockedMusic or Surface.DockedIdle && fit != DockFit.None)
            {
                width.Target = surface == Surface.DockedIdle ? DockedSize(idle: true).Width : DockedSize().Width;
                EnsureRendering();
            }

            UpdateClock();
            UpdateSurface();
            SyncMount();
        }

        // The music button's parts for the room there is
        void ApplyDockFit()
        {
            DockedMusic.Width = DockedSize().Width;
            DockedText.Visibility = dockFit == DockFit.Full ? Visibility.Visible : Visibility.Collapsed;
            DockedRight.Visibility = dockFit is DockFit.Full or DockFit.Small ? Visibility.Visible : Visibility.Collapsed;
            DockedExpand.Visibility = dockFit is DockFit.Full or DockFit.Small ? Visibility.Visible : Visibility.Collapsed;
        }

        // Where it sits is worked out once, for the widest look (the music button). Pushed up against the
        // taskbar's buttons, the edge facing them stays put and a smaller look (the idle sky) keeps to that
        // edge too; otherwise every look shares the middle. So it never drifts as the taskbar comes and
        // goes, and only grows from that edge as a song starts.
        double DockedX(double x)
        {
            var (middle, fit) = FitDocked(x, DockedMusicWidths, taskbarSpans, ScreenWidth);
            SetDockFit((DockFit)fit);

            // No room anywhere: it stays where it was put, above the taskbar
            if (dockFit == DockFit.None)
                return x;

            double wide = DockedSize().Width;
            double w = RestingWidth;

            if (w >= wide || Math.Abs(middle - x) < 0.5)
                return middle;

            return middle < x ? middle + (wide - w) / 2 : middle - (wide - w) / 2;
        }

        // The width of the look resting in the taskbar now
        double RestingWidth => surface == Surface.DockedIdle ? DockedSize(idle: true).Width : DockedSize().Width;
        double placedForWidth = double.NaN;

        // Moves the docked island to its gap, when the buttons or its own size have changed
        void PlaceDocked()
        {
            if (!InTaskbarMode)
                return;

            double x = DockedX(Math.Clamp(Settings.IslandX, 0, 1) * ScreenWidth);

            if (Math.Abs(dockX.Target - x) < 0.5)
                return;

            dockX.Configure(0.55, 0.84);
            dockX.Target = x;
            EnsureRendering();
        }

        // The taskbar's buttons are read in the background (it takes a tenth of a second), and only when
        // they may have changed: the taskbar coming up, apps switching (opening and closing), and now and
        // then as a safety net. Hidden, they can't be read, so the last good reading stays.
        bool readingSpans;
        DateTime spansReadAt = DateTime.MinValue;
        static readonly TimeSpan SpansSafetyCheck = TimeSpan.FromSeconds(15);
        readonly DispatcherTimer spansSoon = new();

        /// <summary>Reads the buttons again shortly, once things have settled (a slide or an app opening).</summary>
        void RefreshTaskbarSpansSoon(int afterMs = 450)
        {
            if (Settings.IslandEdge != IslandEdge.Taskbar)
                return;

            if (!spansSoonSet)
            {
                spansSoonSet = true;
                spansSoon.Tick += (s, e) =>
                {
                    spansSoon.Stop();
                    RefreshTaskbarSpans();
                };
            }

            spansSoon.Stop();
            spansSoon.Interval = TimeSpan.FromMilliseconds(afterMs);
            spansSoon.Start();
        }

        bool spansSoonSet;
        bool spansHiddenSeen;

        /// <summary>
        /// Reads where the taskbar's buttons are, and moves the island clear of them if they've moved.
        /// In the background, except the very first time, when there's nothing to go on yet.
        /// </summary>
        void RefreshTaskbarSpans()
        {
            var taskbar = FindWindow("Shell_TrayWnd", null);

            if (taskbar == IntPtr.Zero || readingSpans ||
                PresentationSource.FromVisual(this) is not { CompositionTarget: { } target })
                return;

            var toUnits = target.TransformFromDevice;

            if (taskbarSpans.Count == 0)
            {
                ApplyTaskbarSpans(TaskbarButtons.Read(taskbar), toUnits);
                return;
            }

            readingSpans = true;

            Task.Run(() => TaskbarButtons.Read(taskbar)).ContinueWith(read =>
            {
                readingSpans = false;

                if (read.IsCompletedSuccessfully)
                    ApplyTaskbarSpans(read.Result, toUnits);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void ApplyTaskbarSpans(List<(double Left, double Right)>? read, Matrix toUnits)
        {
            // Couldn't be seen (slid away): the last good reading stays
            if (read == null)
                return;

            spansReadAt = DateTime.UtcNow;

            var spans = read
                .Select(s => (Left: toUnits.Transform(new Point(s.Left, 0)).X, Right: toUnits.Transform(new Point(s.Right, 0)).X))
                .OrderBy(s => s.Left)
                .ToList();

            bool changed = spans.Count != taskbarSpans.Count ||
                spans.Zip(taskbarSpans).Any(p => Math.Abs(p.First.Left - p.Second.Left) > 1 || Math.Abs(p.First.Right - p.Second.Right) > 1);

            if (!changed)
                return;

            taskbarSpans = spans;
            PlaceDocked();
        }

        // The line along the screen's bottom while the taskbar is hidden, under the island's middle
        void PlaceSliver(double w)
        {
            if (!InTaskbarMode)
                sliverLevel.Target = 0;

            double level = Math.Clamp(sliverLevel.Value, 0, 1);

            if (level < 0.01 || taskbarBand is not { } band)
            {
                if (TaskbarSliver.Visibility != Visibility.Collapsed)
                    TaskbarSliver.Visibility = Visibility.Collapsed;

                return;
            }

            double sliverWidth = Math.Clamp(w * 0.55, 28, 110);

            TaskbarSliver.Visibility = Visibility.Visible;
            TaskbarSliver.Width = sliverWidth;
            TaskbarSliver.Opacity = level;

            // Just above the couple of pixels the taskbar leaves, rising into place as it shows
            TaskbarSliver.Margin = new Thickness(
                islandLeft + w / 2 - sliverWidth / 2,
                band.Top - Top - TaskbarSliver.Height + 3 * (1 - level), 0, 0);
        }

        // While carried: over the taskbar, the outline shows the gap it will drop into
        void StepDropSlot(Point pointer)
        {
            if (dragBand is { } band && EdgeFor(pointer.Y, ScreenHeight, band.Top) == IslandEdge.Taskbar)
            {
                double x = AvoidSpans(dockX.Target, DockedSize().Width, taskbarSpans, ScreenWidth);

                if (slotLevel.Value < 0.05)
                    slotX.Snap(x);
                else
                    slotX.Target = x;

                slotLevel.Target = 1;
            }
            else
            {
                slotLevel.Target = 0;
            }
        }

        void PlaceDropSlot()
        {
            if (backdrop == null)
                return;

            if (dragBand is not { } band || slotLevel.Value < 0.01)
            {
                backdrop.SetSlot(null, 0, 0);
                return;
            }

            double strip = band.Bottom - band.Top;
            // The size it will take there: its taskbar look, not the one it's carried as
            var (w, h) = DockedSize();
            double left = IslandLeftFor(slotX.Value, w, ScreenWidth);

            backdrop.SetSlot(new Rect(left, band.Top + (strip - h) / 2, w, h), Math.Clamp(slotLevel.Value, 0, 1), DockedRadius);
        }

        // The taskbar has moved: the island goes to just above it, rides along inside it, or goes
        // back to the edge once it's gone
        void OnTaskbarMoved()
        {
            if (dragging || Settings.IslandEdge == IslandEdge.Top)
                return;

            if (Settings.IslandEdge == IslandEdge.Taskbar)
            {
                SyncMount();
                SyncWindowFit();

                if (InTaskbarMode && taskbarBand is { } band)
                {
                    // Coming up: its buttons are looked at once the slide has settled
                    if (TaskbarHidden != spansHiddenSeen)
                    {
                        spansHiddenSeen = TaskbarHidden;

                        if (!TaskbarHidden)
                            RefreshTaskbarSpansSoon();
                    }

                    // Fixed to the taskbar: moved with every step of its slide, not after it
                    double top = TaskbarWindowTop(band) + dockArrive.Value;
                    dockY.Snap(top);
                    Top = top;
                    KeepOnTop();
                    return;
                }
            }

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

        // Listens for the taskbar sliding (at the bottom edge or in the taskbar) and for notification
        // pop-ups (while they're being cleared), and for nothing otherwise
        void SyncWinEvents()
        {
            bool taskbar = Settings.IslandEdge != IslandEdge.Top;
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
            return ReadTaskbarBand() is { } band ? Math.Min(bottom, band.Top) : bottom;
        }

        /// <summary>The taskbar's strip on screen, top and bottom, while it runs along the bottom; null otherwise.</summary>
        (double Top, double Bottom)? ReadTaskbarBand()
        {
            var taskbar = FindWindow("Shell_TrayWnd", null);

            if (taskbar == IntPtr.Zero || !GetWindowRect(taskbar, out var r) || PresentationSource.FromVisual(this) is not { CompositionTarget: { } target })
                return null;

            var top = target.TransformFromDevice.Transform(new Point(r.Left, r.Top));
            var bottomRight = target.TransformFromDevice.Transform(new Point(r.Right, r.Bottom));

            // Along the bottom: wider than it is tall, and in the lower half of the screen
            bool alongBottom = bottomRight.X - top.X > (bottomRight.Y - top.Y) * 2 && top.Y > ScreenHeight / 2;

            return alongBottom ? (top.Y, bottomRight.Y) : null;
        }

        void FollowTaskbar()
        {
            if (Settings.IslandEdge == IslandEdge.Top)
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

        /// <summary>
        /// Where a point on screen puts the island: in the taskbar when it's over the taskbar's strip,
        /// else the nearer edge, the top half or the bottom half.
        /// </summary>
        internal static IslandEdge EdgeFor(double y, double screenHeight, double? taskbarTop = null) =>
            taskbarTop is double top && y >= top ? IslandEdge.Taskbar
            : y > screenHeight / 2 ? IslandEdge.Bottom
            : IslandEdge.Top;

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
                ? new Thickness(IslandContainer.Margin.Left, 0, 0, EdgeGap(h))
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
            // Over the island's content, so nothing inside paints over the card's edge
            IslandContainer.Children.Add(flyoutEdge);

            // A taskbar sliding up, or back down, moves the island with it
            taskbarTimer.Tick += (s, e) =>
            {
                // Explorer restarting gives a new taskbar window, which the listening has to be pointed at again
                if (!winEvents.TaskbarStillWatched)
                    SyncWinEvents();

                OnTaskbarMoved();

                // A safety check on the taskbar's buttons, now and then, while they can be seen
                if (Settings.IslandEdge == IslandEdge.Taskbar && !dragging && !TaskbarHidden &&
                    DateTime.UtcNow - spansReadAt > SpansSafetyCheck)
                    RefreshTaskbarSpans();
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

            LayOutFrom(edge != IslandEdge.Top);
            FollowTaskbar();
            taskbarBand = edge == IslandEdge.Top ? null : ReadTaskbarBand();
            SyncWindowFit();

            if (InTaskbarMode && taskbarBand is not null)
            {
                // Clear of the taskbar's buttons, and looked at afresh
                RefreshTaskbarSpans();
                x = DockedX(x);

                // Straight in at start; otherwise it flows in from wherever it was
                double level = RestsInTaskbar(height.Target) ? 1 : 0;

                if (snap)
                    mountLevel.Snap(level);
                else
                    mountLevel.Target = level;

                // Flows in from wherever it was: the stretch of the window above set that up
                if (snap)
                    dockArrive.Snap(0);
            }
            else if (edge == IslandEdge.Top || snap)
            {
                mountLevel.Snap(0);
            }
            else
            {
                // Undocked: it rises out of the taskbar to just above it
                mountLevel.Target = 0;
            }

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

            SyncWindowFit();

            // In the taskbar, the window's place on screen comes from the taskbar and how far the
            // island has drained out of it; kept in dockY too, so leaving starts from here
            if (InTaskbarMode && taskbarBand is { } band)
                dockY.Snap(TaskbarWindowTop(band) + dockArrive.Value);

            if (Math.Abs(Top - dockY.Value) > 0.01)
                Top = dockY.Value;

            islandLeft = left - windowLeft;
            islandOrigin = Math.Clamp((x - left) / w, 0, 1);

            // Lifted away from whichever edge it sits on
            var margin = atBottom
                ? new Thickness(islandLeft, 0, 0, EdgeGap(height.Value) + dragLift.Value)
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
            PlaceDropSlot();
            PlaceSliver(w);
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
            StepDropSlot(pointer);

            // Up and down it follows the pointer, laid out from whichever edge is nearer, so it
            // can be carried all the way to either; kept wholly on screen
            LayOutFrom(EdgeFor(pointer.Y, ScreenHeight) != IslandEdge.Top);

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

            // Picked up out of the taskbar, it flows out of the strip as it lifts
            mountLevel.Target = 0;

            // Where it could be docked, for the outline of where it would land
            dragBand = ReadTaskbarBand();

            if (dragBand != null)
                RefreshTaskbarSpans();

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
            slotLevel.Target = 0;

            if (DragFlair)
                stretch.Velocity += 1.2;

            // It stays where it was let go along the edge, without carrying on past it
            double x = Math.Clamp(dockX.Target, 0, ScreenWidth);
            dockX.Velocity *= 0.15;
            dockY.Velocity *= 0.3;

            // Up or down, it goes to the nearer edge, never left in between; let go over the
            // taskbar, it goes into it
            Settings.IslandEdge = EdgeFor(PointerPosition().Y, ScreenHeight, ReadTaskbarBand()?.Top);

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
