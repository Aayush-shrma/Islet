namespace Islet
{
    /// <summary>
    /// Dates in Nepal's Bikram Sambat calendar, for the island's date. Its months don't follow a
    /// rule: each year's lengths are set by the calendar committee, so they come from the published
    /// table (here 2070 to 2100 BS, 2013 to 2044 AD, from the open-source nepali-datetime project),
    /// counted from 1 Baisakh 2070 = 14 April 2013.
    /// </summary>
    public static class NepaliDate
    {
        const int FirstYear = 2070;
        static readonly DateTime FirstDay = new(2013, 4, 14);

        // Days in each month, Baisakh to Chaitra, year by year from 2070 BS
        static readonly int[][] MonthLengths =
        {
            new[] { 31, 31, 31, 32, 31, 31, 29, 30, 30, 29, 30, 30 },
            new[] { 31, 31, 32, 31, 31, 31, 30, 29, 30, 29, 30, 30 },
            new[] { 31, 32, 31, 32, 31, 30, 30, 29, 30, 29, 30, 30 },
            new[] { 31, 32, 31, 32, 31, 30, 30, 30, 29, 29, 30, 31 },
            new[] { 31, 31, 31, 32, 31, 31, 30, 29, 30, 29, 30, 30 },
            new[] { 31, 31, 32, 31, 31, 31, 30, 29, 30, 29, 30, 30 },
            new[] { 31, 32, 31, 32, 31, 30, 30, 30, 29, 29, 30, 30 },
            new[] { 31, 32, 31, 32, 31, 30, 30, 30, 29, 30, 29, 31 },
            new[] { 31, 31, 31, 32, 31, 31, 30, 29, 30, 29, 30, 30 },
            new[] { 31, 31, 32, 31, 31, 31, 30, 29, 30, 29, 30, 30 },
            new[] { 31, 32, 31, 32, 31, 30, 30, 30, 29, 29, 30, 30 },
            new[] { 31, 32, 31, 32, 31, 30, 30, 30, 29, 30, 29, 31 },
            new[] { 31, 31, 32, 31, 31, 31, 30, 29, 30, 29, 30, 30 },
            new[] { 31, 31, 32, 31, 31, 31, 30, 29, 30, 29, 30, 30 },
            new[] { 31, 31, 32, 31, 31, 30, 30, 30, 29, 30, 30, 30 },
            new[] { 31, 32, 31, 32, 30, 31, 30, 30, 29, 30, 30, 30 },
            new[] { 30, 32, 31, 32, 31, 30, 30, 30, 29, 30, 30, 30 },
            new[] { 31, 31, 32, 31, 31, 31, 30, 29, 30, 30, 30, 30 },
            new[] { 30, 31, 32, 32, 30, 31, 30, 30, 29, 30, 30, 30 },
            new[] { 30, 32, 31, 32, 31, 30, 30, 30, 29, 30, 30, 30 },
            new[] { 30, 32, 31, 32, 31, 30, 30, 30, 29, 30, 30, 30 },
            new[] { 31, 31, 32, 31, 31, 31, 30, 30, 29, 30, 30, 30 },
            new[] { 30, 31, 32, 32, 31, 30, 30, 30, 29, 30, 30, 30 },
            new[] { 30, 32, 31, 32, 31, 30, 30, 30, 29, 30, 30, 30 },
            new[] { 31, 31, 32, 31, 31, 30, 30, 30, 29, 30, 30, 30 },
            new[] { 31, 31, 32, 31, 31, 31, 30, 29, 30, 30, 30, 30 },
            new[] { 30, 31, 32, 32, 31, 30, 30, 29, 30, 29, 30, 30 },
            new[] { 31, 32, 31, 32, 31, 30, 30, 30, 29, 30, 30, 30 },
            new[] { 31, 31, 32, 31, 31, 31, 29, 30, 29, 30, 29, 31 },
            new[] { 31, 31, 32, 31, 31, 31, 30, 29, 29, 30, 30, 30 },
            new[] { 31, 32, 31, 32, 30, 31, 30, 29, 30, 29, 30, 30 }
        };

        static readonly string[] Months =
            { "Baisakh", "Jestha", "Asar", "Shrawan", "Bhadra", "Asoj", "Kartik", "Mangsir", "Poush", "Magh", "Falgun", "Chaitra" };

        static readonly string[] MonthsNepali =
            { "बैशाख", "जेठ", "असार", "साउन", "भदौ", "असोज", "कात्तिक", "मंसिर", "पुस", "माघ", "फागुन", "चैत" };

        static readonly string[] WeekdaysNepali =
            { "आइतबार", "सोमबार", "मंगलबार", "बुधबार", "बिहिबार", "शुक्रबार", "शनिबार" };

        /// <summary>The Bikram Sambat year, month (1 to 12) and day of a date, or null outside the table.</summary>
        public static (int Year, int Month, int Day)? From(DateTime date)
        {
            int days = (date.Date - FirstDay).Days;

            if (days < 0)
                return null;

            for (int y = 0; y < MonthLengths.Length; y++)
                for (int m = 0; m < 12; m++)
                {
                    if (days < MonthLengths[y][m])
                        return (FirstYear + y, m + 1, days + 1);

                    days -= MonthLengths[y][m];
                }

            return null;
        }

        /// <summary>"Kartik 29", or in Nepali "कात्तिक २९"; null outside the table.</summary>
        public static string? MonthDay(DateTime date, bool inNepali) =>
            From(date) is var (_, month, day)
                ? inNepali ? $"{MonthsNepali[month - 1]} {Digits(day)}" : $"{Months[month - 1]} {day}"
                : null;

        /// <summary>"Sunday, Kartik 29", or "आइतबार, कात्तिक २९".</summary>
        public static string? Long(DateTime date, bool inNepali) =>
            MonthDay(date, inNepali) is string monthDay
                ? $"{(inNepali ? WeekdaysNepali[(int)date.DayOfWeek] : date.ToString("dddd"))}, {monthDay}"
                : null;

        /// <summary>Numbers in Devanagari digits: 29 → २९.</summary>
        public static string Digits(int number) =>
            new(number.ToString().Select(c => (char)('०' + (c - '0'))).ToArray());
    }
}
