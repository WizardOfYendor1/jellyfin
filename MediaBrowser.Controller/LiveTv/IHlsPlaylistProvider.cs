using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Controller.LiveTv;

/// <summary>
/// Provides HLS playlists for streaming requests.
/// Implemented by plugins to supply pre-buffered or cached HLS playlists.
/// </summary>
public interface IHlsPlaylistProvider
{
    /// <summary>
    /// Tries to get an HLS playlist for the specified media source and encoding profile.
    /// </summary>
    /// <param name="mediaSourceId">The media source ID (MD5 hash of stream URL for LiveTV).</param>
    /// <param name="encodingProfile">The encoding parameters (codec, bitrate, resolution) requested by the client.</param>
    /// <param name="playlistPath">The path to the playlist file on disk, if available.</param>
    /// <returns>True if a playlist is available and the playlist exists; false otherwise.</returns>
    bool TryGetPlaylist(string mediaSourceId, EncodingProfile encodingProfile, out string? playlistPath);

    /// <summary>
    /// Offers a running FFmpeg process to the provider when a transcode is about
    /// to be killed. If the provider adopts the process, the caller must NOT kill it,
    /// delete its files, or close the live stream — the provider takes full ownership.
    /// The provider is responsible for eventually calling
    /// <c>IMediaSourceManager.CloseLiveStream(liveStreamId)</c> when evicting the process.
    /// </summary>
    /// <param name="mediaSourceId">The media source ID.</param>
    /// <param name="encodingProfile">The encoding parameters of the process being offered.</param>
    /// <param name="playlistPath">The HLS playlist path on disk.</param>
    /// <param name="ffmpegProcess">The running FFmpeg process.</param>
    /// <param name="liveStreamId">The live stream ID for the underlying tuner connection (e.g., SharedHttpStream).
    /// The provider must close this when it eventually stops the process.</param>
    /// <returns>True if the provider adopted the process; false to proceed with normal kill.</returns>
    bool TryAdoptProcess(string mediaSourceId, EncodingProfile encodingProfile, string playlistPath, Process ffmpegProcess, string? liveStreamId);

    /// <summary>
    /// Tries to get an HLS playlist and publish it to the target location.
    /// This method encapsulates the entire playlist workflow: detection, file reading,
    /// publishing to the transcoding directory, and logging.
    /// </summary>
    /// <param name="mediaSourceId">The media source ID.</param>
    /// <param name="encodingProfile">The encoding parameters requested by the client.</param>
    /// <param name="targetPlaylistPath">The absolute path where the server expects the playlist file to exist.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task containing the playlist content if a playlist was found and published; null otherwise.</returns>
    Task<string?> TryGetPlaylistContentAsync(
        string mediaSourceId,
        EncodingProfile encodingProfile,
        string targetPlaylistPath,
        CancellationToken cancellationToken);

    /// <summary>
    /// Notifies the provider that a playlist is about to be served to a client.
    /// The provider should increment its consumer count to prevent eviction while the client
    /// actively consumes segments. The server will call this BEFORE returning the playlist
    /// to the client.
    /// </summary>
    /// <param name="mediaSourceId">The media source ID.</param>
    /// <param name="encodingProfile">The encoding parameters of the playlist being served.</param>
    /// <remarks>
    /// The provider is responsible for decrementing the consumer count when the stream ends
    /// (via PlaybackStopped events) or when a session ends (SessionEnded events).
    /// </remarks>
    void NotifyPlaylistConsumer(string mediaSourceId, EncodingProfile encodingProfile);
}
