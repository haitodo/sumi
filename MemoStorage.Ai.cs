using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace sumi
{
    /// <summary>
    /// AIプロンプトリスト用のNative AOTシリアライズコンテキストです。
    /// </summary>
    [JsonSerializable(typeof(List<AiPromptItem>))]
    [JsonSerializable(typeof(AiPromptItem))]
    internal partial class AiPromptJsonContext : JsonSerializerContext
    {
    }

    public static partial class MemoStorage
    {
        public static void LoadAiPrompts()
        {
            try
            {
                if (File.Exists(AiPromptsPath))
                {
                    string json = File.ReadAllText(AiPromptsPath, Utf8NoBom);
                    // Native AOT対応: ソースジェネレーターベースのコンテキストを使用
                    var list = JsonSerializer.Deserialize(json, AiPromptJsonContext.Default.ListAiPromptItem);
                    if (list != null)
                    {
                        AiPrompts = list;
                    }
                }

                if (AiPrompts == null || AiPrompts.Count == 0)
                {
                    AiPrompts = GetDefaultAiPrompts();
                    SaveAiPrompts();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LoadAiPrompts Error] {ex.Message}");
                if (AiPrompts == null || AiPrompts.Count == 0)
                {
                    AiPrompts = GetDefaultAiPrompts();
                }
            }
        }

        public static void SaveAiPrompts()
        {
            try
            {
                // Native AOT対応: ソースジェネレーターベースのコンテキストを使用
                string json = JsonSerializer.Serialize(AiPrompts, AiPromptJsonContext.Default.ListAiPromptItem);
                File.WriteAllText(AiPromptsPath, json, Utf8NoBom);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SaveAiPrompts Error] {ex.Message}");
            }
        }

        private static List<AiPromptItem> GetDefaultAiPrompts()
        {
            return new List<AiPromptItem>
            {
                new AiPromptItem { Id = Guid.NewGuid().ToString(), Name = "要約する", Prompt = "選択されたテキストの内容を簡潔に要約してください。" },
                new AiPromptItem { Id = Guid.NewGuid().ToString(), Name = "推敲・校正", Prompt = "選択されたテキストの誤字脱字を修正し、自然な日本語に推敲してください。" },
                new AiPromptItem { Id = Guid.NewGuid().ToString(), Name = "丁寧なビジネス表現", Prompt = "選択されたテキストを、ビジネスシーンで使える丁寧な敬語表現に書き換えてください。" },
                new AiPromptItem { Id = Guid.NewGuid().ToString(), Name = "カジュアルな表現", Prompt = "選択されたテキストを、親しみやすいカジュアルな口調に書き換えてください。" }
            };
        }
    }
}
