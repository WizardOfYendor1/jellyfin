using System.Diagnostics;

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
    /// <param name="mediaSourceId">The media source ID (MD5 hash of stream URL for LiveTV).</param>
    /// <param name="playlistPath">The path to the warm playlist file on disk, if available.</param>
    /// <returns>True if a warm process is available and the playlist exists; false otherwise.</returns>
    bool TryGetWarmPlaylist(string mediaSourceId, out string? playlistPath);

    /// <summary>
    /// Offers a running FFmpeg process to the warm pool when a LiveTV transcode is about
    /// to be killed. If the provider adopts the process, the caller must NOT kill it,
    /// delete its files, or close the live stream — the provider takes full ownership.
    /// </summary>
    /// <param name="mediaSourceId">The media source ID.</param>
    /// <param name="playlistPath">The HLS playlist path on disk.</param>
    /// <param name="ffmpegProcess">The running FFmpeg process.</param>
    /// <returns>True if the provider adopted the process; false to proceed with normal kill.</returns>
    bool TryAdoptProcess(string mediaSourceId, string playlistPath, Process ffmpegProcess);
}
