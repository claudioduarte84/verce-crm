using FluentAssertions;
using Verce.SharedKernel.Results;

namespace Verce.SharedKernel.Tests;

public class ResultTests
{
    [Fact]
    public void Success_result_has_no_error_code()
    {
        var result = Result.Success();
        result.IsSuccess.Should().BeTrue();
        result.ErrorCode.Should().BeNull();
    }

    [Fact]
    public void Failure_result_requires_an_error_code()
    {
        var result = Result.Failure("QUOTE_REVISION_ALREADY_DECIDED");
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("QUOTE_REVISION_ALREADY_DECIDED");
    }

    [Fact]
    public void Generic_success_carries_a_value()
    {
        var result = Result.Success(42);
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
    }

    [Fact]
    public void Accessing_Value_on_failure_throws()
    {
        var result = Result.Failure<int>("SOME_ERROR");
        var act = () => result.Value;
        act.Should().Throw<InvalidOperationException>();
    }
}
