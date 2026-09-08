using Microsoft.Extensions.DependencyInjection;
using Verce.Platform.Persistence;
using Verce.SharedKernel.Events;

namespace Verce.Platform.UnitOfWork;

/// <summary>
/// Resolves and invokes every <see cref="IDomainEventHandler{TEvent}"/> registered for the
/// runtime type of the event, in registration order. Deliberately hand-rolled rather than
/// pulling in a mediator library — there is exactly one dispatch rule and no pipeline behaviour
/// to justify the dependency (CLAUDE.md / mission: "no MediatR merely for ceremony").
/// </summary>
public sealed class DomainEventDispatcher : IDomainEventDispatcher
{
    private readonly IServiceProvider _serviceProvider;

    public DomainEventDispatcher(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task DispatchAsync(IDomainEvent domainEvent, VerceDbContext context, CancellationToken cancellationToken)
    {
        var handlerType = typeof(IDomainEventHandler<>).MakeGenericType(domainEvent.GetType());
        var handlers = _serviceProvider.GetServices(handlerType);
        var handleMethod = handlerType.GetMethod(nameof(IDomainEventHandler<IDomainEvent>.HandleAsync))!;

        foreach (var handler in handlers)
        {
            if (handler is null) continue;
            var task = (Task)handleMethod.Invoke(handler, new object[] { domainEvent, context, cancellationToken })!;
            await task.ConfigureAwait(false);
        }
    }
}
