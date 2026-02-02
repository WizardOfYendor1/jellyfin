using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Controller.LiveTv;

/// <summary>
/// Provides the ability to release live TV tuner resources on demand.
/// When all tuners for a host are in use and a new tune request arrives,
/// registered providers are asked to release non-essential resources
/// (e.g., cached streams, prefetched connections) so the request can proceed.
/// </summary>
public interface ITunerResourceProvider
{
    /// <summary>
    /// Releases one non-essential tuner resource, if available.
    /// Called outside any stream locks, so implementations may safely call
    /// <see cref="Library.IMediaSourceManager.CloseLiveStream(string)"/> during execution.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if a tuner resource was released; false otherwise.</returns>
    Task<bool> TryReleaseTunerResourceAsync(CancellationToken cancellationToken);
}
