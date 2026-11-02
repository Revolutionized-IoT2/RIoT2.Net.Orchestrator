using Grpc.Net.Client;
using RIoT2.Core.Security;
using RIoT2.Net.Orchestrator.Grpc;

namespace RIoT2.Net.Orchestrator.Services;

/// <summary>
/// Triggers workflows on the workflow host over gRPC. Every call sends <c>X-RIoT2-Key</c> when the
/// orchestrator has <c>RIOT2_API_KEY</c>, and passes the outbound URL policy (design 7.5).
/// </summary>
internal sealed class WorkflowTriggerClient(string? apiKey = null, IOutboundUrlPolicy? outboundUrlPolicy = null) : IAsyncDisposable
{
    private readonly IOutboundUrlPolicy _outboundUrlPolicy = outboundUrlPolicy ?? AllowAllOutboundUrlPolicy.Instance;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GrpcChannel? _channel;
    private RIoTTriggerService.RIoTTriggerServiceClient? _client;
    private Uri? _address;
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
                var handler = new ApiKeyHandler(apiKey,
                    new OutboundUrlPolicyHandler(_outboundUrlPolicy, OutboundPurposes.Workflow, new SocketsHttpHandler { EnableMultipleHttp2Connections = true }));
                var channel = GrpcChannel.ForAddress(uri, new GrpcChannelOptions { HttpHandler = handler, DisposeHttpClient = true });
                _channel?.Dispose();
                _channel = channel;
                _client = new RIoTTriggerService.RIoTTriggerServiceClient(channel);
                _address = uri;
            }
            // The client is created together with _address, which now equals uri.
            return await _client!.TriggerAsync(request,
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
