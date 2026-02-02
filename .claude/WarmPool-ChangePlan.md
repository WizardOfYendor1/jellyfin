# Warm Pool — Plan for Changes

This document reviews the current warm pool implementation across both repositories and proposes a plan for improvements. The guiding principle is: **plugin-first** — minimize changes to Jellyfin core/server, keep logic in the plugin.

## Development Status

**Phase 1 (Encoding Parameter Matching) - COMPLETED** ✓

- Created `EncodingProfile` class in `MediaBrowser.Controller/LiveTv/EncodingProfile.cs`
- Extended `IWarmProcessProvider` interface with encoding profile parameters
- Updated `DynamicHlsController` to compute and pass encoding profile on warm pool check
- Updated `TranscodeManager` to store encoding profile in `TranscodingJob` and pass on adoption
- Updated plugin `WarmProcessProvider`, `WarmProcessInfo`, and `WarmFFmpegProcessPool` to key by composite `mediaSourceId + encodingProfileHash`
- All changes compile successfully with 0 errors
- Warm pool now ensures matches only occur when both channel AND encoding parameters match

**Phase 2 (Pool Management) - COMPLETED** ✓

- **2a: Idle timeout eviction** — Added background `Timer` to `WarmFFmpegProcessPool` that fires every 60 seconds, scans for entries where `(UtcNow - LastAccessTime) > IdleTimeoutMinutes` with `ConsumerCount <= 0`, and evicts them (kills FFmpeg, cleans files, closes live stream). Also cleans up dead/crashed processes. Uses configurable `IdleTimeoutMinutes` from `PluginConfiguration` (default 10 min).
- **2b: LRU eviction on pool full** — `AdoptProcess()` now performs LRU eviction when pool is full instead of declining. Finds the entry with the oldest `LastAccessTime` and `ConsumerCount <= 0`, evicts it synchronously (kills FFmpeg, cleans files, fires async live stream close), then adopts the new process. Only declines adoption when all entries have active consumers.
- **2c: Consumer counting** — `ConsumerCount` is incremented on warm hits and used for eviction protection (entries with active consumers are never evicted by idle timeout or LRU). Idle timeout serves as the primary eviction mechanism.
- Implemented `IDisposable` on `WarmFFmpegProcessPool` for timer cleanup
- All changes compile successfully with 0 errors, 0 warnings

**Phase 3 (Direct Stream Warm Pool) - COMPLETED** ✓

- **3a: IWarmStreamProvider interface** — Created `MediaBrowser.Controller/LiveTv/IWarmStreamProvider.cs` with `TryGetWarmStream(mediaSourceId, out ILiveStream?)` and `TryAdoptStream(id, ILiveStream)`. Includes full XML documentation covering adoption flow, reuse flow via stream sharing, and eviction responsibilities.
- **3b: Adoption hook in CloseLiveStream** — Modified `MediaSourceManager.CloseLiveStream` to iterate `IEnumerable<IWarmStreamProvider>` when `ConsumerCount <= 0`. If a provider adopts the stream, ConsumerCount is bumped back to 1 and the stream stays alive in `_openStreams`. Added constructor injection of `IEnumerable<IWarmStreamProvider>`.
- **3c: No server change needed** — Existing stream sharing in `DefaultLiveTvService.GetChannelStreamWithDirectStreamProvider` handles reuse automatically. It searches `currentLiveStreams` (which comes from `_openStreams.Values`) by `OriginalStreamId`. Since the warm-adopted stream remains in `_openStreams` with `EnableStreamSharing = true`, it is found and reused when the same channel is tuned again.
- **3d: Plugin WarmStreamPool** — Created `WarmStreamInfo.cs`, `WarmStreamPool.cs`, and `WarmStreamProvider.cs` in the plugin. Pool features: idle timeout eviction (60s timer, configurable `IdleTimeoutMinutes`), LRU eviction on pool full, re-adoption prevention via `_evictingStreamIds` set during eviction-triggered `CloseLiveStream` callbacks. Registered `IWarmStreamProvider` in `PluginServiceRegistrator`. Updated `WarmPoolManager` to manage both process and stream pool singletons.
- All changes compile successfully with 0 errors, 0 warnings (server + plugin)

**Phase 4 (Automatic Pool Management) - COMPLETED** ✓

- **4a: Auto-Warm via Adoption** — Already functional from Phase 1-3. Automatic adoption from TranscodeManager handles the common case: user watches channel, tunes away, FFmpeg adopted, user tunes back, warm hit.
- **4b: Viewing History Tracking** — Created `ViewingHistory.cs` that subscribes to `ISessionManager.PlaybackStart` and `PlaybackStopped` events via `WarmPoolEntryPoint` (`IHostedService`). Tracks per-user recent channel lists, global channel frequency, and sequential transition patterns (A→B). Data persisted to plugin data folder as JSON across server restarts.
- **4c: Predictive Pre-Warming** — Implemented via viewing history pattern detection. `PredictNextChannel()` identifies the most likely next channel from sequential patterns (minimum 2 occurrences). When playback stops, `WarmPoolEntryPoint` boosts the priority of predicted-next channels in both process and stream pools by updating `LastAccessTime`, preventing them from being evicted by LRU or idle timeout.
- **Smart Eviction** — LRU eviction now uses a composite score: `historyPriority - (idleMinutes / 60.0)`. Channels with higher historical view frequency are protected from eviction. This applies to both `WarmFFmpegProcessPool` and `WarmStreamPool`.
- All changes compile successfully with 0 errors, 0 warnings

**Phase 5 (Production Hardening) - COMPLETED** ✓

- **Health checks** — Enhanced dead process detection in the idle timer (already from Phase 2). `WarmFFmpegProcessPool.CheckIdleProcessesAsync()` detects crashed FFmpeg processes, cleans up segment files, and closes live streams. Runs every 60 seconds.
- **Disk space monitoring** — Created `DiskSpaceMonitor.cs` that calculates total warm pool segment disk usage. New `MaxDiskUsageMB` configuration option (default: unlimited). Enforced during adoption (decline if over budget), manual starts (evict oldest first), and periodic health checks (evict oldest when over budget). Reports formatted disk usage in status API.
- **Metrics** — Created `PoolMetrics.cs` with thread-safe `Interlocked` counters: warm hits/misses (process + stream), adoptions/declinations, idle/LRU/dead evictions, manual starts. Computes hit rates for both pools. Integrated into `WarmFFmpegProcessPool`, `WarmStreamPool`, and exposed via `GET /WarmPool/Metrics` API.
- **Admin UI** — Implemented `IHasWebPages` on `Plugin.cs` with embedded HTML config page (`Configuration/config.html`). Dashboard shows: pool configuration editor (save/load), live pool status (process count, stream count, disk usage), performance metrics (hit rates, adoption counts, eviction breakdowns), and viewing history summary (top channels, transition patterns, predicted next channels). REST API expanded with `GET /WarmPool/DetailedStatus`, `GET /WarmPool/Metrics`, `GET /WarmPool/History`, `GET /WarmPool/History/User/{userId}`.
- All changes compile successfully with 0 errors, 0 warnings (server + plugin)

**Phase 7 (Tuner Resource Release) - COMPLETED** ✓

- **Problem**: When all physical tuners are consumed (some by active viewers, some by warm pool entries), new tune requests fail with `LiveTvConflictException`. There was NO mechanism for the warm pool to release tuners on demand — eviction only happened during adoption (pool full) or idle timeout.
- **Root cause**: `MediaSourceManager.OpenLiveStreamInternal` holds `_liveStreamLocker` during the entire tuner host call chain. `CloseLiveStream` also acquires `_liveStreamLocker`. Any resource release calling `CloseLiveStream` from within the lock scope would deadlock.
- **7a: ITunerResourceProvider interface** — New generic interface `MediaBrowser.Controller/LiveTv/ITunerResourceProvider.cs` with `TryReleaseTunerResourceAsync(CancellationToken)`. No warm-pool-specific concepts — any plugin holding tuner resources can implement this.
- **7b: Retry pattern in MediaSourceManager** — `OpenLiveStreamInternal` wraps `OpenLiveStreamInternalCore` with catch-release-retry. When `LiveTvConflictException` propagates out of the `using` block, the lock is released. Providers run OUTSIDE the lock, safely calling `CloseLiveStream`. Retry gets fresh `_openStreams` snapshot reflecting the freed resource. No providers registered → exception propagates immediately (identical to existing behavior).
- **7c: Plugin TunerResourceProvider** — New `TunerResourceProvider.cs` implements `ITunerResourceProvider`. Tries FFmpeg process pool first, then stream pool. Uses same session-aware eviction scoring (orphan penalty, fairness, idle time, history priority).
- **7d: EvictForTunerReleaseAsync** — New async method on both `WarmFFmpegProcessPool` and `WarmStreamPool`. Unlike fire-and-forget eviction during adoption, this method AWAITS `CloseLiveStream` to ensure the tuner is actually freed before returning.
- **Known issue documented**: HEAD request in `M3UTunerHost.GetChannelStream()` causes double TVheadend subscription for extensionless URLs. Harmless (quick unsubscribe) but could be optimized in the future.
- Server changes: 2 files (new interface + retry logic in MediaSourceManager)
- Plugin changes: 4 files (new TunerResourceProvider + EvictForTunerReleaseAsync on both pools + DI registration)
- Plugin version bumped to 1.14.0
- All changes compile successfully with 0 errors, 0 warnings (server + plugin)

**All phases complete.** The warm pool plugin now provides end-to-end fast LiveTV channel zapping with automatic management, metrics, tuner resource release, and administration

**Bug Fix: Redundant Warm Pool Checks (Post-Phase)** ✓

- Moved warm pool provider query inside the `if (!File.Exists(playlistPath))` guard in `DynamicHlsController.GetLiveHlsStream()`
- Previously, every client poll of `live.m3u8` triggered a warm pool check + "MISS" log entry, even while FFmpeg was already running for that session
- Now the warm pool check only runs once per session — when no playlist exists and a cold start (or warm hit) is actually needed
- Eliminates log spam and unnecessary provider queries during active playback

**Bug Fix: Plugin v1.7.0 — Production Testing Fixes** ✓

Three critical bugs found during Android TV transcoding tests with direct play disabled:

1. **ConsumerCount leak** — `TryGetPlaylistPath()` incremented `ConsumerCount` on every warm HIT poll (~every 3s) with no decrement when playback stopped. After a few warm HIT sessions, pool entries became permanently unevictable (ConsumerCount > 0 blocks eviction). Pool would lock up refusing all adoptions ("all entries have active consumers"). **Fix**: Removed `ConsumerCount++` — `LastAccessTime` update is sufficient for LRU/idle protection.

2. **Admin UI refresh buttons broken** — Used inline `onclick` handlers blocked by CSP in Jellyfin's SPA plugin loader. **Fix**: Switched to `addEventListener` bindings like the Save button uses.

3. **Warm HIT log noise** — Every 3s client poll generated 2 Information-level log lines during warm HIT playback (check + HIT message). **Fix**: Track first HIT per pool key, log at Info level only on first HIT, demote subsequent polls to Debug level.

**Testing Results**:

- Cold start: 30+ seconds (FFmpeg startup + buffering)
- Warm HIT: **62ms** from channel open to playlist served (nearly instant)
- History-aware LRU eviction working correctly (kept frequently-watched channels, evicted one-off channels)
- Predictive next-channel protection working (correct predictions, prevented eviction)
- Adoption, ConsumerCount management, and automatic pool population all functioning correctly
- Plugin version: 1.7.0 (2026-02-01)

---

## Core Design Goal

**Eliminate FFmpeg startup latency for LiveTV channel zapping.** When a user tunes a channel, FFmpeg must buffer, probe, and encode several seconds of data before the first HLS segment is ready. This takes 5-10+ seconds. By keeping FFmpeg processes alive in a warm pool — still connected to TVHeadend and continuously processing the mpegts stream — a re-tune to the same channel is nearly instant.

The architecture:

```text
TVHeadend ──(HTTP mpegts)──> FFmpeg (kept alive in warm pool)
                                 │
                                 ├──> HLS segments on disk (continuously written)
                                 │
                             Client tunes channel
                                 │
                                 └──> Immediate HLS playlist served (warm hit)
```

The key insight: **FFmpeg is the bottleneck**, not the TVHeadend connection itself. FFmpeg needs to buffer, probe codec info, and build up enough data to produce output segments. Keeping FFmpeg running on the mpegts stream eliminates this entirely. The TVHeadend connection stays alive because FFmpeg is consuming from it.

---

## Current State Summary

### Server-side (jellyfin, feature/fastchannelzapping branch)

4 commits adding minimal hook interfaces:

1. **`IWarmProcessProvider`** interface in `MediaBrowser.Controller/LiveTv/`:
   - `TryGetWarmPlaylist(mediaSourceId, out playlistPath)` — lookup by `state.MediaSource.Id`
   - `TryAdoptProcess(mediaSourceId, playlistPath, process, liveStreamId)` — adoption

2. **`DynamicHlsController.GetLiveHlsStream()`** — checks warm pool before FFmpeg cold start

3. **`TranscodeManager.KillTranscodingJob()`** — offers process to warm pool before killing; bumps `ConsumerCount` on adoption

### Plugin-side (jellyfin-plugin-warmpool, v1.3.0)

- `WarmProcessProvider` — implements `IWarmProcessProvider`
- `WarmFFmpegProcessPool` — ConcurrentDictionary keyed by `mediaSourceId`
- Automatic adoption from `TranscodeManager` on playback stop
- Proper live stream lifecycle: stores `liveStreamId`, calls `CloseLiveStream()` on eviction
- REST API for manual management (`/WarmPool/Start`, `/Stop`, `/Status`)

---

## Issues and Gaps Identified

### Issue 1: Encoding Parameter Matching (Critical)

**Problem**: The warm pool is keyed only by `mediaSourceId` (MD5 of stream URL). It does NOT consider encoding parameters. Different clients may request different codecs, resolutions, or bitrates for the same channel.

**Example failure scenario:**

1. Client A watches channel 1 with H.264 1080p AAC → FFmpeg adopted into warm pool
2. Client B tunes channel 1 requesting H.265 720p → warm pool returns H.264 1080p playlist → **wrong output**

**Current code** (`DynamicHlsController.cs:315`):

```csharp
var warmSourceId = state.MediaSource.Id;  // Only channel identity, no encoding params
```

**What's needed**: The warm pool key must include encoding parameters so a warm hit only occurs when the channel AND the transcoding profile match. The key should incorporate at minimum: video codec, audio codec, resolution, and bitrate.

**At adoption time** (`TranscodeManager.cs:248`), only `mediaSourceId` and `playlistPath` are passed. The full encoding parameters from `StreamState` are not available to the plugin. The server-side interface needs to be extended.

### Issue 2: Multiple Clients on Same Channel + Same Parameters

**Current behavior**: When two clients request the same channel, Jellyfin's existing `ConsumerCount` pattern on `ILiveStream` allows sharing the TVHeadend HTTP connection (`SharedHttpStream`). However, each client gets its own FFmpeg process because `OutputFilePath = MD5("{MediaPath}-{UserAgent}-{DeviceId}-{PlaySessionId}")` — the `PlaySessionId` is unique per client.

**Implication for warm pool**: When Client A's FFmpeg is adopted into the warm pool, Client B can get a warm hit and read from the same HLS segments on disk. This works correctly today. But when Client B stops and Client C tunes in, the warm pool only holds one entry per `mediaSourceId` — it would serve Client A's original FFmpeg output. This is fine as long as encoding parameters match.

**What's needed**: With encoding-parameter-aware keying, the pool should support multiple entries per channel (one per encoding profile). The key becomes `mediaSourceId + encodingProfileHash`.

### Issue 3: Idle Timeout Not Implemented

`PluginConfiguration.IdleTimeoutMinutes` (default 10) is defined but never used. Warm processes live forever until manually stopped or pool is full.

### Issue 4: No LRU Eviction

When pool is full, `AdoptProcess()` declines. No eviction of oldest/least-used entry. Once pool fills, no new channels can be warmed.

### Issue 5: Consumer Count Tracking Incomplete

`WarmProcessInfo.ConsumerCount` is incremented on warm hit but never decremented. `ReleaseConsumer()` exists but nothing calls it.

### Issue 6: Adopted Playlist Path vs New Client's Expected Path

When FFmpeg is adopted, its `OutputFilePath` was `MD5("{MediaPath}-{UserAgent}-{DeviceId}-{PlaySessionId}")` from the original client. A new client would compute a different `OutputFilePath` (different PlaySessionId, DeviceId, UserAgent). The warm pool bypasses this by returning its own playlist path directly in `TryGetWarmPlaylist()`, which works. But the HLS segment URLs inside that playlist may reference paths that don't match what the new client expects from Jellyfin's routing.

---

## Proposed Changes

### Phase 1: Encoding-Parameter-Aware Matching (Critical Fix)

This is the highest priority because without it, warm hits can serve wrong-format content.

#### 1a. Extend IWarmProcessProvider Interface (Server)

Add encoding parameters to both methods:

```csharp
public interface IWarmProcessProvider
{
    bool TryGetWarmPlaylist(
        string mediaSourceId,
        string? videoCodec,
        string? audioCodec,
        int? videoBitrate,
        int? audioBitrate,
        int? width,
        int? height,
        out string? playlistPath);

    bool TryAdoptProcess(
        string mediaSourceId,
        string playlistPath,
        Process ffmpegProcess,
        string? liveStreamId,
        string? videoCodec,
        string? audioCodec,
        int? videoBitrate,
        int? audioBitrate,
        int? width,
        int? height);
}
```

Alternatively, pass a simple DTO/record to avoid parameter bloat:

```csharp
public record WarmPoolEncodingProfile(
    string? VideoCodec,
    string? AudioCodec,
    int? VideoBitrate,
    int? AudioBitrate,
    int? Width,
    int? Height);

public interface IWarmProcessProvider
{
    bool TryGetWarmPlaylist(string mediaSourceId, WarmPoolEncodingProfile profile, out string? playlistPath);
    bool TryAdoptProcess(string mediaSourceId, string playlistPath, Process ffmpegProcess,
                         string? liveStreamId, WarmPoolEncodingProfile profile);
}
```

**Files**: `MediaBrowser.Controller/LiveTv/IWarmProcessProvider.cs`

#### 1b. Pass Encoding Parameters at Check and Adoption (Server)

In `DynamicHlsController.GetLiveHlsStream()`, build the profile from `StreamState`:

```csharp
var profile = new WarmPoolEncodingProfile(
    state.OutputVideoCodec,
    state.OutputAudioCodec,
    state.OutputVideoBitrate,
    state.OutputAudioBitrate,
    state.OutputWidth,
    state.OutputHeight);
warmProvider.TryGetWarmPlaylist(warmSourceId, profile, out var warmPlaylistPath);
```

In `TranscodeManager.KillTranscodingJob()`, the `TranscodingJob` doesn't currently store encoding params. We need to either:

- Store the encoding profile on `TranscodingJob` when it's created in `OnTranscodeBeginning()`, OR
- Extract it from `job.MediaSource` at kill time

**Files**: `Jellyfin.Api/Controllers/DynamicHlsController.cs`, `MediaBrowser.MediaEncoding/Transcoding/TranscodeManager.cs`

#### 1c. Update Plugin to Key by Channel + Profile (Plugin)

Change the dictionary key from `mediaSourceId` to `mediaSourceId + profileHash`:

```csharp
private static string ComputePoolKey(string mediaSourceId, WarmPoolEncodingProfile profile)
{
    var data = $"{mediaSourceId}-{profile.VideoCodec}-{profile.AudioCodec}-{profile.VideoBitrate}-{profile.AudioBitrate}-{profile.Width}-{profile.Height}";
    return MD5Hash(data);
}
```

This allows multiple warm entries per channel (one per encoding profile).

**Files**: `WarmFFmpegProcessPool.cs`, `WarmProcessInfo.cs`, `WarmProcessProvider.cs`

### Phase 2: Pool Management (Plugin-only)

#### 2a. Implement Idle Timeout Eviction

Add a background `System.Threading.Timer` to `WarmFFmpegProcessPool`:

- Fires every 60 seconds
- Scans `_warmProcesses` for entries where `(UtcNow - LastAccessTime) > IdleTimeoutMinutes`
- Calls `StopWarmProcessAsync()` for expired entries with `ConsumerCount == 0`
- Disposes timer on plugin shutdown

**Files**: `WarmFFmpegProcessPool.cs`

#### 2b. Implement LRU Eviction on Pool Full

In `AdoptProcess()`, when pool is full:

- Find entry with oldest `LastAccessTime` and `ConsumerCount == 0`
- Evict it (kill FFmpeg, close live stream, clean files)
- Adopt the new process into the freed slot
- If all entries have active consumers, decline adoption

**Files**: `WarmFFmpegProcessPool.cs`

#### 2c. Simplify Consumer Counting

For the current implementation, consumer counting is informational only — multiple clients can read the same HLS segments from disk without coordination. Keep the count for LRU eviction decisions (don't evict entries with active consumers) but don't rely on it for correctness.

The count gets incremented on warm hits in `TryGetPlaylistPath()`. For decrement: when `TranscodeManager` kills a job that was using a warm playlist, it could notify the provider. But for simplicity in Phase 2, just use the idle timeout as the primary eviction mechanism.

**Files**: `WarmFFmpegProcessPool.cs`

### Phase 3: Direct Stream Warm Pool (Server + Plugin)

Focus: Keep IPTV connections alive for scenarios where the client plays the mpegts stream directly without FFmpeg. This is a secondary optimization since direct streaming is already fast (~1-5s), but keeping the connection warm eliminates the connection establishment and initial buffering delay.

#### 3a. New Interface: IWarmStreamProvider (Server)

```csharp
public interface IWarmStreamProvider
{
    bool TryGetWarmStream(string mediaSourceId, out IDirectStreamProvider? streamProvider);
    bool TryAdoptStream(string mediaSourceId, ILiveStream liveStream);
}
```

**Files**: New file in `MediaBrowser.Controller/LiveTv/`

#### 3b. Hook in MediaSourceManager.CloseLiveStream (Server)

Before closing a live stream when `ConsumerCount` reaches 0, offer it to warm stream providers:

```csharp
if (liveStream.ConsumerCount <= 0)
{
    foreach (var provider in _warmStreamProviders)
    {
        if (provider.TryAdoptStream(id, liveStream))
        {
            liveStream.ConsumerCount++;
            return; // Don't close — provider owns it now
        }
    }
    // Normal close
}
```

**Files**: `Emby.Server.Implementations/Library/MediaSourceManager.cs` (~10 lines)

#### 3c. Hook in MediaSourceManager or LiveTvMediaSourceProvider (Server)

Before opening a new live stream, check if a warm stream exists:

```csharp
foreach (var provider in _warmStreamProviders)
{
    if (provider.TryGetWarmStream(mediaSourceId, out var warmStream))
    {
        return warmStream; // Reuse existing connection
    }
}
// Normal open
```

**Files**: `Emby.Server.Implementations/Library/MediaSourceManager.cs` or `src/Jellyfin.LiveTv/LiveTvMediaSourceProvider.cs` (~10 lines)

#### 3d. Implement WarmStreamPool in Plugin (Plugin)

New class alongside `WarmFFmpegProcessPool`:

- Stores adopted `ILiveStream` instances (the `SharedHttpStream` to TVHeadend)
- `SharedHttpStream` keeps its background copy task running (data flows from TVHeadend to temp file)
- On warm hit: return the existing stream, bump consumer count
- Same pool size / idle timeout / LRU eviction as the FFmpeg pool

**Files**: New files in plugin

### Phase 4: Automatic Pool Management (Plugin-only)

Remove the need for manual REST API calls.

#### 4a. Auto-Warm via Adoption

The current automatic adoption from `TranscodeManager` already handles the most common case: user watches a channel, then tunes away → FFmpeg adopted → user tunes back → warm hit. No manual intervention needed for this flow.

#### 4b. Track Viewing History

Monitor playback events to build a most-recently-watched channel list. When pool has capacity, proactively warm popular channels.

#### 4c. Predictive Pre-Warming

Based on sequential viewing patterns (user watches channels 1→2→3), pre-warm the likely next channel.

### Phase 5: Production Hardening (Plugin-only)

- Health checks: detect crashed FFmpeg processes, clean up
- Disk space monitoring: cap total segment disk usage
- Metrics: warm hit rate, average tune time improvement
- Admin UI: Jellyfin plugin config page for pool status/management

---

## Server Changes Summary

| Change | Where | Size | Phase |
| ------ | ----- | ---- | ----- |
| `IWarmProcessProvider` interface | `MediaBrowser.Controller` | Done | Done |
| Warm pool check in `DynamicHlsController` | `Jellyfin.Api` | Done | Done |
| Warm pool adoption in `TranscodeManager` | `MediaBrowser.MediaEncoding` | Done | Done |
| ConsumerCount bump on adoption | `MediaBrowser.MediaEncoding` | Done | Done |
| **Extend `IWarmProcessProvider` with encoding profile** | `MediaBrowser.Controller` | ~20 lines | **Phase 1a** |
| **Pass encoding params at check + adoption** | `Jellyfin.Api` + `MediaBrowser.MediaEncoding` | ~20 lines | **Phase 1b** |
| `IWarmStreamProvider` interface (new) | `MediaBrowser.Controller` | Done | Done |
| Adoption hook in `MediaSourceManager.CloseLiveStream` | `Emby.Server.Implementations` | Done | Done |
| Warm stream check in `MediaSourceManager` | Not needed — stream sharing handles reuse | N/A | Done |
| **Guard warm pool check behind playlist-exists** | `Jellyfin.Api` | ~2 lines moved | **Bug fix** |

All server changes are thin hook interfaces. Business logic stays in the plugin.

## Plugin Changes Summary

| Change | Where | Phase |
| ------ | ----- | ----- |
| **Key pool by channel + encoding profile** | `WarmFFmpegProcessPool.cs`, `WarmProcessInfo.cs` | **Phase 1c** |
| Idle timeout eviction timer | `WarmFFmpegProcessPool.cs` | Done |
| LRU eviction on pool full | `WarmFFmpegProcessPool.cs` | Done |
| Simplify consumer counting | `WarmFFmpegProcessPool.cs` | Done |
| Implement `IWarmStreamProvider` | `WarmStreamProvider.cs` (new) | Done |
| Warm stream pool | `WarmStreamPool.cs`, `WarmStreamInfo.cs` (new) | Done |
| Viewing history tracking | `ViewingHistory.cs`, `WarmPoolEntryPoint.cs` (new) | Done |
| Predictive pre-warming (priority boost) | `WarmPoolEntryPoint.cs`, `WarmFFmpegProcessPool.cs`, `WarmStreamPool.cs` | Done |
| Smart eviction (history-aware LRU) | `WarmFFmpegProcessPool.cs`, `WarmStreamPool.cs` | Done |
| Performance metrics | `PoolMetrics.cs` (new) | Done |
| Disk space monitoring | `DiskSpaceMonitor.cs` (new), `PluginConfiguration.cs` | Done |
| Admin UI + REST API expansion | `Plugin.cs`, `Configuration/config.html` (new), `WarmPoolController.cs` | Done |
| Singleton management | `WarmPoolManager.cs` | Done |
| DI registration | `PluginServiceRegistrator.cs` | Done |

---

## Recommended Execution Order

1. **Phase 1** (encoding parameter matching) — Critical correctness fix. Without this, warm hits can serve wrong-format content to clients.
2. **Phase 2a + 2b** (idle timeout + LRU eviction) — Makes the pool self-managing.
3. **Phase 2c** (consumer counting) — Minor cleanup.
4. **Phase 3** (direct stream warm pool) — Secondary optimization, only needed if direct streaming latency is a problem.
5. **Phase 4 + 5** — Polish and production readiness.

---

## Phase 6: Session-Aware Eviction — COMPLETED ✓

### Problem Statement

The warm pool is entirely global with no concept of which user or session owns each entry. `WarmProcessInfo` stores no `SessionId`, `DeviceId`, or `UserId`. This causes two problems:

### Scenario 1: Multi-User Pool Fairness

**Setup**: Pool size 4. User A flips through channels for 10 minutes, filling slots A1-A4. User B then connects and starts flipping channels.

**Current behavior**: When User B tunes a channel, adoption triggers `EvictLeastValuableProcess`. The eviction score (`historyPriority - idleMinutes/60.0`) is purely based on viewing history frequency and recency. There is no concept of "User A holds 4 slots, User B holds 0."

**Problem**: User A's entries accumulate higher `historyPriority` from repeated flipping, making them harder to evict. User B's entries get adopted (displacing User A's oldest), but User B's new entries are immediately vulnerable because they have low history priority. A power user who flips rapidly can effectively monopolize the pool.

**Desired behavior**: When the pool is full, eviction should consider per-user/per-session slot counts. A user holding 3 of 4 slots should have entries evicted before a user holding 1 of 4 slots (all else being equal). This ensures each active user gets a fair share of the pool.

### Scenario 2: Expedited Eviction When User Leaves

**Setup**: User A stops watching LiveTV entirely (turns off TV). Their channels remain in the pool. User C then connects and needs a slot.

**Current behavior**: The plugin subscribes to `PlaybackStart` and `PlaybackStopped` but **does NOT subscribe to `SessionEnded`**. When User A's WebSocket closes (TV off), the server fires `SessionEnded` via `WebSocketController.OnConnectionClosed` → `SessionManager.CloseIfNeededAsync` → `OnSessionEnded`. But the plugin never receives this signal.

User A's warm entries sit in the pool with frozen `LastAccessTime`, waiting for either:

- The 10-minute idle timeout (checked every 60s), OR
- LRU eviction triggered by someone else needing a slot

**Problem**: There is no expedited eviction. User A's entries are treated identically to entries from active users. If User C connects and needs a slot, the eviction score doesn't know User A is gone -- it just uses the generic `historyPriority - idleMinutes/60.0` formula.

**Desired behavior**: When a user's session ends, their pool entries should be deprioritized (not immediately evicted -- there's no need to evict if no one else needs the slot). But when demand arrives, orphaned entries should be the first to go, regardless of their history priority.

### Server Events Available (Research Findings)

The `ISessionManager` interface (`MediaBrowser.Controller/Session/ISessionManager.cs`) already exposes the needed events:

| Event | Trigger | Data Available |
| ----- | ------- | -------------- |
| `PlaybackStart` | Client starts playing | `PlaybackProgressEventArgs`: `Session`, `MediaSourceId`, `DeviceId`, `Users`, `PlaySessionId` |
| `PlaybackStopped` | Client stops a stream | `PlaybackStopEventArgs`: same as above + `PlayedToCompletion` |
| `SessionEnded` | Client fully disconnects | `SessionEventArgs`: `SessionInfo` (includes `Id`, `DeviceId`, `UserId`, `Client`, `DeviceName`) |
| `SessionActivity` | Every ~10s while connected | `SessionEventArgs`: `SessionInfo` |

**`SessionEnded` trigger paths** (traced through code):

1. **WebSocket close** (most common "turn off TV" case):
   - `WebSocketController.OnConnectionClosed` (`Emby.Server.Implementations/Session/WebSocketController.cs:78`)
   - → Removes socket from list, calls `_sessionManager.CloseIfNeededAsync(_session)`
   - → `CloseIfNeededAsync` checks `session.SessionControllers.Any(i => i.IsSessionActive)` (`SessionManager.cs:310`)
   - → If no active controllers → removes from `_activeConnections` → fires `OnSessionEnded` → raises `SessionEnded` event
   - → Also calls `CloseLiveStreamIfNeededAsync` if session had an active live stream

2. **API call**: `SessionController.ReportSessionEnded` (`Jellyfin.Api/Controllers/SessionController.cs:422`) → `ReportSessionEnded` → `OnSessionEnded`

3. **Logout/token revocation**: `Logout` iterates sessions for that device → `ReportSessionEnded` → `OnSessionEnded`

**Important nuances**:

- **Navigating away from LiveTV** (e.g., browsing movie library): Only `PlaybackStopped` fires. Session stays alive. `SessionEnded` does NOT fire.
- **App backgrounded / TV input switched**: Depends on client behavior. Most clients keep the WebSocket alive for a while. Eventually timeout or OS kill → WebSocket close → `SessionEnded`.
- **Idle playback detection** (5 min no check-in): `SessionManager.CheckForIdlePlayback` fires synthetic `PlaybackStopped` only -- NOT `SessionEnded`. Session stays in `_activeConnections`.
- **Inactive session threshold** (paused too long): Sends a stop command, which triggers `PlaybackStopped`. Session stays alive.

### Plugin Event Subscriptions (after v1.8.0)

`WarmPoolEntryPoint.cs` subscribes to:

- `PlaybackStart` → records viewing history, tracks user+channel
- `PlaybackStopped` → records viewing history, triggers predictive channel boosting, records session→mediaSourceId mapping for adoption tagging
- `SessionEnded` → marks all pool entries owned by the ended session as orphaned

**NOT subscribed to**: `SessionActivity`, `SessionStarted`

### Implementation (Plugin-Only, v1.8.0)

All changes are in the plugin. No server modifications needed.

#### 6a. Track Session Ownership

Add session metadata to `WarmProcessInfo`:

```csharp
/// Gets or sets the session ID of the user who caused this process to be adopted.
public string? OwnerSessionId { get; set; }

/// Gets or sets the device ID of the owning session.
public string? OwnerDeviceId { get; set; }

/// Gets or sets whether the owning session has ended (user disconnected).
public bool IsOrphaned { get; set; }
```

**Threading session info to `AdoptProcess()`**: The `IWarmProcessProvider.TryAdoptProcess()` interface does not carry session info. Options:

- **Option A**: Extend `IWarmProcessProvider` interface to include session ID (requires server change -- violates plugin-first principle)
- **Option B (recommended)**: In `WarmPoolEntryPoint.OnPlaybackStopped`, store `{mediaSourceId → sessionId}` in a short-lived lookup. `WarmProcessProvider.TryAdoptProcess` reads from this lookup to tag the adopted entry. The adoption happens synchronously during the same `PlaybackStopped` processing pipeline, so timing is reliable.
- **Option C**: After adoption, use a post-adoption hook to tag the entry based on recent `PlaybackStopped` events.

#### 6b. Subscribe to SessionEnded

In `WarmPoolEntryPoint.StartAsync()`, add:

```csharp
_sessionManager.SessionEnded += OnSessionEnded;
```

Handler marks all entries owned by that session as orphaned:

```csharp
private void OnSessionEnded(object? sender, SessionEventArgs e)
{
    var sessionId = e.SessionInfo.Id;
    _pool.MarkSessionOrphaned(sessionId);
    _logger.LogInformation(
        "[WarmPoolEntryPoint] Session {SessionId} ended (device: {Device}), marking pool entries as orphaned",
        sessionId, e.SessionInfo.DeviceName);
}
```

#### 6c. Modify Eviction Scoring

Update `EvictLeastValuableProcess` to incorporate session awareness:

```text
Current score:
  score = historyPriority - (idleMinutes / 60.0)

New score with session awareness:
  orphanPenalty    = process.IsOrphaned ? -10.0 : 0.0
  fairnessPenalty  = -(ownerSlotCount / totalSlots)
  score = historyPriority - (idleMinutes / 60.0) + orphanPenalty + fairnessPenalty
```

Effects:

- **Scenario 1 (fairness)**: `fairnessPenalty` makes entries from users holding disproportionate pool shares more evictable. User A with 3/4 slots gets penalty -0.75; User B with 1/4 gets -0.25.
- **Scenario 2 (expedited eviction)**: `orphanPenalty` of -10.0 makes orphaned entries drastically more evictable than any active-session entry, ensuring they're the first to go when demand arrives. But they remain in the pool if nobody needs the slot (eviction only happens when pool is full during adoption).

#### 6d. Grace Period for Channel Changes (Optional Refinement)

When `PlaybackStopped` fires, it could be a channel change (stop old → start new) or a true exit. To avoid prematurely orphaning during rapid channel flipping:

- On `PlaybackStopped`: Record timestamp for that session
- On `PlaybackStart` (same session within ~5s): Cancel -- it was a channel change
- Timer expires (no new `PlaybackStart`): User stopped watching LiveTV but session is still active. Could apply a mild priority reduction (less than full orphan penalty).

This is refinement, not essential for the core feature. The orphan flag from `SessionEnded` is the primary mechanism.

### Plugin Changes Summary (Phase 6)

| Change | File | Impact |
| ------ | ---- | ------ |
| Add `OwnerSessionId`, `OwnerDeviceId`, `IsOrphaned` | `WarmProcessInfo.cs` | 3 properties |
| Track session→mediaSourceId mapping on stop | `WarmPoolEntryPoint.cs` | ~15 lines |
| Subscribe to `SessionEnded`, mark orphans | `WarmPoolEntryPoint.cs` | ~20 lines |
| `MarkSessionOrphaned()` method | `WarmFFmpegProcessPool.cs` | ~10 lines |
| Session-aware eviction scoring | `WarmFFmpegProcessPool.cs` | ~10 lines in `EvictLeastValuableProcess` |
| Per-user slot counting in eviction | `WarmFFmpegProcessPool.cs` | ~10 lines |
| Apply same changes to stream pool | `WarmStreamPool.cs`, `WarmStreamInfo.cs` | Mirror of above |

**Server changes needed**: None. All data is available through existing `ISessionManager` events.

---

*This plan should be updated as implementation progresses and new findings emerge.*
