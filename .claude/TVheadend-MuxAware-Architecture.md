# TVheadend Mux-Aware Warm Pool Architecture

**Status**: Research and Design Phase
**Last Updated**: 2026-02-01
**Related Docs**: [CLAUDE.md](CLAUDE.md), [LiveTV-Architecture.md](LiveTV-Architecture.md), [WarmPool-ChangePlan.md](WarmPool-ChangePlan.md)

---

## Table of Contents

1. [Executive Summary](#executive-summary)
2. [Problem Statement](#problem-statement)
3. [TVheadend Architecture Deep Dive](#tvheadend-architecture-deep-dive)
4. [Current Jellyfin Limitations](#current-jellyfin-limitations)
5. [Architectural Solutions](#architectural-solutions)
6. [Implementation Approaches](#implementation-approaches)
7. [API and Protocol Details](#api-and-protocol-details)
8. [Scenarios and Examples](#scenarios-and-examples)
9. [Recommendations](#recommendations)

---

## Executive Summary

**The Problem**: Jellyfin's warm pool currently treats each channel as requiring a separate tuner, but TVheadend can serve **multiple channels from the same mux (frequency) using only ONE tuner**. This creates a critical resource management mismatch where:

- 30 warm pool streams may only consume 4 physical tuners (if all from 4 muxes)
- A 5th user requesting a new mux gets blocked even though tuners are "available"
- Warm pool eviction is channel-based, not mux-based, leading to poor tuner utilization

**The Solution**: Make the warm pool **mux-aware** so it can:
1. Detect which channels share the same TVheadend mux/frequency
2. Prioritize evicting entire muxes when real tuner pressure occurs
3. Allow unlimited warm streams **per mux** (since they share one tuner)
4. Aggressively evict unused muxes when a new mux is requested but tuners are full

**Key Insight**: For TVheadend, the **mux is the tuner resource**, not the channel.

---

## Problem Statement

### TVheadend's Mux Architecture

In digital TV broadcasting (ATSC, DVB-T, DVB-S, DVB-C, IPTV), a **multiplex (mux)** is a single radio frequency that carries **multiple services (channels)**:

```
Frequency 509 MHz (Mux "BBC")
├── Service 1: BBC One HD (channel 101.1)
├── Service 2: BBC Two HD (channel 102.1)
├── Service 3: BBC News HD (channel 103.1)
└── Service 4: CBBC HD (channel 104.1)

Frequency 538 MHz (Mux "ITV")
├── Service 1: ITV HD (channel 201.1)
├── Service 2: ITV2 HD (channel 202.1)
└── Service 3: ITV3 HD (channel 203.1)
```

**Key principle**: When a tuner is locked to a mux (frequency), TVheadend can extract **ANY or ALL services** from that mux simultaneously without additional tuner cost.

**Real-world scenario** (4 tuners, ATSC OTA):
- User A watches channel 5.1, 5.2, 5.3, 5.4 (all from 509 MHz) = **1 tuner**
- User B watches channel 9.1, 9.2 (both from 538 MHz) = **1 tuner**
- User C watches channel 12.1, 12.2, 12.3 (all from 602 MHz) = **1 tuner**
- User D watches channel 19.1 (from 671 MHz) = **1 tuner**
- **Total**: 10 active streams, 4 tuners consumed ✅

**Problem case** (warm pool without mux awareness):
- Warm pool has 30 cached channels from 4 different muxes
- Jellyfin thinks 30 "tuners" are in use (TunerCount logic)
- User E requests channel 48.1 (new mux, 755 MHz)
- **Result**: Blocked by tuner limit, even though only 4 physical tuners are in use ❌

### IPTV and TVheadend "Muxes"

For **IPTV sources** in TVheadend:
- Each IPTV stream URL is typically treated as a separate mux
- TVheadend can still multiplex if the source provides multi-program transport streams
- Most IPTV providers send **one service per URL** (single-program transport stream)
- **Implication**: IPTV channels usually have 1:1 mux-to-channel mapping
- **Warm pool limit (generic IPTV)**: enforce `min(PoolSize - 1, TunerCount - 1)` per tuner host and evict within the same host to avoid exhausting provider stream limits

**Important**: The mux-aware architecture is most critical for **OTA (over-the-air)** and **cable/satellite** sources where true RF multiplexing occurs. For pure IPTV, the benefit is reduced but the architecture still helps with prioritization.

---

## TVheadend Architecture Deep Dive

### Component Hierarchy

Sources: [TVheadend Concepts](https://docs.tvheadend.org/documentation/setup/concepts), [Configure TVheadend](https://profyaffle.github.io/versionB/configure_tvheadend/)

```
┌─────────────────────────────────────────────────────────────┐
│ Adapter (Physical Tuner Hardware)                           │
│ ├── Network (DVB-T, DVB-S, ATSC, IPTV)                      │
│ │   ├── Mux (Frequency/Transponder/URL)                     │
│ │   │   ├── Service (TV/Radio Program)                      │
│ │   │   │   └── Channel (User-facing mapping)               │
│ │   │   ├── Service                                         │
│ │   │   └── Service                                         │
│ │   └── Mux                                                 │
│ └── Network                                                 │
└─────────────────────────────────────────────────────────────┘
```

**Relationships**:
- **Many-to-many at every level**
- One tuner can serve multiple networks
- One network can exist on multiple tuners
- One mux contains 1-10+ services (typically 4-8 for OTA)
- One service can be mapped to multiple channels (regional variations)

### Tuner Allocation Mechanism

**When a client subscribes to a channel** (via HTSP or HTTP):

1. **Channel → Service Resolution**: TVheadend looks up which service(s) provide this channel
2. **Service → Mux Resolution**: Identifies which mux contains the service
3. **Mux → Tuner Allocation**:
   - Check if **ANY tuner is already locked to this mux** → **Reuse tuner** (increment subscription count)
   - If not, find an available tuner → Lock to mux frequency
   - If no tuners available → **Subscription fails**

**Critical insight**: Multiple subscriptions to services on the **same mux** share ONE tuner. The tuner subscription count can be much higher than the physical tuner count.

### Example: 4 Physical Tuners Serving 20 Clients

| Tuner | Locked to Mux | Active Subscriptions (Channels) | HTTP Clients |
|-------|---------------|----------------------------------|--------------|
| Tuner 0 | 509 MHz (BBC) | BBC One HD, BBC Two HD, BBC News HD, CBBC HD | 8 clients |
| Tuner 1 | 538 MHz (ITV) | ITV HD, ITV2 HD, ITV3 HD | 5 clients |
| Tuner 2 | 602 MHz (PBS) | PBS HD, PBS Kids HD | 4 clients |
| Tuner 3 | 671 MHz (Fox) | Fox HD, Fox News HD, FS1 HD | 3 clients |
| **Total** | **4 muxes** | **14 unique services** | **20 concurrent streams** |

**Tuner resource**: 4/4 used (100%)
**Service diversity**: 14 channels available
**Client capacity**: 20 simultaneous viewers

If a 21st client requests a channel from **any of these 4 muxes**, TVheadend serves it **without allocating a new tuner**. If they request a channel from a **5th mux** (e.g., 755 MHz), the subscription **fails** due to no available tuners.

---

## Current Jellyfin Limitations

### How Jellyfin Sees TVheadend

Based on codebase exploration (see [exploration report](#jellyfin-tvheadend-interaction-summary)), Jellyfin treats TVheadend as a **black-box IPTV provider**:

1. **M3U Playlist Fetching**:
   - TVheadend exposes: `http://tvheadend:9981/playlist` (M3U format)
   - Jellyfin parses this once, caches channel list
   - Each channel has a `Path` = HTTP stream URL

2. **Channel Identification**:
   - `Channel.Id = MD5(streamUrl)` using UTF-16LE encoding
   - Example: `http://tvheadend:9981/stream/channel/abc123` → `"7f8e9d6c5b4a3e2f1d0c9b8a7e6d5c4b"`

3. **Tuner Count Limiting** ([M3UTunerHost.cs:78-91](../src/Jellyfin.LiveTv/TunerHosts/M3UTunerHost.cs#L78-L91)):
   ```csharp
   var tunerCount = tunerHost.TunerCount;  // Configured in UI
   if (tunerCount > 0)
   {
       var liveStreams = currentLiveStreams.Where(i =>
           string.Equals(i.TunerHostId, tunerHostId, StringComparison.OrdinalIgnoreCase));

       if (liveStreams.Count() >= tunerCount)
       {
           throw new LiveTvConflictException("M3U simultaneous stream limit has been reached.");
       }
   }
   ```

   **Problem**: Counts `ILiveStream` instances (HTTP connections to TVheadend), not actual TVheadend tuner usage.

4. **No Mux Awareness**:
   - Jellyfin has **zero concept** of mux/frequency/service relationships
   - Each channel URL is treated as independent
   - No mux ID parsing from TVheadend responses
   - No shared resource detection

### Stream Reuse vs Mux Sharing

**Jellyfin's stream reuse** ([DefaultLiveTvService.cs:462-502](../Emby.Server.Implementations/LiveTv/DefaultLiveTvService.cs#L462-L502)):
- Reuses `ILiveStream` if `OriginalStreamId` (channel URL) matches
- Increments `ConsumerCount` (multiple Jellyfin clients share one HTTP connection to TVheadend)
- **Scope**: Jellyfin-side connection pooling only

**TVheadend's mux sharing**:
- Different HTTP stream URLs can share the same tuner (if same mux)
- Jellyfin creates separate HTTP connections for each channel
- TVheadend backend multiplexes these from one tuner
- **Scope**: TVheadend-side tuner multiplexing (invisible to Jellyfin)

**Key mismatch**: Jellyfin's `currentLiveStreams.Count()` counts HTTP connections, not tuners. With 4 physical tuners and 20 HTTP connections to channels on 4 muxes, Jellyfin thinks 20 "tuners" are in use.

### Warm Pool Exacerbates the Problem

With the warm pool plugin (v1.8.0):

1. **Pool Size Configuration**: `PoolSize = 3` (default)
   - Jellyfin thinks this means "3 tuners worth of cached streams"
   - Reality: Could be 3 streams from same mux (0.25 tuners) or 3 streams from 3 muxes (3 tuners)

2. **Eviction Logic** ([WarmFFmpegProcessPool.cs](../../jellyfin-plugin-warmpool/WarmFFmpegProcessPool.cs)):
   ```csharp
   var score = historyPriority - (idleMinutes / 60.0) + orphanPenalty + fairnessPenalty;
   ```
   - Evicts based on **channel popularity**, not mux resource pressure
   - A highly popular channel from a unique mux might stay cached
   - 10 unpopular channels from one mux might get evicted, freeing zero tuners

3. **No Tuner Pressure Detection**:
   - Plugin doesn't know when TVheadend tuners are actually full
   - Can't proactively evict to free real tuner resources
   - Only evicts based on pool size (arbitrary stream count)

### The 30 Streams / 4 Tuners Scenario

**Setup**:
- TVheadend: 4 physical tuners, OTA ATSC
- Jellyfin warm pool: `PoolSize = 30` (aggressive caching)
- Users flip through channels over time, pool fills with:
  - 15 streams from Mux A (509 MHz): channels 5.1, 5.2, 5.3, etc.
  - 8 streams from Mux B (538 MHz): channels 9.1, 9.2, 9.3, etc.
  - 5 streams from Mux C (602 MHz): channels 12.1, 12.2, etc.
  - 2 streams from Mux D (671 MHz): channels 19.1, 19.2

**Actual tuner usage**: 4/4 tuners locked (Mux A, B, C, D)

**New user requests channel 48.1** (Mux E, 755 MHz):
1. Warm pool has no entry for 48.1 → cold start
2. Jellyfin requests `http://tvheadend:9981/stream/channel/48-1` (new HTTP connection)
3. **TVheadend**: Needs to lock a tuner to Mux E (755 MHz)
4. **TVheadend**: All 4 tuners are busy → **Subscription fails**
5. **Jellyfin/User**: Stream fails to start, error message

**What SHOULD happen** (mux-aware eviction):
1. Detect tuner pressure (TVheadend has no free tuners)
2. Identify that warm pool holds 30 streams from only 4 muxes
3. Pick least valuable **mux** (e.g., Mux D with only 2 cached streams, low popularity)
4. Evict **all streams from Mux D** (19.1, 19.2) → frees Tuner 3
5. TVheadend locks Tuner 3 to Mux E
6. Channel 48.1 streams successfully

**Current behavior**: Pool evicts 1 channel (LRU, probably from Mux A since it has 15) → frees zero tuners → stream still fails.

---

## Architectural Solutions

### Solution 1: Mux ID Extraction from TVheadend URLs

**Concept**: Parse TVheadend's channel URLs to extract mux UUID, group warm pool entries by mux.

#### TVheadend URL Formats

Source: [url.md](https://github.com/tvheadend/tvheadend/blob/master/docs/markdown/url.md)

TVheadend provides several stream URL patterns:

| URL Pattern | Example | Mux Visibility |
|-------------|---------|----------------|
| `/stream/channel/{uuid}` | `/stream/channel/abc123def456` | Not in URL |
| `/stream/channelid/{id}` | `/stream/channelid/1234` | Not in URL |
| `/stream/service/{uuid}` | `/stream/service/svc-uuid-here` | Not in URL |
| `/stream/mux/{uuid}` | `/stream/mux/mux-uuid-here` | ✅ **Mux UUID in URL** |

**Problem**: M3U playlists from TVheadend use `/stream/channel/{uuid}` or `/stream/channelid/{id}` format by default. The mux UUID is **not exposed in the stream URL**.

**Possible approaches**:

1. **Parse TVheadend JSON API** to build channel → mux mapping table:
   - Fetch `/api/channel/grid` (all channels with mux references)
   - Fetch `/api/mpegts/mux/grid` (all muxes with UUIDs)
   - Build: `{channelUuid: muxUuid}` lookup table
   - **Pros**: Accurate mux grouping
   - **Cons**: Requires TVheadend API access, credentials, parsing JSON

2. **Use TVheadend's M3U extended attributes**:
   - TVheadend can include custom `#EXTINF` tags in M3U
   - Check if mux ID is in `tvg-id`, `group-title`, or other fields
   - **Pros**: No extra API calls
   - **Cons**: Not standard, depends on TVheadend config

3. **HTSP Protocol Query**:
   - Connect via HTSP, query channel → service → mux hierarchy
   - **Pros**: Native protocol, complete data model
   - **Cons**: Complex implementation, requires HTSP client library

#### Recommended Approach: JSON API Polling

**Implementation** (in `jellyfin-plugin-warmpool`):

```csharp
public class TVheadendMuxMapper
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Dictionary<string, string> _channelToMux = new();  // {channelUuid: muxUuid}

    public async Task<string?> GetMuxIdForChannel(string streamUrl)
    {
        // Extract channel UUID from streamUrl
        var match = Regex.Match(streamUrl, @"/stream/channel/([a-f0-9-]+)");
        if (!match.Success) return null;

        var channelUuid = match.Groups[1].Value;

        // Lookup in cached mapping
        if (_channelToMux.TryGetValue(channelUuid, out var muxUuid))
            return muxUuid;

        // Refresh mapping if not found
        await RefreshMappingAsync();
        _channelToMux.TryGetValue(channelUuid, out muxUuid);
        return muxUuid;
    }

    private async Task RefreshMappingAsync()
    {
        // Fetch: GET http://tvheadend:9981/api/channel/grid
        var channels = await FetchJsonArrayAsync("/api/channel/grid");

        foreach (var channel in channels)
        {
            var channelUuid = channel["uuid"]?.ToString();
            var services = channel["services"]?.ToArray();  // Array of service UUIDs

            if (services?.Length > 0)
            {
                // Fetch service to get mux: GET /api/mpegts/service/load?uuid={serviceUuid}
                var service = await FetchJsonAsync($"/api/mpegts/service/load?uuid={services[0]}");
                var muxUuid = service["multiplex"]?.ToString();  // Mux UUID

                if (!string.IsNullOrEmpty(channelUuid) && !string.IsNullOrEmpty(muxUuid))
                {
                    _channelToMux[channelUuid] = muxUuid;
                }
            }
        }
    }
}
```

**Integration points**:
- `WarmFFmpegProcessPool.AdoptProcess()`: Call `GetMuxIdForChannel(mediaSourceId)`, store in `WarmProcessInfo.MuxId`
- `WarmProcessInfo.cs`: Add `public string? MuxId { get; set; }`
- `EvictLeastValuableProcess()`: Group by `MuxId`, score entire mux groups
- `PluginConfiguration.cs`: Add `TVheadendApiUrl`, `TVheadendUsername`, `TVheadendPassword`

**Challenges**:
- **API Authentication**: TVheadend API requires HTTP digest auth (username/password)
- **API Versioning**: JSON API structure may change between TVheadend versions
- **Performance**: API calls add latency (mitigate with caching + background refresh)
- **IPTV Sources**: IPTV "muxes" in TVheadend are synthetic, 1:1 with channels (less benefit)

---

### Solution 2: TVheadend Tuner Status API Polling

**Concept**: Query TVheadend's real-time tuner status to detect when tuners are full, trigger aggressive eviction.

#### TVheadend Inputs API

Source: [TVheadend JSON API](https://docs.tvheadend.org/documentation/development/json-api/api-description/mpegts)

TVheadend exposes tuner (adapter) status via:
- **Endpoint**: `/api/status/inputs` (fallback: `/api/hardware/input/grid` for older builds)
- **Response**: Array of tuner objects with fields:
  - `uuid`: Tuner UUID
  - `name`: Tuner name (e.g., "Hauppauge WinTV-dualHD DVB #0")
  - `currentMux`: UUID of currently tuned mux (or null if idle)
  - `subscribers`: Number of active subscriptions on this tuner
  - `enabled`: Whether tuner is enabled

**Example response** (4 tuners, 3 in use):
```json
[
  {"uuid": "tuner0", "name": "DVB-T #0", "currentMux": "mux-509mhz", "subscribers": 8, "enabled": true},
  {"uuid": "tuner1", "name": "DVB-T #1", "currentMux": "mux-538mhz", "subscribers": 5, "enabled": true},
  {"uuid": "tuner2", "name": "DVB-T #2", "currentMux": "mux-602mhz", "subscribers": 4, "enabled": true},
  {"uuid": "tuner3", "name": "DVB-T #3", "currentMux": null, "subscribers": 0, "enabled": true}
]
```

**Tuner pressure detection**:
```csharp
var freeEnabled TunersCount = tuners.Count(t => t.enabled && t.currentMux == null);
var isTunerPressure = freeTunersCount == 0;
```

#### Dynamic Eviction Strategy

**Trigger**: Poll `/api/status/inputs` every 10 seconds (configurable; fallback to `/api/hardware/input/grid` if needed).

**Logic**:
1. **No pressure** (`freeTunersCount > 0`):
   - Use standard eviction (idle timeout, orphan penalty, fairness)
   - Keep muxes cached even if some tuners are busy

2. **Tuner pressure** (`freeTunersCount == 0`):
   - Switch to **mux-based eviction**
   - For each cached mux, calculate **mux score**:
     ```csharp
     muxScore = (streamCountInMux * avgHistoryPriority) - (avgIdleMinutes / 60.0) + orphanBonus
     ```
   - Evict **entire lowest-scoring mux** (all streams from that mux)
   - Repeat until `freeTunersCount > 0` or pool size < threshold
   - **Fallback:** If no mux group is eligible (all have active consumers), evict any **idle** warm entry (process or stream) to free a tuner without interrupting active viewers.

3. **No tuner pressure** (`freeTunersCount > 0`):
   - Skip idle-timeout eviction in mux-aware mode to keep warm entries longer.

**Benefits**:
- **Reactive**: Only evicts aggressively when tuners are actually full
- **Mux-aware**: Frees real tuner resources by evicting entire muxes
- **Preserves popular muxes**: High-traffic muxes stay cached even under pressure

**Challenges**:
- **API polling overhead**: 10s interval = 6 requests/min/server (lightweight JSON)
- **Race conditions**: Tuner status may change between poll and eviction
- **IPTV noise**: IPTV "muxes" pollute the tuner list (need filtering by tuner type)

**Implementation** (in plugin):

```csharp
public class TVheadendTunerMonitor : IHostedService
{
    private Timer? _timer;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(CheckTunerPressure, null, TimeSpan.Zero, TimeSpan.FromSeconds(10));
        return Task.CompletedTask;
    }

    private async void CheckTunerPressure(object? state)
    {
        var tuners = await FetchTunerStatusAsync();  // GET /api/status/inputs (fallback: /api/hardware/input/grid)
        var freeTuners = tuners.Count(t => t.Enabled && string.IsNullOrEmpty(t.CurrentMux));

        if (freeTuners == 0)
        {
            _logger.LogWarning("TVheadend tuner pressure detected (0 free tuners), triggering mux-aware eviction");
            WarmPoolManager.ProcessPoolInstance?.EvictLeastValuableMux();
        }
    }
}
```

---

### Solution 3: HTSP Native Integration (Advanced)

**Concept**: Replace Jellyfin's HTTP M3U→TVheadend connection with native HTSP protocol, gaining full mux hierarchy visibility.

#### Why HTSP?

Sources: [HTSP General](https://docs.tvheadend.org/documentation/development/htsp/general), [Client-to-Server RPC](https://docs.tvheadend.org/documentation/development/htsp/client-to-server-rpc-methods)

**HTSP advantages over HTTP streaming**:
1. **Full metadata**: Channel → Service → Mux → Tuner hierarchy exposed
2. **Native subscriptions**: TVheadend manages tuner allocation, Jellyfin just subscribes
3. **EPG integration**: Real-time EPG data (not reliant on XMLTV)
4. **Efficient protocol**: Binary format, lower overhead than HTTP chunked transfer
5. **Timeshift support**: Native DVR/timeshift capabilities

**HTSP disadvantages**:
1. **Complexity**: Binary protocol (HTSMSG format), not simple HTTP
2. **Library dependency**: Need HTSP client library (no official C# library)
3. **Jellyfin architecture**: Would require new `IHtspTunerHost` implementation
4. **Breaking change**: Existing Jellyfin M3U configs wouldn't work

#### HTSP Subscription Flow

**Subscribe RPC method** (from HTSP docs):

```
Request:
{
  "method": "subscribe",
  "channelId": 123,
  "subscriptionId": 1,
  "weight": 100,
  "profile": "htsp",  // Stream profile UUID
  "timeshiftPeriod": 3600
}

Response:
{
  "subscriptionId": 1,
  "streams": [
    {"index": 0, "type": "H264", "width": 1920, "height": 1080},
    {"index": 1, "type": "AAC", "channels": 2, "rate": 48000}
  ],
  "sourceinfo": {
    "mux": "mux-uuid-here",
    "adapter": "adapter-uuid-here",
    "service": "service-uuid-here"
  }
}
```

**Key benefit**: `sourceinfo.mux` provides the mux UUID directly in the subscription response. No separate API query needed.

#### Architectural Changes Required

**New Jellyfin server interfaces** (in `MediaBrowser.Controller`):

```csharp
namespace MediaBrowser.Controller.LiveTv
{
    public interface IHtspTunerHost : ITunerHost
    {
        Task<HtspSubscription> SubscribeAsync(int channelId, string profile, CancellationToken cancellationToken);
        Task UnsubscribeAsync(int subscriptionId);
        Task<IEnumerable<HtspMux>> GetMuxesAsync();
    }

    public class HtspSubscription
    {
        public int SubscriptionId { get; set; }
        public string MuxUuid { get; set; }
        public string AdapterUuid { get; set; }
        public Stream DataStream { get; set; }  // MPEG-TS packets
    }
}
```

**Plugin implementation** (in `jellyfin-plugin-warmpool`):

```csharp
public class HtspWarmPool
{
    private readonly Dictionary<string, List<HtspSubscription>> _subscriptionsByMux = new();

    public async Task<HtspSubscription> GetOrSubscribeAsync(int channelId)
    {
        // Check if we already have a subscription to this channel's mux
        var channel = await _htspClient.GetChannelAsync(channelId);
        var muxUuid = channel.MuxUuid;

        if (_subscriptionsByMux.TryGetValue(muxUuid, out var subs))
        {
            // Reuse existing subscription from this mux (warm hit)
            var existing = subs.FirstOrDefault(s => s.ChannelId == channelId);
            if (existing != null) return existing;
        }

        // Cold start: create new HTSP subscription
        var newSub = await _htspTunerHost.SubscribeAsync(channelId, "htsp", CancellationToken.None);

        // Store grouped by mux
        if (!_subscriptionsByMux.ContainsKey(muxUuid))
            _subscriptionsByMux[muxUuid] = new List<HtspSubscription>();

        _subscriptionsByMux[muxUuid].Add(newSub);
        return newSub;
    }

    public async Task EvictLeastValuableMuxAsync()
    {
        // Find mux with lowest score
        var leastValuableMux = _subscriptionsByMux
            .OrderBy(kvp => CalculateMuxScore(kvp.Value))
            .FirstOrDefault();

        if (leastValuableMux.Value == null) return;

        // Unsubscribe all channels from this mux
        foreach (var sub in leastValuableMux.Value)
        {
            await _htspTunerHost.UnsubscribeAsync(sub.SubscriptionId);
        }

        _subscriptionsByMux.Remove(leastValuableMux.Key);
        _logger.LogInformation("Evicted entire mux {MuxUuid} ({Count} subscriptions)",
            leastValuableMux.Key, leastValuableMux.Value.Count);
    }
}
```

#### Scope of Changes

**Server-side** (`jellyfin` repo):
- New `IHtspTunerHost` interface in `MediaBrowser.Controller/LiveTv/`
- HTSP client library integration (e.g., port [python-htsp](https://github.com/dpcharlton/python-htsp) to C#)
- `Jellyfin.LiveTv` project: new `HtspTunerHost.cs` implementation
- UI changes: new "TVheadend (HTSP)" tuner type in Live TV settings

**Plugin-side** (`jellyfin-plugin-warmpool` repo):
- Detect if tuner host is HTSP vs HTTP
- Use mux-aware pooling for HTSP, channel-based for HTTP
- Handle both in parallel (hybrid support)

**Effort estimate**: 40-80 hours (protocol implementation, testing, debugging)

**Risk**: High complexity, requires deep TVheadend protocol knowledge, potential for bugs.

---

### Solution 4: Heuristic Mux Grouping (TVheadend-Agnostic)

**Concept**: Use statistical heuristics to **infer** which channels share tuner resources, without querying TVheadend.

#### Detection Signals

**1. Simultaneous Stream Success Rate**:
- If channels A and B can **always** be streamed simultaneously, they likely share a mux
- If channel C **blocks** when A+B are active (tuner full error), C is on a different mux

**2. Stream URL Patterns**:
- Some IPTV providers encode frequency in URL: `/stream/freq-509mhz/channel-5-1`
- Parse URL for frequency/mux hints

**3. Channel Number Grouping** (ATSC):
- Channels `5.1, 5.2, 5.3, 5.4` are almost always on the same mux (RF channel 5)
- Group by primary channel number (before decimal)

**4. EPG Data Correlation**:
- TVheadend EPG often includes `<lcn>` (logical channel number) with mux hints
- Parse XMLTV EPG for mux indicators

#### Heuristic Algorithm

```csharp
public class MuxHeuristicGrouper
{
    private readonly Dictionary<string, HashSet<string>> _muxGroups = new();  // {muxHintId: [channelIds]}

    public string GetMuxHint(string channelId, string streamUrl, string channelNumber)
    {
        // Heuristic 1: Channel number grouping (ATSC)
        if (channelNumber.Contains("."))
        {
            var primaryChannel = channelNumber.Split('.')[0];
            return $"atsc-rf-{primaryChannel}";  // e.g., "atsc-rf-5"
        }

        // Heuristic 2: URL pattern matching
        var freqMatch = Regex.Match(streamUrl, @"freq-(\d+)mhz");
        if (freqMatch.Success)
            return $"freq-{freqMatch.Groups[1].Value}";

        // Heuristic 3: Assume each channel is unique mux (fallback)
        return $"channel-{channelId}";
    }

    public void GroupChannel(string channelId, string muxHint)
    {
        if (!_muxGroups.ContainsKey(muxHint))
            _muxGroups[muxHint] = new HashSet<string>();

        _muxGroups[muxHint].Add(channelId);
    }

    public IEnumerable<string> GetChannelsInSameMux(string channelId)
    {
        var muxHint = _muxGroups.FirstOrDefault(kvp => kvp.Value.Contains(channelId)).Key;
        return muxHint != null ? _muxGroups[muxHint] : Enumerable.Empty<string>();
    }
}
```

#### Pros and Cons

**Pros**:
- ✅ No TVheadend API dependency
- ✅ Works with any IPTV provider (generic)
- ✅ Simple implementation
- ✅ No authentication needed

**Cons**:
- ❌ Inaccurate for non-standard channel numbering
- ❌ Doesn't work for IPTV (no .1/.2 subchannel pattern)
- ❌ Can't detect actual tuner pressure
- ❌ Eviction still based on guesses, not real mux data

**Best use case**: Quick win for **ATSC OTA** sources where channel numbering is reliable (5.1, 5.2, 9.1, 12.1, etc.).

---

## Implementation Approaches

### Recommended Phased Plan

#### Phase 1: Foundation (Mux ID Storage)

**Goal**: Add mux awareness infrastructure to warm pool without changing eviction logic yet.

**Changes**:
1. **WarmProcessInfo.cs**: Add `public string? MuxId { get; set; }`
2. **WarmStreamInfo.cs**: Add `public string? MuxId { get; set; }`
3. **PluginConfiguration.cs**: Add:
   ```csharp
   public bool EnableMuxAwareEviction { get; set; } = false;  // Feature flag
   public string? TVheadendApiUrl { get; set; }  // e.g., "http://tvheadend:9981"
   public string? TVheadendUsername { get; set; }
   public string? TVheadendPassword { get; set; }
   ```
4. **TVheadendMuxMapper.cs** (new file): Implement mux ID lookup via JSON API (Solution 1)
5. **WarmFFmpegProcessPool.AdoptProcess()**: Call `GetMuxIdForChannel()`, populate `MuxId` field
6. **Admin UI** (`config.html`): Add mux-aware settings section

**Testing**: Verify mux IDs are correctly populated in warm pool entries (via `/WarmPool/DetailedStatus`).

**Deliverable**: Warm pool stores mux IDs but doesn't use them yet (no behavior change).

---

#### Phase 2: Mux-Aware Eviction Logic

**Goal**: Implement mux-based eviction when `EnableMuxAwareEviction = true`.

**Changes**:
1. **WarmFFmpegProcessPool.cs**:
   - Extract `EvictLeastValuableProcess()` → split into `EvictLeastValuableChannel()` (old) and `EvictLeastValuableMux()` (new)
   - `EvictLeastValuableMux()`:
     ```csharp
     // Group processes by MuxId
     var muxGroups = _warmProcesses
         .Where(kvp => !string.IsNullOrEmpty(kvp.Value.MuxId))
         .GroupBy(kvp => kvp.Value.MuxId);

     // Score each mux
     var muxScores = muxGroups.Select(group => new {
         MuxId = group.Key,
         StreamCount = group.Count(),
         AvgHistoryPriority = group.Average(kvp => GetHistoryPriority(kvp.Key)),
         AvgIdleMinutes = group.Average(kvp => (DateTime.UtcNow - kvp.Value.LastAccessTime).TotalMinutes),
         HasOrphans = group.Any(kvp => kvp.Value.IsOrphaned)
     });

     // Calculate mux score
     var leastValuableMux = muxScores
         .OrderBy(m => (m.StreamCount * m.AvgHistoryPriority) - (m.AvgIdleMinutes / 60.0) + (m.HasOrphans ? -5.0 : 0.0))
         .FirstOrDefault();

     if (leastValuableMux == null) return false;

     // Evict all streams from this mux
     var toEvict = _warmProcesses
         .Where(kvp => kvp.Value.MuxId == leastValuableMux.MuxId)
         .Select(kvp => kvp.Key)
         .ToList();

     foreach (var key in toEvict)
     {
         EvictProcess(key);
     }

     _logger.LogInformation("Evicted mux {MuxId} ({Count} streams)", leastValuableMux.MuxId, toEvict.Count);
     return true;
     ```

2. **AdoptProcess()**: Check `EnableMuxAwareEviction` flag:
   ```csharp
   if (_config.EnableMuxAwareEviction && _warmProcesses.Count >= _config.PoolSize)
   {
       EvictLeastValuableMux();
   }
   else if (_warmProcesses.Count >= _config.PoolSize)
   {
       EvictLeastValuableProcess();  // Old channel-based logic
   }
   ```

3. **Same changes for WarmStreamPool.cs** (direct stream pool)

**Testing**:
- Scenario 1: Fill pool with 10 streams from 2 muxes → verify entire mux evicted on 11th stream
- Scenario 2: Disable feature flag → verify fallback to channel-based eviction

**Deliverable**: Mux-based eviction works when enabled, backwards compatible when disabled.

---

#### Phase 3: Tuner Pressure Detection (Optional Enhancement)

**Goal**: Dynamically evict only when TVheadend tuners are actually full.

**Changes**:
1. **TVheadendTunerMonitor.cs** (new file): Background service polling `/api/status/inputs` (fallback: `/api/hardware/input/grid`)
2. **PluginServiceRegistrator.cs**: Register `TVheadendTunerMonitor` as `IHostedService`
3. **WarmPoolManager.cs**: Add static property:
   ```csharp
   public static bool IsTunerPressure { get; set; } = false;
   ```
4. **TVheadendTunerMonitor**: Update `IsTunerPressure` flag every 10s
5. **EvictLeastValuableMux()**: Check flag before evicting:
   ```csharp
   if (!WarmPoolManager.IsTunerPressure)
   {
       _logger.LogDebug("Tuner pressure not detected, skipping mux eviction");
       return false;  // Don't evict if tuners are available
   }
   ```

**Testing**:
- Manually tune all 4 tuners via TVheadend UI → verify flag=true, eviction triggered
- Leave tuner free → verify flag=false, pool grows beyond 4 cached muxes

**Deliverable**: Pool only evicts muxes when TVheadend is actually full (intelligent resource management).

---

#### Phase 4: HTSP Integration (Future / Experimental)

**Goal**: Native HTSP protocol support for mux-perfect accuracy (long-term).

**Changes**: See [Solution 3](#solution-3-htsp-native-integration-advanced) above.

**Effort**: 40-80 hours, high risk, deferred for now.

---

### Configuration Example

**After Phase 1-3 implementation**, plugin settings would look like:

```json
{
  "Enabled": true,
  "PoolSize": 30,
  "EnableMuxAwareEviction": true,
  "TVheadendApiUrl": "http://192.168.1.100:9981",
  "TVheadendUsername": "admin",
  "TVheadendPassword": "secret",
  "EnableTunerPressureMonitoring": true,
  "TunerPressureCheckIntervalSeconds": 10
}
```

**UI mockup** (`config.html` section):

```html
<h3>TVheadend Mux-Aware Settings</h3>
<div class="inputContainer">
    <input type="checkbox" id="enableMuxAware" />
    <label for="enableMuxAware">Enable mux-aware eviction (recommended for OTA/DVB sources)</label>
</div>

<div class="inputContainer">
    <label for="tvhApiUrl">TVheadend API URL:</label>
    <input type="text" id="tvhApiUrl" placeholder="http://tvheadend:9981" />
    <div class="fieldDescription">Required for mux detection. Leave blank to disable.</div>
</div>

<div class="inputContainer">
    <label for="tvhUsername">TVheadend Username:</label>
    <input type="text" id="tvhUsername" />
</div>

<div class="inputContainer">
    <label for="tvhPassword">TVheadend Password:</label>
    <input type="password" id="tvhPassword" />
</div>

<div class="inputContainer">
    <input type="checkbox" id="enableTunerMonitoring" />
    <label for="enableTunerMonitoring">Monitor TVheadend tuner status (enable dynamic eviction)</label>
</div>
```

---

## API and Protocol Details

### TVheadend JSON API Reference

Source: [TVheadend JSON API Docs](https://docs.tvheadend.org/documentation/development/json-api/api-description/mpegts)

#### Get All Channels with Mux Info

**Endpoint**: `GET /api/channel/grid`

**Authentication**: HTTP Digest (username/password)

**Response** (partial):
```json
{
  "entries": [
    {
      "uuid": "abc123def456",
      "name": "BBC One HD",
      "number": 101,
      "services": ["service-uuid-1"],
      "tags": ["tag-uuid-hd"]
    }
  ],
  "total": 150
}
```

**Limitation**: No `mux` field in channel response → must query service separately.

---

#### Get Service Details (Includes Mux)

**Endpoint**: `GET /api/mpegts/service/load?uuid={serviceUuid}`

**Response**:
```json
{
  "uuid": "service-uuid-1",
  "svcname": "BBC One HD",
  "multiplex": "mux-uuid-509mhz",
  "channel": "channel-uuid-abc123",
  "enabled": true
}
```

**Key field**: `multiplex` = mux UUID

---

#### Get All Muxes

**Endpoint**: `GET /api/mpegts/mux/grid`

**Response**:
```json
{
  "entries": [
    {
      "uuid": "mux-uuid-509mhz",
      "name": "509MHz",
      "frequency": 509000000,
      "network": "network-uuid-dvb-t",
      "num_svc": 4
    }
  ],
  "total": 20
}
```

**Use case**: Build mux name/frequency lookup for UI display.

---

#### Get Tuner/Adapter Status

**Endpoint**: `GET /api/status/inputs` (fallback: `/api/hardware/input/grid`)

**Response**:
```json
{
  "entries": [
    {
      "uuid": "adapter-uuid-0",
      "name": "Hauppauge WinTV-dualHD #0",
      "currentMux": "mux-uuid-509mhz",
      "subscribers": 8,
      "enabled": true,
      "type": "DVB-T"
    },
    {
      "uuid": "adapter-uuid-1",
      "name": "Hauppauge WinTV-dualHD #1",
      "currentMux": null,
      "subscribers": 0,
      "enabled": true,
      "type": "DVB-T"
    }
  ],
  "total": 4
}
```

**Tuner pressure detection**:
```csharp
var busyTuners = entries.Count(e => e.enabled && e.currentMux != null);
var totalEnabled = entries.Count(e => e.enabled);
var freeTuners = totalEnabled - busyTuners;
```

---

### HTSP Protocol Summary

Source: [HTSP Documentation](https://docs.tvheadend.org/documentation/development/htsp)

**Protocol**: Binary, TCP port 9982 (default)

**Message format**: HTSMSG (custom binary serialization, like BSON)

**Key RPC methods**:
- `hello` - Handshake, protocol version negotiation
- `authenticate` - Username/password or token auth
- `getChannels` - List all channels with mux references
- `subscribe` - Start channel subscription (returns mux UUID in response)
- `unsubscribe` - Stop subscription
- `subscriptionStatus` - Real-time subscription state updates

**Subscription response includes**:
```
{
  "subscriptionId": 1,
  "sourceinfo": {
    "mux": "mux-uuid-here",      ← Mux UUID
    "adapter": "adapter-uuid",    ← Tuner UUID
    "service": "service-uuid",
    "provider": "BBC",
    "network": "DVB-T"
  },
  "streams": [...]
}
```

**Advantage over HTTP**: Mux/adapter info is **native** in the protocol, no separate API query needed.

---

## Scenarios and Examples

### Scenario 1: Multi-User Pool Fairness (Mux-Aware)

**Setup**:
- 4 tuners (DVB-T)
- Warm pool size: 12 streams
- Mux-aware eviction: **enabled**

**Timeline**:

1. **T=0**: User A flips channels on Mux A (509 MHz):
   - Tunes 5.1 → cached
   - Tunes 5.2 → cached
   - Tunes 5.3 → cached
   - Tunes 5.4 → cached
   - **Pool**: 4 streams, 1 mux, 1 tuner used

2. **T=5min**: User A continues on Mux B (538 MHz):
   - Tunes 9.1 → cached
   - Tunes 9.2 → cached
   - Tunes 9.3 → cached
   - **Pool**: 7 streams, 2 muxes, 2 tuners used

3. **T=10min**: User A explores Mux C (602 MHz):
   - Tunes 12.1 → cached
   - Tunes 12.2 → cached
   - Tunes 12.3 → cached
   - **Pool**: 10 streams, 3 muxes, 3 tuners used

4. **T=15min**: User B (different client) tunes channel 19.1 (Mux D, 671 MHz):
   - Pool has space (10 < 12) → cached without eviction
   - **Pool**: 11 streams, 4 muxes, 4 tuners used

5. **T=16min**: User B tunes channel 19.2 (same Mux D):
   - Pool has space (11 < 12) → cached
   - **Pool**: 12 streams, 4 muxes, 4 tuners used (FULL)

6. **T=17min**: User C tunes channel 48.1 (Mux E, 755 MHz, NEW):
   - Pool is full (12 streams)
   - **Mux-aware eviction triggered**:
     - Group streams by mux:
       - Mux A: 4 streams, avg idle = 17 min, history priority = 2.0, no orphans
       - Mux B: 3 streams, avg idle = 12 min, history priority = 1.5, no orphans
       - Mux C: 3 streams, avg idle = 7 min, history priority = 1.0, no orphans
       - Mux D: 2 streams, avg idle = 1 min, history priority = 0.0, no orphans
     - Mux scores:
       - Mux A: `(4 * 2.0) - (17/60) = 8.0 - 0.28 = 7.72`
       - Mux B: `(3 * 1.5) - (12/60) = 4.5 - 0.20 = 4.30`
       - Mux C: `(3 * 1.0) - (7/60) = 3.0 - 0.12 = 2.88`
       - Mux D: `(2 * 0.0) - (1/60) = 0.0 - 0.02 = -0.02` ← **Lowest**
     - **Evict all streams from Mux D** (19.1, 19.2)
   - Pool now: 10 streams, 3 muxes, 3 tuners used
   - Cache channel 48.1
   - **Pool**: 11 streams, 4 muxes, 4 tuners used

**Result**: User B's recent channel (Mux D) was evicted, but User A's popular channels stayed cached. User C got a free tuner (since we evicted an entire mux, freeing Tuner 3).

---

### Scenario 2: Tuner Pressure Detection

**Setup**:
- 4 tuners (DVB-T)
- Warm pool size: 30 streams
- Mux-aware eviction: **enabled**
- Tuner pressure monitoring: **enabled**

**Timeline**:

1. **T=0-20min**: Users gradually fill pool with 24 streams from 4 muxes:
   - Mux A: 10 streams (channels 5.1-5.10)
   - Mux B: 8 streams (channels 9.1-9.8)
   - Mux C: 4 streams (channels 12.1-12.4)
   - Mux D: 2 streams (channels 19.1-19.2)
   - **Tuner status**: 4/4 tuners busy (full)
   - **Pool**: 24 streams < 30 limit → no eviction yet

2. **T=21min**: User requests channel 5.11 (Mux A, already cached):
   - Mux A tuner is already locked (Tuner 0)
   - No new tuner needed → cache without eviction
   - **Pool**: 25 streams, 4 muxes, 4 tuners

3. **T=22min**: User requests channel 48.1 (Mux E, NEW):
   - **Tuner pressure check**: Poll `/api/status/inputs` → 0 free tuners
   - `IsTunerPressure = true`
   - **Mux-aware eviction triggered** (even though pool < 30):
     - Score all muxes, evict lowest (Mux D with 2 streams)
     - Free Tuner 3
   - TVheadend locks Tuner 3 to Mux E
   - Cache channel 48.1
   - **Pool**: 23 streams, 4 muxes, 4 tuners

4. **T=25min**: User stops watching all Mux D channels and exits:
   - `SessionEnded` event fires → mark Mux D streams as orphaned
   - **Pool cleanup** (background task):
     - Idle timeout (10 min) hasn't passed yet
     - But `IsOrphaned = true` → orphan penalty = -10.0
     - Next eviction will heavily prioritize orphaned Mux D

5. **T=30min**: No activity, background cleanup runs:
   - All Mux D streams idle > 10 min AND orphaned
   - **Idle timeout eviction** removes them
   - **Pool**: 21 streams, 3 muxes, 3 tuners used
   - **Tuner pressure check**: 1 free tuner → `IsTunerPressure = false`

**Result**: Pool intelligently managed tuner resources, evicting only when TVheadend was actually full, and cleaning up orphaned sessions automatically.

---

### Scenario 3: IPTV vs OTA Hybrid

**Setup**:
- Jellyfin has 2 tuner hosts:
  1. **TVheadend (ATSC OTA)**: 4 tuners, mux-aware enabled
  2. **M3U IPTV**: 10 "tuners" (HTTP connections), mux-aware disabled

**Timeline**:

1. **User A**: Watches IPTV channels → warm pool caches 5 IPTV streams
   - Each IPTV stream = 1 HTTP connection to provider
   - No mux grouping (IPTV URLs have no mux concept)
   - **Pool (IPTV)**: 5 streams, no mux data

2. **User B**: Watches OTA channels on TVheadend → pool caches 8 OTA streams from 2 muxes
   - Mux F (509 MHz): 5 streams (channels 5.1-5.5)
   - Mux G (538 MHz): 3 streams (channels 9.1-9.3)
   - **Pool (OTA)**: 8 streams, 2 muxes, `MuxId` populated

3. **Eviction logic**:
   - **IPTV streams**: Use channel-based eviction (no `MuxId`)
   - **OTA streams**: Use mux-based eviction (grouped by `MuxId`)
   - **Hybrid**: Plugin detects `MuxId != null` and applies appropriate logic per entry

**Result**: Plugin handles both IPTV and OTA sources gracefully, applying mux-aware eviction only where applicable.

---

---

## Detecting TVheadend vs Other IPTV Providers

### Challenge

To apply mux-aware logic selectively, the plugin needs to detect whether a tuner host is TVheadend vs a generic IPTV provider (like Xtream Codes, Stalker Portal, simple HTTP M3U, etc.).

### Detection Methods

#### Method 1: Probe `/api/serverinfo` Endpoint (Recommended)

**Approach**: Query TVheadend's JSON API to verify server identity.

Source: [TVheadend JSON API](https://docs.tvheadend.org/documentation/development/json-api/api-description), [API Config Endpoint](https://docs.tvheadend.org/documentation/development/json-api/api-description/config)

**Endpoint**: `GET http://{tunerHostUrl}/api/serverinfo`

**Response** (TVheadend):
```json
{
  "sw_version": "4.3-2195~g65dfd15",
  "api_version": 18,
  "name": "Tvheadend",
  "capabilities": ["caclient", "tvadapters", ...]
}
```

**Response** (non-TVheadend IPTV): `404 Not Found` or timeout

**Implementation**:

```csharp
public class TVheadendDetector
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly Dictionary<string, bool> _detectionCache = new();  // {tunerHostUrl: isTVheadend}

    public async Task<bool> IsTVheadendAsync(string tunerHostUrl)
    {
        // Check cache first
        if (_detectionCache.TryGetValue(tunerHostUrl, out var cached))
            return cached;

        try
        {
            // Build API URL from M3U playlist URL
            var baseUrl = GetBaseUrl(tunerHostUrl);  // Extract http://host:port from M3U URL
            var apiUrl = $"{baseUrl}/api/serverinfo";

            using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            request.Headers.Add("Accept", "application/json");

            using var client = _httpClientFactory.CreateClient(NamedClient.Default);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);

            if (!response.IsSuccessStatusCode)
            {
                _detectionCache[tunerHostUrl] = false;
                return false;
            }

            var json = await response.Content.ReadAsStringAsync();
            var serverInfo = JsonSerializer.Deserialize<JsonElement>(json);

            // Check for TVheadend signature
            var isTvh = serverInfo.TryGetProperty("name", out var name) &&
                        name.GetString()?.Equals("Tvheadend", StringComparison.OrdinalIgnoreCase) == true;

            _detectionCache[tunerHostUrl] = isTvh;

            if (isTvh)
            {
                var version = serverInfo.TryGetProperty("sw_version", out var ver) ? ver.GetString() : "unknown";
                _logger.LogInformation("Detected TVheadend server version {Version} at {Url}", version, baseUrl);
            }

            return isTvh;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to detect TVheadend at {Url}, assuming generic IPTV", tunerHostUrl);
            _detectionCache[tunerHostUrl] = false;
            return false;
        }
    }

    private string GetBaseUrl(string m3uUrl)
    {
        // Extract base URL from M3U playlist URL
        // Example: http://tvheadend:9981/playlist -> http://tvheadend:9981
        var uri = new Uri(m3uUrl);
        return $"{uri.Scheme}://{uri.Authority}";
    }
}
```

**Pros**:
- ✅ Definitive detection (API only exists on TVheadend)
- ✅ Returns version info (can adapt to API changes)
- ✅ Single HTTP request (fast, can cache result)

**Cons**:
- ❌ Requires network access (fails if API blocked/firewalled)
- ❌ May fail if TVheadend behind reverse proxy with path rewriting

---

#### Method 2: Analyze M3U Playlist Format

**Approach**: TVheadend's M3U playlists have distinctive patterns.

Source: [TVheadend M3U URL Format](https://github.com/tvheadend/tvheadend/blob/master/docs/markdown/url.md), [Playlist Documentation](https://tvheadend.org/d/8702-overviewsummary-of-urls-offering-tvheadend-services)

**TVheadend M3U characteristics**:

1. **Stream URL patterns**:
   - `/stream/channel/{uuid}`
   - `/stream/channelid/{id}`
   - `/stream/service/{uuid}`
   - Regex: `^https?://.+/stream/(channel|channelid|service|mux)/`

2. **EXTINF attributes**:
   - `tvg-id` format: Often uses service name or internal ID
   - `group-title` may include "TVheadend" branding

**Example TVheadend M3U**:
```
#EXTM3U
#EXTINF:-1 tvg-id="bbc-one" tvg-logo="http://..." group-title="TVheadend", BBC One HD
http://tvheadend:9981/stream/channel/abc123def456
#EXTINF:-1 tvg-id="itv-hd" group-title="TVheadend", ITV HD
http://tvheadend:9981/stream/channelid/1234
```

**Example generic IPTV M3U**:
```
#EXTM3U
#EXTINF:-1 tvg-id="BBCOne.uk" tvg-logo="...", BBC One
http://iptv.example.com/live/bbc1/playlist.m3u8
#EXTINF:-1 tvg-id="ITV.uk", ITV
http://iptv.example.com/live/itv/playlist.m3u8
```

**Implementation**:

```csharp
public class M3UPatternDetector
{
    private static readonly Regex _tvhStreamUrlPattern =
        new Regex(@"^https?://.+/stream/(channel|channelid|channelname|channelnumber|service|mux)/",
                  RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public bool IsTVheadendM3U(List<ChannelInfo> channels)
    {
        if (channels.Count == 0) return false;

        // Check first 10 channels for TVheadend URL pattern
        var sampleChannels = channels.Take(10);
        var tvhMatches = sampleChannels.Count(ch => _tvhStreamUrlPattern.IsMatch(ch.Path));

        // If >80% of sample channels match TVheadend pattern, consider it TVheadend
        return (tvhMatches / (double)Math.Min(10, channels.Count)) > 0.8;
    }
}
```

**Pros**:
- ✅ No extra HTTP request (uses existing M3U data)
- ✅ Works even if API is disabled
- ✅ Can detect TVheadend behind proxies

**Cons**:
- ❌ Heuristic (false positives possible if other provider uses `/stream/` paths)
- ❌ Unreliable if M3U URLs are rewritten by proxy/CDN

---

#### Method 3: Check HTTP Response Headers (Experimental)

**Approach**: TVheadend may include server identification in HTTP headers.

Source: [TVheadend FAQ](https://github.com/tvheadend/tvheadend/blob/master/docs/markdown/faqs.md), [User-Agent Detection](https://forum.kodi.tv/showthread.php?tid=348285)

**Potential headers**:
- `Server: HTS/tvheadend` (not confirmed in all versions)
- Custom headers like `X-Tvheadend-Version` (version-dependent)

**Note**: TVheadend's HTTP server implementation (in `src/http.c`) may not consistently send `Server:` header, making this method unreliable.

**Implementation**:

```csharp
public async Task<bool> CheckServerHeaderAsync(string streamUrl)
{
    try
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, streamUrl);
        using var client = _httpClientFactory.CreateClient(NamedClient.Default);
        using var response = await client.SendAsync(request, CancellationToken.None);

        // Check Server header
        if (response.Headers.Server != null)
        {
            var serverHeader = string.Join(" ", response.Headers.Server.Select(s => s.Product?.Name));
            if (serverHeader.Contains("tvheadend", StringComparison.OrdinalIgnoreCase) ||
                serverHeader.Contains("HTS", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Check custom headers
        if (response.Headers.TryGetValues("X-Tvheadend-Version", out var _))
        {
            return true;
        }

        return false;
    }
    catch
    {
        return false;  // Assume not TVheadend if request fails
    }
}
```

**Pros**:
- ✅ Lightweight (HEAD request only)
- ✅ Direct server identification

**Cons**:
- ❌ Unreliable (header presence depends on TVheadend version/config)
- ❌ Fails behind reverse proxies that strip headers
- ❌ Not documented/guaranteed behavior

---

#### Method 4: Configuration Hint (User-Specified)

**Approach**: Let users explicitly mark tuner hosts as "TVheadend" in Jellyfin UI.

**TunerHostInfo extension**:

```csharp
public class TunerHostInfo
{
    // ... existing fields ...

    /// <summary>
    /// Hint for tuner provider type. Used for mux-aware optimizations.
    /// </summary>
    public string? ProviderHint { get; set; }  // "tvheadend", "xtream", "generic", etc.
}
```

**Plugin detection logic**:

```csharp
public async Task<bool> IsTVheadendAsync(TunerHostInfo tunerHost)
{
    // First check explicit hint
    if (!string.IsNullOrEmpty(tunerHost.ProviderHint))
    {
        return tunerHost.ProviderHint.Equals("tvheadend", StringComparison.OrdinalIgnoreCase);
    }

    // Fallback to auto-detection
    return await _tvhDetector.IsTVheadendAsync(tunerHost.Url);
}
```

**UI addition** (Jellyfin Live TV settings):
```
[ M3U Tuner Settings ]
URL: http://tvheadend:9981/playlist
Provider Type: [ Auto-detect ▼ ]  <!-- Dropdown: Auto-detect, TVheadend, Xtream Codes, Generic -->
```

**Pros**:
- ✅ 100% reliable (user knows their backend)
- ✅ Works in all network configurations
- ✅ Allows per-tuner-host customization

**Cons**:
- ❌ Requires user configuration (not automatic)
- ❌ User may not know what they're using

---

### Recommended Detection Strategy (Simplified)

**Priority cascade** (user preference → auto-detect → generic):

```csharp
public class TunerProviderDetectionService
{
    public async Task<TunerProviderType> DetectProviderAsync(TunerHostInfo tunerHost)
    {
        // Priority 1: User-specified checkbox/setting (most reliable)
        if (tunerHost.IsTVheadend.HasValue)
        {
            if (tunerHost.IsTVheadend.Value)
            {
                _logger.LogInformation("User marked tuner host as TVheadend");
                return TunerProviderType.TVheadend;
            }
            else
            {
                _logger.LogInformation("User marked tuner host as generic IPTV");
                return TunerProviderType.GenericIPTV;
            }
        }

        // Priority 2: API probe (auto-detect via /api/serverinfo)
        if (await _apiProbe.IsTVheadendAsync(tunerHost.Url))
        {
            _logger.LogInformation("Detected TVheadend via /api/serverinfo");
            return TunerProviderType.TVheadend;
        }

        // Default: Generic IPTV (safe fallback)
        _logger.LogInformation("No TVheadend detection, treating as generic IPTV");
        return TunerProviderType.GenericIPTV;
    }
}

public enum TunerProviderType
{
    GenericIPTV,
    TVheadend,
    XtreamCodes,
    Stalker,
    // ... other providers
}
```

**When to apply mux-aware logic**:

```csharp
if (providerType == TunerProviderType.TVheadend && _config.EnableMuxAwareEviction)
{
    // Fetch mux data from TVheadend API
    var muxId = await _muxMapper.GetMuxIdForChannel(mediaSourceId);
    processInfo.MuxId = muxId;

    // Use mux-aware eviction
    EvictLeastValuableMux();
}
else
{
    // Use standard channel-based eviction
    EvictLeastValuableProcess();
}
```

---

### Detection Performance Optimization

**Cache detection results**:
```csharp
private readonly ConcurrentDictionary<string, TunerProviderType> _providerCache = new();

public async Task<TunerProviderType> GetProviderTypeAsync(string tunerHostId, TunerHostInfo tunerHost)
{
    if (_providerCache.TryGetValue(tunerHostId, out var cached))
        return cached;

    var detected = await DetectProviderAsync(tunerHost, await GetChannelsAsync(tunerHost));
    _providerCache[tunerHostId] = detected;
    return detected;
}
```

**Background re-detection** (every 24 hours):
```csharp
private async Task RefreshProviderDetectionAsync()
{
    foreach (var tunerHost in await _liveTvManager.GetTunerHostsAsync())
    {
        _providerCache.TryRemove(tunerHost.Id, out _);  // Invalidate cache
        await GetProviderTypeAsync(tunerHost.Id, tunerHost);  // Re-detect
    }
}
```

---

## Recommendations

### Immediate Actions (Phase 1)

**0. TVheadend Detection** (prerequisite):

- Add `IsTVheadend` (nullable bool) to `PluginConfiguration.cs`
  - `null` = auto-detect (default)
  - `true` = force TVheadend mode
  - `false` = force generic IPTV mode
- Update `config.html`:
  - "Auto-detect TVheadend" checkbox (checked by default)
  - Manual override checkbox (hidden unless auto-detect unchecked)
  - Clear descriptions explaining when to use manual override
- Implement `TVheadendDetector.cs`:
  - Probe `GET /api/serverinfo`
  - Check for `"name": "Tvheadend"` in JSON response
  - Cache result per plugin instance (avoid repeated API calls)
  - Graceful fallback to generic IPTV on failure
- **Priority**: User checkbox → API probe → generic fallback
- **Risk**: Low (read-only API call, no behavior changes)
- **Effort**: 3-4 hours
- **Value**: Required for all TVheadend-specific features, gives users control

**1. Mux ID Extraction** (conditional on TVheadend detection):

- Add `TVheadendMuxMapper.cs` to plugin
- Query TVheadend JSON API:
  - `/api/channel/grid` → get channel UUIDs and service references
  - `/api/mpegts/service/load?uuid={serviceUuid}` → get mux UUID per service
  - Build `{channelUuid: muxUuid}` lookup table
- Store mux UUIDs in `WarmProcessInfo.MuxId`
- **Only run if `IsTVheadendAsync() == true`**
- No eviction changes yet (data collection phase)
- **Risk**: Low (read-only queries, conditional on detection)
- **Effort**: 4-8 hours
- **Value**: Foundation for mux-aware eviction

**2. Configuration UI**:

- TVheadend detection section (from step 0)
- TVheadend API credentials:
  - `TVheadendApiUrl` (optional, defaults to M3U playlist base URL)
  - `TVheadendUsername`
  - `TVheadendPassword`
- "Enable Mux-Aware Eviction" checkbox (default: off)
  - Only visible if TVheadend detected or manually specified
  - Grayed out for generic IPTV sources
- **Effort**: 2-3 hours

### Short-Term Enhancements (Phase 2)

3. **Implement mux-aware eviction**:
   - Add `EvictLeastValuableMux()` method
   - Group streams by `MuxId`, score entire muxes
   - Evict all streams from lowest-scoring mux
   - **Risk**: Medium (new eviction logic, needs testing)
   - **Effort**: 8-16 hours
   - **Value**: Solves the 30 streams / 4 tuners problem

4. **Testing scenarios**:
   - Fill pool with 20 streams from 4 OTA muxes
   - Request 5th mux → verify entire mux evicted, not just 1 stream
   - Mix IPTV + OTA sources → verify hybrid handling
   - **Effort**: 4 hours

### Medium-Term Enhancements (Phase 3)

5. **Add tuner pressure monitoring**:
   - Poll `/api/status/inputs` every 10s (fallback to `/api/hardware/input/grid` if needed)
   - Set `IsTunerPressure` flag when all tuners busy
   - Only evict muxes when pressure detected
   - **Risk**: Low (read-only polling, optional feature)
   - **Effort**: 4-8 hours
   - **Value**: Prevents unnecessary evictions, maximizes cache efficiency

6. **Metrics and observability**:
   - Add `MuxEvictionsCount` to `PoolMetrics`
   - Log mux eviction events at Info level
   - Display mux grouping in `/WarmPool/DetailedStatus`
   - **Effort**: 2 hours

### Long-Term Exploration (Phase 4)

7. **Evaluate HTSP integration**:
   - Research C# HTSP client libraries
   - Prototype `IHtspTunerHost` interface
   - Compare complexity vs JSON API approach
   - **Risk**: High (protocol complexity, server changes)
   - **Effort**: 40-80 hours
   - **Decision**: Defer until Phases 1-3 are proven in production

### Non-TVheadend Sources

8. **Heuristic mux grouping for other IPTV providers**:
   - Implement channel number parsing (5.1 → mux "5")
   - Add URL pattern matching for frequency hints
   - **Value**: Limited (mostly benefits ATSC OTA, not generic IPTV)
   - **Decision**: Low priority, defer

---

## Sources

- [TVheadend Concepts Documentation](https://docs.tvheadend.org/documentation/setup/concepts)
- [Configure TVheadend - Tvheadend 4.0](https://profyaffle.github.io/versionB/configure_tvheadend/)
- [Muxes - Tvheadend 4.0](https://profyaffle.github.io/versionB/webui/config_muxes/)
- [Multiple Streams from Same MUX - Kodi Forum](https://forum.kodi.tv/showthread.php?tid=317164)
- [One Backend Multiple streaming Clients - Tvheadend.org](https://tvheadend.org/boards/5/topics/15580)
- [Bug #4219: Transcoding multiple channels from one MUX - Tvheadend](https://tvheadend.org/issues/4219)
- [TVheadend HTSP Protocol - General Documentation](https://docs.tvheadend.org/documentation/development/htsp/general)
- [HTSP Client to Server RPC Methods](https://docs.tvheadend.org/documentation/development/htsp/client-to-server-rpc-methods)
- [Tvheadend HTSP Client - Official Kodi Wiki](https://kodi.wiki/view/Add-on:Tvheadend_HTSP_Client)
- [TVheadend URL Schemes (url.md)](https://github.com/tvheadend/tvheadend/blob/master/docs/markdown/url.md)
- [Overview/Summary of URLs offering tvheadend services - Tvheadend.org](https://tvheadend.org/d/8702-overviewsummary-of-urls-offering-tvheadend-services)
- [TVheadend JSON API - MPEGts](https://docs.tvheadend.org/documentation/development/json-api/api-description/mpegts)
- [Channel API Documentation](https://docs.tvheadend.org/documentation/development/json-api/api-description/channel)

---

**End of Document**
#### Method 4: Configuration Checkbox (User-Specified) — PREFERRED

**Approach**: Let users explicitly mark their backend as "TVheadend" via checkbox in plugin UI.

**Plugin configuration extension** (`PluginConfiguration.cs`):

```csharp
public class PluginConfiguration : BasePluginConfiguration
{
    // ... existing fields ...

    /// <summary>
    /// User-specified flag to indicate this is a TVheadend backend.
    /// When null (default), auto-detection will be attempted via API probe.
    /// </summary>
    public bool? IsTVheadend { get; set; } = null;  // null = auto-detect, true = force TVh, false = force generic

    /// <summary>
    /// TVheadend API base URL (for mux detection). If not specified, derived from M3U playlist URL.
    /// </summary>
    public string? TVheadendApiUrl { get; set; }
}
```

**Plugin UI** (`config.html`):

```html
<h3>TVheadend Detection</h3>
<div class="inputContainer">
    <label>
        <input type="checkbox" id="isTVheadendAuto" checked />
        Auto-detect TVheadend (recommended)
    </label>
    <div class="fieldDescription">
        Automatically probe the M3U source to detect if it's TVheadend.
        Uncheck to manually specify below.
    </div>
</div>

<div class="inputContainer" id="manualTVhContainer" style="display:none;">
    <label>
        <input type="checkbox" id="isTVheadend" />
        This is a TVheadend server (enable mux-aware features)
    </label>
    <div class="fieldDescription">
        Check this box if your M3U source is TVheadend and auto-detection fails
        (e.g., behind firewall, reverse proxy, or API disabled).
    </div>
</div>

<script>
    // Toggle manual checkbox visibility
    document.getElementById('isTVheadendAuto').addEventListener('change', function() {
        document.getElementById('manualTVhContainer').style.display =
            this.checked ? 'none' : 'block';
    });
</script>
```

**Detection logic flow**:

```csharp
public async Task<bool> IsTVheadendAsync()
{
    // Priority 1: Check user's explicit setting
    if (_config.IsTVheadend.HasValue)
    {
        _logger.LogInformation("Using user-specified TVheadend flag: {IsTVh}", _config.IsTVheadend.Value);
        return _config.IsTVheadend.Value;
    }

    // Priority 2: Auto-detect via API probe
    _logger.LogInformation("Auto-detecting TVheadend via API probe");
    var detected = await _tvhDetector.IsTVheadendAsync(GetTunerHostUrl());

    // Cache result for future use (don't persist to config)
    _runtimeDetectionCache = detected;
    return detected;
}
```

**Pros**:

- ✅ Simple user experience (single checkbox, hidden by default)
- ✅ 100% reliable when user knows their backend
- ✅ Auto-detect by default (no manual config needed for most users)
- ✅ Allows override if auto-detection fails (firewall, reverse proxy, etc.)
- ✅ Three-state logic: auto / force-tvh / force-generic
- ✅ **PREFERRED**: Balances automation with user control

**Cons**:

- ❌ Requires UI update to plugin config page
- ❌ User may not understand what "TVheadend" means (mitigated by clear description)

---

### Recommended Detection Strategy — FINAL

**Priority cascade** (user checkbox → API probe → generic fallback):

1. **User checkbox checked** → Enable TVheadend mux-aware features
2. **User checkbox unchecked** → Disable mux-aware features (treat as generic IPTV)
3. **Auto-detect enabled (default)** → Probe `/api/serverinfo`, cache result
4. **API probe fails** → Default to generic IPTV (safe fallback, no mux awareness)

**Implementation**:

```csharp
public class TunerProviderDetectionService
{
    private bool? _detectionCache = null;  // Runtime cache

    public async Task<bool> IsTVheadendAsync()
    {
        // Priority 1: User-specified checkbox/setting (most reliable)
        if (_config.IsTVheadend.HasValue)
        {
            _logger.LogInformation("User explicitly set TVheadend: {Value}", _config.IsTVheadend.Value);
            return _config.IsTVheadend.Value;
        }

        // Check cache (avoid repeated API calls)
        if (_detectionCache.HasValue)
        {
            return _detectionCache.Value;
        }

        // Priority 2: API probe (auto-detect via /api/serverinfo)
        try
        {
            var detected = await _apiProbe.IsTVheadendAsync(GetApiBaseUrl());
            _detectionCache = detected;

            if (detected)
            {
                _logger.LogInformation("Auto-detected TVheadend via /api/serverinfo");
            }
            else
            {
                _logger.LogInformation("API probe returned non-TVheadend response");
            }

            return detected;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TVheadend API probe failed, treating as generic IPTV");
            _detectionCache = false;
            return false;
        }
    }

    private string GetApiBaseUrl()
    {
        // Use explicit API URL if configured, otherwise derive from M3U URL
        if (!string.IsNullOrEmpty(_config.TVheadendApiUrl))
        {
            return _config.TVheadendApiUrl;
        }

        // Extract from Jellyfin tuner host URL (requires access to LiveTvManager)
        var tunerHost = GetTunerHost();  // Implementation depends on plugin architecture
        if (tunerHost != null && !string.IsNullOrEmpty(tunerHost.Url))
        {
            var uri = new Uri(tunerHost.Url);
            return $"{uri.Scheme}://{uri.Authority}";
        }

        return null;
    }
}
```

**Configuration UI flow**:

```
┌─────────────────────────────────────────────────────────┐
│ TVheadend Detection                                     │
├─────────────────────────────────────────────────────────┤
│ ☑ Auto-detect TVheadend (recommended)                   │
│   Automatically probe the M3U source...                 │
│                                                         │
│ [Hidden by default]                                     │
│ ☐ This is a TVheadend server                            │
│   Enable if auto-detection fails...                     │
└─────────────────────────────────────────────────────────┘

User unchecks "Auto-detect" →

┌─────────────────────────────────────────────────────────┐
│ TVheadend Detection                                     │
├─────────────────────────────────────────────────────────┤
│ ☐ Auto-detect TVheadend (recommended)                   │
│   Automatically probe the M3U source...                 │
│                                                         │
│ ☑ This is a TVheadend server                            │  ← Now visible
│   Enable mux-aware eviction for better tuner usage...   │
└─────────────────────────────────────────────────────────┘
```

**Behavior summary**:

| Auto-detect | Manual checkbox | Result |
|-------------|----------------|--------|
| ✅ Checked (default) | N/A (hidden) | API probe → cache result |
| ❌ Unchecked | ✅ Checked | Force TVheadend mode |
| ❌ Unchecked | ❌ Unchecked | Force generic IPTV mode |

---
