using RIoT2.Core.Models;
using RIoT2.Net.Orchestrator.Models;

namespace RIoT2.Net.Orchestrator.Services.Templates
{
    /// <summary>
    /// The report, command and variable templates of the stored configuration, shaped with the node
    /// and device that own them. The one place that builds the template listings of the REST API.
    /// </summary>
    public interface ITemplateCatalog
    {
        /// <summary>Every device report template, in node and device order.</summary>
        IReadOnlyList<NodeReportTemplate> GetReportTemplates();

        /// <summary>Every device command template, in node and device order.</summary>
        IReadOnlyList<NodeCommandTemplate> GetCommandTemplates();

        /// <summary>Every variable, shaped as a template owned by the orchestrator.</summary>
        IReadOnlyList<VariableTemplate> GetVariableTemplates();

        /// <summary>The device report template with the given id, or <see langword="null"/>.</summary>
        NodeReportTemplate FindReportTemplate(string id);
    }
}
