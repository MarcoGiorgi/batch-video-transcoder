namespace BVT;

/// <summary>
/// Parsed command-line options for the three BVT modes.
/// </summary>
public sealed class CliOptions
{
    /// <summary>Requested mode: report, transcode, verify, or help.</summary>
    public string? Mode { get; private set; }

    /// <summary>Root folder scanned in report mode.</summary>
    public string? RootDirectory { get; private set; }

    /// <summary>Directory where report.json and report.csv are written.</summary>
    public string? OutputDirectory { get; private set; }

    /// <summary>Path to report.json for transcode and verify modes.</summary>
    public string? ReportPath { get; private set; }

    /// <summary>x264 preset selected for legacy video transcodes.</summary>
    public string Preset { get; private set; } = string.Empty;

    /// <summary>Path or executable name for ffprobe.</summary>
    public string FfprobePath { get; private set; } = "ffprobe";

    /// <summary>Path or executable name for ffmpeg.</summary>
    public string FfmpegPath { get; private set; } = "ffmpeg";

    /// <summary>Directory where log files are written.</summary>
    public string LogDirectory { get; private set; } = "logs";

    /// <summary>Maximum number of parallel ffmpeg jobs.</summary>
    public int MaxConcurrentFfmpegJobs { get; private set; } = 1;

    /// <summary>True when transcode mode should process only DVD VIDEO_TS report rows.</summary>
    public bool DvdOnly { get; private set; }

    /// <summary>True when the user requested usage information.</summary>
    public bool ShowHelp { get; private set; }

    /// <summary>
    /// Parses raw command-line arguments into a structured options object.
    /// </summary>
    /// <param name="args">Raw command-line arguments supplied to the executable.</param>
    /// <returns>The parsed options object.</returns>
    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();
        if (args.Length == 0)
        {
            return options;
        }

        options.Mode = args[0].ToLowerInvariant();
        if (options.Mode is "-h" or "--help" or "help")
        {
            options.ShowHelp = true;
            return options;
        }

        // Simple switch/value parsing is enough here and avoids adding parser dependencies.
        for (var i = 1; i < args.Length; i++)
        {
            var key = args[i];
            if (!key.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            if (key == "--dvd-only")
            {
                options.DvdOnly = true;
                continue;
            }

            if (i + 1 >= args.Length)
            {
                break;
            }

            var value = args[++i];
            switch (key)
            {
                case "--root": options.RootDirectory = value; break;
                case "--out": options.OutputDirectory = value; break;
                case "--report": options.ReportPath = value; break;
                case "--preset": options.Preset = value; break;
                case "--ffprobe": options.FfprobePath = value; break;
                case "--ffmpeg": options.FfmpegPath = value; break;
                case "--log": options.LogDirectory = value; break;
                case "--max-jobs":
                    if (int.TryParse(value, out var maxJobs))
                    {
                        options.MaxConcurrentFfmpegJobs = maxJobs;
                    }
                    break;
            }
        }

        return options;
    }

    /// <summary>
    /// Applies appsettings defaults to any options not explicitly set on the CLI.
    /// </summary>
    /// <param name="settings">Settings loaded from appsettings.json.</param>
    public void ApplyDefaults(AppSettings settings)
    {
        Preset = string.IsNullOrWhiteSpace(Preset) ? settings.Preset : Preset;
        Preset = string.IsNullOrWhiteSpace(Preset) ? "medium" : Preset;
        FfprobePath = string.IsNullOrWhiteSpace(settings.FfprobePath) ? FfprobePath : settings.FfprobePath;
        FfmpegPath = string.IsNullOrWhiteSpace(settings.FfmpegPath) ? FfmpegPath : settings.FfmpegPath;
        LogDirectory = string.IsNullOrWhiteSpace(settings.LogDirectory) ? LogDirectory : settings.LogDirectory;
        MaxConcurrentFfmpegJobs = MaxConcurrentFfmpegJobs > 1 ? MaxConcurrentFfmpegJobs : settings.MaxConcurrentFfmpegJobs;
        MaxConcurrentFfmpegJobs = Math.Max(1, MaxConcurrentFfmpegJobs);
    }

    /// <summary>
    /// Validates mode-specific required options.
    /// </summary>
    /// <param name="error">When validation fails, receives the user-facing error message.</param>
    /// <returns>True when the options are valid for the selected mode.</returns>
    public bool IsValid(out string error)
    {
        error = string.Empty;
        switch (Mode)
        {
            case "report":
                if (string.IsNullOrWhiteSpace(RootDirectory) || !Directory.Exists(RootDirectory))
                {
                    error = "--root is required and must exist.";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(OutputDirectory))
                {
                    error = "--out is required in report mode.";
                    return false;
                }

                return true;

            case "transcode":
            case "verify":
                if (string.IsNullOrWhiteSpace(ReportPath) || !File.Exists(ReportPath))
                {
                    error = "--report is required and must exist.";
                    return false;
                }

                return true;

            default:
                error = "Mode is required: report, transcode, or verify.";
                return false;
        }
    }

    /// <summary>
    /// Prints the supported command-line shapes.
    /// </summary>
    public static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  batch-video-transcoder.exe report --root \"E:\\Media\\movies\" --out \"E:\\Media\\transcode-report\" [--preset medium]");
        Console.WriteLine("  batch-video-transcoder.exe transcode --report \"E:\\Media\\transcode-report\\report.json\" [--preset medium] [--max-jobs 1] [--dvd-only]");
        Console.WriteLine("  batch-video-transcoder.exe verify --report \"E:\\Media\\transcode-report\\report.json\"");
    }
}

