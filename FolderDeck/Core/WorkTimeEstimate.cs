using System;
using System.Globalization;

namespace FolderDeck.Core
{
    public static class WorkTimeEstimate
    {
        // A null result means insufficient information, not zero remaining time.
        public static double? RemainingSeconds(double elapsed, double completed, double total, double previousSeconds = 0)
        {
            if (!Finite(elapsed) || elapsed < 0 || !Finite(completed) || !Finite(total)) return null;
            if (total > 0 && completed >= total) return 0;
            if (total > 0 && completed > 0 && elapsed >= 2)
            {
                double remaining = elapsed * ((total - completed) / completed);
                return Finite(remaining) && remaining >= 0 ? (double?)remaining : null;
            }
            if (total <= 0 && Finite(previousSeconds) && previousSeconds > elapsed)
                return previousSeconds - elapsed;
            return null;
        }

        public static string Duration(double seconds)
        {
            if (!Finite(seconds) || seconds < 0) return "—";
            if (seconds < 1) return "1秒未満";
            // Keep arbitrarily large estimates away from TimeSpan overflow.
            double days = Math.Floor(seconds / 86400);
            if (days >= 10000) return "10000日以上";
            var value = TimeSpan.FromSeconds(Math.Ceiling(seconds));
            return value.Days > 0
                ? value.Days.ToString(CultureInfo.InvariantCulture) + "日 " + value.ToString(@"hh\:mm\:ss")
                : value.ToString(value.TotalHours >= 1 ? @"hh\:mm\:ss" : @"mm\:ss");
        }

        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
    }
}
