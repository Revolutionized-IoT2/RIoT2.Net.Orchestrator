using Microsoft.AspNetCore.Mvc;
using QRCoder;
using RIoT2.Net.Orchestrator.Services.Matter;

namespace RIoT2.Net.Orchestrator.Controllers
{
    /// <summary>
    /// The REST surface the RIoT UI's Matter view uses to configure the Control Bridge, present its
    /// onboarding codes and inspect the endpoints it exposes.
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    public class MatterController : ControllerBase
    {
        private IMatterBridgeService _bridge;

        public MatterController(IMatterBridgeService matterBridgeService)
        {
            _bridge = matterBridgeService;
        }

        /// <summary>Returns the bridge state: onboarding codes, commissioned fabrics and bridged endpoints.</summary>
        [HttpGet("status")]
        public IActionResult GetStatus()
        {
            return new OkObjectResult(_bridge.GetStatus());
        }

        [HttpGet("configuration")]
        public IActionResult GetConfiguration()
        {
            return new OkObjectResult(_bridge.Configuration);
        }

        /// <summary>Saves the configuration and restarts the bridge so the new identity takes effect.</summary>
        [HttpPost("configuration")]
        public async Task<IActionResult> SaveConfigurationAsync([FromBody] MatterConfiguration configuration)
        {
            if (configuration == null)
                return BadRequest("A Matter configuration is required.");

            try
            {
                await _bridge.SaveConfigurationAsync(configuration);
                return new OkObjectResult(_bridge.GetStatus());
            }
            catch (Exception x)
            {
                return StatusCode(500, x.Message);
            }
        }

        /// <summary>
        /// Renders the onboarding payload as a PNG so the user can scan it with the Google Home app.
        /// </summary>
        [HttpGet("qr")]
        public IActionResult GetQrCode()
        {
            var status = _bridge.GetStatus();
            if (string.IsNullOrEmpty(status.QrCode))
                return NotFound("The Matter bridge is not running, so it has no onboarding payload.");

            using var generator = new QRCodeGenerator();
            // Level M is what the Matter specification recommends for the onboarding QR.
            using var data = generator.CreateQrCode(status.QrCode, QRCodeGenerator.ECCLevel.M);
            var png = new PngByteQRCode(data).GetGraphic(10);

            return File(png, "image/png");
        }

        /// <summary>Re-opens the pairing window so the bridge can be added to another ecosystem.</summary>
        [HttpGet("commissioning/open")]
        public IActionResult OpenCommissioning()
        {
            try
            {
                _bridge.OpenCommissioningWindow();
                return new OkObjectResult(_bridge.GetStatus());
            }
            catch (Exception x)
            {
                return StatusCode(500, x.Message);
            }
        }

        /// <summary>Recomposes the bridged endpoints from the current node configuration.</summary>
        [HttpGet("devices/refresh")]
        public async Task<IActionResult> RefreshDevicesAsync()
        {
            try
            {
                await _bridge.RefreshDevicesAsync();
                return new OkObjectResult(_bridge.GetStatus());
            }
            catch (Exception x)
            {
                return StatusCode(500, x.Message);
            }
        }

        /// <summary>
        /// Decommissions the bridge: every fabric and the current onboarding codes are dropped, and the
        /// bridge comes back with a new pairing code.
        /// </summary>
        [HttpGet("reset")]
        public async Task<IActionResult> ResetAsync()
        {
            try
            {
                await _bridge.ResetAsync();
                return new OkObjectResult(_bridge.GetStatus());
            }
            catch (Exception x)
            {
                return StatusCode(500, x.Message);
            }
        }
    }
}
