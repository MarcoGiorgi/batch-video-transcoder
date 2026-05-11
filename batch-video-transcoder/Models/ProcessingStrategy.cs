namespace BVT.Models;

/// <summary>
/// Describes the action BVT should take for a media item.
/// </summary>
public enum ProcessingStrategy
{
    /// <summary>No output should be generated because the source is already compatible.</summary>
    SkipCompatible,

    /// <summary>Only the video stream should be transcoded to H.264 while audio is copied.</summary>
    LegacyTranscode,

    /// <summary>DVD streams should be copied losslessly into an MKV container.</summary>
    DvdRemux
}

