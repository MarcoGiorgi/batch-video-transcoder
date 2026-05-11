namespace BVT;

/// <summary>
/// Appends operational messages to a timestamped log file.
/// </summary>
public sealed class FileLogger : IDisposable
{
    private readonly StreamWriter _writer;

    /// <summary>
    /// Creates a log writer at the requested path.
    /// </summary>
    /// <param name="path">Path to the log file to append.</param>
    public FileLogger(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        _writer = new StreamWriter(File.Open(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true
        };
    }

    /// <summary>Writes an informational line.</summary>
    /// <param name="message">Message text to write.</param>
    public void Info(string message) => Write("INFO", message);

    /// <summary>Writes a warning line.</summary>
    /// <param name="message">Message text to write.</param>
    public void Warn(string message) => Write("WARN", message);

    /// <summary>Writes an error line.</summary>
    /// <param name="message">Message text to write.</param>
    public void Error(string message) => Write("ERROR", message);

    /// <summary>
    /// Writes one ISO-8601 timestamped log line.
    /// </summary>
    /// <param name="level">Log severity label.</param>
    /// <param name="message">Message text to write.</param>
    private void Write(string level, string message)
    {
        _writer.WriteLine($"{DateTimeOffset.Now:O} [{level}] {message}");
    }

    /// <summary>Flushes and closes the underlying file handle.</summary>
    public void Dispose()
    {
        _writer.Dispose();
    }
}

