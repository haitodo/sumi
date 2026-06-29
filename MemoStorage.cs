using System;
using System.IO;
using System.Text;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Buffers.Binary;

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
        public string Title { get; set; } = string.Empty;
        public int CharCount { get; set; }
    }

    /// <summary>
    /// メモテキストおよびウィンドウ位置情報の物理永続化を行うデータアクセス層クラスです。
    /// </summary>
    public static class MemoStorage
    {
        private static readonly string FolderPath;
        private static readonly string FilePath; // 互換性・移行用
        private static readonly string WindowDatPath;
        private static readonly string WindowDatTempPath;
        private static readonly string NotesFolderPath;
        private static readonly string NotesDatPath;
        private static readonly string NotesDatTempPath;
        private static readonly string SettingsPath;

        // 文字コードのキャッシュ (アロケーション排除)
        private static readonly UTF8Encoding Utf8NoBom = new(false);

        // インメモリの全メモキャッシュ
        public static List<NoteData> Notes { get; } = new();
        public static string CurrentNoteId { get; set; } = string.Empty;

        // 設定の定義（デフォルト値）
        public static string FontFamily { get; set; } = "Noto Sans JP";
        public static double FontSize { get; set; } = 14.0;
        public static double LineSpacing { get; set; } = 1.0;
        public static double Opacity { get; set; } = 93.0; // 0 to 100
        public static string FontWeight { get; set; } = "Medium";

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
        }

        /// <summary>
        /// メモ一覧とメタデータの初期化および移行を行います。
        /// </summary>
        public static void InitializeNotes()
        {
            Notes.Clear();
            CurrentNoteId = string.Empty;

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
                        Notes.Add(note);
                        CurrentNoteId = id;

                        // 物理ファイル保存
                        SaveNoteTextSync(id, content);
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

                            string noteFile = Path.Combine(NotesFolderPath, $"note_{id}.txt");
                            string content = string.Empty;
                            if (File.Exists(noteFile))
                            {
                                content = File.ReadAllText(noteFile, Utf8NoBom);
                            }

                            Notes.Add(new NoteData
                            {
                                Id = id,
                                IsPinned = isPinned,
                                LastOpened = lastOpened,
                                Content = content,
                                Title = GetTitleFromContent(content),
                                CharCount = content.Length
                            });
                        }
                    }

                    // 読み込んだ結果メモが空なら作成
                    if (Notes.Count == 0)
                    {
                        CreateNewNote();
                    }
                    else
                    {
                        // 最も新しく開いたメモをカレントに設定
                        var latest = Notes[0];
                        foreach (var note in Notes)
                        {
                            if (note.LastOpened > latest.LastOpened)
                            {
                                latest = note;
                            }
                        }
                        CurrentNoteId = latest.Id;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[InitializeNotes Error] {ex.Message}");
                // 万一エラーが発生した場合は最低限の空メモを作成して動作継続
                if (Notes.Count == 0)
                {
                    CreateNewNote();
                }
            }
        }

        /// <summary>
        /// コンテンツからタイトル（最初の行）を取得します。
        /// </summary>
        public static string GetTitleFromContent(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return "Untitled";
            
            using (var reader = new StringReader(content))
            {
                string? firstLine = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(firstLine))
                    return "Untitled";
                return firstLine.Trim();
            }
        }

        /// <summary>
        /// 現在のアクティブなメモテキストを同期的に読み込みます。
        /// </summary>
        public static string LoadMemoText()
        {
            var note = Notes.Find(n => n.Id == CurrentNoteId);
            return note?.Content ?? string.Empty;
        }

        /// <summary>
        /// 現在のアクティブなメモテキストをアトミックに非同期保存します。
        /// </summary>
        public static async Task<bool> SaveMemoTextAtomicAsync(string text)
        {
            if (string.IsNullOrEmpty(CurrentNoteId)) return false;
            return await SaveNoteTextAtomicAsync(CurrentNoteId, text);
        }

        /// <summary>
        /// 現在のアクティブなメモテキストをアトミックに同期保存します。
        /// </summary>
        public static bool SaveMemoTextAtomicSync(string text)
        {
            if (string.IsNullOrEmpty(CurrentNoteId)) return false;
            return SaveNoteTextSync(CurrentNoteId, text);
        }

        /// <summary>
        /// 指定されたメモを非同期かつアトミックに保存します。
        /// </summary>
        public static async Task<bool> SaveNoteTextAtomicAsync(string id, string text)
        {
            try
            {
                var note = Notes.Find(n => n.Id == id);
                if (note != null)
                {
                    note.Content = text;
                    note.Title = GetTitleFromContent(text);
                    note.CharCount = text.Length;
                }

                string noteFile = Path.Combine(NotesFolderPath, $"note_{id}.txt");
                string tempFile = Path.Combine(NotesFolderPath, $"note_{id}.tmp");

                using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                using (var writer = new StreamWriter(fs, Utf8NoBom))
                {
                    await writer.WriteAsync(text);
                    await writer.FlushAsync();
                    fs.Flush(true); // 物理フラッシュ
                }

                if (File.Exists(noteFile))
                {
                    File.Replace(tempFile, noteFile, null);
                    using (var fs = new FileStream(noteFile, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                    {
                        fs.Flush(true);
                    }
                }
                else
                {
                    File.Move(tempFile, noteFile);
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SaveNoteText Error] {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 指定されたメモを同期かつアトミックに保存します（終了時用）。
        /// </summary>
        public static bool SaveNoteTextSync(string id, string text)
        {
            try
            {
                var note = Notes.Find(n => n.Id == id);
                if (note != null)
                {
                    note.Content = text;
                    note.Title = GetTitleFromContent(text);
                    note.CharCount = text.Length;
                }

                string noteFile = Path.Combine(NotesFolderPath, $"note_{id}.txt");
                string tempFile = Path.Combine(NotesFolderPath, $"note_{id}.tmp");

                using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: false))
                using (var writer = new StreamWriter(fs, Utf8NoBom))
                {
                    writer.Write(text);
                    writer.Flush();
                    fs.Flush(true);
                }

                if (File.Exists(noteFile))
                {
                    File.Replace(tempFile, noteFile, null);
                    using (var fs = new FileStream(noteFile, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                    {
                        fs.Flush(true);
                    }
                }
                else
                {
                    File.Move(tempFile, noteFile);
                }

                return true;
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
                foreach (var note in Notes)
                {
                    sb.AppendLine($"{note.Id}|{note.IsPinned}|{note.LastOpened.Ticks}");
                }
                byte[] bytes = Utf8NoBom.GetBytes(sb.ToString());

                using (var fs = new FileStream(NotesDatTempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: false))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush();
                    fs.Flush(true); // 物理フラッシュ
                }

                if (File.Exists(NotesDatPath))
                {
                    File.Replace(NotesDatTempPath, NotesDatPath, null);
                    using (var fs = new FileStream(NotesDatPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                    {
                        fs.Flush(true);
                    }
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

            Notes.Add(note);
            CurrentNoteId = id;

            // 物理保存
            SaveNoteTextSync(id, string.Empty);
            SaveMetadata();

            return note;
        }

        /// <summary>
        /// メモを削除し、関連ファイルも削除します。
        /// </summary>
        public static void DeleteNote(string id)
        {
            var note = Notes.Find(n => n.Id == id);
            if (note != null)
            {
                Notes.Remove(note);
                
                string noteFile = Path.Combine(NotesFolderPath, $"note_{id}.txt");
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
        public static void SetCurrentNote(string id)
        {
            var note = Notes.Find(n => n.Id == id);
            if (note != null)
            {
                note.LastOpened = DateTime.UtcNow;
                CurrentNoteId = id;
                SaveMetadata();
            }
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
        /// ウィンドウ座標の復元およびマルチモニターを考慮したクランプ処理を行います。
        /// </summary>
        public static bool LoadWindowPlacement(out int x, out int y, out int width, out int height)
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
                                    if (double.TryParse(val, out double ls)) LineSpacing = Math.Max(1.0, ls);
                                    break;
                                case "Opacity":
                                    if (double.TryParse(val, out double op)) Opacity = op;
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
        }

        /// <summary>
        /// 設定をアトミックに保存します。
        /// </summary>
        public static void SaveSettings()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"FontFamily={FontFamily}");
                sb.AppendLine($"FontWeight={FontWeight}");
                sb.AppendLine($"FontSize={FontSize}");
                sb.AppendLine($"LineSpacing={LineSpacing}");
                sb.AppendLine($"Opacity={Opacity}");
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
    }
}
