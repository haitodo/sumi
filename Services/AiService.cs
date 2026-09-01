using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json.Serialization;

namespace sumi.Services
{
    /// <summary>
    /// AI API のチャットメッセージ型です。
    /// </summary>
    public class AiMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;
    }

    /// <summary>
    /// AI API リクエスト本文用の型です。
    /// </summary>
    public class AiRequestBody
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("messages")]
        public List<AiMessage> Messages { get; set; } = new();

        [JsonPropertyName("temperature")]
        public double Temperature { get; set; }

        [JsonPropertyName("max_tokens")]
        public int MaxTokens { get; set; }

        [JsonPropertyName("stream")]
        public bool Stream { get; set; }
    }

    /// <summary>
    /// AI API リクエスト用の Native AOT JSON コンテキストです。
    /// </summary>
    [JsonSerializable(typeof(AiRequestBody))]
    [JsonSerializable(typeof(List<AiMessage>))]
    [JsonSerializable(typeof(AiMessage))]
    public partial class AiJsonContext : JsonSerializerContext
    {
    }

    /// <summary>
    /// AI 連携機能の共通クライアントです。
    /// </summary>
    public static class AiService
    {
        private static readonly HttpClient _httpClient = new();

        public static HttpClient HttpClient => _httpClient;
    }
}
