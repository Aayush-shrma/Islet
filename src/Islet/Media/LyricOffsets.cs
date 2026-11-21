using System.IO;
using System.Text.Json;

namespace Islet
{
    /// <summary>
    /// How far each song's lyrics have been nudged, in seconds (later is positive), kept in a small
    /// file so a song lined up once stays lined up. By artist and title, so it holds whichever
    /// player the song comes from.
    /// </summary>
    public static class LyricOffsets
    {
        const int Kept = 500;

        static Dictionary<string, double>? offsets;

        static string FilePath => Path.Combine(AppSettings.Folder, "lyric-offsets.json");

        static string Key(string artist, string title) =>
            ArtworkService.Normalize(artist) + "|" + ArtworkService.Normalize(title);

        static Dictionary<string, double> Offsets
        {
            get
            {
                if (offsets != null)
                    return offsets;

                try
                {
                    offsets = File.Exists(FilePath)
                        ? JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(FilePath)) ?? new()
                        : new();
                }
                catch (Exception ex)
                {
                    Log.Info("Couldn't read lyric timings: " + ex.Message);
                    offsets = new();
                }

                return offsets;
            }
        }

        public static double For(string artist, string title) => Offsets.GetValueOrDefault(Key(artist, title));

        public static void Set(string artist, string title, double seconds)
        {
            string key = Key(artist, title);
            seconds = Math.Round(Math.Clamp(seconds, -30, 30), 1);

            if (seconds == 0)
                Offsets.Remove(key);
            else
                Offsets[key] = seconds;

            // Not without end: past this, the oldest-added go
            while (Offsets.Count > Kept)
                Offsets.Remove(Offsets.Keys.First());

            try
            {
                Directory.CreateDirectory(AppSettings.Folder);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(Offsets));
            }
            catch (Exception ex)
            {
                Log.Error("Saving lyric timings failed", ex);
            }
        }
    }
}
