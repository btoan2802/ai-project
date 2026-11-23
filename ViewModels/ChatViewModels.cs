using System.Text.Json.Nodes;

namespace BtoanAiNet.ViewModels
{
    public class ChatRequestModel
    {
        public string? Model { get; set; } = "gemini-3.5-flash";
        public JsonObject? Payload { get; set; }
        public int? User_Id { get; set; }
        public int? Conversation_Id { get; set; }
        public string? Intent { get; set; }
        public List<ChatAttachmentModel>? Attachments { get; set; }

        public int UserId { get => User_Id ?? 0; set => User_Id = value; }
        public int ConversationId { get => Conversation_Id ?? 0; set => Conversation_Id = value; }
        public string? Question => Payload?["question"]?.ToString();
    }

    public class ChatAttachmentModel
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string FileName { get; set; } = "";
        public string ContentType { get; set; } = "";
        public long FileSize { get; set; }
        public string? RelativePath { get; set; }
        public string? Url { get; set; }
        public string? Base64Data { get; set; }
        public string? TextContent { get; set; }
    }

    public class ChatArtifactModel
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Title { get; set; } = "";
        public string FileName { get; set; } = "";
        public string FileType { get; set; } = "docx";
        public long FileSize { get; set; }
        public string? RelativePath { get; set; }
        public string? DownloadUrl { get; set; }
        public string? Content { get; set; }
    }

    public class RenameConversationRequestModel
    {
        public int Id { get; set; }
        public string? Title { get; set; }
        public bool? IsAuto { get; set; }
    }

    public class DeleteConversationRequestModel
    {
        public int Id { get; set; }
    }

    public class SaveMemoryRequestModel
    {
        public int User_Id { get; set; }
        public string? Memory { get; set; }
    }

    public class SaveTrainingRequestModel
    {
        public string? Question { get; set; }
        public string? Ai_Answer { get; set; }
        public string? Correct_Answer { get; set; }
        public int User_Id { get; set; }
    }
}
