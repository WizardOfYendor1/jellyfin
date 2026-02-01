# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Jellyfin is a free software media system — a fork of Emby 3.5.2. This repository contains the **server backend** written in C# targeting .NET 10.0. The web client is in a separate `jellyfin-web` repository.

## Build & Test Commands

```bash
# Build the server
dotnet build Jellyfin.Server/Jellyfin.Server.csproj

# Build entire solution (all projects including tests)
dotnet build Jellyfin.sln

# Run the server (requires jellyfin-web built separately)
dotnet run --project Jellyfin.Server --webdir /path/to/jellyfin-web/dist

# Run all tests
dotnet test Jellyfin.sln

# Run tests with coverage (as CI does)
dotnet test Jellyfin.sln --configuration Release --collect:"XPlat Code Coverage"

# Run a single test project
dotnet test tests/Jellyfin.Api.Tests

# Run a specific test class
dotnet test tests/Jellyfin.Api.Tests --filter "FullyQualifiedName~ClassName"

# Run a specific test method
dotnet test tests/Jellyfin.Api.Tests --filter "FullyQualifiedName~ClassName.MethodName"
```

### EF Core Migrations

```bash
# Create a new SQLite migration (run from repo root)
dotnet ef migrations add MIGRATION_NAME --project "src/Jellyfin.Database/Jellyfin.Database.Providers.Sqlite" -- --migration-provider Jellyfin-SQLite
```

If `dotnet-ef` is not found, run `dotnet restore` first.

## Architecture

### Project Layering

The codebase has two generations of code: **legacy Emby-era** (`Emby.*`, `MediaBrowser.*`) and **modern Jellyfin** (`Jellyfin.*`, `src/Jellyfin.*`). Both coexist in the solution.

**Entry point**: `Jellyfin.Server` — ASP.NET Core host, startup configuration, DI registration.

**API layer**: `Jellyfin.Api` — REST controllers under `Controllers/`. Uses ASP.NET Core authorization, Swagger/Swashbuckle for OpenAPI generation.

**Core interfaces**: `MediaBrowser.Controller` — defines service interfaces (`ILibraryManager`, `IUserManager`, `ISessionManager`, `IMediaSourceManager`, `IMediaEncoder`, etc.) that the implementation projects fulfill. This is the primary dependency for plugins.

**Core models/DTOs**: `MediaBrowser.Model` — shared data transfer objects and enums used across layers.

**Common utilities**: `MediaBrowser.Common` — base plugin system, configuration abstractions, HTTP helpers.

**Implementation projects**:
- `Emby.Server.Implementations` — session management, library scanning, user data (legacy)
- `Jellyfin.Server.Implementations` — plugin manager, modern implementations
- `MediaBrowser.Providers` — metadata providers (TMDb, OMDb, etc.)
- `MediaBrowser.MediaEncoding` — FFmpeg process management (`TranscodeManager`), encoding helpers

**Modern modular libraries** (under `src/`):
- `Jellyfin.LiveTv` — LiveTV/DVR support
- `Jellyfin.MediaEncoding.Hls` — HLS playlist generation
- `Jellyfin.Database.Implementations` — EF Core DbContext, multi-provider (SQLite default)
- `Jellyfin.Extensions` — LINQ/string extension methods
- `Jellyfin.Networking` — network interface discovery
- `Jellyfin.Drawing` / `Jellyfin.Drawing.Skia` — image processing

### Key Patterns

**Dependency Injection**: All services are registered in DI. Controllers receive interfaces. Plugins can register `IWarmProcessProvider`, `IExternalIdProvider`, etc., which get injected as `IEnumerable<T>`.

**Streaming/Transcoding flow**: `DynamicHlsController` handles HLS requests → `StreamingHelpers.GetStreamingState()` resolves media info → `TranscodeManager.StartFfMpeg()` spawns FFmpeg → segments written to `GetTranscodePath()`.

**Plugin system**: Plugins are loaded from `{ConfigDir}/plugins/{PluginName}/`. They implement `IPlugin` and can provide DI-registered services. Plugin assemblies are whitelisted and loaded at startup by `PluginManager`.

### LiveTV HLS Flow (master.m3u8 path used by most clients)

1. Client → `GET Videos/{itemId}/master.m3u8` → `GetMasterHlsVideoPlaylist` returns variant playlist
2. Client → `GET Videos/{itemId}/main.m3u8` → `GetVariantHlsVideoPlaylist` returns media playlist (computed, no FFmpeg)
3. Client → `GET Videos/{itemId}/hls1/{playlistId}/{segmentId}.{container}` → `GetHlsVideoSegment` → `GetDynamicSegment` starts FFmpeg on first segment request
4. `OutputFilePath` = `MD5("{MediaPath}-{UserAgent}-{DeviceId}-{PlaySessionId}")` — different each session

Alternative: `GET Videos/{itemId}/live.m3u8` → `GetLiveHlsStream` — starts FFmpeg immediately, returns playlist directly. Used by some clients for LiveTV.

### Playback Stop Cleanup

`PlaystateController.ReportPlaybackStopped` runs sequentially:

1. `TranscodeManager.KillTranscodingJobs()` — kills FFmpeg process (or offers to warm pool via `IWarmProcessProvider.TryAdoptProcess()`)
2. `SessionManager.OnPlaybackStopped()` → `CloseLiveStreamIfNeededAsync()` → `MediaSourceManager.CloseLiveStream()` — decrements `ConsumerCount`, closes tuner if zero

These are independent cleanup paths — both run even if the other has side effects. When the warm pool adopts a process, `ConsumerCount` is bumped to prevent premature tuner closure.

### Warm Pool Extension Point (feature/fastchannelzapping)

`IWarmProcessProvider` (`MediaBrowser.Controller/LiveTv/IWarmProcessProvider.cs`) allows plugins to keep FFmpeg processes alive for fast LiveTV channel zapping:

- `TryGetWarmPlaylist(mediaSourceId, out playlistPath)` — called by `DynamicHlsController` before FFmpeg cold start
- `TryAdoptProcess(mediaSourceId, playlistPath, ffmpegProcess, liveStreamId)` — called by `TranscodeManager` when killing a job

Registered via DI as `IEnumerable<IWarmProcessProvider>`. Multiple providers supported; first-to-adopt wins. Only applies to infinite streams (LiveTV).

## Related Repository: jellyfin-plugin-warmpool

**Location**: `C:\sourcecode\GitHub\jellyfin-plugin-warmpool` (sibling to this repo)

The warm pool **plugin** implements `IWarmProcessProvider` and lives in a separate repository. It is the preferred approach — keeping warm pool logic in a plugin rather than modifying Jellyfin core/server code. The server only provides minimal hook interfaces.

### Plugin Structure (~600 lines C#)

| File | Purpose |
|------|---------|
| `Plugin.cs` | Entry point, extends `BasePlugin<PluginConfiguration>` |
| `PluginServiceRegistrator.cs` | DI registration: `IWarmProcessProvider` → `WarmProcessProvider` |
| `PluginConfiguration.cs` | Config: `Enabled`, `PoolSize` (default 3), `IdleTimeoutMinutes`, `FFmpegPath` |
| `WarmProcessProvider.cs` | Bridge implementing `IWarmProcessProvider`, delegates to pool |
| `WarmFFmpegProcessPool.cs` | Core pool logic: adoption, lookup, eviction, cleanup (386 lines) |
| `WarmProcessInfo.cs` | Metadata per warm process (process handle, playlist path, liveStreamId) |
| `WarmPoolController.cs` | REST API: `POST /WarmPool/Start`, `POST /WarmPool/Stop`, `GET /WarmPool/Status` |

### Plugin Build

```bash
# Build plugin (requires jellyfin repo as sibling directory)
cd C:\sourcecode\GitHub\jellyfin-plugin-warmpool
dotnet build

# Deploy: copy DLL to Jellyfin plugins directory
# Windows: C:\ProgramData\Jellyfin\Server\plugins\WarmPool\Jellyfin.Plugin.WarmPool.dll
# Linux: /var/lib/jellyfin/plugins/WarmPool/Jellyfin.Plugin.WarmPool.dll
```

### Key Plugin Design

- **MediaSourceId matching**: `MD5(streamUrl)` using `Encoding.Unicode` (UTF-16LE) to match Jellyfin's `M3UTunerHost` channel ID computation
- **Adoption**: On playback stop, adopts FFmpeg process + stores `liveStreamId` for tuner cleanup
- **Eviction**: Kills FFmpeg, deletes segments, calls `IMediaSourceManager.CloseLiveStream(liveStreamId)`
- **Pool size**: Configurable max warm processes (default 3)
- **Current version**: 1.3.0 (automatic adoption + live stream lifecycle management)

### Known Gap: Encoding Parameter Matching

The current `IWarmProcessProvider` interface passes only `mediaSourceId` (channel identity). It does NOT pass encoding parameters (video codec, resolution, bitrate). Different clients may request different transcoding profiles for the same channel. The interface needs to be extended to include encoding parameters so warm hits only occur when both channel AND profile match. See `WarmPool-ChangePlan.md` Phase 1 for the plan.

## Detailed Documentation

These files in `.claude/` contain full architectural context for LiveTV development:

- **`LiveTV-Architecture.md`** — Complete LiveTV tuning/streaming architecture, all code flows, key data structures, warm pool server-side implementation
- **`WarmPool-ChangePlan.md`** — Issues, gaps, and phased plan for warm pool improvements
- **`DesignAndInstructions.MD`** — Original design goals and instructions

These documents should be kept up to date as development progresses and new findings emerge.

## Code Style & Conventions

- **Nullable reference types**: enabled globally. All new code must handle nullability.
- **Warnings as errors**: enabled (`TreatWarningsAsErrors`). Code must compile warning-free.
- **StyleCop + Roslyn analyzers**: enforced in Debug builds. Custom analyzers in `Jellyfin.CodeAnalysis`.
- **Indentation**: 4 spaces for C#, 2 spaces for XML/YAML.
- **Braces**: Allman style (new line).
- **`var` usage**: use `var` when type is apparent or built-in.
- **String comparison**: always specify `StringComparison` (CA1305, CA1307, CA1309, CA1310).
- **Async**: use async methods in async context (CA1849). Always use `.ConfigureAwait(false)`.
- **File-scoped namespaces**: preferred in modern files (mixed with block-scoped in legacy code).

### Banned APIs (BannedSymbols.txt)

- `Task<T>.Result` — use `await` instead
- `Guid.operator==`, `Guid.operator!=`, `Guid.Equals(object)` — use `Guid.Equals(Guid)` instead

### PR/Commit Style

Titles: short, descriptive, imperative mood ("Fix X", "Add Y", not "Fixed X", "Added Y"). Reference: https://chris.beams.io/posts/git-commit/

## Test Organization

- Framework: **xUnit** with **Moq** for mocking and **FsCheck.Xunit** for property-based tests
- Test projects mirror source projects under `tests/` (e.g., `tests/Jellyfin.Api.Tests` tests `Jellyfin.Api`)
- Integration tests in `Jellyfin.Server.Integration.Tests` generate and validate the OpenAPI spec
- Coverage settings in `tests/coverletArgs.runsettings`
