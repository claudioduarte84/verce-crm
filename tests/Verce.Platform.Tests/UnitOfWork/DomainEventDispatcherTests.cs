using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Verce.Platform.Persistence;
using Verce.Platform.UnitOfWork;
using Verce.SharedKernel.Events;

namespace Verce.Platform.Tests.UnitOfWork;

/// <summary>
/// ADR-0012 §6: a drained domain event is dispatched to every registered
/// <see cref="IDomainEventHandler{TEvent}"/> for its runtime type, in DI REGISTRATION order —
/// never assembly-scan order. The ambient <see cref="VerceDbContext"/> passed through is
/// opaque to this dispatcher (it never inspects it), so a null reference stands in for it here
/// — the UnitOfWork's own use of a real, transacted context is exercised end-to-end by
/// Verce.IntegrationTests.
/// </summary>
public class DomainEventDispatcherTests
{
    private sealed class ProbeEvent : DomainEventBase
    {
        public ProbeEvent() : base(Guid.CreateVersion7(), null, DateTimeOffset.UtcNow) { }
    }

    private sealed class OtherProbeEvent : DomainEventBase
    {
        public OtherProbeEvent() : base(Guid.CreateVersion7(), null, DateTimeOffset.UtcNow) { }
    }

    private sealed class RecordingHandler : IDomainEventHandler<ProbeEvent>
    {
        public RecordingHandler(string name, List<string> invocations) { _name = name; _invocations = invocations; }
        private readonly string _name;
        private readonly List<string> _invocations;

        public Task HandleAsync(ProbeEvent domainEvent, VerceDbContext context, CancellationToken cancellationToken)
        {
            _invocations.Add(_name);
            return Task.CompletedTask;
        }
    }

    private sealed class OtherEventHandler : IDomainEventHandler<OtherProbeEvent>
    {
        public bool Invoked { get; private set; }
        public Task HandleAsync(OtherProbeEvent domainEvent, VerceDbContext context, CancellationToken cancellationToken)
        {
            Invoked = true;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Dispatches_to_every_registered_handler_of_the_events_runtime_type()
    {
        var invocations = new List<string>();
        var services = new ServiceCollection();
        services.AddSingleton<IDomainEventHandler<ProbeEvent>>(new RecordingHandler("first", invocations));
        services.AddSingleton<IDomainEventHandler<ProbeEvent>>(new RecordingHandler("second", invocations));
        var dispatcher = new DomainEventDispatcher(services.BuildServiceProvider());

        await dispatcher.DispatchAsync(new ProbeEvent(), context: null!, CancellationToken.None);

        invocations.Should().Equal(new[] { "first", "second" }, "registration order is dispatch order — never assembly-scan order (ADR-0012 §6)");
    }

    [Fact]
    public async Task An_event_type_with_no_registered_handler_dispatches_without_error()
    {
        var services = new ServiceCollection();
        var dispatcher = new DomainEventDispatcher(services.BuildServiceProvider());

        var act = async () => await dispatcher.DispatchAsync(new ProbeEvent(), context: null!, CancellationToken.None);

        await act.Should().NotThrowAsync("S1 ships zero business handlers — dispatch must be a safe no-op until S2+ registers one");
    }

    [Fact]
    public async Task A_handler_registered_for_a_different_event_type_is_never_invoked()
    {
        var services = new ServiceCollection();
        var otherHandler = new OtherEventHandler();
        services.AddSingleton<IDomainEventHandler<OtherProbeEvent>>(otherHandler);
        var dispatcher = new DomainEventDispatcher(services.BuildServiceProvider());

        await dispatcher.DispatchAsync(new ProbeEvent(), context: null!, CancellationToken.None);

        otherHandler.Invoked.Should().BeFalse();
    }

    [Fact]
    public async Task A_handler_that_throws_propagates_the_exception_so_the_transaction_rolls_back()
    {
        // A-3: handler failure must roll back the whole Unit of Work — the dispatcher must not
        // swallow the exception on the caller's behalf.
        var services = new ServiceCollection();
        services.AddSingleton<IDomainEventHandler<ProbeEvent>>(new ThrowingHandler());
        var dispatcher = new DomainEventDispatcher(services.BuildServiceProvider());

        var act = async () => await dispatcher.DispatchAsync(new ProbeEvent(), context: null!, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
    }

    private sealed class ThrowingHandler : IDomainEventHandler<ProbeEvent>
    {
        // Marked async so the compiler captures the exception into the returned Task's fault
        // state (as every real handler's genuinely awaited body would) rather than throwing
        // synchronously through MethodInfo.Invoke, which is a reflection artifact, not a
        // platform behavior — a non-async handler is not a realistic ADR-0012 §9 handler shape.
        public async Task HandleAsync(ProbeEvent domainEvent, VerceDbContext context, CancellationToken cancellationToken)
        {
            await Task.Yield();
            throw new InvalidOperationException("boom");
        }
    }
}
