using System.Reflection;
using FluentAssertions;
using Verce.Platform.Persistence;
using Verce.Platform.UnitOfWork;
using Verce.SharedKernel.Events;

namespace Verce.Architecture.Tests;

/// <summary>ADR-0012 §1: a concrete event implements EXACTLY ONE of IDomainEvent/IIntegrationEvent,
/// never both, never IEvent directly.</summary>
public class EventExclusivityTests
{
    internal static IEnumerable<Type> AllLoadedTypes()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.FullName is null || !assembly.FullName.StartsWith("Verce.")) continue;

            Type?[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types; }

            foreach (var type in types)
                if (type is not null) yield return type;
        }
    }

    [Fact]
    public void No_concrete_type_implements_both_IDomainEvent_and_IIntegrationEvent()
    {
        var violations = AllLoadedTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => typeof(IDomainEvent).IsAssignableFrom(t) && typeof(IIntegrationEvent).IsAssignableFrom(t))
            .Select(t => t.FullName)
            .ToList();

        violations.Should().BeEmpty("a concrete event must implement exactly one of IDomainEvent or IIntegrationEvent");
    }

    [Fact]
    public void No_concrete_type_implements_IEvent_directly_without_the_specific_marker()
    {
        var violations = AllLoadedTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => typeof(IEvent).IsAssignableFrom(t))
            .Where(t => !typeof(IDomainEvent).IsAssignableFrom(t) && !typeof(IIntegrationEvent).IsAssignableFrom(t))
            .Select(t => t.FullName)
            .ToList();

        violations.Should().BeEmpty("every concrete event must be specifically a IDomainEvent or a IIntegrationEvent");
    }
}

/// <summary>
/// ADR-0012 §9: synchronous domain event handlers must not depend on IDbContextFactory&lt;&gt;,
/// IServiceScopeFactory, IServiceProvider, HttpClient, or any I/O abstraction. Passes vacuously
/// today (S1 ships zero business handlers) and becomes a real gate the moment S2+ adds one.
/// </summary>
public class SynchronousHandlerRestrictionTests
{
    private static readonly Type[] ForbiddenConstructorDependencies =
    {
        typeof(IServiceProvider),
        typeof(System.Net.Http.HttpClient),
    };

    private static readonly string[] ForbiddenDependencyTypeNames =
    {
        "IDbContextFactory`1", "IServiceScopeFactory", "IDocumentRenderer", "IAiClient",
    };

    [Fact]
    public void No_domain_event_handler_takes_a_forbidden_constructor_dependency()
    {
        var handlerImplementations = EventExclusivityTests.AllLoadedTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDomainEventHandler<>)))
            .ToList();

        var violations = new List<string>();
        foreach (var handlerType in handlerImplementations)
        {
            foreach (var ctor in handlerType.GetConstructors())
            {
                foreach (var param in ctor.GetParameters())
                {
                    var paramType = param.ParameterType;
                    if (ForbiddenConstructorDependencies.Any(f => f.IsAssignableFrom(paramType)) ||
                        ForbiddenDependencyTypeNames.Contains(paramType.Name))
                    {
                        violations.Add($"{handlerType.FullName} takes forbidden dependency {paramType.Name}");
                    }
                }
            }
        }

        violations.Should().BeEmpty("synchronous domain event handlers may use only the ambient VerceDbContext — no external I/O, no new scope/context");
    }

    [Fact]
    public void No_domain_event_handler_calls_BeginTransaction_in_source()
    {
        var violations = new List<string>();
        foreach (var file in RepoPaths.AllSourceFiles())
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("BeginTransaction") && file.Contains("Handler"))
                    violations.Add($"{Path.GetRelativePath(RepoPaths.RepoRoot, file)}:{i + 1}");
            }
        }
        violations.Should().BeEmpty();
    }
}
