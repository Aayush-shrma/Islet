using System.Windows;
using System.Windows.Controls;

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
            var musicItems = new FrameworkElement[] { MenuNowPlaying, MenuPlayPause, MenuNext };

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

            bool atTop = Settings.IslandEdge == IslandEdge.Top;
            MenuEdge.Header = atTop ? "Move to the bottom" : "Move to the top";
            MenuEdgeGlyph.Text = atTop ? "\uE74B" : "\uE74A";
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

        void MenuEdge_Click(object sender, RoutedEventArgs e)
        {
            Settings.IslandEdge = Settings.IslandEdge == IslandEdge.Top ? IslandEdge.Bottom : IslandEdge.Top;
            Settings.Save();
        }

        void MenuCentre_Click(object sender, RoutedEventArgs e)
        {
            Settings.IslandX = 0.5;
            Settings.Save();
        }
    }
}
