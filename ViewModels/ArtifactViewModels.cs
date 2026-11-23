using System.Text.Json.Serialization;

namespace BtoanAiNet.ViewModels
{
    public class ArtifactExportRequestModel
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; } = "docx";

        [JsonPropertyName("title")]
        public string? Title { get; set; } = "Tai_lieu";

        [JsonPropertyName("content")]
        public string? Content { get; set; } = "";
    }

    public class ArtifactItemSpec
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; } = "docx";

        [JsonPropertyName("title")]
        public string? Title { get; set; } = "Tai_lieu";

        [JsonPropertyName("content")]
        public string? Content { get; set; } = "";

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }

    public class ArtifactBundleExportRequestModel
    {
        [JsonPropertyName("bundleName")]
        public string? BundleName { get; set; } = "Artifact_Bundle";

        [JsonPropertyName("artifacts")]
        public List<ArtifactItemSpec> Artifacts { get; set; } = new();
    }

    public class ArtifactExportResultModel
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("filename")]
        public string? Filename { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }

    public class ArtifactBundleResultModel
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("zipUrl")]
        public string? ZipUrl { get; set; }

        [JsonPropertyName("zipFilename")]
        public string? ZipFilename { get; set; }

        [JsonPropertyName("artifacts")]
        public List<ArtifactExportResultModel> Artifacts { get; set; } = new();

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }
}
