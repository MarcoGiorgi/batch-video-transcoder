using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BVT.Models;

namespace BVT;

/// <summary>
/// Writes equivalent JSON and CSV reports for analyzed media rows.
/// </summary>
public sealed class ReportWriter
{
    /// <summary>
    /// Shared JSON options used for report writing and report reading.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly FileLogger _log;

    /// <summary>
    /// Creates a report writer using the supplied logger.
    /// </summary>
    /// <param name="log">Logger used to record report output paths.</param>
    public ReportWriter(FileLogger log)
    {
        _log = log;
    }

    /// <summary>
    /// Writes the full report as indented JSON.
    /// </summary>
    /// <param name="path">Destination report.json path.</param>
    /// <param name="rows">Rows to serialize.</param>
    /// <returns>A task representing the file write.</returns>
    public async Task WriteJsonAsync(string path, IReadOnlyCollection<MediaFileInfo> rows)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, rows, JsonOptions);
        _log.Info($"Wrote JSON report: {path}");
    }

    /// <summary>
    /// Writes the report as CSV using RFC-style quote escaping for every value.
    /// </summary>
    /// <param name="path">Destination report.csv path.</param>
    /// <param name="rows">Rows to serialize.</param>
    /// <returns>A task representing the file write.</returns>
    public async Task WriteCsvAsync(string path, IReadOnlyCollection<MediaFileInfo> rows)
    {
        // Every field is quoted so paths with commas, quotes, accents, and apostrophes remain valid CSV.
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(",", Columns));

        foreach (var row in rows)
        {
            var values = new[]
            {
                row.MediaType.ToString(),
                row.ProcessingStrategy.ToString(),
                row.FullPath,
                string.Join(";", row.InputFiles),
                row.IsMultipart.ToString(),
                row.Directory,
                row.FileName,
                row.SizeGB.ToString(CultureInfo.InvariantCulture),
                row.Container,
                row.VideoCodec,
                row.VideoCodecTag,
                row.Width.ToString(CultureInfo.InvariantCulture),
                row.Height.ToString(CultureInfo.InvariantCulture),
                row.FrameRate,
                row.Duration,
                row.VideoBitrate,
                row.AudioCodecs,
                row.SubtitleCodecs,
                row.MainTitleDetected,
                row.EstimatedDuration,
                row.EstimatedMainMovieSizeGB.ToString(CultureInfo.InvariantCulture),
                row.Decision.NeedsTranscode.ToString(),
                row.Decision.NeedsProcessing.ToString(),
                row.Decision.Reason,
                row.Decision.RecommendedCrf.ToString(CultureInfo.InvariantCulture),
                row.Decision.OutputPath,
                row.Decision.FfmpegCommand,
                row.Processed.ToString(),
                row.ProcessedAt?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
                row.ProcessingError,
                row.SourceCleaned.ToString(),
                row.SourceCleanedAt?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty
            };

            builder.AppendLine(string.Join(",", values.Select(Escape)));
        }

        await File.WriteAllTextAsync(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        _log.Info($"Wrote CSV report: {path}");
    }

    private static string Escape(string? value)
    {
        value ??= string.Empty;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static readonly string[] Columns =
    {
        "MediaType",
        "ProcessingStrategy",
        "FullPath",
        "InputFiles",
        "IsMultipart",
        "Directory",
        "FileName",
        "SizeGB",
        "Container",
        "VideoCodec",
        "VideoCodecTag",
        "Width",
        "Height",
        "FrameRate",
        "Duration",
        "VideoBitrate",
        "AudioCodecs",
        "SubtitleCodecs",
        "MainTitleDetected",
        "EstimatedDuration",
        "EstimatedMainMovieSizeGB",
        "NeedsTranscode",
        "NeedsProcessing",
        "Reason",
        "RecommendedCrf",
        "OutputPath",
        "FfmpegCommand",
        "Processed",
        "ProcessedAt",
        "ProcessingError",
        "SourceCleaned",
        "SourceCleanedAt"
    };
}



