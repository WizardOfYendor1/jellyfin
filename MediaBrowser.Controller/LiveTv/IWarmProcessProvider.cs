namespace MediaBrowser.Controller.LiveTv;

/// <summary>
/// Provides warm (pre-spawned) FFmpeg processes for fast LiveTV channel zapping.
/// Implemented by plugins to supply pre-buffered HLS playlists.
/// </summary>
public interface IWarmProcessProvider
{
    /// <summary>
    /// Tries to get a warm HLS playlist for the specified media source.
    /// </summary>
    /// <param name="mediaSourceId">The media source ID (typically channel ID for LiveTV).</param>
    /// <param name="playlistPath">The path to the warm playlist file on disk, if available.</param>
    /// <returns>True if a warm process is available and the playlist exists; false otherwise.</returns>
    bool TryGetWarmPlaylist(string mediaSourceId, out string? playlistPath);
}
