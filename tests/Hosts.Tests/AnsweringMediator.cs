using Mediator;

namespace Hosts.Tests;

/// <summary>Base for tiny <see cref="IMediator"/> test stubs: every Send funnels through
/// <see cref="Answer"/> (record/branch there); streams and publish are unsupported. Exists so each stub
/// carries one method instead of the full 12-member interface boilerplate.</summary>
internal abstract class AnsweringMediator : IMediator
{
    protected abstract object? Answer(object message);

    private ValueTask<T> Route<T>(object message) => new((T)Answer(message)!);

    public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken ct = default) => Route<TResponse>(request);
    public ValueTask<TResponse> Send<TResponse>(ICommand<TResponse> command, CancellationToken ct = default) => Route<TResponse>(command);
    public ValueTask<TResponse> Send<TResponse>(IQuery<TResponse> query, CancellationToken ct = default) => Route<TResponse>(query);
    public ValueTask<object?> Send(object message, CancellationToken ct = default) => Route<object?>(message);

    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken ct = default) => throw new NotSupportedException();
    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamCommand<TResponse> command, CancellationToken ct = default) => throw new NotSupportedException();
    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamQuery<TResponse> query, CancellationToken ct = default) => throw new NotSupportedException();
    public IAsyncEnumerable<object?> CreateStream(object message, CancellationToken ct = default) => throw new NotSupportedException();

    public ValueTask Publish<TNotification>(TNotification n, CancellationToken ct = default) where TNotification : INotification => throw new NotSupportedException();
    public ValueTask Publish(object n, CancellationToken ct = default) => throw new NotSupportedException();
}
