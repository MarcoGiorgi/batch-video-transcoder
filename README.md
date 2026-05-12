# batch-video-transcoder

`batch-video-transcoder` (namespace root `BVT`) is a .NET 8 console application for analyzing a Jellyfin video library, generating CSV/JSON reports, and preparing conversions that favor Direct Play and Direct Stream.

It supports three strategies:

- `SkipCompatible`: files already using modern codecs (`h264`, `hevc`, `av1`, `vp9`)
- `LegacyTranscode`: transcodes only legacy video to H.264 while copying audio and subtitles when possible
- `DvdRemux`: remuxes ripped `VIDEO_TS` DVDs losslessly into MKV

## Project Structure

```text
batch-video-transcoder.sln
batch-video-transcoder/
  batch-video-transcoder.csproj
```

The produced assembly is named `batch-video-transcoder.exe` on Windows and `batch-video-transcoder` on Linux/macOS.

## Requirements

- .NET 8 SDK
- `ffmpeg` and `ffprobe` available in `PATH`

On Debian/Ubuntu:

```bash
sudo apt update
sudo apt install ffmpeg
```

The `ffmpeg` package also includes `ffprobe`.

On Windows, install ffmpeg and add its `bin` folder to `PATH`, or pass explicit binary paths with `--ffmpeg` and `--ffprobe`.

## Build

From the repository root:

```bash
dotnet build batch-video-transcoder.sln
```

## Running the Application

You can run from source with `dotnet run`:

```bash
dotnet run --project batch-video-transcoder -- report --root "/path/to/movies" --out "/path/to/transcode-report"
```

Or run the compiled executable after building:

```bash
./batch-video-transcoder/bin/Debug/net8.0/batch-video-transcoder report --root "/path/to/movies" --out "/path/to/transcode-report"
```

On Windows PowerShell:

```powershell
.\batch-video-transcoder\bin\Debug\net8.0\batch-video-transcoder.exe report --root "D:\Media\Movies" --out "D:\Media\transcode-report"
```

## CLI Examples

Report only:

```bash
dotnet run --project batch-video-transcoder -- report --root "/path/to/movies" --out "/path/to/transcode-report"
```

Process every report row marked with `NeedsProcessing=true`:

```bash
dotnet run --project batch-video-transcoder -- transcode --report "/path/to/transcode-report/report.json" --max-jobs 1
```

Process only DVD `VIDEO_TS` rows from an existing report:

```bash
dotnet run --project batch-video-transcoder -- transcode --report "/path/to/transcode-report/report.json" --dvd-only --max-jobs 1
```

Process a small chunk of pending items:

```bash
dotnet run --project batch-video-transcoder -- transcode \
  --report "/path/to/transcode-report/report.json" \
  --take 10 \
  --max-jobs 1 \
  --rate-control source-bitrate \
  --size-margin-percent 3
```

Run the same command again to process the next chunk. BVT marks completed rows as `Processed=true` in the report and skips them on later runs.

Process all remaining pending items:

```bash
dotnet run --project batch-video-transcoder -- transcode \
  --report "/path/to/transcode-report/report.json" \
  --max-jobs 1
```

Verify generated outputs:

```bash
dotnet run --project batch-video-transcoder -- verify \
  --report "/path/to/transcode-report/report.json"
```

Preview source cleanup without deleting anything:

```bash
dotnet run --project batch-video-transcoder -- cleanup \
  --report "/path/to/transcode-report/report.json"
```

Delete original sources that are already processed and have readable outputs:

```bash
dotnet run --project batch-video-transcoder -- cleanup \
  --report "/path/to/transcode-report/report.json" \
  --delete-sources
```

For regular files, cleanup deletes the original input files. For DVD rows, cleanup deletes the processed `VIDEO_TS` folder. Generated `.converted.mkv` and `.dvdremux.mkv` files are never deleted by cleanup; they are renamed to the final Jellyfin name when cleanup is applied.

Before deleting any source, cleanup verifies the generated MKV with `ffprobe` and renames it to the Jellyfin folder name:

```text
Title (1995)/Title (1995).converted.mkv  ->  Title (1995)/Title (1995).mkv
Title (1995)/Title (1995).dvdremux.mkv   ->  Title (1995)/Title (1995).mkv
```

If the final Jellyfin output already exists, cleanup skips that row and leaves sources untouched.

## Jellyfin Layout

The scanner expects movie folders in the Jellyfin style:

```text
Title (1995)/
  Title (1995).mkv
```

Multipart files in the same folder, such as `Part1` and `Part2`, are treated as one logical title. If the title uses a legacy codec, the parts are combined and transcoded into:

```text
Title (1995)/Title (1995).converted.mkv
```

## DVD VIDEO_TS

DVD detection:

```text
Movie (1995)/
  VIDEO_TS/
    VIDEO_TS.IFO
    VTS_01_0.IFO
    VTS_01_1.VOB
    VTS_01_2.VOB
```

If `VIDEO_TS/VIDEO_TS.IFO` exists, the folder is marked as `DVD_VIDEO_TS`.

The main title is estimated by grouping `VTS_XX_X.VOB` files and selecting the group with the largest total size. The output is:

```text
Movie (1995)/Movie (1995).dvdremux.mkv
```

DVD remux does not transcode: it only changes the container to MKV. To improve compatibility and seekability, BVT uses the main-title VOB chain through the `concat:` protocol, asks ffmpeg to regenerate problematic timestamps, and drops `dvd_nav_packet` data streams that are invalid in Matroska.

## Legacy Transcode

Legacy/problematic codecs:

```text
mpeg4, msmpeg4v3, mpeg2video, wmv1, wmv2, wmv3, flv1, rv40, indeo, cinepak, h263, divx
```

Tags `DX50`, `DIVX`, and `XVID` are also treated as legacy.

Quality rules:

- source file smaller than 1 GB: CRF 18
- source file greater than or equal to 1 GB: CRF 16
- no upscaling
- no frame-rate changes
- no video filters
- audio is always copied

If subtitle copying fails, BVT retries with `-sn`.

By default, legacy transcode uses `--rate-control source-bitrate`. BVT reads the original video bitrate, adds `--size-margin-percent` percent, and asks x264 to target that bitrate. This keeps output sizes closer to the original file than CRF-only encoding.

You can still use the original CRF behavior:

```bash
dotnet run --project batch-video-transcoder -- transcode \
  --report "/path/to/transcode-report/report.json" \
  --rate-control crf
```

CRF is quality-targeted, not size-targeted. It can create files that are larger or smaller than the source depending on noise, grain, interlacing, codec efficiency, and the original encode quality. A smaller H.264 file does not automatically mean obvious visible quality loss, but every lossy transcode has some generation loss. Source-bitrate mode is the safer choice when predictable disk usage matters.

## Report State

The report is also the resume database. Processing updates these fields:

```text
Processed, ProcessedAt, ProcessingError, SourceCleaned, SourceCleanedAt
```

Reports also include `RecommendedVideoBitrateKbps` for source-bitrate transcodes.

At the start of `transcode`, BVT prints:

```text
Processable items: <total>; already processed: <done>; remaining: <pending>; selected this run: <chunk>
```

This makes long runs safer: you can process 10, 20, or 50 items at a time and resume later without editing the report manually.

## Errors, Resume, and Parallelism

- Errors on a single file do not stop the full scan.
- Logs are written to the console and to files under `logs`.
- In `transcode` mode, rows with `Processed=true` are skipped.
- If an output already exists and is accepted, the row is marked as processed.
- `--take N` limits the current run to the first N pending rows.
- `--dvd-only` restricts `transcode` mode to DVD `VIDEO_TS` rows only.
- `--max-jobs N` controls how many ffmpeg jobs may run in parallel.
- Each completed job prints progress, elapsed time, and estimated time remaining.

## Remux vs Transcode

Remux means copying streams into a new container. For `VIDEO_TS` DVDs, MPEG2 video, AC3/DTS audio, and compatible subtitles are copied without quality loss.

Transcode means re-encoding video. It is used only for legacy/problematic codecs, never for `VIDEO_TS` DVDs.

## Code Documentation

The project generates XML documentation files during the build and uses the abbreviated namespaces `BVT` and `BVT.Models`.
