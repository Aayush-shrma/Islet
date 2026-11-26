using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Islet
{
    public partial class MainWindow
    {
        // ================= The right-click menu =================
        //
        // Set up each time it opens, from what's going on: the song and its controls when one
        // is playing, the timer's own choices, and the way to the other edge or the middle.

        void IslandMenu_Opened(object sender, RoutedEventArgs e)
        {
            bool hasTrack = track != null;
            var musicItems = new FrameworkElement[] { MenuNowPlaying, MenuPlayPause, MenuNext, MenuScreensaver };

            foreach (var item in musicItems)
                item.Visibility = hasTrack ? Visibility.Visible : Visibility.Collapsed;

            if (track != null)
            {
                MenuSongTitle.Text = track.Title;
                MenuSongArtist.Text = track.Site != null ? $"{track.Artist} · {track.Site}" : track.Artist;
                MenuPlayPause.Header = playing ? "Pause" : "Play";
                MenuPlayPauseGlyph.Text = playing ? "\uE769" : "\uE768";
            }

            // Lyrics timing, only with lyrics, saying where the timing is
            bool hasLyrics = lyricsFound != null && track != null;
            double nudge = hasLyrics ? LyricOffsets.For(track!.Artist, track.Title) : 0;

            LyricsTimingMenu.Visibility = hasLyrics ? Visibility.Visible : Visibility.Collapsed;
            LyricsTimingMenu.Header = nudge == 0 ? "Lyrics timing" : $"Lyrics timing ({nudge:+0.0;-0.0} s)";
            LyricsReset.IsEnabled = nudge != 0;
            MenuMusicSeparator.Visibility = hasTrack ? Visibility.Visible : Visibility.Collapsed;

            BuildTimerMenu();
            BuildLookMenus();

            bool atTop = Settings.IslandEdge == IslandEdge.Top;
            MenuEdge.Header = atTop ? "Move to the bottom" : "Move to the top";
            MenuEdgeGlyph.Text = atTop ? "\uE74B" : "\uE74A";

            // Only where there's a taskbar along the bottom to dock in
            MenuTaskbar.IsChecked = Settings.IslandEdge == IslandEdge.Taskbar;
            MenuTaskbar.Visibility = ReadTaskbarBand() != null ? Visibility.Visible : Visibility.Collapsed;
            MenuCentre.Visibility = Math.Abs(Settings.IslandX - 0.5) > 0.01 ? Visibility.Visible : Visibility.Collapsed;
        }

        // The same choices as the tray's timer menu, by what the timer is doing
        void BuildTimerMenu()
        {
            MenuTimer.Items.Clear();

            MenuItem Add(string header, Action action, string? glyph = null, string? hint = null)
            {
                var item = new MenuItem { Header = header, InputGestureText = hint ?? "" };

                if (glyph != null)
                    item.Icon = new TextBlock { Text = glyph, Style = (Style)FindResource("MenuGlyph") };

                item.Click += (s, e) => action();
                MenuTimer.Items.Add(item);
                return item;
            }

            switch (countdown.State)
            {
                case CountdownState.Idle:
                    MenuTimer.Header = "Timer";
                    Add("Focus", () => countdown.Start(TimerCard.FocusLength, focus: true), "\uE890", "25 min");
                    MenuTimer.Items.Add(new Separator { Style = (Style)FindResource("IslandMenuSeparator") });

                    foreach (int minutes in new[] { 5, 10, 15, 30, 60 })
                        Add($"{minutes} minutes", () => countdown.Start(TimeSpan.FromMinutes(minutes)));
                    break;

                case CountdownState.Ringing:
                    MenuTimer.Header = "Timer (finished)";
                    Add("Dismiss", countdown.Stop, "\uE711");
                    Add("Another minute", () => countdown.AddTime(TimeSpan.FromMinutes(1)), "\uE710");
                    break;

                default:
                    MenuTimer.Header = countdown.State == CountdownState.Paused
                        ? $"Timer (paused, {TimerText.Clock(countdown.Remaining)})"
                        : $"Timer ({TimerText.Clock(countdown.Remaining)})";

                    if (countdown.State == CountdownState.Paused)
                        Add("Resume", countdown.Resume, "\uE768");
                    else
                        Add("Pause", countdown.Pause, "\uE769");

                    Add("Add a minute", () => countdown.AddTime(TimeSpan.FromMinutes(1)), "\uE710");
                    Add("Cancel", countdown.Stop, "\uE711");
                    break;
            }
        }

        static readonly string[] MusicLookNames = { "Pill with bars", "Circle with bars", "Small pill", "Tiny", "Line" };
        static readonly string[] IdleLookNames = { "Tiny clock", "Hide", "Clock", "Clock + weather", "Circle" };

        // Both ways the island looks, as lists to pick from, the current one ticked
        void BuildLookMenus()
        {
            FillLooks(MenuMusicView, MusicLookNames, (int)Settings.MusicLook, i =>
            {
                Settings.MusicLook = (MusicLook)i;
                Settings.Save();
            });

            FillLooks(MenuIdleView, IdleLookNames, (int)Settings.IdleMode, i =>
            {
                Settings.IdleMode = (IdleMode)i;
                Settings.Save();
                _ = RefreshWeatherAsync();
            });
        }

        void FillLooks(MenuItem menu, string[] names, int current, Action<int> pick)
        {
            menu.Items.Clear();

            for (int i = 0; i < names.Length; i++)
            {
                int index = i;
                var item = new MenuItem { Header = names[i] };

                if (i == current)
                    item.Icon = new TextBlock { Text = "", Style = (Style)FindResource("MenuGlyph"), Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0xD7, 0x60)) };

                item.Click += (s, e) => pick(index);
                menu.Items.Add(item);
            }
        }

        // Dismissed again by any key, mouse movement or click (after a moment), as when it comes by itself
        void MenuScreensaver_Click(object sender, RoutedEventArgs e) => ShowScreensaver(byHand: true);

        void MenuEdge_Click(object sender, RoutedEventArgs e)
        {
            Settings.IslandEdge = Settings.IslandEdge == IslandEdge.Top ? IslandEdge.Bottom : IslandEdge.Top;
            Settings.Save();
        }

        // Docked: into the taskbar's strip. Undocked: just above the taskbar, where it sits at the bottom edge
        void MenuTaskbar_Click(object sender, RoutedEventArgs e)
        {
            Settings.IslandEdge = Settings.IslandEdge == IslandEdge.Taskbar ? IslandEdge.Bottom : IslandEdge.Taskbar;
            Settings.Save();
        }

        void MenuCentre_Click(object sender, RoutedEventArgs e)
        {
            Settings.IslandX = 0.5;
            Settings.Save();
        }
    }
}
