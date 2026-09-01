using System;

namespace sumi
{
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
}
