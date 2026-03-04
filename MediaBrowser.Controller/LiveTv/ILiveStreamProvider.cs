using MediaBrowser.Controller.Library;

namespace MediaBrowser.Controller.LiveTv;

/// <summary>
/// Provides live stream pooling and caching for fast channel switching.
/// Implemented by plugins to keep tuner connections alive in a pool so that
/// re-tuning a recently-watched channel skips the connection setup delay.
/// </summary>
/// <remarks>
/// <para>
/// <b>Adoption flow:</b> When <c>MediaSourceManager.CloseLiveStream</c>
/// is about to close a stream (ConsumerCount reaches 0), it offers the stream to
/// registered <see cref="ILiveStreamProvider"/> instances. If a provider adopts it,
/// the stream stays alive in <c>_openStreams</c> with ConsumerCount bumped to 1.
/// </para>
/// <para>
/// <b>Reuse flow:</b> When a new client opens the same channel,
/// <c>DefaultLiveTvService.GetChannelStreamWithDirectStreamProvider</c> searches
/// <c>currentLiveStreams</c> by <c>OriginalStreamId</c>. Since the adopted
/// stream is still in <c>_openStreams</c>, it is found and reused automatically
/// via existing stream sharing (no explicit lookup hook required).
/// </para>
/// <para>
/// <b>Eviction:</b> The provider is responsible for eventually calling
/// <c>IMediaSourceManager.CloseLiveStream(id)</c> when evicting a stream
/// (e.g., on idle timeout or LRU eviction). This decrements ConsumerCount
/// and closes the tuner connection.
/// </para>
/// </remarks>
public interface ILiveStreamProvider
{
    /// <summary>
    /// Checks if a cached live stream exists for the given media source.
    /// </summary>
    /// <param name="mediaSourceId">The media source ID (channel identity).</param>
    /// <param name="liveStream">The cached live stream if available.</param>
    /// <returns>True if a cached stream is available for the given media source.</returns>
    bool TryGetStream(string mediaSourceId, out ILiveStream? liveStream);

    /// <summary>
    /// Offers a live stream to the provider when its consumer count reaches zero
    /// and it is about to be closed. If the provider adopts the stream, the caller
    /// bumps ConsumerCount back to 1 and does NOT close or remove the stream.
    /// </summary>
    /// <param name="id">The live stream ID (the key in MediaSourceManager's open streams dictionary).</param>
    /// <param name="liveStream">The live stream about to be closed.</param>
    /// <returns>True if the provider adopted the stream; false to proceed with normal closure.</returns>
    bool TryAdoptStream(string id, ILiveStream liveStream);
}
