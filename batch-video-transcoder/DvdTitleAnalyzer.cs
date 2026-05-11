using System.Text.RegularExpressions;
using BVT.Models;

namespace BVT;

/// <summary>
/// Selects the most likely main movie title inside a DVD VIDEO_TS folder.
/// </summary>
public sealed class DvdTitleAnalyzer
{
    private static readonly Regex VobRegex = new(@"^VTS_(?<title>\d{2})_(?<part>[1-9]\d*)\.VOB$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Analyzes a VIDEO_TS folder and creates a DVD report row for its largest VTS group.
    /// </summary>
    /// <param name="videoTsPath">Path to the VIDEO_TS folder.</param>
    /// <returns>A DVD report row configured for remux planning.</returns>
    public MediaFileInfo Analyze(string videoTsPath)
    {
        var movieDirectory = Directory.GetParent(videoTsPath)?.FullName ?? videoTsPath;
        var movieName = new DirectoryInfo(movieDirectory).Name;

        // DVD main movies are normally stored as the largest VTS_XX group; menus and trailers are smaller.
        var groups = Directory.EnumerateFiles(videoTsPath, "VTS_*_*.VOB")
            .Select(path => new { Path = path, Match = VobRegex.Match(Path.GetFileName(path)) })
            .Where(x => x.Match.Success)
            .GroupBy(x => x.Match.Groups["title"].Value, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Title = group.Key,
                Files = group.Select(x => x.Path).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
                SizeBytes = group.Sum(x => new FileInfo(x.Path).Length)
            })
            .OrderByDescending(group => group.SizeBytes)
            .ToList();

        var main = groups.FirstOrDefault();
        var titleId = main?.Title ?? "01";
        var mainTitle = $"VTS_{titleId}";
        var ifoPath = Path.Combine(videoTsPath, $"{mainTitle}_0.IFO");
        if (!File.Exists(ifoPath))
        {
            ifoPath = Path.Combine(videoTsPath, "VIDEO_TS.IFO");
        }

        var sizeBytes = main?.SizeBytes ?? 0;
        return new MediaFileInfo
        {
            MediaType = MediaType.DVD_VIDEO_TS,
            ProcessingStrategy = ProcessingStrategy.DvdRemux,
            FullPath = ifoPath,
            InputFiles = main?.Files ?? new List<string>(),
            Directory = movieDirectory,
            FileName = movieName,
            MovieFolderName = movieName,
            SizeBytes = sizeBytes,
            SizeGB = Math.Round(sizeBytes / 1024d / 1024d / 1024d, 3),
            Container = "dvd",
            VideoCodec = "mpeg2video",
            MainTitleDetected = mainTitle,
            EstimatedMainMovieSizeGB = Math.Round(sizeBytes / 1024d / 1024d / 1024d, 3)
        };
    }
}
