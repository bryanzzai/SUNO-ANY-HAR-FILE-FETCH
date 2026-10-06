# SUNO-ANY-HAR-FILE-FETCH

Development branch: `har-raw-scanner`.

Windows WPF utility for reconstructing embedded Suno M4A sources from a HAR file.

## Current workflow

1. Choose one `.har` file with the file picker.
2. Choose a local output folder.
3. Scan the HAR.
4. The scanner finds embedded M4A payloads, Suno clip metadata, and matching embedded rights responses.
5. The result grid shows at most 100 candidates and contains only:
   - `Download`
   - `releasename`
6. Every displayed row can be ticked with one click.
7. Press **Download selected**.
8. For every selected clip, the app reconstructs the embedded M4A/Opus source offline and decrypts it when required using .NET cryptography.
9. The selected M4A files are Roman-numbered only at the output door, then written to the selected output folder.

Example:

```text
Velvet Paradise.m4a
Velvet Paradise.m4a
Velvet Paradise.m4a
```

becomes:

```text
Velvet Paradise [ I ].m4a
Velvet Paradise [ II ].m4a
Velvet Paradise [ III ].m4a
```

The comparison is the exact final full filename, including the extension. A group of one is left untouched; different extensions are not twins.

`releasename` uses the clip title from Suno project metadata when available and otherwise falls back to HAR comment/description text or the source filename.

The app never makes a network request. It uses only the selected HAR's embedded response bodies and never writes the bearer token or rights material to disk. No FFmpeg installation is required.

## Build

```powershell
dotnet build .\src\SubdlProDownload\SubdlProDownload.csproj -c Release
```
