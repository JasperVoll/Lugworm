using System;
using System.Net.Http;

namespace Lugworm.Core.Duet
{
    /// <summary>Connection settings for one Duet board. Host is an IP or hostname
    /// (no scheme); everything else has lab-safe defaults.</summary>
    public sealed class DuetClientOptions
    {
        public string Host = "";
        /// <summary>RRF default password. An empty panel input should map to this.</summary>
        public string Password = "reprap";
        public TimeSpan RequestTimeout = TimeSpan.FromSeconds(4);
        public TimeSpan UploadTimeout = TimeSpan.FromSeconds(60);
        /// <summary>Retries for 503 buffer-full responses (exponential backoff).</summary>
        public int MaxRetries = 3;
        public int RetryBaseDelayMs = 250;
        /// <summary>Test seam: inject a fake handler; null = real HttpClientHandler.</summary>
        public HttpMessageHandler? Handler;
    }
}
