using System.Windows;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;

namespace Islet
{
    public partial class MainWindow
    {
        readonly ListeningStats listening = new(AppSettings.Folder);

        /// <summary>What has played today and yesterday, for the Listening page in Settings.</summary>
        public ListeningStats Listening => listening;
        DateTime lastCounted = DateTime.Now;
        AlbumScreensaver? screensaver;

        // When the day's summary drops out of the island
        const int SummaryHour = 21;
        static readonly TimeSpan SummaryAtLeast = TimeSpan.FromMinutes(10);
        static readonly Color SummaryBadge = Color.FromRgb(0x1D, 0xB9, 0x54);

        void SetUpListening()
        {
            Closed += (s, e) =>
            {
                listening.Save();
                screensaver?.Dismiss();
            };
        }

        // Called every second from the clock
        void TickListening()
        {
            var now = DateTime.Now;
            double gap = (now - lastCounted).TotalSeconds;
            lastCounted = now;

            // A long gap means the PC was asleep, not that music played all that time.
            // Only songs count: not videos, clips or other media.
            if (track != null && playing && gap > 0 && gap < 5 && ListeningStats.Counts(track))
                listening.Add(now, track.Artist, track.Title, TimeSpan.FromSeconds(gap), track.Album, EffectiveArt(track, trackKey),
                    track.Site ?? track.SourceName, track.PositionAt(DateTimeOffset.Now));

            AmbientCaption.Text = (Settings.DailyMusicSummary ? ListeningStats.DescribeShort(listening.For(now)) : null)
                ?? "Nothing playing";

            ShowEveningSummary(now);
            StartScreensaverIfAway();
        }

        // Once a day, in the evening, if there was enough music to talk about
        void ShowEveningSummary(DateTime now)
        {
            if (!Settings.DailyMusicSummary || now.Hour < SummaryHour || listening.SummaryShown(now))
                return;

            var today = listening.For(now);

            if (today.Total < SummaryAtLeast)
                return;

            // Not over a game; it'll come once that's closed
            if (fullscreen.ProcessName != null && Settings.ShouldHideFor(fullscreen.ProcessName))
                return;

            listening.MarkSummaryShown(now);

            // On Sundays, the whole week instead of the day
            bool week = now.DayOfWeek == DayOfWeek.Sunday;
            var period = week ? listening.Period(now.AddDays(1 - 7), now) : listening.Period(now, now);
            var song = period.Songs.FirstOrDefault();
            var artist = period.Artists.FirstOrDefault();
            var detail = new List<string>();

            if (artist != null)
                detail.Add($"Mostly {artist.Name}");

            if (song != null)
                detail.Add($"top song: {song.Name}");

            Enqueue(new NotifyItem(
                week ? "Your week in music" : "Your day in music",
                $"{ListeningStats.Duration(period.Total)} of music {(week ? "this week" : "today")}",
                detail.Count > 0 ? string.Join(" · ", detail) : "Thanks for the company",
                CoverImage(song?.Cover),
                SummaryBadge,
                0xE8D6,
                () => OpenSettings("Listening")));
        }

        static System.Windows.Media.Imaging.BitmapImage? CoverImage(string? file)
        {
            if (file == null)
                return null;

            try
            {
                var image = new System.Windows.Media.Imaging.BitmapImage();
                image.BeginInit();
                image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(file);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch
            {
                return null;
            }
        }

        // ================= Screensaver =================

        void StartScreensaverIfAway()
        {
            if (!Settings.ScreensaverEnabled || screensaver != null || track == null || !playing)
                return;

            if (AlbumScreensaver.IdleFor() < TimeSpan.FromMinutes(Math.Max(1, Settings.ScreensaverMinutes)))
                return;

            if (!ScreensaverMayShow())
                return;

            // Not over a video you're watching in the browser in front of you
            if (track.Source == MediaSource.Browser &&
                string.Equals(ForegroundProcessName(), track.ProcessName, StringComparison.OrdinalIgnoreCase))
                return;

            ShowScreensaver();
        }

        // Sitting still on a call, or watching a video or a game, isn't being away
        bool ScreensaverMayShow()
        {
            if (privacy.Current.Any)
                return false;

            if (fullscreen.ProcessName != null)
                return false;

            // Video players, browsers showing a video and call apps all ask for the screen to be kept on
            return !DisplayRequest.IsHeld();
        }

        /// <summary>Starts the screensaver, whether or not you've been away.</summary>
        void ShowScreensaver(bool byHand = false)
        {
            if (screensaver != null || track == null)
                return;

            try
            {
                // The island's shape on screen: the screensaver grows out of it
                double w = Math.Max(1, width.Value), h = Math.Max(1, height.Value);
                var island = new Rect(Left + islandLeft, Top + IslandTopInWindow(h), w, h);

                screensaver = new AlbumScreensaver(
                    () => track == null || privacy.Current.Any || fullscreen.ProcessName != null
                        ? null
                        : new NowPlaying(track, EffectiveArt(track, trackKey) ?? track.Artwork, lyrics),
                    island,
                    byHand ? TimeSpan.FromSeconds(3) : TimeSpan.Zero);

                // The island stays out of sight until it has gone, then shows what came in meanwhile
                screensaver.Closed += (s, e) =>
                {
                    screensaver = null;
                    SyncHiddenForApp();

                    if (currentNotify == null && notifyQueue.Count > 0)
                        ShowNextNotification();
                };

                screensaver.Show();
                SyncHiddenForApp();
                Log.Info(byHand ? "Screensaver opened from the menu" : "Screensaver started");
            }
            catch (Exception ex)
            {
                Log.Error("Starting the screensaver failed", ex);
                screensaver = null;
            }
        }

        static string? ForegroundProcessName()
        {
            try
            {
                GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
                using var process = Process.GetProcessById((int)pid);
                return process.ProcessName;
            }
            catch
            {
                return null;
            }
        }

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    }
}
