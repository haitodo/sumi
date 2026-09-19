using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace sumi
{
    /// <summary>
    /// メモアイテムのリストバインディング用のビューモデルです。
    /// </summary>
    public class NoteItemViewModel
    {
        private static readonly Brush PinForegroundPinned = new SolidColorBrush(Colors.White);
        private static readonly Brush PinForegroundUnpinned = new SolidColorBrush(ColorHelper.FromArgb(255, 204, 204, 204));
        private static readonly Brush BackgroundBrushHighlighted = new SolidColorBrush(ColorHelper.FromArgb(30, 255, 176, 0));
        private static readonly Brush BackgroundBrushTransparent = new SolidColorBrush(Colors.Transparent);
        private static readonly Brush BackgroundBrushCurrent = new SolidColorBrush(ColorHelper.FromArgb(28, 138, 184, 245));

        public string Id { get; }
        public string Title { get; }
        public string Subtitle { get; }
        public bool IsPinned { get; }
        public bool IsCurrent { get; }
        public bool IsHighlighted { get; }
        public string PinToolTip => IsPinned ? "ピン留め解除" : "ピン留め";
        public Brush PinForeground => IsPinned ? PinForegroundPinned : PinForegroundUnpinned;
        public Visibility CurrentIndicatorVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;
        public Visibility PinnedFillVisibility => IsPinned ? Visibility.Visible : Visibility.Collapsed;
        public Visibility DeleteButtonVisibility => MemoStorage.ShowDeleteButton ? Visibility.Visible : Visibility.Collapsed;
        public Brush BackgroundBrush => IsHighlighted ? BackgroundBrushHighlighted : (IsCurrent ? BackgroundBrushCurrent : BackgroundBrushTransparent);

        public NoteItemViewModel(string id, string title, string subtitle, bool isPinned, bool isCurrent, bool isHighlighted)
        {
            Id = id;
            Title = title;
            Subtitle = subtitle;
            IsPinned = isPinned;
            IsCurrent = isCurrent;
            IsHighlighted = isHighlighted;
        }
    }
}
