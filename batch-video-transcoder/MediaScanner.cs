using System.Text.RegularExpressions;
using BVT.Models;

namespace BVT;

/// <summary>
/// Discovers logical Jellyfin movie items, including regular files, multipart files, and DVD VIDEO_TS folders.
/// </summary>
public sealed class MediaScanner
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".3gp", ".avi", ".divx", ".flv", ".m2ts", ".m4v", ".mkv", ".mov", ".mp4", ".mpeg",
        ".mpg", ".mts", ".ogm", ".rm", ".rmvb", ".ts", ".vob", ".webm", ".wmv"
    };

    private static readonly Regex MultipartRegex = new(
        @"(?ix)
        (?<prefix>.*?)
        (?:[\s._-]*)
        (?:
            part|parte|pt|cd|disc|disk
        )
        (?:[\s._-]*)
        (?<number>[0-9]+)
        (?<suffix>.*?)$",
        RegexOptions.Compiled);

    /// <summary>
    /// Scans a root folder and returns logical report rows for each discovered movie item.
    /// </summary>
    /// <param name="root">Root Jellyfin movies folder to scan recursively.</param>
    /// <param name="dvdDetector">Detector used to find VIDEO_TS DVD structures.</param>
    /// <param name="dvdAnalyzer">Analyzer used to select each DVD main title.</param>
    /// <returns>Logical media items sorted by directory and file name.</returns>
    public IReadOnlyList<MediaFileInfo> Scan(string root, DvdVideoTsDetector dvdDetector, DvdTitleAnalyzer dvdAnalyzer)
    {
        var dvdRows = dvdDetector.FindDvdFolders(root)
            .Select(path => dvdAnalyzer.Analyze(path))
            .ToList();

        var dvdVideoTsFolders = new HashSet<string>(
            dvdRows.Select(x => Path.Combine(x.Directory, "VIDEO_TS")),
            StringComparer.OrdinalIgnoreCase);

        // VOB files inside detected DVDs are handled as part of the DVD row, not as standalone movies.
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => VideoExtensions.Contains(Path.GetExtension(path)))
            .Where(path => !path.EndsWith(".converted.mkv", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.EndsWith(".dvdremux.mkv", StringComparison.OrdinalIgnoreCase))
            .Where(path => !IsInsideDvdVideoTs(path, dvdVideoTsFolders))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var fileRows = BuildFileRows(files);
        return dvdRows.Concat(fileRows)
            .OrderBy(x => x.Directory, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Converts physical files into logical rows, grouping multipart movies into a single item.
    /// </summary>
    /// <param name="files">Candidate video files outside DVD folders.</param>
    /// <returns>Report rows for regular files and multipart groups.</returns>
    private static IEnumerable<MediaFileInfo> BuildFileRows(IReadOnlyList<string> files)
    {
        var grouped = files
            .GroupBy(path => GetGroupingKey(path), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in grouped)
        {
            var parts = group
                .OrderBy(path => GetPartNumber(path))
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var primary = parts[0];
            var totalBytes = parts.Sum(path => new FileInfo(path).Length);
            var directory = Path.GetDirectoryName(primary) ?? string.Empty;
            var folderName = new DirectoryInfo(directory).Name;

            yield return new MediaFileInfo
            {
                MediaType = MediaType.File,
                FullPath = primary,
                InputFiles = parts,
                IsMultipart = parts.Count > 1,
                Directory = directory,
                FileName = parts.Count > 1 ? folderName : Path.GetFileName(primary),
                MovieFolderName = folderName,
                SizeBytes = totalBytes,
                SizeGB = Math.Round(totalBytes / 1024d / 1024d / 1024d, 3),
                EstimatedMainMovieSizeGB = Math.Round(totalBytes / 1024d / 1024d / 1024d, 3)
            };
        }
    }

    /// <summary>
    /// Produces a stable grouping key by removing known part markers from file names.
    /// </summary>
    /// <param name="path">Video file path to inspect.</param>
    /// <returns>A grouping key shared by related multipart files.</returns>
    private static string GetGroupingKey(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var match = MultipartRegex.Match(fileName);
        if (!match.Success)
        {
            return path;
        }

        return Path.Combine(directory, $"{match.Groups["prefix"].Value}{match.Groups["suffix"].Value}{extension}");
    }

    /// <summary>
    /// Reads the part number from a multipart file name.
    /// </summary>
    /// <param name="path">Video file path to inspect.</param>
    /// <returns>The detected part number, or 0 for non-multipart names.</returns>
    private static int GetPartNumber(string path)
    {
        var match = MultipartRegex.Match(Path.GetFileNameWithoutExtension(path));
        if (match.Success && int.TryParse(match.Groups["number"].Value, out var number))
        {
            return number;
        }

        return 0;
    }

    /// <summary>
    /// Determines whether a file belongs to a detected DVD VIDEO_TS folder.
    /// </summary>
    /// <param name="path">File path to test.</param>
    /// <param name="dvdVideoTsFolders">Set of known VIDEO_TS folder paths.</param>
    /// <returns>True when the file is inside a detected DVD folder.</returns>
    private static bool IsInsideDvdVideoTs(string path, HashSet<string> dvdVideoTsFolders)
    {
        var directory = Path.GetDirectoryName(path);
        return directory is not null && dvdVideoTsFolders.Contains(directory);
    }
}

