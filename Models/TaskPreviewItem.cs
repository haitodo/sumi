using System.ComponentModel;

namespace sumi
{
    /// <summary>
    /// AIタスク生成プレビュー用のビューモデルです。
    /// </summary>
    public class TaskPreviewItem : INotifyPropertyChanged
    {
        private string _title = string.Empty;
        private bool _isSelected = true;

        public string Title
        {
            get => _title;
            set
            {
                if (_title != value)
                {
                    _title = value;
                    OnPropertyChanged(nameof(Title));
                }
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged(nameof(IsSelected));
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
