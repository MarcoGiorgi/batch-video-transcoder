using System.Text.Json;
using BVT.Models;

namespace BVT;

/// <summary>
/// Coordinates the command-line application modes: report generation, processing, and output verification.
/// </summary>
public static class App
{
    /// <summary>
    /// Parses command-line arguments, loads configuration, and dispatches to the requested application mode.
    /// </summary>
    /// <param name="args">The raw command-line arguments passed to the executable.</param>
    /// <returns>A process exit code: 0 for success, non-zero for validation or runtime failures.</returns>
    public static async Task<int> RunAsync(string[] args)
    {
        var options = CliOptions.Parse(args);
        if (options.ShowHelp || options.Mode is null)
        {
            CliOptions.PrintUsage();
            return options.ShowHelp ? 0 : 1;
        }

        // appsettings.json provides machine defaults, while CLI switches remain the per-run override surface.
        var settings = AppSettings.Load(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
        options.ApplyDefaults(settings);

        if (!options.IsValid(out var error))
        {
            ConsoleLogger.Error(error);
            CliOptions.PrintUsage();
            return 1;
        }

        Directory.CreateDirectory(options.LogDirectory);
        using var log = new FileLogger(Path.Combine(options.LogDirectory, $"video-analyzer-{DateTime.Now:yyyyMMdd-HHmmss}.log"));

        try
        {
            return options.Mode switch
            {
                "report" => await RunReportAsync(options, log),
                "transcode" => await RunTranscodeAsync(options, log),
                "verify" => await RunVerifyAsync(options, log),
                _ => UnknownMode(options.Mode)
            };
        }
        catch (Exception ex)
        {
            ConsoleLogger.Error(ex.Message);
            log.Error(ex.ToString());
            return 1;
        }
    }

    /// <summary>
    /// Scans the library, probes media metadata, plans processing decisions, and writes CSV/JSON reports.
    /// </summary>
    /// <param name="options">Validated command-line options for the report run.</param>
    /// <param name="log">The file logger used to persist diagnostic details.</param>
    /// <returns>A process exit code for report mode.</returns>
    private static async Task<int> RunReportAsync(CliOptions options, FileLogger log)
    {
        var ffprobe = new FfprobeService(options.FfprobePath, log);
        if (!await ffprobe.IsAvailableAsync())
        {
            ConsoleLogger.Error($"ffprobe non trovato o non eseguibile: {options.FfprobePath}");
            log.Error($"ffprobe unavailable: {options.FfprobePath}");
            return 2;
        }

        Directory.CreateDirectory(options.OutputDirectory!);
        var scanner = new MediaScanner();
        var dvdDetector = new DvdVideoTsDetector();
        var dvdAnalyzer = new DvdTitleAnalyzer();
        var planner = new TranscodePlanner(options.Preset);
        var rows = new List<MediaFileInfo>();

        // The scanner returns logical titles, not just files: a DVD folder and a multipart movie each become one row.
        var items = scanner.Scan(options.RootDirectory!, dvdDetector, dvdAnalyzer).ToList();
        ConsoleLogger.Info($"Trovati {items.Count} titoli video/DVD in {options.RootDirectory}");
        log.Info($"Scanning {items.Count} media items under {options.RootDirectory}");

        foreach (var item in items)
        {
            try
            {
                ConsoleLogger.Info($"Analisi: {item.FullPath}");
                var media = item.MediaType == MediaType.DVD_VIDEO_TS
                    ? await ProbeDvdBestEffortAsync(ffprobe, item, log)
                    : MergeProbe(item, await ffprobe.ProbeAsync(item.FullPath));

                media.Decision = planner.Decide(media);
                media.ProcessingStrategy = media.Decision.ProcessingStrategy;
                rows.Add(media);

                if (media.Decision.NeedsProcessing)
                {
                    ConsoleLogger.Warn($"Da processare: {media.FileName} ({media.Decision.Reason})");
                }
            }
            catch (Exception ex)
            {
                // A bad file must not prevent the rest of a large Jellyfin library from being analyzed.
                ConsoleLogger.Error($"Errore su {item.FullPath}: {ex.Message}");
                log.Error($"Probe failed for {item.FullPath}: {ex}");
            }
        }

        var writer = new ReportWriter(log);
        var jsonPath = Path.Combine(options.OutputDirectory!, "report.json");
        var csvPath = Path.Combine(options.OutputDirectory!, "report.csv");
        await writer.WriteJsonAsync(jsonPath, rows);
        await writer.WriteCsvAsync(csvPath, rows);

        ConsoleLogger.Success($"Report JSON: {jsonPath}");
        ConsoleLogger.Success($"Report CSV: {csvPath}");
        ConsoleLogger.Success($"Legacy transcode: {rows.Count(x => x.Decision.ProcessingStrategy == ProcessingStrategy.LegacyTranscode)}");
        ConsoleLogger.Success($"DVD remux: {rows.Count(x => x.Decision.ProcessingStrategy == ProcessingStrategy.DvdRemux)}");
        ConsoleLogger.Success($"Skip compatibili: {rows.Count(x => x.Decision.ProcessingStrategy == ProcessingStrategy.SkipCompatible)}");
        return 0;
    }

    /// <summary>
    /// Executes all report rows that require processing, honoring resume and max parallel job settings.
    /// </summary>
    /// <param name="options">Validated command-line options for processing mode.</param>
    /// <param name="log">The file logger used to persist ffmpeg failures and operations.</param>
    /// <returns>A process exit code for processing mode.</returns>
    private static async Task<int> RunTranscodeAsync(CliOptions options, FileLogger log)
    {
        var rows = await ReadReportAsync(options.ReportPath!);
        var executor = new FfmpegExecutor(options.FfmpegPath, log);
        if (!await executor.IsAvailableAsync())
        {
            ConsoleLogger.Error($"ffmpeg non trovato o non eseguibile: {options.FfmpegPath}");
            log.Error($"ffmpeg unavailable: {options.FfmpegPath}");
            return 2;
        }

        var targets = rows.Where(x => x.Decision.NeedsProcessing).ToList();
        ConsoleLogger.Info($"Elementi da processare: {targets.Count}, max job paralleli: {options.MaxConcurrentFfmpegJobs}");

        var startedAt = DateTimeOffset.Now;
        var completed = 0;
        using var semaphore = new SemaphoreSlim(options.MaxConcurrentFfmpegJobs);

        // Each ffmpeg job is independent; the semaphore keeps disk and CPU pressure under explicit user control.
        var tasks = targets.Select(async row =>
        {
            await semaphore.WaitAsync();
            try
            {
                await executor.ProcessAsync(row, options.Preset);
            }
            catch (Exception ex)
            {
                ConsoleLogger.Error($"Processing fallito per {row.FullPath}: {ex.Message}");
                log.Error($"Processing failed for {row.FullPath}: {ex}");
            }
            finally
            {
                var done = Interlocked.Increment(ref completed);
                LogProgress(done, targets.Count, startedAt);
                semaphore.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks);
        return 0;
    }

    /// <summary>
    /// Verifies all generated outputs referenced by a report using ffprobe readability checks.
    /// </summary>
    /// <param name="options">Validated command-line options for verify mode.</param>
    /// <param name="log">The file logger used to persist verification failures.</param>
    /// <returns>A process exit code for verification mode.</returns>
    private static async Task<int> RunVerifyAsync(CliOptions options, FileLogger log)
    {
        var rows = await ReadReportAsync(options.ReportPath!);
        var ffprobe = new FfprobeService(options.FfprobePath, log);
        if (!await ffprobe.IsAvailableAsync())
        {
            ConsoleLogger.Error($"ffprobe non trovato o non eseguibile: {options.FfprobePath}");
            log.Error($"ffprobe unavailable: {options.FfprobePath}");
            return 2;
        }

        var ok = 0;
        var failed = 0;
        foreach (var row in rows.Where(x => x.Decision.NeedsProcessing))
        {
            if (!File.Exists(row.Decision.OutputPath))
            {
                failed++;
                ConsoleLogger.Warn($"Output mancante: {row.Decision.OutputPath}");
                continue;
            }

            try
            {
                await ffprobe.ProbeAsync(row.Decision.OutputPath);
                ok++;
                ConsoleLogger.Success($"OK: {row.Decision.OutputPath}");
            }
            catch (Exception ex)
            {
                failed++;
                ConsoleLogger.Error($"Output non leggibile: {row.Decision.OutputPath} - {ex.Message}");
                log.Error($"Verify failed for {row.Decision.OutputPath}: {ex}");
            }
        }

        ConsoleLogger.Info($"Verifica completata. OK={ok}, falliti={failed}");
        return failed == 0 ? 0 : 3;
    }

    /// <summary>
    /// Loads a previously generated JSON report from disk.
    /// </summary>
    /// <param name="reportPath">Absolute or relative path to report.json.</param>
    /// <returns>The deserialized report rows, or an empty list if the report has no rows.</returns>
    private static async Task<List<MediaFileInfo>> ReadReportAsync(string reportPath)
    {
        await using var stream = File.OpenRead(reportPath);
        return await JsonSerializer.DeserializeAsync<List<MediaFileInfo>>(stream, ReportWriter.JsonOptions)
               ?? new List<MediaFileInfo>();
    }

    /// <summary>
    /// Combines scanner-level grouping information with detailed ffprobe metadata.
    /// </summary>
    /// <param name="scanned">The logical item discovered by the library scanner.</param>
    /// <param name="probed">The technical stream metadata returned by ffprobe.</param>
    /// <returns>A report row containing both scanner and probe metadata.</returns>
    private static MediaFileInfo MergeProbe(MediaFileInfo scanned, MediaFileInfo probed)
    {
        probed.MediaType = scanned.MediaType;
        probed.InputFiles = scanned.InputFiles.Count > 0 ? scanned.InputFiles : new List<string> { scanned.FullPath };
        probed.IsMultipart = scanned.IsMultipart;
        probed.Directory = scanned.Directory;
        probed.FileName = scanned.FileName;
        probed.FullPath = scanned.FullPath;
        probed.MovieFolderName = scanned.MovieFolderName;
        probed.SizeBytes = scanned.SizeBytes;
        probed.SizeGB = scanned.SizeGB;
        probed.EstimatedMainMovieSizeGB = scanned.EstimatedMainMovieSizeGB;
        return probed;
    }

    /// <summary>
    /// Attempts to enrich a DVD report row with ffprobe data while tolerating IFO files ffprobe cannot parse.
    /// </summary>
    /// <param name="ffprobe">The ffprobe service used for metadata extraction.</param>
    /// <param name="dvd">The DVD row produced by the DVD analyzer.</param>
    /// <param name="log">The file logger used to record best-effort failures.</param>
    /// <returns>The DVD row, enriched when possible and preserved when probing fails.</returns>
    private static async Task<MediaFileInfo> ProbeDvdBestEffortAsync(FfprobeService ffprobe, MediaFileInfo dvd, FileLogger log)
    {
        try
        {
            var probed = await ffprobe.ProbeAsync(dvd.FullPath);
            probed.MediaType = dvd.MediaType;
            probed.ProcessingStrategy = ProcessingStrategy.DvdRemux;
            probed.FullPath = dvd.FullPath;
            probed.InputFiles = dvd.InputFiles;
            probed.Directory = dvd.Directory;
            probed.FileName = dvd.FileName;
            probed.MovieFolderName = dvd.MovieFolderName;
            probed.SizeBytes = dvd.SizeBytes;
            probed.SizeGB = dvd.SizeGB;
            probed.MainTitleDetected = dvd.MainTitleDetected;
            probed.EstimatedMainMovieSizeGB = dvd.EstimatedMainMovieSizeGB;
            probed.EstimatedDuration = probed.Duration;
            return probed;
        }
        catch (Exception ex)
        {
            log.Warn($"DVD ffprobe best-effort failed for {dvd.FullPath}: {ex.Message}");
            dvd.EstimatedDuration = dvd.Duration;
            return dvd;
        }
    }

    /// <summary>
    /// Writes a compact progress line with elapsed time and a simple ETA estimate.
    /// </summary>
    /// <param name="completed">Number of completed processing jobs.</param>
    /// <param name="total">Total number of jobs scheduled for this run.</param>
    /// <param name="startedAt">Timestamp captured when processing began.</param>
    private static void LogProgress(int completed, int total, DateTimeOffset startedAt)
    {
        var elapsed = DateTimeOffset.Now - startedAt;
        var eta = completed > 0
            ? TimeSpan.FromTicks((long)(elapsed.Ticks / (double)completed * (total - completed)))
            : TimeSpan.Zero;
        ConsoleLogger.Info($"Progresso {completed}/{total} - elapsed {elapsed:hh\\:mm\\:ss} - ETA {eta:hh\\:mm\\:ss}");
    }

    /// <summary>
    /// Reports an unsupported mode to the console.
    /// </summary>
    /// <param name="mode">The unrecognized mode supplied by the user.</param>
    /// <returns>The standard command-line validation error code.</returns>
    private static int UnknownMode(string mode)
    {
        ConsoleLogger.Error($"Modalita non riconosciuta: {mode}");
        return 1;
    }
}
