using System;
using System.ComponentModel;

namespace sumi
{
    /// <summary>
    /// タスクアイテムのバインディング用ビューモデルです（Single Source of Truth用）。
    /// </summary>
    public class TaskItemViewModel : INotifyPropertyChanged
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

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
