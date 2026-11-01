using Microsoft.AspNetCore.Mvc;
using RIoT2.Net.Orchestrator.Services.Backup;

namespace RIoT2.Net.Orchestrator.Controllers
{
    /// <summary>
    /// Configuration backup and restore (design 7.4). A backup holds node configurations with device
    /// credentials and the Matter keys, so treat the files as secrets.
    /// </summary>
    [ApiController]
    [Route("api/v2")]
    public class BackupController : ControllerBase
    {
        private const string ZipType = "application/zip";

        private readonly BackupService _backups;

        public BackupController(BackupService backups)
        {
            _backups = backups;
        }

        /// <summary>Makes a backup of the current configuration and returns it as a zip.</summary>
        [HttpPost("backup")]
        [ProducesResponseType<Stream>(StatusCodes.Status200OK, ZipType)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
        public async Task<IActionResult> CreateAsync(CancellationToken cancellationToken)
        {
            try
            {
                var (name, content) = await _backups.CreateAsync(cancellationToken);
                return File(content, ZipType, name);
            }
            catch (BackupBusyException x)
            {
                return Problem(x.Message, statusCode: StatusCodes.Status409Conflict);
            }
        }

        /// <summary>
        /// Restores a backup sent as the request body (<c>Content-Type: application/zip</c>). The
        /// replaced configuration is saved to <c>Backups/</c> first. 400 lists every problem with the
        /// archive; nothing is changed then.
        /// </summary>
        [HttpPost("restore")]
        [RequestSizeLimit(BackupArchive.MaxArchiveBytes)]
        [BinaryRequestBody(ZipType)]
        [ProducesResponseType<RestoreResult>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)]
        public async Task<IActionResult> RestoreAsync(CancellationToken cancellationToken)
        {
            var archive = await ReadBodyAsync(cancellationToken);
            if (archive == null)
                return Problem($"A backup is at most {BackupArchive.MaxArchiveBytes / (1024 * 1024)} MB.", statusCode: StatusCodes.Status413PayloadTooLarge);
            return await RestoreAsync(archive, cancellationToken);
        }

        /// <summary>The backup schedule, the last scheduled backup, and the backups in <c>Backups/</c>.</summary>
        [HttpGet("backups")]
        [ProducesResponseType<BackupSummary>(StatusCodes.Status200OK)]
        public ActionResult<BackupSummary> GetBackups() => _backups.GetSummary();

        /// <summary>Downloads a backup from <c>Backups/</c>.</summary>
        [HttpGet("backups/{name}")]
        [ProducesResponseType<Stream>(StatusCodes.Status200OK, ZipType)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult GetBackup(string name)
        {
            var path = _backups.Find(name);
            return path == null ? NotFound($"No backup {name}") : PhysicalFile(path, ZipType, name);
        }

        /// <summary>Restores a backup from <c>Backups/</c>, as <c>POST /api/v2/restore</c> does.</summary>
        [HttpPost("backups/{name}/restore")]
        [ProducesResponseType<RestoreResult>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
        public async Task<IActionResult> RestoreStoredAsync(string name, CancellationToken cancellationToken)
        {
            var path = _backups.Find(name);
            if (path == null)
                return NotFound($"No backup {name}");
            return await RestoreAsync(await System.IO.File.ReadAllBytesAsync(path, cancellationToken), cancellationToken);
        }

        private async Task<IActionResult> RestoreAsync(byte[] archive, CancellationToken cancellationToken)
        {
            try
            {
                return Ok(await _backups.RestoreAsync(archive, cancellationToken));
            }
            catch (BackupValidationException x)
            {
                var problem = new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "The backup can't be restored",
                    Detail = string.Join(" ", x.Errors)
                };
                problem.Extensions["errors"] = x.Errors;
                return BadRequest(problem);
            }
            catch (BackupBusyException x)
            {
                return Problem(x.Message, statusCode: StatusCodes.Status409Conflict);
            }
        }

        private async Task<byte[]> ReadBodyAsync(CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await Request.Body.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > BackupArchive.MaxArchiveBytes)
                    return null;
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
    }
}
