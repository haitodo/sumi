using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using sumi.Interop;

namespace sumi
{
    public sealed partial class MainWindow : Window, IDisposable
    {
        // 検索・置換パネルのドラッグ移動用変数
        private bool _isDraggingFindPanel = false;
        private Windows.Foundation.Point _dragStartPoint;
        private double _dragStartX;
        private double _dragStartY;
        private double _flyoutCurrentX;
        private double _flyoutCurrentY;

        // 検索一致件数・インデックス追跡用変数
        private List<int> _matchStartPositions = new();
        private int _currentMatchIndex = -1;

        private void FindReplacePanel_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            var properties = e.GetCurrentPoint(sender as UIElement).Properties;
            if (properties.IsLeftButtonPressed)
            {
                _isDraggingFindPanel = true;
                if (NativeMethods.GetCursorPos(out NativeMethods.POINT pos))
                {
                    _dragStartPoint = new Windows.Foundation.Point(pos.X, pos.Y);
                }
                _dragStartX = _flyoutCurrentX;
                _dragStartY = _flyoutCurrentY;
                (sender as UIElement)?.CapturePointer(e.Pointer);
                e.Handled = true;
            }
        }

        private void FindReplacePanel_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_isDraggingFindPanel)
            {
                if (NativeMethods.GetCursorPos(out NativeMethods.POINT pos))
                {
                    double dpiScale = RootGrid?.XamlRoot?.RasterizationScale ?? 1.0;
                    double deltaX = (pos.X - _dragStartPoint.X) / dpiScale;
                    double deltaY = (pos.Y - _dragStartPoint.Y) / dpiScale;

                    _flyoutCurrentX = _dragStartX + deltaX;
                    _flyoutCurrentY = _dragStartY + deltaY;

                    SetAnchorPosition(_flyoutCurrentX, _flyoutCurrentY);
                }
                e.Handled = true;
            }
            else
            {
                UpdateCursor(sender, e.OriginalSource);
            }
        }

        private void FindReplacePanel_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_isDraggingFindPanel)
            {
                (sender as UIElement)?.ReleasePointerCapture(e.Pointer);
                _isDraggingFindPanel = false;
                e.Handled = true;
            }
        }

        private void FindReplacePanel_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            UpdateCursor(sender, e.OriginalSource);
        }

        private void FindReplacePanel_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is UIElement el)
            {
                // WinUI3のProtectedCursorはprotectedのためリフレクション経由でリセット
                typeof(UIElement).InvokeMember(
                    "ProtectedCursor",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.SetProperty,
                    null,
                    el,
                    new object?[] { null });
            }
        }

        private void UpdateCursor(object sender, object originalSource)
        {
            if (originalSource is DependencyObject depObj && sender is UIElement el)
            {
                DependencyObject current = depObj;
                bool isInteractive = false;
                while (current != null && current != el)
                {
                    if (current is Button || current is ToggleButton || current is TextBox)
                    {
                        isInteractive = true;
                        break;
                    }
                    current = VisualTreeHelper.GetParent(current);
                }

                var cursor = isInteractive ? null : Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeAll);
                // WinUI3のProtectedCursorはprotectedのためリフレクション経由で設定
                typeof(UIElement).InvokeMember(
                    "ProtectedCursor",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.SetProperty,
                    null,
                    el,
                    new object?[] { cursor });
            }
        }

        private void SetAnchorPosition(double x, double y)
        {
            // Canvas の Left/Top プロパティで位置を更新することで
            // 表示中でもリアルタイムにパネル位置を変更できる
            if (FindReplacePanel != null)
            {
                Canvas.SetLeft(FindReplacePanel, x);
                Canvas.SetTop(FindReplacePanel, y);
            }
        }

        private void RecalculateMatches()
        {
            _matchStartPositions.Clear();
            _currentMatchIndex = -1;

            string target = FindTextBox.Text;
            if (string.IsNullOrEmpty(target))
            {
                FindStatusTextBlock.Text = "";
                FindStatusTextBlock.Visibility = Visibility.Collapsed;
                return;
            }

            var doc = MemoTextBox.Document;
            int docLength = doc.GetRange(0, int.MaxValue).Length;
            var range = doc.GetRange(0, docLength);

            while (range.FindText(target, docLength - range.StartPosition, Microsoft.UI.Text.FindOptions.None) > 0)
            {
                _matchStartPositions.Add(range.StartPosition);
                range.StartPosition = range.EndPosition;
            }

            UpdateCurrentMatchIndex();
            UpdateStatusText(null);
        }

        private void UpdateCurrentMatchIndex()
        {
            var selection = MemoTextBox.Document.Selection;
            if (selection == null || _matchStartPositions.Count == 0)
            {
                _currentMatchIndex = -1;
                return;
            }

            _currentMatchIndex = _matchStartPositions.IndexOf(selection.StartPosition);
        }

        private void UpdateStatusText(string? prefixMessage)
        {
            if (string.IsNullOrEmpty(FindTextBox.Text))
            {
                FindStatusTextBlock.Text = "";
                FindStatusTextBlock.Visibility = Visibility.Collapsed;
                return;
            }

            int total = _matchStartPositions.Count;
            int current = _currentMatchIndex + 1; // 1-based index

            string countText = total > 0 ? $"{current} / {total}" : "0 / 0";

            if (total == 0)
            {
                FindStatusTextBlock.Text = "見つかりませんでした";
            }
            else if (!string.IsNullOrEmpty(prefixMessage))
            {
                FindStatusTextBlock.Text = $"{prefixMessage} ({countText})";
            }
            else
            {
                FindStatusTextBlock.Text = countText;
            }

            FindStatusTextBlock.Visibility = Visibility.Visible;
        }

        private void ShowFindReplace(bool showReplace)
        {
            ToggleReplaceModeBtn.IsChecked = showReplace;
            ReplaceRow.Visibility = showReplace ? Visibility.Visible : Visibility.Collapsed;
            FindStatusTextBlock.Visibility = Visibility.Collapsed;

            var selection = MemoTextBox.Document.Selection;
            if (selection != null && !string.IsNullOrEmpty(selection.Text) && !selection.Text.Contains('\r'))
            {
                FindTextBox.Text = selection.Text;
            }

            RecalculateMatches(); // 表示したタイミングで一致件数を計測

            // すでに表示中でない場合のみ初期位置を設定（ドラッグ後の位置を維持するため）
            if (FindReplacePanel.Visibility != Visibility.Visible)
            {
                _flyoutCurrentX = MemoTextBox.ActualWidth - 302;
                _flyoutCurrentY = 12;
                SetAnchorPosition(_flyoutCurrentX, _flyoutCurrentY);
            }

            FindReplacePanel.Visibility = Visibility.Visible;

            if (showReplace) ReplaceTextBox.Focus(FocusState.Programmatic);
            else FindTextBox.Focus(FocusState.Programmatic);
            FindTextBox.SelectAll();
        }

        private void CloseFindReplace_Click(object? sender, RoutedEventArgs? e)
        {
            FindReplacePanel.Visibility = Visibility.Collapsed;
            this.DispatcherQueue.TryEnqueue(() =>
            {
                MemoTextBox.Focus(FocusState.Programmatic);
            });
        }

        private void ToggleReplaceModeBtn_Click(object sender, RoutedEventArgs e)
        {
            bool isReplace = ToggleReplaceModeBtn.IsChecked ?? false;
            ReplaceRow.Visibility = isReplace ? Visibility.Visible : Visibility.Collapsed;
        }

        private void FindTextBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                var shiftState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
                bool isShiftDown = (shiftState & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
                FindNext(backward: isShiftDown);
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Escape)
            {
                CloseFindReplace_Click(null, null);
                e.Handled = true;
            }
        }

        private void ReplaceTextBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                ReplaceCurrent();
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Escape)
            {
                CloseFindReplace_Click(null, null);
                e.Handled = true;
            }
        }

        private void FindNext_Click(object sender, RoutedEventArgs e) 
        { 
            FindNext(backward: false); 
        }

        private void FindPrev_Click(object sender, RoutedEventArgs e) 
        { 
            FindNext(backward: true); 
        }

        private void FindNext(bool backward)
        {
            string target = FindTextBox.Text;
            if (string.IsNullOrEmpty(target)) return;

            var doc = MemoTextBox.Document;
            var selection = doc.Selection;
            
            // 現在の選択を解除し、検索開始位置を決定
            int startPos = backward ? selection.StartPosition : selection.EndPosition;
            int docLength = doc.GetRange(0, int.MaxValue).Length;
            
            var searchRange = doc.GetRange(startPos, backward ? 0 : docLength);
            int searchLength = backward ? -startPos : (docLength - startPos);
            
            int matchLength = searchRange.FindText(target, searchLength, Microsoft.UI.Text.FindOptions.None);

            if (matchLength > 0)
            {
                selection.SetRange(searchRange.StartPosition, searchRange.EndPosition);
                selection.ScrollIntoView(Microsoft.UI.Text.PointOptions.None);
                UpdateCurrentMatchIndex();
                UpdateStatusText(null);
            }
            else
            {
                // ラップアラウンド検索
                var wrapRange = backward ? doc.GetRange(docLength, 0) : doc.GetRange(0, docLength);
                int wrapSearchLength = backward ? -docLength : docLength;
                
                int wrapMatchLength = wrapRange.FindText(target, wrapSearchLength, Microsoft.UI.Text.FindOptions.None);
                if (wrapMatchLength > 0)
                {
                    selection.SetRange(wrapRange.StartPosition, wrapRange.EndPosition);
                    selection.ScrollIntoView(Microsoft.UI.Text.PointOptions.None);
                    UpdateCurrentMatchIndex();
                    string msg = backward ? "先頭に達したため末尾から検索しました" : "末尾に達したため先頭から検索しました";
                    UpdateStatusText(msg);
                }
                else
                {
                    _currentMatchIndex = -1;
                    UpdateStatusText("見つかりませんでした");
                }
            }
        }

        private void Replace_Click(object sender, RoutedEventArgs e) 
        { 
            ReplaceCurrent(); 
        }

        private void ReplaceCurrent()
        {
            var selection = MemoTextBox.Document.Selection;
            if (selection == null || string.IsNullOrEmpty(FindTextBox.Text)) return;

            if (selection.Text != null && selection.Text.Equals(FindTextBox.Text, StringComparison.CurrentCultureIgnoreCase))
            {
                selection.SetText(Microsoft.UI.Text.TextSetOptions.None, ReplaceTextBox.Text);
                ApplyGlobalThemeToEditor();
                MarkAsDirty();
                RecalculateMatches();
            }
            FindNext(backward: false);
        }

        private void ReplaceAll_Click(object sender, RoutedEventArgs e)
        {
            string target = FindTextBox.Text;
            if (string.IsNullOrEmpty(target)) return;

            var doc = MemoTextBox.Document;
            doc.BatchDisplayUpdates();
            try
            {
                int count = 0;
                int docLength = doc.GetRange(0, int.MaxValue).Length;
                var range = doc.GetRange(0, docLength);
                
                while (range.FindText(target, docLength - range.StartPosition, Microsoft.UI.Text.FindOptions.None) > 0)
                {
                    range.SetText(Microsoft.UI.Text.TextSetOptions.None, ReplaceTextBox.Text);
                    range.StartPosition = range.EndPosition; 
                    count++;
                    docLength = doc.GetRange(0, int.MaxValue).Length; // 置換による長さ変動を再取得
                }

                if (count > 0)
                {
                    ApplyGlobalThemeToEditor();
                    MarkAsDirty();
                    RecalculateMatches();
                    FindStatusTextBlock.Text = $"{count} 件を置換しました";
                }
                else
                {
                    FindStatusTextBlock.Text = "置換対象が見つかりませんでした";
                }
                FindStatusTextBlock.Visibility = Visibility.Visible;
            }
            finally
            {
                doc.ApplyDisplayUpdates();
            }
        }

        private void FindTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            RecalculateMatches();
        }
    }
}
