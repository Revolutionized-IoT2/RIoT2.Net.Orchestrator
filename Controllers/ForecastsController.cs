using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RIoT2.Core.Models;
using RIoT2.Core.Security;
using RIoT2.Net.Orchestrator.Services.Forecasts;
using CoreJson = RIoT2.Core.Utils.Json;

namespace RIoT2.Net.Orchestrator.Controllers
{
    /// <summary>
    /// Forecasts (ADR 0019): the latest series of every report whose value is a forecast, such as a weather forecast or
    /// day-ahead prices, and windows of them with their figures.
    /// </summary>
    [ApiController]
    [Route("api/v2/forecasts")]
    [Authorize(Policy = SecurityPolicies.Viewer)]
    public class ForecastsController(IForecastStore forecasts) : ControllerBase
    {
        /// <summary>The reports with a forecast series, without their points.</summary>
        [HttpGet]
        [ProducesResponseType<IReadOnlyList<ForecastInfo>>(StatusCodes.Status200OK)]
        public IActionResult List() => Ok(forecasts.List());

        /// <summary>
        /// A window of the report's latest series: <c>?window=all|today|tomorrow|next|range</c> (default <c>all</c>), <c>hours</c>
        /// with <c>next</c> (1-384, default 24), <c>from</c> and <c>to</c> (ISO 8601) with <c>range</c>, and <c>rank</c> (0-100) for
        /// the lowest and highest points. Today and tomorrow are the orchestrator's local days.
        /// </summary>
        [HttpGet("{id}")]
        [ProducesResponseType<ForecastSummary>(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult Get(string id, [FromQuery] string? window, [FromQuery] int? hours, [FromQuery] string? from, [FromQuery] string? to, [FromQuery] int? rank)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 200)
                return BadRequest("The id must be 1-200 characters.");
            if (!TryTime(from, out var start) || !TryTime(to, out var end))
                return BadRequest("from and to must be ISO 8601 times, such as 2026-11-05T06:00:00Z.");
            ForecastSummary? summary;
            try
            {
                summary = forecasts.Query(id, window, hours, start, end, rank ?? 0);
            }
            catch (ArgumentException x)
            {
                return BadRequest(x.Message.Split(" (Parameter", 2)[0]);
            }
            if (summary == null)
                return NotFound($"The report '{id}' has no forecast.");
            // Core's contract JSON, as the golden file http/forecast-summary.json pins it.
            return Content(CoreJson.Serialize(summary)!, "application/json");
        }

        private static bool TryTime(string? text, out DateTimeOffset? time)
        {
            time = null;
            if (string.IsNullOrWhiteSpace(text))
                return true;
            if (!DateTimeOffset.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                return false;
            time = parsed;
            return true;
        }
    }
}
