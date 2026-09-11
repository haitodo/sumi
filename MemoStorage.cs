using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace sumi
{
    /// <summary>
    /// メモテキストおよび設定情報の物理永続化を行うデータアクセス層クラスです。
    /// </summary>
    public static partial class MemoStorage
    {
        public static readonly string FolderPath;
        private static readonly string FilePath; // 互換性・移行用
        private static readonly string WindowDatPath;
        private static readonly string WindowDatTempPath;
        public static readonly string NotesFolderPath;
        private static readonly string NotesDatPath;
        private static readonly string SettingsPath;
        private static readonly string AiPromptsPath;

        // 文字コードのキャッシュ (アロケーション排除)
        private static readonly UTF8Encoding Utf8NoBom = new(false);

        // インメモリの全メモキャッシュ
        public static List<NoteData> Notes { get; } = new();
        public static string CurrentNoteId { get; set; } = string.Empty;

        // 設定の定義（デフォルト値）
        public static string FontFamily { get; set; } = "Noto Sans JP";
        public static string FontWeight { get; set; } = "Light";
        public static double FontSize { get; set; } = 11.0;
        public static double LineSpacing { get; set; } = 0.9;
        public static double ParagraphSpacing { get; set; } = 6.0; // 段落間の余白（pt単位）
        public static double Opacity { get; set; } = 50.0; // 0 to 100
        public static string LaunchHotKey { get; set; } = string.Empty;
        public static string QuitHotKey { get; set; } = "Alt+Q";
        public static string LastNoteId { get; set; } = string.Empty;
        public static bool IsSidebarPinned { get; set; } = false;
        public static bool IsSidebarOpen { get; set; } = false;
        public static double SidebarWidth { get; set; } = 320.0;
        public static bool IsRightSidebarPinned { get; set; } = false;
        public static bool IsRightSidebarOpen { get; set; } = false;
        public static double RightSidebarWidth { get; set; } = 320.0;
        public static string LastSidebarView { get; set; } = "Notes";
        public static string LastRightSidebarView { get; set; } = "JustDoIt";
        public static string LastSelectedTag { get; set; } = string.Empty;
        public static string LastSelectedRightTag { get; set; } = string.Empty;
        public static int RecentNotesCount { get; set; } = 1;
        public static bool ShowDeleteButton { get; set; } = false;

        // AI Settings
        public static string AiApiKey { get; set; } = string.Empty;
        public static string AiModelName { get; set; } = "openai/gpt-4o-mini";
        public static double AiTemperature { get; set; } = 0.7;
        public static int AiMaxTokens { get; set; } = 2000;
        public static string AiSystemPrompt { get; set; } = "あなたは優秀な文章推敲アシスタントです。";
        public static List<AiPromptItem> AiPrompts { get; set; } = new();

        static MemoStorage()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            FolderPath = Path.Combine(localAppData, "sumi");

            // Directory.Existsによる事前確認をはさみ、Directory.CreateDirectory呼び出しのオーバーヘッドを削減
            if (!Directory.Exists(FolderPath))
            {
                Directory.CreateDirectory(FolderPath);
            }

            FilePath = Path.Combine(FolderPath, "memo.txt");
            WindowDatPath = Path.Combine(FolderPath, "window.dat");
            WindowDatTempPath = Path.Combine(FolderPath, "window.tmp");

            NotesFolderPath = Path.Combine(FolderPath, "notes");
            if (!Directory.Exists(NotesFolderPath))
            {
                Directory.CreateDirectory(NotesFolderPath);
            }
            NotesDatPath = Path.Combine(FolderPath, "notes.dat");
            SettingsPath = Path.Combine(FolderPath, "settings.txt");
            AiPromptsPath = Path.Combine(FolderPath, "ai_prompts.json");
        }

        /// <summary>
        /// 全メモから重複のないソート済みのタグ一覧を取得します。
        /// </summary>
        public static List<string> GetAllTags()
        {
            var tagSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            lock (Notes)
            {
                foreach (var note in Notes)
                {
                    foreach (var tag in note.Tags)
                    {
                        tagSet.Add(tag);
                    }
                }
            }
            var sortedTags = new List<string>(tagSet);
            sortedTags.Sort(StringComparer.OrdinalIgnoreCase);
            return sortedTags;
        }
    }
}
