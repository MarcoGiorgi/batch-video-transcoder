# batch-video-transcoder

`batch-video-transcoder` (namespace root `BVT`) e una console app .NET 8 per analizzare una libreria video Jellyfin, generare report CSV/JSON e preparare conversioni compatibili con Direct Play/Direct Stream.

Supporta tre strategie:

- `SkipCompatible`: file gia moderni (`h264`, `hevc`, `av1`, `vp9`)
- `LegacyTranscode`: ricodifica solo il video legacy in H.264, copiando audio e sottotitoli quando possibile
- `DvdRemux`: DVD rippati `VIDEO_TS` rimuxati lossless in MKV

## Struttura progetto

```text
E:\Projects\Brains\VideoUtilities\BVT\batch-video-transcoder.sln
E:\Projects\Brains\VideoUtilities\BVT\batch-video-transcoder\batch-video-transcoder.csproj
```

L'assembly prodotto si chiama `batch-video-transcoder.exe`.

## Requisiti

- .NET 8 SDK
- `ffmpeg` e `ffprobe` disponibili nel `PATH`

Su Debian/Ubuntu:

```bash
sudo apt update
sudo apt install ffmpeg
```

Il pacchetto `ffmpeg` include anche `ffprobe`.

Su Windows installa ffmpeg e aggiungi la cartella `bin` al `PATH`, oppure passa i percorsi:

```powershell
E:\Projects\Brains\VideoUtilities\BVT\batch-video-transcoder\bin\Debug\net8.0\batch-video-transcoder.exe report --root "E:\Projects\Brains\VideoUtilities\BVT\VideoSample" --out "E:\Projects\Brains\VideoUtilities\BVT\transcode-report" --ffmpeg "C:\ffmpeg\bin\ffmpeg.exe" --ffprobe "C:\ffmpeg\bin\ffprobe.exe"
```

## Build

```powershell
dotnet build E:\Projects\Brains\VideoUtilities\BVT\batch-video-transcoder.sln
```

## Esempi CLI

Solo report:

```bash
dotnet run --project batch-video-transcoder -- report --root "/mnt/media/movies" --out "/mnt/media/transcode-report"
```

Conversione/remux dei soli elementi marcati `NeedsProcessing=true`:

```bash
dotnet run --project batch-video-transcoder -- transcode --report "/mnt/media/transcode-report/report.json" --max-jobs 1
```

Verifica output:

```bash
dotnet run --project batch-video-transcoder -- verify --report "/mnt/media/transcode-report/report.json"
```

Esecuzione compilata sui sample Windows:

```powershell
E:\Projects\Brains\VideoUtilities\BVT\batch-video-transcoder\bin\Debug\net8.0\batch-video-transcoder.exe report --root "E:\Projects\Brains\VideoUtilities\BVT\VideoSample" --out "E:\Projects\Brains\VideoUtilities\BVT\transcode-report"
```

## Struttura Jellyfin

La scansione assume cartelle film nello stile:

```text
Titolo (1995)/
  Titolo (1995).mkv
```

I file multipart nello stesso folder, per esempio `Part1` e `Part2`, vengono trattati come un solo titolo. Se sono legacy, vengono combinati e ricodificati in:

```text
Titolo (1995)/Titolo (1995).converted.mkv
```

## DVD VIDEO_TS

Rilevamento DVD:

```text
Film (1995)/
  VIDEO_TS/
    VIDEO_TS.IFO
    VTS_01_0.IFO
    VTS_01_1.VOB
    VTS_01_2.VOB
```

Se esiste `VIDEO_TS/VIDEO_TS.IFO`, la cartella viene marcata `DVD_VIDEO_TS`.

Il titolo principale viene stimato raggruppando i `VTS_XX_X.VOB` e scegliendo il gruppo con dimensione totale maggiore. L'output e:

```text
Film (1995)/Film (1995).dvdremux.mkv
```

DVD remux non ricodifica: cambia solo container in MKV. Per aumentare compatibilita e seekability, BVT usa i VOB del main title tramite `concat:` protocol, rigenera timestamp problematici con ffmpeg e scarta gli stream `dvd_nav_packet` non validi in Matroska.

Comando concettuale:

```bash
ffmpeg -hide_banner -y -fflags +genpts+igndts -i "concat:VTS_01_1.VOB|VTS_01_2.VOB" -map 0:v:0 -map 0:a -map 0:s? -dn -c copy "Film (1995).dvdremux.mkv"
```

Fallback automatici:

- tentativo 1: concat protocol, video + audio + sottotitoli opzionali, no data streams
- tentativo 2: concat demuxer, video + audio + sottotitoli opzionali, no data streams
- tentativo 3: video + audio, senza sottotitoli
- tentativo 4: video principale + prima traccia audio

## Transcode Legacy

Codec legacy/problematici:

```text
mpeg4, msmpeg4v3, mpeg2video, wmv1, wmv2, wmv3, flv1, rv40, indeo, cinepak, h263, divx
```

Sono considerati legacy anche i tag `DX50`, `DIVX`, `XVID`.

Comando:

```bash
ffmpeg -hide_banner -y -i "input.avi" -map 0 -c:v libx264 -preset medium -crf 18 -c:a copy -c:s copy "input.converted.mkv"
```

Regole qualita:

- file minore di 1 GB: CRF 18
- file maggiore o uguale a 1 GB: CRF 16
- nessun upscale
- nessun cambio framerate
- nessun filtro video
- audio sempre copiato

Se i sottotitoli falliscono, ritenta con `-sn`.

## Report

Il CSV/JSON include, tra le altre, queste colonne:

```text
MediaType, ProcessingStrategy, FullPath, InputFiles, IsMultipart, SizeGB,
Container, VideoCodec, VideoCodecTag, Width, Height, FrameRate, Duration,
AudioCodecs, SubtitleCodecs, MainTitleDetected, EstimatedDuration,
EstimatedMainMovieSizeGB, NeedsTranscode, NeedsProcessing, RecommendedCrf,
OutputPath, FfmpegCommand
```

Esempio strategia:

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

## Errori, resume e parallelismo

- Gli errori su un singolo file non fermano tutta la scansione.
- I log vengono scritti su console e su file in `logs`.
- In `transcode`, se l'output esiste gia, viene saltato: questa e la resume capability.
- `--max-jobs N` controlla quanti job ffmpeg possono girare in parallelo.
- Ogni job completato stampa progresso, elapsed time ed ETA stimata.

## Remux vs Transcode

Remux significa copiare gli stream in un nuovo container. Per i DVD `VIDEO_TS` il video MPEG2, audio AC3/DTS e sottotitoli compatibili vengono copiati senza perdita.

Transcode significa ricodificare il video. Viene usato solo per codec legacy/problematici, mai per DVD `VIDEO_TS`.

## Documentazione codice

Il progetto genera file XML documentation durante la build e usa namespace abbreviati `BVT` e `BVT.Models`.


