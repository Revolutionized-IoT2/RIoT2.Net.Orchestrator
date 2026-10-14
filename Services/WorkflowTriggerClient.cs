using Grpc.Net.Client;
using RIoT2.Net.Orchestrator.Grpc;

namespace RIoT2.Net.Orchestrator.Services;

internal sealed class WorkflowTriggerClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GrpcChannel _channel;
    private RIoTTriggerService.RIoTTriggerServiceClient _client;
    private Uri _address;
    private bool _disposed;

    public async Task<TriggerResponse> TriggerAsync(string address, TriggerRequest request, CancellationToken cancellationToken, DateTime? deadline = null)
    {
        var uri = new Uri(address, UriKind.Absolute);
        var callDeadline = DateTime.UtcNow.AddSeconds(5);
        if (deadline < callDeadline)
            callDeadline = deadline.Value;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_address != uri)
            {
                var channel = GrpcChannel.ForAddress(uri);
                _channel?.Dispose();
                _channel = channel;
                _client = new RIoTTriggerService.RIoTTriggerServiceClient(channel);
                _address = uri;
            }
            return await _client.TriggerAsync(request,
                deadline: callDeadline, cancellationToken: cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed)
                return;
            _disposed = true;
            _channel?.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }
}
