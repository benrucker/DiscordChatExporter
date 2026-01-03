using System;
using System.Threading;

namespace DiscordChatExporter.Core.Utils;

/// <summary>
/// Interface for logging verbose status messages during export operations.
/// </summary>
public interface IStatusLogger
{
    void Log(string message);
}

/// <summary>
/// A no-op status logger that discards all messages.
/// </summary>
public class NullStatusLogger : IStatusLogger
{
    public static NullStatusLogger Instance { get; } = new();

    public void Log(string message) { }
}

/// <summary>
/// Provides an async-local logger that can be scoped to specific operations.
/// </summary>
public static class ScopedStatusLogger
{
    private static readonly AsyncLocal<IStatusLogger?> _current = new();

    /// <summary>
    /// Gets the current scoped logger, or NullStatusLogger if none is set.
    /// </summary>
    public static IStatusLogger Current => _current.Value ?? NullStatusLogger.Instance;

    /// <summary>
    /// Sets the logger for the current async scope. Returns a disposable that restores the previous logger.
    /// </summary>
    public static IDisposable Begin(IStatusLogger logger)
    {
        var previous = _current.Value;
        _current.Value = logger;
        return new Scope(previous);
    }

    private class Scope(IStatusLogger? previous) : IDisposable
    {
        public void Dispose() => _current.Value = previous;
    }
}
