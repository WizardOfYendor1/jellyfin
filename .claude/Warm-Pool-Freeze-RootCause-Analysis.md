# Warm Pool Freeze Issue - Root Cause & Fix

## Executive Summary

**The Problem**: When a warm HIT occurs (cached FFmpeg process returned), the channel plays for ~10 seconds then freezes.

**The Root Cause**: The plugin v1.7.0 removed `ConsumerCount` increment from the warm playlist lookup to fix a lock-up issue. However, this left processes vulnerable to eviction while clients were **actively consuming segments**, causing the playback freeze.

**The Fix**: A two-phase consumer lifecycle:
1. **Server notifies plugin when serving warm HIT** → plugin increments ConsumerCount
2. **Plugin decrements on PlaybackStopped/SessionEnded events** → eviction allowed again

---

## Timeline of Freeze

```
Client tunes channel
   ↓
DynamicHlsController.GetLiveHlsStream() called
   ↓
Warm pool check finds a matching FFmpeg process with pre-buffered segments
   ↓
TryGetPlaylistContentAsync() returns the playlist content
   ↓
Playlist returned to client; ConsumerCount = 0 ❌ (should be 1)
   ↓
Client starts polling for .ts segments every 500ms
   ↓
Meanwhile, idle timer or LRU eviction runs (every 60 seconds)
   ↓
Pool sees ConsumerCount == 0, assumes process is idle/abandoned
   ↓
Process evicted: FFmpeg killed, playlist deleted, live stream closed ❌
   ↓
Client's next segment request fails
   ↓
Playback FREEZES after ~10 seconds
```

---

## Root Cause Analysis

### The Bug in v1.7.0

From `.claude/WarmPool-ChangePlan.md`:

> **Bug Fix: Plugin v1.7.0 — Production Testing Fixes** ✓
>
> **1. ConsumerCount leak** — `TryGetPlaylistPath()` incremented `ConsumerCount` on every warm HIT poll (~every 3s) with no decrement when playback stopped. After a few warm HIT sessions, pool entries became permanently unevictable (ConsumerCount > 0 blocks eviction). Pool would lock up refusing all adoptions ("all entries have active consumers"). **Fix**: Removed `ConsumerCount++` — `LastAccessTime` update is sufficient for LRU/idle protection.

### The Problem This Created

By removing the `ConsumerCount++`, the plugin prevented the lock-up but created a **new vulnerability**:

1. Warm playlist hit occurs → `TryGetPlaylistContentAsync()` is called
2. `TryGetPlaylistPath()` updates `LastAccessTime` but NOT `ConsumerCount`
3. Client receives playlist and starts requesting segments
4. Idle timer runs every 60 seconds and checks which processes to evict
5. **The idle timer checks**: `if (age > IdleTimeoutMinutes && ConsumerCount <= 0) → evict`
6. Since `ConsumerCount == 0`, the process is marked as evictable ❌
7. Even though `LastAccessTime` was just updated, the eviction decision was made based on `ConsumerCount`
8. **Process gets killed while client is actively consuming segments**

The fix in v1.7.0 inadvertently traded one problem (permanent lock-up) for another (premature eviction).

### Why LastAccessTime Update Alone Is Insufficient

The plugin's eviction logic uses **two independent criteria**:

1. **LRU Scoring**: `historyPriority - (idleMinutes / 60.0)`
   - High history → harder to evict
   - Recent access (high `LastAccessTime`) → harder to evict

2. **Idle Check**: `ConsumerCount <= 0` gating
   - If `ConsumerCount > 0` → never evict
   - If `ConsumerCount == 0` → eligible for eviction

The bug: Even if `LastAccessTime` is fresh, if `ConsumerCount == 0`, the process **can** be evicted. The timing is critical:

```
T=0:00:00   Client requests playlist (warm HIT)
T=0:00:00   TryGetPlaylistPath() updates LastAccessTime to T=0:00:00
T=0:00:00   Playlist returned to client; ConsumerCount = 0
T=0:00:05   Client requests first segment
T=0:00:10   Idle timer fires (runs every 60s, but schedules check)
T=0:00:10   Check sees: LastAccessTime = T=0:00:00 (9.9 seconds ago), ConsumerCount = 0
T=0:00:10   **Eviction triggered** (< 10 min idle default, but ConsumerCount gate = true)
T=0:00:11   FFmpeg killed, playlist deleted
T=0:00:15   Client times out waiting for next segment
            Playback FREEZES
```

Wait, that doesn't match the ~10 second freeze symptom exactly. Let me reconsider...

Actually, the issue could be **LRU eviction** triggered during pool adoption (when a new channel is tuned while an old one is warm):

```
T=0:00:00   Client tunes channel A → FFmpeg starts → warm process created → adopted
T=0:00:10   Client starts playing (warm HIT) → playlist served; ConsumerCount = 0
T=0:00:15   Client tunes channel B while still consuming A (double-play)
T=0:00:15   Channel B tune → starts new FFmpeg → adoption hook fires
T=0:00:15   Pool full → LRU eviction runs
T=0:00:15   EvictLeastValuableProcess() scores channel A: ConsumerCount = 0 (evictable!)
T=0:00:15   Channel A process evicted
T=0:00:15   Client's channel A playback continues until segment timeout
T=0:00:20   Playback FREEZES when segment expires
```

Either way, the root cause is the same: **ConsumerCount must be incremented when a warm playlist is served, not when the process is adopted**.

---

## Additional Freeze Cause (2026-02-03)

Even with correct consumer tracking, warm HIT playback can still freeze if the **session playlist file is only copied once**.

### What Happens

1. `DynamicHlsController` asks the warm provider **only when the session playlist file does not exist**.
2. The plugin copies the warm playlist to the session path and returns the content.
3. The session playlist file is **never updated again** (because the controller stops querying once it exists).
4. Clients keep reloading the same stale playlist and eventually request segments that no longer exist.

With typical segment lengths (e.g., 3s), this produces a freeze after ~8–15 seconds if the warm process is young (few segments available at copy time).

### Fix

The plugin now **continuously republishes** the warm playlist to the session playlist path while consumers are active. This keeps playlist content fresh and prevents segment starvation without requiring Jellyfin server changes.

---

## The Fix Architecture

### What Was Wrong With Old Approach

Old approach (v1.7.0 before bug fix):
```
OnPlaybackStopped
   ↓
TryGetPlaylistPath() increments ConsumerCount  ← **WRONG PLACE**
   ↓
Client gets playlist
   ↓
Client polls for segments
   ↓
Multiple PlaybackStopped events could fire or NOT fire
   ↓
ConsumerCount might never decrement → permanent lock-up
```

Problem: `PlaybackStopped` is called by the client, not by the warm pool. Depending on client behavior, it might not fire at all if the client crashes.

### What The Fix Does

New approach:
```
DynamicHlsController.GetLiveHlsStream()
   ↓
TryGetPlaylistContentAsync() found match
   ↓
   ├─→ Playlist returned
   └─→ NotifyPlaylistConsumer(..., playSessionId) called  ← **Server-side callback**
       ↓
       WarmProcessProvider.NotifyPlaylistConsumer()
       ↓
       WarmFFmpegProcessPool.IncrementConsumerCount()  ← **NOW ConsumerCount++**
       ↓
       Playlist already served to client; client can't "undo" this
       ↓
       Client will consume segments until PlaybackStopped fires
       ↓
       WarmPoolEntryPoint.OnPlaybackStopped()
       ↓
       DecrementConsumerCount()  ← **Decrement when consumption actually ends**
```

Why this works:
1. `NotifyPlaylistConsumer(..., playSessionId)` is called **immediately after** the server decides to send a warm playlist
2. The plugin **cannot miss** this notification (it's a direct method call)
3. The increment happens **before** the client can start consuming (it's synchronous)
4. The decrement happens when playback actually stops (via event listener)
5. Between increment and decrement, `ConsumerCount > 0` **protects from eviction**

---

## Changes Summary

### Server-Side (Jellyfin)
**Commit**: `db5d84399` (feature/fastchannelzapping)

1. **Add `NotifyPlaylistConsumer(..., playSessionId)` to `IHlsPlaylistProvider`**
   - Contract: tells provider a warm playlist is being served to a client
   - Provider must increment internal consumer count

2. **Call `NotifyPlaylistConsumer(..., playSessionId)` in `DynamicHlsController`**
   - After successful warm HIT, before returning playlist to client
   - Ensures provider knows a consumer is about to receive the playlist

3. **Documentation**: `Consumer-Tracking-Fix.md` with implementation guide

### Plugin-Side (jellyfin-plugin-warmpool) — COMPLETED (v1.14.2)
**Implemented changes**:

1. **Implement `NotifyPlaylistConsumer(..., playSessionId)` in `WarmProcessProvider`**
   - Delegates to pool's `IncrementConsumerCount()`

2. **Add `IncrementConsumerCount()` to `WarmFFmpegProcessPool`**
   - Increments the pool entry's `ConsumerCount`

3. **Add `DecrementConsumerCount()` and `DecrementAllConsumersForMediaSource()`**
   - Called from `WarmPoolEntryPoint.OnPlaybackStopped()` listener
   - Decrements consumer count when playback ends

4. **Update `WarmPoolEntryPoint.OnPlaybackStopped()`**
   - Extract mediaSourceId from PlaybackStopEventArgs
   - Call `DecrementAllConsumersForMediaSource()` for tracked mediaSourceIds

5. **Verify eviction logic respects `ConsumerCount > 0`**
   - Idle timeout should skip entries with `ConsumerCount > 0`
   - LRU eviction should prefer entries with `ConsumerCount == 0`

---

## Testing Strategy

### Unit-Level Verification

1. **Consumer increment on warm HIT**:
   ```
   POST /WarmPool/Start (warm FFmpeg process)
   GET /WarmPool/Metrics (verify warm process exists, ConsumerCount=0)
   GET /Live/HLS/{id}/stream.m3u8 (warm HIT)
   GET /WarmPool/DetailedStatus (verify ConsumerCount=1 for that entry)
   ```

2. **Consumer decrement on PlaybackStopped**:
   ```
   [Simulate PlaybackStopped event via ISessionManager]
   GET /WarmPool/DetailedStatus (verify ConsumerCount=0 again)
   ```

### Integration Testing (LiveTV)

1. **Single channel warm HIT + extended playback**:
   - Start LiveTV stream
   - Let FFmpeg run for ~30 seconds
   - Stop playback → process adopted into warm pool
   - Tune same channel → warm HIT
   - Let playback run for **60+ seconds without freeze**
   - Verify no eviction occurred
   - Stop playback → verify eviction allowed now

2. **Multi-channel playback (channel switching)**:
   - Tune channel A (cold start)
   - Let it buffer for 10 seconds
   - Switch to channel B while A is still playing
   - Channel B should get a fresh cold start
   - Channel A warm process should be adopted
   - Resume channel A from warm pool → should not freeze
   - Let both A and B play for 30 seconds each

3. **Eviction under pressure**:
   - Start 5 warm channels in pool (if pool size = 4)
   - Verify oldest/least-favorite is evicted (not the one with `ConsumerCount > 0`)
   - Verify client on retained channel can continue playing

### Stress Testing

1. **Rapid channel flipping**:
   - User flips channels rapidly (every 2 seconds)
   - Each tune triggers warm pool check
   - Verify no crashes or hangs

2. **Concurrent clients**:
   - Multiple clients streaming same channel with different encoding profiles
   - Verify each gets separate consumer tracking
   - Verify process not evicted while any client consuming

3. **Memory/Resource stability**:
   - Monitor memory usage over 24-hour test window
   - Verify no leaks (ConsumerCount stuck at > 0)
   - Verify disk space for warm segments self-cleans

---

## Success Criteria

✅ **Warm HIT playback no longer freezes after ~10 seconds**
✅ **Warm process remains active while client consuming segments**
✅ **Warm process evicted correctly after client stops** (not permanently orphaned)
✅ **No regression: cold starts still work normally**
✅ **No regression: process adoption still works**
✅ **Logs clearly show increment/decrement of ConsumerCount**

---

## Related Documentation

- `.claude/WarmPool-ChangePlan.md` — Overall warm pool architecture
- `.claude/Consumer-Tracking-Fix.md` — Implementation guide for plugin
- `.claude/LiveTV-Architecture.md` — LiveTV streaming flow
- `Jellyfin.Api/Controllers/DynamicHlsController.cs:336` — Where NotifyPlaylistConsumer is called
- `MediaBrowser.Controller/LiveTv/IHlsPlaylistProvider.cs` — Interface definition

---

## Why This Approach Is Correct

1. **Server-driven**: The server knows when it's serving a warm playlist (synchronous decision point)
2. **Plugin-driven cleanup**: The plugin knows when playback ends (event-driven via ISessionManager)
3. **Minimal coupling**: No new data structures needed, just a callback method
4. **Reversible**: If this fix breaks something, removing the `NotifyPlaylistConsumer()` call restores old behavior
5. **Non-invasive**: Requires only 2 small server changes + 4 simple plugin methods
6. **Backward compatible**: Old plugins that don't implement `NotifyPlaylistConsumer()` will fail at runtime, which is better than silently misbehaving

---

## Open Questions

1. **What if encoding profile changes mid-stream?**
   - Client switches from H.264 to H.265 while watching same channel
   - `PlaybackStopped` fires with one profile, warm process has another
   - **Current approach**: `DecrementAllConsumersForMediaSource()` decrements ALL profiles for that mediaSourceId (safe but imprecise)
   - **Future improvement**: Pass encoding profile in `PlaybackStopEventArgs` for precise tracking

2. **What if client crashes or disconnects without PlaybackStopped?**
   - WebSocket closes → `SessionEnded` event fires
   - `WarmPoolEntryPoint.OnSessionEnded()` marks entries as orphaned (from Phase 6)
   - Orphaned entries have lower eviction score, so they're released first when space needed
   - **This is correct**: orphaned entries should not block new clients

3. **What about direct streaming (not HLS)?**
   - Direct streaming uses `ILiveStreamProvider`, not `IHlsPlaylistProvider`
   - Phase 3 (Direct Stream Warm Pool) already implements this for direct streams
   - **No change needed**: this fix only applies to HLS warm pools


