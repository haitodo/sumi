using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using sumi.Interop;
using sumi.Services;
using Windows.Graphics;
using WinRT;

namespace sumi
{
    public sealed partial class MainWindow : Window, IDisposable
    {
        public static MainWindow? Instance { get; private set; }

        private readonly SaveScheduler _scheduler;
        private readonly SaveScheduler _taskSaveScheduler;
        private readonly HashSet<string> _dirtyTaskNoteIds = new();

        private SidebarView _currentSidebarView = SidebarView.Notes;
        private SidebarView _currentRightSidebarView = SidebarView.Notes;

        private bool _isLeftSidebarTargetOpen = false;
        private bool _isRightSidebarTargetOpen = false;

        private string _activeLeftTagFilter = string.Empty;
        private string _activeRightTagFilter = string.Empty;

        private string _sidebarTagQuery = string.Empty;
        private string _rightSidebarTagQuery = string.Empty;

        private bool _isResizing = false;
        private double _startOpenPaneLength;
        private double _startPointerPositionX;

        private bool _isRightResizing = false;
        private double _startRightOpenPaneLength;
        private double _startRightPointerPositionX;

        private enum SidebarView
        {
            Notes,
            Tasks,
            AllTasks,
            JustDoIt,
            Tags
        }

        private AppWindow _appWindow;
        private bool _isRestoring = false;
        private bool _isDirty = false;
        private bool _isInitialFocusSet = false;
        private bool _isInitializing = true;
        private IntPtr _hWnd;
        private bool _isTrayIconAdded = false;
        private NativeMethods.SUBCLASSPROC? _subclassProc;
        private bool _isQuitting = false;
        private bool _isShutdownCalled = false;

        private NoteData? _pendingNote = null;
        private long _revision = 0;
        private long _savedRevision = 0;
        private double _targetVerticalOffset = 0;

        private string? _highlightedNoteId = null;
        private readonly DispatcherTimer _highlightTimer;

        private ScrollViewer? _memoScrollViewer;
        private readonly DispatcherQueueTimer _windowPlacementTimer;
        private readonly SaveScheduler _settingsSaveScheduler;
        private readonly DispatcherQueueTimer _noteSearchTimer;
        private readonly DispatcherQueueTimer _sidebarNoteSearchTimer;
        private readonly DispatcherQueueTimer _rightSidebarNoteSearchTimer;

        // AI fields
        private static readonly HttpClient _aiHttpClient = AiService.HttpClient;
        private CancellationTokenSource? _aiRewriteCts;
        private string _lastSelectedTextForRewrite = string.Empty;
        private string _lastSelectedPromptForRewrite = string.Empty;
        private string _lastRewritePromptName = string.Empty;
        private readonly List<AiMessage> _aiRewriteChatHistory = new();

        private CancellationTokenSource? _aiRunCts;
        private readonly List<AiMessage> _aiRunChatHistory = new();
        private string _lastSelectedTextForRun = string.Empty;

        private CancellationTokenSource? _aiTaskGenCts;
        private readonly ObservableCollection<TaskPreviewItem> _aiTaskGenPreviewItems = new();
        private string _lastSelectedTextForTaskGen = string.Empty;

        private SettingsWindow? _settingsWindow;

        public MainWindow()
        {
            Instance = this;
            MemoStorage.TaskChangedAction = OnTaskChanged;
            this.InitializeComponent();
            AiTaskGenPreviewListView.ItemsSource = _aiTaskGenPreviewItems;

            _highlightTimer = new DispatcherTimer();
            _highlightTimer.Interval = TimeSpan.FromMilliseconds(1500);
            _highlightTimer.Tick += HighlightTimer_Tick;

            // ウィンドウ配置保存のデバウンスタイマー (500ms)
            _windowPlacementTimer = this.DispatcherQueue.CreateTimer();
            _windowPlacementTimer.Interval = TimeSpan.FromMilliseconds(500);
            _windowPlacementTimer.Tick += WindowPlacementTimer_Tick;

            _noteSearchTimer = this.DispatcherQueue.CreateTimer();
            _noteSearchTimer.Interval = TimeSpan.FromMilliseconds(150);
            _noteSearchTimer.Tick += NoteSearchTimer_Tick;

            _sidebarNoteSearchTimer = this.DispatcherQueue.CreateTimer();
            _sidebarNoteSearchTimer.Interval = TimeSpan.FromMilliseconds(150);
            _sidebarNoteSearchTimer.Tick += SidebarNoteSearchTimer_Tick;

            _rightSidebarNoteSearchTimer = this.DispatcherQueue.CreateTimer();
            _rightSidebarNoteSearchTimer.Interval = TimeSpan.FromMilliseconds(150);
            _rightSidebarNoteSearchTimer.Tick += RightSidebarNoteSearchTimer_Tick;

            // 設定保存のデバウンスタイマー (500ms)
            _settingsSaveScheduler = new SaveScheduler(
                this.DispatcherQueue,
                () => Task.Run(() =>
                {
                    MemoStorage.SaveSettings();
                    MemoStorage.SaveMetadata();
                }));
            _settingsSaveScheduler.Interval = TimeSpan.FromMilliseconds(500);

            // 1. ウィンドウハンドルと AppWindow の解決
            _hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hWnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);

            // アプリケーションアイコンの設定
            try
            {
                _appWindow.SetIcon(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "AppIcon.ico"));
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[AppIcon Setup Error] {ex.Message}"); }

            // ダークモードと起動アニメーション（配置変更による移動）の一時無効化を適用
            try
            {
                int useDarkMode = 1;
                NativeMethods.DwmSetWindowAttribute(_hWnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDarkMode, sizeof(int));
                int disableTransitions = 1;
                NativeMethods.DwmSetWindowAttribute(_hWnd, NativeMethods.DWMWA_TRANSITIONS_FORCEDISABLED, ref disableTransitions, sizeof(int));
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[DwmSetWindowAttribute Error] {ex.Message}"); }

            // 1.5 アクティブ化（表示）の前に常に最前面を設定して、Z-orderの再計算ちらつきを防止
            try
            {
                var presenter = _appWindow.Presenter.As<OverlappedPresenter>();
                if (presenter != null)
                {
                    presenter.IsAlwaysOnTop = true;
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[AlwaysOnTop Setup Error] {ex.Message}"); }

            // 2. タイトルバーをクライアント領域に拡張し、ドラッグ領域を設定
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(TitleDragRegion);

            // 3. ウィンドウ配置の復元（マルチモニター・作業領域クランプ付き）
            RestoreWindowPlacement();

            // 4. 設定の読込（LastNoteId を InitializeNotes より先にロードする必要があるため先行実行）
            MemoStorage.LoadSettings();

            // 5. メモ一覧の初期化と読込（LastNoteId を参照してカレントメモを決定）
            MemoStorage.InitializeNotes();

            // 初期設定の適用
            ApplySettings();

            NoteData? currentNote = null;
            lock (MemoStorage.Notes)
            {
                currentNote = MemoStorage.Notes.Find(n => n.Id == MemoStorage.CurrentNoteId);
            }

            // TTFP最適化: テキストを直接 RichEditBox に設定するとレイアウト計算がブロックされるため、
            // MemoTextBox.Loaded（初回描画完了の直後）で適用するよう保留しておく。
            _pendingNote = currentNote;

            // タイトルと文字数は軽量なので先行設定してもコストは無視できる
            if (currentNote != null)
            {
                TitleTextBlock.Text = currentNote.Title;
                UpdateCharCount(currentNote.CharCount);
                UpdateHeaderTags();
            }

            // 5. スケジューラ初期化 (DispatcherQueue を渡し、タイマー内のアロケーションをゼロ化)
            _taskSaveScheduler = new SaveScheduler(this.DispatcherQueue, SaveDirtyTasksAsync);
            _taskSaveScheduler.Interval = TimeSpan.FromMilliseconds(300);

            _scheduler = new SaveScheduler(this.DispatcherQueue, async () =>
            {
                long currentRevision = _revision;

                MemoTextBox.Document.GetText(Microsoft.UI.Text.TextGetOptions.UseLf, out string plainText);
                MemoTextBox.Document.GetText(Microsoft.UI.Text.TextGetOptions.FormatRtf, out string rtfText);

                if (plainText.EndsWith("\r")) plainText = plainText.Substring(0, plainText.Length - 1);
                else if (plainText.EndsWith("\n")) plainText = plainText.Substring(0, plainText.Length - 1);

                rtfText = TrimTrailingRtfPar(rtfText);

                bool success = await Task.Run(() => MemoStorage.SaveNoteTextAtomicAsync(MemoStorage.CurrentNoteId, plainText, rtfText));
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
            _appWindow.Changed += AppWindow_Changed;

            // 8. テキストボックスのロード／アンロードイベント (スクロールバー取得／解除用)
            MemoTextBox.Loaded += MemoTextBox_Loaded;
            MemoTextBox.Unloaded += MemoTextBox_Unloaded;

            // 9. グローバルショートカットキーの登録
            this.Content.AddHandler(UIElement.KeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler(Global_KeyDown), true);

            // 10. ウィンドウサイズ変更イベントの登録（Flyoutの高さ調整用）
            RootGrid.SizeChanged += RootGrid_SizeChanged;

            // TTFP最適化: トレイアイコン・ホットキーの登録は描画クリティカルパス外で実行。
            // Low 優先度でキューイングし、最初のフレームを描画した後に処理させる。
            this.DispatcherQueue.TryEnqueue(
                DispatcherQueuePriority.Low,
                () => InitializeTrayAndHotKeys());

            _isInitializing = false;
        }

        public void ApplySettings()
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

            if (!_isInitializing)
            {
                ApplyGlobalThemeToEditor();
            }

            double currentOpacity = MemoStorage.Opacity / 100.0;
            if (RootGrid != null && RootGrid.Background is Microsoft.UI.Xaml.Media.SolidColorBrush brush)
            {
                brush.Color = Microsoft.UI.ColorHelper.FromArgb(255, 0x14, 0x14, 0x14);
                brush.Opacity = currentOpacity;
            }

            if (App.Current.Resources.TryGetValue("SidebarBackgroundBrush", out object? sbBrushObj) && sbBrushObj is Microsoft.UI.Xaml.Media.SolidColorBrush sbBrush)
            {
                sbBrush.Opacity = currentOpacity;
            }
            if (App.Current.Resources.TryGetValue("FlyoutBackgroundBrush", out object? flBrushObj) && flBrushObj is Microsoft.UI.Xaml.Media.SolidColorBrush flBrush)
            {
                flBrush.Opacity = currentOpacity;
            }

            _activeLeftTagFilter = MemoStorage.LastSelectedTag;
            _activeRightTagFilter = MemoStorage.LastSelectedRightTag;

            // サイドバーの現在のビュー（タブ）を復元
            if (Enum.TryParse<SidebarView>(MemoStorage.LastSidebarView, out var savedView))
            {
                _currentSidebarView = savedView;
            }
            else if (MemoStorage.LastSidebarView == "RecentTasks")
            {
                _currentSidebarView = SidebarView.AllTasks;
            }
            else
            {
                _currentSidebarView = SidebarView.Notes;
            }

            // 右サイドバーの現在のビュー（タブ）を復元
            if (Enum.TryParse<SidebarView>(MemoStorage.LastRightSidebarView, out var savedRightView))
            {
                _currentRightSidebarView = savedRightView;
            }
            else
            {
                _currentRightSidebarView = SidebarView.JustDoIt;
            }

            // タイトル、インジケーター、コンテナの表示状態を更新 (左)
            if (PaneTitleTextBlock != null)
            {
                PaneTitleTextBlock.Text = _currentSidebarView switch
                {
                    SidebarView.Notes => "Notes",
                    SidebarView.Tasks => "Tasks",
                    SidebarView.AllTasks => "All Tasks",
                    SidebarView.JustDoIt => "Just Do It",
                    SidebarView.Tags => "Tags",
                    _ => ""
                };
            }

            if (NotesActiveIndicator != null) NotesActiveIndicator.Visibility = _currentSidebarView == SidebarView.Notes ? Visibility.Visible : Visibility.Collapsed;
            if (TasksActiveIndicator != null) TasksActiveIndicator.Visibility = _currentSidebarView == SidebarView.Tasks ? Visibility.Visible : Visibility.Collapsed;
            if (AllTasksActiveIndicator != null) AllTasksActiveIndicator.Visibility = _currentSidebarView == SidebarView.AllTasks ? Visibility.Visible : Visibility.Collapsed;
            if (JustDoItActiveIndicator != null) JustDoItActiveIndicator.Visibility = _currentSidebarView == SidebarView.JustDoIt ? Visibility.Visible : Visibility.Collapsed;
            if (TagsActiveIndicator != null) TagsActiveIndicator.Visibility = _currentSidebarView == SidebarView.Tags ? Visibility.Visible : Visibility.Collapsed;

            if (NotesViewContainer != null) NotesViewContainer.Visibility = _currentSidebarView == SidebarView.Notes ? Visibility.Visible : Visibility.Collapsed;
            if (TasksViewContainer != null) TasksViewContainer.Visibility = _currentSidebarView == SidebarView.Tasks ? Visibility.Visible : Visibility.Collapsed;
            if (AllTasksViewContainer != null) AllTasksViewContainer.Visibility = _currentSidebarView == SidebarView.AllTasks ? Visibility.Visible : Visibility.Collapsed;
            if (JustDoItViewContainer != null) JustDoItViewContainer.Visibility = _currentSidebarView == SidebarView.JustDoIt ? Visibility.Visible : Visibility.Collapsed;
            if (TagsViewContainer != null) TagsViewContainer.Visibility = _currentSidebarView == SidebarView.Tags ? Visibility.Visible : Visibility.Collapsed;
            if (DeleteModeButton != null) DeleteModeButton.Visibility = _currentSidebarView == SidebarView.Tasks ? Visibility.Visible : Visibility.Collapsed;

            // タイトル、インジケーター、コンテナの表示状態を更新 (右)
            if (RightPaneTitleTextBlock != null)
            {
                RightPaneTitleTextBlock.Text = _currentRightSidebarView switch
                {
                    SidebarView.Notes => "Notes",
                    SidebarView.Tasks => "Tasks",
                    SidebarView.AllTasks => "All Tasks",
                    SidebarView.JustDoIt => "Just Do It",
                    SidebarView.Tags => "Tags",
                    _ => ""
                };
            }

            if (RightNotesActiveIndicator != null) RightNotesActiveIndicator.Visibility = _currentRightSidebarView == SidebarView.Notes ? Visibility.Visible : Visibility.Collapsed;
            if (RightTasksActiveIndicator != null) RightTasksActiveIndicator.Visibility = _currentRightSidebarView == SidebarView.Tasks ? Visibility.Visible : Visibility.Collapsed;
            if (RightAllTasksActiveIndicator != null) RightAllTasksActiveIndicator.Visibility = _currentRightSidebarView == SidebarView.AllTasks ? Visibility.Visible : Visibility.Collapsed;
            if (RightJustDoItActiveIndicator != null) RightJustDoItActiveIndicator.Visibility = _currentRightSidebarView == SidebarView.JustDoIt ? Visibility.Visible : Visibility.Collapsed;
            if (RightTagsActiveIndicator != null) RightTagsActiveIndicator.Visibility = _currentRightSidebarView == SidebarView.Tags ? Visibility.Visible : Visibility.Collapsed;

            if (RightNotesViewContainer != null) RightNotesViewContainer.Visibility = _currentRightSidebarView == SidebarView.Notes ? Visibility.Visible : Visibility.Collapsed;
            if (RightTasksViewContainer != null) RightTasksViewContainer.Visibility = _currentRightSidebarView == SidebarView.Tasks ? Visibility.Visible : Visibility.Collapsed;
            if (RightAllTasksViewContainer != null) RightAllTasksViewContainer.Visibility = _currentRightSidebarView == SidebarView.AllTasks ? Visibility.Visible : Visibility.Collapsed;
            if (RightJustDoItTasksViewContainer != null) RightJustDoItTasksViewContainer.Visibility = _currentRightSidebarView == SidebarView.JustDoIt ? Visibility.Visible : Visibility.Collapsed;
            if (RightTagsViewContainer != null) RightTagsViewContainer.Visibility = _currentRightSidebarView == SidebarView.Tags ? Visibility.Visible : Visibility.Collapsed;
            if (RightDeleteModeButton != null) RightDeleteModeButton.Visibility = _currentRightSidebarView == SidebarView.Tasks ? Visibility.Visible : Visibility.Collapsed;

            SetSidebarActiveTagFilterUI();
            SetRightSidebarActiveTagFilterUI();

            if (SidebarSplitView != null)
            {
                SidebarSplitView.DisplayMode = MemoStorage.IsSidebarPinned ? SplitViewDisplayMode.CompactInline : SplitViewDisplayMode.CompactOverlay;
                SidebarSplitView.OpenPaneLength = MemoStorage.SidebarWidth;
                
                // 開閉状態の復元
                SidebarSplitView.IsPaneOpen = MemoStorage.IsSidebarOpen;
                if (MemoStorage.IsSidebarOpen)
                {
                    PopulateSidebarView(_currentSidebarView);
                }
            }
            if (PinSidebarButton != null)
            {
                PinSidebarButton.IsChecked = MemoStorage.IsSidebarPinned;
                if (SidebarPinFilledIcon != null)
                {
                    SidebarPinFilledIcon.Visibility = MemoStorage.IsSidebarPinned ? Visibility.Visible : Visibility.Collapsed;
                }
            }

            if (RightSidebarSplitView != null)
            {
                RightSidebarSplitView.DisplayMode = MemoStorage.IsRightSidebarPinned ? SplitViewDisplayMode.CompactInline : SplitViewDisplayMode.CompactOverlay;
                RightSidebarSplitView.OpenPaneLength = MemoStorage.RightSidebarWidth;
                RightSidebarSplitView.IsPaneOpen = MemoStorage.IsRightSidebarOpen;
                if (MemoStorage.IsRightSidebarOpen)
                {
                    PopulateRightSidebarView(_currentRightSidebarView);
                }
            }
            if (PinRightSidebarButton != null)
            {
                PinRightSidebarButton.IsChecked = MemoStorage.IsRightSidebarPinned;
                if (RightSidebarPinFilledIcon != null)
                {
                    RightSidebarPinFilledIcon.Visibility = MemoStorage.IsRightSidebarPinned ? Visibility.Visible : Visibility.Collapsed;
                }
            }

            // サイドバー開閉ターゲット状態を同期します。
            _isLeftSidebarTargetOpen = MemoStorage.IsSidebarOpen;
            _isRightSidebarTargetOpen = MemoStorage.IsRightSidebarOpen;

            // サイドバー開閉トグルボタンの状態を初期更新します。
            UpdateSidebarToggleButtonState();
        }

        private void RestoreWindowPlacement()
        {
            if (MemoStorage.LoadWindowPlacement(_hWnd, out int x, out int y, out int width, out int height))
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
            // 起動時の配置変更に伴うアニメーション一時無効化を復元
            try
            {
                int disableTransitions = 0;
                NativeMethods.DwmSetWindowAttribute(_hWnd, NativeMethods.DWMWA_TRANSITIONS_FORCEDISABLED, ref disableTransitions, sizeof(int));
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Restore Transitions Error] {ex.Message}"); }

            if (!_isInitialFocusSet)
            {
                _isInitialFocusSet = true;
                if (MemoTextBox != null)
                {
                    MemoTextBox.Focus(FocusState.Programmatic);
                    int pos = GetCaretEndPosition();
                    MemoTextBox.Document.Selection.SetRange(pos, pos);
                }
            }
        }

        private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (_isQuitting || string.IsNullOrEmpty(MemoStorage.LaunchHotKey))
            {
                OnShutdown();
            }
            else
            {
                // 非表示モードでも未保存の装飾データ（太字・ハイライト等）を確実に保存する
                // デバウンス中（_isDirty=true）でも RTF を同期保存し、再表示時に装飾が失われないようにする
                if (_isDirty)
                {
                    try
                    {
                        MemoTextBox.Document.GetText(Microsoft.UI.Text.TextGetOptions.UseLf, out string plainText);
                        MemoTextBox.Document.GetText(Microsoft.UI.Text.TextGetOptions.FormatRtf, out string rtfText);
                        if (plainText.EndsWith("\r") || plainText.EndsWith("\n"))
                            plainText = plainText.Substring(0, plainText.Length - 1);
                        rtfText = TrimTrailingRtfPar(rtfText);
                        MemoStorage.SaveNoteTextSync(MemoStorage.CurrentNoteId, plainText, rtfText);
                        _isDirty = false;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Hide Save Error] {ex.Message}");
                    }
                }
                // 保存完了後はデバウンスタイマーをキャンセルし、保存コールバックが二重起動しないようにする
                _scheduler?.Cancel();

                // 非表示にする前に、デバウンス中の座標を確定して保存
                _windowPlacementTimer.Stop();
                SaveCurrentWindowPlacement();

                // 現在表示しているメモIDを記録して設定に保存
                MemoStorage.LastNoteId = MemoStorage.CurrentNoteId;
                MemoStorage.SaveSettings();

                args.Cancel = true;
                _appWindow.Hide();
            }
        }

        private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (args.DidPositionChange || args.DidSizeChange)
            {
                // ウィンドウ移動・リサイズ中はタイマーをリセットしてデバウンス
                _windowPlacementTimer.Stop();
                _windowPlacementTimer.Start();
            }
        }

        private void SaveCurrentWindowPlacement()
        {
            try
            {
                if (_appWindow == null) return;
                var presenter = _appWindow.Presenter as OverlappedPresenter;
                if (presenter != null && presenter.State != OverlappedPresenterState.Restored)
                {
                    // ウィンドウが最大化または最小化されている場合は、通常表示時のサイズと位置を壊さないように保存をスキップ
                    return;
                }

                var pos = _appWindow.Position;
                var size = _appWindow.Size;
                if (size.Width > 100 && size.Height > 100 && pos.X > -10000 && pos.Y > -10000)
                {
                    MemoStorage.SaveWindowPlacementAtomic(pos.X, pos.Y, size.Width, size.Height);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SaveWindowPlacement Error] {ex.Message}");
            }
        }

        private void WindowPlacementTimer_Tick(DispatcherQueueTimer sender, object args)
        {
            _windowPlacementTimer.Stop();
            if (_appWindow.IsVisible)
            {
                SaveCurrentWindowPlacement();
            }
        }

        public void QueueSaveSettings()
        {
            _settingsSaveScheduler.Schedule();
        }

        private void MarkAsDirty()
        {
            _isDirty = true;
            _revision++;
            _scheduler?.Schedule();
        }

        private void Global_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Handled) return;

            var ctrlState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
            bool isCtrlDown = (ctrlState & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
            var shiftState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
            bool isShiftDown = (shiftState & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

            if (isCtrlDown)
            {
                if (e.Key == Windows.System.VirtualKey.F)
                {
                    ShowFindReplace(showReplace: false);
                    e.Handled = true;
                }
                else if (e.Key == Windows.System.VirtualKey.H)
                {
                    if (isShiftDown)
                    {
                        ToggleHighlight();
                    }
                    else
                    {
                        ShowFindReplace(showReplace: true);
                    }
                    e.Handled = true;
                }
                else if (e.Key == Windows.System.VirtualKey.D)
                {
                    DeleteCurrentNote();
                    e.Handled = true;
                }
                else if (e.Key == (Windows.System.VirtualKey)188) // Comma ','
                {
                    OpenSettingsWindow();
                    e.Handled = true;
                }
                else if (e.Key == (Windows.System.VirtualKey)219) // '['
                {
                    NavigateToPreviousNote();
                    e.Handled = true;
                }
                else if (e.Key == (Windows.System.VirtualKey)221) // ']'
                {
                    NavigateToNextNote();
                    e.Handled = true;
                }
            }
        }

        private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateFlyoutMaxHeights();

            if (FindReplacePanel != null && FindReplacePanel.Visibility == Visibility.Visible)
            {
                // ウィンドウリサイズ時はパネル位置を右端基準に再計算
                _flyoutCurrentX = MemoTextBox.ActualWidth - 302;
                _flyoutCurrentY = 12;
                SetAnchorPosition(_flyoutCurrentX, _flyoutCurrentY);
            }
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
        }

        private void OnShutdown()
        {
            // 二重呼び出しを防止（AppWindow_Closing が複数回発火するケースへの対策）
            if (_isShutdownCalled) return;
            _isShutdownCalled = true;

            // デバウンス保存タイマーを即座にキャンセルし、終了処理中の非同期保存コールバックと競合させない
            _scheduler?.Cancel();
            _taskSaveScheduler?.Cancel();

            // 未保存タスクデータを終了直前に同期的に永続化
            lock (_dirtyTaskNoteIds)
            {
                if (_dirtyTaskNoteIds.Count > 0)
                {
                    foreach (var noteId in _dirtyTaskNoteIds)
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
                            MemoStorage.SaveTasksSync(noteId, tasksToSave);
                        }
                    }
                    _dirtyTaskNoteIds.Clear();
                    MemoStorage.SaveMetadata();
                }
            }

            // 1. 未保存データを終了直前に同期的に安全にディスク永続化
            if (_isDirty)
            {
                string plainText = string.Empty;
                string rtfText = string.Empty;
                bool gotText = false;
                try
                {
                    if (MemoTextBox != null)
                    {
                        MemoTextBox.Document.GetText(Microsoft.UI.Text.TextGetOptions.UseLf, out plainText);
                        MemoTextBox.Document.GetText(Microsoft.UI.Text.TextGetOptions.FormatRtf, out rtfText);
                        if (plainText.EndsWith("\r")) plainText = plainText.Substring(0, plainText.Length - 1);
                        else if (plainText.EndsWith("\n")) plainText = plainText.Substring(0, plainText.Length - 1);
                        rtfText = TrimTrailingRtfPar(rtfText);
                        gotText = true;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Shutdown Text Extraction Error] {ex.Message}");
                }

                if (gotText)
                {
                    MemoStorage.SaveNoteTextSync(MemoStorage.CurrentNoteId, plainText, rtfText);
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[Shutdown Fallback] GetText failed; skipping save to preserve existing RTF.");
                }
            }

            // 終了直前に「現在表示しているメモ」の最終閲覧日時を最新に更新し、確実に記録する
            if (!string.IsNullOrEmpty(MemoStorage.CurrentNoteId))
            {
                MemoStorage.SetCurrentNote(MemoStorage.CurrentNoteId, updateLastOpened: true);
            }

            // 保留中の設定変更を即座に書き込み
            MemoStorage.SaveSettings();

            // 2. 終了座標をアトミックに保存
            SaveCurrentWindowPlacement();

            // 3. 全てのリソース解放（タイマー・イベント・Win32APIフック）を実行
            Dispose();
        }

        private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush SettingItemHoverBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(20, 255, 255, 255));
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

        public void OpenSettingsWindow()
        {
            if (_settingsWindow != null)
            {
                // 既存ウィンドウを TOPMOST にして前面に持ってくる
                var existingHwnd = WinRT.Interop.WindowNative.GetWindowHandle(_settingsWindow);
                NativeMethods.SetWindowPos(existingHwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_SHOWWINDOW);
                _settingsWindow.Activate();
                NativeMethods.SetForegroundWindow(existingHwnd);
                return;
            }

            // メインウィンドウの位置・サイズを取得して初期位置を決める
            var mainPos  = _appWindow.Position;
            var mainSize = _appWindow.Size;

            _settingsWindow = new SettingsWindow();
            _settingsWindow.Closed += (s, e) => { _settingsWindow = null; };

            // まず Activate して HWND を確定させる
            _settingsWindow.Activate();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_settingsWindow);

            // DPIスケールを取得して物理ピクセルに変換（高DPI環境での小さすぎる問題を防ぐ）
            uint dpi = NativeMethods.GetDpiForWindow(hwnd);
            double dpiScale = dpi / 96.0;
            // 論理サイズ（96DPI基準）をDPIスケールで物理ピクセルに変換
            int settingsW = (int)Math.Round(680 * dpiScale);
            int settingsH = (int)Math.Round(520 * dpiScale);
            // メインウィンドウの中央付近に配置
            int initX = mainPos.X + (mainSize.Width  - settingsW) / 2;
            int initY = mainPos.Y + (mainSize.Height - settingsH) / 2;

            // メインウィンドウが AlwaysOnTop (HWND_TOPMOST) なので
            // 設定ウィンドウも TOPMOST にしないと背面に回ってしまう
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, initX, initY, settingsW, settingsH, NativeMethods.SWP_SHOWWINDOW);
            NativeMethods.SetForegroundWindow(hwnd);
        }

        private void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            OpenSettingsWindow();
        }

        #region IDisposable Implementation

        private bool _disposedValue;

        private void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    // マネージドリソースの解放
                    _scheduler?.Cancel();
                    _scheduler?.Dispose();
                    _taskSaveScheduler?.Cancel();
                    _taskSaveScheduler?.Dispose();
                    _settingsSaveScheduler?.Cancel();
                    _settingsSaveScheduler?.Dispose();

                    if (_windowPlacementTimer != null)
                    {
                        _windowPlacementTimer.Stop();
                        _windowPlacementTimer.Tick -= WindowPlacementTimer_Tick;
                    }

                    _noteSearchTimer.Stop();
                    _noteSearchTimer.Tick -= NoteSearchTimer_Tick;
                    _sidebarNoteSearchTimer.Stop();
                    _sidebarNoteSearchTimer.Tick -= SidebarNoteSearchTimer_Tick;
                    _rightSidebarNoteSearchTimer.Stop();
                    _rightSidebarNoteSearchTimer.Tick -= RightSidebarNoteSearchTimer_Tick;

                    if (_highlightTimer != null)
                    {
                        _highlightTimer.Stop();
                        _highlightTimer.Tick -= HighlightTimer_Tick;
                    }

                    if (_memoScrollViewer != null)
                    {
                        _memoScrollViewer.PointerWheelChanged -= ScrollViewer_PointerWheelChanged;
                        _memoScrollViewer = null;
                    }

                    if (MemoTextBox != null)
                    {
                        MemoTextBox.Loaded -= MemoTextBox_Loaded;
                        MemoTextBox.Unloaded -= MemoTextBox_Unloaded;
                    }

                    if (RootGrid != null)
                    {
                        RootGrid.SizeChanged -= RootGrid_SizeChanged;
                    }

                    if (this.Content != null)
                    {
                        this.Content.RemoveHandler(UIElement.KeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler(Global_KeyDown));
                    }

                    if (_appWindow != null)
                    {
                        _appWindow.Closing -= AppWindow_Closing;
                        _appWindow.Changed -= AppWindow_Changed;
                    }
                }

                // アンマネージドリソース（トレイアイコン、ホットキー、サブクラスなど）の解放
                RemoveTrayIcon();
                if (_hWnd != IntPtr.Zero)
                {
                    NativeMethods.UnregisterHotKey(_hWnd, NativeMethods.HOTKEY_ID_QUIT);
                    NativeMethods.UnregisterHotKey(_hWnd, NativeMethods.HOTKEY_ID_LAUNCH);
                    if (_subclassProc != null)
                    {
                        NativeMethods.RemoveWindowSubclass(_hWnd, _subclassProc, (IntPtr)NativeMethods.SUBCLASS_ID);
                        _subclassProc = null;
                    }
                    _hWnd = IntPtr.Zero;
                }

                _disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        #endregion
    }
}
