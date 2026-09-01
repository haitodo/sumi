using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace sumi
{
    /// <summary>
    /// Native AOTシリアライズ用のコンテキストクラスです。
    /// </summary>
    [JsonSerializable(typeof(List<TaskItem>))]
    internal partial class TaskJsonContext : JsonSerializerContext
    {
    }

    public static partial class MemoStorage
    {
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
    }
}
