namespace BVT;

/// <summary>
/// Writes color-coded status messages to the console.
/// </summary>
public static class ConsoleLogger
{
    /// <summary>Writes an informational message.</summary>
    /// <param name="message">Message text to display.</param>
    public static void Info(string message) => Write(message, ConsoleColor.Cyan, "INFO");

    /// <summary>Writes a success message.</summary>
    /// <param name="message">Message text to display.</param>
    public static void Success(string message) => Write(message, ConsoleColor.Green, "OK");

    /// <summary>Writes a warning message.</summary>
    /// <param name="message">Message text to display.</param>
    public static void Warn(string message) => Write(message, ConsoleColor.Yellow, "WARN");

    /// <summary>Writes an error message.</summary>
    /// <param name="message">Message text to display.</param>
    public static void Error(string message) => Write(message, ConsoleColor.Red, "ERROR");

    /// <summary>
    /// Applies a temporary console foreground color for one prefixed log line.
    /// </summary>
    /// <param name="message">Message text to display.</param>
    /// <param name="color">Console color for the level prefix.</param>
    /// <param name="level">Short level label.</param>
    private static void Write(string message, ConsoleColor color, string level)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write($"[{level}] ");
        Console.ForegroundColor = previous;
        Console.WriteLine(message);
    }
}

