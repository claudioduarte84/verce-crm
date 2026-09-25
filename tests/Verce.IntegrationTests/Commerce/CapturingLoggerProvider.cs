using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Verce.IntegrationTests.Commerce;

/// <summary>
/// An in-memory <see cref="ILoggerProvider"/> that captures every formatted log message (across
/// all categories/levels) the real running host emits, for asserting absence of sensitive values
/// (ADR-0024 G-08: "Suppress entire query string and bodies on authorization, callback and token
/// paths... Specifically exclude code, state, error_description..."). Captures the FORMATTED
/// message plus any exception text, since a raw exception message is exactly where an
/// un-redacted value could otherwise leak.
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _messages = new();

    public IReadOnlyCollection<string> Messages => _messages.ToArray();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _messages);

    public void Dispose() { }

    private sealed class CapturingLogger : ILogger
    {
        private readonly string _category;
        private readonly ConcurrentQueue<string> _sink;
        public CapturingLogger(string category, ConcurrentQueue<string> sink) { _category = category; _sink = sink; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            var exceptionText = exception?.ToString();
            _sink.Enqueue($"[{_category}] {message}" + (exceptionText is null ? "" : $" | EXCEPTION: {exceptionText}"));
        }
    }
}
