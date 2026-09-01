using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace sumi
{
    /// <summary>
    /// マウスポインターが乗ったときにリサイズカーソルとハイライトを表示する Grid です。
    /// </summary>
    public partial class ResizableGrid : Grid
    {
        public ResizableGrid()
        {
            this.PointerEntered += (s, e) =>
            {
                this.ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
                this.Background = new SolidColorBrush(Colors.DimGray);
            };
            this.PointerExited += (s, e) =>
            {
                this.ProtectedCursor = null;
                this.Background = new SolidColorBrush(Colors.Transparent);
            };
        }
    }
}
