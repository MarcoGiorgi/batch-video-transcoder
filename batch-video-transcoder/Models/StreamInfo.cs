namespace BVT.Models;

/// <summary>
/// Technical stream information extracted from ffprobe for video, audio, and subtitle streams.
/// </summary>
public sealed class StreamInfo
{
    /// <summary>Zero-based stream index reported by ffprobe.</summary>
    public int Index { get; set; }

    /// <summary>Stream type, such as video, audio, or subtitle.</summary>
    public string CodecType { get; set; } = string.Empty;

    /// <summary>Codec name reported by ffprobe, for example h264, mpeg2video, ac3, or subrip.</summary>
    public string CodecName { get; set; } = string.Empty;

    /// <summary>Container codec tag string, useful for detecting DivX/Xvid variants.</summary>
    public string CodecTagString { get; set; } = string.Empty;

    /// <summary>Video width in pixels, or 0 for non-video streams.</summary>
    public int Width { get; set; }

    /// <summary>Video height in pixels, or 0 for non-video streams.</summary>
    public int Height { get; set; }

    /// <summary>Normalized frame rate for video streams.</summary>
    public string FrameRate { get; set; } = string.Empty;

    /// <summary>Stream duration when ffprobe exposes one.</summary>
    public string Duration { get; set; } = string.Empty;

    /// <summary>Stream bitrate when ffprobe exposes one.</summary>
    public string BitRate { get; set; } = string.Empty;
}
