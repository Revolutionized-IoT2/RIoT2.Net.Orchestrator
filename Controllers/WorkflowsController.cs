using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Models;
using RIoT2.Core.Security;
using RIoT2.Elsa.Workflows.Management;
using RIoT2.Elsa.Workflows.Models;
using RIoT2.Net.Orchestrator.Services.Forecasts;
using RIoT2.Net.Orchestrator.Services.Templates;
using RIoT2.Net.Orchestrator.Services.Workflows;

namespace RIoT2.Net.Orchestrator.Controllers
{
    /// <summary>
    /// The workflow designer's API (ADR 0016), the same in both workflow modes: the workflows as Elsa definition models,
    /// their versions, saving, publishing, importing and exporting them, the runs, the activity catalog and the RIoT
    /// templates the selectors offer. Reads need Viewer, changes Admin, cancelling a run Operator. While a remote workflow
    /// host is unreachable the API answers 503.
    /// </summary>
    [ApiController]
    [Route("api/workflows")]
    [Authorize(Policy = SecurityPolicies.Viewer)]
    [WorkflowEngineErrors]
    public class WorkflowsController(IWorkflowEngine engine, ITemplateCatalog templates, IForecastStore? forecasts = null) : ControllerBase
    {
        [HttpGet]
        [ProducesResponseType<IReadOnlyList<WorkflowSummary>>(StatusCodes.Status200OK)]
        public async Task<IActionResult> List(CancellationToken cancellationToken) => Ok(await engine.ListAsync(cancellationToken));

        /// <summary>Where workflows run (<c>RIOT2_WORKFLOW_MODE</c>) and whether the engine is reachable.</summary>
        [HttpGet("status")]
        [ProducesResponseType<WorkflowEngineStatus>(StatusCodes.Status200OK)]
        public async Task<IActionResult> Status(CancellationToken cancellationToken) => Ok(await engine.GetStatusAsync(cancellationToken));

        /// <summary>The activities the designer offers, with their inputs, outputs and outcomes.</summary>
        [HttpGet("catalog")]
        public async Task<IActionResult> Catalog(CancellationToken cancellationToken) =>
            Content((await engine.GetCatalogAsync(cancellationToken)).ToJsonString(), "application/json");

        /// <summary>
        /// The report, command and variable templates a <c>riot-output-selector</c> input offers:
        /// <c>?types=Report,Command,Variable</c> (default all), in node, device and name order.
        /// </summary>
        [HttpGet("templates")]
        [ProducesResponseType<IReadOnlyList<WorkflowTemplateItem>>(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public IActionResult Templates([FromQuery] string? types)
        {
            var wanted = new HashSet<TemplateType>();
            foreach (var name in (types ?? "Report,Command,Variable").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Enum.TryParse<TemplateType>(name, true, out var type) || type == TemplateType.Unknown)
                    return BadRequest($"Unknown template type '{name}'; the types are Report, Command and Variable.");
                wanted.Add(type);
            }

            var items = new List<WorkflowTemplateItem>();
            if (wanted.Contains(TemplateType.Report))
            {
                var series = (forecasts?.List() ?? []).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
                items.AddRange(templates.GetReportTemplates().Select(x => WorkflowTemplateItem.From(x, x.Node, x.Device, TemplateType.Report,
                    x.Schema?.Semantic == ValueSemantics.Forecast || series.Contains(x.Id))));
            }
            if (wanted.Contains(TemplateType.Command))
                items.AddRange(templates.GetCommandTemplates().Select(x => WorkflowTemplateItem.From(x, x.Node, x.Device, TemplateType.Command)));
            if (wanted.Contains(TemplateType.Variable))
                items.AddRange(templates.GetVariableTemplates().Select(x => WorkflowTemplateItem.From(x, x.Node, x.Device, TemplateType.Variable)));
            return Ok(items.OrderBy(x => x.Node, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Device, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList());
        }

        [HttpGet("runs")]
        [ProducesResponseType<WorkflowRunsPage>(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> Runs([FromQuery] string? definitionId, [FromQuery] string? status, [FromQuery] int page = 0,
            [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default)
        {
            if (page < 0 || pageSize is < 1 or > WorkflowManagementService.MaxPageSize)
                return BadRequest($"page must be 0 or more and pageSize 1-{WorkflowManagementService.MaxPageSize}.");
            return Ok(await engine.ListRunsAsync(definitionId, status, page, pageSize, cancellationToken));
        }

        [HttpGet("runs/{instanceId}")]
        [ProducesResponseType<WorkflowRunDetails>(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Run(string instanceId, CancellationToken cancellationToken) =>
            await engine.GetRunAsync(instanceId, cancellationToken) is { } run ? Ok(run) : NotFound();

        [HttpPost("runs/{instanceId}/cancel")]
        [Authorize(Policy = SecurityPolicies.Operator)]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> CancelRun(string instanceId, CancellationToken cancellationToken) =>
            await engine.CancelRunAsync(instanceId, cancellationToken) ? NoContent() : NotFound();

        /// <summary>Creates a workflow from a definition model, and publishes it with <c>"publish": true</c>.</summary>
        [HttpPost]
        [Authorize(Policy = SecurityPolicies.Admin)]
        [ProducesResponseType<WorkflowSaveResult>(StatusCodes.Status200OK)]
        [ProducesResponseType<WorkflowSaveResult>(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> Create([FromBody] SaveWorkflowRequest request, CancellationToken cancellationToken) =>
            Saved(await engine.SaveAsync(null, Model(request), request.Publish, cancellationToken));

        /// <summary>Imports an Elsa export or a template (<c>templates/</c> of RIoT2.Elsa).</summary>
        [HttpPost("import")]
        [Authorize(Policy = SecurityPolicies.Admin)]
        [ProducesResponseType<WorkflowSaveResult>(StatusCodes.Status200OK)]
        [ProducesResponseType<WorkflowSaveResult>(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> Import([FromBody] SaveWorkflowRequest request, CancellationToken cancellationToken) =>
            Saved(await engine.ImportAsync(Model(request), request.Publish, cancellationToken));

        /// <summary>The latest version, or <c>?version=</c> a given one.</summary>
        [HttpGet("{definitionId}")]
        [ProducesResponseType<WorkflowDetails>(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Get(string definitionId, [FromQuery] int? version, CancellationToken cancellationToken) =>
            await engine.GetAsync(definitionId, version, cancellationToken) is { } workflow ? Ok(workflow) : NotFound();

        [HttpGet("{definitionId}/versions")]
        [ProducesResponseType<IReadOnlyList<WorkflowVersion>>(StatusCodes.Status200OK)]
        public async Task<IActionResult> Versions(string definitionId, CancellationToken cancellationToken) =>
            Ok(await engine.ListVersionsAsync(definitionId, cancellationToken));

        [HttpGet("{definitionId}/export")]
        [ProducesResponseType<WorkflowExport>(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Export(string definitionId, CancellationToken cancellationToken) =>
            await engine.ExportAsync(definitionId, cancellationToken) is { } export ? Ok(export) : NotFound();

        /// <summary>Saves the workflow's draft (a new version when the latest is published), and publishes it with <c>"publish": true</c>.</summary>
        [HttpPut("{definitionId}")]
        [Authorize(Policy = SecurityPolicies.Admin)]
        [ProducesResponseType<WorkflowSaveResult>(StatusCodes.Status200OK)]
        [ProducesResponseType<WorkflowSaveResult>(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> Save(string definitionId, [FromBody] SaveWorkflowRequest request, CancellationToken cancellationToken) =>
            Saved(await engine.SaveAsync(definitionId, Model(request), request.Publish, cancellationToken));

        [HttpPost("{definitionId}/publish")]
        [Authorize(Policy = SecurityPolicies.Admin)]
        [ProducesResponseType<WorkflowSaveResult>(StatusCodes.Status200OK)]
        [ProducesResponseType<WorkflowSaveResult>(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Publish(string definitionId, CancellationToken cancellationToken) =>
            await engine.PublishAsync(definitionId, cancellationToken) is { } result ? Saved(result) : NotFound();

        /// <summary>Unpublishes the workflow, so its triggers stop.</summary>
        [HttpPost("{definitionId}/retract")]
        [Authorize(Policy = SecurityPolicies.Admin)]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Retract(string definitionId, CancellationToken cancellationToken) =>
            await engine.RetractAsync(definitionId, cancellationToken) ? NoContent() : NotFound();

        [HttpDelete("{definitionId}")]
        [Authorize(Policy = SecurityPolicies.Admin)]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Delete(string definitionId, CancellationToken cancellationToken) =>
            await engine.DeleteAsync(definitionId, cancellationToken) ? NoContent() : NotFound();

        private IActionResult Saved(WorkflowSaveResult result) => result.Succeeded ? Ok(result) : BadRequest(result);

        private static JsonObject Model(SaveWorkflowRequest request) =>
            request?.Model ?? throw new ArgumentException("The request needs the workflow's definition model.");
    }

    /// <summary>
    /// A report, command or variable a RIoT selector offers; the designer stores it in the input's literal. <see cref="Forecast"/>
    /// (1.23.0) marks a report whose value is a forecast series, for <c>RIoT Forecast</c>'s selector.
    /// </summary>
    public sealed record WorkflowTemplateItem(string Id, string Name, Core.ValueType Type, string? Node, string? Device, object? Model, TemplateType TemplateType,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] bool? Forecast = null)
    {
        public static WorkflowTemplateItem From(ITemplate template, string? node, string? device, TemplateType templateType, bool forecast = false) =>
            new(template.Id, template.Name, template.Type, node, device, template.Model, templateType, forecast ? true : null);
    }

    /// <summary>Answers 503 while the workflow engine is unreachable, and 400 for an invalid argument.</summary>
    [AttributeUsage(AttributeTargets.Class)]
    internal sealed class WorkflowEngineErrorsAttribute : ExceptionFilterAttribute
    {
        public override void OnException(ExceptionContext context)
        {
            switch (context.Exception)
            {
                case WorkflowEngineUnavailableException unavailable:
                    context.Result = new ObjectResult(new ProblemDetails
                    {
                        Status = StatusCodes.Status503ServiceUnavailable,
                        Title = "The workflow engine is unavailable.",
                        Detail = unavailable.Message
                    })
                    { StatusCode = StatusCodes.Status503ServiceUnavailable };
                    context.ExceptionHandled = true;
                    break;
                case ArgumentException invalid:
                    context.Result = new BadRequestObjectResult(invalid.Message);
                    context.ExceptionHandled = true;
                    break;
            }
        }
    }
}
