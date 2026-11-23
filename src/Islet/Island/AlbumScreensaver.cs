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
    /// <summary>What's playing, as the screensaver needs it.</summary>
    public sealed record NowPlaying(TrackInfo Track, BitmapSource? Art, IReadOnlyList<LyricsService.Line>? Lyrics);

    /// <summary>
    /// The island grown into the whole screen while you're away and music plays: the cover large
    /// over a blurred, slowly drifting copy of itself, the song, and its lyrics scrolling gently so
    /// the line being sung sits a little above the middle. Without lyrics, the clock takes their
    /// place. Any key, mouse movement or click brings you back.
    ///
    /// It never takes the keyboard, so a key pressed to wake it doesn't also go somewhere
    /// unexpected; waking is noticed through Windows' own record of the last input.
    /// </summary>
    public sealed class AlbumScreensaver : Window
    {
        const double OpenSeconds = 1.1;
        const double CloseSeconds = 0.4;
        const double SongFadeSeconds = 1.4;

        readonly Func<NowPlaying?> now;
        readonly Point origin;
        readonly double screenWidth;
        readonly double screenHeight;
        readonly double unit;
        uint openedInput = LastInput();
        readonly DateTime openedAt = DateTime.Now;
        readonly TimeSpan grace;
        readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromMilliseconds(250) };

        // The pieces
        readonly Grid stage = new() { Background = Brushes.Black };
        readonly ScaleTransform grow = new(0.92, 0.92);
        readonly Image backdrop = new() { Stretch = Stretch.UniformToFill };
        readonly Image backdropBefore = new() { Stretch = Stretch.UniformToFill, Opacity = 0 };
        readonly ScaleTransform drift = new(1.12, 1.12);
        readonly TranslateTransform driftShift = new();
        readonly SolidColorBrush tintBrush = new(Color.FromRgb(24, 18, 48));
        readonly Canvas content = new();
        readonly Canvas facade = new();
        readonly Border coverFrame = new();
        readonly Image cover = new() { Stretch = Stretch.UniformToFill };
        readonly DropShadowEffect coverGlow = new() { BlurRadius = 90, ShadowDepth = 0, Opacity = 0.4, Color = Color.FromRgb(150, 130, 255) };
        readonly TextBlock title = new();
        readonly TextBlock artist = new();
        readonly Border barTrack = new();
        readonly Border barFill = new();
        readonly SolidColorBrush barBrush = new(Colors.White);
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
        double scrollSpeed;
        double scrollTarget;
        double portHeight;
        double portWidth;
        double barWidth;

        // State
        string shownSong = "";
        double progress;
        double songFade;
        double time;
        bool closing;
        bool closed;
        bool settled;
        TimeSpan lastFrame = TimeSpan.Zero;
        Color tint = Color.FromRgb(24, 18, 48);
        Color tintTarget = Color.FromRgb(24, 18, 48);

        public event Action? Dismissed;

        /// <param name="now">What's playing; null once it should go away (nothing playing, a call, a game).</param>
        /// <param name="from">Where the island is on screen: the screensaver grows out of it.</param>
        /// <param name="grace">How long input is ignored at first: opened by hand, a nudge of the mouse isn't "back".</param>
        public AlbumScreensaver(Func<NowPlaying?> now, Rect from, TimeSpan grace = default)
        {
            this.now = now;
            this.grace = grace;

            screenWidth = SystemParameters.PrimaryScreenWidth;
            screenHeight = SystemParameters.PrimaryScreenHeight;
            unit = screenHeight / 1080;
            origin = new Point(Math.Clamp((from.X + from.Width / 2) / screenWidth, 0, 1), Math.Clamp((from.Y + from.Height / 2) / screenHeight, 0, 1));

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            // Opaque: a see-through window this size would be copied by the processor every frame.
            // It fades in and out with Windows' own window alpha instead, which the GPU does.
            Background = Brushes.Black;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Title = "Islet screensaver";
            Cursor = Cursors.None;
            Left = 0;
            Top = 0;
            Width = screenWidth;
            Height = screenHeight;

            Build();
            Content = stage;

            // A click lands here rather than in whatever is underneath
            stage.MouseDown += (s, e) =>
            {
                if (DateTime.Now - openedAt >= grace)
                    Dismiss();
            };

            SourceInitialized += (s, e) =>
            {
                handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                SetAlpha(0);
            };

            tick.Tick += (s, e) => Tick();
            Loaded += (s, e) =>
            {
                Refresh();
                Step(0);
                Hook();
                tick.Start();
            };
            slow.Tick += (s, e) => OnSlowStep();
            Closed += (s, e) =>
            {
                closed = true;
                slow.Stop();
                CompositionTarget.Rendering -= OnFrame;
            };
        }

        // ================= The pieces =================

        void Build()
        {
            double u = unit;

            stage.Width = screenWidth;
            stage.Height = screenHeight;
            stage.RenderTransformOrigin = origin;
            stage.RenderTransform = grow;

            // The backdrop drifts slowly, larger than the screen so its edges never show
            var drifting = new TransformGroup();
            drifting.Children.Add(drift);
            drifting.Children.Add(driftShift);

            foreach (var image in new[] { backdrop, backdropBefore })
            {
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Linear);
                image.RenderTransformOrigin = new Point(0.5, 0.5);
                image.RenderTransform = drifting;
                stage.Children.Add(image);
            }

            // Darkened, tinted with the cover's own colour, and vignetted towards the corners
            stage.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)), IsHitTestVisible = false });
            stage.Children.Add(new Border { Background = tintBrush, Opacity = 0.34, IsHitTestVisible = false });
            stage.Children.Add(new Border
            {
                IsHitTestVisible = false,
                Background = new RadialGradientBrush(
                    new GradientStopCollection
                    {
                        new(Color.FromArgb(0, 0, 0, 0), 0.35),
                        new(Color.FromArgb(170, 0, 0, 0), 1)
                    })
                { RadiusX = 0.85, RadiusY = 0.85 }
            });

            content.Width = screenWidth;
            content.Height = screenHeight;
            content.IsHitTestVisible = false;
            stage.Children.Add(content);

            // Sizes: the cover and what belongs to it on the left, the lyrics to the right, together centred
            double side = Math.Min(screenHeight * 0.4, screenWidth * 0.24);
            double gap = side * 0.3;
            portWidth = Math.Min(screenWidth * 0.4, 840 * u * 1.15);
            double left = (screenWidth - (side + gap + portWidth)) / 2;
            double columnHeight = side + 202 * u;
            double columnTop = (screenHeight - columnHeight) / 2;

            // Cover
            coverFrame.Width = coverFrame.Height = side;
            coverFrame.CornerRadius = new CornerRadius(22 * u);
            coverFrame.Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
            coverFrame.Child = cover;
            coverFrame.Clip = new RectangleGeometry(new Rect(0, 0, side, side), 22 * u, 22 * u);
            RenderOptions.SetBitmapScalingMode(cover, BitmapScalingMode.HighQuality);

            // Glows in the cover's colour; a plain dark shadow beneath keeps it lifted off the backdrop
            var coverHost = new Border
            {
                Width = side,
                Height = side,
                CornerRadius = new CornerRadius(22 * u),
                Background = Brushes.Black,
                Effect = coverGlow,
                Child = coverFrame
            };
            Canvas.SetLeft(coverHost, left);
            Canvas.SetTop(coverHost, columnTop);
            facade.Children.Add(coverHost);

            // Title and artist under it
            var words = new StackPanel { Width = side };
            Dress(title, side, 32 * u, FontWeights.SemiBold, 1, maxLines: 2);
            Dress(artist, side, 21 * u, FontWeights.Normal, 0.66, maxLines: 1);
            artist.Margin = new Thickness(0, 6 * u, 0, 0);
            words.Children.Add(title);
            words.Children.Add(artist);
            Canvas.SetLeft(words, left);
            Canvas.SetTop(words, columnTop + side + 28 * u);
            facade.Children.Add(words);

            // Drawn once and kept: only the lyrics and the bar move
            facade.CacheMode = new BitmapCache { RenderAtScale = 1 };
            content.Children.Add(facade);

            // Progress at the foot of the column
            barWidth = side;
            double barTop = columnTop + columnHeight - 36 * u;
            barTrack.Width = barWidth;
            barTrack.Height = 5 * u;
            barTrack.CornerRadius = new CornerRadius(3 * u);
            barTrack.Background = new SolidColorBrush(Color.FromArgb(56, 255, 255, 255));
            Canvas.SetLeft(barTrack, left);
            Canvas.SetTop(barTrack, barTop);
            content.Children.Add(barTrack);

            barFill.Height = 5 * u;
            barFill.CornerRadius = new CornerRadius(3 * u);
            barFill.Background = barBrush;
            barFill.Width = 0;
            Canvas.SetLeft(barFill, left);
            Canvas.SetTop(barFill, barTop);
            content.Children.Add(barFill);

            Dress(elapsed, side / 2, 15 * u, FontWeights.Normal, 0.6, maxLines: 1);
            Dress(remaining, side / 2, 15 * u, FontWeights.Normal, 0.6, maxLines: 1);
            remaining.TextAlignment = TextAlignment.Right;
            Dress(paused, side, 14 * u, FontWeights.SemiBold, 0.55, maxLines: 1);
            paused.TextAlignment = TextAlignment.Center;
            Canvas.SetLeft(elapsed, left);
            Canvas.SetTop(elapsed, barTop + 14 * u);
            Canvas.SetLeft(remaining, left + side / 2);
            Canvas.SetTop(remaining, barTop + 14 * u);
            Canvas.SetLeft(paused, left);
            Canvas.SetTop(paused, barTop + 14 * u);
            content.Children.Add(elapsed);
            content.Children.Add(remaining);
            content.Children.Add(paused);

            // The clock, small in the corner
            Dress(clock, 220 * u, 27 * u, FontWeights.Light, 0.8, maxLines: 1);
            clock.TextAlignment = TextAlignment.Right;
            Canvas.SetLeft(clock, screenWidth - 56 * u - 220 * u);
            Canvas.SetTop(clock, 44 * u);
            content.Children.Add(clock);

            // Lyrics, to the right of the column; or, with none, a large clock
            double portLeft = left + side + gap;
            portHeight = screenHeight * 0.76;

            port.Width = portWidth;
            port.Height = portHeight;
            port.OpacityMask = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromArgb(0, 0, 0, 0), 0),
                    new(Color.FromArgb(255, 0, 0, 0), 0.14),
                    new(Color.FromArgb(255, 0, 0, 0), 0.82),
                    new(Color.FromArgb(0, 0, 0, 0), 1)
                }, new Point(0, 0), new Point(0, 1));
            lines.RenderTransform = scrollShift;
            port.Children.Add(lines);

            Canvas.SetLeft(port, portLeft);
            Canvas.SetTop(port, (screenHeight - portHeight) / 2);
            content.Children.Add(port);

            Dress(bigClock, portWidth, 150 * u, FontWeights.Light, 1, maxLines: 1);
            Dress(bigDate, portWidth, 32 * u, FontWeights.Normal, 0.65, maxLines: 1);
            Canvas.SetLeft(bigClock, portLeft);
            Canvas.SetTop(bigClock, screenHeight / 2 - 130 * u);
            Canvas.SetLeft(bigDate, portLeft + 6 * u);
            Canvas.SetTop(bigDate, screenHeight / 2 + 40 * u);
            content.Children.Add(bigClock);
            content.Children.Add(bigDate);
            bigClock.Visibility = bigDate.Visibility = Visibility.Collapsed;
        }

        static void Dress(TextBlock text, double width, double size, FontWeight weight, double opacity, int maxLines)
        {
            text.Width = width;
            text.FontSize = size;
            text.FontWeight = weight;
            text.FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
            text.Foreground = new SolidColorBrush(Color.FromArgb((byte)(255 * opacity), 255, 255, 255));
            text.TextWrapping = maxLines > 1 ? TextWrapping.Wrap : TextWrapping.NoWrap;
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            text.MaxHeight = size * 1.45 * maxLines;
        }

        // ================= Frames =================

        // While something moves quickly (opening, a lyric scrolling into place, a new song) every
        // frame is drawn. Otherwise only the backdrop's slow drift is left, about a pixel a second, and
        // four steps a second look the same: every frame would have the whole screen redrawn 60 times a second.
        readonly DispatcherTimer slow = new() { Interval = TimeSpan.FromMilliseconds(250) };
        readonly System.Diagnostics.Stopwatch slowClock = new();
        bool hooked;

        bool Moving => progress < 1 || closing || songFade > 0 || !settled || LineChangeSoon();

        // A line is sung a little early, so it's lit as the voice starts
        static readonly TimeSpan LyricLead = TimeSpan.FromMilliseconds(200);

        // Comfortably more than one slow step
        static readonly TimeSpan LineAhead = TimeSpan.FromMilliseconds(400);

        /// <summary>Whether the line being sung changes within the next moment.</summary>
        bool LineChangeSoon()
        {
            if (laidOut is not { Count: > 0 } || shownNow is not { Track.Playing: true } playing)
                return false;

            var position = playing.Track.PositionAt(DateTimeOffset.Now) + LyricLead;
            return LyricsService.IndexAt(laidOut, position) != LyricsService.IndexAt(laidOut, position + LineAhead);
        }

        void Hook()
        {
            if (hooked || closed)
                return;

            hooked = true;
            lastFrame = TimeSpan.Zero;
            slow.Stop();
            CompositionTarget.Rendering += OnFrame;
        }

        void Unhook()
        {
            if (!hooked)
                return;

            hooked = false;
            CompositionTarget.Rendering -= OnFrame;

            if (!closed)
            {
                slowClock.Restart();
                slow.Start();
            }
        }

        void OnSlowStep()
        {
            // The next line is close: every frame is drawn from before it starts, so the move to it
            // begins smoothly instead of with one quarter-second leap
            if (LineChangeSoon())
            {
                Hook();
                return;
            }

            double dt = Math.Min(slowClock.Elapsed.TotalSeconds, 1);
            slowClock.Restart();
            Step(dt);

            if (Moving)
                Hook();
        }

        void OnFrame(object? sender, EventArgs e)
        {
            var at = ((RenderingEventArgs)e).RenderingTime;

            if (at == lastFrame)
                return;

            int cap = PerformanceProfile.For(AppSettings.Current.Performance).ContinuousFps;

            if (cap > 0 && lastFrame != TimeSpan.Zero && (at - lastFrame).TotalSeconds < 1.0 / cap - 0.004)
                return;

            double dt = lastFrame == TimeSpan.Zero ? 1.0 / 60 : Math.Clamp((at - lastFrame).TotalSeconds, 0, 0.1);
            lastFrame = at;
            Step(dt);

            if (!Moving)
                Unhook();
        }

        static double Smooth(double x) => x * x * (3 - 2 * x);

        static double Lerp(double a, double b, double t) => a + (b - a) * t;

        static Color Lerp(Color a, Color b, double t) =>
            Color.FromRgb((byte)Lerp(a.R, b.R, t), (byte)Lerp(a.G, b.G, t), (byte)Lerp(a.B, b.B, t));

        /// <summary>One step of the animation, <paramref name="dt"/> seconds long.</summary>
        public void Step(double dt)
        {
            if (closed)
                return;

            progress = Math.Clamp(progress + (closing ? -dt / CloseSeconds : dt / OpenSeconds), 0, 1);
            time += dt;

            // Grows out of where the island sits, and fades in with it
            if (handle == IntPtr.Zero)
                stage.Opacity = Smooth(progress);
            else
                SetAlpha(Smooth(progress));

            double grown = 1 - Math.Pow(1 - progress, 3);
            grow.ScaleX = grow.ScaleY = Lerp(0.92, 1, grown);

            // The previous song's backdrop fades away behind the new one
            if (songFade > 0)
            {
                songFade = Math.Max(0, songFade - dt / SongFadeSeconds);
                backdropBefore.Opacity = Smooth(songFade);

                if (songFade == 0)
                    backdropBefore.Source = null;
            }

            // The backdrop wanders very slowly; the cover's colours are taken up gradually
            double scale = 1.12 + 0.035 * Math.Sin(time * 0.07);
            drift.ScaleX = drift.ScaleY = scale;
            driftShift.X = Math.Sin(time * 0.045) * screenWidth * 0.014;
            driftShift.Y = Math.Cos(time * 0.037) * screenHeight * 0.014;

            tint = Lerp(tint, tintTarget, 1 - Math.Exp(-dt * 2.2));
            tintBrush.Color = tint;

            StepInside(dt);

            if (closing && progress <= 0)
                Finish();
        }

        void StepInside(double dt)
        {
            var playing = shownNow;

            if (playing == null)
                return;

            var track = playing.Track;
            var position = track.PositionAt(DateTimeOffset.Now);
            var duration = track.Duration;

            if (duration > TimeSpan.Zero)
            {
                double fill = barWidth * Math.Clamp(position / duration, 0, 1);

                if (Math.Abs(barFill.Width - fill) > 0.5)
                    barFill.Width = fill;
            }

            settled = true;

            if (laidOut == null || laidOut.Count == 0)
                return;

            // The line being sung, scrolled to a little above the middle, the ones round it dimmer
            int index = LyricsService.IndexAt(laidOut, position + LyricLead);
            int focus = Math.Max(index, 0);

            scrollTarget = portHeight * 0.42 - (tops[focus] + heights[focus] / 2);

            // A critically damped spring: it eases into each line and comes to rest without
            // overshooting and drifting back
            const double stiffness = 55;
            double damping = 2 * Math.Sqrt(stiffness);
            int steps = Math.Max(1, (int)Math.Ceiling(dt / 0.008));
            double h = dt / steps;

            for (int s = 0; s < steps; s++)
            {
                scrollSpeed += (stiffness * (scrollTarget - scroll) - damping * scrollSpeed) * h;
                scroll += scrollSpeed * h;
            }

            if (Math.Abs(scrollTarget - scroll) < 0.08 && Math.Abs(scrollSpeed) < 0.4)
            {
                scroll = scrollTarget;
                scrollSpeed = 0;
            }
            else
            {
                settled = false;
            }

            if (Math.Abs(scrollShift.Y - scroll) > 0.02)
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

                if (!track.Playing)
                    lit *= 0.7;

                if (Math.Abs(glow[i] - lit) > 0.004)
                    settled = false;

                // The finished line dims and the next brightens over about half a second
                glow[i] += (lit - glow[i]) * (1 - Math.Exp(-dt * 5.5));

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

        NowPlaying? shownNow;

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

            shownNow = playing;

            var track = playing.Track;
            var moment = DateTime.Now;
            string text = moment.ToString(AppSettings.Current.Use24HourClock ? "HH:mm" : "h:mm");

            clock.Text = text;
            bigClock.Text = text;
            bigDate.Text = DateText.IslandLong(moment);

            var position = track.PositionAt(DateTimeOffset.Now);

            if (track.Duration > TimeSpan.Zero)
            {
                string passed = Format(position);

                if (elapsed.Text != passed)
                {
                    elapsed.Text = passed;
                    remaining.Text = "-" + Format(track.Duration > position ? track.Duration - position : TimeSpan.Zero);
                }
            }

            string pausedText = track.Playing ? "" : "PAUSED";

            if (paused.Text != pausedText)
                paused.Text = pausedText;

            string song = $"{track.Title}\n{track.Artist}";

            if (song != shownSong)
            {
                bool first = shownSong.Length == 0;
                shownSong = song;

                title.Text = track.Title;
                artist.Text = track.Artist;
                ShowArt(playing.Art, first);

                if (!first)
                    FadeIn(coverFrame, title, artist);
            }
            else if (playing.Art != null && !ReferenceEquals(playing.Art, shownArt))
            {
                // The cover often arrives a moment after the song's name (looked up online, or
                // sent late by the player): it takes its place then, rather than never
                ShowArt(playing.Art, false);
                FadeIn(coverFrame);
            }

            // The lyrics are laid out when they arrive, and again if they change
            if (!laidOutOnce || !ReferenceEquals(laidOut, playing.Lyrics))
                LayOutLyrics(playing.Lyrics);
        }

        BitmapSource? shownArt;

        // The cover, the blurred backdrop made from it, and the colours taken from it
        void ShowArt(BitmapSource? art, bool first)
        {
            shownArt = art;
            cover.Source = art;

            // The old backdrop stays under the new one for a moment, and fades out
            if (!first && backdrop.Source != null)
            {
                backdropBefore.Source = backdrop.Source;
                backdropBefore.Opacity = 1;
                songFade = 1;
                Hook();
            }

            backdrop.Source = art != null ? MainWindow.BlurredCover(art, 240) : null;

            var (primary, _) = AlbumPalette.From(art);
            tintTarget = AlbumPalette.Tone(primary, 0.2, 0.4, 0.75);
            var accent = AlbumPalette.Tone(primary, 0.66, 0.45, 0.95);
            coverGlow.Color = accent;
            barBrush.Color = Lerp(accent, Colors.White, 0.45);

            if (first)
            {
                tint = tintTarget;
                tintBrush.Color = tint;
            }
            else
            {
                Hook();
            }
        }

        static void FadeIn(params UIElement[] parts)
        {
            foreach (var part in parts)
                part.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(600)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        void LayOutLyrics(IReadOnlyList<LyricsService.Line>? lyrics)
        {
            laidOut = lyrics;
            laidOutOnce = true;
            lines.Children.Clear();
            blocks.Clear();
            shownFirst = 1;
            shownLast = 0;
            scroll = scrollSpeed = 0;

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
            double gap = 22 * unit;

            for (int i = 0; i < lyrics.Count; i++)
            {
                var block = new TextBlock
                {
                    Text = lyrics[i].Text.Length == 0 ? "♪" : lyrics[i].Text,
                    Width = portWidth,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = Math.Clamp(46 * unit, 26, 64),
                    FontWeight = FontWeights.SemiBold,
                    FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
                    Foreground = Brushes.White,
                    Opacity = 0,
                    Visibility = Visibility.Hidden,
                    RenderTransformOrigin = new Point(0, 0.5),
                    RenderTransform = new ScaleTransform(0.88, 0.88),

                    // Each line drawn once and kept as a picture: scrolling, dimming and growing only move it
                    CacheMode = new BitmapCache { RenderAtScale = 1, SnapsToDevicePixels = false }
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
            scroll = scrollTarget = portHeight * 0.42 - (tops[0] + heights[0] / 2);
            scrollShift.Y = scroll;
            settled = false;
            Hook();
        }

        // ================= Opening and closing =================

        /// <summary>Lays the window out without showing it, for drawing it to a picture.</summary>
        public void PrepareLayout()
        {
            var size = new Size(screenWidth, screenHeight);
            stage.Measure(size);
            stage.Arrange(new Rect(size));
            Refresh();
            stage.UpdateLayout();
        }

        /// <summary>The picture it would show now.</summary>
        public BitmapSource Snapshot()
        {
            var bitmap = new RenderTargetBitmap((int)screenWidth, (int)screenHeight, 96, 96, PixelFormats.Pbgra32);
            stage.UpdateLayout();
            bitmap.Render(stage);
            return bitmap;
        }

        /// <summary>Fades away, then goes.</summary>
        public void Dismiss()
        {
            if (closing || closed)
                return;

            closing = true;
            tick.Stop();
            Hook();
            Dismissed?.Invoke();
        }

        void Finish()
        {
            if (closed)
                return;

            closed = true;
            slow.Stop();
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

        IntPtr handle;
        int alphaShown = -1;

        // The whole window's opacity, 0 to 1; layered only while partly see-through
        void SetAlpha(double alpha)
        {
            int value = (int)Math.Round(Math.Clamp(alpha, 0, 1) * 255);

            if (value == alphaShown || handle == IntPtr.Zero)
                return;

            alphaShown = value;
            long style = GetWindowLongPtr(handle, ExStyle).ToInt64();

            if (value >= 255)
            {
                SetWindowLongPtr(handle, ExStyle, new IntPtr(style & ~Layered));
                return;
            }

            if ((style & Layered) == 0)
                SetWindowLongPtr(handle, ExStyle, new IntPtr(style | Layered));

            SetLayeredWindowAttributes(handle, 0, (byte)value, LayeredAlpha);
        }

        const int ExStyle = -20;
        const long Layered = 0x80000;
        const uint LayeredAlpha = 0x2;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

        [DllImport("user32.dll")]
        static extern bool SetLayeredWindowAttributes(IntPtr window, uint key, byte alpha, uint flags);

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
