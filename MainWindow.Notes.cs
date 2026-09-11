using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace sumi
{
    public sealed partial class MainWindow : Window, IDisposable
    {
        private void HighlightTimer_Tick(object? sender, object e)
        {
            _highlightTimer.Stop();
            _highlightedNoteId = null;
            RefreshAllNotesLists();
        }

        private void NoteItemButton_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                btn.Click += NoteItem_Click;
            }
        }

        private void NoteItemButton_Unloaded(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                btn.Click -= NoteItem_Click;
            }
        }

        private void PinItemButton_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                btn.Click += PinItem_Click;
            }
        }

        private void PinItemButton_Unloaded(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                btn.Click -= PinItem_Click;
            }
        }

        private void DeleteItemButton_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                btn.Click += DeleteItem_Click;
            }
        }

        private void DeleteItemButton_Unloaded(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                btn.Click -= DeleteItem_Click;
            }
        }

        private void NoteSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _noteSearchTimer.Stop();
            _noteSearchTimer.Start();
        }
        private void NoteSearchTimer_Tick(DispatcherQueueTimer sender, object args)
        {
            _noteSearchTimer.Stop();
            PopulateNotesList(NoteSearchBox?.Text ?? string.Empty);
        }

        private void NoteSearchBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                _noteSearchTimer.Stop();
                PopulateNotesList(NoteSearchBox.Text);
                var pinnedVMs = PinnedListView.ItemsSource as List<NoteItemViewModel>;
                if (pinnedVMs != null && pinnedVMs.Count > 0)
                {
                    OnNoteSelected(pinnedVMs[0].Id);
                    e.Handled = true;
                    return;
                }

                var normalVMs = NotesListView.ItemsSource as List<NoteItemViewModel>;
                if (normalVMs != null && normalVMs.Count > 0)
                {
                    OnNoteSelected(normalVMs[0].Id);
                    e.Handled = true;
                    return;
                }
            }
        }

        private void SidebarNoteSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _sidebarNoteSearchTimer.Stop();
            _sidebarNoteSearchTimer.Start();
        }

        private void SidebarNoteSearchTimer_Tick(DispatcherQueueTimer sender, object args)
        {
            _sidebarNoteSearchTimer.Stop();
            PopulateSidebarNotesList(SidebarNoteSearchBox?.Text ?? string.Empty);
        }

        private void SidebarNoteSearchBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                _sidebarNoteSearchTimer.Stop();
                PopulateSidebarNotesList(SidebarNoteSearchBox.Text);
                var pinnedVMs = SidebarPinnedListView.ItemsSource as List<NoteItemViewModel>;
                if (pinnedVMs != null && pinnedVMs.Count > 0)
                {
                    OnNoteSelected(pinnedVMs[0].Id);
                    e.Handled = true;
                    return;
                }

                var normalVMs = SidebarNotesListView.ItemsSource as List<NoteItemViewModel>;
                if (normalVMs != null && normalVMs.Count > 0)
                {
                    OnNoteSelected(normalVMs[0].Id);
                    e.Handled = true;
                    return;
                }
            }
        }

        private void NotesFlyout_Opened(object? sender, object? e)
        {
            if (NoteSearchBox != null) NoteSearchBox.Text = string.Empty;
            PopulateNotesList();
            NoteSearchBox?.Focus(FocusState.Programmatic);
        }

        private void NotesFlyout_Closed(object? sender, object? e)
        {
        }

        private void NoteItemGrid_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Grid grid)
            {
                // アクションボタンを表示
                var actionsPanel = grid.FindName("ActionsPanel") as UIElement;
                if (actionsPanel != null) actionsPanel.Visibility = Visibility.Visible;

                // ホバー専用レイヤーを表示
                var hoverOverlay = grid.FindName("HoverOverlay") as UIElement;
                if (hoverOverlay != null) hoverOverlay.Visibility = Visibility.Visible;
            }
        }

        private void NoteItemGrid_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Grid grid)
            {
                // アクションボタンを非表示
                var actionsPanel = grid.FindName("ActionsPanel") as UIElement;
                if (actionsPanel != null) actionsPanel.Visibility = Visibility.Collapsed;

                // ホバー専用レイヤーを非表示
                var hoverOverlay = grid.FindName("HoverOverlay") as UIElement;
                if (hoverOverlay != null) hoverOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private void OnNoteSelected(string id)
        {
            SwitchToNote(id);
            if (NotesFlyout != null && NotesFlyout.IsOpen)
            {
                NotesFlyout.Hide();
            }
            else if (SidebarSplitView != null && SidebarSplitView.IsPaneOpen && !MemoStorage.IsSidebarPinned)
            {
                SidebarSplitView.IsPaneOpen = false;
            }
        }

        private void NoteItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is NoteItemViewModel vm)
            {
                OnNoteSelected(vm.Id);
            }
        }

        private void PinItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is NoteItemViewModel vm)
            {
                NoteData? note = null;
                lock (MemoStorage.Notes)
                {
                    note = MemoStorage.Notes.Find(n => n.Id == vm.Id);
                }
                if (note != null)
                {
                    lock (MemoStorage.Notes)
                    {
                        note.IsPinned = !note.IsPinned;
                    }
                    QueueSaveSettings();

                    // ハイライトの開始
                    _highlightedNoteId = note.Id;
                    _highlightTimer.Stop(); // 既に動いている場合は一旦停止
                    _highlightTimer.Start();

                    RefreshAllNotesLists();
                }
            }
        }

        private void DeleteItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is NoteItemViewModel vm)
            {
                if (vm.Id == MemoStorage.CurrentNoteId)
                {
                    DeleteCurrentNote();
                }
                else
                {
                    MemoStorage.DeleteNote(vm.Id);
                }
                RefreshAllNotesLists();
            }
        }

        private void SwitchToNote(string id, bool updateLastOpened = true)
        {
            if (id == MemoStorage.CurrentNoteId) return;

            if (_isDirty)
            {
                MemoTextBox.Document.GetText(Microsoft.UI.Text.TextGetOptions.UseLf, out string plainText);
                MemoTextBox.Document.GetText(Microsoft.UI.Text.TextGetOptions.FormatRtf, out string rtfText);
                if (plainText.EndsWith("\r") || plainText.EndsWith("\n")) 
                    plainText = plainText.Substring(0, plainText.Length - 1);

                rtfText = TrimTrailingRtfPar(rtfText);

                MemoStorage.SaveNoteTextSync(MemoStorage.CurrentNoteId, plainText, rtfText);
                _isDirty = false;
            }

            MemoStorage.SetCurrentNote(id, updateLastOpened, persistMetadata: false);
            QueueSaveSettings();

            NoteData? note = null;
            lock (MemoStorage.Notes)
            {
                note = MemoStorage.Notes.Find(n => n.Id == id);
            }

            if (note != null)
            {
                MemoTextBox.TextChanged -= MemoTextBox_TextChanged;
                _isRestoring = true;

                string rtfData = MemoStorage.LoadNoteRtf(id);
                try
                {
                    if (rtfData.StartsWith("{\\rtf1"))
                    {
                        MemoTextBox.Document.SetText(Microsoft.UI.Text.TextSetOptions.FormatRtf, rtfData);
                        // 既存の文字装飾を保護しながら、末尾の段落記号や新規入力箇所の基準サイズを設定値に合わせる
                        ApplyGlobalThemeToEditor(preserveFormatting: true);
                    }
                    else
                    {
                        MemoTextBox.Document.SetText(Microsoft.UI.Text.TextSetOptions.None, rtfData);
                        // プレーンテキストの場合は、現在のデフォルトテーマ（フォントファミリー、サイズ、行間等）を適用する。
                        ApplyGlobalThemeToEditor(preserveFormatting: false, isPlainText: true);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[RTF Load Fallback Error] {ex.Message}");
                    MemoTextBox.Document.SetText(Microsoft.UI.Text.TextSetOptions.None, rtfData);
                    ApplyGlobalThemeToEditor(preserveFormatting: false, isPlainText: true);
                }

                TitleTextBlock.Text = note.Title;
                UpdateCharCount(note.CharCount);
                UpdateHeaderTags();

                if (PlaceholderTextBlock != null)
                {
                    MemoTextBox.Document.GetText(Microsoft.UI.Text.TextGetOptions.UseLf, out string plainText);
                    if (plainText.EndsWith("\r") || plainText.EndsWith("\n"))
                        plainText = plainText.Substring(0, plainText.Length - 1);
                    PlaceholderTextBlock.Visibility = string.IsNullOrEmpty(plainText) ? Visibility.Visible : Visibility.Collapsed;
                }

                if (SidebarSplitView != null && SidebarSplitView.IsPaneOpen)
                {
                    if (_currentSidebarView == SidebarView.Tasks)
                    {
                        PopulateCurrentTasks();
                    }
                    else if (_currentSidebarView == SidebarView.Notes)
                    {
                        PopulateSidebarNotesList(SidebarNoteSearchBox.Text);
                    }
                }
                if (RightSidebarSplitView != null && RightSidebarSplitView.IsPaneOpen)
                {
                    if (_currentRightSidebarView == SidebarView.Tasks)
                    {
                        PopulateRightCurrentTasks();
                    }
                    else if (_currentRightSidebarView == SidebarView.Notes)
                    {
                        PopulateRightSidebarNotesList(RightSidebarNoteSearchBox.Text);
                    }
                }

                this.DispatcherQueue.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    () =>
                    {
                        MemoTextBox.TextChanged -= MemoTextBox_TextChanged;
                        MemoTextBox.TextChanged += MemoTextBox_TextChanged;
                        _isRestoring = false;
                        UpdateFormatButtonStates();
                    });
            }
        }

        /// <summary>
        /// Ctrl+[ で一つ前のメモに移動します（先頭の場合は移動しない）。
        /// </summary>
        private void NavigateToPreviousNote()
        {
            var ordered = GetOrderedNoteIds();
            int idx = ordered.IndexOf(MemoStorage.CurrentNoteId);
            if (idx > 0)
            {
                // ナビゲーションでは LastOpened を更新しない（順序が変わるとループするため）
                SwitchToNote(ordered[idx - 1], updateLastOpened: false);
            }
        }

        /// <summary>
        /// Ctrl+] で一つ次のメモに移動します（末尾の場合は移動しない）。
        /// </summary>
        private void NavigateToNextNote()
        {
            var ordered = GetOrderedNoteIds();
            int idx = ordered.IndexOf(MemoStorage.CurrentNoteId);
            if (idx >= 0 && idx < ordered.Count - 1)
            {
                // ナビゲーションでは LastOpened を更新しない（順序が変わるとループするため）
                SwitchToNote(ordered[idx + 1], updateLastOpened: false);
            }
        }

        /// <summary>
        /// ピン留め優先、LastOpened降順でソートしたメモIDリストを返します。
        /// </summary>
        private List<string> GetOrderedNoteIds()
        {
            List<NoteData> notes;
            lock (MemoStorage.Notes)
            {
                notes = new List<NoteData>(MemoStorage.Notes);
            }
            // ピン留め→通常の順、各グループ内は作成日時（Id）数値降順
            notes.Sort((a, b) =>
            {
                if (a.IsPinned != b.IsPinned) return a.IsPinned ? -1 : 1;
                if (long.TryParse(a.Id, out long aTicks) && long.TryParse(b.Id, out long bTicks))
                {
                    return bTicks.CompareTo(aTicks);
                }
                return string.Compare(b.Id, a.Id, StringComparison.Ordinal);
            });
            return notes.ConvertAll(n => n.Id);
        }

        private List<NoteData> GetFilteredNotes(string filter, string tagFilter = "")
        {
            string query = filter.Trim();
            List<NoteData> notes;
            lock (MemoStorage.Notes)
            {
                notes = new List<NoteData>(MemoStorage.Notes);
            }

            var filteredNotes = new List<NoteData>(notes.Count);
            foreach (var note in notes)
            {
                if (string.IsNullOrEmpty(query))
                {
                    if (!string.IsNullOrEmpty(tagFilter))
                    {
                        if (tagFilter == "Untagged" && note.Tags.Count > 0) continue;
                        if (tagFilter != "Untagged" && !note.Tags.Contains(tagFilter, StringComparer.OrdinalIgnoreCase)) continue;
                    }
                    filteredNotes.Add(note);
                    continue;
                }
                // ファイル I/O は Notes のロック外で行い、検索中に保存処理を止めない。
                MemoStorage.EnsureNoteLoaded(note);
                if (!note.Title.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !note.Content.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(tagFilter))
                {
                    if (tagFilter == "Untagged" && note.Tags.Count > 0) continue;
                    if (tagFilter != "Untagged" && !note.Tags.Contains(tagFilter, StringComparer.OrdinalIgnoreCase)) continue;
                }

                filteredNotes.Add(note);
            }

            return filteredNotes;
        }

        private void PopulateNotesList(string filter = "")
        {
            var query = filter.Trim();
            var pinnedVMs = new List<NoteItemViewModel>();
            var normalVMs = new List<NoteItemViewModel>();
            var recentVMs = new List<NoteItemViewModel>();

            List<NoteData> filteredNotes = GetFilteredNotes(query);

            // Pinned/Notes 用に作成日時（Id）の数値降順でソート（順番が変わらないようにするため）
            filteredNotes.Sort((a, b) =>
            {
                if (long.TryParse(a.Id, out long aTicks) && long.TryParse(b.Id, out long bTicks))
                {
                    return bTicks.CompareTo(aTicks);
                }
                return string.Compare(b.Id, a.Id, StringComparison.Ordinal);
            });

            foreach (var note in filteredNotes)
            {
                bool isCurrent = note.Id == MemoStorage.CurrentNoteId;
                string subtitle = isCurrent
                    ? $"Current • {note.CharCount} characters"
                    : $"{GetRelativeTimeText(note.LastOpened)} • {note.CharCount} characters";

                bool isHighlighted = note.Id == _highlightedNoteId;
                var vm = new NoteItemViewModel(note.Id, note.Title, subtitle, note.IsPinned, isCurrent, isHighlighted);

                if (note.IsPinned) pinnedVMs.Add(vm);
                else normalVMs.Add(vm);
            }

            // Recent 用に LastOpened 降順でソート
            if (MemoStorage.RecentNotesCount > 0)
            {
                var recentNotes = new List<NoteData>(filteredNotes);
                recentNotes.Sort((a, b) => b.LastOpened.CompareTo(a.LastOpened));
                foreach (var note in recentNotes.Take(MemoStorage.RecentNotesCount))
                {
                    bool isCurrent = note.Id == MemoStorage.CurrentNoteId;
                    string subtitle = isCurrent
                        ? $"Current • {note.CharCount} characters"
                        : $"{GetRelativeTimeText(note.LastOpened)} • {note.CharCount} characters";

                    bool isHighlighted = note.Id == _highlightedNoteId;
                    var vm = new NoteItemViewModel(note.Id, note.Title, subtitle, note.IsPinned, isCurrent, isHighlighted);
                    recentVMs.Add(vm);
                }
            }

            if (RecentListView != null)
            {
                var currentList = RecentListView.ItemsSource as IList<NoteItemViewModel>;
                if (!AreNoteListsEqual(currentList, recentVMs))
                {
                    RecentListView.ItemsSource = null;
                    RecentListView.ItemsSource = recentVMs;
                }
            }

            if (PinnedListView != null)
            {
                var currentList = PinnedListView.ItemsSource as IList<NoteItemViewModel>;
                if (!AreNoteListsEqual(currentList, pinnedVMs))
                {
                    PinnedListView.ItemsSource = null;
                    PinnedListView.ItemsSource = pinnedVMs;
                }
            }

            if (NotesListView != null)
            {
                var currentList = NotesListView.ItemsSource as IList<NoteItemViewModel>;
                if (!AreNoteListsEqual(currentList, normalVMs))
                {
                    NotesListView.ItemsSource = null;
                    NotesListView.ItemsSource = normalVMs;
                }
            }

            RecentSection.Visibility = (recentVMs.Count > 0 && MemoStorage.RecentNotesCount > 0) ? Visibility.Visible : Visibility.Collapsed;
            PinnedSection.Visibility = pinnedVMs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            NotesSection.Visibility = normalVMs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void PopulateSidebarNotesList(string filter = "")
        {
            var query = filter.Trim();
            var pinnedVMs = new List<NoteItemViewModel>();
            var normalVMs = new List<NoteItemViewModel>();
            var recentVMs = new List<NoteItemViewModel>();

            List<NoteData> filteredNotes = GetFilteredNotes(query, _activeLeftTagFilter);

            // Pinned/Notes 用に作成日時（Id）の数値降順でソート（順番が変わらないようにするため）
            filteredNotes.Sort((a, b) =>
            {
                if (long.TryParse(a.Id, out long aTicks) && long.TryParse(b.Id, out long bTicks))
                {
                    return bTicks.CompareTo(aTicks);
                }
                return string.Compare(b.Id, a.Id, StringComparison.Ordinal);
            });

            foreach (var note in filteredNotes)
            {
                bool isCurrent = note.Id == MemoStorage.CurrentNoteId;
                string subtitle = isCurrent
                    ? $"Current • {note.CharCount} characters"
                    : $"{GetRelativeTimeText(note.LastOpened)} • {note.CharCount} characters";

                bool isHighlighted = note.Id == _highlightedNoteId;
                var vm = new NoteItemViewModel(note.Id, note.Title, subtitle, note.IsPinned, isCurrent, isHighlighted);

                if (note.IsPinned) pinnedVMs.Add(vm);
                else normalVMs.Add(vm);
            }

            // Recent 用に LastOpened 降順でソート
            if (MemoStorage.RecentNotesCount > 0)
            {
                var recentNotes = new List<NoteData>(filteredNotes);
                recentNotes.Sort((a, b) => b.LastOpened.CompareTo(a.LastOpened));
                foreach (var note in recentNotes.Take(MemoStorage.RecentNotesCount))
                {
                    bool isCurrent = note.Id == MemoStorage.CurrentNoteId;
                    string subtitle = isCurrent
                        ? $"Current • {note.CharCount} characters"
                        : $"{GetRelativeTimeText(note.LastOpened)} • {note.CharCount} characters";

                    bool isHighlighted = note.Id == _highlightedNoteId;
                    var vm = new NoteItemViewModel(note.Id, note.Title, subtitle, note.IsPinned, isCurrent, isHighlighted);
                    recentVMs.Add(vm);
                }
            }

            if (SidebarRecentListView != null)
            {
                var currentList = SidebarRecentListView.ItemsSource as IList<NoteItemViewModel>;
                if (!AreNoteListsEqual(currentList, recentVMs))
                {
                    SidebarRecentListView.ItemsSource = null;
                    SidebarRecentListView.ItemsSource = recentVMs;
                }
            }
            if (SidebarPinnedListView != null)
            {
                var currentList = SidebarPinnedListView.ItemsSource as IList<NoteItemViewModel>;
                if (!AreNoteListsEqual(currentList, pinnedVMs))
                {
                    SidebarPinnedListView.ItemsSource = null;
                    SidebarPinnedListView.ItemsSource = pinnedVMs;
                }
            }
            if (SidebarNotesListView != null)
            {
                var currentList = SidebarNotesListView.ItemsSource as IList<NoteItemViewModel>;
                if (!AreNoteListsEqual(currentList, normalVMs))
                {
                    SidebarNotesListView.ItemsSource = null;
                    SidebarNotesListView.ItemsSource = normalVMs;
                }
            }

            if (SidebarRecentSection != null) SidebarRecentSection.Visibility = (recentVMs.Count > 0 && MemoStorage.RecentNotesCount > 0) ? Visibility.Visible : Visibility.Collapsed;
            if (SidebarPinnedSection != null) SidebarPinnedSection.Visibility = pinnedVMs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (SidebarNotesSection != null) SidebarNotesSection.Visibility = normalVMs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        public void RefreshAllNotesLists()
        {
            PopulateNotesList(NoteSearchBox != null ? NoteSearchBox.Text : "");
            PopulateSidebarNotesList(SidebarNoteSearchBox != null ? SidebarNoteSearchBox.Text : "");
            PopulateRightSidebarNotesList(RightSidebarNoteSearchBox != null ? RightSidebarNoteSearchBox.Text : "");
        }

        private void PopulateRightSidebarNotesList(string filter = "")
        {
            var query = filter.Trim();
            var pinnedVMs = new List<NoteItemViewModel>();
            var normalVMs = new List<NoteItemViewModel>();
            var recentVMs = new List<NoteItemViewModel>();

            List<NoteData> filteredNotes = GetFilteredNotes(query, _activeRightTagFilter);

            // Pinned/Notes 用に作成日時（Id）の数値降順でソート（順番が変わらないようにするため）
            filteredNotes.Sort((a, b) =>
            {
                if (long.TryParse(a.Id, out long aTicks) && long.TryParse(b.Id, out long bTicks))
                {
                    return bTicks.CompareTo(aTicks);
                }
                return string.Compare(b.Id, a.Id, StringComparison.Ordinal);
            });

            foreach (var note in filteredNotes)
            {
                bool isCurrent = note.Id == MemoStorage.CurrentNoteId;
                string subtitle = isCurrent
                    ? $"Current • {note.CharCount} characters"
                    : $"{GetRelativeTimeText(note.LastOpened)} • {note.CharCount} characters";

                bool isHighlighted = note.Id == _highlightedNoteId;
                var vm = new NoteItemViewModel(note.Id, note.Title, subtitle, note.IsPinned, isCurrent, isHighlighted);

                if (note.IsPinned) pinnedVMs.Add(vm);
                else normalVMs.Add(vm);
            }

            // Recent 用に LastOpened 降順でソート
            if (MemoStorage.RecentNotesCount > 0)
            {
                var recentNotes = new List<NoteData>(filteredNotes);
                recentNotes.Sort((a, b) => b.LastOpened.CompareTo(a.LastOpened));
                foreach (var note in recentNotes.Take(MemoStorage.RecentNotesCount))
                {
                    bool isCurrent = note.Id == MemoStorage.CurrentNoteId;
                    string subtitle = isCurrent
                        ? $"Current • {note.CharCount} characters"
                        : $"{GetRelativeTimeText(note.LastOpened)} • {note.CharCount} characters";

                    bool isHighlighted = note.Id == _highlightedNoteId;
                    var vm = new NoteItemViewModel(note.Id, note.Title, subtitle, note.IsPinned, isCurrent, isHighlighted);
                    recentVMs.Add(vm);
                }
            }

            if (RightSidebarRecentListView != null)
            {
                var currentList = RightSidebarRecentListView.ItemsSource as IList<NoteItemViewModel>;
                if (!AreNoteListsEqual(currentList, recentVMs))
                {
                    RightSidebarRecentListView.ItemsSource = null;
                    RightSidebarRecentListView.ItemsSource = recentVMs;
                }
            }
            if (RightSidebarPinnedListView != null)
            {
                var currentList = RightSidebarPinnedListView.ItemsSource as IList<NoteItemViewModel>;
                if (!AreNoteListsEqual(currentList, pinnedVMs))
                {
                    RightSidebarPinnedListView.ItemsSource = null;
                    RightSidebarPinnedListView.ItemsSource = pinnedVMs;
                }
            }
            if (RightSidebarNotesListView != null)
            {
                var currentList = RightSidebarNotesListView.ItemsSource as IList<NoteItemViewModel>;
                if (!AreNoteListsEqual(currentList, normalVMs))
                {
                    RightSidebarNotesListView.ItemsSource = null;
                    RightSidebarNotesListView.ItemsSource = normalVMs;
                }
            }

            if (RightSidebarRecentSection != null) RightSidebarRecentSection.Visibility = (recentVMs.Count > 0 && MemoStorage.RecentNotesCount > 0) ? Visibility.Visible : Visibility.Collapsed;
            if (RightSidebarPinnedSection != null) RightSidebarPinnedSection.Visibility = pinnedVMs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (RightSidebarNotesSection != null) RightSidebarNotesSection.Visibility = normalVMs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RightSidebarNoteSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _rightSidebarNoteSearchTimer.Stop();
            _rightSidebarNoteSearchTimer.Start();
        }

        private void RightSidebarNoteSearchTimer_Tick(DispatcherQueueTimer sender, object args)
        {
            _rightSidebarNoteSearchTimer.Stop();
            PopulateRightSidebarNotesList(RightSidebarNoteSearchBox?.Text ?? string.Empty);
        }

        private void RightSidebarNoteSearchBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                _rightSidebarNoteSearchTimer.Stop();
                PopulateRightSidebarNotesList(RightSidebarNoteSearchBox.Text);
                var pinnedVMs = RightSidebarPinnedListView.ItemsSource as List<NoteItemViewModel>;
                if (pinnedVMs != null && pinnedVMs.Count > 0)
                {
                    OnNoteSelected(pinnedVMs[0].Id);
                    e.Handled = true;
                    return;
                }

                var normalVMs = RightSidebarNotesListView.ItemsSource as List<NoteItemViewModel>;
                if (normalVMs != null && normalVMs.Count > 0)
                {
                    OnNoteSelected(normalVMs[0].Id);
                    e.Handled = true;
                    return;
                }
            }
        }

        private string GetRelativeTimeText(DateTime lastOpened)
        {
            var localTime = lastOpened.ToLocalTime();
            var now = DateTime.Now;
            var span = now - localTime;

            if (span.TotalSeconds < 0) return "Opened just now";
            if (span.TotalSeconds < 60) return "Opened just now";
            if (span.TotalMinutes < 60)
            {
                int mins = (int)span.TotalMinutes;
                return $"Opened {mins} minute{(mins > 1 ? "s" : "")} ago";
            }
            if (span.TotalHours < 24 && localTime.Date == now.Date)
            {
                int hours = (int)span.TotalHours;
                return $"Opened {hours} hour{(hours > 1 ? "s" : "")} ago";
            }
            if (localTime.Date == now.Date.AddDays(-1))
            {
                return "Opened yesterday";
            }
            if (span.TotalDays < 7)
            {
                int days = (int)span.TotalDays;
                return $"Opened {days} day{(days > 1 ? "s" : "")} ago";
            }
            return $"Opened on {localTime:MMMM d}";
        }

        private bool AreNoteListsEqual(IList<NoteItemViewModel>? listA, List<NoteItemViewModel> listB)
        {
            if (listA == null) return false;
            if (listA.Count != listB.Count) return false;
            for (int i = 0; i < listB.Count; i++)
            {
                var itemA = listA[i];
                var itemB = listB[i];
                if (itemA.Id != itemB.Id ||
                    itemA.Title != itemB.Title ||
                    itemA.Subtitle != itemB.Subtitle ||
                    itemA.IsPinned != itemB.IsPinned ||
                    itemA.IsCurrent != itemB.IsCurrent ||
                    itemA.IsHighlighted != itemB.IsHighlighted)
                {
                    return false;
                }
            }
            return true;
        }

        private void NewButton_Click(object sender, RoutedEventArgs e)
        {
            // 1. 現在表示中のメモが空の場合は、新しく作成せずそのままフォーカスする
            NoteData? currentNote = null;
            lock (MemoStorage.Notes)
            {
                currentNote = MemoStorage.Notes.Find(n => n.Id == MemoStorage.CurrentNoteId);
            }

            if (currentNote != null && string.IsNullOrWhiteSpace(currentNote.Content))
            {
                MemoTextBox.Focus(FocusState.Programmatic);
                return;
            }

            // 2. 既存のメモの中に中身が空のメモがある場合は、新規作成せずにそのメモを表示する
            NoteData? emptyNote = null;
            lock (MemoStorage.Notes)
            {
                // ロード完了かつ内容が空のメモを検索
                emptyNote = MemoStorage.Notes.Find(n => n.Id != MemoStorage.CurrentNoteId && n.Title != "Loading..." && string.IsNullOrWhiteSpace(n.Content));
            }

            if (emptyNote != null)
            {
                SwitchToNote(emptyNote.Id);
                MemoTextBox.Focus(FocusState.Programmatic);
                return;
            }

            // 3. 空のメモが存在しない場合のみ新規作成する
            if (_isDirty)
            {
                MemoTextBox.Document.GetText(Microsoft.UI.Text.TextGetOptions.UseLf, out string plainText);
                MemoTextBox.Document.GetText(Microsoft.UI.Text.TextGetOptions.FormatRtf, out string rtfText);
                if (plainText.EndsWith("\r") || plainText.EndsWith("\n")) 
                    plainText = plainText.Substring(0, plainText.Length - 1);

                rtfText = TrimTrailingRtfPar(rtfText);

                MemoStorage.SaveNoteTextSync(MemoStorage.CurrentNoteId, plainText, rtfText);
                _isDirty = false;
            }

            var newNote = MemoStorage.CreateNewNote();
            
            MemoTextBox.TextChanged -= MemoTextBox_TextChanged;
            _isRestoring = true;
            MemoTextBox.Document.SetText(Microsoft.UI.Text.TextSetOptions.None, string.Empty);
            ApplyGlobalThemeToEditor(preserveFormatting: false, isPlainText: true);

            TitleTextBlock.Text = newNote.Title;
            UpdateCharCount(0);
            UpdateHeaderTags(); // 新規メモにはタグがないためヘッダーのタグ表示をクリア

            if (PlaceholderTextBlock != null)
            {
                PlaceholderTextBlock.Visibility = Visibility.Visible;
            }

            this.DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () =>
                {
                    MemoTextBox.TextChanged -= MemoTextBox_TextChanged;
                    MemoTextBox.TextChanged += MemoTextBox_TextChanged;
                    _isRestoring = false;
                    UpdateFormatButtonStates();
                    MemoTextBox.Focus(FocusState.Programmatic);
                });
        }

        public void DeleteCurrentNote()
        {
            string currentId = MemoStorage.CurrentNoteId;
            if (string.IsNullOrEmpty(currentId)) return;

            string nextId = string.Empty;
            List<NoteData> sorted;
            lock (MemoStorage.Notes)
            {
                sorted = new List<NoteData>(MemoStorage.Notes);
            }
            sorted.Sort((a, b) => b.LastOpened.CompareTo(a.LastOpened));
            foreach (var note in sorted)
            {
                if (note.Id != currentId)
                {
                    nextId = note.Id;
                    break;
                }
            }

            MemoStorage.DeleteNote(currentId);
            _isDirty = false;

            if (!string.IsNullOrEmpty(nextId))
            {
                SwitchToNote(nextId);
            }
            else
            {
                var newNote = MemoStorage.CreateNewNote();
                
                MemoTextBox.TextChanged -= MemoTextBox_TextChanged;
                MemoTextBox.Document.SetText(Microsoft.UI.Text.TextSetOptions.None, string.Empty);
                ApplyGlobalThemeToEditor();
                TitleTextBlock.Text = newNote.Title;
                UpdateCharCount(0);
                UpdateHeaderTags(); // 新規メモにはタグがないためヘッダーのタグ表示をクリア

                this.DispatcherQueue.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    () =>
                    {
                        MemoTextBox.TextChanged -= MemoTextBox_TextChanged;
                        MemoTextBox.TextChanged += MemoTextBox_TextChanged;
                        UpdateFormatButtonStates();
                    });
            }

            MemoTextBox.Focus(FocusState.Programmatic);
        }

        private void DeleteConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            DeleteConfirmFlyout.Hide();
            DeleteCurrentNote();
        }

        private void DeleteCancelButton_Click(object sender, RoutedEventArgs e)
        {
            DeleteConfirmFlyout.Hide();
        }

        private void DeleteConfirmFlyout_Opened(object sender, object e)
        {
            DeleteCancelButton.Focus(FocusState.Programmatic);
        }

        private void MemoTextBox_TextChanged(object sender, RoutedEventArgs e)
        {
            if (_isRestoring) return;

            // 入力時はプレーンテキストのみを取得し、文字数とタイトルUIだけ更新する
            MemoTextBox.Document.GetText(Microsoft.UI.Text.TextGetOptions.UseLf, out string plainText);
            if (plainText.EndsWith("\r") || plainText.EndsWith("\n")) 
                plainText = plainText.Substring(0, plainText.Length - 1);

            NoteData? currentNote = null;
            lock (MemoStorage.Notes) { currentNote = MemoStorage.Notes.Find(n => n.Id == MemoStorage.CurrentNoteId); }

            // ロード直後の遅延イベント等による不要な _isDirty 化を防ぐため、
            // テキストに変化がない場合は早期リターンする。
            // (ただし、すでに _isDirty が true の場合は装飾変更等の可能性があるためスルーする)
            if (currentNote != null && currentNote.Content == plainText && !_isDirty)
            {
                return;
            }

            if (PlaceholderTextBlock != null)
            {
                PlaceholderTextBlock.Visibility = string.IsNullOrEmpty(plainText) ? Visibility.Visible : Visibility.Collapsed;
            }

            _isDirty = true;
            _revision++;
            _scheduler.Schedule(); // RTF生成と保存はスケジューラーに任せる

            if (currentNote != null)
            {
                lock (MemoStorage.Notes)
                {
                    currentNote.Content = plainText; // 検索用にキャッシュ
                    currentNote.Title = MemoStorage.GetTitleFromContent(plainText);
                    currentNote.CharCount = plainText.Length;
                }
                TitleTextBlock.Text = currentNote.Title; 
            }
            UpdateCharCount(plainText.Length);

            if (FindReplacePanel != null && FindReplacePanel.Visibility == Visibility.Visible)
            {
                RecalculateMatches();
            }
        }

        private void UpdateCharCount(int length)
        {
            CharCountTextBlock.Text = $"{length} characters";
        }
    }
}
