# batch-video-transcoder

`batch-video-transcoder` (namespace root `BVT`) is a .NET 8 console application for analyzing a Jellyfin video library, generating CSV/JSON reports, and preparing conversions that favor Direct Play and Direct Stream.

It supports three strategies:

- `SkipCompatible`: files already using modern codecs (`h264`, `hevc`, `av1`, `vp9`)
- `LegacyTranscode`: transcodes only legacy video to H.264 while copying audio and subtitles when possible
- `DvdRemux`: remuxes ripped `VIDEO_TS` DVDs losslessly into MKV

## Project Structure

```text
E:\Projects\Brains\VideoUtilities\BVT\batch-video-transcoder.sln
E:\Projects\Brains\VideoUtilities\BVT\batch-video-transcoder\batch-video-transcoder.csproj
```

The produced assembly is named `batch-video-transcoder.exe`.

## Requirements

- .NET 8 SDK
- `ffmpeg` and `ffprobe` available in `PATH`

On Debian/Ubuntu:

```bash
sudo apt update
sudo apt install ffmpeg
```

The `ffmpeg` package also includes `ffprobe`.

On Windows, install ffmpeg and add its `bin` folder to `PATH`, or pass explicit binary paths:

```powershell
E:\Projects\Brains\VideoUtilities\BVT\batch-video-transcoder\bin\Debug\net8.0\batch-video-transcoder.exe report --root "E:\Projects\Brains\VideoUtilities\BVT\VideoSample" --out "E:\Projects\Brains\VideoUtilities\BVT\transcode-report" --ffmpeg "C:\ffmpeg\bin\ffmpeg.exe" --ffprobe "C:\ffmpeg\bin\ffprobe.exe"
```

## Build

```powershell
dotnet build E:\Projects\Brains\VideoUtilities\BVT\batch-video-transcoder.sln
```

## CLI Examples

Report only:

```bash
dotnet run --project batch-video-transcoder -- report --root "/mnt/media/movies" --out "/mnt/media/transcode-report"
```

Convert/remux only items marked with `NeedsProcessing=true`:

```bash
dotnet run --project batch-video-transcoder -- transcode --report "/mnt/media/transcode-report/report.json" --max-jobs 1
```

Verify outputs:

```bash
dotnet run --project batch-video-transcoder -- verify --report "/mnt/media/transcode-report/report.json"
```

Run the compiled executable against local Windows samples:

```powershell
E:\Projects\Brains\VideoUtilities\BVT\batch-video-transcoder\bin\Debug\net8.0\batch-video-transcoder.exe report --root "E:\Projects\Brains\VideoUtilities\BVT\VideoSample" --out "E:\Projects\Brains\VideoUtilities\BVT\transcode-report"
```

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

Conceptual command:

```bash
ffmpeg -hide_banner -y -fflags +genpts+igndts -i "concat:VTS_01_1.VOB|VTS_01_2.VOB" -map 0:v:0 -map 0:a -map 0:s? -dn -c copy "Movie (1995).dvdremux.mkv"
```

Automatic fallbacks:

- attempt 1: concat protocol, video + audio + optional subtitles, no data streams
- attempt 2: concat demuxer, video + audio + optional subtitles, no data streams
- attempt 3: video + audio, no subtitles
- attempt 4: main video + first audio track

## Legacy Transcode

Legacy/problematic codecs:

```text
mpeg4, msmpeg4v3, mpeg2video, wmv1, wmv2, wmv3, flv1, rv40, indeo, cinepak, h263, divx
```

Tags `DX50`, `DIVX`, and `XVID` are also treated as legacy.

Command:

```bash
ffmpeg -hide_banner -y -i "input.avi" -map 0 -c:v libx264 -preset medium -crf 18 -c:a copy -c:s copy "input.converted.mkv"
```

Quality rules:

- source file smaller than 1 GB: CRF 18
- source file greater than or equal to 1 GB: CRF 16
- no upscaling
- no frame-rate changes
- no video filters
- audio is always copied

If subtitle copying fails, BVT retries with `-sn`.

## Report

The CSV/JSON report includes these columns, among others:

```text
MediaType, ProcessingStrategy, FullPath, InputFiles, IsMultipart, SizeGB,
Container, VideoCodec, VideoCodecTag, Width, Height, FrameRate, Duration,
AudioCodecs, SubtitleCodecs, MainTitleDetected, EstimatedDuration,
EstimatedMainMovieSizeGB, NeedsTranscode, NeedsProcessing, RecommendedCrf,
OutputPath, FfmpegCommand
```

Example strategy:

```json
{
  "MediaType": "DVD_VIDEO_TS",
  "ProcessingStrategy": "DvdRemux",
  "MainTitleDetected": "VTS_01",
  "Decision": {
    "NeedsTranscode": false,
    "NeedsProcessing": true,
    "ProcessingStrategy": "DvdRemux"
  }
}
```

## Errors, Resume, and Parallelism

- Errors on a single file do not stop the full scan.
- Logs are written to the console and to files under `logs`.
- In `transcode` mode, existing outputs are skipped; this is the resume capability.
- `--max-jobs N` controls how many ffmpeg jobs may run in parallel.
- Each completed job prints progress, elapsed time, and estimated time remaining.

## Remux vs Transcode

Remux means copying streams into a new container. For `VIDEO_TS` DVDs, MPEG2 video, AC3/DTS audio, and compatible subtitles are copied without quality loss.

Transcode means re-encoding video. It is used only for legacy/problematic codecs, never for `VIDEO_TS` DVDs.

## Code Documentation

The project generates XML documentation files during the build and uses the abbreviated namespaces `BVT` and `BVT.Models`.
