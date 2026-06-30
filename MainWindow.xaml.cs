using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Dispatching;
using System;
using Windows.Graphics;
using WinRT;

namespace sumi
{
    /// <summary>
    /// メモの入力 UI と、ウィンドウのアクティベーション・ライフサイクル制御を行うメインウィンドウクラスです。
    /// </summary>
    public sealed partial class MainWindow : Window, IDisposable
    {
        private readonly SaveScheduler _scheduler;
        private readonly AppWindow _appWindow;
        private bool _isRestoring = false;
        private bool _isDirty = false;
        private bool _isInitialFocusSet = false;
        private bool _isInitializing = true;
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
            _scheduler = new SaveScheduler(this.DispatcherQueue, async () =>
            {
                long currentRevision = _revision;
                
                MemoTextBox.Document.GetText(Microsoft.UI.Text.TextGetOptions.UseLf, out string plainText);
                MemoTextBox.Document.GetText(Microsoft.UI.Text.TextGetOptions.FormatRtf, out string rtfText);
                
                if (plainText.EndsWith("\r")) plainText = plainText.Substring(0, plainText.Length - 1);
                else if (plainText.EndsWith("\n")) plainText = plainText.Substring(0, plainText.Length - 1);

                rtfText = TrimTrailingRtfPar(rtfText);

                bool success = await MemoStorage.SaveNoteTextAtomicAsync(MemoStorage.CurrentNoteId, plainText, rtfText);
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

            // 7. メモ一覧 Flyout の初期イベントフック
            NotesFlyout.Opened += NotesFlyout_Opened;

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

            if (RootGrid != null && RootGrid.Background is Microsoft.UI.Xaml.Media.SolidColorBrush brush)
            {
                brush.Color = Microsoft.UI.ColorHelper.FromArgb(255, 0x14, 0x14, 0x14);
                brush.Opacity = MemoStorage.Opacity / 100.0;
            }
        }

        private void ApplyGlobalThemeToEditor()
        {
            if (MemoTextBox == null) return;

            var doc = MemoTextBox.Document;
            doc.BatchDisplayUpdates(); // 描画を一時停止し、パフォーマンスを最大化
            try
            {
                // デフォルトの文字フォーマットを設定 (空ドキュメントや新規テキスト入力用)
                var defaultFormat = doc.GetDefaultCharacterFormat();
                if (defaultFormat != null)
                {
                    defaultFormat.Name = MemoStorage.FontFamily;
                    defaultFormat.Size = (float)MemoStorage.FontSize;
                    defaultFormat.Weight = GetDefaultFontWeight();
                    doc.SetDefaultCharacterFormat(defaultFormat);
                }

                // 全テキストを選択
                var range = doc.GetRange(0, int.MaxValue);

                // 1. フォントとサイズの上書き
                range.CharacterFormat.Name = MemoStorage.FontFamily;
                range.CharacterFormat.Size = (float)MemoStorage.FontSize;

                // 1.5. フォントウェイトの一括更新（太字装飾や見出しを維持しつつ、設定されたデフォルトの太さを適用）
                ushort defaultWeight = GetDefaultFontWeight();
                ushort boldWeight = GetBoldFontWeight();
                UpdateRangeWeight(doc, 0, range.Length, defaultWeight, boldWeight);

                // 1.6. 現在の選択範囲/カーソル位置のフォントウェイトも更新し、新規入力時の太さを同期
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

                // 2. 行間 (Line Spacing) の動的制御
                float lineSpacing = (float)MemoStorage.LineSpacing;
                if (lineSpacing < 1.0f)
                {
                    // 1.0未満は Multiple（倍数）が WinUI 仕様上無視されるため、Exactly（固定値）で上書き
                    // 標準的な行高さを「フォントサイズ × 1.25」のSingle相当ラインとして定義し、そこへ入力倍率を乗算
                    float exactLineHeight = (float)(MemoStorage.FontSize * 1.25f * lineSpacing);
                    range.ParagraphFormat.SetLineSpacing(Microsoft.UI.Text.LineSpacingRule.Exactly, exactLineHeight);
                }
                else
                {
                    // 1.0以上の場合は現行通り Multiple（倍数指定）を適用
                    range.ParagraphFormat.SetLineSpacing(Microsoft.UI.Text.LineSpacingRule.Multiple, lineSpacing);
                }

                // 3. 段落間余白 (Paragraph Spacing) の上書き
                range.ParagraphFormat.SpaceAfter = (float)MemoStorage.ParagraphSpacing;
            }
            finally
            {
                doc.ApplyDisplayUpdates(); // 一括反映
            }
        }

        private void UpdateRangeWeight(Microsoft.UI.Text.RichEditTextDocument doc, int start, int end, ushort defaultWeight, ushort boldWeight)
        {
            if (start >= end) return;

            var range = doc.GetRange(start, end);
            var bold = range.CharacterFormat.Bold;
            var size = range.CharacterFormat.Size;
            var weight = range.CharacterFormat.Weight;

            // 範囲内の太字、サイズ、ウェイトが均一である場合、一括で更新
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
                // 範囲の長さが1文字以下の場合は、これ以上分割できないためここで更新
                if (end - start <= 1)
                {
                    bool isBoldOrHeading = (bold == Microsoft.UI.Text.FormatEffect.On || size == 24 || size == 18);
                    ushort targetWeight = isBoldOrHeading ? boldWeight : defaultWeight;
                    if (weight != targetWeight)
                    {
                        range.CharacterFormat.Weight = targetWeight;
                    }
                    return;
                }

                // 均一でない場合は、範囲を半分に分割して再帰的に処理（分割統治法による高速化）
                int mid = start + (end - start) / 2;
                UpdateRangeWeight(doc, start, mid, defaultWeight, boldWeight);
                UpdateRangeWeight(doc, mid, end, defaultWeight, boldWeight);
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

            if (PlaceholderTextBlock != null)
            {
                PlaceholderTextBlock.Visibility = string.IsNullOrEmpty(plainText) ? Visibility.Visible : Visibility.Collapsed;
            }

            _isDirty = true;
            _revision++;
            _scheduler.Schedule(); // RTF生成と保存はスケジューラーに任せる

            NoteData? currentNote = null;
            lock (MemoStorage.Notes) { currentNote = MemoStorage.Notes.Find(n => n.Id == MemoStorage.CurrentNoteId); }
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

        private void OnShutdown()
        {
            // 二重呼び出しを防止（AppWindow_Closing が複数回発火するケースへの対策）
            if (_isShutdownCalled) return;
            _isShutdownCalled = true;

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
                    // フォールバック: 安全なインメモリキャッシュから取得
                    string safeText = MemoStorage.LoadMemoText();
                    MemoStorage.SaveNoteTextSync(MemoStorage.CurrentNoteId, safeText, safeText);
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

            QuitHotKeyButton.Content = MemoStorage.QuitHotKey;
            LaunchHotKeyButton.Content = MemoStorage.LaunchHotKey;

            FontItem.Visibility = Visibility.Visible;
            FontWeightItem.Visibility = Visibility.Visible;
            FontSizeItem.Visibility = Visibility.Visible;
            LineSpacingItem.Visibility = Visibility.Visible;
            OpacityItem.Visibility = Visibility.Visible;
            LaunchHotKeyItem.Visibility = Visibility.Visible;
            QuitHotKeyItem.Visibility = Visibility.Visible;
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
                ParagraphSpacingItem.Visibility = Visibility.Visible;
                OpacityItem.Visibility = Visibility.Visible;
                LaunchHotKeyItem.Visibility = Visibility.Visible;
                QuitHotKeyItem.Visibility = Visibility.Visible;
                DeleteNoteItem.Visibility = Visibility.Visible;
                return;
            }

            FontItem.Visibility = "font フォント 書体".Contains(query) ? Visibility.Visible : Visibility.Collapsed;
            FontWeightItem.Visibility = "font weight フォント ウェイト 太さ 太字".Contains(query) ? Visibility.Visible : Visibility.Collapsed;
            FontSizeItem.Visibility = "font size フォントサイズ 大きさ サイズ 文字".Contains(query) ? Visibility.Visible : Visibility.Collapsed;
            LineSpacingItem.Visibility = "line spacing 行間 行の高さ 高さ".Contains(query) ? Visibility.Visible : Visibility.Collapsed;
            ParagraphSpacingItem.Visibility = "paragraph spacing 段落間 行間 行の高さ 高さ 余白 改行".Contains(query) ? Visibility.Visible : Visibility.Collapsed;
            OpacityItem.Visibility = "opacity 不透明度 透明度 背景 透け".Contains(query) ? Visibility.Visible : Visibility.Collapsed;
            LaunchHotKeyItem.Visibility = "launch hotkey ショートカット キーボード 起動 ホットキー ランチ".Contains(query) ? Visibility.Visible : Visibility.Collapsed;
            QuitHotKeyItem.Visibility = "quit hotkey ショートカット キーボード 終了 ホットキー クイック".Contains(query) ? Visibility.Visible : Visibility.Collapsed;
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
                ApplyGlobalThemeToEditor();
                QueueSaveSettings();
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
            ApplyGlobalThemeToEditor();
            QueueSaveSettings();
        }

        private void LineSpacingComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;
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
            if (_isInitializing) return;
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
            QueueSaveSettings();
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

        private void HighlightTimer_Tick(object? sender, object e)
        {
            _highlightTimer.Stop();
            _highlightedNoteId = null;
            PopulateNotesList(NoteSearchBox.Text);
        }

        private void NoteItemGrid_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is Grid grid)
            {
                CheckPointerOverGrid(grid);
            }
        }

        private void NoteItemGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is Grid grid)
            {
                CheckPointerOverGrid(grid);
            }
        }

        private void CheckPointerOverGrid(Grid grid)
        {
            if (grid.ActualWidth == 0 || grid.ActualHeight == 0 || _hWnd == IntPtr.Zero) return;
            try
            {
                POINT pt;
                if (GetCursorPos(out pt))
                {
                    ScreenToClient(_hWnd, ref pt);
                    var transform = grid.TransformToVisual(this.Content);
                    var bounds = transform.TransformBounds(new Windows.Foundation.Rect(0, 0, grid.ActualWidth, grid.ActualHeight));
                    double scale = grid.XamlRoot?.RasterizationScale ?? 1.0;
                    double mouseX = pt.X / scale;
                    double mouseY = pt.Y / scale;

                    if (bounds.Contains(new Windows.Foundation.Point(mouseX, mouseY)))
                    {
                        var actionsPanel = grid.FindName("ActionsPanel") as UIElement;
                        if (actionsPanel != null)
                        {
                            actionsPanel.Visibility = Visibility.Visible;
                        }
                    }
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[PointerOver Check Error] {ex.Message}"); }
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

                string rtfFile = System.IO.Path.Combine(MemoStorage.NotesFolderPath, $"note_{id}.rtf");
                string txtFile = System.IO.Path.Combine(MemoStorage.NotesFolderPath, $"note_{id}.txt");

                if (System.IO.File.Exists(rtfFile))
                {
                    string rtfData = System.IO.File.ReadAllText(rtfFile, new System.Text.UTF8Encoding(false));
                    if (rtfData.StartsWith("{\\rtf"))
                    {
                        MemoTextBox.Document.SetText(Microsoft.UI.Text.TextSetOptions.FormatRtf, rtfData);
                    }
                    else
                    {
                        MemoTextBox.Document.SetText(Microsoft.UI.Text.TextSetOptions.None, rtfData);
                    }
                }
                else if (System.IO.File.Exists(txtFile))
                {
                    string txtData = System.IO.File.ReadAllText(txtFile, new System.Text.UTF8Encoding(false));
                    MemoTextBox.Document.SetText(Microsoft.UI.Text.TextSetOptions.None, txtData);
                }
                else
                {
                    MemoTextBox.Document.SetText(Microsoft.UI.Text.TextSetOptions.None, string.Empty);
                }

                ApplyGlobalThemeToEditor();

                TitleTextBlock.Text = note.Title;
                UpdateCharCount(note.CharCount);

                if (PlaceholderTextBlock != null)
                {
                    PlaceholderTextBlock.Visibility = string.IsNullOrEmpty(note.Content) ? Visibility.Visible : Visibility.Collapsed;
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
            // ピン留め→通常の順、各グループ内はLastOpened降順
            notes.Sort((a, b) =>
            {
                if (a.IsPinned != b.IsPinned) return a.IsPinned ? -1 : 1;
                return b.LastOpened.CompareTo(a.LastOpened);
            });
            return notes.ConvertAll(n => n.Id);
        }

        private void PopulateNotesList(string filter = "")
        {
            var query = filter.Trim();
            var pinnedVMs = new List<NoteItemViewModel>();
            var normalVMs = new List<NoteItemViewModel>();

            List<NoteData> sortedNotes;
            lock (MemoStorage.Notes)
            {
                sortedNotes = new List<NoteData>(MemoStorage.Notes);
            }
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

                bool isHighlighted = note.Id == _highlightedNoteId;
                var vm = new NoteItemViewModel(note.Id, note.Title, subtitle, note.IsPinned, isCurrent, isHighlighted);

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
            ApplyGlobalThemeToEditor();

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

            // 「文字選択は加味しない」要件を満たすため、選択範囲全体ではなく
            // カーソルの開始位置（キャレット位置）ピンポイントの書式を取得する
            int cursorPos = MemoTextBox.Document.Selection.StartPosition;
            var range = MemoTextBox.Document.GetRange(cursorPos, cursorPos);
            var format = range.CharacterFormat;

            // カーソル位置の書式に応じて ToggleButton の状態を同期
            FormatBoldBtn.IsChecked = (format.Bold == Microsoft.UI.Text.FormatEffect.On || format.Weight >= GetBoldFontWeight());
            FormatItalicBtn.IsChecked = format.Italic == Microsoft.UI.Text.FormatEffect.On;
            FormatUnderlineBtn.IsChecked = format.Underline != Microsoft.UI.Text.UnderlineType.None;
            FormatStrikethroughBtn.IsChecked = format.Strikethrough == Microsoft.UI.Text.FormatEffect.On;
            
            var highlightColor = Microsoft.UI.ColorHelper.FromArgb(255, 120, 100, 0);
            FormatHighlightBtn.IsChecked = format.BackgroundColor == highlightColor;

            // リスト状態の同期
            var listType = range.ParagraphFormat.ListType;
            FormatBulletListBtn.IsChecked = (listType == Microsoft.UI.Text.MarkerType.Bullet);
            FormatNumberListBtn.IsChecked = (listType == Microsoft.UI.Text.MarkerType.Arabic);

            // 見出し状態の同期（基準サイズと太字で判定）
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
                        case Windows.System.VirtualKey.H: // Ctrl + H でハイライト
                            ToggleHighlight();
                            e.Handled = true;
                            break;
                        case Windows.System.VirtualKey.T: // Ctrl + T で取り消し線
                            ToggleStrikethrough();
                            e.Handled = true;
                            break;
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
        }

        private void FormatItalic_Click(object sender, RoutedEventArgs e)
        {
            var format = MemoTextBox.Document.Selection.CharacterFormat;
            format.Italic = format.Italic == Microsoft.UI.Text.FormatEffect.On ? Microsoft.UI.Text.FormatEffect.Off : Microsoft.UI.Text.FormatEffect.On;
            UpdateFormatButtonStates();
        }

        private void FormatUnderline_Click(object sender, RoutedEventArgs e)
        {
            var format = MemoTextBox.Document.Selection.CharacterFormat;
            format.Underline = format.Underline == Microsoft.UI.Text.UnderlineType.None ? Microsoft.UI.Text.UnderlineType.Single : Microsoft.UI.Text.UnderlineType.None;
            UpdateFormatButtonStates();
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
        }

        private void FormatHeading1_Click(object? sender, RoutedEventArgs? e)
        {
            var selection = MemoTextBox.Document.Selection;
            bool isCurrentlyH1 = selection.CharacterFormat.Size == 24;

            selection.CharacterFormat.Size = isCurrentlyH1 ? (float)MemoStorage.FontSize : 24;
            selection.CharacterFormat.Bold = isCurrentlyH1 ? GetDefaultBoldEffect() : Microsoft.UI.Text.FormatEffect.On;
            selection.CharacterFormat.Weight = isCurrentlyH1 ? GetDefaultFontWeight() : GetBoldFontWeight();
            
            MemoTextBox.Focus(FocusState.Programmatic);
            UpdateFormatButtonStates();
        }

        private void FormatHeading2_Click(object? sender, RoutedEventArgs? e)
        {
            var selection = MemoTextBox.Document.Selection;
            bool isCurrentlyH2 = selection.CharacterFormat.Size == 18;

            selection.CharacterFormat.Size = isCurrentlyH2 ? (float)MemoStorage.FontSize : 18;
            selection.CharacterFormat.Bold = isCurrentlyH2 ? GetDefaultBoldEffect() : Microsoft.UI.Text.FormatEffect.On;
            selection.CharacterFormat.Weight = isCurrentlyH2 ? GetDefaultFontWeight() : GetBoldFontWeight();
            
            MemoTextBox.Focus(FocusState.Programmatic);
            UpdateFormatButtonStates();
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
        }

        private void ToggleStrikethrough()
        {
            var format = MemoTextBox.Document.Selection.CharacterFormat;
            format.Strikethrough = format.Strikethrough == Microsoft.UI.Text.FormatEffect.On ? Microsoft.UI.Text.FormatEffect.Off : Microsoft.UI.Text.FormatEffect.On;
            UpdateFormatButtonStates();
        }

        private void ClearFormatting()
        {
            var selection = MemoTextBox.Document.Selection;
            var format = selection.CharacterFormat;
            format.Bold = GetDefaultBoldEffect();
            format.Weight = GetDefaultFontWeight();
            format.Italic = Microsoft.UI.Text.FormatEffect.Off;
            format.Underline = Microsoft.UI.Text.UnderlineType.None;
            format.Strikethrough = Microsoft.UI.Text.FormatEffect.Off;
            format.BackgroundColor = Microsoft.UI.Colors.Transparent;
            format.Size = (float)MemoStorage.FontSize;

            selection.ParagraphFormat.ListType = Microsoft.UI.Text.MarkerType.None;

            UpdateFormatButtonStates();
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
                // ScrollViewer の PointerWheelChanged イベントにハンドラーを追加
                _memoScrollViewer.PointerWheelChanged += ScrollViewer_PointerWheelChanged;
            }

            // TTFP最適化: コンストラクタ内で保留されていたテキストを、初回描画完了後に適用する。
            if (_pendingNote != null)
            {
                MemoTextBox.TextChanged -= MemoTextBox_TextChanged;
                string id = _pendingNote.Id;
                _isRestoring = true;

                string rtfFile = System.IO.Path.Combine(MemoStorage.NotesFolderPath, $"note_{id}.rtf");
                string txtFile = System.IO.Path.Combine(MemoStorage.NotesFolderPath, $"note_{id}.txt");

                if (System.IO.File.Exists(rtfFile))
                {
                    string rtfData = System.IO.File.ReadAllText(rtfFile, new System.Text.UTF8Encoding(false));
                    if (rtfData.StartsWith("{\\rtf"))
                    {
                        MemoTextBox.Document.SetText(Microsoft.UI.Text.TextSetOptions.FormatRtf, rtfData);
                    }
                    else
                    {
                        MemoTextBox.Document.SetText(Microsoft.UI.Text.TextSetOptions.None, rtfData);
                    }
                }
                else if (System.IO.File.Exists(txtFile))
                {
                    string txtData = System.IO.File.ReadAllText(txtFile, new System.Text.UTF8Encoding(false));
                    MemoTextBox.Document.SetText(Microsoft.UI.Text.TextSetOptions.None, txtData);
                }
                else
                {
                    MemoTextBox.Document.SetText(Microsoft.UI.Text.TextSetOptions.None, string.Empty);
                }

                ApplyGlobalThemeToEditor();

                if (PlaceholderTextBlock != null)
                {
                    PlaceholderTextBlock.Visibility = string.IsNullOrEmpty(_pendingNote.Content) ? Visibility.Visible : Visibility.Collapsed;
                }
                _pendingNote = null;

                this.DispatcherQueue.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    () =>
                    {
                        MemoTextBox.TextChanged -= MemoTextBox_TextChanged;
                        MemoTextBox.TextChanged += MemoTextBox_TextChanged;
                        _isRestoring = false;
                        MemoTextBox.Focus(FocusState.Programmatic);
                        int pos = GetCaretEndPosition();
                        MemoTextBox.Document.Selection.SetRange(pos, pos);
                        UpdateFormatButtonStates();
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

            string iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "AppIcon.ico");
            IntPtr hIcon = IntPtr.Zero;
            if (System.IO.File.Exists(iconPath))
            {
                hIcon = LoadImage(IntPtr.Zero, iconPath, 1, 16, 16, 0x00000010);
            }
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
                }

                // アンマネージドリソース（トレイアイコン、ホットキー、サブクラスなど）の解放
                RemoveTrayIcon();
                if (_hWnd != IntPtr.Zero)
                {
                    UnregisterHotKey(_hWnd, 1001); // 元のコード通りマジックナンバーを使用
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

            int lastPar = rtf.LastIndexOf("\\par");
            if (lastPar == -1) return rtf;

            return rtf.Remove(lastPar, 4);
        }

        private int GetCaretEndPosition()
        {
            if (MemoTextBox == null) return 0;
            MemoTextBox.Document.GetText(Microsoft.UI.Text.TextGetOptions.UseLf, out string text);
            if (text.EndsWith("\r") || text.EndsWith("\n"))
                text = text.Substring(0, text.Length - 1);
            return text.Length;
        }

        #endregion
    }

    /// <summary>
    /// メモアイテムのリストバインディング用のビューモデルです。
    /// </summary>
    public class NoteItemViewModel
    {
        private static readonly Microsoft.UI.Xaml.Media.Brush PinForegroundPinned = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 255, 176, 0));
        private static readonly Microsoft.UI.Xaml.Media.Brush PinForegroundUnpinned = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 204, 204, 204));
        private static readonly Microsoft.UI.Xaml.Media.Brush BackgroundBrushHighlighted = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(30, 255, 176, 0));
        private static readonly Microsoft.UI.Xaml.Media.Brush BackgroundBrushTransparent = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);

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
        public Microsoft.UI.Xaml.Media.Brush BackgroundBrush => IsHighlighted ? BackgroundBrushHighlighted : BackgroundBrushTransparent;

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
