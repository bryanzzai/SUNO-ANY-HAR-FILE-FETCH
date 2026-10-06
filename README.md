# SUNO-ANY-HAR-FILE-FETCH

Windows WPF utility for inspecting HAR captures and recovering/downloading audio and video resources.

## Current branch: `har-raw-scanner`

This is deliberately a **raw development scanner**. It is not the cleaned-up production view yet.

### Current flow

1. Choose one `.har` file.
2. Choose a local output folder.
3. Press **BORE THROUGH HAR**.
4. The app walks the complete `log.entries` array.
5. The result grid shows the **first 100 HAR entries in original order**, including `Other` and diagnostic/error rows.
6. Audio/video rows that have a usable recovery path can be ticked.
7. Press **DOWNLOAD TICKED**.
8. Only at the write boundary are exact filename twins Roman-numbered.

The raw grid is never cosmetically deduplicated or renamed.

### Roman duplicate rule

Roman numbering is global and applies only when selected output rows have the same full filename
(case-insensitive), including the same extension.

Example:

```text
music.mp3
music.mp3
music.wav
```

is written as:

```text
music [ I ].mp3
music [ II ].mp3
music.wav
```

`music.mp3` and `music.wav` are not twins and are not Roman-numbered.

Roman order follows the original HAR entry order, not visual sorting.

### Recovery modes

The raw scanner currently recognizes:

- **Embedded** — response content is present inside the HAR.
- **Replay GET** — an HTTP/HTTPS GET can be attempted using captured request headers.
- **Unavailable** — visible for diagnosis, not tickable.

The download replay intentionally drops `Range`/`If-Range` so a captured partial request does not
automatically force a partial output file.

### Diagnostic philosophy

The development build favors evidence over presentation:

- maximum 100 displayed rows;
- the parser still scans the complete HAR for counts;
- no hidden dedupe;
- non-media entries remain visible;
- entry-level failures become visible diagnostic rows;
- fatal HAR structure/parser failures show the full exception dump.

A later production pass will filter the grid down to recoverable audio/video and silently bore past
bad entries while preserving useful failure status per attempted download.

## Build

Requirements:

- Windows 10 or 11
- .NET 10 SDK

```powershell
dotnet build .\src\SubdlProDownload\SubdlProDownload.csproj -c Release
dotnet run --project .\src\SubdlProDownload\SubdlProDownload.csproj -c Release
```
