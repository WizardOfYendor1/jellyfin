# Android TV LiveTV Record Failure (Year 0000 Date)

## Summary
Android TV (Jellyfin Android TV 0.19.7) fails to create LiveTV recordings. The client sends `StartDate`/`EndDate` values that serialize to year **0000**, which is **not parseable by .NET DateTime**. Model binding fails and the server returns **HTTP 400** before the controller runs.

## Symptoms
- Android TV UI shows "Unable to create recording".
- Server logs show:
  - `GET /LiveTv/Timers/Defaults` -> 200
  - `POST /LiveTv/Timers` -> 400
- No LiveTvController.CreateTimer logs fire (request fails during model binding).

## Evidence (Captured Request)
```
POST /LiveTv/Timers
Content-Type: application/json; charset=utf-8
Body:
{
  "Type": "SeriesTimer",
  "ProgramId": "<guid>",
  "StartDate": "0000-12-31T19:03:58-04:56:02",
  "EndDate": "0000-12-31T19:03:58-04:56:02",
  ...
}
```
The year **0000** causes `System.Text.Json` -> `DateTime` parsing to throw, resulting in HTTP 400.

## Root Cause
The Android TV client flow calls:
1. `GET /LiveTv/Timers/Defaults` **without** `programId`.
2. The server returns a default timer with `DateTime.MinValue` for `StartDate`/`EndDate`.
3. The client serializes those values with a timezone offset, producing **year 0000**.
4. Server model binding rejects the request (invalid DateTime), returning **400**.

This behavior is not tied to warm-pool changes; it’s a client-side serialization edge case combined with strict server parsing.

## Resolution Implemented (Server-side, Robust)
Updated the shared JSON DateTime converter to **accept year 0000** and normalize it to `DateTime.MinValue` (year 0001). This allows the request to bind successfully and proceed to normal LiveTV timer handling.

**Code change:** `src/Jellyfin.Extensions/Json/Converters/JsonDateTimeConverter.cs`

```csharp
if (text.StartsWith("0000", StringComparison.Ordinal))
{
    return DateTime.MinValue;
}
```

## Why This Is Safe
- Only affects DateTime parsing when year is **0000** (invalid in .NET).
- Leaves all standard/valid DateTime values unchanged.
- Provides resilience for any client that serializes `DateTime.MinValue` into year 0000.

## Suggested Upstream Actions
1. **Server-side fix (recommended):** keep the JsonDateTimeConverter normalization for year 0000.
2. **Client-side improvement (optional):** when programId is null, do not send Start/End, or send a valid value (e.g., omit or use `0001-01-01T00:00:00Z`).

## Reproduction Steps
1. Use Android TV client (0.19.7) on Live TV.
2. Press **Record** on a live channel.
3. Observe `POST /LiveTv/Timers` with year 0000 date values.
4. Server returns 400; recording fails.

## Fix Verification
After applying the converter change, `POST /LiveTv/Timers` succeeds and recording is created.

---

## Commit (Local)
- `Handle year 0000 dates in JsonDateTimeConverter`

## Clean-up
All debug logging and investigation-only changes were reverted to keep the fix minimal.
