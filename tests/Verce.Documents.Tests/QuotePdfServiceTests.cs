using FluentAssertions;
using Verce.Modules.Documents;

namespace Verce.Documents.Tests;

public sealed class QuotePdfServiceTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public async Task Reissue_rejects_an_invalid_reason_before_any_dependency_is_used(string? reason)
    {
        var service = new QuotePdfService(null!, null!, null!);
        var act = () => service.ReissueAsync(null!, null, DateTimeOffset.UtcNow, reason!, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("DOCUMENT_REISSUE_REASON_REQUIRED");
    }
}
