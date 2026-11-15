using System.Text.Json;
using Islet;

namespace Islet.Tests;

public class UpdateServiceTests
{
    static JsonElement Release(string tag, bool withZip = true, bool signed = true) => JsonDocument.Parse($$"""
    {
      "tag_name": "{{tag}}",
      "html_url": "https://github.com/Prathambist/Islet/releases/tag/{{tag}}",
      "assets": [
        { "name": "notes.txt", "browser_download_url": "https://x/notes.txt" }
        {{(withZip ? """, { "name": "Islet-win-x64.zip", "browser_download_url": "https://x/app.zip" }""" : "")}}
        {{(signed ? """, { "name": "Islet-win-x64.zip.sig", "browser_download_url": "https://x/app.zip.sig" }""" : "")}}
      ]
    }
    """).RootElement.Clone();

    [Fact]
    public void Offers_only_signed_releases()
    {
        Assert.Equal("https://x/app.zip.sig", UpdateService.ParseRelease(Release("v1.2.0"), Installed)!.SignatureUrl);
        Assert.Null(UpdateService.ParseRelease(Release("v1.2.0", signed: false), Installed));
    }

    static readonly Version Installed = new(1, 1, 0);

    [Fact]
    public void Offers_a_newer_release()
    {
        var release = UpdateService.ParseRelease(Release("v1.2.0"), Installed);

        Assert.NotNull(release);
        Assert.Equal(new Version(1, 2, 0), release!.Version);
        Assert.Equal("https://x/app.zip", release.ZipUrl);
    }

    [Theory]
    [InlineData("v1.1.0")]
    [InlineData("v1.0.9")]
    [InlineData("1.1")]
    public void Ignores_same_or_older_releases(string tag)
    {
        Assert.Null(UpdateService.ParseRelease(Release(tag), Installed));
    }

    [Fact]
    public void Two_part_tags_are_compared_correctly()
    {
        Assert.NotNull(UpdateService.ParseRelease(Release("v1.2"), Installed));
    }

    [Fact]
    public void Release_notes_come_out_as_plain_lines()
    {
        var notes = UpdateService.NotesFromMarkdown(string.Join("LF",
            "## What's Changed", "", "* **Listening** page with [charts](https://x)", "- Fix the screensaver by @someone in https://github.com/x/pull/3",
            "1. Faster startup", "", "**Full Changelog**: https://github.com/x/compare/v1...v2").Replace("LF", "\n"));

        Assert.Equal(new[] { "Listening page with charts", "Fix the screensaver", "Faster startup" }, notes);
        Assert.Empty(UpdateService.NotesFromMarkdown("**Full Changelog**: https://github.com/x/compare/v1...v2"));
        Assert.Empty(UpdateService.NotesFromMarkdown(null));
    }

    [Fact]
    public void Commit_titles_become_notes_newest_first()
    {
        var compare = JsonDocument.Parse("""
        { "commits": [
          { "commit": { "message": "Add Listening page" } },
          { "commit": { "message": "Merge branch 'main'" } },
          { "commit": { "message": "Steady screensaver layoutLFLFLonger explanation" } }
        ] }
        """.Replace("LF", "\\n")).RootElement;

        Assert.Equal(new[] { "Steady screensaver layout", "Add Listening page" }, UpdateService.NotesFromCommits(compare));
    }

    [Fact]
    public void Ignores_pre_release_style_tags_and_missing_zips()
    {
        Assert.Null(UpdateService.ParseRelease(Release("v1.2.0-beta"), Installed));
        Assert.Null(UpdateService.ParseRelease(Release("v9.0.0", withZip: false), Installed));
    }
}

public class FullscreenRuleTests
{
    static AppSettings With(bool hideByDefault, params (string Process, FullscreenRule Rule)[] apps) => new()
    {
        HideInFullscreen = hideByDefault,
        FullscreenApps = apps.Select(a => new FullscreenApp { ProcessName = a.Process, DisplayName = a.Process, Rule = a.Rule }).ToList()
    };

    [Fact]
    public void Default_rule_follows_the_main_switch()
    {
        Assert.True(With(true).ShouldHideFor("game"));
        Assert.False(With(false).ShouldHideFor("game"));
        Assert.True(With(true, ("game", FullscreenRule.Default)).ShouldHideFor("game"));
    }

    [Fact]
    public void Per_app_rules_override_the_main_switch()
    {
        var settings = With(true, ("vlc", FullscreenRule.Show));
        Assert.False(settings.ShouldHideFor("vlc"));
        Assert.True(settings.ShouldHideFor("game"));

        settings = With(false, ("POWERPNT", FullscreenRule.Hide));
        Assert.True(settings.ShouldHideFor("powerpnt"));
        Assert.False(settings.ShouldHideFor("chrome"));
    }
}

public class SpringTests
{
    static void Run(Spring spring, double seconds)
    {
        for (double t = 0; t < seconds; t += 1.0 / 60)
            spring.Step(1.0 / 60);
    }

    [Fact]
    public void Settles_on_the_target()
    {
        var spring = new Spring(38, 0.5, 0.8) { Target = 172 };
        Run(spring, 3);

        Assert.True(spring.IsSettled);
        Assert.Equal(172, spring.Value, 2);
    }

    [Fact]
    public void Bouncy_spring_overshoots_and_critical_does_not()
    {
        var bouncy = new Spring(0, 0.5, 0.5) { Target = 100 };
        var critical = new Spring(0, 0.5, 1) { Target = 100 };
        double bouncyMax = 0, criticalMax = 0;

        for (int i = 0; i < 180; i++)
        {
            bouncy.Step(1.0 / 60);
            critical.Step(1.0 / 60);
            bouncyMax = Math.Max(bouncyMax, bouncy.Value);
            criticalMax = Math.Max(criticalMax, critical.Value);
        }

        Assert.True(bouncyMax > 105);
        Assert.True(criticalMax <= 100.5);
    }

    [Fact]
    public void Retargeting_keeps_momentum()
    {
        var spring = new Spring(0, 0.5, 0.8) { Target = 100 };
        Run(spring, 0.1);
        double velocity = spring.Velocity;

        spring.Target = 0;

        Assert.Equal(velocity, spring.Velocity);
        Assert.True(velocity > 0);
    }

    [Fact]
    public void Large_frame_gaps_stay_stable()
    {
        var spring = new Spring(0, 0.3, 0.7) { Target = 400 };

        for (int i = 0; i < 20; i++)
            spring.Step(0.25);

        Assert.InRange(spring.Value, 399, 401);
    }
}

public class NotificationClassifyTests
{
    [Theory]
    [InlineData("Discord", ChatApp.Discord, false)]
    [InlineData("WhatsApp", ChatApp.WhatsApp, false)]
    [InlineData("WhatsApp Web - Brave", ChatApp.WhatsApp, true)]
    public void Recognises_apps_by_name(string appName, ChatApp expected, bool fromBrowser)
    {
        var (app, browser) = NotificationWatcher.Classify(appName, new List<string> { "Sam", "hi" });

        Assert.Equal(expected, app);
        Assert.Equal(fromBrowser, browser);
    }

    [Theory]
    [InlineData("web.whatsapp.com", ChatApp.WhatsApp)]
    [InlineData("https://discord.com/channels/1", ChatApp.Discord)]
    [InlineData("www.messenger.com", ChatApp.Messenger)]
    public void Recognises_browser_tabs_by_site_and_removes_the_address(string site, ChatApp expected)
    {
        var texts = new List<string> { "Sam", "hi", site };

        var (app, browser) = NotificationWatcher.Classify("Google Chrome", texts);

        Assert.Equal(expected, app);
        Assert.True(browser);
        Assert.Equal(new[] { "Sam", "hi" }, texts);
    }

    [Theory]
    [InlineData("mail.example.com")]
    [InlineData("check out discord.com")]
    [InlineData("notdiscord.com")]
    public void Other_browser_notifications_stay_generic(string lastLine)
    {
        var texts = new List<string> { "Sam", lastLine };

        var (app, _) = NotificationWatcher.Classify("Microsoft Edge", texts);

        Assert.Equal(ChatApp.Browser, app);
        Assert.Equal(2, texts.Count);
    }

    [Fact]
    public void Other_apps_are_their_own_even_with_a_chat_address()
    {
        // Outlook mentioning discord.com is still Outlook, not a Discord message
        Assert.Equal(ChatApp.Other, NotificationWatcher.Classify("Outlook", new List<string> { "Sam", "discord.com" }).App);
        Assert.Equal(ChatApp.Other, NotificationWatcher.Classify("Microsoft Teams", new List<string> { "Meeting in 5 minutes" }).App);
    }
}

public class LyricOffsetsTests
{
    [Fact]
    public void A_songs_lyric_timing_is_remembered_by_artist_and_title()
    {
        LyricOffsets.Set("Daft Punk", "Instant Crush", 1.5);

        // The same song from another player, written a little differently, finds the same timing
        Assert.Equal(1.5, LyricOffsets.For("DAFT PUNK", "Instant Crush"));
        Assert.Equal(0, LyricOffsets.For("Daft Punk", "Get Lucky"));

        LyricOffsets.Set("Daft Punk", "Instant Crush", 0);
        Assert.Equal(0, LyricOffsets.For("Daft Punk", "Instant Crush"));
    }
}

public class IslandEdgeTests
{
    [Theory]
    [InlineData(100, IslandEdge.Top)]
    [InlineData(539, IslandEdge.Top)]
    [InlineData(541, IslandEdge.Bottom)]
    [InlineData(1050, IslandEdge.Bottom)]
    public void A_dropped_island_goes_to_the_nearer_edge(double y, IslandEdge edge) =>
        Assert.Equal(edge, MainWindow.EdgeFor(y, 1080));
}

public class NepaliDateTests
{
    [Theory]
    [InlineData(2024, 4, 13, 2081, 1, 1)]   // New Year 2081
    [InlineData(2025, 4, 14, 2082, 1, 1)]   // New Year 2082
    [InlineData(2026, 4, 14, 2083, 1, 1)]   // New Year 2083
    [InlineData(2026, 4, 13, 2082, 12, 30)] // the day before: the last of Chaitra
    [InlineData(2026, 11, 15, 2083, 7, 29)] // Kartik 29
    public void Converts_to_Bikram_Sambat(int y, int m, int d, int bsYear, int bsMonth, int bsDay) =>
        Assert.Equal((bsYear, bsMonth, bsDay), NepaliDate.From(new DateTime(y, m, d)));

    [Fact]
    public void Writes_it_in_English_letters_or_Nepali()
    {
        var day = new DateTime(2026, 11, 15);

        Assert.Equal("Kartik 29", NepaliDate.MonthDay(day, inNepali: false));
        Assert.Equal("कात्तिक २९", NepaliDate.MonthDay(day, inNepali: true));
        Assert.Equal("आइतबार, कात्तिक २९", NepaliDate.Long(day, inNepali: true));
        Assert.Equal("२०८३", NepaliDate.Digits(2083));
    }

    [Fact]
    public void Says_nothing_outside_its_table() =>
        Assert.Null(NepaliDate.From(new DateTime(2000, 1, 1)));
}

public class DateTextTests
{
    [Fact]
    public void Dates_follow_the_languages_own_order()
    {
        var before = System.Globalization.CultureInfo.CurrentCulture;

        try
        {
            var day = new DateTime(2026, 11, 15);

            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("en-US");
            Assert.Equal("Sunday, November 15", DateText.Long(day));
            Assert.Equal("Sun, Nov 15", DateText.Short(day));

            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("en-GB");
            Assert.Equal("Sunday, 15 November", DateText.Long(day));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = before;
        }
    }
}
