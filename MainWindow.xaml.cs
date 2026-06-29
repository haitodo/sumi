using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using Windows.Graphics;
using WinRT;

namespace sumi
{
    /// <summary>
    /// メモの入力 UI と、ウィンドウのアクティベーション・ライフサイクル制御を行うメインウィンドウクラスです。
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private readonly SaveScheduler _scheduler;
        private readonly AppWindow _appWindow;
        private bool _isRestoring = false;
        private bool _isDirty = false;
        private bool _isAlwaysOnTopSet = false;
        private bool _isInitialFocusSet = false;
        private bool _isInitializing = true;

        // 競合防止用のリビジョン番号
        private long _revision = 0;
        private long _savedRevision = 0;
        private double _targetVerticalOffset = 0;

        public MainWindow()
        {
            this.InitializeComponent();

            // 1. ウィンドウハンドルと AppWindow の解決
            IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);

            // 2. タイトルバーをクライアント領域に拡張し、ドラッグ領域を設定
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(TitleDragRegion);

            // 3. ウィンドウ配置の復元（マルチモニター・作業領域クランプ付き）
            RestoreWindowPlacement();

            // 4. メモ一覧の初期化と読込
            MemoStorage.InitializeNotes();
            MemoStorage.LoadSettings();

            // 初期設定の適用
            ApplySettings();

            var currentNote = MemoStorage.Notes.Find(n => n.Id == MemoStorage.CurrentNoteId);
            if (currentNote != null)
            {
                MemoText = currentNote.Content;
                TitleTextBlock.Text = currentNote.Title;
                UpdateCharCount(currentNote.CharCount);
            }

            // 5. スケジューラ初期化 (DispatcherQueue を渡し、タイマー内のアロケーションをゼロ化)
            _scheduler = new SaveScheduler(this.DispatcherQueue, async () =>
            {
                long currentRevision = _revision;
                string textToSave = MemoText;

                bool success = await MemoStorage.SaveMemoTextAtomicAsync(textToSave);
                if (success)
                {
                    _savedRevision = currentRevision;
                    if (_savedRevision == _revision)
                    {
                        _isDirty = false;
                    }
                }
            });

            // 6. ライフサイクルイベント監視
            this.Activated += MainWindow_Activated;
            _appWindow.Closing += AppWindow_Closing;

            // 7. メモ一覧 Flyout の初期イベントフック
            NotesFlyout.Opened += NotesFlyout_Opened;

            // 8. テキストボックスのロードイベント (スクロールバー取得用)
            MemoTextBox.Loaded += MemoTextBox_Loaded;

            // 9. グローバルショートカットキーの登録
            this.Content.AddHandler(UIElement.KeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler(Global_KeyDown), true);

            // 10. ウィンドウサイズ変更イベントの登録（Flyoutの高さ調整用）
            RootGrid.SizeChanged += RootGrid_SizeChanged;

            _isInitializing = false;
        }

        private string MemoText
        {
            get
            {
                if (MemoTextBox == null) return string.Empty;
                MemoTextBox.Document.GetText(Microsoft.UI.Text.TextGetOptions.UseLf, out string text);
                if (text.EndsWith("\r"))
                {
                    text = text.Substring(0, text.Length - 1);
                }
                else if (text.EndsWith("\n"))
                {
                    text = text.Substring(0, text.Length - 1);
                }
                return text;
            }
            set
            {
                if (MemoTextBox == null) return;
                _isRestoring = true;
                MemoTextBox.Document.SetText(Microsoft.UI.Text.TextSetOptions.None, value ?? string.Empty);
                ApplyLineSpacingToTextBox(MemoStorage.LineSpacing);
                _isRestoring = false;

                if (PlaceholderTextBlock != null)
                {
                    PlaceholderTextBlock.Visibility = string.IsNullOrEmpty(value) ? Visibility.Visible : Visibility.Collapsed;
                }
            }
        }

        private void ApplySettings()
        {
            if (MemoTextBox == null || PlaceholderTextBlock == null) return;

            var font = new Microsoft.UI.Xaml.Media.FontFamily(MemoStorage.FontFamily);
            MemoTextBox.FontFamily = font;
            PlaceholderTextBlock.FontFamily = font;

            MemoTextBox.FontSize = MemoStorage.FontSize;
            PlaceholderTextBlock.FontSize = MemoStorage.FontSize;

            var fw = GetFontWeight(MemoStorage.FontWeight);
            MemoTextBox.FontWeight = fw;
            PlaceholderTextBlock.FontWeight = fw;

            ApplyLineSpacingToTextBox(MemoStorage.LineSpacing);

            if (RootGrid != null && RootGrid.Background is Microsoft.UI.Xaml.Media.SolidColorBrush brush)
            {
                brush.Color = Microsoft.UI.ColorHelper.FromArgb(255, 0x14, 0x14, 0x14);
                brush.Opacity = MemoStorage.Opacity / 100.0;
            }
        }

        private void ApplyLineSpacingToTextBox(double spacing)
        {
            try
            {
                if (MemoTextBox == null) return;
                var document = MemoTextBox.Document;
                var range = document.GetRange(0, int.MaxValue);
                range.ParagraphFormat.SetLineSpacing(Microsoft.UI.Text.LineSpacingRule.Multiple, (float)spacing);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ApplyLineSpacing Error] {ex.Message}");
            }
        }

        private void DeleteCurrentNote()
        {
            string currentId = MemoStorage.CurrentNoteId;
            if (string.IsNullOrEmpty(currentId)) return;

            string nextId = string.Empty;
            var sorted = new List<NoteData>(MemoStorage.Notes);
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
                MemoText = string.Empty;
                TitleTextBlock.Text = newNote.Title;
                UpdateCharCount(0);
            }

            MemoTextBox.Focus(FocusState.Programmatic);
        }

        private void Global_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            var ctrlState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
            bool isCtrlDown = (ctrlState & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

            if (isCtrlDown)
            {
                if (e.Key == Windows.System.VirtualKey.D)
                {
                    DeleteCurrentNote();
                    e.Handled = true;
                }
                else if (e.Key == (Windows.System.VirtualKey)188) // Comma ','
                {
                    SettingsFlyout.ShowAt(PlayButton);
                    e.Handled = true;
                }
            }
        }

        private void RestoreWindowPlacement()
        {
            if (MemoStorage.LoadWindowPlacement(out int x, out int y, out int width, out int height))
            {
                _appWindow.MoveAndResize(new RectInt32(x, y, width, height));
            }
            else
            {
                // デフォルトサイズ（幅 360, 高さ 400）を設定
                int defaultWidth = 360;
                int defaultHeight = 400;
                
                // プライマリモニターのワークエリア（タスクバー除外領域）の中央に配置
                var displayArea = DisplayArea.Primary;
                if (displayArea != null)
                {
                    int cx = displayArea.WorkArea.X + (displayArea.WorkArea.Width - defaultWidth) / 2;
                    int cy = displayArea.WorkArea.Y + (displayArea.WorkArea.Height - defaultHeight) / 2;
                    _appWindow.MoveAndResize(new RectInt32(cx, cy, defaultWidth, defaultHeight));
                }
                else
                {
                    _appWindow.Resize(new SizeInt32(defaultWidth, defaultHeight));
                }
            }
        }

        private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            // アクティブ化が完全に確定した最初のタイミングで常に最前面を設定
            if (!_isAlwaysOnTopSet)
            {
                try
                {
                    // Native AOT 下で安全に WinRT 型変換を行うために .As<T>() を使用
                    var presenter = _appWindow.Presenter.As<OverlappedPresenter>();
                    if (presenter != null)
                    {
                        presenter.IsAlwaysOnTop = true;
                    }
                }
                catch (Exception) { }
                _isAlwaysOnTopSet = true;
            }

            if (!_isInitialFocusSet)
            {
                _isInitialFocusSet = true;
                if (MemoTextBox != null)
                {
                    MemoTextBox.Focus(FocusState.Programmatic);
                    MemoTextBox.Document.Selection.SetRange(int.MaxValue, int.MaxValue);
                }
            }
        }

        private void MemoTextBox_TextChanged(object sender, RoutedEventArgs e)
        {
            if (_isRestoring) return;

            string text = MemoText;
            if (PlaceholderTextBlock != null)
            {
                PlaceholderTextBlock.Visibility = string.IsNullOrEmpty(text) ? Visibility.Visible : Visibility.Collapsed;
            }

            _isDirty = true;
            _revision++; // 入力ごとにリビジョンを更新
            _scheduler.Schedule();

            // キャッシュデータとタイトル表示の更新
            var currentNote = MemoStorage.Notes.Find(n => n.Id == MemoStorage.CurrentNoteId);
            if (currentNote != null)
            {
                currentNote.Content = text;
                currentNote.Title = MemoStorage.GetTitleFromContent(text);
                currentNote.CharCount = text.Length;
                TitleTextBlock.Text = currentNote.Title; // ヘッダータイトルをリアルタイム更新
            }

            // 文字数のリアルタイム更新
            UpdateCharCount(text.Length);
        }

        private void UpdateCharCount(int length)
        {
            CharCountTextBlock.Text = $"{length} characters";
        }

        private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            OnShutdown();
        }

        private void OnShutdown()
        {
            _scheduler.Cancel();

            // 1. 未保存データを終了直前に同期的に安全にディスク永続化
            if (_isDirty)
            {
                MemoStorage.SaveMemoTextAtomicSync(MemoText);
            }

            // 2. 終了座標をアトミックに保存
            var pos = _appWindow.Position;
            var size = _appWindow.Size;
            MemoStorage.SaveWindowPlacementAtomic(pos.X, pos.Y, size.Width, size.Height);
        }

        private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush SettingItemHoverBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x2d, 0x2d, 0x2d));
        private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush SettingItemTransparentBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);

        private void SettingItem_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Grid grid)
            {
                grid.Background = SettingItemHoverBrush;
            }
        }

        private void SettingItem_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Grid grid)
            {
                grid.Background = SettingItemTransparentBrush;
            }
        }

        private void SettingsFlyout_Opened(object sender, object e)
        {
            UpdateFlyoutMaxHeights();
            SettingsSearchBox.Text = string.Empty;

            // ComboBox/Slider初期値設定
            foreach (var item in FontComboBox.Items)
            {
                if (item is string s && s == MemoStorage.FontFamily)
                {
                    FontComboBox.SelectedItem = item;
                    break;
                }
            }

            foreach (var item in FontWeightComboBox.Items)
            {
                if (item is string s && s == MemoStorage.FontWeight)
                {
                    FontWeightComboBox.SelectedItem = item;
                    break;
                }
            }

            FontSizeSlider.Value = MemoStorage.FontSize;
            FontSizeValueText.Text = MemoStorage.FontSize.ToString("0.0");

            string spacingStr = MemoStorage.LineSpacing.ToString("0.0");
            foreach (var item in LineSpacingComboBox.Items)
            {
                if (item is string s && s == spacingStr)
                {
                    LineSpacingComboBox.SelectedItem = item;
                    break;
                }
            }

            OpacitySlider.Value = MemoStorage.Opacity;
            OpacityValueText.Text = $"{(int)MemoStorage.Opacity}%";

            FontItem.Visibility = Visibility.Visible;
            FontWeightItem.Visibility = Visibility.Visible;
            FontSizeItem.Visibility = Visibility.Visible;
            LineSpacingItem.Visibility = Visibility.Visible;
            OpacityItem.Visibility = Visibility.Visible;
            DeleteNoteItem.Visibility = Visibility.Visible;

            SettingsSearchBox.Focus(FocusState.Programmatic);
        }

        private void SettingsSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            string query = SettingsSearchBox.Text.Trim().ToLower();
            if (string.IsNullOrEmpty(query))
            {
                FontItem.Visibility = Visibility.Visible;
                FontWeightItem.Visibility = Visibility.Visible;
                FontSizeItem.Visibility = Visibility.Visible;
                LineSpacingItem.Visibility = Visibility.Visible;
                OpacityItem.Visibility = Visibility.Visible;
                DeleteNoteItem.Visibility = Visibility.Visible;
                return;
            }

            FontItem.Visibility = "font フォント 書体".Contains(query) ? Visibility.Visible : Visibility.Collapsed;
            FontWeightItem.Visibility = "font weight フォント ウェイト 太さ 太字".Contains(query) ? Visibility.Visible : Visibility.Collapsed;
            FontSizeItem.Visibility = "font size フォントサイズ 大きさ サイズ 文字".Contains(query) ? Visibility.Visible : Visibility.Collapsed;
            LineSpacingItem.Visibility = "line spacing 行間 行の高さ 高さ".Contains(query) ? Visibility.Visible : Visibility.Collapsed;
            OpacityItem.Visibility = "opacity 不透明度 透明度 背景 透け".Contains(query) ? Visibility.Visible : Visibility.Collapsed;
            DeleteNoteItem.Visibility = "delete note メモを削除 削除 ゴミ箱".Contains(query) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void FontComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;
            if (FontComboBox.SelectedItem is string font)
            {
                MemoStorage.FontFamily = font;
                var fontFamily = new Microsoft.UI.Xaml.Media.FontFamily(font);
                MemoTextBox.FontFamily = fontFamily;
                PlaceholderTextBlock.FontFamily = fontFamily;
                MemoStorage.SaveSettings();
            }
        }

        private void FontWeightComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;
            if (FontWeightComboBox.SelectedItem is string weight)
            {
                MemoStorage.FontWeight = weight;
                var fw = GetFontWeight(weight);
                if (MemoTextBox != null)
                {
                    MemoTextBox.FontWeight = fw;
                }
                if (PlaceholderTextBlock != null)
                {
                    PlaceholderTextBlock.FontWeight = fw;
                }
                MemoStorage.SaveSettings();
            }
        }

        private Windows.UI.Text.FontWeight GetFontWeight(string weightStr)
        {
            return weightStr switch
            {
                "Light" => Microsoft.UI.Text.FontWeights.Light,
                "Medium" => Microsoft.UI.Text.FontWeights.Medium,
                "SemiBold" => Microsoft.UI.Text.FontWeights.SemiBold,
                "Bold" => Microsoft.UI.Text.FontWeights.Bold,
                _ => Microsoft.UI.Text.FontWeights.Normal,
            };
        }

        private void FontSizeDecreaseButton_Click(object sender, RoutedEventArgs e)
        {
            if (FontSizeSlider != null)
            {
                FontSizeSlider.Value = Math.Max(FontSizeSlider.Minimum, FontSizeSlider.Value - 0.5);
            }
        }

        private void FontSizeIncreaseButton_Click(object sender, RoutedEventArgs e)
        {
            if (FontSizeSlider != null)
            {
                FontSizeSlider.Value = Math.Min(FontSizeSlider.Maximum, FontSizeSlider.Value + 0.5);
            }
        }

        private void OpacityDecreaseButton_Click(object sender, RoutedEventArgs e)
        {
            if (OpacitySlider != null)
            {
                OpacitySlider.Value = Math.Max(OpacitySlider.Minimum, OpacitySlider.Value - 1);
            }
        }

        private void OpacityIncreaseButton_Click(object sender, RoutedEventArgs e)
        {
            if (OpacitySlider != null)
            {
                OpacitySlider.Value = Math.Min(OpacitySlider.Maximum, OpacitySlider.Value + 1);
            }
        }

        private void FontSizeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_isInitializing) return;
            double size = FontSizeSlider.Value;
            if (FontSizeValueText != null)
            {
                FontSizeValueText.Text = size.ToString("0.0");
            }
            MemoStorage.FontSize = size;
            if (MemoTextBox != null)
            {
                MemoTextBox.FontSize = size;
            }
            if (PlaceholderTextBlock != null)
            {
                PlaceholderTextBlock.FontSize = size;
            }
            MemoStorage.SaveSettings();
        }

        private void LineSpacingComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;
            if (LineSpacingComboBox.SelectedItem is string val && double.TryParse(val, out double ls))
            {
                MemoStorage.LineSpacing = ls;
                ApplyLineSpacingToTextBox(ls);
                MemoStorage.SaveSettings();
            }
        }

        private void OpacitySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_isInitializing) return;
            double opacity = OpacitySlider.Value;
            if (OpacityValueText != null)
            {
                OpacityValueText.Text = $"{(int)opacity}%";
            }
            MemoStorage.Opacity = opacity;
            if (RootGrid != null && RootGrid.Background is Microsoft.UI.Xaml.Media.SolidColorBrush brush)
            {
                brush.Opacity = opacity / 100.0;
            }
            MemoStorage.SaveSettings();
        }

        private void DeleteNoteItem_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            DeleteCurrentNote();
            SettingsFlyout.Hide();
        }

        /// <summary>
        /// メモ一覧ボタンクリック時のハンドラです（WinUIにより自動でFlyoutが開かれます）。
        /// </summary>
        private void NotesButton_Click(object sender, RoutedEventArgs e)
        {
        }

        private void NotesFlyout_Opened(object? sender, object? e)
        {
            UpdateFlyoutMaxHeights();
            NoteSearchBox.Text = string.Empty;
            PopulateNotesList();
            NoteSearchBox.Focus(FocusState.Programmatic);
        }

        private void NotesFlyout_Closed(object? sender, object? e)
        {
            PinnedListView.ItemsSource = null;
            NotesListView.ItemsSource = null;
        }

        private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateFlyoutMaxHeights();
        }

        private void UpdateFlyoutMaxHeights()
        {
            if (RootGrid == null) return;
            double windowHeight = RootGrid.ActualHeight;

            // ウィンドウサイズに応じた最大高さを算出（余白マージンとして120pxを確保、最小は100px）
            double maxScrollHeight = Math.Max(100, windowHeight - 120);

            if (NotesScrollViewer != null)
            {
                NotesScrollViewer.MaxHeight = maxScrollHeight;
            }
            if (SettingsScrollViewer != null)
            {
                SettingsScrollViewer.MaxHeight = maxScrollHeight;
            }
        }

        private void NoteSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            PopulateNotesList(NoteSearchBox.Text);
        }

        private void NoteSearchBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                var pinnedVMs = PinnedListView.ItemsSource as List<NoteItemViewModel>;
                if (pinnedVMs != null && pinnedVMs.Count > 0)
                {
                    SwitchToNote(pinnedVMs[0].Id);
                    NotesFlyout.Hide();
                    e.Handled = true;
                    return;
                }

                var normalVMs = NotesListView.ItemsSource as List<NoteItemViewModel>;
                if (normalVMs != null && normalVMs.Count > 0)
                {
                    SwitchToNote(normalVMs[0].Id);
                    NotesFlyout.Hide();
                    e.Handled = true;
                    return;
                }
            }
        }

        private void NoteItemGrid_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Grid grid)
            {
                var actionsPanel = grid.FindName("ActionsPanel") as UIElement;
                if (actionsPanel != null)
                {
                    actionsPanel.Visibility = Visibility.Visible;
                }
            }
        }

        private void NoteItemGrid_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Grid grid)
            {
                var actionsPanel = grid.FindName("ActionsPanel") as UIElement;
                if (actionsPanel != null)
                {
                    actionsPanel.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void NoteItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is NoteItemViewModel vm)
            {
                SwitchToNote(vm.Id);
                NotesFlyout.Hide();
            }
        }

        private void PinItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is NoteItemViewModel vm)
            {
                var note = MemoStorage.Notes.Find(n => n.Id == vm.Id);
                if (note != null)
                {
                    note.IsPinned = !note.IsPinned;
                    MemoStorage.SaveMetadata();
                    PopulateNotesList(NoteSearchBox.Text);
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
                PopulateNotesList(NoteSearchBox.Text);
            }
        }

        private void SwitchToNote(string id)
        {
            if (id == MemoStorage.CurrentNoteId) return;

            if (_isDirty)
            {
                MemoStorage.SaveNoteTextSync(MemoStorage.CurrentNoteId, MemoText);
                _isDirty = false;
            }

            MemoStorage.SetCurrentNote(id);

            var note = MemoStorage.Notes.Find(n => n.Id == id);
            if (note != null)
            {
                MemoText = note.Content;
                TitleTextBlock.Text = note.Title;
                UpdateCharCount(note.CharCount);
            }
        }

        private void PopulateNotesList(string filter = "")
        {
            var query = filter.Trim();
            var pinnedVMs = new List<NoteItemViewModel>();
            var normalVMs = new List<NoteItemViewModel>();

            var sortedNotes = new List<NoteData>(MemoStorage.Notes);
            sortedNotes.Sort((a, b) => b.LastOpened.CompareTo(a.LastOpened));

            foreach (var note in sortedNotes)
            {
                if (!string.IsNullOrEmpty(query))
                {
                    bool matchTitle = note.Title.Contains(query, StringComparison.OrdinalIgnoreCase);
                    bool matchContent = note.Content.Contains(query, StringComparison.OrdinalIgnoreCase);
                    if (!matchTitle && !matchContent)
                    {
                        continue;
                    }
                }

                bool isCurrent = note.Id == MemoStorage.CurrentNoteId;
                string subtitle = isCurrent 
                    ? $"Current • {note.CharCount} characters" 
                    : $"{GetRelativeTimeText(note.LastOpened)} • {note.CharCount} characters";

                var vm = new NoteItemViewModel(note.Id, note.Title, subtitle, note.IsPinned, isCurrent);

                if (note.IsPinned) pinnedVMs.Add(vm);
                else normalVMs.Add(vm);
            }

            PinnedListView.ItemsSource = pinnedVMs;
            NotesListView.ItemsSource = normalVMs;

            PinnedSection.Visibility = pinnedVMs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            NotesSection.Visibility = normalVMs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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

        /// <summary>
        /// 新規作成のため新しく空のメモを作成します。
        /// </summary>
        private void NewButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isDirty)
            {
                MemoStorage.SaveNoteTextSync(MemoStorage.CurrentNoteId, MemoText);
                _isDirty = false;
            }

            var newNote = MemoStorage.CreateNewNote();
            
            MemoText = string.Empty;
            TitleTextBlock.Text = newNote.Title;
            UpdateCharCount(0);

            MemoTextBox.Focus(FocusState.Programmatic);
        }

        /// <summary>
        /// メモテキストの右端折り返し設定を切り替えます。
        /// </summary>
        private void WordWrapButton_Click(object sender, RoutedEventArgs e)
        {
            MemoTextBox.TextWrapping = MemoTextBox.TextWrapping == TextWrapping.Wrap ? TextWrapping.NoWrap : TextWrapping.Wrap;
        }

        /// <summary>
        /// テキストボックスがロードされた際に、内部の ScrollViewer を取得して
        /// ホイールイベントを滑らかなスクロール用にフックします。
        /// </summary>
        private void MemoTextBox_Loaded(object sender, RoutedEventArgs e)
        {
            var scrollViewer = FindScrollViewer(MemoTextBox);
            if (scrollViewer != null)
            {
                // ScrollViewer の PointerWheelChanged イベントにハンドラーを追加
                scrollViewer.PointerWheelChanged += ScrollViewer_PointerWheelChanged;
            }
        }

        /// <summary>
        /// ScrollViewer の内部を走査して ScrollViewer コントロールを検索します。
        /// </summary>
        private ScrollViewer? FindScrollViewer(DependencyObject parent)
        {
            if (parent is ScrollViewer sv) return sv;
            int childrenCount = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childrenCount; i++)
            {
                var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
                var result = FindScrollViewer(child);
                if (result != null) return result;
            }
            return null;
        }

        /// <summary>
        /// ホイール操作時に ChangeView を用いて滑らかなスクロールを実行します。
        /// </summary>
        private void ScrollViewer_PointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            var scrollViewer = sender as ScrollViewer;
            if (scrollViewer == null) return;

            var pointerPoint = e.GetCurrentPoint(scrollViewer);
            var properties = pointerPoint.Properties;
            if (properties.IsHorizontalMouseWheel) return;

            int delta = properties.MouseWheelDelta;
            
            // スクロール速度の定義 (1ノッチ delta = 120 につき 48 ピクセル)
            double scrollAmount = -delta / 120.0 * 48.0;

            // ドラッグスクロールなどによる実際の位置のズレを同期
            if (Math.Abs(scrollViewer.VerticalOffset - _targetVerticalOffset) > 1.0)
            {
                _targetVerticalOffset = scrollViewer.VerticalOffset;
            }

            _targetVerticalOffset += scrollAmount;
            _targetVerticalOffset = Math.Clamp(_targetVerticalOffset, 0, scrollViewer.ScrollableHeight);

            // アニメーションを有効 (disableAnimation: false) にしてスクロールを実行
            scrollViewer.ChangeView(null, _targetVerticalOffset, null, false);

            e.Handled = true;
        }
    }

    /// <summary>
    /// メモアイテムのリストバインディング用のビューモデルです。
    /// </summary>
    public class NoteItemViewModel
    {
        public string Id { get; }
        public string Title { get; }
        public string Subtitle { get; }
        public bool IsPinned { get; }
        public bool IsCurrent { get; }
        public string PinIcon => IsPinned ? "\uE841" : "\uE718";
        public string PinToolTip => IsPinned ? "ピン留め解除" : "ピン留め";
        public Microsoft.UI.Xaml.Media.Brush PinForeground => IsPinned
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 255, 176, 0))
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 204, 204, 204));
        public Visibility CurrentIndicatorVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;

        public NoteItemViewModel(string id, string title, string subtitle, bool isPinned, bool isCurrent)
        {
            Id = id;
            Title = title;
            Subtitle = subtitle;
            IsPinned = isPinned;
            IsCurrent = isCurrent;
        }
    }
}
