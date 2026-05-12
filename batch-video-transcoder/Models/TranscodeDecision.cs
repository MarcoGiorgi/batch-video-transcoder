namespace BVT.Models;

/// <summary>
/// Processing decision and recommended ffmpeg command generated for one report row.
/// </summary>
public sealed class TranscodeDecision
{
    /// <summary>True only when video transcoding is required.</summary>
    public bool NeedsTranscode { get; set; }

    /// <summary>True when any output action is required, including DVD remux.</summary>
    public bool NeedsProcessing { get; set; }

    /// <summary>The selected processing strategy for this item.</summary>
    public ProcessingStrategy ProcessingStrategy { get; set; } = ProcessingStrategy.SkipCompatible;

    /// <summary>Human-readable explanation for the selected strategy.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Recommended libx264 CRF for legacy transcodes; 0 for remux-only rows.</summary>
    public int RecommendedCrf { get; set; }

    /// <summary>Recommended target video bitrate in Kbps for source-bitrate transcodes; 0 when CRF is used.</summary>
    public int RecommendedVideoBitrateKbps { get; set; }

    /// <summary>Destination path for the generated MKV output.</summary>
    public string OutputPath { get; set; } = string.Empty;

    /// <summary>Display-only ffmpeg command for review; execution uses ArgumentList instead.</summary>
    public string FfmpegCommand { get; set; } = string.Empty;
}

