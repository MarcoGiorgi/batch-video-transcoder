using BVT.Models;

namespace BVT;

/// <summary>
/// Thin adapter for DVD remux execution, kept for callers that want a DVD-specific abstraction.
/// </summary>
public sealed class DvdRemuxExecutor
{
    private readonly FfmpegExecutor _ffmpegExecutor;

    /// <summary>
    /// Creates an adapter around the shared ffmpeg executor.
    /// </summary>
    /// <param name="ffmpegExecutor">Executor that owns ffmpeg process handling.</param>
    public DvdRemuxExecutor(FfmpegExecutor ffmpegExecutor)
    {
        _ffmpegExecutor = ffmpegExecutor;
    }

    /// <summary>
    /// Remuxes a DVD report row using the shared ffmpeg executor.
    /// </summary>
    /// <param name="dvd">DVD media row to remux.</param>
    /// <param name="cancellationToken">Cancellation token for the ffmpeg process.</param>
    /// <returns>A task representing the remux operation.</returns>
    public Task RemuxAsync(MediaFileInfo dvd, CancellationToken cancellationToken = default)
    {
        return _ffmpegExecutor.ProcessAsync(dvd, preset: "medium", cancellationToken);
    }
}
