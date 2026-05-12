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
                "cleanup" => await RunCleanupAsync(options, log),
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
            ConsoleLogger.Error($"ffprobe not found or not executable: {options.FfprobePath}");
            log.Error($"ffprobe unavailable: {options.FfprobePath}");
            return 2;
        }

        Directory.CreateDirectory(options.OutputDirectory!);
        var scanner = new MediaScanner();
        var dvdDetector = new DvdVideoTsDetector();
        var dvdAnalyzer = new DvdTitleAnalyzer();
        var planner = new TranscodePlanner(options.Preset, options.RateControl, options.SizeMarginPercent);
        var rows = new List<MediaFileInfo>();

        // The scanner returns logical titles, not just files: a DVD folder and a multipart movie each become one row.
        var items = scanner.Scan(options.RootDirectory!, dvdDetector, dvdAnalyzer).ToList();
        ConsoleLogger.Info($"Found {items.Count} video/DVD titles in {options.RootDirectory}");
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
                    ConsoleLogger.Warn($"Needs processing: {media.FileName} ({media.Decision.Reason})");
                }
            }
            catch (Exception ex)
            {
                // A bad file must not prevent the rest of a large Jellyfin library from being analyzed.
                ConsoleLogger.Error($"Error on {item.FullPath}: {ex.Message}");
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
        ConsoleLogger.Success($"Compatible skips: {rows.Count(x => x.Decision.ProcessingStrategy == ProcessingStrategy.SkipCompatible)}");
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
            ConsoleLogger.Error($"ffmpeg not found or not executable: {options.FfmpegPath}");
            log.Error($"ffmpeg unavailable: {options.FfmpegPath}");
            return 2;
        }

        RefreshPendingTranscodeDecisions(rows, options);
        var processable = rows.Where(x => x.Decision.NeedsProcessing).ToList();
        var alreadyProcessed = processable.Count(x => x.Processed);
        var pending = processable.Where(x => !x.Processed).ToList();
        var targets = options.Take.HasValue ? pending.Take(options.Take.Value).ToList() : pending;
        ConsoleLogger.Info($"Processable items: {processable.Count}; already processed: {alreadyProcessed}; remaining: {pending.Count}; selected this run: {targets.Count}; max parallel jobs: {options.MaxConcurrentFfmpegJobs}");

        var startedAt = DateTimeOffset.Now;
        var completed = 0;
        using var semaphore = new SemaphoreSlim(options.MaxConcurrentFfmpegJobs);
        using var reportWriteLock = new SemaphoreSlim(1, 1);

        // Each ffmpeg job is independent; the semaphore keeps disk and CPU pressure under explicit user control.
        var tasks = targets.Select(async row =>
        {
            await semaphore.WaitAsync();
            try
            {
                await executor.ProcessAsync(row, options.Preset);
                row.Processed = File.Exists(row.Decision.OutputPath);
                row.ProcessedAt = row.Processed ? DateTimeOffset.UtcNow : row.ProcessedAt;
                row.ProcessingError = row.Processed ? string.Empty : $"Expected output was not found: {row.Decision.OutputPath}";
            }
            catch (Exception ex)
            {
                row.Processed = false;
                row.ProcessingError = ex.Message;
                ConsoleLogger.Error($"Processing failed for {row.FullPath}: {ex.Message}");
                log.Error($"Processing failed for {row.FullPath}: {ex}");
            }
            finally
            {
                await reportWriteLock.WaitAsync();
                try
                {
                    await PersistReportAsync(options.ReportPath!, rows, log);
                }
                finally
                {
                    reportWriteLock.Release();
                }

                var done = Interlocked.Increment(ref completed);
                LogProgress(done, targets.Count, startedAt);
                semaphore.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks);
        return 0;
    }

    /// <summary>
    /// Deletes original sources for rows already marked as processed, after confirming generated outputs exist and are readable.
    /// </summary>
    /// <param name="options">Validated command-line options for cleanup mode.</param>
    /// <param name="log">The file logger used to persist cleanup operations.</param>
    /// <returns>A process exit code for cleanup mode.</returns>
    private static async Task<int> RunCleanupAsync(CliOptions options, FileLogger log)
    {
        var rows = await ReadReportAsync(options.ReportPath!);
        var ffprobe = new FfprobeService(options.FfprobePath, log);
        if (!await ffprobe.IsAvailableAsync())
        {
            ConsoleLogger.Error($"ffprobe not found or not executable: {options.FfprobePath}");
            log.Error($"ffprobe unavailable: {options.FfprobePath}");
            return 2;
        }

        var candidates = rows
            .Where(x => x.Decision.NeedsProcessing)
            .Where(x => x.Processed)
            .Where(x => !x.SourceCleaned)
            .ToList();

        ConsoleLogger.Info($"Cleanup candidates: {candidates.Count}; delete enabled: {options.DeleteSources}");
        if (!options.DeleteSources)
        {
            ConsoleLogger.Warn("Dry run only. Add --delete-sources to delete originals that have processed outputs.");
        }

        var cleaned = 0;
        foreach (var row in candidates)
        {
            if (!File.Exists(row.Decision.OutputPath))
            {
                ConsoleLogger.Warn($"Skipping cleanup because output is missing: {row.Decision.OutputPath}");
                continue;
            }

            try
            {
                await ffprobe.ProbeAsync(row.Decision.OutputPath);
                if (!CanFinalizeOutputName(row, log))
                {
                    continue;
                }

                var sourceTargets = GetCleanupTargets(row);
                foreach (var target in sourceTargets)
                {
                    ConsoleLogger.Info($"{(options.DeleteSources ? "Deleting" : "Would delete")}: {target}");
                    if (options.DeleteSources)
                    {
                        DeleteSourceTarget(target);
                        log.Info($"Deleted source target: {target}");
                    }
                }

                FinalizeOutputName(row, options.DeleteSources, log);

                if (options.DeleteSources)
                {
                    row.SourceCleaned = true;
                    row.SourceCleanedAt = DateTimeOffset.UtcNow;
                    cleaned++;
                    await PersistReportAsync(options.ReportPath!, rows, log);
                }
            }
            catch (Exception ex)
            {
                ConsoleLogger.Error($"Cleanup skipped for {row.FullPath}: {ex.Message}");
                log.Error($"Cleanup skipped for {row.FullPath}: {ex}");
            }
        }

        ConsoleLogger.Success($"Cleanup completed. Cleaned sources: {cleaned}");
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
            ConsoleLogger.Error($"ffprobe not found or not executable: {options.FfprobePath}");
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
                ConsoleLogger.Warn($"Missing output: {row.Decision.OutputPath}");
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
                ConsoleLogger.Error($"Unreadable output: {row.Decision.OutputPath} - {ex.Message}");
                log.Error($"Verify failed for {row.Decision.OutputPath}: {ex}");
            }
        }

        ConsoleLogger.Info($"Verification completed. OK={ok}, failed={failed}");
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
    /// Persists updated JSON and CSV reports after processing state changes.
    /// </summary>
    /// <param name="reportPath">Path to the JSON report being updated.</param>
    /// <param name="rows">Current report rows.</param>
    /// <param name="log">Logger used by the report writer.</param>
    /// <returns>A task representing the write operation.</returns>
    private static async Task PersistReportAsync(string reportPath, IReadOnlyCollection<MediaFileInfo> rows, FileLogger log)
    {
        var writer = new ReportWriter(log);
        await writer.WriteJsonAsync(reportPath, rows);
        await writer.WriteCsvAsync(Path.ChangeExtension(reportPath, ".csv"), rows);
    }

    /// <summary>
    /// Rebuilds pending legacy decisions so older reports can use the latest rate-control settings.
    /// </summary>
    /// <param name="rows">Report rows loaded from disk.</param>
    /// <param name="options">Current command-line options.</param>
    private static void RefreshPendingTranscodeDecisions(IEnumerable<MediaFileInfo> rows, CliOptions options)
    {
        var planner = new TranscodePlanner(options.Preset, options.RateControl, options.SizeMarginPercent);
        foreach (var row in rows.Where(x => !x.Processed && x.Decision.ProcessingStrategy == ProcessingStrategy.LegacyTranscode))
        {
            row.Decision = planner.Decide(row);
            row.ProcessingStrategy = row.Decision.ProcessingStrategy;
        }
    }

    /// <summary>
    /// Calculates original file or DVD folder targets that can be deleted after successful processing.
    /// </summary>
    /// <param name="row">Processed report row.</param>
    /// <returns>Source paths eligible for cleanup.</returns>
    private static IReadOnlyList<string> GetCleanupTargets(MediaFileInfo row)
    {
        if (row.MediaType == MediaType.DVD_VIDEO_TS)
        {
            var videoTsDirectory = Path.GetDirectoryName(row.FullPath);
            return !string.IsNullOrWhiteSpace(videoTsDirectory) && Directory.Exists(videoTsDirectory)
                ? new[] { videoTsDirectory }
                : Array.Empty<string>();
        }

        return row.InputFiles
            .Where(path => !string.Equals(path, row.Decision.OutputPath, StringComparison.OrdinalIgnoreCase))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Checks whether the processed output can later be renamed to the final Jellyfin movie name.
    /// </summary>
    /// <param name="row">Processed report row whose output should be finalized.</param>
    /// <param name="log">Logger used to record conflicts.</param>
    /// <returns>True when cleanup can continue; false when a conflicting final file already exists.</returns>
    private static bool CanFinalizeOutputName(MediaFileInfo row, FileLogger log)
    {
        var currentOutput = row.Decision.OutputPath;
        var finalOutput = GetFinalOutputPath(row);
        if (string.Equals(currentOutput, finalOutput, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!File.Exists(finalOutput))
        {
            return true;
        }

        ConsoleLogger.Warn($"Skipping cleanup because final Jellyfin output already exists: {finalOutput}");
        log.Warn($"Final output already exists, cleanup skipped: {finalOutput}");
        return false;
    }

    /// <summary>
    /// Renames generated outputs to the Jellyfin movie folder name after the old source has been removed.
    /// </summary>
    /// <param name="row">Processed report row whose output should be finalized.</param>
    /// <param name="applyChanges">True to rename the file; false to print the dry-run action only.</param>
    /// <param name="log">Logger used to record rename operations.</param>
    private static void FinalizeOutputName(MediaFileInfo row, bool applyChanges, FileLogger log)
    {
        var currentOutput = row.Decision.OutputPath;
        var finalOutput = GetFinalOutputPath(row);
        if (string.Equals(currentOutput, finalOutput, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ConsoleLogger.Info($"{(applyChanges ? "Renaming" : "Would rename")}: {currentOutput} -> {finalOutput}");
        if (applyChanges)
        {
            File.Move(currentOutput, finalOutput);
            row.Decision.OutputPath = finalOutput;
            log.Info($"Renamed output for Jellyfin: {currentOutput} -> {finalOutput}");
        }
    }

    /// <summary>
    /// Builds the final Jellyfin-compatible MKV path for a processed movie folder.
    /// </summary>
    /// <param name="row">Processed report row.</param>
    /// <returns>Final MKV path named after the Jellyfin movie folder.</returns>
    private static string GetFinalOutputPath(MediaFileInfo row)
    {
        var baseName = !string.IsNullOrWhiteSpace(row.MovieFolderName)
            ? row.MovieFolderName
            : Path.GetFileNameWithoutExtension(row.FileName);
        return Path.Combine(row.Directory, $"{baseName}.mkv");
    }

    /// <summary>
    /// Deletes a source file or source directory selected by cleanup mode.
    /// </summary>
    /// <param name="target">File or directory to delete.</param>
    private static void DeleteSourceTarget(string target)
    {
        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
            return;
        }

        if (File.Exists(target))
        {
            File.Delete(target);
        }
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
        ConsoleLogger.Info($"Progress {completed}/{total} - elapsed {elapsed:hh\\:mm\\:ss} - ETA {eta:hh\\:mm\\:ss}");
    }

    /// <summary>
    /// Reports an unsupported mode to the console.
    /// </summary>
    /// <param name="mode">The unrecognized mode supplied by the user.</param>
    /// <returns>The standard command-line validation error code.</returns>
    private static int UnknownMode(string mode)
    {
        ConsoleLogger.Error($"Unknown mode: {mode}");
        return 1;
    }
}


