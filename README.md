# SUNO-ANY-HAR-FILE-FETCH

Development branch: `har-raw-scanner`.

Windows WPF utility for reading a HAR file and recovering downloadable audio/video resources.

## Current workflow

1. Choose one `.har` file with the file picker.
2. Choose a local output folder.
3. Scan the HAR.
4. The scanner walks the full HAR entry list and keeps downloadable audio/video candidates.
5. The result grid shows at most 100 candidates and contains only:
   - `Download`
   - `release_name`
6. Every displayed row can be ticked with one click.
7. Press **Download selected**.
8. Exact same-name + same-extension output twins are Roman-numbered only when the files are written.

Example:

```text
music.mp3
music.mp3
music.wav
```

becomes:

```text
music [ I ].mp3
music [ II ].mp3
music.wav
```

`music.mp3` and `music.wav` are not twins.

`release_name` uses HAR comment/description text when available and otherwise falls back to the raw filename or URL-derived filename.

The current development build supports embedded HAR response bodies and replayable HTTP/HTTPS GET requests.

## Build

```powershell
dotnet build .\src\SubdlProDownload\SubdlProDownload.csproj -c Release
```
