namespace BVT;

/// <summary>
/// Builds display-only ffmpeg command text for DVD remux report rows.
/// </summary>
public static class DvdRemuxPlanner
{
    /// <summary>
    /// Builds a human-readable DVD remux command for the report.
    /// </summary>
    /// <param name="inputIfo">Selected DVD IFO path used as the logical DVD source marker.</param>
    /// <param name="output">Destination MKV path.</param>
    /// <returns>A display command; execution still uses ProcessStartInfo.ArgumentList.</returns>
    public static string BuildDisplayCommand(string inputIfo, string output)
    {
        _ = inputIfo;
        return $"ffmpeg -hide_banner -y -fflags +genpts+igndts -i <concat:dvd-main-title-vobs> -map 0:v:0 -map 0:a -map 0:s? -dn -c copy {Quote(output)}";
    }

    /// <summary>
    /// Quotes a path for display in the report command string.
    /// </summary>
    /// <param name="value">Path or argument to quote.</param>
    /// <returns>A quoted display value.</returns>
    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}

