using FluentAssertions;

namespace Verce.IntegrationTests.Auth;

/// <summary>
/// M-TESTHOST-001: <see cref="VerceWebApplicationFactory"/> mirrors its configuration into
/// process-wide environment variables (the only way an eagerly-read value like
/// <c>Outbox:SchedulingEnabled</c> can be visible to Program.cs before builder.Build() runs —
/// see that class's own comment). These tests prove the mutation cannot leak between factory
/// instances: each factory's disposal restores the TRUE original value (captured before the
/// first factory in the process ever touched it), never merely clears to null, and a later
/// factory that doesn't override a key never inherits a previous factory's leftover value. None
/// of this needs the host to actually start — the behavior under test happens entirely in the
/// constructor and DisposeAsync, so these run fast and need no Postgres fixture beyond a
/// syntactically valid connection string.
/// </summary>
[Collection(PostgresCollection.Name)]
public class VerceWebApplicationFactoryEnvironmentIsolationTests
{
    private const string UnreachableConnectionString = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused";
    private const string EnvKey = "Outbox__DispatchIntervalSeconds";
    private const string SchedulingEnvKey = "Outbox__SchedulingEnabled";

    [Fact]
    public async Task An_override_does_not_leak_after_the_factory_that_set_it_is_disposed()
    {
        var trueOriginal = Environment.GetEnvironmentVariable(EnvKey);
        try
        {
            await using (new VerceWebApplicationFactory(UnreachableConnectionString,
                       new Dictionary<string, string?> { ["Outbox:DispatchIntervalSeconds"] = "7" }))
            {
                Environment.GetEnvironmentVariable(EnvKey).Should().Be("7");
            }

            // Disposed — the process must show exactly what it showed before ANY factory touched
            // this key, not merely be cleared to null (a real value could legitimately be present
            // in a developer's shell or CI).
            Environment.GetEnvironmentVariable(EnvKey).Should().Be(trueOriginal);

            await using var withoutOverride = new VerceWebApplicationFactory(UnreachableConnectionString);
            Environment.GetEnvironmentVariable(EnvKey).Should().Be(trueOriginal,
                "a factory that never overrides this key must never inherit a previous factory's leftover value");
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvKey, trueOriginal);
        }
    }

    [Fact]
    public async Task Two_sequential_factories_with_opposite_scheduling_settings_each_observe_their_own_value()
    {
        var trueOriginal = Environment.GetEnvironmentVariable(SchedulingEnvKey);
        try
        {
            await using (new VerceWebApplicationFactory(UnreachableConnectionString,
                       new Dictionary<string, string?> { ["Outbox:SchedulingEnabled"] = "true" }))
            {
                Environment.GetEnvironmentVariable(SchedulingEnvKey).Should().Be("true");
            }

            await using (new VerceWebApplicationFactory(UnreachableConnectionString,
                       new Dictionary<string, string?> { ["Outbox:SchedulingEnabled"] = "false" }))
            {
                Environment.GetEnvironmentVariable(SchedulingEnvKey).Should().Be("false",
                    "this factory's own explicit override must win — never the previous factory's 'true'");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(SchedulingEnvKey, trueOriginal);
        }
    }

    [Fact]
    public async Task A_factory_without_an_override_observes_the_class_default_after_a_previous_override_disposes()
    {
        var trueOriginal = Environment.GetEnvironmentVariable(SchedulingEnvKey);
        try
        {
            await using (new VerceWebApplicationFactory(UnreachableConnectionString,
                       new Dictionary<string, string?> { ["Outbox:SchedulingEnabled"] = "true" }))
            {
                Environment.GetEnvironmentVariable(SchedulingEnvKey).Should().Be("true");
            }

            // No override at all — must observe THIS class's own baked-in default ("false"), not
            // the disposed previous factory's "true".
            await using var withoutOverride = new VerceWebApplicationFactory(UnreachableConnectionString);
            Environment.GetEnvironmentVariable(SchedulingEnvKey).Should().Be("false");
        }
        finally
        {
            Environment.SetEnvironmentVariable(SchedulingEnvKey, trueOriginal);
        }
    }
}
