using System;

namespace MythBlazor.Utils
{
    public static class DateTimeHelpers
    {
        public static string ToLocalDateString(DateTimeOffset? dto)
            => dto.HasValue ? dto.Value.ToLocalTime().ToString("d") : string.Empty;

        public static string ToLocalDateTimeString(DateTimeOffset? dto)
            => dto.HasValue ? dto.Value.ToLocalTime().ToString("g") : string.Empty;

        public static string ToLocalTimeString(DateTimeOffset? dto)
            => dto.HasValue ? dto.Value.ToLocalTime().ToString("t") : string.Empty;

        public static string ToLocalTimeRange(DateTimeOffset? start, DateTimeOffset? end)
        {
            if (!start.HasValue && !end.HasValue) return string.Empty;
            if (!start.HasValue) return ToLocalTimeString(end);
            if (!end.HasValue) return ToLocalTimeString(start);
            return $"{ToLocalTimeString(start)} — {ToLocalTimeString(end)}";
        }

        public static string FormatDurationMinutes(DateTimeOffset? start, DateTimeOffset? end)
        {
            if (!start.HasValue || !end.HasValue) return "-";
            var span = end.Value - start.Value;
            return $"{(int)span.TotalMinutes}m";
        }
    }
}