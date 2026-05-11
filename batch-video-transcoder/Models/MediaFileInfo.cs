namespace BVT.Models;

/// <summary>
/// Report row representing a regular video, a multipart group, or a DVD VIDEO_TS title.
/// </summary>
public sealed class MediaFileInfo
{
    /// <summary>Physical media type represented by this row.</summary>
    public MediaType MediaType { get; set; } = MediaType.File;

    /// <summary>Selected processing strategy for the row.</summary>
    public ProcessingStrategy ProcessingStrategy { get; set; } = ProcessingStrategy.SkipCompatible;

    /// <summary>Primary input path, usually the first file or the selected DVD IFO.</summary>
    public string FullPath { get; set; } = string.Empty;

    /// <summary>All source files participating in this logical item, including multipart files or DVD VOBs.</summary>
    public List<string> InputFiles { get; set; } = new();

    /// <summary>True when multiple regular video files are combined into one output.</summary>
    public bool IsMultipart { get; set; }

    /// <summary>Containing movie folder.</summary>
    public string Directory { get; set; } = string.Empty;

    /// <summary>Display file or title name used in reports.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Total source size in GiB rounded for report readability.</summary>
    public double SizeGB { get; set; }

    /// <summary>Total source size in bytes for exact CRF decisions.</summary>
    public long SizeBytes { get; set; }

    /// <summary>Container format reported by ffprobe.</summary>
    public string Container { get; set; } = string.Empty;

    /// <summary>Primary video codec name reported by ffprobe.</summary>
    public string VideoCodec { get; set; } = string.Empty;

    /// <summary>Primary video codec tag string reported by ffprobe.</summary>
    public string VideoCodecTag { get; set; } = string.Empty;

    /// <summary>Primary video width in pixels.</summary>
    public int Width { get; set; }

    /// <summary>Primary video height in pixels.</summary>
    public int Height { get; set; }

    /// <summary>Primary video frame rate.</summary>
    public string FrameRate { get; set; } = string.Empty;

    /// <summary>Media duration when known.</summary>
    public string Duration { get; set; } = string.Empty;

    /// <summary>Primary video bitrate when known.</summary>
    public string VideoBitrate { get; set; } = string.Empty;

    /// <summary>Distinct audio codecs found in the item.</summary>
    public string AudioCodecs { get; set; } = string.Empty;

    /// <summary>Distinct subtitle codecs found in the item.</summary>
    public string SubtitleCodecs { get; set; } = string.Empty;

    /// <summary>DVD title set selected as the main movie, such as VTS_01.</summary>
    public string MainTitleDetected { get; set; } = string.Empty;

    /// <summary>Best available estimated duration for DVD rows.</summary>
    public string EstimatedDuration { get; set; } = string.Empty;

    /// <summary>Estimated size of the selected DVD main movie VOB group.</summary>
    public double EstimatedMainMovieSizeGB { get; set; }

    /// <summary>Jellyfin movie folder name, normally Title (Year).</summary>
    public string MovieFolderName { get; set; } = string.Empty;

    /// <summary>Detailed streams parsed from ffprobe.</summary>
    public List<StreamInfo> Streams { get; set; } = new();

    /// <summary>Planner output containing the reason, output path, and command recommendation.</summary>
    public TranscodeDecision Decision { get; set; } = new();
}
