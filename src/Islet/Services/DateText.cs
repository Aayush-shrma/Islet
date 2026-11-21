using System.Globalization;

namespace Islet
{
    /// <summary>
    /// Dates written the same way everywhere, in the order Windows uses for its language and region
    /// ("November 15" or "15 November"), and, for the island, in Nepal's calendar when chosen.
    /// </summary>
    public static class DateText
    {
        static DateTimeFormatInfo Format => CultureInfo.CurrentCulture.DateTimeFormat;

        // The day-and-month pattern Windows uses, with the month spelled out or shortened
        static string MonthDayPattern(bool shortMonth)
        {
            string pattern = Format.MonthDayPattern;
            return shortMonth ? pattern.Replace("MMMM", "MMM") : pattern;
        }

        /// <summary>"November 15", or "Nov 15" shortened.</summary>
        public static string MonthDay(DateTime date, bool shortMonth = true) => date.ToString(MonthDayPattern(shortMonth));

        /// <summary>"Sunday, November 15".</summary>
        public static string Long(DateTime date) => $"{date:dddd}, {MonthDay(date, shortMonth: false)}";

        /// <summary>"Sun, Nov 15".</summary>
        public static string Short(DateTime date) => $"{date:ddd}, {MonthDay(date)}";

        /// <summary>"Nov 15 – Nov 21".</summary>
        public static string Range(DateTime first, DateTime last) => $"{MonthDay(first)} – {MonthDay(last)}";

        /// <summary>"Nov 15, 2026", in Windows' order.</summary>
        public static string WithYear(DateTime date) =>
            date.ToString(Format.LongDatePattern.Contains("dddd") ? Format.LongDatePattern.Replace("dddd, ", "").Replace("dddd ", "").Replace("MMMM", "MMM") : "d MMM yyyy");

        // ----- On the island, Nepal's calendar when chosen -----

        /// <summary>The island's full date line: "Sunday, November 15", or "Sunday, Kartik 29" / "आइतबार, कात्तिक २९".</summary>
        public static string IslandLong(DateTime date) => AppSettings.Current.NepaliDate switch
        {
            NepaliDateStyle.Latin => NepaliDate.Long(date, inNepali: false) ?? Long(date),
            NepaliDateStyle.Devanagari => NepaliDate.Long(date, inNepali: true) ?? Long(date),
            _ => Long(date)
        };

        /// <summary>The island's short date: "Sun 15", or "Kartik 29" / "कात्तिक २९".</summary>
        public static string IslandShort(DateTime date) => AppSettings.Current.NepaliDate switch
        {
            NepaliDateStyle.Latin => NepaliDate.MonthDay(date, inNepali: false) ?? date.ToString("ddd d"),
            NepaliDateStyle.Devanagari => NepaliDate.MonthDay(date, inNepali: true) ?? date.ToString("ddd d"),
            _ => date.ToString("ddd d")
        };
    }
}
