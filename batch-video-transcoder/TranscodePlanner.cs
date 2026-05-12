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
    private readonly string _rateControl;
    private readonly int _sizeMarginPercent;

    /// <summary>
    /// Creates a planner with the desired x264 preset for legacy transcodes.
    /// </summary>
    /// <param name="preset">x264 preset to include in generated commands.</param>
    /// <param name="rateControl">Rate control mode for legacy transcodes: source-bitrate or crf.</param>
    /// <param name="sizeMarginPercent">Percentage added to source video bitrate in source-bitrate mode.</param>
    public TranscodePlanner(string preset, string rateControl, int sizeMarginPercent)
    {
        _preset = preset;
        _rateControl = rateControl;
        _sizeMarginPercent = sizeMarginPercent;
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
                Reason = $"DVD VIDEO_TS detected, main title {media.MainTitleDetected}: remux lossless in MKV",
                RecommendedCrf = 0,
                OutputPath = dvdOutputPath,
                FfmpegCommand = DvdRemuxPlanner.BuildDisplayCommand(media.FullPath, dvdOutputPath)
            };
        }

        var crf = media.SizeBytes < 1024L * 1024L * 1024L ? 18 : 16;
        var targetVideoBitrateKbps = _rateControl == "source-bitrate"
            ? CalculateTargetVideoBitrateKbps(media, _sizeMarginPercent)
            : 0;
        var outputBaseName = media.IsMultipart
            ? media.MovieFolderName
            : Path.GetFileNameWithoutExtension(media.FileName);
        var outputPath = Path.Combine(media.Directory, $"{outputBaseName}.converted.mkv");
        var command = BuildDisplayCommand(media.FullPath, outputPath, _preset, crf, targetVideoBitrateKbps, includeSubtitles: true, media.IsMultipart);

        if (ModernCodecs.Contains(media.VideoCodec))
        {
            return new TranscodeDecision
            {
                NeedsTranscode = false,
                NeedsProcessing = false,
                ProcessingStrategy = ProcessingStrategy.SkipCompatible,
                Reason = $"Modern/supported codec: {media.VideoCodec}",
                RecommendedCrf = crf,
                RecommendedVideoBitrateKbps = targetVideoBitrateKbps,
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
                ? $"Legacy/problematic video codec: {media.VideoCodec}"
                : $"Legacy/problematic codec tag: {media.VideoCodecTag}";

            if (media.IsMultipart)
            {
                reason += "; multipart detected, parts will be combined into a single MKV";
            }

            if (targetVideoBitrateKbps > 0)
            {
                reason += $"; target video bitrate {targetVideoBitrateKbps} Kbps ({_sizeMarginPercent}% source margin)";
            }

            return new TranscodeDecision
            {
                NeedsTranscode = true,
                NeedsProcessing = true,
                ProcessingStrategy = ProcessingStrategy.LegacyTranscode,
                Reason = reason,
                RecommendedCrf = crf,
                RecommendedVideoBitrateKbps = targetVideoBitrateKbps,
                OutputPath = outputPath,
                FfmpegCommand = command
            };
        }

        return new TranscodeDecision
        {
            NeedsTranscode = false,
            NeedsProcessing = false,
            ProcessingStrategy = ProcessingStrategy.SkipCompatible,
            Reason = $"Codec not included in legacy rules: {media.VideoCodec}",
            RecommendedCrf = crf,
            RecommendedVideoBitrateKbps = targetVideoBitrateKbps,
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
    /// <param name="targetVideoBitrateKbps">Recommended target video bitrate in Kbps, or 0 to show CRF.</param>
    /// <param name="includeSubtitles">Whether the display command should copy subtitle streams.</param>
    /// <param name="isMultipart">Whether the input is represented by a concat list.</param>
    /// <returns>A report command string intended for review, not shell execution.</returns>
    public static string BuildDisplayCommand(string input, string output, string preset, int crf, int targetVideoBitrateKbps, bool includeSubtitles, bool isMultipart = false)
    {
        var subtitleArgs = includeSubtitles ? "-c:s copy" : "-sn";
        var inputArgs = isMultipart ? "-f concat -safe 0 -i <concat-list.txt>" : $"-i {Quote(input)}";
        var rateArgs = targetVideoBitrateKbps > 0
            ? $"-b:v {targetVideoBitrateKbps}K -maxrate {CalculateMaxRateKbps(targetVideoBitrateKbps)}K -bufsize {CalculateBufferSizeKbps(targetVideoBitrateKbps)}K"
            : $"-crf {crf}";
        return $"ffmpeg -hide_banner -y {inputArgs} -map 0 -c:v libx264 -preset {preset} {rateArgs} -c:a copy {subtitleArgs} {Quote(output)}";
    }

    /// <summary>
    /// Calculates a target video bitrate from ffprobe metadata while keeping a small user-selected safety margin.
    /// </summary>
    /// <param name="media">Media row containing bitrate, duration, and size information.</param>
    /// <param name="sizeMarginPercent">Percentage margin added to the source video bitrate.</param>
    /// <returns>Target video bitrate in Kbps, or 0 when the source bitrate cannot be estimated safely.</returns>
    private static int CalculateTargetVideoBitrateKbps(MediaFileInfo media, int sizeMarginPercent)
    {
        var sourceBitsPerSecond = ParseLong(media.VideoBitrate);
        if (sourceBitsPerSecond <= 0 && TryParseDuration(media.Duration, out var duration) && duration.TotalSeconds > 0)
        {
            // Fallback to total average bitrate when stream-level video bitrate is missing.
            sourceBitsPerSecond = (long)Math.Round(media.SizeBytes * 8d / duration.TotalSeconds);
        }

        if (sourceBitsPerSecond <= 0)
        {
            return 0;
        }

        var withMargin = sourceBitsPerSecond * (1d + sizeMarginPercent / 100d);
        return Math.Max(300, (int)Math.Ceiling(withMargin / 1000d));
    }

    /// <summary>
    /// Parses an integer value from ffprobe string fields.
    /// </summary>
    /// <param name="value">Potential numeric value.</param>
    /// <returns>The parsed integer, or 0 when parsing fails.</returns>
    private static long ParseLong(string value)
    {
        return long.TryParse(value, out var parsed) ? parsed : 0;
    }

    /// <summary>
    /// Parses the duration format emitted into the report by ffprobe.
    /// </summary>
    /// <param name="value">Duration text.</param>
    /// <param name="duration">Parsed duration when the method succeeds.</param>
    /// <returns>True when the duration was parsed successfully.</returns>
    private static bool TryParseDuration(string value, out TimeSpan duration)
    {
        if (TimeSpan.TryParse(value, out duration))
        {
            return true;
        }

        if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
        {
            duration = TimeSpan.FromSeconds(seconds);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Calculates a gentle VBV maximum bitrate for single-pass source-bitrate mode.
    /// </summary>
    /// <param name="targetVideoBitrateKbps">Target video bitrate in Kbps.</param>
    /// <returns>Maximum bitrate in Kbps.</returns>
    public static int CalculateMaxRateKbps(int targetVideoBitrateKbps)
    {
        return (int)Math.Ceiling(targetVideoBitrateKbps * 1.15d);
    }

    /// <summary>
    /// Calculates the VBV buffer size used by source-bitrate mode.
    /// </summary>
    /// <param name="targetVideoBitrateKbps">Target video bitrate in Kbps.</param>
    /// <returns>Buffer size in Kbps.</returns>
    public static int CalculateBufferSizeKbps(int targetVideoBitrateKbps)
    {
        return targetVideoBitrateKbps * 2;
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

