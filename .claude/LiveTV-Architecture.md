# Jellyfin LiveTV Architecture

This document describes how LiveTV tuning and streaming works in Jellyfin, with a focus on M3U/IPTV sources and the warm pool fast-channel-zapping feature.

---

## Table of Contents

1. [High-Level Overview](#1-high-level-overview)
2. [Tuner Host Architecture](#2-tuner-host-architecture)
3. [M3U Tuner Implementation](#3-m3u-tuner-implementation)
4. [Client Request Flow](#4-client-request-flow)
5. [Stream Types and Sharing](#5-stream-types-and-sharing)
6. [Direct vs Remux vs Transcode](#6-direct-vs-remux-vs-transcode)
7. [HLS Streaming Pipeline](#7-hls-streaming-pipeline)
8. [DVR Recording System](#8-dvr-recording-system)
9. [Playback Stop and Cleanup](#9-playback-stop-and-cleanup)
10. [Warm Pool Implementation](#10-warm-pool-implementation)
11. [Key Files Reference](#11-key-files-reference)
12. [Design Considerations for Fast Channel Zapping](#12-design-considerations-for-fast-channel-zapping)

---

## 1. High-Level Overview

Jellyfin's LiveTV system connects clients (Android TV, web, etc.) to live television sources through a layered architecture:

```
Client Device (AndroidTV, Web, etc.)
        |
        | HTTP (HLS / Direct Stream)
        v
Jellyfin Server
  ├─ API Controllers (DynamicHlsController, LiveTvController)
  ├─ Stream Management (MediaSourceManager, SessionManager, TranscodeManager)
  ├─ LiveTV Service (DefaultLiveTvService, LiveTvMediaSourceProvider)
  ├─ Tuner Hosts (M3UTunerHost, HdHomerunHost)
  └─ Stream Classes (SharedHttpStream, LiveStream)
        |
        | HTTP / UDP
        v
IPTV Source (TVHeadend, HDHomeRun, etc.)
```

**Two tuner types are built-in:**
- **M3UTunerHost** — Connects to IPTV providers (TVHeadend, etc.) via HTTP using M3U playlists
- **HdHomerunHost** — Connects to HDHomeRun hardware tuners via UDP/HTTP

The M3U tuner is the focus of this document and the warm pool feature.

---

## 2. Tuner Host Architecture

### Core Interfaces and Classes

| Interface / Class | Location | Purpose |
|---|---|---|
| `ITunerHost` | `MediaBrowser.Controller/LiveTv/ITunerHost.cs` | Base interface for all tuner types |
| `BaseTunerHost` | `src/Jellyfin.LiveTv/TunerHosts/BaseTunerHost.cs` | Abstract base with caching, channel aggregation, stream opening |
| `M3UTunerHost` | `src/Jellyfin.LiveTv/TunerHosts/M3UTunerHost.cs` | M3U/IPTV implementation |
| `HdHomerunHost` | `src/Jellyfin.LiveTv/TunerHosts/HdHomerun/HdHomerunHost.cs` | HDHomeRun hardware implementation |
| `ITunerHostManager` | `MediaBrowser.Controller/LiveTv/ITunerHostManager.cs` | Manages tuner registration, validation, persistence |
| `TunerHostManager` | `src/Jellyfin.LiveTv/TunerHosts/TunerHostManager.cs` | Implementation |
| `TunerHostInfo` | `MediaBrowser.Model/LiveTv/TunerHostInfo.cs` | Configuration model for a tuner instance |

### Tuner Registration (DI)

```csharp
services.AddSingleton<ITunerHostManager, TunerHostManager>();
services.AddSingleton<ITunerHost, M3UTunerHost>();
services.AddSingleton<ITunerHost, HdHomerunHost>();
services.AddSingleton<ILiveTvService, DefaultLiveTvService>();
```

### TunerHostInfo Configuration

Stored in `{ConfigPath}/livetv.json` under `TunerHosts[]`:

| Property | Purpose | Default |
|----------|---------|---------|
| `Id` | Unique GUID | Generated on save |
| `Url` | M3U playlist URL or file path | Required |
| `Type` | `"m3u"` or `"hdhomerun"` | Required |
| `TunerCount` | Max simultaneous streams (0 = unlimited) | 0 |
| `AllowStreamSharing` | Enable multi-client stream reuse | true |
| `EnableStreamLooping` | Re-buffer on EOF | false |
| `UserAgent` | Custom HTTP User-Agent header | Chrome UA |
| `FallbackMaxStreamingBitrate` | Max bitrate for transcoding | 30 Mbps |
| `AllowHWTranscoding` | GPU transcoding | true |
| `AllowFmp4TranscodingContainer` | FMP4 output | false |

---

## 3. M3U Tuner Implementation

### Channel Discovery

`M3uParser` (`src/Jellyfin.LiveTv/TunerHosts/M3uParser.cs`) parses M3U playlists:

1. Fetches playlist via HTTP GET (with custom User-Agent) or reads local file
2. Parses `#EXTINF:` metadata lines with regex for `key="value"` attributes
3. Extracts stream URL from the next line after each `#EXTINF`
4. Builds `ChannelInfo` objects with:
   - **Name**: from `tvg-name`, `tvg-id`, or EXTINF metadata
   - **Number**: from `tvg-chno`, numeric `tvg-id`, or `channel-id`
   - **ID**: MD5 hash of stream URL (ensures uniqueness)
   - **Logo**: `tvg-logo` or `logo` attribute
   - **Group**: `group-title` attribute

Example M3U input:
```
#EXTM3U
#EXTINF:-1 tvg-id="bbc.uk" tvg-logo="http://..." group-title="UK", BBC One
http://tvheadend:9981/stream/channelid/1
```

### Stream Type Detection

When a client requests a channel, `M3UTunerHost.GetChannelStream()` decides what stream class to use:

```
If AllowStreamSharing=true AND Protocol=HTTP:
  ├─ HEAD request or file extension check for MPEG-TS
  ├─ If .ts/.tsv/.m2t extension OR video/MP2T MIME → SharedHttpStream
  └─ Otherwise → LiveStream (direct, not shared)
Else:
  └─ LiveStream (direct, not shared)
```

### Tuner Count Limiting

Per tuner host instance — counts active streams (not consumers):

```csharp
if (tunerCount > 0) {
    var activeStreams = currentLiveStreams
        .Where(i => i.TunerHostId == tunerHostId).Count();
    if (activeStreams >= tunerCount)
        throw new LiveTvConflictException("M3U simultaneous stream limit reached");
}
```

A single shared stream serving 5 clients counts as 1 stream toward the limit.

---

## 4. Client Request Flow

When a user selects a LiveTV channel from the program guide, the client makes this exact sequence of HTTP calls:

### Step 1: Get Playback Info

```text
POST /Items/{itemId}/PlaybackInfo
  Body: { userId, maxStreamingBitrate, deviceProfile, enableDirectPlay, enableDirectStream, enableTranscoding }
  → MediaInfoController.GetPostedPlaybackInfo()
    → MediaInfoHelper.GetPlaybackInfo()
      → Gets MediaSourceInfo[] for the channel
  ← PlaybackInfoResponse:
      mediaSources[0].requiresOpening = true  (LiveTV always needs OpenLiveStream)
      mediaSources[0].openToken = "..."       (token for Step 2)
      playSessionId = "unique-session-guid"   (used in all subsequent calls)
```

### Step 2: Open Live Stream (the "button press")

This is the call that actually tunes the channel — opens the HTTP connection to TVHeadend.

```text
POST /LiveStreams/Open
  Body: { itemId, openToken, playSessionId, userId, maxStreamingBitrate, enableDirectPlay, enableDirectStream }
  → MediaInfoController.OpenLiveStream()
    → MediaInfoHelper.OpenMediaSource()
      → MediaSourceManager.OpenLiveStreamInternal()  [under async lock]
        → LiveTvMediaSourceProvider.OpenMediaSource()
          → DefaultLiveTvService.GetChannelStreamWithDirectStreamProvider()
            → Check currentLiveStreams[] for existing shared stream
            → If found + sharing enabled: increment ConsumerCount, return existing
            → If not found: M3UTunerHost.GetChannelStream()
              → Create SharedHttpStream or LiveStream
              → liveStream.Open() — initiates HTTP connection to IPTV source
        ← ILiveStream stored in _openStreams[liveStreamId]
  ← LiveStreamResponse:
      mediaSource.liveStreamId = "live-stream-xyz"  (tuner reference for all future calls)
      mediaSource.path = "http://..."
      mediaSource.mediaStreams = [video, audio codec info]
```

### Step 3: Get HLS Master Playlist

```text
GET /Videos/{itemId}/master.m3u8?mediaSourceId=...&liveStreamId=...&playSessionId=...
    &videoCodec=h264&audioCodec=aac&videoBitRate=...&maxWidth=...&maxHeight=...
  → DynamicHlsController.GetMasterHlsVideoPlaylist()
    → StreamingHelpers.GetStreamingState()  (builds StreamState with all encoding params)
  ← #EXTM3U variant playlist with quality options
```

Note: All encoding parameters (codec, bitrate, resolution) are passed as **query parameters** on this URL. They flow through to `StreamState` fields.

### Step 4: Get HLS Media Playlist

```text
GET /Videos/{itemId}/main.m3u8?mediaSourceId=...&liveStreamId=...&playSessionId=...&<encoding params>
  → DynamicHlsController.GetVariantHlsVideoPlaylist()
  ← #EXTM3U media playlist with segment URLs
```

### Step 5: Get First HLS Segment (FFmpeg starts here)

```text
GET /Videos/{itemId}/hls1/{playlistId}/{segmentId}.ts?mediaSourceId=...&liveStreamId=...
  → DynamicHlsController.GetHlsVideoSegment()
    → GetDynamicSegment()
      → TranscodeManager.StartFfMpeg()  ← FFmpeg cold start happens here (5-10s delay)
  ← Binary TS segment data
```

**Alternative flow**: Some clients use `GET /Videos/{itemId}/live.m3u8` instead of the master/main/segment flow. This endpoint starts FFmpeg immediately and returns the playlist directly. **The warm pool check lives in this `GetLiveHlsStream()` method**, guarded so it only runs when the playlist file does not yet exist (i.e., no FFmpeg is already running for the session). Subsequent client polls of `live.m3u8` skip the warm pool check entirely.

### Step 6-7: Playback Reporting

```text
POST /Sessions/Playing                  ← Report playback started
POST /Sessions/Playing/Progress         ← Periodic heartbeat (position, isPaused, etc.)
```

### Step 8: Playback Stop (user tunes away)

```text
POST /Sessions/Playing/Stopped
  Body: { itemId, mediaSourceId, playSessionId, liveStreamId, positionTicks }
  → PlaystateController.ReportPlaybackStopped()
    → TranscodeManager.KillTranscodingJobs() — kills FFmpeg (or offers to warm pool)
    → SessionManager.OnPlaybackStopped()
      → CloseLiveStreamIfNeededAsync()
        → MediaSourceManager.CloseLiveStream() — decrements ConsumerCount
```

### Key IDs Flowing Through the Sequence

| ID | Created At | Purpose |
| --- | --- | --- |
| `itemId` | Channel library item | Identifies the channel in Jellyfin's library |
| `playSessionId` | Step 1 (PlaybackInfo response) | Unique per playback session, tracks the client |
| `liveStreamId` | Step 2 (OpenLiveStream response) | References the tuner connection (`ILiveStream`) |
| `mediaSourceId` | Step 1 (PlaybackInfo response) | MD5 of stream URL — identifies the IPTV source |
| Encoding params | Step 3-5 (query params) | Video/audio codec, bitrate, resolution — client-specific |

---

## 5. Stream Types and Sharing

### ILiveStream Interface

`MediaBrowser.Controller/Library/ILiveStream.cs`:

| Property | Purpose |
|----------|---------|
| `ConsumerCount` | Number of active clients sharing this stream |
| `EnableStreamSharing` | Whether stream can be reused by other clients |
| `OriginalStreamId` | Identifier for stream reuse detection |
| `TunerHostId` | Parent tuner host ID |
| `UniqueId` | Session-specific GUID |
| `MediaSource` | Current media source info |

### Stream Class Hierarchy

| Class | File | Shareable | Use Case |
|-------|------|-----------|----------|
| `LiveStream` | `src/Jellyfin.LiveTv/TunerHosts/LiveStream.cs` | Yes | Base class; direct file/network streams |
| `SharedHttpStream` | `src/Jellyfin.LiveTv/TunerHosts/SharedHttpStream.cs` | Yes | HTTP MPEG-TS streams with local temp file buffering |
| `HdHomerunUdpStream` | `src/Jellyfin.LiveTv/TunerHosts/HdHomerun/HdHomerunUdpStream.cs` | Yes | UDP multicast from HDHomeRun devices |
| `ExclusiveLiveStream` | `src/Jellyfin.LiveTv/IO/ExclusiveLiveStream.cs` | No | Legacy plugins, recordings (exclusive access) |

### SharedHttpStream Data Flow

This is the primary stream class for M3U/IPTV sources:

```
TVHeadend (HTTP MPEG-TS)
        |
        | HTTP GET (continuous, ResponseHeadersRead)
        v
SharedHttpStream.Open()
  → StartStreaming() background task
    → StreamHelper.CopyToAsync()
      → Read 64KB chunks from HTTP response
      → Write to temp file: /config/transcodes/{UniqueId}.ts
      → Uses ArrayPool<byte> for zero-copy buffering
        |
        | Temp file on disk (growing continuously)
        v
Multiple clients call GetStream()
  → Each gets FileStream to same temp file
  → Late joiners seek to current position
```

### Consumer Count Pattern

```
Client A opens channel → ConsumerCount = 1
Client B opens same channel → ConsumerCount = 2 (reuses stream)
Client A stops → ConsumerCount = 1 (stream stays open)
Client B stops → ConsumerCount = 0 → stream.Close() → tuner released
```

Thread safety: `MediaSourceManager._liveStreamLocker` (AsyncNonKeyedLocker) protects all open/close operations.

---

## 6. Direct vs Remux vs Transcode

### Decision Flow

`M3UTunerHost.CreateMediaSourceInfo()` sets initial capabilities:

```
SupportsDirectPlay = (!EnableStreamLooping && TunerCount == 0)  // Rare for IPTV
SupportsDirectStream = (!EnableStreamLooping)                    // Common
SupportsTranscoding = true                                       // Always available
```

Then `StreamBuilder` (in `MediaInfoHelper`) applies device profile constraints and user permissions:

| Mode | When Used | FFmpeg? | Latency | CPU |
|------|-----------|---------|---------|-----|
| **Direct Play** | Client natively supports source codec+container | No | ~Zero | None |
| **Direct Stream (remux)** | Client supports codec but not container (e.g., TS→HLS) | Yes (copy codecs) | ~2-6 sec (HLS segment time) | Low |
| **Transcode** | Client doesn't support source codecs | Yes (re-encode) | ~5-10 sec (buffer + encode) | High |

### Typical M3U/IPTV Scenarios

- **TVHeadend MPEG-TS → Android TV**: Usually **direct stream** (remux TS to HLS, copy codecs)
- **TVHeadend MPEG-TS → Web browser**: May need **transcode** if source is MPEG-2 video
- **TVHeadend MPEG-TS → Kodi**: Often **direct play** (Kodi handles TS natively)

### Key Insight for Warm Pool

Direct streaming (no FFmpeg) has near-zero startup latency. The 5+ second delay comes from FFmpeg startup when remuxing or transcoding — it needs to buffer HLS segments before serving them. The warm pool targets this FFmpeg startup delay.

---

## 7. HLS Streaming Pipeline

### Endpoint Flow

| Step | Endpoint | Controller Method | Purpose |
|------|----------|-------------------|---------|
| 1 | `GET /Videos/{id}/master.m3u8` | `GetMasterHlsVideoPlaylist` | Variant playlist with quality options |
| 2 | `GET /Videos/{id}/main.m3u8` | `GetVariantHlsVideoPlaylist` | Media playlist (computed, no FFmpeg) |
| 3 | `GET /Videos/{id}/hls1/{pid}/{sid}.{ext}` | `GetHlsVideoSegment` | Individual HLS segments |

Alternative: `GET /Videos/{id}/live.m3u8` → `GetLiveHlsStream` — starts FFmpeg immediately, returns playlist directly. Used by some clients for LiveTV.

### FFmpeg Command (typical for remux)

```
ffmpeg
  -i "http://tvheadend:9981/stream/channelid/123"  # Input from IPTV source
  -c:v copy                                          # Copy video codec (no transcode)
  -c:a copy                                          # Copy audio codec
  -f hls                                              # Output format: HLS
  -hls_time 6                                        # 6-second segments
  -hls_list_size 0                                   # Keep all segments in playlist
  -hls_flags append_list                             # Append mode for live
  "/transcodes/{hash}/main.m3u8"                     # Output playlist
```

### Output File Path

Computed in `StreamingHelpers.GetOutputFilePath()` (`Jellyfin.Api/Helpers/StreamingHelpers.cs`):

```csharp
var data = $"{state.MediaPath}-{state.UserAgent}-{deviceId!}-{playSessionId!}";
var filename = data.GetMD5().ToString("N", CultureInfo.InvariantCulture);
```

**Every client session produces a unique OutputFilePath** because `PlaySessionId` is unique per session. This means even two clients watching the same channel with the same encoding parameters get different FFmpeg output paths. The warm pool bypasses this by returning its own playlist path in `TryGetWarmPlaylist()` rather than relying on `OutputFilePath`.

### StreamState Key Fields

`StreamState` (inherits from `EncodingJobInfo`) carries all encoding context through the pipeline. Built in `StreamingHelpers.GetStreamingState()`. Fields relevant to warm pool matching:

| Field | Source | Example |
| --- | --- | --- |
| `MediaSource.Id` | MD5 of stream URL (channel ID) | `"b1b30b87..."` |
| `MediaSource.IsInfiniteStream` | `true` for LiveTV | `true` |
| `OutputVideoCodec` | From `Request.VideoCodec` | `"h264"` |
| `OutputAudioCodec` | From `Request.AudioCodec` | `"aac"` |
| `OutputVideoBitrate` | Calculated from request+stream | `5000000` |
| `OutputAudioBitrate` | Calculated from request+stream | `192000` |
| `OutputWidth` / `OutputHeight` | From request constraints | `1920` / `1080` |
| `OutputAudioChannels` | Calculated | `2` |
| `OutputFilePath` | MD5 hash (see above) | `"/transcodes/a1b2c3d4.m3u8"` |
| `UserAgent` | HTTP header from client | `"AndroidTV/..."` |
| `Request.DeviceId` | Client device ID | `"device-guid"` |
| `Request.PlaySessionId` | Unique per session | `"session-guid"` |

The warm pool check in `DynamicHlsController` occurs **after** `StreamState` is fully built, so all these fields are available at check time.

### TranscodingJob Key Fields

Created in `TranscodeManager.OnTranscodeBeginning()`. Stored in `_activeTranscodingJobs`:

| Field | Purpose |
| --- | --- |
| `Path` | OutputFilePath (the .m3u8 playlist) — used as job identity |
| `Type` | `TranscodingJobType.Hls` |
| `Process` | FFmpeg `Process` handle |
| `PlaySessionId` | Client session ID |
| `DeviceId` | Client device ID |
| `LiveStreamId` | The `ILiveStream` ID (tuner connection) |
| `MediaSource` | `MediaSourceInfo` (includes `.Id`, `.IsInfiniteStream`) |
| `CancellationTokenSource` | Used to signal FFmpeg termination |
| `ActiveRequestCount` | Number of pending segment requests |

**Note**: `TranscodingJob` does NOT currently store encoding parameters (video codec, bitrate, resolution, etc.). Only `MediaSource` and `Path` are available at adoption time in `KillTranscodingJob()`. This is a gap for encoding-parameter-aware warm pool matching.

### TranscodeManager

`MediaBrowser.MediaEncoding/Transcoding/TranscodeManager.cs`:

- Manages FFmpeg process lifecycle
- Tracks active jobs in `_activeTranscodingJobs`
- Kill timer: 60-second timeout for HLS (reset on each segment request)
- `StartFfMpeg()` — spawns process, returns `TranscodingJob`
- `KillTranscodingJob()` — stops process (or offers to warm pool)

---

## 8. DVR Recording System

### Timer Management

| Component | File | Purpose |
|-----------|------|---------|
| `TimerInfo` | `MediaBrowser.Controller/LiveTv/TimerInfo.cs` | Single recording timer (channel, start/end, padding) |
| `SeriesTimerInfo` | `MediaBrowser.Controller/LiveTv/SeriesTimerInfo.cs` | Recurring series recording rules |
| `TimerManager` | `src/Jellyfin.LiveTv/Timers/TimerManager.cs` | Schedules .NET Timers, fires events |
| `SeriesTimerManager` | `src/Jellyfin.LiveTv/Timers/SeriesTimerManager.cs` | Manages series timer persistence |

Timers stored in `{DataPath}/livetv/timers.json` and `seriestimers.json`.

### Recording Flow

```
TimerManager.TimerFired event
  → DefaultLiveTvService.OnTimerManagerTimerFired()
    → RecordingsManager.RecordStream(activeRecordingInfo, channel, endDate)
      1. Determine recording path:
         {RecordingPath}/Series/{ShowName}/Season {N}/{filename}.ts
      2. Open live stream (SEPARATE from live viewers):
         MediaSourceManager.GetPlaybackMediaSources(channel)
         MediaSourceManager.OpenLiveStreamInternal()
      3. Select recorder:
         ├─ DirectRecorder: if source is TS container + HTTP/File protocol
         └─ EncodedRecorder: if re-encoding needed (spawns FFmpeg)
      4. recorder.Record(stream, path, duration, cancellationToken)
      5. On completion: close live stream, save metadata (.nfo), run post-processor
```

### Key Detail: Recordings Open Separate Streams

Each recording opens its own dedicated live stream — it does NOT share with live viewers. `ExclusiveLiveStream` is used with `EnableStreamSharing = false`. Each recording consumes a tuner slot.

### Retry Logic

If a recording fails before `EndDate` and `RetryCount < 10`, it retries after 60 seconds.

---

## 9. Playback Stop and Cleanup

Two independent cleanup paths run sequentially in `PlaystateController.ReportPlaybackStopped`:

### Path 1: TranscodeManager.KillTranscodingJobs()

- Kills FFmpeg process for the device/session
- Deletes HLS segment files
- **OR** offers process to warm pool (see Section 10)

### Path 2: SessionManager.OnPlaybackStopped()

- Updates user watch data
- Calls `CloseLiveStreamIfNeededAsync(liveStreamId, sessionId)`
  - Removes session from `_activeLiveStreamSessions` (a `ConcurrentDictionary<liveStreamId, ConcurrentDictionary<sessionId, playSessionId>>`)
  - **Always** calls `MediaSourceManager.CloseLiveStream(liveStreamId)` when a session was removed
  - `CloseLiveStream()` decrements `ConsumerCount`
  - If `ConsumerCount <= 0`: removes from `_openStreams`, calls `liveStream.Close()`, releases tuner
  - If `ConsumerCount > 0`: stream stays open (this is the warm pool protection path)

These paths are independent — both run regardless of the other's outcome. The `ConsumerCount` pattern ensures the tuner is only released when all references are gone.

**Critical for warm pool**: When a process is adopted, `TranscodeManager` bumps `ConsumerCount` from 1 to 2. Then `SessionManager` calls `CloseLiveStream()` which decrements to 1 — still > 0, so the stream stays open. The plugin is responsible for eventually calling `CloseLiveStream()` again on eviction, which decrements to 0 and actually closes the tuner.

---

## 10. Warm Pool Implementation

### Purpose

Eliminate FFmpeg startup delay (~5+ seconds) when changing LiveTV channels by keeping FFmpeg processes alive in a "warm pool" after the user stops watching.

### Interface

`MediaBrowser.Controller/LiveTv/IWarmProcessProvider.cs`:

```csharp
public interface IWarmProcessProvider
{
    bool TryGetWarmPlaylist(string mediaSourceId, out string? playlistPath);
    bool TryAdoptProcess(string mediaSourceId, string playlistPath,
                         Process ffmpegProcess, string? liveStreamId);
}
```

- **`TryGetWarmPlaylist`**: Called before FFmpeg cold start. Returns true + playlist path if a warm process exists for the requested media source.
- **`TryAdoptProcess`**: Called when killing a transcoding job. Returns true if the provider takes ownership of the FFmpeg process.

### Integration Points

#### DynamicHlsController (check on play)

In `GetLiveHlsStream()`, the warm pool check is **guarded by the playlist-exists check** — it only runs when no FFmpeg is already active for this session. This prevents redundant warm pool queries on every client poll of `live.m3u8`:

```
1. If playlist file already exists (FFmpeg running): skip warm pool check, return playlist
2. If playlist file does NOT exist (cold start needed):
   a. Check if media source is infinite stream (LiveTV)
   b. For each IWarmProcessProvider:
      → TryGetWarmPlaylist(mediaSourceId, encodingProfile, out playlistPath)
      → If HIT and file exists: return cached playlist immediately
      → If MISS: proceed to cold start FFmpeg
   c. Acquire transcode lock, start FFmpeg
```

#### TranscodeManager (offer on stop)

In `KillTranscodingJob()`, before killing FFmpeg:

```
1. Check if stream is infinite (LiveTV) and process is alive
2. For each IWarmProcessProvider:
   → TryAdoptProcess(mediaSourceId, playlistPath, process, liveStreamId)
   → If adopted:
     a. Bump ConsumerCount on live stream (prevents premature closure)
     b. Skip ALL cleanup (CTS cancel, process kill, file delete)
     c. Return early (process ownership transferred to plugin)
   → If not adopted: proceed with normal cleanup
```

### Consumer Count Bump (Critical)

When a warm pool provider adopts a process, `TranscodeManager` bumps the live stream's `ConsumerCount` from 1 to 2. This prevents `SessionManager.CloseLiveStreamIfNeededAsync()` (which runs independently) from closing the tuner connection:

```
Initial:                    ConsumerCount = 1
After warm pool adoption:   ConsumerCount = 2  (TranscodeManager bumps)
SessionManager cleanup:     ConsumerCount = 1  (decrements, but > 0 so stays open)
Plugin eventually stops:    ConsumerCount = 0  (plugin calls CloseLiveStream, tuner released)
```

### Plugin Contract

A warm pool plugin must:
1. Implement `IWarmProcessProvider`
2. Register via DI container
3. On adoption: maintain FFmpeg process, keep playlist files on disk, hold `liveStreamId`
4. On eviction: call `IMediaSourceManager.CloseLiveStream(liveStreamId)` to release tuner
5. Never interact with process/files after returning `false` from `TryAdoptProcess()`

### Plugin Implementation Details (jellyfin-plugin-warmpool v1.3.0)

The plugin is at `C:\sourcecode\GitHub\jellyfin-plugin-warmpool`. Key implementation details needed for development:

**Pool storage**: `ConcurrentDictionary<string, WarmProcessInfo>` keyed by `mediaSourceId`.

**MediaSourceId computation** (must match Jellyfin's `M3UTunerHost` logic):

```csharp
// Plugin: WarmFFmpegProcessPool.ComputeMediaSourceId()
var hash = MD5.HashData(Encoding.Unicode.GetBytes(streamUrl));  // UTF-16LE
return new Guid(hash).ToString("N", CultureInfo.InvariantCulture);
```

This matches how Jellyfin generates `ChannelInfo.Id` in `M3uParser` via `path.GetMD5()`, which also uses `Encoding.Unicode`.

**Plugin FFmpeg arguments** (for manual pre-warm via REST API):

```text
-i "{streamUrl}" -codec copy -f hls -hls_time 3 -hls_list_size 5
-hls_flags delete_segments+append_list -start_number 0
-hls_base_url "hls/{warmPrefix}/"
-hls_segment_filename "{transcodePath}/{warmPrefix}%d.ts"
"{transcodePath}/{warmPrefix}.m3u8"
```

Key: `-codec copy` means remux only (no transcoding). `-hls_list_size 5` keeps a rolling 15-second window. `-hls_flags delete_segments` auto-deletes old segments.

**Playlist readiness check**: Polls every 100ms for up to 15 seconds, waiting for the .m3u8 file to exist and contain at least one `.ts` reference.

**WarmProcessInfo fields**:

| Field | Purpose |
| --- | --- |
| `ChannelId` | Human-readable name (for logging) |
| `MediaSourceId` | Pool lookup key (MD5 of stream URL) |
| `FFmpegProcess` | Running `Process` handle |
| `PlaylistPath` | Path to .m3u8 file on disk |
| `SegmentPrefix` | Filename prefix for segment files |
| `LiveStreamId` | Jellyfin tuner connection ID (null for manual starts) |
| `StreamUrl` | Original IPTV URL (empty for adopted processes) |
| `CreatedTime` | When process was started |
| `LastAccessTime` | Last warm hit timestamp |
| `ConsumerCount` | Number of active clients reading segments |
| `IsRunning` | `Process != null && !Process.HasExited` |

**Singleton pattern**: `WarmPoolManager` uses double-checked locking to ensure one pool instance per Jellyfin process. The pool is lazily created on first `TryGetWarmPlaylist()` or `TryAdoptProcess()` call via `WarmProcessProvider.EnsurePool()`.

**Eviction cleanup** (`StopWarmProcessAsync`):

1. Kill FFmpeg: `process.Kill(entireProcessTree: true)`
2. Delete segment files: all files matching `{segmentPrefix}*` in transcode dir
3. Close live stream: `IMediaSourceManager.CloseLiveStream(liveStreamId)` to release tuner

### MediaSourceManager._openStreams Registry

`Emby.Server.Implementations/Library/MediaSourceManager.cs` maintains a global registry of all open live streams:

```csharp
private readonly ConcurrentDictionary<string, ILiveStream> _openStreams;
private readonly AsyncNonKeyedLocker _liveStreamLocker = new(1);
```

- **`_openStreams`**: keyed by `liveStreamId`, stores `ILiveStream` instances
- **`_liveStreamLocker`**: ensures atomic open/close — only one thread at a time can modify the registry
- **`GetLiveStreamInfo(id)`**: returns `ILiveStream` from registry (used by `TranscodeManager` to bump `ConsumerCount`)
- **`OpenLiveStreamInternal()`**: acquires lock, calls provider, stores stream, optionally probes with FFprobe
- **`CloseLiveStream(id)`**: acquires lock, decrements `ConsumerCount`, closes and removes if zero

### Branch Commits (feature/fastchannelzapping)

| Commit | Description |
|--------|-------------|
| `83a589861` | Add `IWarmProcessProvider` interface and warm pool check in `DynamicHlsController` |
| `c2b1c68d4` | Fix warm pool logging: one-time registration log, promote check/miss to Information |
| `4882d6f78` | Add automatic warm pool adoption in `TranscodeManager.KillTranscodingJob()` |
| `fbbed4e80` | Add `liveStreamId` parameter, implement ConsumerCount bump to prevent premature stream close |
| `0f0c0013a` | Fix warm pool check running on every `live.m3u8` poll — move check inside playlist-exists guard |

---

## 11. Key Files Reference

### API Layer

| File | Key Class | Purpose |
|------|-----------|---------|
| `Jellyfin.Api/Controllers/LiveTvController.cs` | `LiveTvController` | Channel listing, guide, timers |
| `Jellyfin.Api/Controllers/DynamicHlsController.cs` | `DynamicHlsController` | HLS playlist generation, segment serving, warm pool check |
| `Jellyfin.Api/Controllers/PlaystateController.cs` | `PlaystateController` | Playback stop reporting |
| `Jellyfin.Api/Helpers/StreamingHelpers.cs` | `StreamingHelpers` | Build StreamState from request parameters |

### Service Layer

| File | Key Class | Purpose |
|------|-----------|---------|
| `Emby.Server.Implementations/Library/MediaSourceManager.cs` | `MediaSourceManager` | Open/close live streams, manage `_openStreams` registry |
| `Emby.Server.Implementations/Session/SessionManager.cs` | `SessionManager` | Track playback sessions, trigger cleanup |
| `MediaBrowser.MediaEncoding/Transcoding/TranscodeManager.cs` | `TranscodeManager` | FFmpeg lifecycle, warm pool adoption |

### LiveTV Core

| File | Key Class | Purpose |
|------|-----------|---------|
| `src/Jellyfin.LiveTv/DefaultLiveTvService.cs` | `DefaultLiveTvService` | Orchestrate tuners, stream reuse, timer firing |
| `src/Jellyfin.LiveTv/LiveTvMediaSourceProvider.cs` | `LiveTvMediaSourceProvider` | IMediaSourceProvider for LiveTV |
| `src/Jellyfin.LiveTv/TunerHosts/M3UTunerHost.cs` | `M3UTunerHost` | M3U/IPTV tuner implementation |
| `src/Jellyfin.LiveTv/TunerHosts/M3uParser.cs` | `M3uParser` | M3U playlist parsing |
| `src/Jellyfin.LiveTv/TunerHosts/BaseTunerHost.cs` | `BaseTunerHost` | Shared tuner logic, caching |
| `src/Jellyfin.LiveTv/TunerHosts/TunerHostManager.cs` | `TunerHostManager` | Tuner registration and validation |

### Stream Classes

| File | Key Class | Purpose |
|------|-----------|---------|
| `src/Jellyfin.LiveTv/TunerHosts/LiveStream.cs` | `LiveStream` | Base stream class |
| `src/Jellyfin.LiveTv/TunerHosts/SharedHttpStream.cs` | `SharedHttpStream` | HTTP stream with temp file buffering |
| `src/Jellyfin.LiveTv/IO/ExclusiveLiveStream.cs` | `ExclusiveLiveStream` | Non-shareable stream (recordings) |

### Recording System

| File | Key Class | Purpose |
|------|-----------|---------|
| `src/Jellyfin.LiveTv/Recordings/RecordingsManager.cs` | `RecordingsManager` | Recording orchestration |
| `src/Jellyfin.LiveTv/IO/DirectRecorder.cs` | `DirectRecorder` | Direct stream-to-file recording |
| `src/Jellyfin.LiveTv/IO/EncodedRecorder.cs` | `EncodedRecorder` | FFmpeg-based recording |
| `src/Jellyfin.LiveTv/Timers/TimerManager.cs` | `TimerManager` | Timer scheduling |

### Interfaces

| File | Key Interface | Purpose |
|------|---------------|---------|
| `MediaBrowser.Controller/LiveTv/ITunerHost.cs` | `ITunerHost` | Tuner host contract |
| `MediaBrowser.Controller/LiveTv/ILiveTvService.cs` | `ILiveTvService` | LiveTV backend provider contract |
| `MediaBrowser.Controller/LiveTv/IWarmProcessProvider.cs` | `IWarmProcessProvider` | Warm pool plugin contract |
| `MediaBrowser.Controller/Library/ILiveStream.cs` | `ILiveStream` | Live stream contract |
| `MediaBrowser.Controller/Library/IMediaSourceManager.cs` | `IMediaSourceManager` | Media source management contract |

---

## 12. Design Considerations for Fast Channel Zapping

### Core Design Goal

**FFmpeg is the primary bottleneck.** When a client tunes a LiveTV channel that requires remuxing or transcoding, FFmpeg must:

1. Establish an HTTP connection to the mpegts source (TVHeadend)
2. Buffer incoming data
3. Probe codec information (video codec, audio codec, bitrate, resolution)
4. Build up enough data to begin producing output segments

This takes 5-10+ seconds. The warm pool eliminates this by keeping FFmpeg processes alive and continuously consuming the mpegts stream from TVHeadend. When a client re-tunes, HLS segments are already on disk and can be served immediately.

```text
TVHeadend ──(HTTP mpegts)──> FFmpeg (kept alive in warm pool)
                                 │
                                 ├──> HLS segments on disk (continuously written)
                                 │
                             Client tunes channel
                                 │
                                 └──> Immediate HLS playlist served (~100-200ms)
```

The TVHeadend connection stays alive because FFmpeg is consuming from it. Keeping FFmpeg alive implicitly keeps the IPTV connection alive.

### Current State

The warm pool implementation on `feature/fastchannelzapping` addresses FFmpeg startup delay by:

1. Offering live FFmpeg processes to a plugin on playback stop
2. Checking the plugin for cached playlists on playback start
3. Using ConsumerCount bumping to keep tuner connections alive

### Encoding Parameter Matching

Different clients may request different transcoding parameters for the same channel (e.g., H.264 vs H.265, 1080p vs 720p, different bitrates). A warm pool FFmpeg process can only serve a client whose requested encoding parameters match what FFmpeg is already producing.

Currently, the warm pool is keyed only by `mediaSourceId` (channel identity). It does not consider encoding parameters. This needs to be extended so the pool key includes the encoding profile (video codec, audio codec, resolution, bitrate).

### Multiple Clients on Same Channel

When multiple clients request the same channel with the same encoding parameters:

- They share the same `SharedHttpStream` to TVHeadend (via `ConsumerCount`)
- Each client currently gets its own FFmpeg process (because `OutputFilePath` includes `PlaySessionId`)
- With the warm pool, a second client can read from the same HLS segments that the first client's FFmpeg is producing
- The warm pool holds one entry per channel+encoding-profile combination

### What the Warm Pool Handles

- **Remux/Transcode scenarios**: FFmpeg process is kept alive, HLS segments continue to be written
- **Same channel re-tune with matching encoding params**: Near-instant because warm playlist and segments already exist on disk
- **TVHeadend connection**: Stays alive because FFmpeg continues to consume from it

### What the Warm Pool Does NOT Currently Handle

- **Direct streaming (no FFmpeg)**: The warm pool only operates on transcoding jobs. If a stream is direct play without FFmpeg, there's no process to warm-pool. A future `IWarmStreamProvider` interface could keep `SharedHttpStream` connections alive for this case.
- **Different channel tune**: A new channel requires a new FFmpeg process and IPTV connection.
- **Mismatched encoding parameters**: A warm entry for H.264 1080p cannot serve a client requesting H.265 720p.

### Key Timing Breakdown

| Phase | Direct Stream | Remux (HLS) | Transcode |
|-------|--------------|-------------|-----------|
| Open live stream (IPTV connect) | ~0.5-2s | ~0.5-2s | ~0.5-2s |
| FFmpeg startup + buffering + probe | N/A | ~5-8s | ~5-10s |
| Client buffering | ~1-3s | ~1-2s | ~1-2s |
| **Total cold start** | **~1.5-5s** | **~6-12s** | **~7-14s** |
| **With warm pool** | N/A (not applicable yet) | **~100-200ms** | **~100-200ms** |

### Architecture Implications

1. **Plugin-based design** is correct — keeps warm pool logic out of Jellyfin core, server provides only thin hook interfaces
2. **FFmpeg is the key resource** — the warm pool fundamentally keeps FFmpeg alive on the mpegts stream; the TVHeadend connection is a side effect of this
3. **Encoding parameter matching** is critical — warm hits must only occur when channel AND transcoding profile match
4. **ConsumerCount pattern** is essential for preventing premature stream/tuner closure
5. **Pool size** limits resource consumption — each warm process uses CPU, memory, bandwidth, and a tuner slot
6. **Multiple warm pool providers** are supported (first-to-adopt wins), allowing different strategies
7. **Direct streaming optimization** (Phase 3) would require changes to `SharedHttpStream` and `MediaSourceManager`, not `TranscodeManager`

---

*This document should be updated as the warm pool feature evolves and new findings emerge.*
