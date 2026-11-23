using BtoanAiNet.Services;
using BtoanAiNet.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace BtoanAiNet.Controllers
{
    public class FileController : Controller
    {
        private readonly IStorageService _storageService;
        private readonly IArtifactGeneratorService _artifactGeneratorService;
        private readonly IWebHostEnvironment _env;

        public FileController(
            IStorageService storageService,
            IArtifactGeneratorService artifactGeneratorService,
            IWebHostEnvironment env)
        {
            _storageService = storageService;
            _artifactGeneratorService = artifactGeneratorService;
            _env = env;
        }

        // POST /ai/api/upload.php & /File/Upload
        [HttpPost]
        [Route("ai/api/upload.php")]
        [Route("File/Upload")]
        public async Task<IActionResult> Upload([FromBody] FileUploadRequestModel model)
        {
            if (model == null || string.IsNullOrWhiteSpace(model.Content))
                return Json(new { error = "no content" });

            string webRootPath = _env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
            string ext = (model.Ext ?? "txt").ToLowerInvariant().Trim();

            // Use high-fidelity ArtifactGeneratorService for docx, pdf, xlsx, pptx
            if (ext == "docx" || ext == "pdf" || ext == "xlsx" || ext == "pptx" || ext == "md" || ext == "html" || ext == "txt")
            {
                var artifact = await _artifactGeneratorService.GenerateArtifactAsync(ext, "Tài liệu xuất", model.Content, webRootPath);
                if (artifact.Success && !string.IsNullOrEmpty(artifact.Url))
                {
                    return Json(new { url = artifact.Url });
                }
            }

            // Fallback to legacy storage service if needed
            var result = await _storageService.ExportFileAsync(model, webRootPath);
            return Content(result.ToJsonString(), "application/json");
        }

        // POST /api/file/export-artifact
        [HttpPost]
        [Route("api/file/export-artifact")]
        public async Task<IActionResult> ExportArtifact([FromBody] ArtifactExportRequestModel model)
        {
            if (model == null || string.IsNullOrWhiteSpace(model.Content))
                return BadRequest(new { error = "Content is required" });

            string webRootPath = _env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
            var result = await _artifactGeneratorService.GenerateArtifactAsync(
                model.Type ?? "docx",
                model.Title ?? "Tài liệu",
                model.Content,
                webRootPath);

            if (!result.Success)
            {
                return StatusCode(500, new { error = result.Error ?? "Export failed" });
            }

            return Ok(result);
        }

        // POST /api/file/export-bundle
        [HttpPost]
        [Route("api/file/export-bundle")]
        public async Task<IActionResult> ExportBundle([FromBody] ArtifactBundleExportRequestModel model)
        {
            if (model == null || model.Artifacts == null || model.Artifacts.Count == 0)
                return BadRequest(new { error = "Artifacts list is required" });

            string webRootPath = _env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
            var result = await _artifactGeneratorService.GenerateBundleAsync(model, webRootPath);

            if (!result.Success)
            {
                return StatusCode(500, new { error = result.Error ?? "Bundle export failed" });
            }

            return Ok(result);
        }

        // GET /ai/api/download_proxy.php
        [HttpGet]
        [Route("ai/api/download_proxy.php")]
        [Route("File/DownloadProxy")]
        public async Task<IActionResult> DownloadProxy([FromQuery] string url, [FromQuery] string filename = "downloaded_file")
        {
            if (string.IsNullOrEmpty(url)) return BadRequest("Missing URL");

            var (stream, contentType, downloadFilename) = await _storageService.GetDownloadProxyStreamAsync(url, filename);
            if (stream == null) return NotFound("Could not open source file.");

            return File(stream, contentType, downloadFilename);
        }
    }
}
