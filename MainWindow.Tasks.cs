using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace sumi
{
    public sealed partial class MainWindow : Window, IDisposable
    {
        private void OnTaskChanged(string noteId)
        {
            lock (_dirtyTaskNoteIds)
            {
                _dirtyTaskNoteIds.Add(noteId);
            }

            // Update UncompletedTaskCount in NoteData
            NoteData? note = null;
            lock (MemoStorage.Notes)
            {
                note = MemoStorage.Notes.Find(n => n.Id == noteId);
            }
            if (note != null)
            {
                int count = 0;
                lock (note.Tasks)
                {
                    foreach (var t in note.Tasks)
                    {
                        if (!t.IsCompleted) count++;
                    }
                }
                note.UncompletedTaskCount = count;
            }

            _taskSaveScheduler.Schedule();
        }

        private async Task SaveDirtyTasksAsync()
        {
            List<string> noteIdsToSave;
            lock (_dirtyTaskNoteIds)
            {
                noteIdsToSave = new List<string>(_dirtyTaskNoteIds);
                _dirtyTaskNoteIds.Clear();
            }

            if (noteIdsToSave.Count == 0) return;

            foreach (var noteId in noteIdsToSave)
            {
                NoteData? note = null;
                lock (MemoStorage.Notes)
                {
                    note = MemoStorage.Notes.Find(n => n.Id == noteId);
                }
                if (note != null)
                {
                    List<TaskItem> tasksToSave = new List<TaskItem>();
                    lock (note.Tasks)
                    {
                        foreach (var t in note.Tasks)
                        {
                            tasksToSave.Add(new TaskItem
                            {
                                Id = t.Id,
                                Title = t.Title,
                                IsCompleted = t.IsCompleted,
                                CreatedAt = t.CreatedAt
                            });
                        }
                    }
                    // JSON 化とファイル I/O を UI スレッドから切り離す。
                    await Task.Run(() => MemoStorage.SaveTasksAtomicAsync(noteId, tasksToSave));
                }
            }

            // Save metadata
            await Task.Run(() => MemoStorage.SaveMetadata());

            // Refresh UI list if visible
            this.DispatcherQueue.TryEnqueue(() =>
            {
                if (SidebarSplitView != null && SidebarSplitView.IsPaneOpen)
                {
                    if (_currentSidebarView == SidebarView.Notes)
                    {
                        PopulateSidebarNotesList(SidebarNoteSearchBox.Text);
                    }
                    else if (_currentSidebarView == SidebarView.AllTasks)
                    {
                        PopulateAllTasks();
                    }
                    else if (_currentSidebarView == SidebarView.JustDoIt)
                    {
                        PopulateJustDoItTasks();
                    }
                }
                if (RightSidebarSplitView != null && RightSidebarSplitView.IsPaneOpen)
                {
                    if (_currentRightSidebarView == SidebarView.Notes)
                    {
                        PopulateRightSidebarNotesList(RightSidebarNoteSearchBox.Text);
                    }
                    else if (_currentRightSidebarView == SidebarView.AllTasks)
                    {
                        PopulateRightAllTasks();
                    }
                    else if (_currentRightSidebarView == SidebarView.JustDoIt)
                    {
                        PopulateJustDoItTasks();
                    }
                }
            });
        }

        private void PopulateCurrentTasks()
        {
            NoteData? currentNote = null;
            lock (MemoStorage.Notes)
            {
                currentNote = MemoStorage.Notes.Find(n => n.Id == MemoStorage.CurrentNoteId);
            }

            if (currentNote != null)
            {
                MemoStorage.LoadTasksForNoteSync(currentNote);
                if (CurrentTasksListView != null)
                {
                    if (CurrentTasksListView.ItemsSource != currentNote.Tasks)
                    {
                        CurrentTasksListView.ItemsSource = currentNote.Tasks;
                    }
                }
            }
            else
            {
                if (CurrentTasksListView != null && CurrentTasksListView.ItemsSource != null)
                {
                    CurrentTasksListView.ItemsSource = null;
                }
            }
        }

        private void PopulateAllTasks()
        {
            var groups = new List<AllTasksGroupViewModel>();
            List<NoteData> notes;
            lock (MemoStorage.Notes)
            {
                notes = new List<NoteData>(MemoStorage.Notes);
            }

            foreach (var note in notes)
            {
                MemoStorage.LoadTasksForNoteSync(note);
                var uncompletedTasks = new ObservableCollection<TaskItemViewModel>();
                lock (note.Tasks)
                {
                    foreach (var t in note.Tasks)
                    {
                        if (!t.IsCompleted)
                        {
                            uncompletedTasks.Add(t);
                        }
                    }
                }
                if (uncompletedTasks.Count > 0)
                {
                    groups.Add(new AllTasksGroupViewModel(note.Id, note.Title, uncompletedTasks));
                }
            }

            if (AllTasksGroupsControl != null)
            {
                var currentList = AllTasksGroupsControl.ItemsSource as List<AllTasksGroupViewModel>;
                if (!AreAllTasksGroupsEqual(currentList, groups))
                {
                    AllTasksGroupsControl.ItemsSource = null;
                    AllTasksGroupsControl.ItemsSource = groups;
                }
            }
        }

        private void PopulateRightCurrentTasks()
        {
            NoteData? currentNote = null;
            lock (MemoStorage.Notes)
            {
                currentNote = MemoStorage.Notes.Find(n => n.Id == MemoStorage.CurrentNoteId);
            }

            if (currentNote != null)
            {
                MemoStorage.LoadTasksForNoteSync(currentNote);
                if (RightCurrentTasksListView != null)
                {
                    if (RightCurrentTasksListView.ItemsSource != currentNote.Tasks)
                    {
                        RightCurrentTasksListView.ItemsSource = currentNote.Tasks;
                    }
                }
            }
            else
            {
                if (RightCurrentTasksListView != null && RightCurrentTasksListView.ItemsSource != null)
                {
                    RightCurrentTasksListView.ItemsSource = null;
                }
            }
        }

        private void PopulateRightAllTasks()
        {
            var groups = new List<AllTasksGroupViewModel>();
            List<NoteData> notes;
            lock (MemoStorage.Notes)
            {
                notes = new List<NoteData>(MemoStorage.Notes);
            }

            foreach (var note in notes)
            {
                MemoStorage.LoadTasksForNoteSync(note);
                var uncompletedTasks = new ObservableCollection<TaskItemViewModel>();
                lock (note.Tasks)
                {
                    foreach (var t in note.Tasks)
                    {
                        if (!t.IsCompleted)
                        {
                            uncompletedTasks.Add(t);
                        }
                    }
                }
                if (uncompletedTasks.Count > 0)
                {
                    groups.Add(new AllTasksGroupViewModel(note.Id, note.Title, uncompletedTasks));
                }
            }

            if (RightAllTasksGroupsControl != null)
            {
                var currentList = RightAllTasksGroupsControl.ItemsSource as List<AllTasksGroupViewModel>;
                if (!AreAllTasksGroupsEqual(currentList, groups))
                {
                    RightAllTasksGroupsControl.ItemsSource = null;
                    RightAllTasksGroupsControl.ItemsSource = groups;
                }
            }
        }

        private void PopulateJustDoItTasks()
        {
            var groups = new List<AllTasksGroupViewModel>();
            List<NoteData> notes;
            lock (MemoStorage.Notes)
            {
                notes = new List<NoteData>(MemoStorage.Notes);
            }

            foreach (var note in notes)
            {
                MemoStorage.LoadTasksForNoteSync(note);
                var justDoItTasks = new ObservableCollection<TaskItemViewModel>();
                lock (note.Tasks)
                {
                    foreach (var t in note.Tasks)
                    {
                        if (t.IsJustDoIt)
                        {
                            justDoItTasks.Add(t);
                        }
                    }
                }
                if (justDoItTasks.Count > 0)
                {
                    groups.Add(new AllTasksGroupViewModel(note.Id, note.Title, justDoItTasks));
                }
            }

            if (JustDoItTasksGroupsControl != null)
            {
                var currentList = JustDoItTasksGroupsControl.ItemsSource as List<AllTasksGroupViewModel>;
                if (!AreAllTasksGroupsEqual(currentList, groups))
                {
                    JustDoItTasksGroupsControl.ItemsSource = null;
                    JustDoItTasksGroupsControl.ItemsSource = groups;
                }
            }

            if (LeftJustDoItTasksGroupsControl != null)
            {
                var currentList = LeftJustDoItTasksGroupsControl.ItemsSource as List<AllTasksGroupViewModel>;
                if (!AreAllTasksGroupsEqual(currentList, groups))
                {
                    LeftJustDoItTasksGroupsControl.ItemsSource = null;
                    LeftJustDoItTasksGroupsControl.ItemsSource = groups;
                }
            }
        }

        private void AddTaskBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                string text = AddTaskBox.Text.Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    NoteData? currentNote = null;
                    lock (MemoStorage.Notes)
                    {
                        currentNote = MemoStorage.Notes.Find(n => n.Id == MemoStorage.CurrentNoteId);
                    }
                    if (currentNote != null)
                    {
                        MemoStorage.LoadTasksForNoteSync(currentNote);
                        var newTask = new TaskItemViewModel(
                            Guid.NewGuid().ToString(),
                            currentNote.Id,
                            text,
                            false,
                            DateTime.UtcNow,
                            () => OnTaskChanged(currentNote.Id)
                        );
                        lock (currentNote.Tasks)
                        {
                            currentNote.Tasks.Add(newTask);
                        }
                        OnTaskChanged(currentNote.Id);

                        AddTaskBox.Text = string.Empty;
                        PopulateCurrentTasks();
                        PopulateRightCurrentTasks();
                    }
                }
                e.Handled = true;
            }
        }

        private void RightAddTaskBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                string text = RightAddTaskBox.Text.Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    NoteData? currentNote = null;
                    lock (MemoStorage.Notes)
                    {
                        currentNote = MemoStorage.Notes.Find(n => n.Id == MemoStorage.CurrentNoteId);
                    }
                    if (currentNote != null)
                    {
                        MemoStorage.LoadTasksForNoteSync(currentNote);
                        var newTask = new TaskItemViewModel(
                            Guid.NewGuid().ToString(),
                            currentNote.Id,
                            text,
                            false,
                            DateTime.UtcNow,
                            () => OnTaskChanged(currentNote.Id)
                        );
                        lock (currentNote.Tasks)
                        {
                            currentNote.Tasks.Add(newTask);
                        }
                        OnTaskChanged(currentNote.Id);

                        RightAddTaskBox.Text = string.Empty;
                        PopulateCurrentTasks();
                        PopulateRightCurrentTasks();
                    }
                }
                e.Handled = true;
            }
        }

        private void DeleteTaskButton_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                btn.Click += DeleteTaskButton_Click;
            }
        }

        private void DeleteTaskButton_Unloaded(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                btn.Click -= DeleteTaskButton_Click;
            }
        }

        private void DeleteTaskButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is TaskItemViewModel vm)
            {
                NoteData? note = null;
                lock (MemoStorage.Notes)
                {
                    note = MemoStorage.Notes.Find(n => n.Id == vm.ParentNoteId);
                }
                if (note != null)
                {
                    lock (note.Tasks)
                    {
                        note.Tasks.Remove(vm);
                    }
                    OnTaskChanged(vm.ParentNoteId);
                    PopulateCurrentTasks();
                    PopulateRightCurrentTasks();
                }
            }
        }

        private void TaskItemGrid_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Grid grid)
            {
                var justDoItBtn = grid.FindName("JustDoItTaskButton") as FrameworkElement;
                if (justDoItBtn != null)
                {
                    justDoItBtn.Visibility = Visibility.Visible;
                }
            }
        }

        private void TaskItemGrid_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Grid grid)
            {
                var justDoItBtn = grid.FindName("JustDoItTaskButton") as FrameworkElement;
                if (justDoItBtn != null && justDoItBtn.DataContext is TaskItemViewModel vm && !vm.IsJustDoIt)
                {
                    justDoItBtn.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void CurrentTaskTitleTextBlock_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (sender is TextBlock textBlock)
            {
                textBlock.Visibility = Visibility.Collapsed;
                var parentGrid = textBlock.Parent as Grid;
                if (parentGrid != null)
                {
                    var textBox = parentGrid.FindName("CurrentTaskTitleTextBox") as TextBox;
                    if (textBox != null)
                    {
                        textBox.Visibility = Visibility.Visible;
                        textBox.Focus(FocusState.Programmatic);
                        textBox.SelectAll();
                    }
                }
            }
        }

        private void CurrentTaskTitleTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                textBox.Visibility = Visibility.Collapsed;
                var parentGrid = textBox.Parent as Grid;
                if (parentGrid != null)
                {
                    var textBlock = parentGrid.FindName("CurrentTaskTitleTextBlock") as TextBlock;
                    if (textBlock != null)
                    {
                        textBlock.Visibility = Visibility.Visible;
                    }
                }
            }
        }

        private void CurrentTaskTitleTextBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                if (sender is TextBox textBox)
                {
                    textBox.Visibility = Visibility.Collapsed;
                    var parentGrid = textBox.Parent as Grid;
                    if (parentGrid != null)
                    {
                        var textBlock = parentGrid.FindName("CurrentTaskTitleTextBlock") as TextBlock;
                        if (textBlock != null)
                        {
                            textBlock.Visibility = Visibility.Visible;
                        }
                    }
                    e.Handled = true;
                }
            }
            else if (e.Key == Windows.System.VirtualKey.Escape)
            {
                if (sender is TextBox textBox && textBox.DataContext is TaskItemViewModel vm)
                {
                    textBox.Text = vm.Title;
                    textBox.Visibility = Visibility.Collapsed;
                    var parentGrid = textBox.Parent as Grid;
                    if (parentGrid != null)
                    {
                        var textBlock = parentGrid.FindName("CurrentTaskTitleTextBlock") as TextBlock;
                        if (textBlock != null)
                        {
                            textBlock.Visibility = Visibility.Visible;
                        }
                    }
                    e.Handled = true;
                }
            }
        }

        private void RightCurrentTaskTitleTextBlock_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (sender is TextBlock textBlock)
            {
                textBlock.Visibility = Visibility.Collapsed;
                var parentGrid = textBlock.Parent as Grid;
                if (parentGrid != null)
                {
                    var textBox = parentGrid.FindName("RightCurrentTaskTitleTextBox") as TextBox;
                    if (textBox != null)
                    {
                        textBox.Visibility = Visibility.Visible;
                        textBox.Focus(FocusState.Programmatic);
                        textBox.SelectAll();
                    }
                }
            }
        }

        private void RightCurrentTaskTitleTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                textBox.Visibility = Visibility.Collapsed;
                var parentGrid = textBox.Parent as Grid;
                if (parentGrid != null)
                {
                    var textBlock = parentGrid.FindName("RightCurrentTaskTitleTextBlock") as TextBlock;
                    if (textBlock != null)
                    {
                        textBlock.Visibility = Visibility.Visible;
                    }
                }
            }
        }

        private void RightCurrentTaskTitleTextBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                if (sender is TextBox textBox)
                {
                    textBox.Visibility = Visibility.Collapsed;
                    var parentGrid = textBox.Parent as Grid;
                    if (parentGrid != null)
                    {
                        var textBlock = parentGrid.FindName("RightCurrentTaskTitleTextBlock") as TextBlock;
                        if (textBlock != null)
                        {
                            textBlock.Visibility = Visibility.Visible;
                        }
                    }
                    e.Handled = true;
                }
            }
            else if (e.Key == Windows.System.VirtualKey.Escape)
            {
                if (sender is TextBox textBox && textBox.DataContext is TaskItemViewModel vm)
                {
                    textBox.Text = vm.Title;
                    textBox.Visibility = Visibility.Collapsed;
                    var parentGrid = textBox.Parent as Grid;
                    if (parentGrid != null)
                    {
                        var textBlock = parentGrid.FindName("RightCurrentTaskTitleTextBlock") as TextBlock;
                        if (textBlock != null)
                        {
                            textBlock.Visibility = Visibility.Visible;
                        }
                    }
                    e.Handled = true;
                }
            }
        }

        private void AllTaskItemGrid_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Grid grid)
            {
                var justDoItBtn = grid.FindName("AllJustDoItButton") as FrameworkElement;
                if (justDoItBtn != null)
                {
                    justDoItBtn.Visibility = Visibility.Visible;
                }
            }
        }

        private void AllTaskItemGrid_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Grid grid)
            {
                var justDoItBtn = grid.FindName("AllJustDoItButton") as FrameworkElement;
                if (justDoItBtn != null && justDoItBtn.DataContext is TaskItemViewModel vm && !vm.IsJustDoIt)
                {
                    justDoItBtn.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void JustDoItTaskItemGrid_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Grid grid)
            {
                var justDoItBtn = grid.FindName("JustDoItToggleBtn") as FrameworkElement;
                if (justDoItBtn != null)
                {
                    justDoItBtn.Visibility = Visibility.Visible;
                }
            }
        }

        private void JustDoItTaskItemGrid_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Grid grid)
            {
                var justDoItBtn = grid.FindName("JustDoItToggleBtn") as FrameworkElement;
                if (justDoItBtn != null)
                {
                    justDoItBtn.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void JustDoItTaskButton_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                btn.Click += JustDoItButton_Click;
            }
        }

        private void JustDoItTaskButton_Unloaded(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                btn.Click -= JustDoItButton_Click;
            }
        }

        private void AllJustDoItButton_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                btn.Click += JustDoItButton_Click;
            }
        }

        private void AllJustDoItButton_Unloaded(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                btn.Click -= JustDoItButton_Click;
            }
        }

        private void JustDoItToggleBtn_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                btn.Click += JustDoItButton_Click;
            }
        }

        private void JustDoItToggleBtn_Unloaded(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                btn.Click -= JustDoItButton_Click;
            }
        }

        private bool IsInRightSidebar(DependencyObject? obj)
        {
            while (obj != null)
            {
                if (obj == RightSidebarSplitView)
                    return true;
                if (obj == SidebarSplitView)
                    return false;
                obj = VisualTreeHelper.GetParent(obj);
            }
            return false;
        }

        private void JustDoItButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is TaskItemViewModel vm)
            {
                vm.IsJustDoIt = !vm.IsJustDoIt;
                
                // Save task changes
                OnTaskChanged(vm.ParentNoteId);
                
                // Immediately refresh views
                PopulateCurrentTasks();
                PopulateRightCurrentTasks();
                PopulateAllTasks();
                PopulateRightAllTasks();
                PopulateJustDoItTasks();
            }
        }

        private void AllTasksGroupHeader_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is AllTasksGroupViewModel group)
            {
                SwitchToNote(group.NoteId);
                if (IsInRightSidebar(btn))
                {
                    SetRightSidebarView(SidebarView.Tasks);
                }
                else
                {
                    SetSidebarView(SidebarView.Tasks);
                }
            }
        }

        private void AllTaskItem_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            var originalSource = e.OriginalSource as DependencyObject;
            var parent = originalSource;
            while (parent != null)
            {
                if (parent is CheckBox)
                {
                    return; // CheckBoxクリック時はジャンプしない
                }
                parent = VisualTreeHelper.GetParent(parent);
            }

            if (sender is Grid grid && grid.DataContext is TaskItemViewModel vm)
            {
                SwitchToNote(vm.ParentNoteId);
                if (IsInRightSidebar(grid))
                {
                    SetRightSidebarView(SidebarView.Tasks);
                }
                else
                {
                    SetSidebarView(SidebarView.Tasks);
                }
                e.Handled = true;
            }
        }

        private bool AreAllTasksGroupsEqual(List<AllTasksGroupViewModel>? listA, List<AllTasksGroupViewModel> listB)
        {
            if (listA == null) return false;
            if (listA.Count != listB.Count) return false;
            for (int i = 0; i < listB.Count; i++)
            {
                var groupA = listA[i];
                var groupB = listB[i];
                if (groupA.NoteId != groupB.NoteId || groupA.NoteTitle != groupB.NoteTitle) return false;
                if (groupA.Tasks.Count != groupB.Tasks.Count) return false;
                for (int j = 0; j < groupB.Tasks.Count; j++)
                {
                    var taskA = groupA.Tasks[j];
                    var taskB = groupB.Tasks[j];
                    if (taskA.Id != taskB.Id ||
                        taskA.Title != taskB.Title ||
                        taskA.IsCompleted != taskB.IsCompleted ||
                        taskA.IsJustDoIt != taskB.IsJustDoIt)
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        public static Brush GetJustDoItBrush(bool isJustDoIt)
        {
            if (isJustDoIt)
            {
                return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 193, 7));
            }
            return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 136, 136, 136));
        }

        public static Brush GetJustDoItFillBrush(bool isJustDoIt)
        {
            if (isJustDoIt)
            {
                return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 193, 7));
            }
            return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }

        public static Brush GetJustDoItStrokeBrush(bool isJustDoIt)
        {
            if (isJustDoIt)
            {
                return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 193, 7));
            }
            return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 136, 136, 136));
        }

        public static Visibility BoolToVisibility(bool visible)
        {
            return visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
