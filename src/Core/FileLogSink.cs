using System.Text;
using OpenReflect.Core.Abstractions;

namespace OpenReflect.Core;

/// <summary>Built-in file logger (ARCHITECTURE.md D8/D9): one UTF-8 line-per-entry log per run, flushed per line.</summary>
public sealed class FileLogSink : ILogSink
{
    private readonly StreamWriter _writer;
    private readonly object _gate = new();

    public string Path { get; }

    public FileLogSink(string logDirectory, string? runName = null)
    {
        Directory.CreateDirectory(logDirectory);
        Path = System.IO.Path.Combine(logDirectory, $"{runName ?? "orb-run"}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
        _writer = new StreamWriter(new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.Read), Encoding.UTF8) { AutoFlush = true };
    }

    public void Write(LogLevel level, string component, string message, Exception? exception = null)
    {
        lock (_gate)
        {
            _writer.Write(DateTime.UtcNow.ToString("o"));
            _writer.Write(" [");
            _writer.Write(level.ToString().ToUpperInvariant());
            _writer.Write("] ");
            _writer.Write(component);
            _writer.Write(": ");
            _writer.WriteLine(exception is null ? message : $"{message} :: {exception}");
        }
    }
}
