using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Dlna;

namespace MediaBrowser.Controller.LiveTv;

/// <summary>
/// Defines a mechanism for warm stream providers to publish their playlists to a target location.
/// Implement this interface in addition to <see cref="IWarmProcessProvider"/> if your plugin
/// needs to control how the warm playlist file is written to the server's transcoding directory.
/// </summary>
public interface ICustomPlaylistPublisher
{
    /// <summary>
    /// Tries to publish the warm playlist to the specified target path.
    /// This allows the provider to handle atomic writes, file permissions, or copy logic
    /// specific to its storage implementation.
    /// </summary>
    /// <param name="mediaSourceId">The media source ID.</param>
    /// <param name="encodingProfile">The encoding parameters requested.</param>
    /// <param name="targetPlaylistPath">The absolute path where the server expects the playlist file to exist.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task containing true if the playlist was successfully published; otherwise false.</returns>
    Task<bool> TryPublishPlaylistAsync(
        string mediaSourceId,
        EncodingProfile encodingProfile,
        string targetPlaylistPath,
        CancellationToken cancellationToken);
}
