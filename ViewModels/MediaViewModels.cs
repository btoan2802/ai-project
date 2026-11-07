using System.Text.Json;
using System.Text.Json.Serialization;

namespace BtoanAiNet.ViewModels
{
    public class FlexibleStringConverter : JsonConverter<string?>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number)
            {
                if (reader.TryGetInt64(out long l)) return l.ToString();
                if (reader.TryGetDouble(out double d)) return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            if (reader.TokenType == JsonTokenType.String)
            {
                return reader.GetString();
            }
            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }
            if (reader.TokenType == JsonTokenType.True) return "true";
            if (reader.TokenType == JsonTokenType.False) return "false";
            return reader.GetString();
        }

        public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
        {
            if (value == null) writer.WriteNullValue();
            else writer.WriteStringValue(value);
        }
    }

    public class ImageGenerationRequestModel
    {
        [JsonPropertyName("prompt")]
        public string? Prompt { get; set; }

        [JsonPropertyName("conversation_id")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public int? Conversation_Id { get; set; }

        [JsonPropertyName("user_id")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public int? User_Id { get; set; }

        [JsonPropertyName("assistant_text")]
        public string? Assistant_Text { get; set; } = "Đây là ảnh của bạn:";

        [JsonPropertyName("aspect_ratio")]
        public string? Aspect_Ratio { get; set; } = "1:1";
    }

    public class EditImageRequestModel
    {
        [JsonPropertyName("prompt")]
        public string? Prompt { get; set; }

        [JsonPropertyName("image")]
        public string? Image { get; set; } // Base64 or local URL

        [JsonPropertyName("conversation_id")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public int? Conversation_Id { get; set; }

        [JsonPropertyName("user_id")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public int? User_Id { get; set; }

        [JsonPropertyName("assistant_text")]
        public string? Assistant_Text { get; set; } = "Đây là ảnh đã chỉnh sửa của bạn:";
    }

    public class VideoGenerationRequestModel
    {
        [JsonPropertyName("prompt")]
        public string? Prompt { get; set; }

        [JsonPropertyName("conversation_id")]
        [JsonConverter(typeof(FlexibleStringConverter))]
        public string? Conversation_Id { get; set; } = "default";

        [JsonPropertyName("user_id")]
        [JsonConverter(typeof(FlexibleStringConverter))]
        public string? User_Id { get; set; } = "unknown";

        [JsonPropertyName("chat_id")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public int? Chat_Id { get; set; }

        [JsonPropertyName("image")]
        public string? Image { get; set; }

        [JsonPropertyName("duration")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public int Duration { get; set; } = 8;
    }

    public class MusicGenerationRequestModel
    {
        [JsonPropertyName("user_id")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public int? User_Id { get; set; }

        [JsonPropertyName("conversation_id")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public int? Conversation_Id { get; set; }

        [JsonPropertyName("chat_id")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public int? Chat_Id { get; set; }

        [JsonPropertyName("prompt")]
        public string? Prompt { get; set; }

        [JsonPropertyName("duration")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public int Duration { get; set; } = 30;
    }

    public class FileUploadRequestModel
    {
        public string? Content { get; set; }
        public string? Ext { get; set; } = "txt";
    }
}
