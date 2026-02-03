# MediaBrowser.Controller.LiveTv IWarm* Interface Refactoring Analysis

## Executive Summary

**Can the warm interfaces be moved to the plugin?**

**Short answer**: **No, not entirely. But significant refactoring is possible to minimize server-side code.**

The interfaces `IHlsPlaylistProvider`, `ILiveStreamProvider`, and `ITunerResourceProvider` must remain in the server codebase because:

1. **DI Registration Requirement**: Controllers and services must be able to discover and inject these interfaces at startup
2. **Dependency Inversion Principle**: The server cannot depend on the plugin; the plugin depends on the server by implementing these interfaces
3. **Multi-provider Support**: The DI system automatically collects `IEnumerable<IHlsPlaylistProvider>` from all plugins — this is a core framework feature

However, the **supporting types** and **non-critical interfaces** can be refactored, and the server-side code using them can be significantly simplified.

---

## 1. Current State of MediaBrowser.Controller.LiveTv Namespace

### Files in LiveTv Directory

```
IHlsPlaylistProvider.cs       ← Core interface (plugin hook)
ILiveStreamProvider.cs        ← Core interface (plugin hook)
ITunerResourceProvider.cs     ← Core interface (plugin hook)
ICustomPlaylistPublisher.cs   ← Optional interface (legacy; unused by server path)
EncodingProfile.cs            ← Supporting DTO (shared between server & plugin)
ChannelInfo.cs                ← LiveTV domain model
ProgramInfo.cs                ← LiveTV domain model
TimerInfo.cs                  ← LiveTV domain model
SeriesTimerInfo.cs            ← LiveTV domain model
TimerEventInfo.cs             ← LiveTV domain model
LiveTvChannel.cs              ← LiveTV domain model
LiveTvProgram.cs              ← LiveTV domain model
ActiveRecordingInfo.cs        ← LiveTV domain model
IGuideManager.cs              ← Service interface
IListingsManager.cs           ← Service interface
IListingsProvider.cs          ← Service interface
ILiveTvManager.cs             ← Service interface
IRecordingsManager.cs         ← Service interface
ITunerHost.cs                 ← Service interface
ITunerHostManager.cs          ← Service interface
ILiveTvService.cs             ← Service interface
LiveTvConflictException.cs    ← Domain exception
```

### Categorization

**Must Stay in Server (Core Framework)**:
- `IHlsPlaylistProvider` — Plugin discovery hook
- `ILiveStreamProvider` — Plugin discovery hook
- `ITunerResourceProvider` — Plugin discovery hook
- `EncodingProfile` — Shared DTO between server and plugin

**Should Stay (LiveTV Core Domain)**:
- All `ChannelInfo`, `ProgramInfo`, `TimerInfo`, etc. — LiveTV domain models
- All `IGuideManager`, `IListingsManager`, `ILiveTvManager`, etc. — LiveTV service contracts

**Could Be Removed/Simplified**:
- `ICustomPlaylistPublisher` — Optional; server does not call it directly

---

## 2. Architecture: Why Interfaces Can't Move to Plugin

### Dependency Direction (Core Architecture)

```
Plugin Assembly (WarmPool)
        │
        │ implements
        ▼
IHlsPlaylistProvider    ◄─── Must be defined in server
        │                     (in MediaBrowser.Controller)
        │
        │ registered in
        ▼
DI Container (IServiceCollection)
        │
        │ injected into
        ▼
DynamicHlsController
        │
        │ consumes as
        ▼
IEnumerable<IHlsPlaylistProvider>
```

**Why the interface must be in the server**:

1. **Compile-time Contract**: Controllers need to reference the interface at compile time
2. **DI Registration**: The plugin registers itself via DI, but the container is configured in the server
3. **Multi-plugin Support**: The server needs a way to discover and aggregate providers from all plugins
4. **Inverse Dependency**: Server → Interface, Plugin → Interface (NOT Server → Plugin)

**Current design is correct**:
- Server defines minimal hook interfaces
- Plugin implements them
- Server discovers them via DI
- Server calls them polymorphically
- Plugin code is completely decoupled from server implementation

---

## 3. Detailed Interface Analysis

### 3.1 IHlsPlaylistProvider

**Current Definition**:
```csharp
namespace MediaBrowser.Controller.LiveTv;

public interface IHlsPlaylistProvider
{
    bool TryGetPlaylist(
        string mediaSourceId,
        EncodingProfile encodingProfile,
        out string? playlistPath);

    bool TryAdoptProcess(
        string mediaSourceId,
        EncodingProfile encodingProfile,
        string playlistPath,
        Process ffmpegProcess,
        string? liveStreamId);

    Task<string?> TryGetPlaylistContentAsync(
        string mediaSourceId,
        EncodingProfile encodingProfile,
        string targetPlaylistPath,
        CancellationToken cancellationToken);

    void NotifyPlaylistConsumer(
        string mediaSourceId,
        EncodingProfile encodingProfile);
}
```

**Status**: ✅ **CORRECT LOCATION - Cannot Move**

**Why**:
- Called by `DynamicHlsController` at HLS request time
- Called by `TranscodeManager` at job termination time
- Multiple providers supported (registered as `IEnumerable<IHlsPlaylistProvider>`)
- Plugin must implement this to be discovered

**Server-side usage**:
- [DynamicHlsController.cs line 337](DynamicHlsController.cs#L337): Calls `TryGetPlaylistContentAsync()` in warm hit loop
- [DynamicHlsController.cs line 350](DynamicHlsController.cs#L350): Calls `NotifyPlaylistConsumer()` before returning a warm playlist
- [TranscodeManager](TranscodeManager.cs): Calls `TryAdoptProcess()` when killing FFmpeg jobs

**Refactoring opportunity**: `TryGetPlaylist` is legacy/compatibility and unused by the server path. Consider deprecating it with `[Obsolete]` attributes. `TryAdoptProcess` is still required for warm pool adoption.

---

### 3.2 ILiveStreamProvider

**Current Definition**:
```csharp
namespace MediaBrowser.Controller.LiveTv;

public interface ILiveStreamProvider
{
    bool TryGetStream(
        string mediaSourceId,
        out ILiveStream? liveStream);

    bool TryAdoptStream(
        string id,
        ILiveStream liveStream);
}
```

**Status**: ✅ **CORRECT LOCATION - Cannot Move**

**Why**:
- Called by `MediaSourceManager.CloseLiveStreamIfNeededAsync()` when live stream consumer count reaches 0
- Multiple providers supported (registered as `IEnumerable<ILiveStreamProvider>`)
- Requires knowledge of `ILiveStream` interface (from `MediaBrowser.Controller.Library`)

**Server-side usage**:
- [MediaSourceManager.cs line 922](MediaSourceManager.cs#L922): Calls `TryAdoptStream()` to offer stream before closure

**Note**: This is relatively new (v1.8.0 feature) and well-designed. No changes recommended.

---

### 3.3 ITunerResourceProvider

**Current Definition**:
```csharp
namespace MediaBrowser.Controller.LiveTv;

public interface ITunerResourceProvider
{
    Task<bool> TryReleaseTunerResourceAsync(
        CancellationToken cancellationToken);
}
```

**Status**: ✅ **CORRECT LOCATION - Cannot Move**

**Why**:
- Called by `MediaSourceManager.OpenLiveStreamInternal()` when all tuners are in use
- Multiple providers supported (registered as `IEnumerable<ITunerResourceProvider>`)
- Escape hatch for plugins to release non-essential resources on demand

**Server-side usage**:
- [MediaSourceManager.cs line 522](MediaSourceManager.cs#L522): Calls `TryReleaseTunerResourceAsync()` after catching `LiveTvConflictException`

**Design**: Excellent — clean, minimal, single-purpose.

---

### 3.4 EncodingProfile

**Current Definition** (lines 1-60):
```csharp
namespace MediaBrowser.Controller.LiveTv;

public class EncodingProfile
{
    public EncodingProfile(
        string? videoCodec,
        string? audioCodec,
        int? videoBitrate,
        int? audioBitrate,
        int? width,
        int? height,
        int? audioChannels) { ... }

    public string VideoCodec { get; }
    public string AudioCodec { get; }
    public int VideoBitrate { get; }
    public int AudioBitrate { get; }
    public int Width { get; }
    public int Height { get; }
    public int AudioChannels { get; }

    public string ComputeHash() { ... }
    public override string ToString() { ... }
}
```

**Status**: ✅ **CORRECT LOCATION (but could be optimized)**

**Why it must stay**:
- Shared DTO between server and plugin
- Used in method signatures of all IWarm* interfaces
- Immutable value type with hashing capability
- Part of the warm provider contract

**Could be optimized**:
- Move XML documentation to a partial class in the plugin for examples
- No behavioral changes — this is a good design
- Consider making it `record` type (C# 9+) for built-in `Equals` and `GetHashCode()`

---

### 3.5 ICustomPlaylistPublisher

**Current Definition**:
```csharp
namespace MediaBrowser.Controller.LiveTv;

public interface ICustomPlaylistPublisher
{
    Task<bool> TryPublishPlaylistAsync(
        string mediaSourceId,
        EncodingProfile encodingProfile,
        string targetPlaylistPath,
        CancellationToken cancellationToken);
}
```

**Status**: ⚠️ **OPTIONAL - Unused by Server Path**

**Why it's optional/unused**:
- `TryGetPlaylistContentAsync()` on `IHlsPlaylistProvider` encapsulates the full warm-hit workflow used by the server
- The server no longer calls `ICustomPlaylistPublisher` directly
- Plugins may still implement it for internal reuse or manual publishing, but it is not required

**Recommendation**:
1. Consider marking as `[Obsolete("Use IHlsPlaylistProvider.TryGetPlaylistContentAsync instead")]` once plugins migrate
2. Keep in codebase for backward compatibility until ecosystem adoption is confirmed
3. Server code should continue to avoid calling it (current behavior)

---

## 4. Refactoring Opportunities

### 4.1 Deprecate Legacy Methods on IHlsPlaylistProvider

**Current code has multiple methods**; only the legacy lookup is superseded:

```csharp
// Legacy (kept for backward compatibility)
bool TryGetPlaylist(...);

// New (encapsulates warm-hit workflow)
Task<string?> TryGetPlaylistContentAsync(...);
```

**Action**:
```csharp
[Obsolete("Use TryGetPlaylistContentAsync instead", false)]
bool TryGetPlaylist(...);
```

**Rationale**:
- Gives plugins time to migrate
- Reduces confusion about which method to implement
- Encourages adoption of the simpler async pattern

---

### 4.2 Deprecate ICustomPlaylistPublisher

```csharp
[Obsolete("This interface is superseded by IHlsPlaylistProvider.TryGetPlaylistContentAsync. " +
          "Implement IHlsPlaylistProvider instead.", true)]
public interface ICustomPlaylistPublisher
{
    // ...
}
```

**Rationale**:
- The warmpool plugin no longer implements this
- All new providers should use `IHlsPlaylistProvider` exclusively
- Having two ways to publish is confusing and error-prone

---

### 4.3 Improve EncodingProfile as a Record Type

**Current**:
```csharp
public class EncodingProfile
{
    public EncodingProfile(...) { ... }
    public string VideoCodec { get; }
    // ... more properties
}
```

**Improved** (backward compatible):
```csharp
public record EncodingProfile(
    string VideoCodec,
    string AudioCodec,
    int VideoBitrate,
    int AudioBitrate,
    int Width,
    int Height,
    int AudioChannels)
{
    // Normalize empty strings to defaults for backward compatibility
    public EncodingProfile(
        string? videoCodec,
        string? audioCodec,
        int? videoBitrate,
        int? audioBitrate,
        int? width,
        int? height,
        int? audioChannels)
        : this(
            videoCodec ?? string.Empty,
            audioCodec ?? string.Empty,
            videoBitrate ?? 0,
            audioBitrate ?? 0,
            width ?? 0,
            height ?? 0,
            audioChannels ?? 0)
    {
    }

    public string ComputeHash() { ... }
}
```

**Benefits**:
- Records have built-in `Equals()` and `GetHashCode()` based on properties
- Immutability enforced by language
- Can compare `EncodingProfile` instances directly: `profile1 == profile2`
- Less boilerplate

---

### 4.4 Consolidate LiveTV Interfaces in a Dedicated Module

**Current problem**: LiveTV interfaces are scattered across multiple namespaces:
- `MediaBrowser.Controller.LiveTv` — warm pool hooks
- `MediaBrowser.Controller.Entities` — LiveTV domain models
- `Jellyfin.LiveTv` — LiveTV service implementations

**Recommendation** (future work):
- Keep `MediaBrowser.Controller.LiveTv` as the public contract
- All plugin-facing interfaces should be defined here
- Document which are "hook" interfaces (for plugin implementation) vs. "service" interfaces (consumed by plugins)

---

## 5. What CANNOT Be Moved to Plugin

### IHlsPlaylistProvider, ILiveStreamProvider, ITunerResourceProvider

These three must remain in the server because:

1. **Discovery Mechanism**: The DI container needs to know about them at registration time
2. **Controller/Service Dependencies**: Many server components depend on them:
   - `DynamicHlsController` depends on `IEnumerable<IHlsPlaylistProvider>`
   - `MediaSourceManager` depends on `IEnumerable<ILiveStreamProvider>` and `IEnumerable<ITunerResourceProvider>`
3. **Plugin Polymorphism**: The plugin system relies on these to be discoverable

The plugin implements them; the server uses them. This is correct architecture.

---

## 6. What CAN Be Simplified

### Server-Side Implementation

The server-side code **using** these interfaces is already quite clean:

**DynamicHlsController** (lines 320-360):
```csharp
// Check warm process providers for pre-buffered LiveTV streams
foreach (var warmProvider in _warmProcessProviders)
{
    var warmContent = await warmProvider.TryGetPlaylistContentAsync(...)
        .ConfigureAwait(false);

    if (warmContent is not null)
    {
        return Content(warmContent, ...);
    }
}
```

**This is already optimal** — simple, clean delegation to plugins.

---

## 7. Plugin-Owned Responsibilities

The warm plugin (`jellyfin-plugin-warmpool`) correctly owns:

- `WarmFFmpegProcessPool` — Core pool management
- `WarmStreamPool` — Live stream pooling
- `WarmPoolEntryPoint` — Session event subscription
- `WarmPoolController` — REST API for status/metrics
- `WarmProcessInfo` / `WarmStreamInfo` — Domain models
- `PluginConfiguration` — Settings management
- `TunerResourceProvider` — Logic for releasing tuner resources

This is excellent separation of concerns.

---

## 8. Recommendations Summary

| Item | Current | Action | Why |
|------|---------|--------|-----|
| `IHlsPlaylistProvider` | In server | **Keep** | Must be discoverable by DI |
| `ILiveStreamProvider` | In server | **Keep** | Must be discoverable by DI |
| `ITunerResourceProvider` | In server | **Keep** | Must be discoverable by DI |
| `EncodingProfile` | In server | **Keep** | Shared DTO across server/plugin boundary |
| `ICustomPlaylistPublisher` | In server | **Consider deprecate** | Unused by server path; retained for plugin compatibility |
| Legacy lookup on `IHlsPlaylistProvider` | Defined | **Deprecate** | Superseded by `TryGetPlaylistContentAsync` |
| LiveTV domain models | In server | **Keep** | Part of core LiveTV system |
| Plugin implementation classes | In plugin | **Keep** | Already in correct location |

---

## 9. Conclusion

**The current architecture is sound.**

The warm pool interfaces belong in the server because they are **discovery hooks** for the DI system, not domain logic. The principle of "minimize server changes" is already being followed:

- Server provides three minimal interfaces (IHlsPlaylistProvider, ILiveStreamProvider, ITunerResourceProvider)
- Server provides one shared DTO (EncodingProfile)
- **All business logic lives in the plugin** (WarmFFmpegProcessPool, scheduling, eviction, metrics)
- Server-side code using these interfaces is already clean and minimal

**What would NOT improve**:
- Moving interfaces to the plugin (they need to be in server for DI discovery)
- Reorganizing the directory structure (current structure is clear)
- Breaking up EncodingProfile (it's a small, well-designed DTO)

**What would improve** (minor):
- Mark the legacy lookup method as `[Obsolete]` to guide plugin developers
- Consider deprecating `ICustomPlaylistPublisher` explicitly once plugin adoption is confirmed
- Consider making `EncodingProfile` a `record` type (C# 9+)

---

## References

- [CLAUDE.md](CLAUDE.md) — Plugin-first development philosophy
- [LiveTV-Architecture.md](LiveTV-Architecture.md) — Detailed LiveTV flow diagrams
- [WarmPool-ChangePlan.md](WarmPool-ChangePlan.md) — Warm pool implementation phases
- Plugin: `jellyfin-plugin-warmpool` repository

