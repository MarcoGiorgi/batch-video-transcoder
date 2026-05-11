using BVT.Models;

namespace BVT;

/// <summary>
/// Applies codec and DVD rules to decide whether a media item should be skipped, transcoded, or remuxed.
/// </summary>
public sealed class TranscodePlanner
{
    private static readonly HashSet<string> ModernCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "h264", "hevc", "av1", "vp9"
    };

    private static readonly HashSet<string> LegacyCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "mpeg4", "msmpeg4v3", "mpeg2video", "wmv1", "wmv2", "wmv3", "flv1", "rv40",
        "indeo", "cinepak", "h263", "divx"
    };

    private static readonly HashSet<string> LegacyTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "DX50", "DIVX", "XVID"
    };

    private readonly string _preset;

    /// <summary>
    /// Creates a planner with the desired x264 preset for legacy transcodes.
    /// </summary>
    /// <param name="preset">x264 preset to include in generated commands.</param>
    public TranscodePlanner(string preset)
    {
        _preset = preset;
    }

    /// <summary>
    /// Decides the processing strategy for one media item and builds its recommended command.
    /// </summary>
    /// <param name="media">Media metadata collected by the scanner and ffprobe.</param>
    /// <returns>The decision for the supplied media item.</returns>
    public TranscodeDecision Decide(MediaFileInfo media)
    {
        if (media.MediaType == MediaType.DVD_VIDEO_TS)
        {
            var dvdOutputPath = Path.Combine(media.Directory, $"{media.MovieFolderName}.dvdremux.mkv");
            return new TranscodeDecision
            {
                NeedsTranscode = false,
                NeedsProcessing = true,
                ProcessingStrategy = ProcessingStrategy.DvdRemux,
                Reason = $"DVD VIDEO_TS rilevato, main title {media.MainTitleDetected}: remux lossless in MKV",
                RecommendedCrf = 0,
                OutputPath = dvdOutputPath,
                FfmpegCommand = DvdRemuxPlanner.BuildDisplayCommand(media.FullPath, dvdOutputPath)
            };
        }

        var crf = media.SizeBytes < 1024L * 1024L * 1024L ? 18 : 16;
        var outputBaseName = media.IsMultipart
            ? media.MovieFolderName
            : Path.GetFileNameWithoutExtension(media.FileName);
        var outputPath = Path.Combine(media.Directory, $"{outputBaseName}.converted.mkv");
        var command = BuildDisplayCommand(media.FullPath, outputPath, _preset, crf, includeSubtitles: true, media.IsMultipart);

        if (ModernCodecs.Contains(media.VideoCodec))
        {
            return new TranscodeDecision
            {
                NeedsTranscode = false,
                NeedsProcessing = false,
                ProcessingStrategy = ProcessingStrategy.SkipCompatible,
                Reason = $"Codec moderno/supportato: {media.VideoCodec}",
                RecommendedCrf = crf,
                OutputPath = outputPath,
                FfmpegCommand = command
            };
        }

        // Xvid/DivX are often reported as mpeg4 with only codec_tag_string distinguishing them.
        var tagIsLegacy = LegacyTags.Contains(media.VideoCodecTag);
        var codecIsLegacy = LegacyCodecs.Contains(media.VideoCodec);
        if (codecIsLegacy || tagIsLegacy)
        {
            var reason = codecIsLegacy
                ? $"Codec video legacy/problematico: {media.VideoCodec}"
                : $"Codec tag legacy/problematico: {media.VideoCodecTag}";

            if (media.IsMultipart)
            {
                reason += "; multipart rilevato, le parti verranno combinate in un unico MKV";
            }

            return new TranscodeDecision
            {
                NeedsTranscode = true,
                NeedsProcessing = true,
                ProcessingStrategy = ProcessingStrategy.LegacyTranscode,
                Reason = reason,
                RecommendedCrf = crf,
                OutputPath = outputPath,
                FfmpegCommand = command
            };
        }

        return new TranscodeDecision
        {
            NeedsTranscode = false,
            NeedsProcessing = false,
            ProcessingStrategy = ProcessingStrategy.SkipCompatible,
            Reason = $"Codec non incluso nelle regole legacy: {media.VideoCodec}",
            RecommendedCrf = crf,
            OutputPath = outputPath,
            FfmpegCommand = command
        };
    }

    /// <summary>
    /// Builds a display-only ffmpeg command for legacy video transcodes.
    /// </summary>
    /// <param name="input">Input file path shown in the command.</param>
    /// <param name="output">Output MKV path shown in the command.</param>
    /// <param name="preset">x264 preset to show.</param>
    /// <param name="crf">Recommended CRF value.</param>
    /// <param name="includeSubtitles">Whether the display command should copy subtitle streams.</param>
    /// <param name="isMultipart">Whether the input is represented by a concat list.</param>
    /// <returns>A report command string intended for review, not shell execution.</returns>
    public static string BuildDisplayCommand(string input, string output, string preset, int crf, bool includeSubtitles, bool isMultipart = false)
    {
        var subtitleArgs = includeSubtitles ? "-c:s copy" : "-sn";
        var inputArgs = isMultipart ? "-f concat -safe 0 -i <concat-list.txt>" : $"-i {Quote(input)}";
        return $"ffmpeg -hide_banner -y {inputArgs} -map 0 -c:v libx264 -preset {preset} -crf {crf} -c:a copy {subtitleArgs} {Quote(output)}";
    }

    /// <summary>
    /// Quotes a path for safe display in the generated report command.
    /// </summary>
    /// <param name="value">Value to quote.</param>
    /// <returns>A quoted display value.</returns>
    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}
