#nullable disable
#pragma warning disable CS1591

namespace MediaBrowser.Model.LiveTv
{
    public class TunerHostInfo
    {
        public TunerHostInfo()
        {
            AllowHWTranscoding = true;
            IgnoreDts = true;
            ReadAtNativeFramerate = false;
            AllowStreamSharing = true;
            AllowFmp4TranscodingContainer = false;
            FallbackMaxStreamingBitrate = 30000000;
        }

        public string Id { get; set; }

        public string Url { get; set; }

        public string Type { get; set; }

        public string DeviceId { get; set; }

        public string FriendlyName { get; set; }

        public bool ImportFavoritesOnly { get; set; }

        public bool AllowHWTranscoding { get; set; }

        public bool AllowFmp4TranscodingContainer { get; set; }

        public bool AllowStreamSharing { get; set; }

        public int FallbackMaxStreamingBitrate { get; set; }

        public bool EnableStreamLooping { get; set; }

        public string Source { get; set; }

        public int TunerCount { get; set; }

        public string UserAgent { get; set; }

        public bool IgnoreDts { get; set; }

        public bool ReadAtNativeFramerate { get; set; }

        /// <summary>
        /// Gets or sets the FFmpeg analyze duration in milliseconds for this tuner.
        /// Lower values speed up tuning but may cause stream detection issues.
        /// When null, the global setting or FFmpeg default is used.
        /// </summary>
        public int? AnalyzeDurationMs { get; set; }

        /// <summary>
        /// Gets or sets the FFmpeg probe size in bytes for this tuner.
        /// Lower values speed up initial stream detection.
        /// When null, the global setting or FFmpeg default is used.
        /// </summary>
        public int? ProbeSizeBytes { get; set; }

        /// <summary>
        /// Gets or sets the maximum output delay in microseconds for live HLS streams.
        /// Lower values reduce the time before the first segment is available.
        /// When null, the default of 5000000 (5 seconds) is used.
        /// </summary>
        public int? MaxDelayUs { get; set; }

        /// <summary>
        /// Gets or sets additional output format flags for live streams.
        /// Example: "+flush_packets+nobuffer" for lower latency.
        /// </summary>
        public string OutputFFlags { get; set; }

        /// <summary>
        /// Gets or sets the HLS segment length in seconds for this tuner.
        /// Shorter segments reduce initial tune time but increase overhead.
        /// When null, the client-requested or default segment length is used.
        /// </summary>
        public int? SegmentLength { get; set; }
    }
}
