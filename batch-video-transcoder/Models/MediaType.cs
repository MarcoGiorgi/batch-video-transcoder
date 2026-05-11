namespace BVT.Models;

/// <summary>
/// Describes the physical source type represented by a report row.
/// </summary>
public enum MediaType
{
    /// <summary>A regular video file or a logical group of multipart video files.</summary>
    File,

    /// <summary>A ripped DVD folder containing a VIDEO_TS directory.</summary>
    DVD_VIDEO_TS
}
