using System.Diagnostics;
using MediaBrowser.Model.Dto;

namespace MediaBrowser.Controller.LiveTv;

/// <summary>
/// Provides contextual information when offering an HLS process for adoption.
/// </summary>
public sealed class HlsProcessAdoptionContext
{
    /// <summary>
    /// Gets the media source ID (MD5 hash of stream URL for LiveTV).
    /// </summary>
    public string MediaSourceId { get; init; } = string.Empty;

    /// <summary>
    /// Gets the encoding parameters (codec, bitrate, resolution) requested by the client.
    /// </summary>
    public EncodingProfile EncodingProfile { get; init; } = null!;

    /// <summary>
    /// Gets the HLS playlist path on disk.
    /// </summary>
    public string PlaylistPath { get; init; } = string.Empty;

    /// <summary>
    /// Gets the running FFmpeg process.
    /// </summary>
    public Process FfmpegProcess { get; init; } = null!;

    /// <summary>
    /// Gets the live stream ID for the underlying tuner connection, if available.
    /// </summary>
    public string? LiveStreamId { get; init; }

    /// <summary>
    /// Gets the play session ID, if provided by the client.
    /// </summary>
    public string? PlaySessionId { get; init; }

    /// <summary>
    /// Gets the device ID, if provided by the client.
    /// </summary>
    public string? DeviceId { get; init; }

    /// <summary>
    /// Gets the media source info, if available.
    /// </summary>
    public MediaSourceInfo? MediaSource { get; init; }
}
