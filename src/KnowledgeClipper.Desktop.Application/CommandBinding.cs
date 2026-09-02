using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KnowledgeClipper.Desktop.Application.V3
{
    internal enum InvocationSource { Pointer, Keyboard, UiAutomation }

    internal sealed class ApplicationCommandDispatcher
    {
        private readonly IReadOnlyDictionary<ApplicationCommandKind, Func<ApplicationCommand, CancellationToken, Task>> handlers;
        internal ApplicationCommandDispatcher(IReadOnlyDictionary<ApplicationCommandKind, Func<ApplicationCommand, CancellationToken, Task>> handlers) => this.handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
        internal Task DispatchAsync(ApplicationCommand command, CancellationToken cancellationToken)
        {
            if (command == null || !handlers.TryGetValue(command.Kind, out var handler)) throw new InvalidOperationException("application-command-unbound");
            return handler(command, cancellationToken);
        }
    }

    internal sealed class UnifiedApplicationCommandBinding
    {
        private readonly ApplicationCommandDispatcher dispatcher;
        internal UnifiedApplicationCommandBinding(ApplicationCommandDispatcher dispatcher) => this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        internal event Action<InvocationSource, ApplicationCommandKind> Invoked;
        internal Task InvokeAsync(InvocationSource source, ApplicationCommand command, CancellationToken cancellationToken)
        {
            Invoked?.Invoke(source, command.Kind);
            return dispatcher.DispatchAsync(command, cancellationToken);
        }
    }
}
