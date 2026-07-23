using System;
using System.IO;
using System.Text;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace sumi
{
    /// <summary>
    /// メモのデータモデルです。
    /// </summary>
    public class NoteData
    {
        public string Id { get; set; } = string.Empty;
        public bool IsPinned { get; set; }
        public DateTime LastOpened { get; set; }
        public string Content { get; set; } = string.Empty;
        public string RtfContent { get; set; } = string.Empty;
        public bool IsRtfLoaded { get; set; }
        public string Title { get; set; } = string.Empty;
        public int CharCount { get; set; }
        public int UncompletedTaskCount { get; set; }
        public ObservableCollection<TaskItemViewModel> Tasks { get; } = new();
        public bool HasLoadedTasks { get; set; }
        public List<string> Tags { get; set; } = new();
    }

    /// <summary>
    /// タスクの物理保存用のデータモデルです。
    /// </summary>
    public class TaskItem
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public bool IsCompleted { get; set; }
        public bool IsJustDoIt { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// タスクアイテムのバインディング用ビューモデルです（Single Source of Truth用）。
    /// </summary>
    public class TaskItemViewModel : System.ComponentModel.INotifyPropertyChanged
    {
        private string _title = string.Empty;
        private bool _isCompleted;
        private bool _isJustDoIt;

        public string Id { get; set; } = string.Empty;
        public string ParentNoteId { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }

        public string Title
        {
            get => _title;
            set
            {
                if (_title != value)
                {
                    _title = value;
                    OnPropertyChanged(nameof(Title));
                    _onChanged?.Invoke();
                }
            }
        }

        public bool IsCompleted
        {
            get => _isCompleted;
            set
            {
                if (_isCompleted != value)
                {
                    _isCompleted = value;
                    OnPropertyChanged(nameof(IsCompleted));
                    _onChanged?.Invoke();
                }
            }
        }

        public bool IsJustDoIt
        {
            get => _isJustDoIt;
            set
            {
                if (_isJustDoIt != value)
                {
                    _isJustDoIt = value;
                    OnPropertyChanged(nameof(IsJustDoIt));
                    _onChanged?.Invoke();
                }
            }
        }

        private readonly Action? _onChanged;

        public TaskItemViewModel(string id, string parentNoteId, string title, bool isCompleted, DateTime createdAt, Action? onChanged)
            : this(id, parentNoteId, title, isCompleted, false, createdAt, onChanged)
        {
        }

        public TaskItemViewModel(string id, string parentNoteId, string title, bool isCompleted, bool isJustDoIt, DateTime createdAt, Action? onChanged)
        {
            Id = id;
            ParentNoteId = parentNoteId;
            _title = title;
            _isCompleted = isCompleted;
            _isJustDoIt = isJustDoIt;
            CreatedAt = createdAt;
            _onChanged = onChanged;
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// Native AOTシリアライズ用のコンテキストクラスです。
    /// </summary>
    [JsonSerializable(typeof(List<TaskItem>))]
    internal partial class TaskJsonContext : JsonSerializerContext
    {
    }

    /// <summary>
    /// AIプロンプトリスト用のNative AOTシリアライズコンテキストです。
    /// </summary>
    [JsonSerializable(typeof(List<AiPromptItem>))]
    [JsonSerializable(typeof(AiPromptItem))]
    internal partial class AiPromptJsonContext : JsonSerializerContext
    {
    }

    /// <summary>
    /// メモテキストおよびウィンドウ位置情報の物理永続化を行うデータアクセス層クラスです。
    /// </summary>
    public static class MemoStorage
    {
        public static readonly string FolderPath;
        private static readonly string FilePath; // 互換性・移行用
        private static readonly string WindowDatPath;
        private static readonly string WindowDatTempPath;
        public static readonly string NotesFolderPath;
        private static readonly string NotesDatPath;
        private static readonly string NotesDatTempPath;
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

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);
        private const uint MONITOR_DEFAULTTONULL = 0;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

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
            NotesDatTempPath = Path.Combine(FolderPath, "notes.tmp");
            SettingsPath = Path.Combine(FolderPath, "settings.txt");
            AiPromptsPath = Path.Combine(FolderPath, "ai_prompts.json");
        }

        /// <summary>
        /// メモ一覧とメタデータの初期化および移行を行います。
        /// </summary>
        public static void InitializeNotes()
        {
            lock (Notes)
            {
                Notes.Clear();
                CurrentNoteId = string.Empty;
            }

            try
            {
                // 1. メタデータファイルが存在しない場合
                if (!File.Exists(NotesDatPath))
                {
                    // 既存の単一 memo.txt があれば移行する
                    if (File.Exists(FilePath))
                    {
                        string content = File.ReadAllText(FilePath, Utf8NoBom);
                        string id = DateTime.UtcNow.Ticks.ToString();

                        var note = new NoteData
                        {
                            Id = id,
                            IsPinned = false,
                            LastOpened = DateTime.UtcNow,
                            Content = content,
                            Title = GetTitleFromContent(content),
                            CharCount = content.Length
                        };
                        lock (Notes)
                        {
                            Notes.Add(note);
                            CurrentNoteId = id;
                        }

                        // 物理ファイル保存
                        SaveNoteTextSync(id, content, content);
                        SaveMetadata();

                        // memo.txt をバックアップに退避
                        try
                        {
                            File.Move(FilePath, FilePath + ".bak", overwrite: true);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[Migration Backup Error] {ex.Message}");
                        }
                    }
                    else
                    {
                        // 何も存在しない場合は空のデフォルトメモを1つ作成
                        CreateNewNote();
                    }
                }
                else
                {
                    // 2. メタデータファイルが存在する場合、読み込む
                    var lines = File.ReadAllLines(NotesDatPath, Utf8NoBom);
                    lock (Notes)
                    {
                        foreach (var line in lines)
                        {
                            if (string.IsNullOrWhiteSpace(line)) continue;
                            var parts = line.Split('|');
                            if (parts.Length >= 3)
                            {
                                string id = parts[0];
                                bool isPinned = bool.Parse(parts[1]);
                                long ticks = long.Parse(parts[2]);
                                var lastOpened = new DateTime(ticks, DateTimeKind.Utc);

                                // 後方互換性に配慮しつつ、保存済みのTitle、CharCount、UncompletedTaskCount、Tagsをパース
                                string title = "Untitled";
                                int charCount = 0;
                                int uncompletedTaskCount = 0;
                                var tags = new List<string>();
                                if (parts.Length >= 5)
                                {
                                    title = parts[3];
                                    if (int.TryParse(parts[4], out int count))
                                    {
                                        charCount = count;
                                    }
                                }
                                if (parts.Length >= 6)
                                {
                                    if (int.TryParse(parts[5], out int taskCount))
                                    {
                                        uncompletedTaskCount = taskCount;
                                    }
                                }
                                if (parts.Length >= 7)
                                {
                                    var tagPart = parts[6];
                                    if (!string.IsNullOrWhiteSpace(tagPart))
                                    {
                                        var tagArray = tagPart.Split(',');
                                        foreach (var t in tagArray)
                                        {
                                            var trimmed = t.Trim();
                                            if (!string.IsNullOrEmpty(trimmed))
                                            {
                                                tags.Add(trimmed);
                                            }
                                        }
                                    }
                                }

                                Notes.Add(new NoteData
                                {
                                    Id = id,
                                    IsPinned = isPinned,
                                    LastOpened = lastOpened,
                                    Content = string.Empty,
                                    Title = title,       // 起動直後に即座に表示可能
                                    CharCount = charCount, // 起動直後に即座に表示可能
                                    UncompletedTaskCount = uncompletedTaskCount,
                                    Tags = tags
                                });
                            }
                        }
                    }

                    // 読み込んだ結果メモが空なら作成
                    bool isEmpty;
                    lock (Notes)
                    {
                        isEmpty = Notes.Count == 0;
                    }

                    if (isEmpty)
                    {
                        CreateNewNote();
                    }
                    else
                    {
                        // 前回終了時に表示していたメモ（LastNoteId）を優先し、
                        // 存在しない場合は LastOpened が最新のメモをカレントに設定
                        NoteData latest;
                        lock (Notes)
                        {
                            NoteData? lastNote = string.IsNullOrEmpty(LastNoteId)
                                ? null
                                : Notes.Find(n => n.Id == LastNoteId);

                            if (lastNote != null)
                            {
                                latest = lastNote;
                            }
                            else
                            {
                                // フォールバック: LastOpened が最も新しいメモを選択
                                latest = Notes[0];
                                foreach (var note in Notes)
                                {
                                    if (note.LastOpened > latest.LastOpened)
                                    {
                                        latest = note;
                                    }
                                }
                            }
                            CurrentNoteId = latest.Id;
                        }

                        // カレントのメモだけ同期でロード（起動時の表示遅延を防ぐ）
                        string rtfFile = Path.Combine(NotesFolderPath, $"note_{latest.Id}.rtf");
                        string txtFile = Path.Combine(NotesFolderPath, $"note_{latest.Id}.txt");
                        string content = string.Empty;
                        string rtfData = string.Empty;

                        if (File.Exists(rtfFile))
                        {
                            rtfData = File.ReadAllText(rtfFile, Utf8NoBom);
#pragma warning disable CS0618
                            content = RtfToPlainTextConverter.ConvertRtfToPlainText(rtfData);
#pragma warning restore CS0618
                        }
                        else if (File.Exists(txtFile))
                        {
                            // 旧形式 (.txt) のメモがある場合は読み出し、.rtf へ自動移行する
                            content = File.ReadAllText(txtFile, Utf8NoBom);
                            try
                            {
                                File.WriteAllText(rtfFile, content, Utf8NoBom);
                                File.Delete(txtFile);
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[Migration Error] {ex.Message}");
                            }
                        }

                        lock (Notes)
                        {
                            latest.RtfContent = rtfData;
                            latest.Content = content;
                            latest.IsRtfLoaded = true;

                            // 自前コンバーターでの解析結果が有効な場合のみ更新し、
                            // 解析不全（Untitled）の場合は notes.dat からロードした正しいタイトルを保護する
                            string parsedTitle = GetTitleFromContent(content);
                            if (parsedTitle != "Untitled")
                            {
                                latest.Title = parsedTitle;
                            }
                            else if (string.IsNullOrEmpty(latest.Title))
                            {
                                latest.Title = "Untitled";
                            }

                            latest.CharCount = content.Length > 0 ? content.Length : latest.CharCount;
                        }

                        // カレントメモのタスクも同期でロード
                        LoadTasksForNoteSync(latest);

                        // 他のメモはオンデマンド（選択時・検索時）で遅延ロード
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[InitializeNotes Error] {ex.Message}");
                // 万一エラーが発生した場合は最低限の空メモを作成して動作継続
                bool isEmpty;
                lock (Notes)
                {
                    isEmpty = Notes.Count == 0;
                }
                if (isEmpty)
                {
                    CreateNewNote();
                }
            }
        }

        /// <summary>
        /// メモの RTF およびプレーンテキストが未ロードの場合に遅延読み込みしてメモリキャッシュに格納します。
        /// </summary>
        public static void EnsureNoteLoaded(NoteData note)
        {
            if (note.IsRtfLoaded) return;

            string rtfFile = Path.Combine(NotesFolderPath, $"note_{note.Id}.rtf");
            string txtFile = Path.Combine(NotesFolderPath, $"note_{note.Id}.txt");
            string rtfData = string.Empty;
            string plainText = string.Empty;

            try
            {
                if (File.Exists(rtfFile))
                {
                    rtfData = File.ReadAllText(rtfFile, Utf8NoBom);
#pragma warning disable CS0618
                    plainText = RtfToPlainTextConverter.ConvertRtfToPlainText(rtfData);
#pragma warning restore CS0618
                }
                else if (File.Exists(txtFile))
                {
                    plainText = File.ReadAllText(txtFile, Utf8NoBom);
                    rtfData = plainText;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[EnsureNoteLoaded Error] {ex.Message}");
            }

            lock (Notes)
            {
                note.RtfContent = rtfData;
                note.Content = plainText;
                note.IsRtfLoaded = true;

                if (string.IsNullOrEmpty(note.Title) || note.Title == "Untitled")
                {
                    string parsedTitle = GetTitleFromContent(plainText);
                    if (parsedTitle != "Untitled")
                    {
                        note.Title = parsedTitle;
                    }
                }
                note.CharCount = plainText.Length > 0 ? plainText.Length : note.CharCount;
            }
        }

        /// <summary>
        /// コンテンツからタイトル（最初の有効なテキスト行）を取得します。
        /// </summary>
        public static string GetTitleFromContent(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return "Untitled";

            using (var reader = new StringReader(content))
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    // 先頭にある空行や空白行をスキップし、最初に現れた有効な文字列をタイトルにする
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        return line.Trim();
                    }
                }
            }
            return "Untitled";
        }


        /// <summary>
        /// 現在のアクティブなメモテキストを同期的に読み込みます。
        /// </summary>
        public static string LoadMemoText()
        {
            lock (Notes)
            {
                var note = Notes.Find(n => n.Id == CurrentNoteId);
                return note?.Content ?? string.Empty;
            }
        }

        /// <summary>
        /// 指定されたノートの RTF テキストを読み込みます。メモリキャッシュが存在する場合は即座に返します。
        /// </summary>
        public static string LoadNoteRtf(string id)
        {
            NoteData? note = null;
            lock (Notes)
            {
                note = Notes.Find(n => n.Id == id);
            }

            if (note != null)
            {
                if (!note.IsRtfLoaded)
                {
                    EnsureNoteLoaded(note);
                }
                return note.RtfContent;
            }

            string rtfFile = Path.Combine(NotesFolderPath, $"note_{id}.rtf");
            string txtFile = Path.Combine(NotesFolderPath, $"note_{id}.txt");

            try
            {
                if (File.Exists(rtfFile))
                {
                    return File.ReadAllText(rtfFile, Utf8NoBom);
                }
                else if (File.Exists(txtFile))
                {
                    return File.ReadAllText(txtFile, Utf8NoBom);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LoadNoteRtf Error] {ex.Message}");
            }
            return string.Empty;
        }

        /// <summary>
        /// 指定されたメモを非同期かつアトミックに保存します。一時的なロックに備えてリトライ処理を行います。
        /// </summary>
        public static async Task<bool> SaveNoteTextAtomicAsync(string id, string plainText, string rtfText)
        {
            try
            {
                lock (Notes)
                {
                    var note = Notes.Find(n => n.Id == id);
                    if (note != null)
                    {
                        note.Content = plainText;
                        note.RtfContent = rtfText;
                        note.IsRtfLoaded = true;
                        note.Title = GetTitleFromContent(plainText);
                        note.CharCount = plainText.Length;
                    }
                }

                string noteFile = Path.Combine(NotesFolderPath, $"note_{id}.rtf");
                string tempFile = Path.Combine(NotesFolderPath, $"note_{id}.tmp");

                int maxRetries = 5;
                int delayMs = 100;
                for (int i = 0; i < maxRetries; i++)
                {
                    try
                    {
                        using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                        using (var writer = new StreamWriter(fs, Utf8NoBom))
                        {
                            await writer.WriteAsync(rtfText);
                            await writer.FlushAsync();
                        }

                        if (File.Exists(noteFile))
                        {
                            File.Replace(tempFile, noteFile, null);
                        }
                        else
                        {
                            File.Move(tempFile, noteFile);
                        }

                        return true;
                    }
                    catch (IOException ex) when (i < maxRetries - 1)
                    {
                        Debug.WriteLine($"[SaveNoteTextAtomicAsync Retry {i + 1}] IOException: {ex.Message}");
                        await Task.Delay(delayMs);
                        delayMs *= 2;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SaveNoteText Error] {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 指定されたメモを同期かつアトミックに保存します（終了時用）。一時的なロックに備えてリトライ処理を行います。
        /// </summary>
        public static bool SaveNoteTextSync(string id, string plainText, string rtfText)
        {
            try
            {
                lock (Notes)
                {
                    var note = Notes.Find(n => n.Id == id);
                    if (note != null)
                    {
                        note.Content = plainText;
                        note.RtfContent = rtfText;
                        note.IsRtfLoaded = true;
                        note.Title = GetTitleFromContent(plainText);
                        note.CharCount = plainText.Length;
                    }
                }

                string noteFile = Path.Combine(NotesFolderPath, $"note_{id}.rtf");
                string tempFile = Path.Combine(NotesFolderPath, $"note_{id}.tmp");

                int maxRetries = 5;
                int delayMs = 100;
                for (int i = 0; i < maxRetries; i++)
                {
                    try
                    {
                        using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: false))
                        using (var writer = new StreamWriter(fs, Utf8NoBom))
                        {
                            writer.Write(rtfText);
                            writer.Flush();
                        }

                        if (File.Exists(noteFile))
                        {
                            File.Replace(tempFile, noteFile, null);
                        }
                        else
                        {
                            File.Move(tempFile, noteFile);
                        }

                        return true;
                    }
                    catch (IOException ex) when (i < maxRetries - 1)
                    {
                        Debug.WriteLine($"[SaveNoteTextSync Retry {i + 1}] IOException: {ex.Message}");
                        System.Threading.Thread.Sleep(delayMs);
                        delayMs *= 2;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SaveNoteTextSync Error] {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// メタデータ一覧をアトミックに物理保存します。
        /// </summary>
        public static void SaveMetadata()
        {
            try
            {
                var sb = new StringBuilder();
                lock (Notes)
                {
                    foreach (var note in Notes)
                    {
                        // Title、CharCount、UncompletedTaskCount、Tagsをメタデータに含めて保存します
                        // タグ内のカンマやパイプ文字を排除するために事前に置換・エスケープ処理
                        var sanitizedTags = new List<string>();
                        foreach (var tag in note.Tags)
                        {
                            var s = tag.Replace("|", "_").Replace(",", "_").Trim();
                            if (!string.IsNullOrEmpty(s))
                            {
                                sanitizedTags.Add(s);
                            }
                        }
                        sb.AppendLine($"{note.Id}|{note.IsPinned}|{note.LastOpened.Ticks}|{note.Title}|{note.CharCount}|{note.UncompletedTaskCount}|{string.Join(",", sanitizedTags)}");
                    }
                }
                byte[] bytes = Utf8NoBom.GetBytes(sb.ToString());

                using (var fs = new FileStream(NotesDatTempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: false))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush();
                    fs.Flush(true);
                }

                if (File.Exists(NotesDatPath))
                {
                    File.Replace(NotesDatTempPath, NotesDatPath, null);
                }
                else
                {
                    File.Move(NotesDatTempPath, NotesDatPath);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SaveMetadata Error] {ex.Message}");
            }
        }

        /// <summary>
        /// 新規メモを作成してインメモリキャッシュに追加し、物理保存します。
        /// </summary>
        public static NoteData CreateNewNote()
        {
            string id = DateTime.UtcNow.Ticks.ToString();
            var note = new NoteData
            {
                Id = id,
                IsPinned = false,
                LastOpened = DateTime.UtcNow,
                Content = string.Empty,
                Title = "Untitled",
                CharCount = 0
            };

            lock (Notes)
            {
                Notes.Add(note);
                CurrentNoteId = id;
            }

            // 物理保存
            SaveNoteTextSync(id, string.Empty, string.Empty);
            SaveMetadata();

            return note;
        }

        /// <summary>
        /// メモを削除し、関連ファイルも削除します。
        /// </summary>
        public static void DeleteNote(string id)
        {
            bool removed = false;
            lock (Notes)
            {
                var note = Notes.Find(n => n.Id == id);
                if (note != null)
                {
                    Notes.Remove(note);
                    removed = true;
                }
            }

            if (removed)
            {
                string noteFile = Path.Combine(NotesFolderPath, $"note_{id}.rtf");
                string tempFile = Path.Combine(NotesFolderPath, $"note_{id}.tmp");

                try
                {
                    if (File.Exists(noteFile)) File.Delete(noteFile);
                    if (File.Exists(tempFile)) File.Delete(tempFile);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[DeleteNote Files Error] {ex.Message}");
                }

                SaveMetadata();
            }
        }

        /// <summary>
        /// アクティブなメモを切り替え、最終開封日時を更新します。
        /// </summary>
        public static void SetCurrentNote(string id, bool updateLastOpened = true)
        {
            lock (Notes)
            {
                var note = Notes.Find(n => n.Id == id);
                if (note != null)
                {
                    if (updateLastOpened)
                        note.LastOpened = DateTime.UtcNow;
                    CurrentNoteId = id;
                }
            }
            SaveMetadata();
        }

        /// <summary>
        /// 完全にアロケーションフリーなウィンドウ座標保存 (スタック上で処理) を行います。
        /// </summary>
        public static void SaveWindowPlacementAtomic(int x, int y, int width, int height)
        {
            try
            {
                // BinaryWriterなどのオブジェクト確保を完全に排したスタック配列書き込み
                Span<byte> buffer = stackalloc byte[16];
                BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(0, 4), x);
                BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(4, 4), y);
                BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(8, 4), width);
                BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(12, 4), height);

                using (var fs = new FileStream(WindowDatTempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(buffer);
                }

                if (File.Exists(WindowDatPath))
                {
                    File.Replace(WindowDatTempPath, WindowDatPath, null);
                }
                else
                {
                    File.Move(WindowDatTempPath, WindowDatPath);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Window Save Error] {ex.Message}");
            }
        }

        /// <summary>
        /// ウィンドウ座標の復元およびマルチモニターを考慮したクランプ処理、DPI自動スケーリングの相殺処理を行います。
        /// </summary>
        public static bool LoadWindowPlacement(IntPtr hWnd, out int x, out int y, out int width, out int height)
        {
            x = 0; y = 0; width = 360; height = 480; // デフォルト値
            try
            {
                if (File.Exists(WindowDatPath))
                {
                    Span<byte> buffer = stackalloc byte[16];
                    using (var fs = new FileStream(WindowDatPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        if (fs.Length >= 16)
                        {
                            int read = fs.Read(buffer);
                            if (read >= 16)
                            {
                                int lx = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(0, 4));
                                int ly = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(4, 4));
                                int lw = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(8, 4));
                                int lh = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(12, 4));

                                RECT rect = new RECT { Left = lx, Top = ly, Right = lx + lw, Bottom = ly + lh };
                                IntPtr hMonitor = MonitorFromRect(ref rect, MONITOR_DEFAULTTONULL);

                                if (hMonitor != IntPtr.Zero)
                                {
                                    // ワークエリア情報（タスクバーを除いた領域）の取得
                                    MONITORINFO info = new MONITORINFO();
                                    info.cbSize = Marshal.SizeOf<MONITORINFO>();
                                    if (GetMonitorInfo(hMonitor, ref info))
                                    {
                                        // 【重要】
                                        // ウィンドウが画面に表示される前の初期化段階では、GetDpiForWindow は標準の 96 DPI を返します。
                                        // その後、対象モニターに配置される際、OSによって自動的に「targetDpi / initialDpi」倍にリサイズされます。
                                        // 起動時の二重スケーリングを防ぐため、事前にこの拡大比率の逆数を掛けてサイズを補正します。
                                        uint initialDpi = 96;
                                        try
                                        {
                                            initialDpi = GetDpiForWindow(hWnd);
                                        }
                                        catch (Exception)
                                        {
                                            initialDpi = 96;
                                        }

                                        uint targetDpi = 96;
                                        try
                                        {
                                            if (GetDpiForMonitor(hMonitor, 0, out uint dpiX, out uint dpiY) == 0)
                                            {
                                                targetDpi = dpiX;
                                            }
                                        }
                                        catch (Exception)
                                        {
                                            targetDpi = 96;
                                        }

                                        if (initialDpi == 0) initialDpi = 96;
                                        if (targetDpi == 0) targetDpi = 96;

                                        // DPIの差分がある場合、OSによる自動スケーリングを事前に相殺する
                                        if (initialDpi != targetDpi)
                                        {
                                            lw = (int)Math.Round(lw * ((double)initialDpi / targetDpi));
                                            lh = (int)Math.Round(lh * ((double)initialDpi / targetDpi));
                                        }

                                        // 座標がワークエリアからはみ出ている場合は、安全にワークエリア内に収まるようクランプ
                                        x = Math.Clamp(lx, info.rcWork.Left, info.rcWork.Right - lw);
                                        y = Math.Clamp(ly, info.rcWork.Top, info.rcWork.Bottom - lh);
                                        width = lw;
                                        height = lh;
                                        return true;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Window Load Error] {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// 設定をロードします。存在しない場合はデフォルト値を使用します。
        /// </summary>
        public static void LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var lines = File.ReadAllLines(SettingsPath, Utf8NoBom);
                    foreach (var line in lines)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var parts = line.Split('=', 2);
                        if (parts.Length == 2)
                        {
                            string key = parts[0].Trim();
                            string val = parts[1].Trim();
                            switch (key)
                            {
                                case "FontFamily":
                                    FontFamily = val;
                                    break;
                                case "FontWeight":
                                    FontWeight = val;
                                    break;
                                case "FontSize":
                                    if (double.TryParse(val, out double fs)) FontSize = fs;
                                    break;
                                case "LineSpacing":
                                    if (double.TryParse(val, out double ls)) LineSpacing = Math.Clamp(ls, 0.5, 4.0);
                                    break;
                                case "ParagraphSpacing":
                                    if (double.TryParse(val, out double ps)) ParagraphSpacing = ps;
                                    break;
                                case "Opacity":
                                    if (double.TryParse(val, out double op)) Opacity = op;
                                    break;
                                case "QuitHotKey":
                                    QuitHotKey = val;
                                    break;
                                case "LaunchHotKey":
                                    LaunchHotKey = val;
                                    break;
                                case "LastNoteId":
                                    LastNoteId = val;
                                    break;
                                case "IsSidebarPinned":
                                    if (bool.TryParse(val, out bool pinned)) IsSidebarPinned = pinned;
                                    break;
                                case "IsSidebarOpen":
                                    if (bool.TryParse(val, out bool open)) IsSidebarOpen = open;
                                    break;
                                case "SidebarWidth":
                                    if (double.TryParse(val, out double w)) SidebarWidth = Math.Clamp(w, 200, 600);
                                    break;
                                case "IsRightSidebarPinned":
                                    if (bool.TryParse(val, out bool rpinned)) IsRightSidebarPinned = rpinned;
                                    break;
                                case "IsRightSidebarOpen":
                                    if (bool.TryParse(val, out bool ropen)) IsRightSidebarOpen = ropen;
                                    break;
                                case "RightSidebarWidth":
                                    if (double.TryParse(val, out double rw)) RightSidebarWidth = Math.Clamp(rw, 200, 600);
                                    break;
                                case "LastSidebarView":
                                    LastSidebarView = val;
                                    break;
                                case "LastRightSidebarView":
                                    LastRightSidebarView = val;
                                    break;
                                case "LastSelectedTag":
                                    LastSelectedTag = val;
                                    break;
                                case "LastSelectedRightTag":
                                    LastSelectedRightTag = val;
                                    break;
                                case "RecentNotesCount":
                                    if (int.TryParse(val, out int rnc)) RecentNotesCount = Math.Max(0, rnc);
                                    break;
                                case "ShowDeleteButton":
                                    if (bool.TryParse(val, out bool sdb)) ShowDeleteButton = sdb;
                                    break;
                                case "AiApiKey":
                                    AiApiKey = val;
                                    break;
                                case "AiModelName":
                                    AiModelName = val;
                                    break;
                                case "AiTemperature":
                                    if (double.TryParse(val, out double temp)) AiTemperature = temp;
                                    break;
                                case "AiMaxTokens":
                                    if (int.TryParse(val, out int tokens)) AiMaxTokens = tokens;
                                    break;
                                case "AiSystemPrompt":
                                    AiSystemPrompt = val.Replace("\\n", "\n");
                                    break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LoadSettings Error] {ex.Message}");
            }
            LoadAiPrompts();
        }

        /// <summary>
        /// 設定をアトミックに保存します。
        /// </summary>
        public static void SaveSettings()
        {
            try
            {
                // CurrentNoteId が確定している場合は LastNoteId を常に最新に保つ
                // （タイマー経由の呼び出しでも確実に現在のメモIDが保存されるようにする）
                if (!string.IsNullOrEmpty(CurrentNoteId))
                {
                    LastNoteId = CurrentNoteId;
                }

                var sb = new StringBuilder();
                sb.AppendLine($"FontFamily={FontFamily}");
                sb.AppendLine($"FontWeight={FontWeight}");
                sb.AppendLine($"FontSize={FontSize}");
                sb.AppendLine($"LineSpacing={LineSpacing}");
                sb.AppendLine($"ParagraphSpacing={ParagraphSpacing}");
                sb.AppendLine($"Opacity={Opacity}");
                sb.AppendLine($"QuitHotKey={QuitHotKey}");
                sb.AppendLine($"LaunchHotKey={LaunchHotKey}");
                sb.AppendLine($"LastNoteId={LastNoteId}");
                sb.AppendLine($"IsSidebarPinned={IsSidebarPinned}");
                sb.AppendLine($"IsSidebarOpen={IsSidebarOpen}");
                sb.AppendLine($"SidebarWidth={SidebarWidth}");
                sb.AppendLine($"IsRightSidebarPinned={IsRightSidebarPinned}");
                sb.AppendLine($"IsRightSidebarOpen={IsRightSidebarOpen}");
                sb.AppendLine($"RightSidebarWidth={RightSidebarWidth}");
                sb.AppendLine($"LastSidebarView={LastSidebarView}");
                sb.AppendLine($"LastRightSidebarView={LastRightSidebarView}");
                sb.AppendLine($"LastSelectedTag={LastSelectedTag}");
                sb.AppendLine($"LastSelectedRightTag={LastSelectedRightTag}");
                sb.AppendLine($"RecentNotesCount={RecentNotesCount}");
                sb.AppendLine($"ShowDeleteButton={ShowDeleteButton}");
                sb.AppendLine($"AiApiKey={AiApiKey}");
                sb.AppendLine($"AiModelName={AiModelName}");
                sb.AppendLine($"AiTemperature={AiTemperature}");
                sb.AppendLine($"AiMaxTokens={AiMaxTokens}");
                sb.AppendLine($"AiSystemPrompt={AiSystemPrompt.Replace("\r", "").Replace("\n", "\\n")}");
                byte[] bytes = Utf8NoBom.GetBytes(sb.ToString());

                string tempPath = SettingsPath + ".tmp";
                using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: false))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush();
                    fs.Flush(true); // 物理フラッシュ
                }

                if (File.Exists(SettingsPath))
                {
                    File.Replace(tempPath, SettingsPath, null);
                    using (var fs = new FileStream(SettingsPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                    {
                        fs.Flush(true);
                    }
                }
                else
                {
                    File.Move(tempPath, SettingsPath);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SaveSettings Error] {ex.Message}");
            }
        }

        public static Action<string>? TaskChangedAction { get; set; }

        public static void LoadTasksForNoteSync(NoteData note)
        {
            if (note.HasLoadedTasks) return;

            string tasksFile = Path.Combine(NotesFolderPath, $"note_{note.Id}.tasks");
            if (File.Exists(tasksFile))
            {
                try
                {
                    string json = File.ReadAllText(tasksFile, Utf8NoBom);
                    var items = JsonSerializer.Deserialize(json, TaskJsonContext.Default.ListTaskItem);
                    if (items != null)
                    {
                        lock (note.Tasks)
                        {
                            foreach (var item in items)
                            {
                                var vm = new TaskItemViewModel(
                                    item.Id,
                                    note.Id,
                                    item.Title,
                                    item.IsCompleted,
                                    item.IsJustDoIt,
                                    item.CreatedAt,
                                    () => TaskChangedAction?.Invoke(note.Id)
                                );
                                note.Tasks.Add(vm);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[LoadTasksForNoteSync Error] {ex.Message}");
                }
            }
            note.HasLoadedTasks = true;
        }

        public static async Task<bool> SaveTasksAtomicAsync(string id, List<TaskItem> tasks)
        {
            try
            {
                string tasksFile = Path.Combine(NotesFolderPath, $"note_{id}.tasks");
                string tempFile = Path.Combine(NotesFolderPath, $"note_{id}.tasks.tmp");

                string json = JsonSerializer.Serialize(tasks, TaskJsonContext.Default.ListTaskItem);

                int maxRetries = 5;
                int delayMs = 100;
                for (int i = 0; i < maxRetries; i++)
                {
                    try
                    {
                        using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                        using (var writer = new StreamWriter(fs, Utf8NoBom))
                        {
                            await writer.WriteAsync(json);
                            await writer.FlushAsync();
                            fs.Flush(true);
                        }

                        if (File.Exists(tasksFile))
                        {
                            File.Replace(tempFile, tasksFile, null);
                            using (var fs = new FileStream(tasksFile, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                            {
                                fs.Flush(true);
                            }
                        }
                        else
                        {
                            File.Move(tempFile, tasksFile);
                        }

                        return true;
                    }
                    catch (IOException ex) when (i < maxRetries - 1)
                    {
                        Debug.WriteLine($"[SaveTasksAtomicAsync Retry {i + 1}] IOException: {ex.Message}");
                        await Task.Delay(delayMs);
                        delayMs *= 2;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SaveTasksAtomicAsync Error] {ex.Message}");
                return false;
            }
        }

        public static bool SaveTasksSync(string id, List<TaskItem> tasks)
        {
            try
            {
                string tasksFile = Path.Combine(NotesFolderPath, $"note_{id}.tasks");
                string tempFile = Path.Combine(NotesFolderPath, $"note_{id}.tasks.tmp");

                string json = JsonSerializer.Serialize(tasks, TaskJsonContext.Default.ListTaskItem);

                int maxRetries = 5;
                int delayMs = 100;
                for (int i = 0; i < maxRetries; i++)
                {
                    try
                    {
                        using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: false))
                        using (var writer = new StreamWriter(fs, Utf8NoBom))
                        {
                            writer.Write(json);
                            writer.Flush();
                            fs.Flush(true);
                        }

                        if (File.Exists(tasksFile))
                        {
                            File.Replace(tempFile, tasksFile, null);
                            using (var fs = new FileStream(tasksFile, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                            {
                                fs.Flush(true);
                            }
                        }
                        else
                        {
                            File.Move(tempFile, tasksFile);
                        }

                        return true;
                    }
                    catch (IOException) when (i < maxRetries - 1)
                    {
                        System.Threading.Thread.Sleep(delayMs);
                        delayMs *= 2;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SaveTasksSync Error] {ex.Message}");
                return false;
            }
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

    public class AiPromptItem
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
    }
}
