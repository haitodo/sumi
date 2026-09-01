using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

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
}
