# Consumer Tracking Fix for Warm Pool

## Problem
When a warm HIT is served to a client (cached/pre-buffered FFmpeg process), the client receives the playlist and begins requesting segments. However, the current plugin implementation has `ConsumerCount == 0` for the warm process, making it eligible for eviction by the idle timeout or LRU logic. If the process is evicted while the client is actively consuming segments, playback freezes after ~10 seconds.

## Root Cause
In v1.7.0, the plugin removed the `ConsumerCount++` from `TryGetPlaylistPath()` because it was causing a "lock-up" where entries became permanently unevictable (ConsumerCount never decremented properly). The fix was to rely solely on `LastAccessTime` updates for eviction protection, but this is insufficient because:

1. `LastAccessTime` gets updated in `TryGetPlaylistPath()`
2. But the idle/LRU eviction logic may still consider the process eligible for eviction
3. By the time eviction runs, the client may have already received the playlist and started polling for segments
4. Process gets evicted while client is actively consuming → playback freezes

## Solution: Two-Phase Consumer Lifecycle

### Phase 1: Server-Side (Jellyfin Core) — COMPLETED ✓
- **Commit**: `002b00fd6` (feature/fastchannelzapping branch)
- **Changes**:
  1. Added `NotifyPlaylistConsumer(mediaSourceId, encodingProfile, playSessionId)` method to `IHlsPlaylistProvider` interface
  2. Updated `DynamicHlsController.GetLiveHlsStream()` to call `NotifyPlaylistConsumer()` AFTER a successful warm HIT
  3. The method is called BEFORE returning the playlist content to the client

### Phase 2: Plugin-Side Implementation — COMPLETED (plugin v1.14.2)

#### 2a. Update WarmProcessProvider.cs

Implement the new `NotifyPlaylistConsumer()` method:

```csharp
public void NotifyPlaylistConsumer(string mediaSourceId, EncodingProfile encodingProfile, string? playSessionId)
{
    var pool = EnsurePool();
    pool.IncrementConsumerCount(mediaSourceId, encodingProfile, playSessionId);
}
```

#### 2b. Add IncrementConsumerCount() to WarmFFmpegProcessPool.cs

New public method that increments ConsumerCount for the matching pool entry:

```csharp
/// <summary>
/// Increments the consumer count for a warm process.
/// Called when the server is serving a warm HIT to a client.
/// The plugin must decrement this count in response to PlaybackStopped or SessionEnded events.
/// </summary>
public void IncrementConsumerCount(string mediaSourceId, EncodingProfile encodingProfile, string? playSessionId)
{
    var encodingProfileHash = encodingProfile.ComputeHash();
    var poolKey = GetPoolKey(mediaSourceId, encodingProfileHash);

    if (_warmProcesses.TryGetValue(poolKey, out var warmProcess))
    {
        lock (_consumerCountLock)
        {
            warmProcess.ConsumerCount++;
        }
        _logger.LogDebug(
            "[WarmPool] Incremented consumer count for {MediaSourceId} profile {Profile}: {Count}",
            mediaSourceId,
            encodingProfile.ToString(),
            warmProcess.ConsumerCount);
    }
}
```

#### 2c. Ensure DecrementConsumerCount() is properly wired in PlaybackStopped

The plugin already tracks `PlaybackStopped` events in `WarmPoolEntryPoint`. For warm playlist HITs, the same `mediaSourceId` will appear in the `PlaybackStopEventArgs`. The logic should be:

```csharp
private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
{
    // ... existing logic ...

    var pool = WarmPoolManager.ProcessPoolInstance;
    if (pool is null)
    {
        return;
    }

    if (!string.IsNullOrEmpty(e.PlaySessionId))
    {
        if (!pool.TryDecrementConsumerByPlaySession(e.PlaySessionId))
        {
            pool.DecrementAllConsumersForMediaSource(e.MediaSourceId);
        }
    }
    else if (!string.IsNullOrEmpty(e.MediaSourceId))
    {
        // Decrement all pool entries matching this mediaSourceId.
        // This handles the case where a client switched encoding profiles
        // (unlikely but possible) or where the same channel has multiple variants.
        WarmPoolManager.ProcessPoolInstance?.DecrementAllConsumersForMediaSource(e.MediaSourceId);
    }

    // ... rest of existing logic ...
}
```

#### 2d. Add DecrementConsumerCount() and DecrementAllConsumersForMediaSource() to WarmFFmpegProcessPool.cs

```csharp
/// <summary>
/// Decrements the consumer count for a warm process.
/// Called when a client stops consuming a warm playlist.
/// </summary>
public void DecrementConsumerCount(string mediaSourceId, EncodingProfile encodingProfile)
{
    var encodingProfileHash = encodingProfile.ComputeHash();
    var poolKey = GetPoolKey(mediaSourceId, encodingProfileHash);

    if (_warmProcesses.TryGetValue(poolKey, out var warmProcess))
    {
        lock (_consumerCountLock)
        {
            if (warmProcess.ConsumerCount > 0)
            {
                warmProcess.ConsumerCount--;
            }
        }
        _logger.LogDebug(
            "[WarmPool] Decremented consumer count for {MediaSourceId} profile {Profile}: {Count}",
            mediaSourceId,
            encodingProfile.ToString(),
            warmProcess.ConsumerCount);
    }
}

/// <summary>
/// Decrements all consumer counts for a given media source ID.
/// Used when PlaybackStopped fires with a mediaSourceId but we don't know the exact encoding profile.
/// This handles multiple concurrent clients with different encoding profiles on the same channel.
/// </summary>
public void DecrementAllConsumersForMediaSource(string mediaSourceId)
{
    var decremented = 0;
    foreach (var kvp in _warmProcesses)
    {
        var (key, process) = kvp;

        // key is "mediaSourceId|encodingProfileHash"
        var parts = key.Split('|');
        if (parts.Length == 2 && parts[0] == mediaSourceId)
        {
            lock (_consumerCountLock)
            {
                if (process.ConsumerCount > 0)
                {
                    process.ConsumerCount--;
                    decremented++;
                }
            }
        }
    }

    if (decremented > 0)
    {
        _logger.LogDebug(
            "[WarmPool] Decremented {Count} consumer(s) for mediaSourceId {MediaSourceId}",
            decremented,
            mediaSourceId);
    }
}
```

#### 2e. Verify Eviction Logic Respects ConsumerCount

The idle timeout and LRU eviction logic should already check `ConsumerCount > 0` before evicting. Verify in `CheckIdleProcessesAsync()` and `EvictLeastValuableProcess()`:

```csharp
// Example from idle timeout logic (should already be there)
if (warmProcess.ConsumerCount > 0)
{
    _logger.LogDebug("[WarmPool] Not evicting {Key}: active consumer", poolKey);
    continue;  // Skip this entry
}
```

## Testing Checklist

1. **Warm HIT consumer increment**:
   - Start warm process
   - Client requests playlist (warm HIT)
   - Verify `ConsumerCount > 0` in pool status API
   - Verify log shows "Incremented consumer count"

2. **Consumer decrement on PlaybackStopped**:
   - Stop playback
   - Verify `ConsumerCount` decrements back to 0
   - Verify log shows "Decremented consumer count"

3. **Eviction protection while consuming**:
   - Start warm process with client consuming
   - Verify process is NOT evicted even if idle timer runs
   - Verify log shows "Not evicting: active consumer"

4. **Eviction allowed after consumer stops**:
   - Stop playback
   - Let idle timeout run
   - Verify process IS evicted (if idle time threshold exceeded)
   - Verify log shows "Evicted idle process"

5. **End-to-end playback**:
   - Client tunes channel (cold start)
   - FFmpeg starts, segments generated
   - DynamicHlsController offers process to warm pool → adopted
   - Client requests more segments without switching channels (warm HIT) → Playlist served
   - Consumer count incremented → process protected from eviction
   - Client plays for extended time (30+ seconds) → should NOT freeze
   - Client stops playback → consumer count decremented
   - Verify playback is smooth throughout

## Implementation Order (Completed)

1. Implement `NotifyPlaylistConsumer()` in `WarmProcessProvider`
2. Add `IncrementConsumerCount()` to `WarmFFmpegProcessPool`
3. Add `DecrementConsumerCount()` and `DecrementAllConsumersForMediaSource()` to `WarmFFmpegProcessPool`
4. Update `OnPlaybackStopped()` in `WarmPoolEntryPoint` to call `DecrementAllConsumersForMediaSource()`
5. Verify idle/LRU eviction logic respects `ConsumerCount > 0`
6. Test end-to-end

## Future Enhancement

To avoid the "decrement all for mediaSourceId" heuristic, the server could be extended to pass the exact encoding profile through `PlaybackStopEventArgs`. This would allow precise consumer tracking per encoding variant. For now, the `DecrementAllConsumersForMediaSource()` approach is acceptable since it's unlikely multiple encoding profiles are simultaneously consuming the same channel.

## Related Files

- **Server**: `Jellyfin.Api/Controllers/DynamicHlsController.cs` (line ~336)
- **Server**: `MediaBrowser.Controller/LiveTv/IHlsPlaylistProvider.cs`
- **Plugin**: `WarmProcessProvider.cs`
- **Plugin**: `WarmFFmpegProcessPool.cs`
- **Plugin**: `WarmPoolEntryPoint.cs`

