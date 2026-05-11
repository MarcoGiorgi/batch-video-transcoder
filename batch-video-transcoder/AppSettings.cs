namespace BVT;

/// <summary>
/// Strongly typed configuration loaded from appsettings.json.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Default x264 preset used when the CLI does not provide --preset.</summary>
    public string Preset { get; set; } = "medium";

    /// <summary>Path or executable name for ffprobe.</summary>
    public string FfprobePath { get; set; } = "ffprobe";

    /// <summary>Path or executable name for ffmpeg.</summary>
    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>Directory where BVT writes operation logs.</summary>
    public string LogDirectory { get; set; } = "logs";

    /// <summary>Maximum number of ffmpeg jobs allowed to run at the same time.</summary>
    public int MaxConcurrentFfmpegJobs { get; set; } = 1;

    /// <summary>
    /// Loads settings from disk, falling back to defaults when the file is absent or malformed.
    /// </summary>
    /// <param name="path">Path to appsettings.json.</param>
    /// <returns>The loaded settings or default settings.</returns>
    public static AppSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(path);
            return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, ReportWriter.JsonOptions) ?? new AppSettings();
        }
        catch
        {
            // Configuration should never make the tool unusable; defaults keep the CLI predictable.
            return new AppSettings();
        }
    }
}

