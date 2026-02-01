# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Jellyfin is a free software media system — a fork of Emby 3.5.2. This repository contains the **server backend** written in C# targeting .NET 10.0. The web client is in a separate `jellyfin-web` repository.

### Plugin-First Development Philosophy

**IMPORTANT**: When implementing new features or modifications, **minimize changes to the Jellyfin server codebase**. Prefer implementing functionality in plugins (like `jellyfin-plugin-warmpool`) whenever possible or feasible. The server should only provide minimal hook interfaces (like `IWarmProcessProvider`, `IWarmStreamProvider`) while keeping all business logic in the plugin. This approach:

- Keeps the core server lean and maintainable
- Allows features to be optional and independently versioned
- Reduces the risk of breaking core functionality
- Makes features easier to test, update, and disable
- Enables deployment without server recompilation

Only add server-side code when absolutely necessary (e.g., defining plugin interfaces, adding essential hooks at integration points). All feature logic, algorithms, and business rules should live in the plugin.

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

- `TryGetWarmPlaylist(mediaSourceId, encodingProfile, out playlistPath)` — called by `DynamicHlsController` only when no playlist exists (i.e., FFmpeg cold start is needed)
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
# Docker: /config/plugins/WarmPool/Jellyfin.Plugin.WarmPool.dll (inside container)
```

### Docker Deployment

**Production Environment**: Ubuntu server running Jellyfin in Docker container, managed via Portainer.

**Build Workflow**:
1. Build plugin locally (on Windows development machine)
2. Copy DLL to Docker host
3. Restart Jellyfin container to load new plugin version

**Deployment Steps**:
```bash
# On development machine (Windows)
cd C:\sourcecode\GitHub\jellyfin-plugin-warmpool
dotnet build

# Copy DLL to Docker host (via scp, shared volume, or Portainer file upload)
# Example using scp:
scp bin/Debug/net10.0/Jellyfin.Plugin.WarmPool.dll user@ubuntu-server:/path/to/jellyfin/config/plugins/WarmPool/

# Restart container via Portainer UI or Docker CLI on Ubuntu server:
docker restart jellyfin
```

**Plugin Path in Container**:
- Jellyfin typically mounts config at `/config` inside the container
- Plugins directory: `/config/plugins/WarmPool/`
- The host path depends on your Docker volume mapping (e.g., `/srv/jellyfin/config/plugins/WarmPool/`)

**Verification**:
- Check Jellyfin logs after restart: `docker logs jellyfin`
- Look for: `[PluginManager] Loaded plugin: Warm FFmpeg Process Pool 1.6.0`
- Access plugin settings in Jellyfin web UI: Dashboard → Plugins → Warm FFmpeg Process Pool

### Plugin Version Management

**IMPORTANT**: After each set of changes to the plugin, you **MUST** increment the version number in `build.props` according to semantic versioning (semver) standards:

- **MAJOR** (X.0.0): Increment for breaking changes, incompatible API changes, or when the plugin requires a newer Jellyfin server version
- **MINOR** (x.Y.0): Increment for new features, functionality additions, or enhancements that are backwards-compatible
- **PATCH** (x.y.Z): Increment for bug fixes, performance improvements, or other backwards-compatible changes

Version number location: `jellyfin-plugin-warmpool/Jellyfin.Plugin.WarmPool.csproj` in the `<Version>`, `<AssemblyVersion>`, and `<FileVersion>` elements.

Examples:
- Bug fix in pool cleanup logic: 1.3.0 → 1.3.1
- Add new configuration option: 1.3.1 → 1.4.0
- Change IWarmProcessProvider interface (breaking): 1.4.0 → 2.0.0

### Plugin Development Workflow

After making changes to the plugin, follow this workflow:

1. **Increment version** according to semantic versioning (see above)
2. **Build the plugin** to verify no compilation errors:
   ```bash
   cd C:\sourcecode\GitHub\jellyfin-plugin-warmpool
   dotnet build
   ```
3. **After successful build**, commit and push changes:
   ```bash
   git add .
   git commit -m "Descriptive commit message (e.g., 'Fix checkbox bug in configuration page')"
   git push
   ```

Always verify the build succeeds before committing to avoid pushing broken code.

### Key Plugin Design

- **MediaSourceId matching**: `MD5(streamUrl)` using `Encoding.Unicode` (UTF-16LE) to match Jellyfin's `M3UTunerHost` channel ID computation
- **Adoption**: On playback stop, adopts FFmpeg process + stores `liveStreamId` for tuner cleanup
- **Eviction**: Kills FFmpeg, deletes segments, calls `IMediaSourceManager.CloseLiveStream(liveStreamId)`
- **Pool size**: Configurable max warm processes (default 3)
- **Current version**: 1.6.0 (automatic adoption + encoding profile API parameters + fixed checkbox bug)

### Warm Pool Population: Automatic vs Manual

**IMPORTANT**: All LiveTV tuning requests from Jellyfin clients (web interface, mobile apps, etc.) should use **automatic adoption**, NOT manual pre-warming.

**Automatic Adoption (Recommended)**:
- When a client tunes a channel and then stops watching, the FFmpeg process is automatically adopted into the warm pool
- The exact encoding profile the client requested is captured (e.g., h264/aac 1920x1080 20Mbps)
- Next time ANY client tunes the same channel with the same encoding profile, it's a warm HIT
- No configuration needed - just enable the warm pool via the checkbox in plugin settings
- This is the primary design and works perfectly for normal Jellyfin client usage

**Manual Pre-Warming (Advanced)**:
- Via REST API: `POST /WarmPool/Start?channelId=...&streamUrl=...&videoCodec=h264&audioCodec=aac&videoBitrate=20000000&audioBitrate=256000&width=1920&height=1080&audioChannels=2`
- Useful for pre-warming channels during server startup or based on viewing history
- **Limitation**: Currently uses FFmpeg `-codec copy` regardless of specified encoding profile parameters
- The encoding profile is only used for pool key matching, not actual FFmpeg transcoding
- Future enhancement: Build full FFmpeg transcode command based on encoding profile

**Common Issue**: If you see continuous warm pool MISSES for the same channel:
1. Check that the warm pool is **enabled** via the plugin settings checkbox
2. Verify encoding profiles match between warm pool entries and client requests
3. For clients requesting transcoding (h264/aac), manually started processes with "copy/copy" codec won't match

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
