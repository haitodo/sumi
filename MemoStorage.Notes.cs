using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace sumi
{
    public static partial class MemoStorage
    {
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
    }
}
