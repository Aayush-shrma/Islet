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
        NowPlayingTakeover? screensaver;
        readonly float[] takeoverLevels = new float[AudioVisualizer.BandCount];

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
            int minutes = Settings.ScreensaverMinutes;

            if (minutes <= 0 || screensaver != null || track == null || !playing)
                return;

            // Sitting still on a call isn't being away
            if (privacy.Current.Any)
                return;

            if (NowPlayingTakeover.IdleFor() < TimeSpan.FromMinutes(minutes))
                return;

            // Not over a full-screen app, or a video you're watching in the browser in front of you
            if (fullscreen.ProcessName != null)
                return;

            if (track.Source == MediaSource.Browser &&
                string.Equals(ForegroundProcessName(), track.ProcessName, StringComparison.OrdinalIgnoreCase))
                return;

            ShowTakeover();
        }

        /// <summary>Starts the Now Playing takeover, whether or not you've been away.</summary>
        void ShowTakeover(bool byHand = false)
        {
            if (screensaver != null || track == null)
                return;

            try
            {
                // The island's shape on screen: the takeover grows out of exactly that
                double w = Math.Max(1, width.Value), h = Math.Max(1, height.Value);
                var island = new Rect(Left + islandLeft, Top + IslandTopInWindow(h), w, h);

                screensaver = new NowPlayingTakeover(
                    () => track == null
                        ? null
                        : new NowPlaying(track, EffectiveArt(track, trackKey) ?? track.Artwork, lyrics),
                    TakeoverLevel,
                    island,
                    byHand ? TimeSpan.FromSeconds(4) : TimeSpan.Zero);

                // The waves follow the real audio, so it's listened to while this is up
                visualizer.SetActive(Settings.RealAudioVisualizer && Profile.RealAudio && playing);

                screensaver.Dismissed += () =>
                {
                    screensaver = null;
                    SyncVisualizer(surface);
                };

                screensaver.Show();
                Log.Info("Now Playing takeover started");
            }
            catch (Exception ex)
            {
                Log.Error("Starting the Now Playing takeover failed", ex);
                screensaver = null;
            }
        }

        // How loud the music is, 0 to 1, or -1 when the audio isn't being listened to
        float TakeoverLevel()
        {
            if (!visualizer.IsRunning)
                return -1;

            visualizer.GetLevels(takeoverLevels);
            return Math.Clamp(takeoverLevels.Average() * 1.8f, 0, 1);
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
