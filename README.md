# SUNO-ANY-HAR-FILE-FETCH

Development branch: `har-raw-scanner`.

Windows WPF utility for reading a current Suno HAR file and producing a ZIP of PCM WAV files.

## Current workflow

1. Choose one `.har` file with the file picker.
2. Choose a local output folder.
3. Scan the HAR.
4. The scanner finds the Suno clip metadata, live M4A sources, and matching rights requests.
5. The result grid shows at most 100 candidates and contains only:
   - `Download`
   - `releasename`
6. Every displayed row can be ticked with one click.
7. Press **Download selected**.
8. For every selected clip, the app fetches the live source, decrypts the M4A/Opus source when required, and invokes FFmpeg to decode its PCM samples into a WAV container.
9. The selected WAV files are Roman-numbered only at the output door, then packed into one ZIP file.

Example:

```text
Velvet Paradise.wav
Velvet Paradise.wav
Velvet Paradise.wav
```

becomes:

```text
Velvet Paradise [ I ].wav
Velvet Paradise [ II ].wav
Velvet Paradise [ III ].wav
```

The comparison is the exact final full filename, including the extension. A group of one is left untouched; different extensions are not twins.

`releasename` uses the clip title from Suno project metadata when available and otherwise falls back to HAR comment/description text or the source filename.

The HAR’s embedded media fragments are used only to classify the recorded network posts. The actual work replays the live request and matching rights request; no bearer token or decrypted M4A is written to the ZIP. FFmpeg must be installed and available on PATH.

## Build

```powershell
dotnet build .\src\SubdlProDownload\SubdlProDownload.csproj -c Release
```
