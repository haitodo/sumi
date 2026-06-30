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
        public static readonly string FolderPath;
        private static readonly string FilePath; // 互換性・移行用
        private static readonly string WindowDatPath;
        private static readonly string WindowDatTempPath;
        public static readonly string NotesFolderPath;
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
        public static double FontSize { get; set; } = 11.0;
        public static double LineSpacing { get; set; } = 0.9;
        public static double ParagraphSpacing { get; set; } = 6.0; // 段落間の余白（pt単位）
        public static double Opacity { get; set; } = 50.0; // 0 to 100
        public static string FontWeight { get; set; } = "Light";
        public static string QuitHotKey { get; set; } = "Alt+Q";
        public static string LaunchHotKey { get; set; } = string.Empty;
        /// <summary>
        /// 前回終了時に表示していたメモのID（再起動後の復元用）。
        /// </summary>
        public static string LastNoteId { get; set; } = string.Empty;

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

                                // 高速起動のためコンテンツは空で初期設定（バックグラウンドで遅延読み込み）
                                Notes.Add(new NoteData
                                {
                                    Id = id,
                                    IsPinned = isPinned,
                                    LastOpened = lastOpened,
                                    Content = string.Empty,
                                    Title = "Loading...",
                                    CharCount = 0
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

                        if (File.Exists(rtfFile))
                        {
                            string rtfData = File.ReadAllText(rtfFile, Utf8NoBom);
                            content = RtfToPlainTextConverter.ConvertRtfToPlainText(rtfData);
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
                            latest.Content = content;
                            latest.Title = GetTitleFromContent(content);
                            latest.CharCount = content.Length;
                        }

                        // 残りのメモはバックグラウンドで非同期にロードする
                        _ = Task.Run(() => LoadRemainingNotesBackground());
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

        private static void LoadRemainingNotesBackground()
        {
            try
            {
                NoteData[] notesCopy;
                string currentId;
                lock (Notes)
                {
                    notesCopy = Notes.ToArray();
                    currentId = CurrentNoteId;
                }

                foreach (var note in notesCopy)
                {
                    if (note.Id == currentId) continue;

                    string rtfFile = Path.Combine(NotesFolderPath, $"note_{note.Id}.rtf");
                    string txtFile = Path.Combine(NotesFolderPath, $"note_{note.Id}.txt");
                    string content = string.Empty;

                    if (File.Exists(rtfFile))
                    {
                        string rtfData = File.ReadAllText(rtfFile, Utf8NoBom);
                        content = RtfToPlainTextConverter.ConvertRtfToPlainText(rtfData);
                    }
                    else if (File.Exists(txtFile))
                    {
                        content = File.ReadAllText(txtFile, Utf8NoBom);
                        try
                        {
                            File.WriteAllText(rtfFile, content, Utf8NoBom);
                            File.Delete(txtFile);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[Background Migration Error] {ex.Message}");
                        }
                    }

                    lock (Notes)
                    {
                        // 既に読み込み済みの場合はスキップ（Loading... の未ロードメモのみ更新する）
                        if (note.Title == "Loading...")
                        {
                            note.Content = content;
                            note.Title = GetTitleFromContent(content);
                            note.CharCount = content.Length;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LoadRemainingNotesBackground Error] {ex.Message}");
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
            lock (Notes)
            {
                var note = Notes.Find(n => n.Id == CurrentNoteId);
                return note?.Content ?? string.Empty;
            }
        }

        /// <summary>
        /// 指定されたメモを非同期かつアトミックに保存します。
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
                        note.Title = GetTitleFromContent(plainText);
                        note.CharCount = plainText.Length;
                    }
                }

                string noteFile = Path.Combine(NotesFolderPath, $"note_{id}.rtf");
                string tempFile = Path.Combine(NotesFolderPath, $"note_{id}.tmp");

                using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                using (var writer = new StreamWriter(fs, Utf8NoBom))
                {
                    await writer.WriteAsync(rtfText);
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
                        note.Title = GetTitleFromContent(plainText);
                        note.CharCount = plainText.Length;
                    }
                }

                string noteFile = Path.Combine(NotesFolderPath, $"note_{id}.rtf");
                string tempFile = Path.Combine(NotesFolderPath, $"note_{id}.tmp");

                using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: false))
                using (var writer = new StreamWriter(fs, Utf8NoBom))
                {
                    writer.Write(rtfText);
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
                lock (Notes)
                {
                    foreach (var note in Notes)
                    {
                        sb.AppendLine($"{note.Id}|{note.IsPinned}|{note.LastOpened.Ticks}");
                    }
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
        /// ウィンドウ座標の復元およびマルチモニターを考慮したクランプ処理を行います。
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
                                        // 現在のウィンドウの初期DPIと、移動先モニターのDPIを取得して、DPI遷移によるサイズ変更を打ち消す調整を行う
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

                                        // DPI差分による自動スケーリングを相殺するようにサイズを補正
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
