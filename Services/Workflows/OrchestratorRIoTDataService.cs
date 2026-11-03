using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RIoT2.Core.Models;
using RIoT2.Elsa.Workflows.Models;
using RIoT2.Elsa.Workflows.Services;
using RIoT2.Net.Orchestrator.Models;
using RIoT2.Net.Orchestrator.Services.Commands;
using RIoT2.Net.Orchestrator.Services.Persistence;
using CoreJson = RIoT2.Core.Utils.Json;

namespace RIoT2.Net.Orchestrator.Services.Workflows
{
    /// <summary>
    /// The RIoT activities' view of the orchestrator in the embedded mode (ADR 0016): the same values and commands the
    /// remote workflow host gets over HTTP (<c>GET /api/report/{id}/value</c>, <c>/api/command/{id}/value</c>,
    /// <c>/api/variable/{id}/value</c>, <c>POST /api/command/execute</c> and <c>/api/v2/commands</c>), read and sent in
    /// process. Every value takes the same JSON round trip as on the wire, so a workflow sees the same objects in both modes.
    /// </summary>
    internal sealed class OrchestratorRIoTDataService(
        IMessageStateService state,
        IOrchestratorConfigurationService configuration,
        IStoredObjectStore storedObjects,
        IOrchestratorMqttService mqtt,
        ICommandOutbox commands,
        IOptions<JsonOptions> mvcJson) : IRIoTDataService
    {
        /// <summary>The longest wait for a command's result, as <c>POST /api/v2/commands</c> allows.</summary>
        public const int MaxTtlSeconds = 86400;

        public Task<object> GetReportValueAsync(string reportId)
        {
            var report = state.Reports.FirstOrDefault(x => x.Id == reportId)
                ?? configuration.GetReportTemplates().FirstOrDefault(x => x.Id == reportId)?.GetAsReport();
            object value = report == null
                ? new ElsaReport { Id = reportId }
                : CoreJson.Deserialize<ElsaReport>(CoreJson.Serialize(report)) ?? new ElsaReport { Id = reportId };
            return Task.FromResult(value);
        }

        public Task<object> GetCommandValueAsync(string commandId)
        {
            var command = state.Commands.FirstOrDefault(x => x.Id == commandId)
                ?? configuration.GetCommandTemplates().FirstOrDefault(x => x.Id == commandId)?.GetAsCommand();
            object value = command == null
                ? new Command { Id = commandId }
                : CoreJson.Deserialize<Command>(CoreJson.Serialize(command)) ?? new Command { Id = commandId };
            return Task.FromResult(value);
        }

        public Task<object> GetVariableValueAsync(string variableId)
        {
            var variable = storedObjects.Get<Variable>(variableId);
            if (variable == null)
                return Task.FromResult<object>(new Variable { Id = variableId });
            // The controller answers with the DTO in the controllers' JSON settings.
            var json = JsonSerializer.Serialize(VariableDTO.ToVariableDTO(variable), mvcJson.Value.JsonSerializerOptions);
            return Task.FromResult<object>(CoreJson.Deserialize<Variable>(json) ?? new Variable { Id = variableId });
        }

        public async Task ExecuteCommandAsync(string id, object? data, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("A command identifier is required.", nameof(id));
            var command = CoreJson.Deserialize<Command>(CoreJson.Serialize(new Command { Id = id, Value = new ValueModel(data) }))
                ?? throw new InvalidOperationException("The command could not be read.");
            if (!await mqtt.ExecuteCommand(command))
                throw new InvalidOperationException($"Could not find a node for command with ID: {id}");
        }

        public async Task<CommandConfirmation> ExecuteCommandWithConfirmationAsync(string id, object? data, int ttlSeconds, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("A command identifier is required.", nameof(id));
            if (ttlSeconds is < 1 or > MaxTtlSeconds)
                throw new ArgumentOutOfRangeException(nameof(ttlSeconds), ttlSeconds, $"ttlSeconds must be a whole number from 1 to {MaxTtlSeconds}.");
            var value = CoreJson.Deserialize<ValueModel>(CoreJson.Serialize(new ValueModel(data)))
                ?? throw new InvalidOperationException("The command needs a value.");
            var nodeId = configuration.FindNodeId(id);
            if (string.IsNullOrEmpty(nodeId))
                throw new InvalidOperationException($"Could not find a node for command with ID: {id}");

            var delivery = commands.Enqueue(new Command { Id = id, Value = value }, nodeId, TimeSpan.FromSeconds(ttlSeconds));
            var final = await commands.WaitAsync(delivery.CorrelationId, cancellationToken) ?? delivery;
            return new CommandConfirmation
            {
                CorrelationId = final.CorrelationId,
                CommandId = final.CommandId,
                NodeId = final.NodeId,
                Status = final.Status,
                Error = final.Error,
                Attempts = final.Attempts
            };
        }
    }
}
