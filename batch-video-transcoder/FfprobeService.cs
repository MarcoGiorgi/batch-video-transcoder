using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using BVT.Models;

namespace BVT;

/// <summary>
/// Wraps ffprobe JSON execution and maps stream metadata into BVT model objects.
/// </summary>
public sealed class FfprobeService
{
    private readonly string _ffprobePath;
    private readonly FileLogger _log;

    /// <summary>
    /// Creates an ffprobe service for a configured binary path.
    /// </summary>
    /// <param name="ffprobePath">Path or executable name for ffprobe.</param>
    /// <param name="log">Logger used for subtitle warnings and probe diagnostics.</param>
    public FfprobeService(string ffprobePath, FileLogger log)
    {
        _ffprobePath = ffprobePath;
        _log = log;
    }

    /// <summary>
    /// Checks whether ffprobe can be started successfully.
    /// </summary>
    /// <returns>True when ffprobe responds to -version.</returns>
    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            var result = await RunAsync("-version");
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Runs ffprobe on a media path and returns normalized metadata for reporting.
    /// </summary>
    /// <param name="path">Media file, DVD IFO, or generated output to inspect.</param>
    /// <returns>Parsed media metadata.</returns>
    public async Task<MediaFileInfo> ProbeAsync(string path)
    {
        var result = await RunAsync(null, path);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(result.Error.Trim());
        }

        using var document = JsonDocument.Parse(result.Output);
        var root = document.RootElement;
        var format = root.GetProperty("format");
        var streams = root.GetProperty("streams");
        var fileInfo = new FileInfo(path);

        // Streams are parsed by type so the report can summarize codecs while retaining details.
        var videoStreams = ParseStreams(streams, "video").ToList();
        var audioStreams = ParseStreams(streams, "audio").ToList();
        var subtitleStreams = ParseStreams(streams, "subtitle").ToList();
        var primaryVideo = videoStreams.FirstOrDefault();

        var media = new MediaFileInfo
        {
            FullPath = path,
            Directory = Path.GetDirectoryName(path) ?? string.Empty,
            FileName = Path.GetFileName(path),
            SizeGB = Math.Round(fileInfo.Length / 1024d / 1024d / 1024d, 3),
            SizeBytes = fileInfo.Length,
            Container = GetString(format, "format_name"),
            Duration = TimeSpan.FromSeconds(ParseDouble(GetString(format, "duration"))).ToString(@"hh\:mm\:ss"),
            VideoCodec = primaryVideo?.CodecName ?? string.Empty,
            VideoCodecTag = primaryVideo?.CodecTagString ?? string.Empty,
            Width = primaryVideo?.Width ?? 0,
            Height = primaryVideo?.Height ?? 0,
            FrameRate = primaryVideo?.FrameRate ?? string.Empty,
            VideoBitrate = primaryVideo?.BitRate ?? GetString(format, "bit_rate"),
            AudioCodecs = string.Join(";", audioStreams.Select(x => x.CodecName).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase)),
            SubtitleCodecs = string.Join(";", subtitleStreams.Select(x => x.CodecName).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase)),
            Streams = videoStreams.Concat(audioStreams).Concat(subtitleStreams).ToList()
        };

        LogSubtitleWarnings(media);
        return media;
    }

    private async Task<ProcessResult> RunAsync(string? singleArgument, string? mediaPath = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _ffprobePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        if (singleArgument is not null)
        {
            startInfo.ArgumentList.Add(singleArgument);
        }
        else
        {
            startInfo.ArgumentList.Add("-v");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-print_format");
            startInfo.ArgumentList.Add("json");
            startInfo.ArgumentList.Add("-show_format");
            startInfo.ArgumentList.Add("-show_streams");
            startInfo.ArgumentList.Add(mediaPath!);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Impossibile avviare ffprobe.");
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, output, error);
    }

    private static IEnumerable<StreamInfo> ParseStreams(JsonElement streams, string type)
    {
        foreach (var stream in streams.EnumerateArray())
        {
            if (!string.Equals(GetString(stream, "codec_type"), type, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return new StreamInfo
            {
                Index = GetInt(stream, "index"),
                CodecType = type,
                CodecName = GetString(stream, "codec_name"),
                CodecTagString = GetString(stream, "codec_tag_string"),
                Width = GetInt(stream, "width"),
                Height = GetInt(stream, "height"),
                FrameRate = NormalizeFrameRate(GetString(stream, "r_frame_rate")),
                Duration = GetString(stream, "duration"),
                BitRate = GetString(stream, "bit_rate")
            };
        }
    }

    private void LogSubtitleWarnings(MediaFileInfo media)
    {
        var risky = media.Streams
            .Where(x => x.CodecType == "subtitle")
            .Where(x => x.CodecName is "dvd_subtitle" or "hdmv_pgs_subtitle" or "xsub")
            .Select(x => x.CodecName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (risky.Count > 0)
        {
            _log.Warn($"Subtitle warning for {media.FullPath}: bitmap/non-text subtitles may fail copy to MKV ({string.Join(", ", risky)}).");
        }
    }

    private static string GetString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) ? value.ToString() : string.Empty;
    }

    private static int GetInt(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.TryGetInt32(out var parsed) ? parsed : 0;
    }

    private static double ParseDouble(string value)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    private static string NormalizeFrameRate(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "0/0")
        {
            return string.Empty;
        }

        var parts = value.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) &&
            denominator != 0)
        {
            return Math.Round(numerator / denominator, 3).ToString(CultureInfo.InvariantCulture);
        }

        return value;
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}



