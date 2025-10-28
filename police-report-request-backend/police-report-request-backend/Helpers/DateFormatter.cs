using System;
using System.Globalization;

namespace police_report_request_backend.Helpers
{
    public static class DateFormatter
    {
        public static string ToFriendlyPacificTime(DateTime utcDate)
        {
            try
            {
                // Use the standard IANA time zone ID for cross-platform compatibility
                var pacificZone = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
                var pacificTime = TimeZoneInfo.ConvertTimeFromUtc(utcDate, pacificZone);

                // Get the correct abbreviation (PDT/PST)
                var abbreviation = pacificZone.IsDaylightSavingTime(pacificTime) ? "PDT" : "PST";

                // Format the string into a more readable version
                return $"{pacificTime:MMMM d, yyyy 'at' h:mm tt} {abbreviation}";
            }
            catch (TimeZoneNotFoundException)
            {
                // Fallback if the time zone isn't found on the server
                return utcDate.ToString("f") + " UTC";
            }
        }

        // Overload to handle date strings directly from the JSON
        public static string? ToFriendlyPacificTime(string? isoDateString)
        {
            if (string.IsNullOrWhiteSpace(isoDateString)) return isoDateString;

            // Try to parse the string into a DateTime object
            if (DateTime.TryParse(isoDateString, null, DateTimeStyles.AdjustToUniversal, out var utcDate))
            {
                // If successful, use our other helper to format it
                return ToFriendlyPacificTime(utcDate);
            }

            // If parsing fails, return the original string
            return isoDateString;
        }
    }
}