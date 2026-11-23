using BtoanAiNet.ViewModels;

namespace BtoanAiNet.Services
{
    public interface IArtifactGeneratorService
    {
        Task<ArtifactExportResultModel> GenerateArtifactAsync(string type, string title, string content, string webRootPath);
        Task<ArtifactBundleResultModel> GenerateBundleAsync(ArtifactBundleExportRequestModel bundleRequest, string webRootPath);
    }
}
