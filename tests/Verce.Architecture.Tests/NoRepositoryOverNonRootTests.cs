using System.Reflection;
using FluentAssertions;
using Verce.SharedKernel.Domain;

namespace Verce.Architecture.Tests;

/// <summary>
/// B-8 (ROADMAP S1 catalogue, ADR-0011 §2.6): "writes go through the aggregate root; reads do
/// not — there is no repository over a non-root entity." S1 has no repository abstraction at
/// all (writes go through VerceDbContext + the aggregate directly, per ADR-0001's rejection of
/// a generic-repository framework) — this is currently true because the concept doesn't exist
/// yet, not because a rule rejected it. This test is the regression net: the day a
/// "SomethingRepository&lt;TEntity&gt;"-shaped type appears anywhere in a Verce.* assembly, it
/// must be generic over an <see cref="IAggregateRoot"/>, never a plain owned child entity.
/// </summary>
public class NoRepositoryOverNonRootTests
{
    [Fact]
    public void No_repository_shaped_type_anywhere_is_generic_over_a_non_root_entity()
    {
        var verceAssemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.FullName?.StartsWith("Verce.") == true);

        var violations = new List<string>();

        foreach (var assembly in verceAssemblies)
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t is not null).Select(t => t!).ToArray(); }

            foreach (var type in types)
            {
                if (!type.Name.Contains("Repository", StringComparison.OrdinalIgnoreCase)) continue;
                if (!type.IsGenericType) continue;

                foreach (var typeArgument in type.GetGenericArguments())
                {
                    if (typeof(IAggregateRoot).IsAssignableFrom(typeArgument)) continue;
                    violations.Add($"{type.FullName} is generic over {typeArgument.FullName}, which is not an IAggregateRoot");
                }
            }
        }

        violations.Should().BeEmpty("B-8: any repository-shaped type must be generic over an aggregate root, never a plain owned child entity");
    }
}
