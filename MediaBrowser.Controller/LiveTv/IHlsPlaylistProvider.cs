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
    /// <param name="context">The playlist request context.</param>
    /// <param name="playlistPath">The path to the playlist file on disk, if available.</param>
    /// <returns>True if a playlist is available and the playlist exists; false otherwise.</returns>
    bool TryGetPlaylist(HlsPlaylistRequestContext context, out string? playlistPath);

    /// <summary>
    /// Offers a running FFmpeg process to the provider when a transcode is about
    /// to be killed. If the provider adopts the process, the caller must NOT kill it,
    /// delete its files, or close the live stream — the provider takes full ownership.
    /// The provider is responsible for eventually calling
    /// <c>IMediaSourceManager.CloseLiveStream(liveStreamId)</c> when evicting the process.
    /// </summary>
    /// <param name="context">The process adoption context.</param>
    /// <returns>True if the provider adopted the process; false to proceed with normal kill.</returns>
    bool TryAdoptProcess(HlsProcessAdoptionContext context);

    /// <summary>
    /// Tries to get an HLS playlist and publish it to the target location.
    /// This method encapsulates the entire playlist workflow: detection, file reading,
    /// publishing to the transcoding directory, and logging.
    /// </summary>
    /// <param name="context">The playlist request context.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task containing the playlist content if a playlist was found and published; null otherwise.</returns>
    Task<string?> TryGetPlaylistContentAsync(
        HlsPlaylistRequestContext context,
        CancellationToken cancellationToken);

    /// <summary>
    /// Notifies the provider that a playlist is about to be served to a client.
    /// The provider should increment its consumer count to prevent eviction while the client
    /// actively consumes segments. The server will call this BEFORE returning the playlist
    /// to the client.
    /// </summary>
    /// <param name="context">The playlist request context.</param>
    /// <remarks>
    /// The provider is responsible for decrementing the consumer count when the stream ends
    /// (via PlaybackStopped events) or when a session ends (SessionEnded events).
    /// </remarks>
    void NotifyPlaylistConsumer(HlsPlaylistRequestContext context);
}
