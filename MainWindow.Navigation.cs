using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace sumi
{
    public sealed partial class MainWindow : Window, IDisposable
    {
        /// <summary>
        /// 左右のサイドバーの表示・非表示を切り替えます。
        /// </summary>
        private void ToggleSidebarsButton_Click(object sender, RoutedEventArgs e)
        {
            bool isLeftOpen = SidebarSplitView != null && SidebarSplitView.IsPaneOpen;
            bool isRightOpen = RightSidebarSplitView != null && RightSidebarSplitView.IsPaneOpen;

            if (isLeftOpen || isRightOpen)
            {
                // どちらかのサイドバーが開いている場合は、両方を非表示にする
                if (SidebarSplitView != null)
                {
                    SidebarSplitView.IsPaneOpen = false;
                }
                if (RightSidebarSplitView != null)
                {
                    RightSidebarSplitView.IsPaneOpen = false;
                }
            }
            else
            {
                // 両方閉じている場合は、両方を表示する
                if (SidebarSplitView != null && !SidebarSplitView.IsPaneOpen)
                {
                    PopulateSidebarView(_currentSidebarView);
                    SidebarSplitView.IsPaneOpen = true;
                }
                if (RightSidebarSplitView != null && !RightSidebarSplitView.IsPaneOpen)
                {
                    PopulateRightSidebarView(_currentRightSidebarView);
                    RightSidebarSplitView.IsPaneOpen = true;
                }
            }
        }

        /// <summary>
        /// 左右のサイドバーの開閉状態に応じて、トグルボタンのアイコンとツールチップを更新します。
        /// </summary>
        private void UpdateSidebarToggleButtonState()
        {
            if (ToggleSidebarsButton == null) return;

            if (_isLeftSidebarTargetOpen || _isRightSidebarTargetOpen)
            {
                // どちらかのサイドバーが開いている場合は「両方非表示」の状態にする
                ToggleSidebarsButton.Content = "\uEDB4";
                ToolTipService.SetToolTip(ToggleSidebarsButton, "両方非表示");
            }
            else
            {
                // 両方のサイドバーが閉じている場合は「両方表示」の状態にする
                ToggleSidebarsButton.Content = "\uF57C";
                ToolTipService.SetToolTip(ToggleSidebarsButton, "両方表示");
            }
        }

        private void SidebarSplitView_PaneOpening(SplitView sender, object args)
        {
            _isLeftSidebarTargetOpen = true;
            UpdateSidebarToggleButtonState();
        }

        private void SidebarSplitView_PaneClosing(SplitView sender, SplitViewPaneClosingEventArgs args)
        {
            _isLeftSidebarTargetOpen = false;
            UpdateSidebarToggleButtonState();
        }

        private void RightSidebarSplitView_PaneOpening(SplitView sender, object args)
        {
            _isRightSidebarTargetOpen = true;
            UpdateSidebarToggleButtonState();
        }

        private void RightSidebarSplitView_PaneClosing(SplitView sender, SplitViewPaneClosingEventArgs args)
        {
            _isRightSidebarTargetOpen = false;
            UpdateSidebarToggleButtonState();
        }

        private void HamburgerButton_Click(object sender, RoutedEventArgs e)
        {
            if (SidebarSplitView != null)
            {
                bool targetOpen = !SidebarSplitView.IsPaneOpen;
                if (targetOpen)
                {
                    PopulateSidebarView(_currentSidebarView);
                }
                SidebarSplitView.IsPaneOpen = targetOpen;
            }
        }

        private void NotesMenuButton_Click(object sender, RoutedEventArgs e)
        {
            SetSidebarView(SidebarView.Notes);
        }

        private void NotesButton_Click(object sender, RoutedEventArgs e)
        {
            SetSidebarView(SidebarView.Notes);
        }

        private void TasksMenuButton_Click(object sender, RoutedEventArgs e)
        {
            SetSidebarView(SidebarView.Tasks);
        }

        private void AllTasksMenuButton_Click(object sender, RoutedEventArgs e)
        {
            SetSidebarView(SidebarView.AllTasks);
        }

        private void JustDoItMenuButton_Click(object sender, RoutedEventArgs e)
        {
            SetSidebarView(SidebarView.JustDoIt);
        }

        private void TagsMenuButton_Click(object sender, RoutedEventArgs e)
        {
            SetSidebarView(SidebarView.Tags);
        }

        private void RightNotesMenuButton_Click(object sender, RoutedEventArgs e)
        {
            SetRightSidebarView(SidebarView.Notes);
        }

        private void RightTasksMenuButton_Click(object sender, RoutedEventArgs e)
        {
            SetRightSidebarView(SidebarView.Tasks);
        }

        private void RightAllTasksMenuButton_Click(object sender, RoutedEventArgs e)
        {
            SetRightSidebarView(SidebarView.AllTasks);
        }

        private void RightJustDoItMenuButton_Click(object sender, RoutedEventArgs e)
        {
            SetRightSidebarView(SidebarView.JustDoIt);
        }

        private void RightTagsMenuButton_Click(object sender, RoutedEventArgs e)
        {
            SetRightSidebarView(SidebarView.Tags);
        }

        private void RightHamburgerButton_Click(object sender, RoutedEventArgs e)
        {
            if (RightSidebarSplitView != null)
            {
                bool targetOpen = !RightSidebarSplitView.IsPaneOpen;
                RightSidebarSplitView.IsPaneOpen = targetOpen;
                if (targetOpen)
                {
                    PopulateRightSidebarView(_currentRightSidebarView);
                }
            }
        }

        private void SetSidebarView(SidebarView view)
        {
            _currentSidebarView = view;
            MemoStorage.LastSidebarView = view.ToString();
            QueueSaveSettings();

            // Update Title Text
            if (PaneTitleTextBlock != null)
            {
                PaneTitleTextBlock.Text = view switch
                {
                    SidebarView.Notes => "Notes",
                    SidebarView.Tasks => "Tasks",
                    SidebarView.AllTasks => "All Tasks",
                    SidebarView.JustDoIt => "Just Do It",
                    SidebarView.Tags => "Tags",
                    _ => ""
                };
            }

            // Update Indicators
            if (NotesActiveIndicator != null) NotesActiveIndicator.Visibility = view == SidebarView.Notes ? Visibility.Visible : Visibility.Collapsed;
            if (TasksActiveIndicator != null) TasksActiveIndicator.Visibility = view == SidebarView.Tasks ? Visibility.Visible : Visibility.Collapsed;
            if (AllTasksActiveIndicator != null) AllTasksActiveIndicator.Visibility = view == SidebarView.AllTasks ? Visibility.Visible : Visibility.Collapsed;
            if (JustDoItActiveIndicator != null) JustDoItActiveIndicator.Visibility = view == SidebarView.JustDoIt ? Visibility.Visible : Visibility.Collapsed;
            if (TagsActiveIndicator != null) TagsActiveIndicator.Visibility = view == SidebarView.Tags ? Visibility.Visible : Visibility.Collapsed;

            // Update Containers Visibility
            if (NotesViewContainer != null) NotesViewContainer.Visibility = view == SidebarView.Notes ? Visibility.Visible : Visibility.Collapsed;
            if (TasksViewContainer != null) TasksViewContainer.Visibility = view == SidebarView.Tasks ? Visibility.Visible : Visibility.Collapsed;
            if (AllTasksViewContainer != null) AllTasksViewContainer.Visibility = view == SidebarView.AllTasks ? Visibility.Visible : Visibility.Collapsed;
            if (JustDoItViewContainer != null) JustDoItViewContainer.Visibility = view == SidebarView.JustDoIt ? Visibility.Visible : Visibility.Collapsed;
            if (TagsViewContainer != null) TagsViewContainer.Visibility = view == SidebarView.Tags ? Visibility.Visible : Visibility.Collapsed;

            // Update DeleteModeButton Visibility & Checked state
            if (DeleteModeButton != null)
            {
                DeleteModeButton.Visibility = view == SidebarView.Tasks ? Visibility.Visible : Visibility.Collapsed;
                if (view != SidebarView.Tasks)
                {
                    DeleteModeButton.IsChecked = false;
                }
            }

            // Open pane if closed
            if (SidebarSplitView != null && !SidebarSplitView.IsPaneOpen)
            {
                PopulateSidebarView(view);
                SidebarSplitView.IsPaneOpen = true;
            }
            else
            {
                // Pane is already open, populate directly
                PopulateSidebarView(view);
            }
        }

        private void SetRightSidebarView(SidebarView view)
        {
            _currentRightSidebarView = view;
            MemoStorage.LastRightSidebarView = view.ToString();
            QueueSaveSettings();

            // Update Title Text
            if (RightPaneTitleTextBlock != null)
            {
                RightPaneTitleTextBlock.Text = view switch
                {
                    SidebarView.Notes => "Notes",
                    SidebarView.Tasks => "Tasks",
                    SidebarView.AllTasks => "All Tasks",
                    SidebarView.JustDoIt => "Just Do It",
                    _ => ""
                };
            }

            // Update Indicators
            if (RightNotesActiveIndicator != null) RightNotesActiveIndicator.Visibility = view == SidebarView.Notes ? Visibility.Visible : Visibility.Collapsed;
            if (RightTasksActiveIndicator != null) RightTasksActiveIndicator.Visibility = view == SidebarView.Tasks ? Visibility.Visible : Visibility.Collapsed;
            if (RightAllTasksActiveIndicator != null) RightAllTasksActiveIndicator.Visibility = view == SidebarView.AllTasks ? Visibility.Visible : Visibility.Collapsed;
            if (RightJustDoItActiveIndicator != null) RightJustDoItActiveIndicator.Visibility = view == SidebarView.JustDoIt ? Visibility.Visible : Visibility.Collapsed;
            if (RightTagsActiveIndicator != null) RightTagsActiveIndicator.Visibility = view == SidebarView.Tags ? Visibility.Visible : Visibility.Collapsed;

            // Update Containers Visibility
            if (RightNotesViewContainer != null) RightNotesViewContainer.Visibility = view == SidebarView.Notes ? Visibility.Visible : Visibility.Collapsed;
            if (RightTasksViewContainer != null) RightTasksViewContainer.Visibility = view == SidebarView.Tasks ? Visibility.Visible : Visibility.Collapsed;
            if (RightAllTasksViewContainer != null) RightAllTasksViewContainer.Visibility = view == SidebarView.AllTasks ? Visibility.Visible : Visibility.Collapsed;
            if (RightJustDoItTasksViewContainer != null) RightJustDoItTasksViewContainer.Visibility = view == SidebarView.JustDoIt ? Visibility.Visible : Visibility.Collapsed;
            if (RightTagsViewContainer != null) RightTagsViewContainer.Visibility = view == SidebarView.Tags ? Visibility.Visible : Visibility.Collapsed;

            // Update RightDeleteModeButton Visibility & Checked state
            if (RightDeleteModeButton != null)
            {
                RightDeleteModeButton.Visibility = view == SidebarView.Tasks ? Visibility.Visible : Visibility.Collapsed;
                if (view != SidebarView.Tasks)
                {
                    RightDeleteModeButton.IsChecked = false;
                }
            }

            // Open pane if closed
            if (RightSidebarSplitView != null && !RightSidebarSplitView.IsPaneOpen)
            {
                PopulateRightSidebarView(view);
                RightSidebarSplitView.IsPaneOpen = true;
            }
            else
            {
                // Pane is already open, populate directly
                PopulateRightSidebarView(view);
            }
        }

        private void PopulateSidebarView(SidebarView view)
        {
            if (view == SidebarView.Notes)
            {
                if (SidebarNoteSearchBox != null) SidebarNoteSearchBox.Text = string.Empty;
                PopulateSidebarNotesList();
                SidebarNoteSearchBox?.Focus(FocusState.Programmatic);
            }
            else if (view == SidebarView.Tasks)
            {
                PopulateCurrentTasks();
                AddTaskBox?.Focus(FocusState.Programmatic);
            }
            else if (view == SidebarView.AllTasks)
            {
                PopulateAllTasks();
            }
            else if (view == SidebarView.JustDoIt)
            {
                PopulateJustDoItTasks();
            }
            else if (view == SidebarView.Tags)
            {
                PopulateTagsView();
                SidebarTagSearchBox?.Focus(FocusState.Programmatic);
            }
        }

        private void PopulateRightSidebarView(SidebarView view)
        {
            if (view == SidebarView.Notes)
            {
                if (RightSidebarNoteSearchBox != null) RightSidebarNoteSearchBox.Text = string.Empty;
                PopulateRightSidebarNotesList();
                RightSidebarNoteSearchBox?.Focus(FocusState.Programmatic);
            }
            else if (view == SidebarView.Tasks)
            {
                PopulateRightCurrentTasks();
                RightAddTaskBox?.Focus(FocusState.Programmatic);
            }
            else if (view == SidebarView.AllTasks)
            {
                PopulateRightAllTasks();
            }
            else if (view == SidebarView.JustDoIt)
            {
                PopulateJustDoItTasks();
            }
            else if (view == SidebarView.Tags)
            {
                PopulateRightTagsView();
                RightSidebarTagSearchBox?.Focus(FocusState.Programmatic);
            }
        }

        private void SidebarSplitView_PaneOpened(SplitView sender, object args)
        {
            MemoStorage.IsSidebarOpen = true;
            QueueSaveSettings();

            // Focus the appropriate input control once the pane is fully opened
            if (_currentSidebarView == SidebarView.Notes)
            {
                SidebarNoteSearchBox?.Focus(FocusState.Programmatic);
            }
            else if (_currentSidebarView == SidebarView.Tasks)
            {
                AddTaskBox?.Focus(FocusState.Programmatic);
            }
        }

        private void SidebarSplitView_PaneClosed(SplitView sender, object args)
        {
            MemoStorage.IsSidebarOpen = false;
            QueueSaveSettings();
        }

        private void RightSidebarSplitView_PaneOpened(SplitView sender, object args)
        {
            MemoStorage.IsRightSidebarOpen = true;
            QueueSaveSettings();

            // Focus the appropriate input control once the pane is fully opened
            if (_currentRightSidebarView == SidebarView.Notes)
            {
                RightSidebarNoteSearchBox?.Focus(FocusState.Programmatic);
            }
            else if (_currentRightSidebarView == SidebarView.Tasks)
            {
                RightAddTaskBox?.Focus(FocusState.Programmatic);
            }
        }

        private void RightSidebarSplitView_PaneClosed(SplitView sender, object args)
        {
            MemoStorage.IsRightSidebarOpen = false;
            QueueSaveSettings();
        }

        private void PinSidebarButton_Click(object sender, RoutedEventArgs e)
        {
            if (PinSidebarButton != null && SidebarSplitView != null)
            {
                bool pinned = PinSidebarButton.IsChecked ?? false;
                MemoStorage.IsSidebarPinned = pinned;
                SidebarSplitView.DisplayMode = pinned ? SplitViewDisplayMode.CompactInline : SplitViewDisplayMode.CompactOverlay;
                if (SidebarPinFilledIcon != null)
                {
                    SidebarPinFilledIcon.Visibility = pinned ? Visibility.Visible : Visibility.Collapsed;
                }
                QueueSaveSettings();
            }
        }

        private void Resizer_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is FrameworkElement element && SidebarSplitView != null)
            {
                _isResizing = true;
                element.CapturePointer(e.Pointer);
                var pt = e.GetCurrentPoint(this.Content);
                _startPointerPositionX = pt.Position.X;
                _startOpenPaneLength = SidebarSplitView.OpenPaneLength;
                e.Handled = true;
            }
        }

        private void Resizer_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_isResizing && SidebarSplitView != null)
            {
                var pt = e.GetCurrentPoint(this.Content);
                double deltaX = pt.Position.X - _startPointerPositionX;
                double newWidth = _startOpenPaneLength + deltaX;
                newWidth = Math.Clamp(newWidth, 200, 600);
                SidebarSplitView.OpenPaneLength = newWidth;
                e.Handled = true;
            }
        }

        private void Resizer_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_isResizing && SidebarSplitView != null)
            {
                if (sender is FrameworkElement element)
                {
                    element.ReleasePointerCapture(e.Pointer);
                }
                _isResizing = false;
                MemoStorage.SidebarWidth = SidebarSplitView.OpenPaneLength;
                QueueSaveSettings();
                e.Handled = true;
            }
        }

        private void PinRightSidebarButton_Click(object sender, RoutedEventArgs e)
        {
            if (PinRightSidebarButton != null && RightSidebarSplitView != null)
            {
                bool pinned = PinRightSidebarButton.IsChecked ?? false;
                MemoStorage.IsRightSidebarPinned = pinned;
                RightSidebarSplitView.DisplayMode = pinned ? SplitViewDisplayMode.CompactInline : SplitViewDisplayMode.CompactOverlay;
                if (RightSidebarPinFilledIcon != null)
                {
                    RightSidebarPinFilledIcon.Visibility = pinned ? Visibility.Visible : Visibility.Collapsed;
                }
                QueueSaveSettings();
            }
        }

        private void RightResizer_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is FrameworkElement element && RightSidebarSplitView != null)
            {
                _isRightResizing = true;
                element.CapturePointer(e.Pointer);
                var pt = e.GetCurrentPoint(this.Content);
                _startRightPointerPositionX = pt.Position.X;
                _startRightOpenPaneLength = RightSidebarSplitView.OpenPaneLength;
                e.Handled = true;
            }
        }

        private void RightResizer_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_isRightResizing && RightSidebarSplitView != null)
            {
                var pt = e.GetCurrentPoint(this.Content);
                double deltaX = pt.Position.X - _startRightPointerPositionX;
                double newWidth = _startRightOpenPaneLength - deltaX;
                newWidth = Math.Clamp(newWidth, 200, 600);
                RightSidebarSplitView.OpenPaneLength = newWidth;
                e.Handled = true;
            }
        }

        private void RightResizer_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_isRightResizing && RightSidebarSplitView != null)
            {
                if (sender is FrameworkElement element)
                {
                    element.ReleasePointerCapture(e.Pointer);
                }
                _isRightResizing = false;
                MemoStorage.RightSidebarWidth = RightSidebarSplitView.OpenPaneLength;
                QueueSaveSettings();
                e.Handled = true;
            }
        }
    }
}
