using System.Runtime.CompilerServices;

// AmbientOperationContext.WaveIndex / CurrentEvent are internal-set — only Verce.Platform's own
// UnitOfWork may advance them. Verce.Platform.Tests needs the same visibility to simulate wave
// progression when testing ambient-context mechanics in isolation from a real transaction.
[assembly: InternalsVisibleTo("Verce.Platform.Tests")]
[assembly: InternalsVisibleTo("Verce.IntegrationTests")]
