namespace BVT;

/// <summary>
/// Detects ripped DVD structures by looking for VIDEO_TS/VIDEO_TS.IFO.
/// </summary>
public sealed class DvdVideoTsDetector
{
    /// <summary>
    /// Finds all DVD VIDEO_TS folders under a root folder.
    /// </summary>
    /// <param name="root">Root folder to scan recursively.</param>
    /// <returns>Paths to detected VIDEO_TS folders.</returns>
    public IReadOnlyList<string> FindDvdFolders(string root)
    {
        return Directory.EnumerateDirectories(root, "VIDEO_TS", SearchOption.AllDirectories)
            .Where(path => File.Exists(Path.Combine(path, "VIDEO_TS.IFO")))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
