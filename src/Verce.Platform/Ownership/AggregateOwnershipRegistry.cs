using System.Reflection;
using Verce.SharedKernel.Domain;

namespace Verce.Platform.Ownership;

/// <summary>
/// Resolves any owned child entity to its ultimate aggregate root by walking declared
/// <see cref="IOwnedBy{TParent}"/> chains (ADR-0011 §2.5). Built once at startup; a broken
/// chain (non-terminating, cyclic, or ambiguous) fails startup loudly rather than being
/// discovered at runtime. No naming-convention reflection: only the interfaces a type declares
/// are consulted.
/// </summary>
public sealed class AggregateOwnershipRegistry
{
    // Maps a child CLR type -> the type of its immediate parent (as declared by IOwnedBy<TParent>).
    private readonly Dictionary<Type, Type> _immediateParentOf = new();

    // Cache of fully-resolved root types, computed lazily and memoized.
    private readonly Dictionary<Type, Type> _resolvedRootOf = new();

    private AggregateOwnershipRegistry() { }

    /// <summary>
    /// Scans the given assemblies for every type implementing <see cref="IOwnedBy{TParent}"/>
    /// and every <see cref="IAggregateRoot"/>, then validates that every ownership chain
    /// terminates at exactly one root with no cycles. Throws on any violation.
    /// </summary>
    public static AggregateOwnershipRegistry BuildAndValidate(IEnumerable<Assembly> assemblies)
    {
        var registry = new AggregateOwnershipRegistry();
        var allTypes = assemblies.SelectMany(a => SafeGetTypes(a)).Distinct().ToList();

        foreach (var type in allTypes)
        {
            foreach (var iface in type.GetInterfaces())
            {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IOwnedBy<>))
                {
                    var parentType = iface.GetGenericArguments()[0];
                    if (registry._immediateParentOf.TryGetValue(type, out var existingParent) && existingParent != parentType)
                    {
                        throw new InvalidOperationException(
                            $"Type '{type.FullName}' declares IOwnedBy<> for both '{existingParent.FullName}' and " +
                            $"'{parentType.FullName}' — a child must have exactly one immediate parent.");
                    }
                    registry._immediateParentOf[type] = parentType;
                }
            }
        }

        // Validate every declared chain resolves to exactly one root, with cycle detection.
        foreach (var childType in registry._immediateParentOf.Keys)
        {
            registry.ResolveRoot(childType, new HashSet<Type>());
        }

        return registry;
    }

    /// <summary>
    /// Resolves the ultimate aggregate root type for a given entity type. If the type is
    /// itself an <see cref="IAggregateRoot"/>, returns itself. Throws if the chain is broken.
    /// </summary>
    public Type ResolveRoot(Type entityType) => ResolveRoot(entityType, new HashSet<Type>());

    private Type ResolveRoot(Type entityType, HashSet<Type> visited)
    {
        if (_resolvedRootOf.TryGetValue(entityType, out var cached))
            return cached;

        if (typeof(IAggregateRoot).IsAssignableFrom(entityType))
        {
            _resolvedRootOf[entityType] = entityType;
            return entityType;
        }

        if (!visited.Add(entityType))
        {
            throw new InvalidOperationException(
                $"Cyclic ownership chain detected involving '{entityType.FullName}'. " +
                "An IOwnedBy<> chain must terminate at an IAggregateRoot with no cycles.");
        }

        if (!_immediateParentOf.TryGetValue(entityType, out var parentType))
        {
            throw new InvalidOperationException(
                $"Type '{entityType.FullName}' does not implement IAggregateRoot and has no " +
                "IOwnedBy<> declaration — its ownership chain does not terminate at a root. " +
                "Every persisted domain entity must be an aggregate root or declare its " +
                "immediate parent via IOwnedBy<TParent> (ADR-0011 §2.5).");
        }

        var root = ResolveRoot(parentType, visited);
        _resolvedRootOf[entityType] = root;
        return root;
    }

    /// <summary>True if the given type participates in the ownership model at all (root or owned child).</summary>
    public bool IsTracked(Type entityType) =>
        typeof(IAggregateRoot).IsAssignableFrom(entityType) || _immediateParentOf.ContainsKey(entityType);

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null).Select(t => t!);
        }
    }
}
