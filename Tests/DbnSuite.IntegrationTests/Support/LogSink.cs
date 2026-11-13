using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace DbnSuite.IntegrationTests.Support;

/// <summary>What the worker logged, through the providers Serilog forwards to.</summary>
public sealed class LogSink : ILoggerProvider
{
    private readonly ConcurrentQueue<(LogLevel Level, string Text, Exception? Exception)> lines = new();

    public IReadOnlyList<(LogLevel Level, string Text, Exception? Exception)> Lines => [.. lines];

    public ILogger CreateLogger(string categoryName) => new Sink(lines);

    public void Dispose()
    {
    }

    private sealed class Sink(ConcurrentQueue<(LogLevel, string, Exception?)> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => lines.Enqueue((logLevel, formatter(state, exception), exception));
    }
}
