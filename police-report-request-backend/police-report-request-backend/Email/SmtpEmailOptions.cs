namespace police_report_request_backend.Email
{
    public sealed class SmtpEmailOptions
    {
        // SMTP server
        public string Host { get; set; } = "";
        public int Port { get; set; } = 25;
        public bool UseSsl { get; set; } = false;

        // Optional auth
        public string? Username { get; set; }
        public string? Password { get; set; }

        // Required sender
        public string From { get; set; } = "";   // e.g., "no-reply@metro.net"

        // Optional list (comma/semicolon separated)
        public string? OpsTo { get; set; }       // e.g., "ops@metro.net, other@metro.net"
    }
}
