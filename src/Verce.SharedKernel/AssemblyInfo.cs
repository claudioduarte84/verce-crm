using System.Runtime.CompilerServices;

// The Unit of Work and the AggregateVersionInterceptor live in Verce.Platform and are the
// ONLY legitimate callers of AggregateRoot's internal drain/version-mutation methods
// (ADR-0011 §2, ADR-0012 §2). Test projects need the same visibility to assert on platform
// mechanics against test-only aggregates.
[assembly: InternalsVisibleTo("Verce.Platform")]
[assembly: InternalsVisibleTo("Verce.SharedKernel.Tests")]
[assembly: InternalsVisibleTo("Verce.Platform.Tests")]
[assembly: InternalsVisibleTo("Verce.IntegrationTests")]
