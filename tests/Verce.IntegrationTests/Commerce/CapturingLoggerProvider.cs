using System.Collections.Concurrent;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Verce.IntegrationTests.Commerce;

/// <summary>
/// A-08-LOG: one CAPTURED log event, keeping the REAL structured state/property bag Microsoft.
/// Extensions.Logging passed to this call (e.g. the actual {Provider}, {Operation}, {HttpStatus},
/// {Classification}, {Certainty} values) — not just the rendered/formatted text. Asserting against
/// <see cref="State"/> proves what a real structured log sink (Seq/ELK/whatever) would actually
/// index and could alert/dashboard on; asserting only against rendered text can never distinguish
/// "the real field value is safe" from "the message template happens not to mention it this time".
/// </summary>
public sealed record CapturedLogEntry(string Category, LogLevel Level, string Message, string? ExceptionText, IReadOnlyDictionary<string, object?> State);

/// <summary>
/// An in-memory <see cref="ILoggerProvider"/> that captures every log call (across all
/// categories/levels) the real running host emits, for asserting absence of sensitive values
/// (ADR-0024 G-08: "Suppress entire query string and bodies on authorization, callback and token
/// paths... Specifically exclude code, state, error_description..."). Captures the FORMATTED
/// message plus any exception text (<see cref="Messages"/>, unchanged — many existing tests assert
/// against this), and ADDITIONALLY the real structured state (<see cref="Entries"/>) for tests that
/// need to assert on the genuine property bag rather than string-matching rendered output.
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _messages = new();
    private readonly ConcurrentQueue<CapturedLogEntry> _entries = new();

    public IReadOnlyCollection<string> Messages => _messages.ToArray();

    /// <summary>A-08-LOG: every captured event with its real structured state — see
    /// <see cref="CapturedLogEntry"/>.</summary>
    public IReadOnlyCollection<CapturedLogEntry> Entries => _entries.ToArray();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _messages, _entries);

    public void Dispose() { }

    private sealed class CapturingLogger : ILogger
    {
        private readonly string _category;
        private readonly ConcurrentQueue<string> _sink;
        private readonly ConcurrentQueue<CapturedLogEntry> _entrySink;
        public CapturingLogger(string category, ConcurrentQueue<string> sink, ConcurrentQueue<CapturedLogEntry> entrySink)
        { _category = category; _sink = sink; _entrySink = entrySink; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            var exceptionText = exception?.ToString();
            _sink.Enqueue($"[{_category}] {message}" + (exceptionText is null ? "" : $" | EXCEPTION: {exceptionText}"));

            // Microsoft.Extensions.Logging's LogInformation/LogWarning/etc. extensions pass a
            // FormattedLogValues (or any caller-supplied TState) implementing this enumerable
            // key/value shape for every templated call — this recovers the REAL argument values by
            // their template placeholder names, not a re-parse of the rendered string.
            IReadOnlyDictionary<string, object?> structured = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.ToDictionary(p => p.Key, p => p.Value)
                : new Dictionary<string, object?>();
            _entrySink.Enqueue(new CapturedLogEntry(_category, logLevel, message, exceptionText, structured));
        }
    }
}
