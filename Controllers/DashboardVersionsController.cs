using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RIoT2.Core.Security;
using RIoT2.Net.Orchestrator.Services.Dashboard;

namespace RIoT2.Net.Orchestrator.Controllers
{
    /// <summary>
    /// The dashboard's card catalog, which the UI uploads for the MCP dashboard tools, and the dashboard's versions with their
    /// restore (I60). Reads need Viewer; uploading the catalog and restoring a version need Admin, like saving the dashboard.
    /// </summary>
    [ApiController]
    [Route("api/v2/dashboard")]
    [Tags("Dashboard")]
    [Authorize(Policy = SecurityPolicies.Viewer)]
    public class DashboardVersionsController(DashboardCardCatalogService catalogs, DashboardVersionService versions) : ControllerBase
    {
        /// <summary>The stored card catalog; 204 when no UI has uploaded one.</summary>
        [HttpGet("cards")]
        [ProducesResponseType<DashboardCardCatalog>(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public IActionResult GetCards() => catalogs.Get() is { } catalog ? Ok(catalog) : NoContent();

        /// <summary>Stores the UI's card catalog; 409 when the stored one is from a newer UI.</summary>
        [HttpPut("cards")]
        [Authorize(Policy = SecurityPolicies.Admin)]
        [RequestSizeLimit(2_000_000)]
        [ProducesResponseType<DashboardCardCatalogSummary>(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public IActionResult SaveCards([FromBody] DashboardCardCatalog catalog)
        {
            var result = catalogs.Save(catalog);
            if (result.Catalog != null)
                return Ok(new DashboardCardCatalogSummary(result.Catalog.UiVersion, result.Catalog.Hash, result.Catalog.Cards.Count, result.Catalog.UploadedAt));
            return result.Conflict ? Conflict(new { errors = result.Errors }) : BadRequest(new { errors = result.Errors });
        }

        /// <summary>The dashboard's versions, newest first (the newest is the dashboard now), without their dashboards.</summary>
        [HttpGet("versions")]
        [ProducesResponseType<IReadOnlyList<DashboardVersionInfo>>(StatusCodes.Status200OK)]
        public IActionResult ListVersions() => Ok(versions.List().Select(DashboardVersionInfo.From).ToList());

        /// <summary>One version with its dashboard.</summary>
        [HttpGet("versions/{id}")]
        [ProducesResponseType<DashboardVersion>(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult GetVersion(string id) => versions.Get(id) is { } version ? Ok(version) : NotFound();

        /// <summary>Makes a version's dashboard the dashboard again, which records a new version; returns that version.</summary>
        [HttpPost("versions/{id}/restore")]
        [Authorize(Policy = SecurityPolicies.Admin)]
        [ProducesResponseType<DashboardVersionInfo>(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public IActionResult Restore(string id)
        {
            try
            {
                return versions.Restore(id) is { } latest ? Ok(DashboardVersionInfo.From(latest)) : NotFound();
            }
            catch (InvalidOperationException x)
            {
                return Problem(x.Message);
            }
        }
    }

    /// <summary>A stored card catalog: the UI version and hash that the UI compares with its own, and the number of card kinds.</summary>
    public sealed record DashboardCardCatalogSummary(string UiVersion, string Hash, int Cards, DateTimeOffset UploadedAt);
}
