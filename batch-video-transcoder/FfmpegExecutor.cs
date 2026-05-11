using System.Diagnostics;
using BVT.Models;

namespace BVT;

/// <summary>
/// Executes ffmpeg operations for legacy transcodes and DVD remux jobs without using a shell.
/// </summary>
public sealed class FfmpegExecutor
{
    private readonly string _ffmpegPath;
    private readonly FileLogger _log;

    /// <summary>
    /// Creates an executor for a specific ffmpeg binary.
    /// </summary>
    /// <param name="ffmpegPath">Path or executable name for ffmpeg.</param>
    /// <param name="log">Logger used to record ffmpeg operations and stderr failures.</param>
    public FfmpegExecutor(string ffmpegPath, FileLogger log)
    {
        _ffmpegPath = ffmpegPath;
        _log = log;
    }

    /// <summary>
    /// Checks whether ffmpeg can be started successfully.
    /// </summary>
    /// <returns>True when ffmpeg responds to -version.</returns>
    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            var result = await RunAsync(["-version"]);
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Executes the processing strategy stored in a report row.
    /// </summary>
    /// <param name="media">Report row to process.</param>
    /// <param name="preset">x264 preset for legacy video transcodes.</param>
    /// <param name="cancellationToken">Cancellation token for the ffmpeg process.</param>
    /// <returns>A task representing the ffmpeg operation.</returns>
    public async Task ProcessAsync(MediaFileInfo media, string preset, CancellationToken cancellationToken = default)
    {
        switch (media.Decision.ProcessingStrategy)
        {
            case ProcessingStrategy.LegacyTranscode:
                await TranscodeAsync(media, preset, cancellationToken);
                break;
            case ProcessingStrategy.DvdRemux:
                await RemuxDvdAsync(media, cancellationToken);
                break;
            case ProcessingStrategy.SkipCompatible:
                ConsoleLogger.Info($"Compatible skip: {media.FullPath}");
                break;
        }
    }

    private async Task TranscodeAsync(MediaFileInfo media, string preset, CancellationToken cancellationToken)
    {
        var output = media.Decision.OutputPath;
        if (File.Exists(output))
        {
            ConsoleLogger.Warn($"Resume: skipping existing output: {output}");
            _log.Warn($"Skipping existing output: {output}");
            return;
        }

        ConsoleLogger.Info($"Transcode legacy: {media.FullPath}");
        _log.Info($"Starting transcode: {media.FullPath} -> {output}");

        var concatList = media.IsMultipart ? await CreateConcatListAsync(media.InputFiles) : null;
        try
        {
            var args = BuildTranscodeArguments(media, output, preset, media.Decision.RecommendedCrf, includeSubtitles: true, concatList);
            var result = await RunAsync(args, cancellationToken);
            if (result.ExitCode == 0)
            {
                ConsoleLogger.Success($"Created: {output}");
                _log.Info($"Transcode completed: {output}");
                return;
            }

            ConsoleLogger.Warn("ffmpeg failed while copying subtitles; retrying with -sn.");
            _log.Warn($"ffmpeg subtitle-copy failure for {media.FullPath}: {result.Error}");
            DeletePartialOutput(output);

            var retryArgs = BuildTranscodeArguments(media, output, preset, media.Decision.RecommendedCrf, includeSubtitles: false, concatList);
            var retry = await RunAsync(retryArgs, cancellationToken);
            if (retry.ExitCode != 0)
            {
                throw new InvalidOperationException(retry.Error.Trim());
            }

            ConsoleLogger.Success($"Created without subtitles: {output}");
            _log.Info($"Transcode completed without subtitles: {output}");
        }
        finally
        {
            DeleteTempList(concatList);
        }
    }

    private async Task RemuxDvdAsync(MediaFileInfo media, CancellationToken cancellationToken)
    {
        var output = media.Decision.OutputPath;
        if (File.Exists(output))
        {
            ConsoleLogger.Warn($"Resume: skipping existing output: {output}");
            _log.Warn($"Skipping existing DVD remux output: {output}");
            return;
        }

        ConsoleLogger.Info($"DVD remux lossless: {media.FullPath}");
        _log.Info($"Starting DVD remux: {media.FullPath} -> {output}");

        // DVD VOBs can carry discontinuous timestamps. The first attempt uses the concat protocol,
        // which treats the selected VOB chain like one MPEG program stream and usually produces better seekability.
        var concatList = media.InputFiles.Count > 0 ? await CreateConcatListAsync(media.InputFiles) : null;
        try
        {
            // Fallbacks progressively reduce stream complexity while keeping video/audio copy-only.
            var attempts = new[]
            {
                BuildDvdRemuxArguments(media.FullPath, output, DvdRemuxAttempt.AllStreams, concatList, media.InputFiles, DvdInputMode.ConcatProtocol),
                BuildDvdRemuxArguments(media.FullPath, output, DvdRemuxAttempt.AllStreams, concatList, media.InputFiles, DvdInputMode.ConcatDemuxer),
                BuildDvdRemuxArguments(media.FullPath, output, DvdRemuxAttempt.NoSubtitles, concatList, media.InputFiles, DvdInputMode.ConcatProtocol),
                BuildDvdRemuxArguments(media.FullPath, output, DvdRemuxAttempt.MainVideoFirstAudio, concatList, media.InputFiles, DvdInputMode.ConcatProtocol)
            };

            for (var i = 0; i < attempts.Length; i++)
            {
                DeletePartialOutput(output);
                var result = await RunAsync(attempts[i], cancellationToken);
                if (result.ExitCode == 0)
                {
                    var suffix = i switch
                    {
                        0 => "concat protocol, compatible tracks",
                        1 => "concat demuxer, compatible tracks",
                        2 => "without subtitles",
                        _ => "main video and first audio track"
                    };
                    ConsoleLogger.Success($"DVD remux created ({suffix}): {output}");
                    _log.Info($"DVD remux completed attempt {i + 1}: {output}");
                    return;
                }

                _log.Warn($"DVD remux attempt {i + 1} failed for {media.FullPath}: {result.Error}");
                ConsoleLogger.Warn(i switch
                {
                    0 => "DVD remux failed with concat protocol; retrying with concat demuxer.",
                    1 => "DVD remux failed with all compatible tracks; retrying without subtitles.",
                    2 => "DVD remux failed without subtitles; retrying with main video and first audio track.",
                    _ => "DVD remux failed."
                });
            }

            throw new InvalidOperationException($"DVD remux failed for {media.FullPath}");
        }
        finally
        {
            DeleteTempList(concatList);
        }
    }

    private static string[] BuildTranscodeArguments(MediaFileInfo media, string output, string preset, int crf, bool includeSubtitles, string? concatList)
    {
        var args = new List<string> { "-hide_banner", "-y" };

        if (concatList is not null)
        {
            args.AddRange(["-f", "concat", "-safe", "0", "-i", concatList]);
        }
        else
        {
            args.AddRange(["-i", media.FullPath]);
        }

        args.AddRange(["-map", "0", "-c:v", "libx264", "-preset", preset, "-crf", crf.ToString(), "-c:a", "copy"]);

        if (includeSubtitles)
        {
            args.AddRange(["-c:s", "copy"]);
        }
        else
        {
            args.Add("-sn");
        }

        args.Add(output);
        return args.ToArray();
    }

    private static string[] BuildDvdRemuxArguments(string inputIfo, string output, DvdRemuxAttempt attempt, string? concatList, IReadOnlyList<string> inputFiles, DvdInputMode inputMode)
    {
        // genpts/igndts asks ffmpeg to repair problematic DVD timestamps instead of preserving broken DTS values.
        var args = new List<string> { "-hide_banner", "-y", "-fflags", "+genpts+igndts", "-err_detect", "ignore_err" };
        if (inputMode == DvdInputMode.ConcatProtocol && inputFiles.Count > 0)
        {
            args.AddRange(["-i", BuildConcatProtocolInput(inputFiles)]);
        }
        else if (concatList is not null)
        {
            args.AddRange(["-f", "concat", "-safe", "0", "-i", concatList]);
        }
        else
        {
            args.AddRange(["-i", inputIfo]);
        }

        switch (attempt)
        {
            case DvdRemuxAttempt.AllStreams:
                args.AddRange(["-map", "0:v:0", "-map", "0:a", "-map", "0:s?", "-dn", "-c", "copy"]);
                break;
            case DvdRemuxAttempt.NoSubtitles:
                args.AddRange(["-map", "0:v:0", "-map", "0:a", "-dn", "-sn", "-c", "copy"]);
                break;
            case DvdRemuxAttempt.MainVideoFirstAudio:
                args.AddRange(["-map", "0:v:0", "-map", "0:a:0", "-dn", "-sn", "-c", "copy"]);
                break;
        }

        args.AddRange(["-avoid_negative_ts", "make_zero", "-max_interleave_delta", "0", "-muxdelay", "0", "-muxpreload", "0", output]);
        return args.ToArray();
    }

    private static string BuildConcatProtocolInput(IReadOnlyList<string> inputFiles)
    {
        var urls = inputFiles.Select(file => Path.GetFullPath(file).Replace('\\', '/'));
        return "concat:" + string.Join("|", urls);
    }

    private static async Task<string> CreateConcatListAsync(IReadOnlyList<string> inputFiles)
    {
        var path = Path.Combine(Path.GetTempPath(), $"jellyfin-video-analyzer-{Guid.NewGuid():N}.ffconcat");
        var lines = inputFiles.Select(file =>
        {
            var normalized = Path.GetFullPath(file).Replace('\\', '/');
            return $"file '{normalized.Replace("'", "'\\''", StringComparison.Ordinal)}'";
        });
        await File.WriteAllLinesAsync(path, lines);
        return path;
    }

    private static void DeleteTempList(string? concatList)
    {
        if (concatList is not null && File.Exists(concatList))
        {
            File.Delete(concatList);
        }
    }

    private static void DeletePartialOutput(string output)
    {
        if (File.Exists(output))
        {
            File.Delete(output);
        }
    }

    private async Task<ProcessResult> RunAsync(IEnumerable<string> arguments, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Impossibile avviare ffmpeg.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
    }

    private enum DvdRemuxAttempt
    {
        AllStreams,
        NoSubtitles,
        MainVideoFirstAudio
    }

    private enum DvdInputMode
    {
        ConcatProtocol,
        ConcatDemuxer
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}





