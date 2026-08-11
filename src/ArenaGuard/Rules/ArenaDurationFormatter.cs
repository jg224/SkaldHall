using System;
using System.Globalization;

namespace ArenaGuard.Rules
{
    internal static class ArenaDurationFormatter
    {
        internal static string FormatHms(long milliseconds)
        {
            long totalSeconds = Math.Max(0L, milliseconds) / 1000L;
            long hours = totalSeconds / 3600L;
            long minutes = totalSeconds / 60L % 60L;
            long seconds = totalSeconds % 60L;
            return hours.ToString(CultureInfo.InvariantCulture) + ":" +
                   minutes.ToString("00", CultureInfo.InvariantCulture) + ":" +
                   seconds.ToString("00", CultureInfo.InvariantCulture);
        }
    }
}
