using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Islet
{
    /// <summary>What's playing, as the takeover needs it.</summary>
    public sealed record NowPlaying(TrackInfo Track, BitmapSource? Art, IReadOnlyList<LyricsService.Line>? Lyrics);

    /// <summary>
    /// While you're away and music plays, the island slowly grows from where it sits until it fills
    /// about 60% of the screen, centred. Waves roll in from outside and break on its edge to the
    /// music's beat, and inside it the cover, the song and its lyrics play, the line being sung
    /// scrolled to the middle. Any key, mouse movement or click shrinks it back into the island.
    ///
    /// It never takes the keyboard, so a key pressed to wake it doesn't also go somewhere
    /// unexpected; waking is noticed through Windows' own record of the last input.
    /// </summary>
    public sealed class NowPlayingTakeover : Window
    {
        const double OpenSeconds = 2.4;
        const double CloseSeconds = 0.95;
        const double TargetShare = 0.6;

        readonly Func<NowPlaying?> now;
        readonly Func<float> level;
        readonly Rect from;
        readonly Rect to;
        readonly double screenWidth;
        readonly double screenHeight;
        uint openedInput = LastInput();
        readonly DateTime openedAt = DateTime.Now;
        readonly TimeSpan grace;
        readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromMilliseconds(250) };

        readonly Grid root = new();
        readonly Border dim = new() { Background = Brushes.Black, Opacity = 0 };
        readonly WaveEdge waves = new() { IsHitTestVisible = false };
        readonly Canvas content = new() { IsHitTestVisible = false, Opacity = 0 };

        // Inside
        readonly Image backdrop = new() { Stretch = Stretch.UniformToFill, Opacity = 0.26 };
        readonly Border coverFrame = new();
        readonly Image cover = new() { Stretch = Stretch.UniformToFill };
        readonly TextBlock title = new();
        readonly TextBlock artist = new();
        readonly Border barTrack = new();
        readonly Border barFill = new();
        readonly TextBlock elapsed = new();
        readonly TextBlock remaining = new();
        readonly TextBlock paused = new();
        readonly TextBlock clock = new();
        readonly TextBlock bigClock = new();
        readonly TextBlock bigDate = new();
        readonly Canvas port = new() { ClipToBounds = true };
        readonly Canvas lines = new();
        readonly TranslateTransform scrollShift = new();

        // The lyrics as laid out: where each line sits, how tall, and how lit it is now
        readonly List<TextBlock> blocks = new();
        double[] tops = Array.Empty<double>();
        double[] heights = Array.Empty<double>();
        double[] glow = Array.Empty<double>();
        IReadOnlyList<LyricsService.Line>? laidOut;
        bool laidOutOnce;
        int shownFirst = 1;
        int shownLast = 0;
        double scroll;
        double scrollTarget;

        // Sizes, worked out from the card
        double cardWidth;
        double cardHeight;
        double unit;
        double portWidth;
        double portHeight;
        double barWidth;

        // State
        NowPlaying? current;
        string shownSong = "";
        double progress;
        bool closing;
        bool closed;
        bool openClipSet;
        double smoothLevel = 0.35;
        double clockTime;
        TimeSpan lastFrame = TimeSpan.Zero;
        Color accent = Color.FromRgb(150, 130, 255);
        Color accentTarget = Color.FromRgb(150, 130, 255);
        Color tint = Color.FromRgb(24, 18, 48);
        Color tintTarget = Color.FromRgb(24, 18, 48);

        public event Action? Dismissed;

        /// <param name="now">What's playing; null once nothing is.</param>
        /// <param name="level">How loud the music is, 0 to 1, or less than 0 when there's no audio to follow.</param>
        /// <param name="from">Where the island is on screen: the shape grows out of it.</param>
        /// <param name="grace">How long input is ignored at first: opened by hand, a nudge of the mouse isn't "back".</param>
        public NowPlayingTakeover(Func<NowPlaying?> now, Func<float> level, Rect from, TimeSpan grace = default)
        {
            this.grace = grace;
            this.now = now;
            this.level = level;
            this.from = from;

            screenWidth = SystemParameters.PrimaryScreenWidth;
            screenHeight = SystemParameters.PrimaryScreenHeight;

            double w = Math.Min(screenWidth, Math.Max(TargetShare * screenWidth, 720));
            double h = Math.Min(screenHeight, Math.Max(TargetShare * screenHeight, 420));
            to = new Rect((screenWidth - w) / 2, (screenHeight - h) / 2, w, h);

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Title = "Islet now playing";
            Cursor = Cursors.None;
            Left = 0;
            Top = 0;
            Width = screenWidth;
            Height = screenHeight;

            Build();
            Content = root;

            // A click lands here rather than in whatever is underneath
            root.MouseDown += (s, e) =>
            {
                if (DateTime.Now - openedAt >= grace)
                    Dismiss();
            };

            tick.Tick += (s, e) => Tick();
            Loaded += (s, e) =>
            {
                Refresh();
                Step(0);
                CompositionTarget.Rendering += OnFrame;
                tick.Start();
            };
            Closed += (s, e) => CompositionTarget.Rendering -= OnFrame;
        }

        // ================= The pieces =================

        void Build()
        {
            cardWidth = to.Width;
            cardHeight = to.Height;
            unit = cardHeight / 520;

            root.Width = screenWidth;
            root.Height = screenHeight;
            root.Background = Brushes.Transparent;

            dim.IsHitTestVisible = false;
            root.Children.Add(dim);
            root.Children.Add(waves);

            content.Width = cardWidth;
            content.Height = cardHeight;
            content.HorizontalAlignment = HorizontalAlignment.Left;
            content.VerticalAlignment = VerticalAlignment.Top;
            content.Margin = new Thickness(to.Left, to.Top, 0, 0);

            // Drawn once and kept: only the lyrics move, so only they cause it to be drawn again
            content.CacheMode = new BitmapCache { RenderAtScale = 1 };
            root.Children.Add(content);

            double pad = 34 * unit;
            double side = Math.Min(cardHeight * 0.44, cardWidth * 0.27);
            double columnLeft = pad;

            RenderOptions.SetBitmapScalingMode(backdrop, BitmapScalingMode.HighQuality);
            backdrop.Width = cardWidth;
            backdrop.Height = cardHeight;
            content.Children.Add(backdrop);

            // Cover
            coverFrame.Width = coverFrame.Height = side;
            coverFrame.CornerRadius = new CornerRadius(16 * unit);
            coverFrame.Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
            coverFrame.Child = cover;
            coverFrame.ClipToBounds = true;
            coverFrame.Effect = new DropShadowEffect { BlurRadius = 36, ShadowDepth = 10, Opacity = 0.55, Color = Colors.Black };
            RenderOptions.SetBitmapScalingMode(cover, BitmapScalingMode.HighQuality);
            double coverTop = pad + 4 * unit;
            Canvas.SetLeft(coverFrame, columnLeft);
            Canvas.SetTop(coverFrame, coverTop);
            content.Children.Add(coverFrame);

            // Title and artist under it
            double textTop = coverTop + side + 20 * unit;
            Place(title, 0, 0, side, 25 * unit, FontWeights.SemiBold, 1, maxLines: 2, add: false);
            Place(artist, 0, 0, side, 17 * unit, FontWeights.Normal, 0.68, maxLines: 1, add: false);
            artist.Margin = new Thickness(0, 4 * unit, 0, 0);

            var words = new StackPanel { Width = side };
            words.Children.Add(title);
            words.Children.Add(artist);
            Canvas.SetLeft(words, columnLeft);
            Canvas.SetTop(words, textTop);
            content.Children.Add(words);

            // Progress at the foot of the column
            barWidth = side;
            double barTop = cardHeight - pad - 34 * unit;
            barTrack.Width = barWidth;
            barTrack.Height = 4 * unit;
            barTrack.CornerRadius = new CornerRadius(2 * unit);
            barTrack.Background = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));
            Canvas.SetLeft(barTrack, columnLeft);
            Canvas.SetTop(barTrack, barTop);
            content.Children.Add(barTrack);

            barFill.Height = 4 * unit;
            barFill.CornerRadius = new CornerRadius(2 * unit);
            barFill.Background = Brushes.White;
            barFill.Width = 0;
            Canvas.SetLeft(barFill, columnLeft);
            Canvas.SetTop(barFill, barTop);
            content.Children.Add(barFill);

            Place(elapsed, columnLeft, barTop + 12 * unit, side / 2, 12 * unit, FontWeights.Normal, 0.6, maxLines: 1);
            Place(remaining, columnLeft + side / 2, barTop + 12 * unit, side / 2, 12 * unit, FontWeights.Normal, 0.6, maxLines: 1);
            remaining.TextAlignment = TextAlignment.Right;
            Place(paused, columnLeft, barTop - 24 * unit, side, 12 * unit, FontWeights.SemiBold, 0.55, maxLines: 1);

            // The clock, small in the corner
            Place(clock, cardWidth - pad - 90 * unit, pad - 8 * unit, 90 * unit, 16 * unit, FontWeights.Light, 0.8, maxLines: 1);
            clock.TextAlignment = TextAlignment.Right;

            // Lyrics, to the right of the column; or, with none, a large clock
            double portLeft = columnLeft + side + pad;
            portWidth = cardWidth - portLeft - pad;
            portHeight = cardHeight - 2 * pad;

            port.Width = portWidth;
            port.Height = portHeight;
            port.OpacityMask = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromArgb(0, 0, 0, 0), 0),
                    new(Color.FromArgb(255, 0, 0, 0), 0.16),
                    new(Color.FromArgb(255, 0, 0, 0), 0.84),
                    new(Color.FromArgb(0, 0, 0, 0), 1)
                }, new Point(0, 0), new Point(0, 1));
            lines.RenderTransform = scrollShift;
            port.Children.Add(lines);
            Canvas.SetLeft(port, portLeft);
            Canvas.SetTop(port, pad);
            content.Children.Add(port);

            Place(bigClock, portLeft, cardHeight / 2 - 80 * unit, portWidth, 78 * unit, FontWeights.Light, 1, maxLines: 1);
            Place(bigDate, portLeft, cardHeight / 2 + 12 * unit, portWidth, 20 * unit, FontWeights.Normal, 0.65, maxLines: 1);
            bigClock.Visibility = bigDate.Visibility = Visibility.Collapsed;
        }

        void Place(TextBlock text, double left, double top, double width, double size, FontWeight weight, double opacity, int maxLines, bool add = true)
        {
            text.Width = width;
            text.FontSize = size;
            text.FontWeight = weight;
            text.FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
            text.Foreground = new SolidColorBrush(Color.FromArgb((byte)(255 * opacity), 255, 255, 255));
            text.TextWrapping = maxLines > 1 ? TextWrapping.Wrap : TextWrapping.NoWrap;
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            text.MaxHeight = size * 1.45 * maxLines;
            Canvas.SetLeft(text, left);
            Canvas.SetTop(text, top);

            if (add)
                content.Children.Add(text);
        }

        // ================= Frames =================

        void OnFrame(object? sender, EventArgs e)
        {
            var time = ((RenderingEventArgs)e).RenderingTime;

            if (time == lastFrame)
                return;

            double dt = lastFrame == TimeSpan.Zero ? 1.0 / 60 : Math.Clamp((time - lastFrame).TotalSeconds, 0, 0.05);

            // The waves are slow, so once it's open 30 frames a second is plenty (and lighter modes may ask for fewer)
            bool open = progress >= 1 && !closing;
            int fps = PerformanceProfile.For(AppSettings.Current.Performance).ContinuousFps;

            if (open)
                fps = fps > 0 ? Math.Min(fps, 30) : 30;

            if (open && fps > 0 && lastFrame != TimeSpan.Zero && (time - lastFrame).TotalSeconds < 1.0 / fps - 0.004)
                return;

            lastFrame = time;
            Step(dt);
        }

        static double Smooth(double from, double to, double x)
        {
            double t = Math.Clamp((x - from) / (to - from), 0, 1);
            return t * t * (3 - 2 * t);
        }

        static double EaseInOut(double p) => p < 0.5 ? 4 * p * p * p : 1 - Math.Pow(-2 * p + 2, 3) / 2;

        static double Lerp(double a, double b, double t) => a + (b - a) * t;

        static Color Lerp(Color a, Color b, double t) =>
            Color.FromRgb((byte)Lerp(a.R, b.R, t), (byte)Lerp(a.G, b.G, t), (byte)Lerp(a.B, b.B, t));

        /// <summary>One step of the animation, <paramref name="dt"/> seconds long.</summary>
        public void Step(double dt)
        {
            if (closed)
                return;

            progress = Math.Clamp(progress + (closing ? -dt / CloseSeconds : dt / OpenSeconds), 0, 1);
            clockTime += dt;

            double e = EaseInOut(progress);
            var rect = new Rect(Lerp(from.X, to.X, e), Lerp(from.Y, to.Y, e), Lerp(from.Width, to.Width, e), Lerp(from.Height, to.Height, e));
            double radius = Lerp(Math.Min(19, from.Height / 2), 64 * unit, e);

            // The music's loudness, eased so the waves swell rather than twitch
            double heard = level();
            double want = heard >= 0 ? heard : 0.4 + 0.16 * Math.Sin(clockTime * 1.7) + 0.08 * Math.Sin(clockTime * 4.3);

            if (current?.Track.Playing == false)
                want = 0.05;

            smoothLevel += (want - smoothLevel) * (1 - Math.Exp(-dt * (want > smoothLevel ? 9 : 3.5)));

            // The cover's colours, taken up gradually after a change of song
            accent = Lerp(accent, accentTarget, 1 - Math.Exp(-dt * 3));
            tint = Lerp(tint, tintTarget, 1 - Math.Exp(-dt * 3));

            dim.Opacity = 0.62 * Smooth(0, 0.75, e);

            waves.Shape = rect;
            waves.Radius = radius;
            waves.Accent = accent;
            waves.Tint = tint;
            waves.Level = smoothLevel;
            waves.Time = clockTime;
            waves.Presence = Smooth(0.45, 1, e);
            waves.Waves = PerformanceProfile.For(AppSettings.Current.Performance).Glow ? 3 : 2;
            waves.InvalidateVisual();

            // The inside is revealed by the shape, as the island's own views are
            content.Opacity = Smooth(0.62, 0.96, e);

            if (progress >= 1 && !closing)
            {
                // Fully open the shape stops changing, so its clip is set once and left
                if (!openClipSet)
                {
                    openClipSet = true;
                    content.Clip = new RectangleGeometry(new Rect(0, 0, to.Width, to.Height), radius, radius);
                }
            }
            else
            {
                openClipSet = false;
                content.Clip = new RectangleGeometry(new Rect(rect.X - to.X, rect.Y - to.Y, rect.Width, rect.Height), radius, radius);
            }

            StepInside(dt);

            if (closing && progress <= 0)
                Finish();
        }

        void StepInside(double dt)
        {
            if (current == null)
                return;

            var track = current.Track;
            var position = track.PositionAt(DateTimeOffset.Now);
            var duration = track.Duration;

            if (duration > TimeSpan.Zero)
            {
                double fraction = Math.Clamp(position / duration, 0, 1);
                double fill = barWidth * fraction;

                if (Math.Abs(barFill.Width - fill) > 0.5)
                    barFill.Width = fill;

                string passed = Format(position);

                if (elapsed.Text != passed)
                {
                    elapsed.Text = passed;
                    remaining.Text = "-" + Format(duration > position ? duration - position : TimeSpan.Zero);
                }
            }

            string pausedText = track.Playing ? "" : "PAUSED";

            if (paused.Text != pausedText)
                paused.Text = pausedText;

            if (laidOut == null || laidOut.Count == 0)
                return;

            // The line being sung, scrolled to a little above the middle, the ones round it dimmer
            int index = LyricsService.IndexAt(laidOut, position + TimeSpan.FromMilliseconds(200));
            int focus = Math.Max(index, 0);

            scrollTarget = portHeight * 0.4 - (tops[focus] + heights[focus] / 2);
            scroll += (scrollTarget - scroll) * (1 - Math.Exp(-dt * 5.5));

            if (Math.Abs(scrollShift.Y - scroll) > 0.05)
                scrollShift.Y = scroll;

            int first = Math.Max(0, focus - 6);
            int last = Math.Min(laidOut.Count - 1, focus + 6);

            for (int i = shownFirst; i <= shownLast; i++)
                if (i < first || i > last)
                    blocks[i].Visibility = Visibility.Hidden;

            shownFirst = first;
            shownLast = last;

            for (int i = first; i <= last; i++)
            {
                int distance = index < 0 ? i + 1 : Math.Abs(i - index);
                double lit = distance == 0 ? 1 : distance == 1 ? 0.5 : distance == 2 ? 0.3 : 0.16;

                glow[i] += (lit - glow[i]) * (1 - Math.Exp(-dt * 9));

                var block = blocks[i];

                if (block.Visibility != Visibility.Visible)
                    block.Visibility = Visibility.Visible;

                if (Math.Abs(block.Opacity - glow[i]) > 0.004)
                {
                    block.Opacity = glow[i];

                    double size = 0.88 + 0.12 * Math.Clamp((glow[i] - 0.16) / 0.84, 0, 1);
                    var scale = (ScaleTransform)block.RenderTransform;
                    scale.ScaleX = scale.ScaleY = size;
                }
            }
        }

        static string Format(TimeSpan time) =>
            time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : $"{(int)time.TotalMinutes}:{time.Seconds:00}";

        // ================= What's playing =================

        void Tick()
        {
            // Any key or mouse movement since it opened, anywhere
            if (LastInput() != openedInput)
            {
                if (DateTime.Now - openedAt < grace)
                    openedInput = LastInput();
                else
                {
                    Dismiss();
                    return;
                }
            }

            Refresh();
        }

        void Refresh()
        {
            var playing = now();

            if (playing == null)
            {
                Dismiss();
                return;
            }

            current = playing;

            var track = playing.Track;
            var time = DateTime.Now;
            string text = time.ToString(AppSettings.Current.Use24HourClock ? "HH:mm" : "h:mm");

            clock.Text = text;
            bigClock.Text = text;
            bigDate.Text = DateText.IslandLong(time);

            string song = $"{track.Title}\n{track.Artist}";

            if (song != shownSong)
            {
                bool first = shownSong.Length == 0;
                shownSong = song;

                title.Text = track.Title;
                artist.Text = track.Artist;
                cover.Source = playing.Art;
                backdrop.Source = playing.Art != null ? MainWindow.BlurredCover(playing.Art, 240) : null;

                var (primary, _) = AlbumPalette.From(playing.Art);
                accentTarget = AlbumPalette.Tone(primary, 0.62, 0.5, 0.95);
                tintTarget = AlbumPalette.Tone(primary, 0.16, 0.35, 0.7);

                if (first)
                {
                    accent = accentTarget;
                    tint = tintTarget;
                }

                if (!first)
                    FadeIn(coverFrame, title, artist);
            }

            // The lyrics are laid out when they arrive, and again if they change
            if (!laidOutOnce || !ReferenceEquals(laidOut, playing.Lyrics))
                LayOutLyrics(playing.Lyrics);
        }

        static void FadeIn(params UIElement[] parts)
        {
            foreach (var part in parts)
                part.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(520)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        void LayOutLyrics(IReadOnlyList<LyricsService.Line>? lyrics)
        {
            laidOut = lyrics;
            laidOutOnce = true;
            lines.Children.Clear();
            blocks.Clear();
            shownFirst = 1;
            shownLast = 0;
            scroll = 0;

            bool has = lyrics is { Count: > 0 };
            bigClock.Visibility = bigDate.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            clock.Visibility = has ? Visibility.Visible : Visibility.Collapsed;

            if (!has)
            {
                tops = heights = glow = Array.Empty<double>();
                return;
            }

            tops = new double[lyrics!.Count];
            heights = new double[lyrics.Count];
            glow = new double[lyrics.Count];

            double y = 0;
            double gap = 16 * unit;

            for (int i = 0; i < lyrics.Count; i++)
            {
                var block = new TextBlock
                {
                    Text = lyrics[i].Text.Length == 0 ? "♪" : lyrics[i].Text,
                    Width = portWidth,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = Math.Clamp(31 * unit, 20, 44),
                    FontWeight = FontWeights.SemiBold,
                    FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
                    Foreground = Brushes.White,
                    Opacity = 0,
                    Visibility = Visibility.Hidden,
                    RenderTransformOrigin = new Point(0, 0.5),
                    RenderTransform = new ScaleTransform(0.88, 0.88)
                };

                block.Measure(new Size(portWidth, double.PositiveInfinity));
                tops[i] = y;
                heights[i] = block.DesiredSize.Height;
                y += heights[i] + gap;

                Canvas.SetTop(block, tops[i]);
                lines.Children.Add(block);
                blocks.Add(block);
                glow[i] = 0.16;
            }

            // Starts already in place, rather than sliding in from the top
            scroll = portHeight * 0.4 - (tops[0] + heights[0] / 2);
            scrollShift.Y = scroll;
        }

        // ================= Opening and closing =================

        /// <summary>Lays the window out without showing it, for drawing it to a picture.</summary>
        public void PrepareLayout()
        {
            var size = new Size(screenWidth, screenHeight);
            root.Measure(size);
            root.Arrange(new Rect(size));
            Refresh();
            root.UpdateLayout();
        }

        /// <summary>The picture it would show now.</summary>
        public BitmapSource Snapshot()
        {
            var bitmap = new RenderTargetBitmap((int)screenWidth, (int)screenHeight, 96, 96, PixelFormats.Pbgra32);
            root.UpdateLayout();
            bitmap.Render(root);
            return bitmap;
        }

        /// <summary>Shrinks back into the island, then goes.</summary>
        public void Dismiss()
        {
            if (closing || closed)
                return;

            closing = true;
            tick.Stop();
            Dismissed?.Invoke();
        }

        void Finish()
        {
            if (closed)
                return;

            closed = true;
            CompositionTarget.Rendering -= OnFrame;

            try
            {
                Close();
            }
            catch (InvalidOperationException)
            {
                // Never shown, so nothing to close
            }
        }

        /// <summary>When the last key or mouse input happened, in Windows' milliseconds since startup.</summary>
        public static uint LastInput()
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            return GetLastInputInfo(ref info) ? info.dwTime : 0;
        }

        /// <summary>How long since you last touched the keyboard or mouse.</summary>
        public static TimeSpan IdleFor() =>
            TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - LastInput()));

        [StructLayout(LayoutKind.Sequential)]
        struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    }
}
