namespace sumi
{
    /// <summary>
    /// AIプロンプトリスト用のデータモデルです。
    /// </summary>
    public class AiPromptItem
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
    }
}
