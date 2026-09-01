using System.Collections.ObjectModel;

namespace sumi
{
    /// <summary>
    /// すべてのタスクビューでノートごとにグループ化して表示するためのビューモデルです。
    /// </summary>
    public class AllTasksGroupViewModel
    {
        public string NoteId { get; }
        public string NoteTitle { get; }
        public ObservableCollection<TaskItemViewModel> Tasks { get; }

        public AllTasksGroupViewModel(string noteId, string noteTitle, ObservableCollection<TaskItemViewModel> tasks)
        {
            NoteId = noteId;
            NoteTitle = noteTitle;
            Tasks = tasks;
        }
    }
}
