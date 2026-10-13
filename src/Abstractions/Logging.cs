namespace OpenReflect.Core.Abstractions;

public enum LogLevel { Debug = 0, Info = 1, Warn = 2, Error = 3 }

/// <summary>Minimal logging seam (ARCHITECTURE.md D8/D9). Libraries never configure logging; front ends pass a sink via options.</summary>
public interface ILogSink
{
    void Write(LogLevel level, string component, string message, Exception? exception = null);
}

public sealed class NullLogSink : ILogSink
{
    public static readonly NullLogSink Instance = new();
    public void Write(LogLevel level, string component, string message, Exception? exception = null) { }
}
