# Copilot Instructions for Jellyfin Server

## Project Overview

Jellyfin is a free software media system—a fork of Emby 3.5.2 written in C# targeting .NET 10.0. This repository contains the **server backend** only; the web client lives in a separate `jellyfin-web` repository.

## Architecture Quick Start

### Two Generations of Code
- **Legacy (Emby-era)**: `Emby.*`, `MediaBrowser.*` namespaces coexist with modern code
- **Modern (Jellyfin-era)**: `Jellyfin.*`, `src/Jellyfin.*` namespaces represent new patterns

### Key Projects & Responsibilities

| Project | Role |
|---------|------|
| `Jellyfin.Server` | ASP.NET Core entry point, startup configuration, DI registration |
| `Jellyfin.Api` | REST controllers (`Controllers/`), OpenAPI/Swagger, authorization |
| `MediaBrowser.Controller` | **Core service interfaces** — primary plugin dependency (ILibraryManager, IUserManager, ISessionManager, IMediaSourceManager, IMediaEncoder, etc.) |
| `MediaBrowser.Model` | Shared DTOs and enums across all layers |
| `MediaBrowser.Common` | Plugin system, configuration abstractions, HTTP utilities |
| `Emby.Server.Implementations` | Session management, library scanning, user data (legacy implementations) |
| `Jellyfin.Server.Implementations` | Modern plugin manager |
| `MediaBrowser.Providers` | Metadata providers (TMDb, OMDb, etc.) |
| `MediaBrowser.MediaEncoding` | FFmpeg process management, transcoding helpers |
| `src/Jellyfin.LiveTv` | LiveTV/DVR tuning, channel management, HLS streaming |
| `src/Jellyfin.Database*` | EF Core DbContext, multi-provider support (SQLite default) |
| `src/Jellyfin.Drawing*` | Image processing with Skia |

### Layering Pattern
**API Controller → Service Interface → Implementation → Data Layer**

Controllers depend on interfaces in `MediaBrowser.Controller`, never directly on implementations. This allows plugins to replace or extend behavior.

## Critical Development Workflows

### Build & Test

```bash
# Build the server (primary entry point)
dotnet build Jellyfin.Server/Jellyfin.Server.csproj

# Build entire solution
dotnet build Jellyfin.sln

# Run all tests
dotnet test Jellyfin.sln

# Run with coverage (CI approach)
dotnet test Jellyfin.sln --configuration Release --collect:"XPlat Code Coverage"

# Test a single project
dotnet test tests/Jellyfin.Api.Tests

# Test with filter (class or method name)
dotnet test tests/Jellyfin.Api.Tests --filter "FullyQualifiedName~ClassName.MethodName"

# EF Core migrations (SQLite)
dotnet ef migrations add MIGRATION_NAME --project "src/Jellyfin.Database/Jellyfin.Database.Providers.Sqlite" -- --migration-provider Jellyfin-SQLite
```

### Running the Server Locally

```bash
# Run with default settings (requires jellyfin-web built separately)
dotnet run --project Jellyfin.Server

# Run with custom web directory
dotnet run --project Jellyfin.Server -- --webdir /path/to/jellyfin-web/dist
```

## Code Patterns & Conventions

### Dependency Injection
- All services registered in DI at startup
- Controllers receive interfaces via constructor injection
- **Plugins register as `IEnumerable<T>`**: Multiple implementations supported (e.g., `IWarmProcessProvider`, `IExternalIdProvider`)
- Key interface locations: all in `MediaBrowser.Controller/`

### Streaming/Transcoding Flow
1. **Client Request** → `DynamicHlsController.GetHlsVideoSegment()`
2. **Resolve Media** → `StreamingHelpers.GetStreamingState()`
3. **Start FFmpeg** → `TranscodeManager.StartFfMpeg()`
4. **Write Segments** → `{TranscodePath}/{OutputFilePath}.{container}`
5. **Cleanup** → `TranscodeManager.KillTranscodingJobs()` + `SessionManager.CloseLiveStreamIfNeededAsync()`

**Output path hashing**: `MD5("{MediaPath}-{UserAgent}-{DeviceId}-{PlaySessionId}")` — different per session

### LiveTV/DVR Patterns

**Tuner lifecycle**:
1. Client calls `LiveTvController.GetChannels()` → lists available tuner hosts
2. Client requests playback → `MediaSourceManager.OpenLiveStreamAsync()` → claims tuner resource
3. Streaming flows through HLS (see Streaming/Transcoding Flow above)
4. `PlaystateController.ReportPlaybackStopped()` → cleanup in two independent paths:
   - `TranscodeManager.KillTranscodingJobs()` — stops FFmpeg (or offers to plugin pool)
   - `SessionManager.CloseLiveStreamIfNeededAsync()` → `MediaSourceManager.CloseLiveStream()` — decrements tuner count

**Plugin Hooks** (in `MediaBrowser.Controller/LiveTv/`):
- `IWarmProcessProvider`: Adopt idle FFmpeg processes for fast channel zapping
- `ITunerResourceProvider`: Release tuners on demand when all are in use
- `IWarmStreamProvider`: (v1.8.0+) Direct stream reuse for LiveTV

### Plugin Philosophy
**MINIMIZE server changes; prefer plugin implementation:**
- Server provides minimal hook interfaces (IWarmProcessProvider, ITunerResourceProvider, IWarmStreamProvider)
- All feature logic, algorithms, and business rules live in the plugin
- Plugins are optional, independently versioned, testable, and deployable without server recompilation
- Example: WarmPool FFmpeg optimization is a plugin, not server code

## Code Style & Enforcement

- **Nullable reference types**: Required; handle all nullability
- **Warnings as errors**: All code must compile warning-free
- **StyleCop + Roslyn**: Enforced in Debug builds; custom analyzers in `Jellyfin.CodeAnalysis`
- **Indentation**: 4 spaces (C#), 2 spaces (XML/YAML)
- **Braces**: Allman style (opening brace on new line)
- **var usage**: Allowed when type is apparent
- **String comparison**: Always specify `StringComparison` (CA1305, CA1307, CA1309, CA1310)
- **Async**: Use `await` in async contexts; always `.ConfigureAwait(false)`
- **File-scoped namespaces**: Preferred in modern files (mixed with block-scoped in legacy code)

### Banned APIs
- `Task<T>.Result` → use `await`
- `Guid.operator==`, `Guid.operator!=` → use `Guid.Equals(Guid)`

## Testing Framework

- **Framework**: xUnit
- **Mocking**: Moq
- **Property-based tests**: FsCheck.Xunit
- **Coverage settings**: `tests/coverletArgs.runsettings`
- **Integration tests**: `Jellyfin.Server.Integration.Tests` — validates OpenAPI spec
- **Test layout**: Mirror source structure under `tests/` (e.g., `tests/Jellyfin.Api.Tests` tests `Jellyfin.Api`)

## Database & Configuration

- **Default provider**: SQLite (EF Core)
- **Config location**: `{ConfigDir}/config/` (per platform)
- **Database file**: `{ConfigDir}/data/jellyfin.db` (SQLite)
- **Plugin directory**: `{ConfigDir}/plugins/{PluginName}/`
- **Transcode cache**: `{ConfigDir}/transcodes/`

## Common Pitfalls

1. **Plugin registration too late**: Register plugin services in `IServiceCollection` extensions, not at runtime
2. **Forgetting `.ConfigureAwait(false)`**: Required for library code (triggers CA1849)
3. **String comparison without StringComparison**: Triggers CA1305/CA1307
4. **Direct implementation dependencies**: Always depend on `MediaBrowser.Controller` interfaces
5. **Tuner cleanup order**: Both `TranscodeManager` and `SessionManager` cleanup run independently — plugin must handle adoption correctly

## Key Files to Reference

- [Jellyfin.Api/BaseJellyfinApiController.cs](Jellyfin.Api/BaseJellyfinApiController.cs) — Base controller class
- [Jellyfin.Api/Controllers/DynamicHlsController.cs](Jellyfin.Api/Controllers/DynamicHlsController.cs) — HLS streaming entry point
- [Emby.Server.Implementations/ApplicationHost.cs](Emby.Server.Implementations/ApplicationHost.cs) — DI registration (search `RegisterServices`)
- [MediaBrowser.Controller/LiveTv/IWarmProcessProvider.cs](MediaBrowser.Controller/LiveTv/IWarmProcessProvider.cs) — Plugin hook for process adoption
- [MediaBrowser.Controller/LiveTv/ITunerResourceProvider.cs](MediaBrowser.Controller/LiveTv/ITunerResourceProvider.cs) — Plugin hook for tuner release

## When Unsure

1. **Service patterns**: Examine `MediaBrowser.Controller` for the interface, then find implementation in `Emby.Server.Implementations` or `Jellyfin.Server.Implementations`
2. **API design**: Check existing controllers in `Jellyfin.Api/Controllers/` for patterns (authorization, DTOs, responses)
3. **Plugin interactions**: Search `.claude/CLAUDE.md` for detailed LiveTV and warm pool workflows
4. **Build issues**: Run `dotnet restore` first, then check `Directory.Packages.props` for version conflicts

## Example: Adding a New API Endpoint

1. Create controller in `Jellyfin.Api/Controllers/YourController.cs` inheriting `BaseJellyfinApiController`
2. Inject service interfaces (from `MediaBrowser.Controller`) via constructor
3. Add `[HttpGet]` / `[HttpPost]` method with `[Authorize]` if needed
4. Return `ActionResult<YourDto>` or `IActionResult` with appropriate status codes
5. Add XML documentation (`/// <summary>`) for OpenAPI generation
6. Write tests in `tests/Jellyfin.Api.Tests/Controllers/YourControllerTests.cs`
7. Run `dotnet test` to validate and regenerate OpenAPI spec
