using FluentAssertions;
using Verce.Platform.Outbox;

namespace Verce.Platform.Tests.Outbox;

public class OutboxRetryPolicyTests
{
    private readonly OutboxRetryPolicy _policy = new();

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 5)]
    [InlineData(3, 30)]
    [InlineData(4, 120)]
    public void C6_retryable_failure_uses_the_approved_ladder(int attempt, int expectedMinutes)
    {
        var result = _policy.Decide(attempt, maxAttempts: 5);

        result.IsTerminal.Should().BeFalse();
        result.Delay.Should().Be(TimeSpan.FromMinutes(expectedMinutes));
    }

    [Fact]
    public void C6_final_attempt_is_terminal()
    {
        var result = _policy.Decide(attemptNumber: 5, maxAttempts: 5);

        result.IsTerminal.Should().BeTrue();
        result.Delay.Should().BeNull();
    }

    [Fact]
    public void C6_per_message_budget_override_is_honored()
    {
        _policy.Decide(attemptNumber: 3, maxAttempts: 3).IsTerminal.Should().BeTrue();
    }
}
