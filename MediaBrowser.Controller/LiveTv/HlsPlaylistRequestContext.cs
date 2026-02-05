using System;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Model.Dto;

namespace MediaBrowser.Controller.LiveTv;

/// <summary>
/// Provides contextual information about an HLS playlist request.
/// </summary>
public sealed class HlsPlaylistRequestContext
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
    /// Gets the absolute path where the server expects the playlist file to exist.
    /// </summary>
    public string TargetPlaylistPath { get; init; } = string.Empty;

    /// <summary>
    /// Gets the streaming request, if available.
    /// </summary>
    public StreamingRequestDto? Request { get; init; }

    /// <summary>
    /// Gets the stream state, if available.
    /// </summary>
    public StreamState? StreamState { get; init; }

    /// <summary>
    /// Gets the media source info, if available.
    /// </summary>
    public MediaSourceInfo? MediaSource { get; init; }

    /// <summary>
    /// Gets the play session ID, if provided by the client.
    /// </summary>
    public string? PlaySessionId { get; init; }

    /// <summary>
    /// Gets the device ID, if provided by the client.
    /// </summary>
    public string? DeviceId { get; init; }

    /// <summary>
    /// Gets the user ID, if available.
    /// </summary>
    public Guid? UserId { get; init; }

    /// <summary>
    /// Gets the user agent string, if available.
    /// </summary>
    public string? UserAgent { get; init; }

    /// <summary>
    /// Gets the client name, if available.
    /// </summary>
    public string? Client { get; init; }

    /// <summary>
    /// Gets the client version, if available.
    /// </summary>
    public string? ClientVersion { get; init; }

    /// <summary>
    /// Gets the device name, if available.
    /// </summary>
    public string? DeviceName { get; init; }

    /// <summary>
    /// Gets the remote IP address as a string, if available.
    /// </summary>
    public string? RemoteIp { get; init; }
}
