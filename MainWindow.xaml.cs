using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using System;
using System.Linq;
using Windows.Graphics;
using WinRT;

namespace sumi
{
    /// <summary>
    /// メモの入力 UI と、ウィンドウのアクティベーション・ライフサイクル制御を行うメインウィンドウクラスです。
    /// </summary>
    public sealed partial class MainWindow : Window, IDisposable
    {
        public static MainWindow? Instance { get; private set; }
        private readonly SaveScheduler _scheduler;
        private readonly SaveScheduler _taskSaveScheduler;
        private readonly HashSet<string> _dirtyTaskNoteIds = new();
        private SidebarView _currentSidebarView = SidebarView.Notes;
        private SidebarView _currentRightSidebarView = SidebarView.JustDoIt;
        private bool _isLeftSidebarTargetOpen;
        private bool _isRightSidebarTargetOpen;
        private bool _isResizing;
        private double _startOpenPaneLength;
        private double _startPointerPositionX;

        private enum SidebarView
        {
            Notes,
            Tasks,
            AllTasks,
            JustDoIt
        }

        private bool _isRightResizing;
        private double _startRightOpenPaneLength;
        private double _startRightPointerPositionX;

        private readonly AppWindow _appWindow;
        private bool _isRestoring = false;
        private bool _isDirty = false;
        private bool _isInitialFocusSet = false;
        private bool _isInitializing = true;
        private bool _isInitializingSettings = false;
        private IntPtr _hWnd = IntPtr.Zero;
        private bool _isTrayIconAdded = false;
        private SUBCLASSPROC? _subclassProc;
        private bool _isQuitting = false;
        private bool _isShutdownCalled = false; // OnShutdown の二重呼び出し防止フラグ

        // 起動時テキスト遅延適用用: Loaded イベントで RichEditBox に流し込む
        private NoteData? _pendingNote = null;

        // 競合防止用のリビジョン番号
        private long _revision = 0;
        private long _savedRevision = 0;
        private double _targetVerticalOffset = 0;

        private string? _highlightedNoteId;
        private readonly DispatcherTimer _highlightTimer;

        private ScrollViewer? _memoScrollViewer;
        private readonly DispatcherQueueTimer _windowPlacementTimer;
        private readonly DispatcherQueueTimer _settingsSaveTimer;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;

        private const int HOTKEY_ID_QUIT = 1001;
        private const int HOTKEY_ID_LAUNCH = 1002;
        private const uint WM_HOTKEY = 0x0312;
        private const uint WM_LBUTTONUP = 0x0202;
        private const uint WM_LBUTTONDBLCLK = 0x0203;
        private const uint WM_RBUTTONUP = 0x0205;
        private const uint TRAY_ICON_ID = 1;
        private const int SUBCLASS_ID = 1;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        public MainWindow()
        {
            Instance = this;
            MemoStorage.TaskChangedAction = OnTaskChanged;
            this.InitializeComponent();

            _highlightTimer = new DispatcherTimer();
            _highlightTimer.Interval = TimeSpan.FromMilliseconds(1500);
            _highlightTimer.Tick += HighlightTimer_Tick;

            // ウィンドウ配置保存のデバウンスタイマー (500ms)
            _windowPlacementTimer = this.DispatcherQueue.CreateTimer();
            _windowPlacementTimer.Interval = TimeSpan.FromMilliseconds(500);
            _windowPlacementTimer.Tick += WindowPlacementTimer_Tick;

            // 設定保存のデバウンスタイマー (500ms)
            _settingsSaveTimer = this.DispatcherQueue.CreateTimer();
            _settingsSaveTimer.Interval = TimeSpan.FromMilliseconds(500);
            _settingsSaveTimer.Tick += SettingsSaveTimer_Tick;

            // 1. ウィンドウハンドルと AppWindow の解決
            _hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(_hWnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);

            // アプリケーションアイコンの設定
            try
            {
                _appWindow.SetIcon(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "AppIcon.ico"));
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[AppIcon Setup Error] {ex.Message}"); }

            // ダークモードと起動アニメーション（配置変更による移動）の一時無効化を適用
            try
            {
                int useDarkMode = 1;
                DwmSetWindowAttribute(_hWnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDarkMode, sizeof(int));
                int disableTransitions = 1;
                DwmSetWindowAttribute(_hWnd, DWMWA_TRANSITIONS_FORCEDISABLED, ref disableTransitions, sizeof(int));
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

                bool success = await Task.Run(async () => await MemoStorage.SaveNoteTextAtomicAsync(MemoStorage.CurrentNoteId, plainText, rtfText));
                if (success)
                {
                    _savedRevision = currentRevision;
                    if (_savedRevision == _revision)
                    {
                        _isDirty = false;
                    }

                    // ★タイピングが一段落して保存が完了したタイミングで一時オブジェクトをクリーンアップ
                    this.DispatcherQueue.TryEnqueue(
                        Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                        () =>
                        {
                            System.GC.Collect();
                            System.GC.WaitForPendingFinalizers();
                        });
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
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => InitializeTrayAndHotKeys());

            _isInitializing = false;
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
                    _ => ""
                };
            }

            if (NotesActiveIndicator != null) NotesActiveIndicator.Visibility = _currentSidebarView == SidebarView.Notes ? Visibility.Visible : Visibility.Collapsed;
            if (TasksActiveIndicator != null) TasksActiveIndicator.Visibility = _currentSidebarView == SidebarView.Tasks ? Visibility.Visible : Visibility.Collapsed;
            if (AllTasksActiveIndicator != null) AllTasksActiveIndicator.Visibility = _currentSidebarView == SidebarView.AllTasks ? Visibility.Visible : Visibility.Collapsed;
            if (JustDoItActiveIndicator != null) JustDoItActiveIndicator.Visibility = _currentSidebarView == SidebarView.JustDoIt ? Visibility.Visible : Visibility.Collapsed;

            if (NotesViewContainer != null) NotesViewContainer.Visibility = _currentSidebarView == SidebarView.Notes ? Visibility.Visible : Visibility.Collapsed;
            if (TasksViewContainer != null) TasksViewContainer.Visibility = _currentSidebarView == SidebarView.Tasks ? Visibility.Visible : Visibility.Collapsed;
            if (AllTasksViewContainer != null) AllTasksViewContainer.Visibility = _currentSidebarView == SidebarView.AllTasks ? Visibility.Visible : Visibility.Collapsed;
            if (JustDoItViewContainer != null) JustDoItViewContainer.Visibility = _currentSidebarView == SidebarView.JustDoIt ? Visibility.Visible : Visibility.Collapsed;
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
                    _ => ""
                };
            }

            if (RightNotesActiveIndicator != null) RightNotesActiveIndicator.Visibility = _currentRightSidebarView == SidebarView.Notes ? Visibility.Visible : Visibility.Collapsed;
            if (RightTasksActiveIndicator != null) RightTasksActiveIndicator.Visibility = _currentRightSidebarView == SidebarView.Tasks ? Visibility.Visible : Visibility.Collapsed;
            if (RightAllTasksActiveIndicator != null) RightAllTasksActiveIndicator.Visibility = _currentRightSidebarView == SidebarView.AllTasks ? Visibility.Visible : Visibility.Collapsed;
            if (RightJustDoItActiveIndicator != null) RightJustDoItActiveIndicator.Visibility = _currentRightSidebarView == SidebarView.JustDoIt ? Visibility.Visible : Visibility.Collapsed;

            if (RightNotesViewContainer != null) RightNotesViewContainer.Visibility = _currentRightSidebarView == SidebarView.Notes ? Visibility.Visible : Visibility.Collapsed;
            if (RightTasksViewContainer != null) RightTasksViewContainer.Visibility = _currentRightSidebarView == SidebarView.Tasks ? Visibility.Visible : Visibility.Collapsed;
            if (RightAllTasksViewContainer != null) RightAllTasksViewContainer.Visibility = _currentRightSidebarView == SidebarView.AllTasks ? Visibility.Visible : Visibility.Collapsed;
            if (RightJustDoItTasksViewContainer != null) RightJustDoItTasksViewContainer.Visibility = _currentRightSidebarView == SidebarView.JustDoIt ? Visibility.Visible : Visibility.Collapsed;
            if (RightDeleteModeButton != null) RightDeleteModeButton.Visibility = _currentRightSidebarView == SidebarView.Tasks ? Visibility.Visible : Visibility.Collapsed;

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

        /// <param name="preserveFormatting">
        /// true の場合、RTF 読み込み直後に呼ばれるケース向けに、個々の文字サイズ・ウェイトの上書きをスキップする。
        /// </param>
        /// <param name="isPlainText">
        /// true の場合、プレーンテキストとして全体に一括適用し、重い UpdateRangeWeight をスキップします。
        /// </param>
        private void ApplyGlobalThemeToEditor(bool preserveFormatting = false, bool isPlainText = false)
        {
            if (MemoTextBox == null) return;

            var doc = MemoTextBox.Document;
            doc.BatchDisplayUpdates();
            try
            {
                // 【修正 1】SetDefaultCharacterFormat はRTFの装飾を破壊するため、必ず !preserveFormatting の中でのみ実行します
                if (!preserveFormatting)
                {
                    var defaultFormat = doc.GetDefaultCharacterFormat();
                    if (defaultFormat != null)
                    {
                        defaultFormat.Name = MemoStorage.FontFamily;
                        defaultFormat.Size = (float)MemoStorage.FontSize;
                        defaultFormat.Weight = GetDefaultFontWeight();
                        doc.SetDefaultCharacterFormat(defaultFormat);
                    }
                }

                var range = doc.GetRange(0, int.MaxValue);

                if (!preserveFormatting)
                {
                    range.CharacterFormat.Name = MemoStorage.FontFamily;
                    range.CharacterFormat.Size = (float)MemoStorage.FontSize;

                    ushort defaultWeight = GetDefaultFontWeight();
                    ushort boldWeight = GetBoldFontWeight();

                    if (isPlainText)
                    {
                        range.CharacterFormat.Weight = defaultWeight;
                    }
                    else
                    {
                        UpdateRangeWeight(doc, 0, range.Length, defaultWeight, boldWeight);
                    }

                    var selection = doc.Selection;
                    if (selection != null)
                    {
                        var selBold = selection.CharacterFormat.Bold;
                        var selSize = selection.CharacterFormat.Size;
                        var selWeight = selection.CharacterFormat.Weight;

                        bool isBoldOrHeading = (selBold == Microsoft.UI.Text.FormatEffect.On || selSize == 24 || selSize == 18);
                        ushort targetWeight = isBoldOrHeading ? boldWeight : defaultWeight;
                        if (selWeight != targetWeight)
                        {
                            selection.CharacterFormat.Weight = targetWeight;
                        }
                    }
                }
                else
                {
                    // 【修正 2】RTFロード時（装飾保護モード）は、一番最後の隠し改行文字とカーソル位置のみをターゲットにする
                    int endPos = range.Length;
                    if (endPos > 0)
                    {
                        // 最後の1文字（自動生成された改行）を取得し、フォント名とサイズだけを設定値に合わせる
                        // ※太字(Weight)や色には触れないため、直前の文字の装飾が消えることはありません
                        var endRange = doc.GetRange(endPos - 1, endPos);
                        endRange.CharacterFormat.Name = MemoStorage.FontFamily;
                        endRange.CharacterFormat.Size = (float)MemoStorage.FontSize;

                        // 末尾のカーソル位置（0文字幅）に対しても適用
                        var endPointRange = doc.GetRange(endPos, endPos);
                        endPointRange.CharacterFormat.Name = MemoStorage.FontFamily;
                        endPointRange.CharacterFormat.Size = (float)MemoStorage.FontSize;
                    }
                }

                float lineSpacing = (float)MemoStorage.LineSpacing;
                if (lineSpacing < 1.0f)
                {
                    // 文書全体の行間を一括で固定値に設定すると、H1やH2の段落で文字が押し潰されて重なってしまいます。
                    // 段落をループ処理し、それぞれのフォントサイズに基づいた適切な Exactly 行高を個別に適用します。
                    var paraRange = doc.GetRange(0, 0);
                    while (true)
                    {
                        paraRange.Expand(Microsoft.UI.Text.TextRangeUnit.Paragraph);

                        float paraFontSize = paraRange.CharacterFormat.Size;

                        // ★修正ポイント：サイズが混在して NaN になる場合、段落の「先頭」のサイズを取得する
                        if (float.IsNaN(paraFontSize) || paraFontSize <= 0)
                        {
                            var temp = paraRange.GetClone();
                            temp.Collapse(true); // 選択範囲を段落の先頭に畳む
                            paraFontSize = temp.CharacterFormat.Size;

                            // それでも取得できない場合はデフォルトサイズ
                            if (float.IsNaN(paraFontSize) || paraFontSize <= 0)
                            {
                                paraFontSize = (float)MemoStorage.FontSize;
                            }
                        }

                        // 先頭の文字が見出しサイズ（H1:24 または H2:18）かどうかで余白を分ける
                        if (paraFontSize == 24 || paraFontSize == 18)
                        {
                            // 見出しの場合は上部余白を0、下部余白を設定値にする
                            paraRange.ParagraphFormat.SpaceBefore = 0.0f;
                            paraRange.ParagraphFormat.SpaceAfter = (float)MemoStorage.ParagraphSpacing;
                        }
                        else
                        {
                            // 通常テキストの場合は上下の余白を詰める
                            paraRange.ParagraphFormat.SpaceBefore = 4.5f;
                            paraRange.ParagraphFormat.SpaceAfter = 1.5f;
                        }

                        // 下側をクリッピングする
                        float exactLineHeight = (float)(paraFontSize * 1.5f * lineSpacing);
                        paraRange.ParagraphFormat.SetLineSpacing(Microsoft.UI.Text.LineSpacingRule.Exactly, exactLineHeight);

                        int moved = paraRange.Move(Microsoft.UI.Text.TextRangeUnit.Paragraph, 1);
                        if (moved <= 0) break;
                    }
                }
                else
                {
                    range.ParagraphFormat.SetLineSpacing(Microsoft.UI.Text.LineSpacingRule.Multiple, lineSpacing);
                    range.ParagraphFormat.SpaceAfter = (float)MemoStorage.ParagraphSpacing;
                }
            }
            finally
            {
                doc.ApplyDisplayUpdates();
            }
        }

        private void UpdateRangeWeight(Microsoft.UI.Text.RichEditTextDocument doc, int start, int end, ushort defaultWeight, ushort boldWeight)
        {
            if (start >= end) return;

            // Stackによるループ構造に置き換え、コールスタック枯渇（StackOverflowException）を防ぎます
            var rangesToProcess = new System.Collections.Generic.Stack<(int Start, int End)>();
            rangesToProcess.Push((start, end));

            while (rangesToProcess.Count > 0)
            {
                var (currentStart, currentEnd) = rangesToProcess.Pop();
                if (currentStart >= currentEnd) continue;

                var range = doc.GetRange(currentStart, currentEnd);
                var bold = range.CharacterFormat.Bold;
                var size = range.CharacterFormat.Size;
                var weight = range.CharacterFormat.Weight;

                // 範囲内の太字、サイズ、ウェイトが均一であれば一括で更新
                if (bold != Microsoft.UI.Text.FormatEffect.Toggle &&
                    !float.IsNaN(size) &&
                    size > 0 &&
                    weight != 0)
                {
                    bool isBoldOrHeading = (bold == Microsoft.UI.Text.FormatEffect.On || size == 24 || size == 18);
                    ushort targetWeight = isBoldOrHeading ? boldWeight : defaultWeight;

                    if (weight != targetWeight)
                    {
                        range.CharacterFormat.Weight = targetWeight;
                    }
                }
                else
                {
                    // 範囲の長さが1文字以下の場合は、これ以上分割できないためここで直接更新
                    if (currentEnd - currentStart <= 1)
                    {
                        bool isBoldOrHeading = (bold == Microsoft.UI.Text.FormatEffect.On || size == 24 || size == 18);
                        ushort targetWeight = isBoldOrHeading ? boldWeight : defaultWeight;
                        if (weight != targetWeight)
                        {
                            range.CharacterFormat.Weight = targetWeight;
                        }
                        continue;
                    }

                    // 均一でない場合は半分に分割してスタックに積み直す（非再帰化）
                    int mid = currentStart + (currentEnd - currentStart) / 2;
                    rangesToProcess.Push((mid, currentEnd));
                    rangesToProcess.Push((currentStart, mid));
                }
            }
        }

        private void DeleteCurrentNote()
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
                    SettingsFlyout.ShowAt(PlayButton);
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
                DwmSetWindowAttribute(_hWnd, DWMWA_TRANSITIONS_FORCEDISABLED, ref disableTransitions, sizeof(int));
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
                var pos = _appWindow.Position;
                var size = _appWindow.Size;
                if (size.Width > 100 && size.Height > 100 && pos.X > -10000 && pos.Y > -10000)
                {
                    MemoStorage.SaveWindowPlacementAtomic(pos.X, pos.Y, size.Width, size.Height);
                }

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

        private void WindowPlacementTimer_Tick(DispatcherQueueTimer sender, object args)
        {
            _windowPlacementTimer.Stop();
            if (_appWindow.IsVisible)
            {
                var pos = _appWindow.Position;
                var size = _appWindow.Size;
                if (size.Width > 100 && size.Height > 100 && pos.X > -10000 && pos.Y > -10000)
                {
                    MemoStorage.SaveWindowPlacementAtomic(pos.X, pos.Y, size.Width, size.Height);
                }
            }
        }

        private void SettingsSaveTimer_Tick(DispatcherQueueTimer sender, object args)
        {
            _settingsSaveTimer.Stop();
            MemoStorage.SaveSettings();
        }

        private void QueueSaveSettings()
        {
            _settingsSaveTimer.Stop();
            _settingsSaveTimer.Start();
        }

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
                    await MemoStorage.SaveTasksAtomicAsync(noteId, tasksToSave);
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
                    // フォールバック: GetText に失敗した場合は保存をスキップし、既存の RTF ファイルを保持する
                    // （プレーンテキストで RTF ファイルを上書きすると太字・ハイライト等の装飾が消えるため保存しない）
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
            var pos = _appWindow.Position;
            var size = _appWindow.Size;
            MemoStorage.SaveWindowPlacementAtomic(pos.X, pos.Y, size.Width, size.Height);

            // 3. 全てのリソース解放（タイマー・イベント・Win32APIフック）を実行
            Dispose();
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

            // UIコントロールへの初期値割り当て中にイベントが連鎖するのを防ぐ
            _isInitializingSettings = true;
            try
            {
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

                // 行間 ComboBox の初期値設定
                double currentLS = MemoStorage.LineSpacing;
                bool foundLS = false;
                foreach (var item in LineSpacingComboBox.Items)
                {
                    if (item is string s && double.TryParse(s, out double itemVal) && Math.Abs(itemVal - currentLS) < 0.01)
                    {
                        LineSpacingComboBox.SelectedItem = item;
                        foundLS = true;
                        break;
                    }
                }
                if (!foundLS)
                {
                    LineSpacingComboBox.SelectedItem = null;
                    LineSpacingComboBox.Text = currentLS.ToString("0.##");
                }

                // 段落スペース ComboBox の初期値設定
                double currentPS = MemoStorage.ParagraphSpacing;
                bool foundPS = false;
                foreach (var item in ParagraphSpacingComboBox.Items)
                {
                    if (item is string s && double.TryParse(s, out double itemVal) && Math.Abs(itemVal - currentPS) < 0.1)
                    {
                        ParagraphSpacingComboBox.SelectedItem = item;
                        foundPS = true;
                        break;
                    }
                }
                if (!foundPS)
                {
                    ParagraphSpacingComboBox.SelectedItem = null;
                    ParagraphSpacingComboBox.Text = ((int)currentPS).ToString();
                }

                OpacitySlider.Value = MemoStorage.Opacity;
                OpacityValueText.Text = $"{(int)MemoStorage.Opacity}%";

                RecentNotesCountSlider.Value = MemoStorage.RecentNotesCount;
                RecentNotesCountValueText.Text = MemoStorage.RecentNotesCount.ToString();
                ShowDeleteButtonToggle.IsOn = MemoStorage.ShowDeleteButton;
            }
            finally
            {
                _isInitializingSettings = false;
            }

            QuitHotKeyButton.Content = MemoStorage.QuitHotKey;
            LaunchHotKeyButton.Content = MemoStorage.LaunchHotKey;

            FontItem.Visibility = Visibility.Visible;
            FontWeightItem.Visibility = Visibility.Visible;
            FontSizeItem.Visibility = Visibility.Visible;
            LineSpacingItem.Visibility = Visibility.Visible;
            ParagraphSpacingItem.Visibility = Visibility.Visible;
            OpacityItem.Visibility = Visibility.Visible;
            RecentNotesCountItem.Visibility = Visibility.Visible;
            LaunchHotKeyItem.Visibility = Visibility.Visible;
            QuitHotKeyItem.Visibility = Visibility.Visible;
            DeleteNoteItem.Visibility = Visibility.Visible;
            ShowDeleteButtonItem.Visibility = Visibility.Visible;

            // タブをエディタにリセット
            if (SettingsSelectorBar != null)
            {
                SettingsSelectorBar.SelectedItem = EditorTab;
                SettingsSelectorBar.Visibility = Visibility.Visible;
            }
            UpdateSettingsTabVisibility();

            SettingsSearchBox.Focus(FocusState.Programmatic);
        }

        private void SettingsSelectorBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
        {
            UpdateSettingsTabVisibility();
        }

        private void UpdateSettingsTabVisibility()
        {
            // 検索中（クエリが存在する）は、タブごとの切り替えは適用せず、全パネルを表示状態にする
            if (SettingsSearchBox != null && !string.IsNullOrEmpty(SettingsSearchBox.Text))
            {
                return;
            }

            if (SettingsSelectorBar == null) return;

            var selectedItem = SettingsSelectorBar.SelectedItem;
            if (selectedItem == EditorTab)
            {
                if (EditorSettingsPanel != null) EditorSettingsPanel.Visibility = Visibility.Visible;
                if (WindowSettingsPanel != null) WindowSettingsPanel.Visibility = Visibility.Collapsed;
                if (SystemSettingsPanel != null) SystemSettingsPanel.Visibility = Visibility.Collapsed;
            }
            else if (selectedItem == WindowTab)
            {
                if (EditorSettingsPanel != null) EditorSettingsPanel.Visibility = Visibility.Collapsed;
                if (WindowSettingsPanel != null) WindowSettingsPanel.Visibility = Visibility.Visible;
                if (SystemSettingsPanel != null) SystemSettingsPanel.Visibility = Visibility.Collapsed;
            }
            else if (selectedItem == SystemTab)
            {
                if (EditorSettingsPanel != null) EditorSettingsPanel.Visibility = Visibility.Collapsed;
                if (WindowSettingsPanel != null) WindowSettingsPanel.Visibility = Visibility.Collapsed;
                if (SystemSettingsPanel != null) SystemSettingsPanel.Visibility = Visibility.Visible;
            }
        }

        private void SettingsSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            string query = SettingsSearchBox.Text.Trim().ToLower();
            if (string.IsNullOrEmpty(query))
            {
                if (SettingsSelectorBar != null)
                {
                    SettingsSelectorBar.Visibility = Visibility.Visible;
                }

                // 全項目を表示状態に戻した上で、選択中タブに対応するパネルのみを表示
                FontItem.Visibility = Visibility.Visible;
                FontWeightItem.Visibility = Visibility.Visible;
                FontSizeItem.Visibility = Visibility.Visible;
                LineSpacingItem.Visibility = Visibility.Visible;
                ParagraphSpacingItem.Visibility = Visibility.Visible;
                OpacityItem.Visibility = Visibility.Visible;
                RecentNotesCountItem.Visibility = Visibility.Visible;
                LaunchHotKeyItem.Visibility = Visibility.Visible;
                QuitHotKeyItem.Visibility = Visibility.Visible;
                DeleteNoteItem.Visibility = Visibility.Visible;
                ShowDeleteButtonItem.Visibility = Visibility.Visible;

                UpdateSettingsTabVisibility();
                return;
            }

            if (SettingsSelectorBar != null)
            {
                SettingsSelectorBar.Visibility = Visibility.Collapsed;
            }

            // 検索中：項目が配置されているすべての StackPanel を表示状態にする
            if (EditorSettingsPanel != null) EditorSettingsPanel.Visibility = Visibility.Visible;
            if (WindowSettingsPanel != null) WindowSettingsPanel.Visibility = Visibility.Visible;
            if (SystemSettingsPanel != null) SystemSettingsPanel.Visibility = Visibility.Visible;

            FontItem.Visibility = "font フォント 書体".Contains(query) ? Visibility.Visible : Visibility.Collapsed;
            FontWeightItem.Visibility = "font weight フォント ウェイト 太さ 太字".Contains(query) ? Visibility.Visible : Visibility.Collapsed;
            FontSizeItem.Visibility = "font size フォントサイズ 大きさ サイズ 文字".Contains(query) ? Visibility.Visible : Visibility.Collapsed;
            LineSpacingItem.Visibility = "line spacing 行間 行の高さ 高さ".Contains(query) ? Visibility.Visible : Visibility.Collapsed;
            ParagraphSpacingItem.Visibility = "paragraph spacing 段落間 行間 行の高さ 高さ 余白 改行".Contains(query) ? Visibility.Visible : Visibility.Collapsed;
            OpacityItem.Visibility = "opacity 不透明度 透明度 背景 透け".Contains(query) ? Visibility.Visible : Visibility.Collapsed;
            RecentNotesCountItem.Visibility = "recent notes count 直近 メモ 件数 表示 履歴 順番".Contains(query) ? Visibility.Visible : Visibility.Collapsed;
            LaunchHotKeyItem.Visibility = "launch hotkey ショートカット キーボード 起動 ホットキー ランチ".Contains(query) ? Visibility.Visible : Visibility.Collapsed;
            QuitHotKeyItem.Visibility = "quit hotkey ショートカット キーボード 終了 ホットキー クイック".Contains(query) ? Visibility.Visible : Visibility.Collapsed;
            DeleteNoteItem.Visibility = "delete note メモを削除 削除 ゴミ箱".Contains(query) ? Visibility.Visible : Visibility.Collapsed;
            ShowDeleteButtonItem.Visibility = "show delete button 削除ボタン 表示 非表示 設定 ゴミ箱".Contains(query) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void FontComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing || _isInitializingSettings) return;
            if (FontComboBox.SelectedItem is string font)
            {
                MemoStorage.FontFamily = font;
                var fontFamily = new Microsoft.UI.Xaml.Media.FontFamily(font);
                MemoTextBox.FontFamily = fontFamily;
                PlaceholderTextBlock.FontFamily = fontFamily;
                ApplyGlobalThemeToEditor();
                QueueSaveSettings();
            }
        }

        private void FontWeightComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing || _isInitializingSettings) return;
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
                ApplyGlobalThemeToEditor();
                QueueSaveSettings();
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

        private ushort GetDefaultFontWeight()
        {
            return GetFontWeight(MemoStorage.FontWeight).Weight;
        }

        private Microsoft.UI.Text.FormatEffect GetDefaultBoldEffect()
        {
            return GetDefaultFontWeight() >= Microsoft.UI.Text.FontWeights.Bold.Weight 
                ? Microsoft.UI.Text.FormatEffect.On 
                : Microsoft.UI.Text.FormatEffect.Off;
        }

        private ushort GetBoldFontWeight()
        {
            var defaultWeight = GetDefaultFontWeight();
            if (defaultWeight == Microsoft.UI.Text.FontWeights.Light.Weight)
                return Microsoft.UI.Text.FontWeights.Medium.Weight;
            if (defaultWeight == Microsoft.UI.Text.FontWeights.Normal.Weight)
                return Microsoft.UI.Text.FontWeights.SemiBold.Weight;
            if (defaultWeight == Microsoft.UI.Text.FontWeights.Medium.Weight)
                return Microsoft.UI.Text.FontWeights.Bold.Weight;
            if (defaultWeight == Microsoft.UI.Text.FontWeights.SemiBold.Weight)
                return Microsoft.UI.Text.FontWeights.ExtraBold.Weight;
            
            return Microsoft.UI.Text.FontWeights.Black.Weight;
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

        private void RecentNotesCountDecreaseButton_Click(object sender, RoutedEventArgs e)
        {
            if (RecentNotesCountSlider != null)
            {
                RecentNotesCountSlider.Value = Math.Max(RecentNotesCountSlider.Minimum, RecentNotesCountSlider.Value - 1);
            }
        }

        private void RecentNotesCountIncreaseButton_Click(object sender, RoutedEventArgs e)
        {
            if (RecentNotesCountSlider != null)
            {
                RecentNotesCountSlider.Value = Math.Min(RecentNotesCountSlider.Maximum, RecentNotesCountSlider.Value + 1);
            }
        }

        private void RecentNotesCountSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_isInitializing || _isInitializingSettings) return;
            int count = (int)RecentNotesCountSlider.Value;
            if (RecentNotesCountValueText != null)
            {
                RecentNotesCountValueText.Text = count.ToString();
            }
            MemoStorage.RecentNotesCount = count;
            QueueSaveSettings();
            RefreshAllNotesLists();
        }

        private void ShowDeleteButtonToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing || _isInitializingSettings) return;
            if (sender is ToggleSwitch ts)
            {
                MemoStorage.ShowDeleteButton = ts.IsOn;
                RefreshAllNotesLists();
                QueueSaveSettings();
            }
        }

        private void FontSizeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_isInitializing || _isInitializingSettings) return;
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
            ApplyGlobalThemeToEditor();
            QueueSaveSettings();
        }

        private void LineSpacingComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing || _isInitializingSettings) return;
            if (LineSpacingComboBox.SelectedItem is string val && double.TryParse(val, out double ls))
            {
                MemoStorage.LineSpacing = ls;
                ApplyGlobalThemeToEditor();
                QueueSaveSettings();
            }
        }

        private void LineSpacingComboBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            if (double.TryParse(LineSpacingComboBox.Text, out double ls))
            {
                // 0.8 〜 1.0 に制限
                ls = Math.Clamp(ls, 0.8, 1.0);
                if (Math.Abs(MemoStorage.LineSpacing - ls) > 0.01)
                {
                    MemoStorage.LineSpacing = ls;
                    ApplyGlobalThemeToEditor();
                    QueueSaveSettings();
                }

                // 0.95などが丸められないよう "0.##" に変更
                string targetText = ls.ToString("0.##");
                bool found = false;
                foreach (var item in LineSpacingComboBox.Items)
                {
                    if (item is string s && double.TryParse(s, out double itemVal) && Math.Abs(itemVal - ls) < 0.01)
                    {
                        if (LineSpacingComboBox.SelectedItem != item)
                        {
                            LineSpacingComboBox.SelectedItem = item;
                        }
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    LineSpacingComboBox.SelectedItem = null;
                    if (LineSpacingComboBox.Text != targetText)
                    {
                        LineSpacingComboBox.Text = targetText;
                    }
                }
            }
            else
            {
                // 不正入力時は直前の有効な値に復元
                RestoreLineSpacingComboBoxSelection();
            }
        }

        private void RestoreLineSpacingComboBoxSelection()
        {
            bool found = false;
            foreach (var item in LineSpacingComboBox.Items)
            {
                if (item is string s && double.TryParse(s, out double itemVal) && Math.Abs(itemVal - MemoStorage.LineSpacing) < 0.01)
                {
                    LineSpacingComboBox.SelectedItem = item;
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                LineSpacingComboBox.SelectedItem = null;
                LineSpacingComboBox.Text = MemoStorage.LineSpacing.ToString("0.##");
            }
        }

        private void ParagraphSpacingComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing || _isInitializingSettings) return;
            if (ParagraphSpacingComboBox.SelectedItem is string val && double.TryParse(val, out double ps))
            {
                MemoStorage.ParagraphSpacing = ps;
                ApplyGlobalThemeToEditor();
                QueueSaveSettings();
            }
        }

        private void ParagraphSpacingComboBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            if (double.TryParse(ParagraphSpacingComboBox.Text, out double ps))
            {
                // 0 〜 12 に制限
                ps = Math.Clamp(ps, 0.0, 12.0);
                if (Math.Abs(MemoStorage.ParagraphSpacing - ps) > 0.1)
                {
                    MemoStorage.ParagraphSpacing = ps;
                    ApplyGlobalThemeToEditor();
                    QueueSaveSettings();
                }

                // UIの表示を入力・クランプ後の値に同期
                string targetText = ((int)ps).ToString();
                bool found = false;
                foreach (var item in ParagraphSpacingComboBox.Items)
                {
                    if (item is string s && double.TryParse(s, out double itemVal) && Math.Abs(itemVal - ps) < 0.1)
                    {
                        if (ParagraphSpacingComboBox.SelectedItem != item)
                        {
                            ParagraphSpacingComboBox.SelectedItem = item;
                        }
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    ParagraphSpacingComboBox.SelectedItem = null;
                    if (ParagraphSpacingComboBox.Text != targetText)
                    {
                        ParagraphSpacingComboBox.Text = targetText;
                    }
                }
            }
            else
            {
                // 不正入力時は直前の有効な値に復元
                RestoreParagraphSpacingComboBoxSelection();
            }
        }

        private void RestoreParagraphSpacingComboBoxSelection()
        {
            bool found = false;
            foreach (var item in ParagraphSpacingComboBox.Items)
            {
                if (item is string s && double.TryParse(s, out double itemVal) && Math.Abs(itemVal - MemoStorage.ParagraphSpacing) < 0.1)
                {
                    ParagraphSpacingComboBox.SelectedItem = item;
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                ParagraphSpacingComboBox.SelectedItem = null;
                ParagraphSpacingComboBox.Text = ((int)MemoStorage.ParagraphSpacing).ToString();
            }
        }

        private void OpacitySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_isInitializing || _isInitializingSettings) return;
            double opacity = OpacitySlider.Value;
            if (OpacityValueText != null)
            {
                OpacityValueText.Text = $"{(int)opacity}%";
            }
            MemoStorage.Opacity = opacity;

            double currentOpacity = opacity / 100.0;
            if (RootGrid != null && RootGrid.Background is Microsoft.UI.Xaml.Media.SolidColorBrush brush)
            {
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

            QueueSaveSettings();
        }

        private void DeleteNoteItem_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            DeleteCurrentNote();
            SettingsFlyout.Hide();
        }

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
                    _ => ""
                };
            }

            // Update Indicators
            if (NotesActiveIndicator != null) NotesActiveIndicator.Visibility = view == SidebarView.Notes ? Visibility.Visible : Visibility.Collapsed;
            if (TasksActiveIndicator != null) TasksActiveIndicator.Visibility = view == SidebarView.Tasks ? Visibility.Visible : Visibility.Collapsed;
            if (AllTasksActiveIndicator != null) AllTasksActiveIndicator.Visibility = view == SidebarView.AllTasks ? Visibility.Visible : Visibility.Collapsed;
            if (JustDoItActiveIndicator != null) JustDoItActiveIndicator.Visibility = view == SidebarView.JustDoIt ? Visibility.Visible : Visibility.Collapsed;

            // Update Containers Visibility
            if (NotesViewContainer != null) NotesViewContainer.Visibility = view == SidebarView.Notes ? Visibility.Visible : Visibility.Collapsed;
            if (TasksViewContainer != null) TasksViewContainer.Visibility = view == SidebarView.Tasks ? Visibility.Visible : Visibility.Collapsed;
            if (AllTasksViewContainer != null) AllTasksViewContainer.Visibility = view == SidebarView.AllTasks ? Visibility.Visible : Visibility.Collapsed;
            if (JustDoItViewContainer != null) JustDoItViewContainer.Visibility = view == SidebarView.JustDoIt ? Visibility.Visible : Visibility.Collapsed;

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

            // Update Containers Visibility
            if (RightNotesViewContainer != null) RightNotesViewContainer.Visibility = view == SidebarView.Notes ? Visibility.Visible : Visibility.Collapsed;
            if (RightTasksViewContainer != null) RightTasksViewContainer.Visibility = view == SidebarView.Tasks ? Visibility.Visible : Visibility.Collapsed;
            if (RightAllTasksViewContainer != null) RightAllTasksViewContainer.Visibility = view == SidebarView.AllTasks ? Visibility.Visible : Visibility.Collapsed;
            if (RightJustDoItTasksViewContainer != null) RightJustDoItTasksViewContainer.Visibility = view == SidebarView.JustDoIt ? Visibility.Visible : Visibility.Collapsed;

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
                var uncompletedTasks = new System.Collections.ObjectModel.ObservableCollection<TaskItemViewModel>();
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
                var uncompletedTasks = new System.Collections.ObjectModel.ObservableCollection<TaskItemViewModel>();
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
                var justDoItTasks = new System.Collections.ObjectModel.ObservableCollection<TaskItemViewModel>();
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
            PopulateNotesList(NoteSearchBox.Text);
        }

        private void NoteSearchBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
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
            PopulateSidebarNotesList(SidebarNoteSearchBox.Text);
        }

        private void SidebarNoteSearchBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
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
                var actionsPanel = grid.FindName("ActionsPanel") as UIElement;
                if (actionsPanel != null)
                {
                    actionsPanel.Visibility = Visibility.Visible;
                }

                // ホバー時にアクションボタン（ピン留め・削除）とテキストが重ならないよう、ボタンの右パディングを動的に調整
                var noteButton = grid.FindName("NoteButton") as Button;
                if (noteButton != null)
                {
                    double rightPadding = 44; // ピン留めボタンのみ表示時の幅（ボタン幅28 + 左右余白16）
                    if (grid.DataContext is NoteItemViewModel vm)
                    {
                        if (vm.DeleteButtonVisibility == Visibility.Visible)
                        {
                            rightPadding = 74; // ピン留めと削除ボタン両方表示時の幅（ボタン2個分56 + 隙間2 + 左右余白16）
                        }
                    }
                    noteButton.Padding = new Thickness(12, 10, rightPadding, 10);
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

                // ホバー解除時はパディングをデフォルト値（左右対称の12）に戻し、タイトル表示領域を最大化する
                var noteButton = grid.FindName("NoteButton") as Button;
                if (noteButton != null)
                {
                    noteButton.Padding = new Thickness(12, 10, 12, 10);
                }
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
                    MemoStorage.SaveMetadata();

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

            MemoStorage.SetCurrentNote(id, updateLastOpened);

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

        private void PopulateNotesList(string filter = "")
        {
            var query = filter.Trim();
            var pinnedVMs = new List<NoteItemViewModel>();
            var normalVMs = new List<NoteItemViewModel>();
            var recentVMs = new List<NoteItemViewModel>();

            List<NoteData> filteredNotes = new List<NoteData>();
            lock (MemoStorage.Notes)
            {
                foreach (var note in MemoStorage.Notes)
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
                    filteredNotes.Add(note);
                }
            }

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

            List<NoteData> filteredNotes = new List<NoteData>();
            lock (MemoStorage.Notes)
            {
                foreach (var note in MemoStorage.Notes)
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
                    filteredNotes.Add(note);
                }
            }

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

        private void RefreshAllNotesLists()
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

            List<NoteData> filteredNotes = new List<NoteData>();
            lock (MemoStorage.Notes)
            {
                foreach (var note in MemoStorage.Notes)
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
                    filteredNotes.Add(note);
                }
            }

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
            PopulateRightSidebarNotesList(RightSidebarNoteSearchBox.Text);
        }

        private void RightSidebarNoteSearchBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
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

        private bool AreNoteListsEqual(System.Collections.Generic.IList<NoteItemViewModel>? listA, System.Collections.Generic.List<NoteItemViewModel> listB)
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

        private bool AreAllTasksGroupsEqual(System.Collections.Generic.List<AllTasksGroupViewModel>? listA, System.Collections.Generic.List<AllTasksGroupViewModel> listB)
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

        /// <summary>
        /// メモテキストの右端折り返し設定を切り替えます。
        /// </summary>
        private void WordWrapButton_Click(object sender, RoutedEventArgs e)
        {
            MemoTextBox.TextWrapping = MemoTextBox.TextWrapping == TextWrapping.Wrap ? TextWrapping.NoWrap : TextWrapping.Wrap;
        }

        #region テキスト装飾 (Formatting)

        private void MemoTextBox_SelectionChanged(object sender, RoutedEventArgs e)
        {
            if (_isRestoring || _isInitializing) return;
            UpdateFormatButtonStates();
        }

        private void UpdateFormatButtonStates()
        {
            if (MemoTextBox == null || FormatBoldBtn == null || FormatItalicBtn == null ||
                FormatUnderlineBtn == null || FormatStrikethroughBtn == null || FormatHighlightBtn == null ||
                FormatBulletListBtn == null || FormatNumberListBtn == null ||
                FormatHeading1Btn == null || FormatHeading2Btn == null)
            {
                return;
            }

            // ★GetRangeを呼ぶのをやめ、既存のSelectionをそのまま使用してCOMオブジェクトの新規生成を回避
            var selection = MemoTextBox.Document.Selection;
            if (selection == null) return;

            var format = selection.CharacterFormat;

            FormatBoldBtn.IsChecked = (format.Bold == Microsoft.UI.Text.FormatEffect.On || format.Weight >= GetBoldFontWeight());
            FormatItalicBtn.IsChecked = format.Italic == Microsoft.UI.Text.FormatEffect.On;
            FormatUnderlineBtn.IsChecked = format.Underline != Microsoft.UI.Text.UnderlineType.None;
            FormatStrikethroughBtn.IsChecked = format.Strikethrough == Microsoft.UI.Text.FormatEffect.On;

            var highlightColor = Microsoft.UI.ColorHelper.FromArgb(255, 120, 100, 0);
            FormatHighlightBtn.IsChecked = format.BackgroundColor == highlightColor;

            var listType = selection.ParagraphFormat.ListType;
            FormatBulletListBtn.IsChecked = (listType == Microsoft.UI.Text.MarkerType.Bullet);
            FormatNumberListBtn.IsChecked = (listType == Microsoft.UI.Text.MarkerType.Arabic);

            FormatHeading1Btn.IsChecked = (format.Size == 24 && (format.Bold == Microsoft.UI.Text.FormatEffect.On || format.Weight >= GetBoldFontWeight()));
            FormatHeading2Btn.IsChecked = (format.Size == 18 && (format.Bold == Microsoft.UI.Text.FormatEffect.On || format.Weight >= GetBoldFontWeight()));
        }

        private void MemoTextBox_PreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            var ctrlState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
            bool isCtrlDown = (ctrlState & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
            var shiftState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
            bool isShiftDown = (shiftState & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

            if (isCtrlDown)
            {
                // Ctrl + B, Ctrl + I, Ctrl + U を検知してドキュメントを変更状態にする
                if (e.Key == Windows.System.VirtualKey.B || e.Key == Windows.System.VirtualKey.I || e.Key == Windows.System.VirtualKey.U)
                {
                    MarkAsDirty();
                }

                // 箇条書き (Ctrl + Shift + L)
                if (isShiftDown && e.Key == Windows.System.VirtualKey.L)
                {
                    FormatBulletList_Click(null, null);
                    e.Handled = true;
                }
                // 番号付きリスト (Ctrl + Shift + N)
                else if (isShiftDown && e.Key == Windows.System.VirtualKey.N)
                {
                    FormatNumberList_Click(null, null);
                    e.Handled = true;
                }
                // ハイライト (Ctrl + Shift + H)
                else if (isShiftDown && e.Key == Windows.System.VirtualKey.H)
                {
                    ToggleHighlight();
                    e.Handled = true;
                }
                // 取り消し線 (Ctrl + Shift + S)
                else if (isShiftDown && e.Key == Windows.System.VirtualKey.S)
                {
                    ToggleStrikethrough();
                    e.Handled = true;
                }
                // 検索バー表示 (Ctrl + F)
                else if (!isShiftDown && e.Key == Windows.System.VirtualKey.F)
                {
                    ShowFindReplace(showReplace: false);
                    e.Handled = true;
                }
                // 置換バー表示 (Ctrl + H)
                else if (!isShiftDown && e.Key == Windows.System.VirtualKey.H)
                {
                    ShowFindReplace(showReplace: true);
                    e.Handled = true;
                }
                // 見出し1 (Ctrl + 1)
                else if (e.Key == Windows.System.VirtualKey.Number1)
                {
                    FormatHeading1_Click(null, null);
                    e.Handled = true;
                }
                // 見出し2 (Ctrl + 2)
                else if (e.Key == Windows.System.VirtualKey.Number2)
                {
                    FormatHeading2_Click(null, null);
                    e.Handled = true;
                }
                else
                {
                    switch (e.Key)
                    {
                        case Windows.System.VirtualKey.Space: // Ctrl + Space で装飾クリア
                            ClearFormatting();
                            e.Handled = true;
                            break;
                    }
                }
            }
        }

        private void FormatBold_Click(object sender, RoutedEventArgs e)
        {
            var format = MemoTextBox.Document.Selection.CharacterFormat;
            bool isBold = format.Bold == Microsoft.UI.Text.FormatEffect.On || format.Weight >= GetBoldFontWeight();
            if (isBold)
            {
                format.Bold = Microsoft.UI.Text.FormatEffect.Off;
                format.Weight = GetDefaultFontWeight();
            }
            else
            {
                format.Bold = Microsoft.UI.Text.FormatEffect.On;
                format.Weight = GetBoldFontWeight();
            }
            UpdateFormatButtonStates();
            MarkAsDirty();
        }

        private void FormatItalic_Click(object sender, RoutedEventArgs e)
        {
            var format = MemoTextBox.Document.Selection.CharacterFormat;
            format.Italic = format.Italic == Microsoft.UI.Text.FormatEffect.On ? Microsoft.UI.Text.FormatEffect.Off : Microsoft.UI.Text.FormatEffect.On;
            UpdateFormatButtonStates();
            MarkAsDirty();
        }

        private void FormatUnderline_Click(object sender, RoutedEventArgs e)
        {
            var format = MemoTextBox.Document.Selection.CharacterFormat;
            format.Underline = format.Underline == Microsoft.UI.Text.UnderlineType.None ? Microsoft.UI.Text.UnderlineType.Single : Microsoft.UI.Text.UnderlineType.None;
            UpdateFormatButtonStates();
            MarkAsDirty();
        }

        private void FormatStrikethrough_Click(object sender, RoutedEventArgs e)
        {
            ToggleStrikethrough();
        }

        private void FormatHighlight_Click(object sender, RoutedEventArgs e)
        {
            ToggleHighlight();
        }

        private void FormatClear_Click(object sender, RoutedEventArgs e)
        {
            ClearFormatting();
        }

        private void FormatBulletList_Click(object? sender, RoutedEventArgs? e)
        {
            var selection = MemoTextBox.Document.Selection;
            selection.ParagraphFormat.ListType = (selection.ParagraphFormat.ListType == Microsoft.UI.Text.MarkerType.Bullet) 
                ? Microsoft.UI.Text.MarkerType.None 
                : Microsoft.UI.Text.MarkerType.Bullet;
            
            MemoTextBox.Focus(FocusState.Programmatic);
            UpdateFormatButtonStates();
            MarkAsDirty();
        }

        private void FormatNumberList_Click(object? sender, RoutedEventArgs? e)
        {
            var selection = MemoTextBox.Document.Selection;
            if (selection.ParagraphFormat.ListType == Microsoft.UI.Text.MarkerType.Arabic)
            {
                selection.ParagraphFormat.ListType = Microsoft.UI.Text.MarkerType.None;
            }
            else
            {
                selection.ParagraphFormat.ListType = Microsoft.UI.Text.MarkerType.Arabic;
                selection.ParagraphFormat.ListStart = 1;
            }
                
            MemoTextBox.Focus(FocusState.Programmatic);
            UpdateFormatButtonStates();
            MarkAsDirty();
        }

        // ★ 追加：表の生成ボタン確定イベント
        private void InsertTableConfirm_Click(object sender, RoutedEventArgs e)
        {
            int rows = 3;
            int cols = 3;

            if (TableRowsComboBox.SelectedItem is string rStr && int.TryParse(rStr, out int r))
            {
                rows = r;
            }
            if (TableColsComboBox.SelectedItem is string cStr && int.TryParse(cStr, out int c))
            {
                cols = c;
            }

            // 指定された行・列数に基づいてRTFテーブルコードを生成
            string tableRtf = CreateTableRtf(rows, cols);

            try
            {
                // 現在の選択範囲（カーソル位置）にテーブルをフォーマットされたRTFとして挿入
                MemoTextBox.Document.Selection.SetText(Microsoft.UI.Text.TextSetOptions.FormatRtf, tableRtf);
                MarkAsDirty();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Insert Table Error] {ex.Message}");
            }

            InsertTableFlyout.Hide();
            MemoTextBox.Focus(FocusState.Programmatic);
        }

        // ★ 境界線の色（グレー）を指定してRTF形式の表データを作成するメソッド
        private string CreateTableRtf(int rows, int cols)
        {
            var sb = new System.Text.StringBuilder();

            // RTFヘッダーにカラーテーブルを定義し、インデックス1にグレー（R:150, G:150, B:150）を登録します
            sb.Append(@"{\rtf1\ansi\deff0{\colortbl;\red150\green150\blue150;}");

            int colWidth = 1800;

            for (int r = 0; r < rows; r++)
            {
                sb.Append(@"\trowd\trgaph100");

                for (int c = 0; c < cols; c++)
                {
                    int cellX = (c + 1) * colWidth;

                    // 各罫線の定義（clbrdr*）の末尾に、カラーテーブルから色を適用する制御ワード「\brdrcf1」を付加します
                    sb.Append(@"\clbrdrt\brdrs\brdrw15\brdrcf1" +
                              @"\clbrdrl\brdrs\brdrw15\brdrcf1" +
                              @"\clbrdrb\brdrs\brdrw15\brdrcf1" +
                              @"\clbrdrr\brdrs\brdrw15\brdrcf1");

                    sb.Append(@"\clpadt60\clpadl100\clpadb60\clpadr100");
                    sb.Append($@"\cellx{cellX}");
                }

                for (int c = 0; c < cols; c++)
                {
                    sb.Append(@" \intbl\cell");
                }

                sb.Append(@"\row");
            }

            sb.Append(@"}");
            return sb.ToString();
        }

        private void FormatHeading1_Click(object? sender, RoutedEventArgs? e)
        {
            var selection = MemoTextBox.Document.Selection;
            if (selection == null) return;

            // 1. 自動追跡可能なクローンを使って現在の選択範囲を保存
            var savedSelection = selection.GetClone();

            // 2. 選択範囲のサイズからトグル状態を判定
            float currentSize = selection.CharacterFormat.Size;
            if (float.IsNaN(currentSize) || currentSize <= 0)
            {
                var temp = selection.GetClone();
                temp.Collapse(true);
                currentSize = temp.CharacterFormat.Size;
            }

            bool isCurrentlyH1 = (currentSize == 24);

            float targetSize = isCurrentlyH1 ? (float)MemoStorage.FontSize : 24;
            var boldEffect = isCurrentlyH1 ? GetDefaultBoldEffect() : Microsoft.UI.Text.FormatEffect.On;
            ushort targetWeight = isCurrentlyH1 ? GetDefaultFontWeight() : GetBoldFontWeight();

            // 3. 選択された文字列（selection）に対してフォントとウェイトを適用
            selection.CharacterFormat.Size = targetSize;
            selection.CharacterFormat.Bold = boldEffect;
            selection.CharacterFormat.Weight = targetWeight;

            // 4. 選択範囲が含まれる段落全体を特定し、適用した targetSize に基づいて行高をダイレクトに設定
            var paraRange = MemoTextBox.Document.GetRange(selection.StartPosition, selection.EndPosition);
            paraRange.Expand(Microsoft.UI.Text.TextRangeUnit.Paragraph);

            float lineSpacing = (float)MemoStorage.LineSpacing;
            if (lineSpacing < 1.0f)
            {
                float exactLineHeight = (float)(targetSize * 1.5f * lineSpacing);
                paraRange.ParagraphFormat.SetLineSpacing(Microsoft.UI.Text.LineSpacingRule.Exactly, exactLineHeight);
            }
            else
            {
                paraRange.ParagraphFormat.SetLineSpacing(Microsoft.UI.Text.LineSpacingRule.Multiple, lineSpacing);
            }
            if (isCurrentlyH1)
            {
                // H2を解除して通常テキストに戻る場合は、装飾クリアと同じタイトな余白にリセットする
                paraRange.ParagraphFormat.SpaceBefore = 4.5f;
                paraRange.ParagraphFormat.SpaceAfter = 1.5f;
            }
            else
            {
                // H2見出しにする場合
                paraRange.ParagraphFormat.SpaceBefore = 0.0f;
                paraRange.ParagraphFormat.SpaceAfter = (float)MemoStorage.ParagraphSpacing;
            }

            // 5. 選択範囲（カーソル位置）を正確に復元
            selection.SetRange(savedSelection.StartPosition, savedSelection.EndPosition);

            MemoTextBox.Focus(FocusState.Programmatic);
            UpdateFormatButtonStates();
            MarkAsDirty();
        }

        private void FormatHeading2_Click(object? sender, RoutedEventArgs? e)
        {
            var selection = MemoTextBox.Document.Selection;
            if (selection == null) return;

            // 1. 自動追跡可能なクローンを使って現在の選択範囲を保存
            var savedSelection = selection.GetClone();

            // 2. 選択範囲のサイズからトグル状態を判定
            float currentSize = selection.CharacterFormat.Size;
            if (float.IsNaN(currentSize) || currentSize <= 0)
            {
                var temp = selection.GetClone();
                temp.Collapse(true);
                currentSize = temp.CharacterFormat.Size;
            }

            bool isCurrentlyH2 = (currentSize == 18);

            float targetSize = isCurrentlyH2 ? (float)MemoStorage.FontSize : 18;
            var boldEffect = isCurrentlyH2 ? GetDefaultBoldEffect() : Microsoft.UI.Text.FormatEffect.On;
            ushort targetWeight = isCurrentlyH2 ? GetDefaultFontWeight() : GetBoldFontWeight();

            // 3. 選択された文字列（selection）に対してフォントとウェイトを適用
            selection.CharacterFormat.Size = targetSize;
            selection.CharacterFormat.Bold = boldEffect;
            selection.CharacterFormat.Weight = targetWeight;

            // 4. 選択範囲が含まれる段落全体を特定し、適用した targetSize に基づいて行高をダイレクトに設定
            var paraRange = MemoTextBox.Document.GetRange(selection.StartPosition, selection.EndPosition);
            paraRange.Expand(Microsoft.UI.Text.TextRangeUnit.Paragraph);

            float lineSpacing = (float)MemoStorage.LineSpacing;
            if (lineSpacing < 1.0f)
            {
                float exactLineHeight = (float)(targetSize * 1.5f * lineSpacing);
                paraRange.ParagraphFormat.SetLineSpacing(Microsoft.UI.Text.LineSpacingRule.Exactly, exactLineHeight);
            }
            else
            {
                paraRange.ParagraphFormat.SetLineSpacing(Microsoft.UI.Text.LineSpacingRule.Multiple, lineSpacing);
            }
            if (isCurrentlyH2)
            {
                // H1を解除して通常テキストに戻る場合は、装飾クリアと同じタイトな余白にリセットする
                paraRange.ParagraphFormat.SpaceBefore = 4.5f;
                paraRange.ParagraphFormat.SpaceAfter = 1.5f;
            }
            else
            {
                // H1見出しにする場合は、設定に準拠した余白（または見出し用の余白）にする
                paraRange.ParagraphFormat.SpaceBefore = 0.0f; // 必要に応じて調整してください
                paraRange.ParagraphFormat.SpaceAfter = (float)MemoStorage.ParagraphSpacing;
            }

            // 5. 選択範囲（カーソル位置）を正確に復元
            selection.SetRange(savedSelection.StartPosition, savedSelection.EndPosition);

            MemoTextBox.Focus(FocusState.Programmatic);
            UpdateFormatButtonStates();
            MarkAsDirty();
        }

        private void ToggleHighlight()
        {
            var format = MemoTextBox.Document.Selection.CharacterFormat;
            
            // ダークテーマに合う控えめな黄色のハイライト色を設定
            var highlightColor = Microsoft.UI.ColorHelper.FromArgb(255, 120, 100, 0);
            var transparentColor = Microsoft.UI.Colors.Transparent;

            if (format.BackgroundColor == highlightColor)
            {
                format.BackgroundColor = transparentColor; // すでにハイライトされていれば解除
            }
            else
            {
                format.BackgroundColor = highlightColor; // ハイライトを適用
            }
            UpdateFormatButtonStates();
            MarkAsDirty();
        }

        private void ToggleStrikethrough()
        {
            var format = MemoTextBox.Document.Selection.CharacterFormat;
            format.Strikethrough = format.Strikethrough == Microsoft.UI.Text.FormatEffect.On ? Microsoft.UI.Text.FormatEffect.Off : Microsoft.UI.Text.FormatEffect.On;
            UpdateFormatButtonStates();
            MarkAsDirty();
        }

        private void ClearFormatting()
        {
            var selection = MemoTextBox.Document.Selection;
            if (selection == null) return;

            // 1. 自動追跡可能なクローンを使って現在の選択範囲を保存
            var savedSelection = selection.GetClone();

            // 2. 選択部分の文字装飾を標準に戻す
            var format = selection.CharacterFormat;
            format.Bold = GetDefaultBoldEffect();
            format.Weight = GetDefaultFontWeight();
            format.Italic = Microsoft.UI.Text.FormatEffect.Off;
            format.Underline = Microsoft.UI.Text.UnderlineType.None;
            format.Strikethrough = Microsoft.UI.Text.FormatEffect.Off;
            format.BackgroundColor = Microsoft.UI.Colors.Transparent;
            format.Size = (float)MemoStorage.FontSize;

            selection.ParagraphFormat.ListType = Microsoft.UI.Text.MarkerType.None;

            // 3. 選択範囲が含まれる段落全体を特定し、行高をデフォルトにダイレクトにリセット
            var paraRange = MemoTextBox.Document.GetRange(selection.StartPosition, selection.EndPosition);
            paraRange.Expand(Microsoft.UI.Text.TextRangeUnit.Paragraph);

            // ★ 追加: クリアした時も段落余白をゼロにする
            paraRange.ParagraphFormat.SpaceBefore = 4.5f;
            paraRange.ParagraphFormat.SpaceAfter = 1.5f;

            float lineSpacing = (float)MemoStorage.LineSpacing;

            // ★ ★下側をクリッピングする
            float exactLineHeight = (float)(MemoStorage.FontSize * 1.5f * lineSpacing);
            paraRange.ParagraphFormat.SetLineSpacing(Microsoft.UI.Text.LineSpacingRule.Exactly, exactLineHeight);
            paraRange.ParagraphFormat.ListType = Microsoft.UI.Text.MarkerType.None;

            // 4. 選択範囲（カーソル位置）を正確に復元
            selection.SetRange(savedSelection.StartPosition, savedSelection.EndPosition);

            UpdateFormatButtonStates();
            MarkAsDirty();
        }

        private void RemoveEmptyLines_Click(object sender, RoutedEventArgs e)
        {
            if (MemoTextBox == null || MemoTextBox.IsReadOnly) return;

            var selection = MemoTextBox.Document.Selection;
            if (selection == null) return;

            // 選択範囲がある場合のみ実行
            if (selection.StartPosition != selection.EndPosition)
            {
                string selectedText = selection.Text;
                if (!string.IsNullOrEmpty(selectedText))
                {
                    string cleanedText = RemoveEmptyLinesFromText(selectedText);
                    selection.Text = cleanedText;

                    UpdateFormatButtonStates();
                    MarkAsDirty();
                }
            }

            // エディタにフォーカスを戻す
            MemoTextBox.Focus(FocusState.Programmatic);
        }

        private string RemoveEmptyLinesFromText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // 改行コード（\r\n, \r, \n）で分割
            var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            // 空行またはスペースのみの行を除外
            var nonEmptyLines = lines.Where(line => !string.IsNullOrWhiteSpace(line));

            // 元の改行コードを優先して結合
            string separator = "\r";
            if (text.Contains("\r\n"))
            {
                separator = "\r\n";
            }
            else if (text.Contains("\n"))
            {
                separator = "\n";
            }

            return string.Join(separator, nonEmptyLines);
        }

        #endregion

        private void MemoTextBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (_memoScrollViewer != null)
            {
                _memoScrollViewer.PointerWheelChanged -= ScrollViewer_PointerWheelChanged;
            }
            _memoScrollViewer = FindScrollViewer(MemoTextBox);
            if (_memoScrollViewer != null)
            {
                _memoScrollViewer.PointerWheelChanged += ScrollViewer_PointerWheelChanged;
            }

            // TTFP最適化: コントロールが完全に描画完了・初期化された後に、テキストの適用とテーマ設定を行う。
            if (_pendingNote != null)
            {
                string id = _pendingNote.Id;
                var pendingNoteCopy = _pendingNote;
                _pendingNote = null;

                this.DispatcherQueue.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    () =>
                    {
                        MemoTextBox.TextChanged -= MemoTextBox_TextChanged;
                        _isRestoring = true;

                        string rtfData = MemoStorage.LoadNoteRtf(id);
                        try
                        {
                            if (rtfData.StartsWith("{\\rtf1"))
                            {
                                MemoTextBox.Document.SetText(Microsoft.UI.Text.TextSetOptions.FormatRtf, rtfData);
                                // 個別装飾（太字・ハイライト等）を保護しつつ、新規入力行のためにデフォルト書式を登録する
                                ApplyGlobalThemeToEditor(preserveFormatting: true);
                            }
                            else
                            {
                                MemoTextBox.Document.SetText(Microsoft.UI.Text.TextSetOptions.None, rtfData);
                                ApplyGlobalThemeToEditor(preserveFormatting: false, isPlainText: true);
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[RTF Load Fallback Error] {ex.Message}");
                            MemoTextBox.Document.SetText(Microsoft.UI.Text.TextSetOptions.None, rtfData);
                            ApplyGlobalThemeToEditor(preserveFormatting: false, isPlainText: true);
                        }

                        // RichEditBoxに読み込ませた直後に、OSネイティブの解析結果（プレーンテキスト）を正確に取得
                        MemoTextBox.Document.GetText(Microsoft.UI.Text.TextGetOptions.UseLf, out string plainText);
                        if (plainText.EndsWith("\r") || plainText.EndsWith("\n"))
                            plainText = plainText.Substring(0, plainText.Length - 1);

                        if (PlaceholderTextBlock != null)
                        {
                            PlaceholderTextBlock.Visibility = string.IsNullOrEmpty(plainText) ? Visibility.Visible : Visibility.Collapsed;
                        }

                        // 起動直後、ネイティブ解析結果を用いてタイトルと文字数を確実に同期・更新する
                        if (pendingNoteCopy != null)
                        {
                            lock (MemoStorage.Notes)
                            {
                                pendingNoteCopy.Content = plainText;
                                pendingNoteCopy.Title = MemoStorage.GetTitleFromContent(plainText);
                                pendingNoteCopy.CharCount = plainText.Length;
                            }
                            TitleTextBlock.Text = pendingNoteCopy.Title;
                            UpdateCharCount(pendingNoteCopy.CharCount);
                        }

                        // ネストした TryEnqueue で非同期の TextChanged 処理を完全にやり過ごしてから状態を復帰する
                        this.DispatcherQueue.TryEnqueue(
                            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                            () =>
                            {
                                MemoTextBox.TextChanged -= MemoTextBox_TextChanged;
                                MemoTextBox.TextChanged += MemoTextBox_TextChanged;
                                _isRestoring = false;
                                _isDirty = false; // 起動時の自動汚染を防ぐために確実に false にする
                                MemoTextBox.Focus(FocusState.Programmatic);
                                int pos = GetCaretEndPosition();
                                MemoTextBox.Document.Selection.SetRange(pos, pos);
                                UpdateFormatButtonStates();
                            });
                    });
            }
        }

        private void MemoTextBox_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_memoScrollViewer != null)
            {
                _memoScrollViewer.PointerWheelChanged -= ScrollViewer_PointerWheelChanged;
                _memoScrollViewer = null;
            }
        }

        // メニューが表示される直前に内容を動的に構築する
        private void MemoTextBox_ContextFlyout_Opening(object sender, object e)
        {
            if (sender is not MenuFlyout menu) return;

            // 毎回メニュー項目をリセット
            menu.Items.Clear();

            // 現在選択されているテキストを取得
            var selection = MemoTextBox.Document.Selection;
            string selectedText = (selection?.Text ?? string.Empty).Trim();
            bool hasSelection = !string.IsNullOrEmpty(selectedText);

            // --- 標準テキスト編集コマンド ---
            var cutItem = new MenuFlyoutItem { Text = "切り取り", Icon = new SymbolIcon(Symbol.Cut) };
            cutItem.Click += (s, args) => selection?.Cut();
            cutItem.IsEnabled = hasSelection && !MemoTextBox.IsReadOnly;

            var copyItem = new MenuFlyoutItem { Text = "コピー", Icon = new SymbolIcon(Symbol.Copy) };
            copyItem.Click += (s, args) => selection?.Copy();
            copyItem.IsEnabled = hasSelection;

            var pasteItem = new MenuFlyoutItem { Text = "貼り付け", Icon = new SymbolIcon(Symbol.Paste) };
            pasteItem.Click += (s, args) => selection?.Paste(0);
            pasteItem.IsEnabled = !MemoTextBox.IsReadOnly;

            var selectAllItem = new MenuFlyoutItem { Text = "すべて選択", Icon = new SymbolIcon(Symbol.SelectAll) };
            selectAllItem.Click += (s, args) => selection?.SetRange(0, int.MaxValue);

            menu.Items.Add(cutItem);
            menu.Items.Add(copyItem);
            menu.Items.Add(pasteItem);
            menu.Items.Add(selectAllItem);

            // --- Web連携コマンド ---
            if (hasSelection)
            {
                menu.Items.Add(new MenuFlyoutSeparator());

                // 1. Web検索
                string displaySearchText = selectedText.Length > 15 ? selectedText.Substring(0, 15) + "..." : selectedText;
                var searchItem = new MenuFlyoutItem
                {
                    Text = $"Webで \"{displaySearchText}\" を検索",
                    Icon = new FontIcon { Glyph = "\xE721", FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons") }
                };
                searchItem.Click += (s, args) =>
                {
                    string queryUrl = "https://www.google.com/search?q=" + Uri.EscapeDataString(selectedText);
                    OpenUrlInDefaultBrowser(queryUrl);
                };
                menu.Items.Add(searchItem);

                // 2. Webで開く (URL判定)
                bool isUrl = Uri.TryCreate(selectedText, UriKind.Absolute, out var uriResult)
                             && (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps);

                if (!isUrl && (selectedText.StartsWith("www.", StringComparison.OrdinalIgnoreCase) || selectedText.Contains(".")))
                {
                    isUrl = true; 
                }

                var openItem = new MenuFlyoutItem
                {
                    Text = "Webで開く",
                    Icon = new FontIcon { Glyph = "\xE71B", FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons") },
                    IsEnabled = isUrl 
                };
                openItem.Click += (s, args) =>
                {
                    string url = selectedText;
                    if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        url = "https://" + url; 
                    }
                    OpenUrlInDefaultBrowser(url);
                };
                menu.Items.Add(openItem);
            }

            // --- テキスト整形コマンド ---
            if (hasSelection && !MemoTextBox.IsReadOnly)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
                var removeEmptyLinesItem = new MenuFlyoutItem
                {
                    Text = "選択範囲の空行を削除",
                    Icon = new FontIcon { Glyph = "\xED60", FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons") }
                };
                removeEmptyLinesItem.Click += RemoveEmptyLines_Click;
                menu.Items.Add(removeEmptyLinesItem);
            }
        }

        /// <summary>
        /// .NET Core / WinUI 3環境で、安全にOS規定のデフォルトブラウザでURLを開くためのヘルパーです。
        /// </summary>
        private void OpenUrlInDefaultBrowser(string url)
        {
            try
            {
                // .NET Core環境で既定のブラウザを呼び出すには、UseShellExecute = true が必須です
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Browser Open Error] {ex.Message}");
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

        #region Win32 P/Invoke & HotKey/Tray Icon Management

        [System.Runtime.InteropServices.DllImport("comctl32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, IntPtr uIdSubclass, IntPtr dwRefData);

        [System.Runtime.InteropServices.DllImport("comctl32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern bool RemoveWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, IntPtr uIdSubclass);

        [System.Runtime.InteropServices.DllImport("comctl32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
        private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr CreatePopupMenu();

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "AppendMenuW")]
        private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string lpNewItem);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool DestroyMenu(IntPtr hMenu);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern uint RegisterWindowMessage(string lpString);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "LoadImageW", SetLastError = true)]
        private static extern IntPtr LoadImage(IntPtr hInst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "LoadIconW")]
        private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        private delegate IntPtr SUBCLASSPROC(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uVersion;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        private const uint NIM_ADD = 0;
        private const uint NIM_MODIFY = 1;
        private const uint NIM_DELETE = 2;
        private const uint NIF_MESSAGE = 1;
        private const uint NIF_ICON = 2;
        private const uint NIF_TIP = 4;
        private const uint WM_TRAYICON = 0x8000 + 2048;
        private static readonly uint WM_SHOWME = RegisterWindowMessage("SUMI_SHOW_ME_MESSAGE");
        private const uint MF_STRING = 0x00000000;
        private const uint TPM_RETURNCMD = 0x0100;
        private const uint TPM_LEFTALIGN = 0x0000;

        private void InitializeTrayAndHotKeys()
        {
            _hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            
            _subclassProc = new SUBCLASSPROC(WindowSubclassProc);
            SetWindowSubclass(_hWnd, _subclassProc, SUBCLASS_ID, IntPtr.Zero);

            UpdateTrayIconAndHotKeys();
        }

        private void UpdateTrayIconAndHotKeys()
        {
            UnregisterHotKey(_hWnd, HOTKEY_ID_QUIT);
            UnregisterHotKey(_hWnd, HOTKEY_ID_LAUNCH);
            if (TryParseHotKey(MemoStorage.QuitHotKey, out uint quitMod, out uint quitVk))
            {
                RegisterHotKey(_hWnd, HOTKEY_ID_QUIT, quitMod, quitVk);
            }
            if (TryParseHotKey(MemoStorage.LaunchHotKey, out uint launchMod, out uint launchVk))
            {
                RegisterHotKey(_hWnd, HOTKEY_ID_LAUNCH, launchMod, launchVk);
            }

            bool needTray = !string.IsNullOrEmpty(MemoStorage.LaunchHotKey);
            if (needTray)
            {
                AddOrModifyTrayIcon();
            }
            else
            {
                RemoveTrayIcon();
            }
        }

        private void AddOrModifyTrayIcon()
        {
            var nid = new NOTIFYICONDATA();
            nid.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NOTIFYICONDATA>();
            nid.hWnd = _hWnd;
            nid.uID = TRAY_ICON_ID;
            nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
            nid.uCallbackMessage = WM_TRAYICON;

            IntPtr hIcon = IntPtr.Zero;

            // 1. 実行ファイルの埋め込みリソースからカスタムアイコンの読み込みを試行 (ID: 32512 は IDI_APPLICATION に対応)
            IntPtr hInst = GetModuleHandle(null);
            if (hInst != IntPtr.Zero)
            {
                hIcon = LoadIcon(hInst, (IntPtr)32512);
            }

            // 2. 埋め込みリソースからの読み込みが失敗した場合は、ファイルシステムから直接読み込む
            if (hIcon == IntPtr.Zero)
            {
                string iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "AppIcon.ico");
                if (System.IO.File.Exists(iconPath))
                {
                    hIcon = LoadImage(IntPtr.Zero, iconPath, 1, 16, 16, 0x00000010);
                }
            }

            // 3. すべて失敗した場合は、システム既定のアプリケーションアイコンをフォールバックとして使用
            if (hIcon == IntPtr.Zero)
            {
                hIcon = LoadIcon(IntPtr.Zero, (IntPtr)32512);
            }

            nid.hIcon = hIcon;
            nid.szTip = "Sumi Memo";

            if (_isTrayIconAdded)
            {
                Shell_NotifyIcon(NIM_MODIFY, ref nid);
            }
            else
            {
                if (Shell_NotifyIcon(NIM_ADD, ref nid))
                {
                    _isTrayIconAdded = true;
                }
            }
        }

        private void RemoveTrayIcon()
        {
            if (_isTrayIconAdded)
            {
                var nid = new NOTIFYICONDATA();
                nid.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NOTIFYICONDATA>();
                nid.hWnd = _hWnd;
                nid.uID = TRAY_ICON_ID;
                Shell_NotifyIcon(NIM_DELETE, ref nid);
                _isTrayIconAdded = false;
            }
        }

        private IntPtr WindowSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData)
        {
            if (uMsg == WM_HOTKEY) // WM_HOTKEY
            {
                int id = wParam.ToInt32();
                if (id == HOTKEY_ID_QUIT) // Quit
                {
                    // Launchのホットキーが指定されている場合はタスクトレイに常駐するため、
                    // Quitのホットキーを入力したとしても完全終了せず、右上のバツボタンと同じ動作（Hide）にする
                    if (string.IsNullOrEmpty(MemoStorage.LaunchHotKey))
                    {
                        _isQuitting = true;
                        OnShutdown();
                        Close();
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
                                System.Diagnostics.Debug.WriteLine($"[HotKey Hide Save Error] {ex.Message}");
                            }
                        }
                        // 保存完了後はデバウンスタイマーをキャンセルし、保存コールバックが二重起動しないようにする
                        _scheduler?.Cancel();

                        // 非表示にする前に最新のウィンドウ配置を保存
                        var pos = _appWindow.Position;
                        var size = _appWindow.Size;
                        if (size.Width > 100 && size.Height > 100 && pos.X > -10000 && pos.Y > -10000)
                        {
                            MemoStorage.SaveWindowPlacementAtomic(pos.X, pos.Y, size.Width, size.Height);
                        }
                        // 現在表示しているメモIDを記録して設定に保存
                        MemoStorage.LastNoteId = MemoStorage.CurrentNoteId;
                        MemoStorage.SaveSettings();
                        _appWindow.Hide();
                    }
                    return IntPtr.Zero;
                }
                else if (id == HOTKEY_ID_LAUNCH) // Launch
                {
                    ShowAndActivateWindow();
                    return IntPtr.Zero;
                }
            }
            else if (uMsg == WM_TRAYICON)
            {
                uint mouseMsg = (uint)lParam.ToInt32();
                if (mouseMsg == WM_LBUTTONUP /* WM_LBUTTONUP */ || mouseMsg == WM_LBUTTONDBLCLK /* WM_LBUTTONDBLCLK */)
                {
                    ShowAndActivateWindow();
                }
                else if (mouseMsg == WM_RBUTTONUP /* WM_RBUTTONUP */)
                {
                    ShowTrayContextMenu();
                }
            }
            else if (uMsg == WM_SHOWME)
            {
                ShowAndActivateWindow();
                return IntPtr.Zero;
            }

            return DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        private void ShowAndActivateWindow()
        {
            _appWindow.Show();
            
            try
            {
                var presenter = _appWindow.Presenter.As<OverlappedPresenter>();
                if (presenter != null)
                {
                    presenter.Restore();
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Window Restore Error] {ex.Message}"); }

            SetForegroundWindow(_hWnd);

            if (MemoTextBox != null)
            {
                MemoTextBox.Focus(FocusState.Programmatic);
                // テキスト末尾にカーソルを移動
                int pos = GetCaretEndPosition();
                MemoTextBox.Document.Selection.SetRange(pos, pos);
            }
        }

        private void ShowTrayContextMenu()
        {
            POINT pos;
            GetCursorPos(out pos);

            IntPtr hMenu = CreatePopupMenu();
            AppendMenu(hMenu, MF_STRING, (IntPtr)1, "Show");
            AppendMenu(hMenu, MF_STRING, (IntPtr)2, "Quit");

            SetForegroundWindow(_hWnd);
            int selected = TrackPopupMenu(hMenu, TPM_RETURNCMD | TPM_LEFTALIGN, pos.X, pos.Y, 0, _hWnd, IntPtr.Zero);
            PostMessage(_hWnd, 0, IntPtr.Zero, IntPtr.Zero);
            DestroyMenu(hMenu);

            if (selected == 1)
            {
                ShowAndActivateWindow();
            }
            else if (selected == 2)
            {
                _isQuitting = true;
                OnShutdown();
                Close();
            }
        }

        private static bool TryParseHotKey(string hotkeyStr, out uint fsModifiers, out uint vk)
        {
            fsModifiers = 0;
            vk = 0;
            if (string.IsNullOrWhiteSpace(hotkeyStr))
            {
                return false;
            }

            var parts = hotkeyStr.Split('+');
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();
                if (i == parts.Length - 1)
                {
                    if (Enum.TryParse<Windows.System.VirtualKey>(part, true, out var virtualKey))
                    {
                        vk = (uint)virtualKey;
                    }
                    else
                    {
                        return false;
                    }
                }
                else
                {
                    if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("Control", StringComparison.OrdinalIgnoreCase))
                    {
                        fsModifiers |= 0x0002;
                    }
                    else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
                    {
                        fsModifiers |= 0x0001;
                    }
                    else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                    {
                        fsModifiers |= 0x0004;
                    }
                    else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase) || part.Equals("Windows", StringComparison.OrdinalIgnoreCase))
                    {
                        fsModifiers |= 0x0008;
                    }
                    else
                    {
                        return false;
                    }
                }
            }

            return vk != 0;
        }

        private void HotKeyFlyout_Opened(object sender, object e)
        {
            // 一時的にグローバルホットキーを無効化
            UnregisterHotKey(_hWnd, 1001);
            UnregisterHotKey(_hWnd, 1002);

            if (sender is Flyout flyout)
            {
                if (flyout == LaunchHotKeyFlyout)
                {
                    LaunchHotKeyInput.Text = MemoStorage.LaunchHotKey;
                    LaunchHotKeyInput.Focus(FocusState.Programmatic);
                }
                else if (flyout == QuitHotKeyFlyout)
                {
                    QuitHotKeyInput.Text = MemoStorage.QuitHotKey;
                    QuitHotKeyInput.Focus(FocusState.Programmatic);
                }
            }
        }

        private void HotKeyFlyout_Closed(object sender, object e)
        {
            UpdateTrayIconAndHotKeys();
        }

        private void HotKeyInput_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (sender is not TextBox textBox) return;
            var key = e.Key;

            var ctrlState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
            var altState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu);
            var shiftState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
            var winState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.LeftWindows) | 
                           Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.RightWindows);

            bool ctrl = (ctrlState & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
            bool alt = (altState & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
            bool shift = (shiftState & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
            bool win = (winState & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

            e.Handled = true;

            if (key == Windows.System.VirtualKey.Enter)
            {
                if (textBox == LaunchHotKeyInput)
                {
                    SaveLaunchHotKey();
                }
                else if (textBox == QuitHotKeyInput)
                {
                    SaveQuitHotKey();
                }
                return;
            }

            if (key == Windows.System.VirtualKey.Escape)
            {
                if (textBox == LaunchHotKeyInput)
                {
                    LaunchHotKeyFlyout.Hide();
                }
                else if (textBox == QuitHotKeyInput)
                {
                    QuitHotKeyFlyout.Hide();
                }
                return;
            }

            if (key == Windows.System.VirtualKey.Back || key == Windows.System.VirtualKey.Delete)
            {
                textBox.Text = string.Empty;
                return;
            }

            // Ignore modifier keys themselves
            if (key == Windows.System.VirtualKey.Control || 
                key == Windows.System.VirtualKey.Menu || 
                key == Windows.System.VirtualKey.Shift || 
                key == Windows.System.VirtualKey.LeftWindows || 
                key == Windows.System.VirtualKey.RightWindows)
            {
                var sbTemp = new System.Text.StringBuilder();
                if (ctrl) sbTemp.Append("Ctrl+");
                if (alt) sbTemp.Append("Alt+");
                if (shift) sbTemp.Append("Shift+");
                if (win) sbTemp.Append("Win+");
                textBox.Text = sbTemp.ToString();
                return;
            }

            var sb = new System.Text.StringBuilder();
            if (ctrl) sb.Append("Ctrl+");
            if (alt) sb.Append("Alt+");
            if (shift) sb.Append("Shift+");
            if (win) sb.Append("Win+");
            sb.Append(key.ToString());

            textBox.Text = sb.ToString();
        }

        private void SaveLaunchHotKey_Click(object sender, RoutedEventArgs e)
        {
            SaveLaunchHotKey();
        }

        private void ClearLaunchHotKey_Click(object sender, RoutedEventArgs e)
        {
            LaunchHotKeyInput.Text = string.Empty;
            SaveLaunchHotKey();
        }

        private void CancelLaunchHotKey_Click(object sender, RoutedEventArgs e)
        {
            LaunchHotKeyFlyout.Hide();
        }

        private void SaveQuitHotKey_Click(object sender, RoutedEventArgs e)
        {
            SaveQuitHotKey();
        }

        private void ClearQuitHotKey_Click(object sender, RoutedEventArgs e)
        {
            QuitHotKeyInput.Text = string.Empty;
            SaveQuitHotKey();
        }

        private void CancelQuitHotKey_Click(object sender, RoutedEventArgs e)
        {
            QuitHotKeyFlyout.Hide();
        }

        private void SaveLaunchHotKey()
        {
            string val = LaunchHotKeyInput.Text;
            MemoStorage.LaunchHotKey = val;
            MemoStorage.SaveSettings();
            LaunchHotKeyButton.Content = val;
            LaunchHotKeyFlyout.Hide();
        }

        private void SaveQuitHotKey()
        {
            string val = QuitHotKeyInput.Text;
            MemoStorage.QuitHotKey = val;
            MemoStorage.SaveSettings();
            QuitHotKeyButton.Content = val;
            QuitHotKeyFlyout.Hide();
        }

        #endregion

        #region 検索・置換機能

        // 検索・置換パネルのドラッグ移動用変数
        private bool _isDraggingFindPanel = false;
        private Windows.Foundation.Point _dragStartPoint;
        private double _dragStartX;
        private double _dragStartY;

        // 検索一致件数・インデックス追跡用変数
        private System.Collections.Generic.List<int> _matchStartPositions = new();
        private int _currentMatchIndex = -1;

        private void FindReplacePanel_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            var properties = e.GetCurrentPoint(FindReplacePanel).Properties;
            if (properties.IsLeftButtonPressed)
            {
                _isDraggingFindPanel = true;
                _dragStartPoint = e.GetCurrentPoint(this.Content).Position;
                _dragStartX = FindReplaceTransform.X;
                _dragStartY = FindReplaceTransform.Y;
                FindReplacePanel.CapturePointer(e.Pointer);
                e.Handled = true;
            }
        }

        private void FindReplacePanel_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_isDraggingFindPanel)
            {
                var currentPoint = e.GetCurrentPoint(this.Content).Position;
                double deltaX = currentPoint.X - _dragStartPoint.X;
                double deltaY = currentPoint.Y - _dragStartPoint.Y;

                FindReplaceTransform.X = _dragStartX + deltaX;
                FindReplaceTransform.Y = _dragStartY + deltaY;
                e.Handled = true;
            }
            else
            {
                // ドラッグ中ではない場合、インタラクティブコントロールの上ならカーソルを戻し、空き領域なら移動矢印にする
                UpdateCursor(e.OriginalSource);
            }
        }

        private void FindReplacePanel_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_isDraggingFindPanel)
            {
                FindReplacePanel.ReleasePointerCapture(e.Pointer);
                _isDraggingFindPanel = false;
                e.Handled = true;
            }
        }

        private void FindReplacePanel_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            UpdateCursor(e.OriginalSource);
        }

        private void FindReplacePanel_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            typeof(UIElement).InvokeMember(
                "ProtectedCursor",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.SetProperty,
                null,
                FindReplacePanel,
                new object?[] { null });
        }

        private void UpdateCursor(object originalSource)
        {
            if (originalSource is DependencyObject depObj)
            {
                DependencyObject current = depObj;
                bool isInteractive = false;
                while (current != null && current != FindReplacePanel)
                {
                    if (current is Button || current is ToggleButton || current is TextBox)
                    {
                        isInteractive = true;
                        break;
                    }
                    current = VisualTreeHelper.GetParent(current);
                }

                var cursor = isInteractive ? null : Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeAll);
                typeof(UIElement).InvokeMember(
                    "ProtectedCursor",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.SetProperty,
                    null,
                    FindReplacePanel,
                    new object?[] { cursor });
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
            // 位置を初期位置にリセットする
            FindReplaceTransform.X = 0;
            FindReplaceTransform.Y = 0;

            FindReplacePanel.Visibility = Visibility.Visible;
            ToggleReplaceModeBtn.IsChecked = showReplace;
            ReplaceRow.Visibility = showReplace ? Visibility.Visible : Visibility.Collapsed;
            FindStatusTextBlock.Visibility = Visibility.Collapsed;

            var selection = MemoTextBox.Document.Selection;
            if (selection != null && !string.IsNullOrEmpty(selection.Text) && !selection.Text.Contains('\r'))
            {
                FindTextBox.Text = selection.Text;
            }

            RecalculateMatches(); // 表示したタイミングで一致件数を計測

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

        private void FindTextBox_TextChanged(object sender, Microsoft.UI.Xaml.Controls.TextChangedEventArgs e)
        {
            RecalculateMatches();
        }

        #endregion

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

                    if (_windowPlacementTimer != null)
                    {
                        _windowPlacementTimer.Stop();
                        _windowPlacementTimer.Tick -= WindowPlacementTimer_Tick;
                    }

                    if (_settingsSaveTimer != null)
                    {
                        _settingsSaveTimer.Stop();
                        _settingsSaveTimer.Tick -= SettingsSaveTimer_Tick;
                    }

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

                    // ★ 追加: RootGrid のイベント解除
                    if (RootGrid != null)
                    {
                        RootGrid.SizeChanged -= RootGrid_SizeChanged;
                    }

                    // ★ 追加: KeyDown ハンドラの解除
                    if (this.Content != null)
                    {
                        this.Content.RemoveHandler(UIElement.KeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler(Global_KeyDown));
                    }

                    // ★ 追加: AppWindow イベントの解除
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
                    UnregisterHotKey(_hWnd, 1001);
                    UnregisterHotKey(_hWnd, 1002);
                    if (_subclassProc != null)
                    {
                        RemoveWindowSubclass(_hWnd, _subclassProc, 1);
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

        private static string TrimTrailingRtfPar(string rtf)
        {
            if (string.IsNullOrEmpty(rtf)) return rtf;

            // RTF の閉じ括弧 '}' の直前に \par が存在するかを後ろから走査して厳密に確認します
            int lastCloseBrace = rtf.LastIndexOf('}');
            if (lastCloseBrace == -1) return rtf;

            // 閉じカッコの直前にある改行文字やスペースをスキップ
            int searchIndex = lastCloseBrace - 1;
            while (searchIndex >= 0 && (rtf[searchIndex] == '\r' || rtf[searchIndex] == '\n' || rtf[searchIndex] == ' '))
            {
                searchIndex--;
            }

            if (searchIndex >= 3)
            {
                // ターゲット位置が本当に "\\par" であるかを部分的に検証して安全にトリム
                int parIndex = rtf.LastIndexOf("\\par", searchIndex, 4, StringComparison.Ordinal);
                if (parIndex != -1 && parIndex == searchIndex - 3)
                {
                    return rtf.Remove(parIndex, 4);
                }
            }

            return rtf;
        }

        private void MarkAsDirty()
        {
            _isDirty = true;
            _revision++;
            _scheduler?.Schedule();
        }

        private int GetCaretEndPosition()
        {
            if (MemoTextBox == null) return 0;
            MemoTextBox.Document.GetText(Microsoft.UI.Text.TextGetOptions.UseLf, out string text);
            if (text.EndsWith("\r") || text.EndsWith("\n"))
                text = text.Substring(0, text.Length - 1);
            return text.Length;
        }

        public static Microsoft.UI.Xaml.Media.Brush GetJustDoItBrush(bool isJustDoIt)
        {
            if (isJustDoIt)
            {
                return new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 193, 7));
            }
            return new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 136, 136, 136));
        }

        public static Microsoft.UI.Xaml.Media.Brush GetJustDoItFillBrush(bool isJustDoIt)
        {
            if (isJustDoIt)
            {
                return new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 193, 7));
            }
            return new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }

        public static Microsoft.UI.Xaml.Media.Brush GetJustDoItStrokeBrush(bool isJustDoIt)
        {
            if (isJustDoIt)
            {
                return new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 193, 7));
            }
            return new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 136, 136, 136));
        }

        public static Microsoft.UI.Xaml.Visibility BoolToVisibility(bool visible)
        {
            return visible ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        }

        #endregion
    }

    /// <summary>
    /// メモアイテムのリストバインディング用のビューモデルです。
    /// </summary>
    public class NoteItemViewModel
    {
        private static readonly Microsoft.UI.Xaml.Media.Brush PinForegroundPinned = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
        private static readonly Microsoft.UI.Xaml.Media.Brush PinForegroundUnpinned = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 204, 204, 204));
        private static readonly Microsoft.UI.Xaml.Media.Brush BackgroundBrushHighlighted = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(30, 255, 176, 0));
        private static readonly Microsoft.UI.Xaml.Media.Brush BackgroundBrushTransparent = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        private static readonly Microsoft.UI.Xaml.Media.Brush BackgroundBrushCurrent = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 37, 37, 37)); // #252525 (選択中状態の背景色)

        public string Id { get; }
        public string Title { get; }
        public string Subtitle { get; }
        public bool IsPinned { get; }
        public bool IsCurrent { get; }
        public bool IsHighlighted { get; }
        public string PinToolTip => IsPinned ? "ピン留め解除" : "ピン留め";
        public Microsoft.UI.Xaml.Media.Brush PinForeground => IsPinned ? PinForegroundPinned : PinForegroundUnpinned;
        public Visibility CurrentIndicatorVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;
        public Visibility PinnedFillVisibility => IsPinned ? Visibility.Visible : Visibility.Collapsed;
        public Visibility DeleteButtonVisibility => MemoStorage.ShowDeleteButton ? Visibility.Visible : Visibility.Collapsed;
        public Microsoft.UI.Xaml.Media.Brush BackgroundBrush => IsHighlighted ? BackgroundBrushHighlighted : (IsCurrent ? BackgroundBrushCurrent : BackgroundBrushTransparent);

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

    /// <summary>
    /// すべてのタスクビューでノートごとにグループ化して表示するためのビューモデルです。
    /// </summary>
    public class AllTasksGroupViewModel
    {
        public string NoteId { get; }
        public string NoteTitle { get; }
        public System.Collections.ObjectModel.ObservableCollection<TaskItemViewModel> Tasks { get; }

        public AllTasksGroupViewModel(string noteId, string noteTitle, System.Collections.ObjectModel.ObservableCollection<TaskItemViewModel> tasks)
        {
            NoteId = noteId;
            NoteTitle = noteTitle;
            Tasks = tasks;
        }
    }

    /// <summary>
    /// bool値をVisibilityに変換するコンバーターです。
    /// </summary>
    public partial class BoolToVisibilityConverter : Microsoft.UI.Xaml.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool b)
            {
                return b ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
            }
            return Microsoft.UI.Xaml.Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// マウスポインターが乗ったときにリサイズカーソルとハイライトを表示する Grid です。
    /// </summary>
    public partial class ResizableGrid : Microsoft.UI.Xaml.Controls.Grid
    {
        public ResizableGrid()
        {
            this.PointerEntered += (s, e) =>
            {
                this.ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast);
                this.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.DimGray);
            };
            this.PointerExited += (s, e) =>
            {
                this.ProtectedCursor = null;
                this.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            };
        }
    }

}
