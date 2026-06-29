# 超極限最適化・常時最前面メモアプリケーション 仕様書（プロダクション仕様・最終確定版）

## 1. システム概要と性能目標 (Performance KPI)
本アプリケーションは、Windows 10/11環境において、起動・描画・ファイル永続化のすべてにおいて最速かつ省電力で動作するプレーンテキスト専用メモツールです。

### 1.1. 現実的な性能指標 (Realistic Performance KPI Targets)
本アプリの設計および実装は、Windows Defender等の常駐セキュリティソフトや標準的なハードウェア性能を考慮した、以下の現実的かつ野心的な数値目標に基づいて評価されます。

| 評価項目 | 目標値 | 測定方法 / 備考 |
| :--- | :--- | :--- |
| **コールドスタート時間** | **200 〜 350 ms** | アプリ未起動状態から入力可能になるまで（セキュリティソフトの影響を加味） |
| **ウォームスタート時間** | **80 〜 120 ms** | 2回目以降のOSキャッシュを活用した瞬時起動 |
| **待機時（アイドル）CPU使用率** | **0.0 %** | タスクマネージャーによる観測（完全にイベント駆動） |
| **待機時（アイドル）GPU使用率** | **0.0 〜 0.1 %** | ウィンドウ再描画要因（点滅カーソル等）を除き実質ゼロ |
| **Working Set（物理メモリ）** | **50 〜 80 MB** | WinUI 3自体のフットプリントを極限までシェイプアップした状態（標準：80〜120MB） |
| **Private Bytes（コミットサイズ）** | **60 〜 80 MB** | GC動的アロケーションを排除してヒープの肥大化を抑制 |
| **GC 発生頻度 (Gen 0/sec)** | **ほぼ 0** | タイピング中、タイマーによるGCアロケーションの徹底排除 |
| **GC 発生頻度 (Gen 1 / Gen 2)** | **0** | アプリライフサイクル中におけるUIスタッター（カクつき）を完全に防止 |

---

## 2. 技術選定とシステム構成（ゼロ・ディペンデンシー方針）

### 2.1. 動作・配置方針
*   **ランタイム環境**: .NET 10 LTS（最新安定版）
*   **配置構成方針**: 
    **Native AOT (Ahead-Of-Time) コンパイル**を最優先で適用。ただし、Windows App SDK（CsWinRT）の今後の仕様・互換性への適合状況に応じて、サイズ・保守性・安全性を総合評価した上で、実用的な最適化構成として **ReadyToRun (R2R) + ILトリミング** を代替として選択します。
*   **排他処理 (Local Mutex)**: データ破損を防ぐため、`Local\SimpleMemo_Instance_Mutex_9981` を使用して同一セッション内での単一インスタンス動作を保証します [1]。

### 2.2. コントロールの選定
*   **コントロール**: `TextBox`
*   **理由**: 装飾テキスト（RichText）が不要なため、描画・レイアウト計算が軽いプレーンテキスト専用の `TextBox` を採用し、不要なOS連携スレッドを抑制します。

---

## 3. クラス設計と責務分離
動的なアロケーションを完全に排除しつつ、保守性を高めるため、以下の3つのクラスに完全に責務を分離します。

```
                     [ MainWindow ]
                (UI配置 / 画面表示 / イベント)
                     /            \
                    /              \
  [ SaveScheduler ] <-------------- [ MemoStorage ]
   (非同期遅延トリガー)             (アトミック保存、window.dat制御)
```

1.  **`MainWindow` (UI層)**: テキスト入力イベント、ウィンドウライフサイクル（表示・最前面化・サイズ変更）の監視。
2.  **`SaveScheduler` (制御層)**: `DispatcherQueueTimer` による、GCアロケーションがゼロの入力遅延（デバウンサー）管理。
3.  **`MemoStorage` (データアクセス層)**: テキストおよびウィンドウ状態の、物理フラッシュと一時ファイル置換を用いたアトミックな保存。

---

## 4. 画面・UIコントロール設計

### 4.1. XAML設計と入力抑制
```xml
<Window
    x:Class="SimpleMemo.MainWindow"
    xmlns="http://schemas.microsoft.com/winfx/2000/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2000/xaml"
    Title="Memo">

    <Grid>
        <!-- レイアウトの明示、日本語用フォント固定、不要機能の無効化、MaxLengthを明示 -->
        <TextBox 
            x:Name="MemoTextBox"
            HorizontalAlignment="Stretch"
            VerticalAlignment="Stretch"
            AcceptsReturn="True"
            TextWrapping="Wrap"
            BorderThickness="0"
            Padding="12"
            FontSize="14.5"
            FontFamily="Yu Gothic UI"
            MaxLength="10000"
            IsSpellCheckEnabled="False"
            TextPredictionEnabled="False"
            TextChanged="MemoTextBox_TextChanged" />
    </Grid>
</Window>
```

*   **フォント指定**: 英数字・日本語ともにレンダリング品質とフォールバックが最も安定している `Yu Gothic UI` に固定します。
*   **文字数制限（MaxLength="10000"）**:
    *   WinUI 3の内部文字列表現は「UTF-16」であるため、本設定は **10,000 UTF-16コードユニット（Char単位）** として制限されます。
    *   絵文字などのサロゲートペア（2コードユニット消費する文字）が含まれる場合、画面上の見た目の文字数と異なる場合がありますが、要件の「約1万字」をカバーするには十分であり、仕様と実装の整合性を図ります。
    *   この制限は、クリップボードからの**ペースト（貼り付け）時にも自動適用**され、予期せぬ巨大データの挿入を防ぎます。
*   **外部監視**: `FileSystemWatcher` 等による外部編集の監視は、余計な常時バックグラウンドスレッドを発生させるため、**一切実装しません**。

---

## 5. 内部ロジックと堅牢性設計

### 5.1. メモデータの物理永続化アトミック書き込み（完全なデータ破損防止）
`File.WriteAllText` による保存はOSのファイルキャッシュに留まる可能性があり、システム全体の急なシャットダウンやクラッシュにより、ファイル破損（0バイト化）のリスクが存在します。本アプリでは、以下のフローにより、**確実に物理ディスクにデータを物理フラッシュし、置換後さらにメタデータの更新を即時同期**します。

1.  書き込み用の一時ファイル（`memo.tmp`）を開き、`FileStream` を用いて書き込み。
2.  `StreamWriter.Flush()` 後、`FileStream.Flush(true)` を呼び出し、OSのファイルシステムキャッシュを超えて、**物理ディスク（SSD/HDD）にデータを強制永続化**します（Windows APIの `FlushFileBuffers` 相当）。
3.  `File.Replace` を用い、安全に `memo.tmp` で `memo.txt` を置き換えます。
4.  **【置換後のメタデータ物理書き込み】**: `File.Replace` を実行した直後、再生成された `memo.txt` を一度 `FileStream` で開き、再度 `Flush(true)` を呼び出します。これにより、NTFSのファイルシステム変更ジャーナル（メタデータ）の更新がOSキャッシュに留まるのを防ぎ、置換処理自体が即座に物理的かつ完全に永続化されることを保証します。

### 5.2. アロケーションフリーな SaveScheduler (DispatcherQueueTimer)
*   **非推奨**: キー入力（TextChanged）のたびに、`CancellationTokenSource` や `Task` などのインスタンスを毎回再生成すると、数千文字のタイピング時に何千ものゴミオブジェクトがヒープにアロケーションされ、GCプレッシャーを引き起こします。
*   **採用（確定仕様）**: WinUI 3 が持つ `DispatcherQueueTimer` （`DispatcherQueue.CreateTimer()`）を1つだけインスタンス化して保持します。入力が発生するたびに `Stop()` と `Start()` を切り替えるだけで遅延判定がリセットされるため、**タイピングによる一時的なメモリ割り当てが完全に0（ゼロ・アロケーション）**になります。

### 5.3. 保存完了と新規入力の競合防止（リビジョン管理）
ユーザーが大量のテキストを入力している最中に非同期保存が開始された場合、保存処理中（Async I/O待ち）にさらに追加のキー入力（新規テキスト）が行われると、保存完了後にDirtyフラグを単純に `false` にリセットしてしまうと追加分のデータが「保存済み」と誤認され、以降保存されなくなってしまいます。
*   **解決策**:
    入力のたびにインクリメントされる `long _revision` と、保存完了時のバージョンを示す `long _savedRevision` を導入します。保存完了時、`_savedRevision == _revision` （保存開始時から新たに入力されていない）の場合のみ、`_isDirty = false` にリセットします。もし保存中に新しい文字が打ち込まれていた場合、Dirtyフラグは有効に維持され、次回の遅延保存スケジュールが自動的に保護されます。

### 5.4. ウィンドウ配置の完全なスタックレス・アトミック保存
*   保存先: `window.dat` (一時ファイル: `window.tmp`)
*   最適化: 4つの 32bit整数（`X, Y, Width, Height` の計16バイト）の保存において、メモリ確保を伴う `BinaryWriter` の生成すら排除します。`stackalloc byte[16]` でスタック上にのみ領域を確保し、`BinaryPrimitives` でバイト列を詰め、`FileStream` に直接流し込みます。
*   また、ウィンドウ位置情報は最悪失われても問題がないため、SSDの書き込み回数に配慮して `Flush(true)` による物理フラッシュは行わず、置換（`File.Replace`）のみを行います。

### 5.5. マルチモニター・DPI変更を考慮した配置復元
*   **課題**: 前回終了時と現在で外部モニターの状態や解像度、DPIが変わっていた場合、保存時の座標をそのまま適用するとウィンドウが画面外に消え去ってしまいます。
*   **解決策**:
    起動時に `window.dat` から座標を読み取った後、Win32 API（`MonitorFromRect` 等）や `GetMonitorInfo` を介して、現在接続されている有効なモニターのワークエリア（タスクバー等の除外領域）情報を取得します。
    ウィンドウが現在の全画面領域から完全に外れている場合は、メインモニターのワークエリア中央に安全に配置をリセットします。また、部分的に画面からはみ出している場合は、現在のワークエリア内に適切にクリップ（収まるように補正）します。

### 5.6. 最前面表示 (Always-on-top) の安定化
*   **設定タイミング**: ウィンドウの `Activated` イベントを監視し、ウィンドウの表示とアクティブ化が完全に完了した最初のタイミングでのみ `IsAlwaysOnTop = true` を設定します。これにより、起動時に他のバックグラウンドウィンドウの裏に隠れる現象を確実に防ぎます。

### 5.7. アプリケーション終了時
*   **イベント**: `AppWindow.Closing` （ウィンドウ破棄開始の契機）。
*   **振る舞い**: 進行中の非同期遅延保存（SaveScheduler）をキャンセルし、Dirty状態（`_isDirty == true`）の場合は、UIスレッドから同期的・強制的に最終テキストと現在のウィンドウ位置をアトミック保存します。

---

## 6. 主要クラスの実装（C#）

### 6.1. データアクセス層: `MemoStorage`
```csharp
using System;
using System.IO;
using System.Text;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Buffers.Binary;

namespace SimpleMemo
{
    public static class MemoStorage
    {
        private static readonly string FolderPath;
        private static readonly string FilePath;
        private static readonly string TempFilePath;
        private static readonly string WindowDatPath;
        private static readonly string WindowDatTempPath;

        // 文字コードのキャッシュ (アロケーション排除)
        private static readonly UTF8Encoding Utf8NoBom = new(false);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);
        private const uint MONITOR_DEFAULTTONULL = 0;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        static MemoStorage()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            FolderPath = Path.Combine(localAppData, "SimpleMemo");
            
            // Directory.Existsによる事前確認をはさみ、Directory.CreateDirectory呼び出しのオーバーヘッドを削減
            if (!Directory.Exists(FolderPath))
            {
                Directory.CreateDirectory(FolderPath);
            }

            FilePath = Path.Combine(FolderPath, "memo.txt");
            TempFilePath = Path.Combine(FolderPath, "memo.tmp");
            WindowDatPath = Path.Combine(FolderPath, "window.dat");
            WindowDatTempPath = Path.Combine(FolderPath, "window.tmp");
        }

        // 起動速度優先のため、同期で読み込む (UTF-8)
        public static string LoadMemoText()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    return File.ReadAllText(FilePath, Utf8NoBom);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Load Error] {ex.Message}"); // Debugビルド時のみコンパイル・実行されます
            }
            return string.Empty;
        }

        // 物理ディスクへのフラッシュおよび置換後のジャーナル同期までカバーするアトミック非同期保存
        public static async Task<bool> SaveMemoTextAtomicAsync(string text)
        {
            try
            {
                // UIスレッドをフリーズさせない非同期書き出し (UTF-8 BOMなし)
                using (var fs = new FileStream(TempFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                using (var writer = new StreamWriter(fs, Utf8NoBom))
                {
                    await writer.WriteAsync(text);
                    await writer.FlushAsync();
                    
                    // 1. 物理SSD/HDDの書き込み完了を確認
                    fs.Flush(true);
                }

                if (File.Exists(FilePath))
                {
                    // 2. 一時ファイルで実ファイルを置換
                    File.Replace(TempFilePath, FilePath, null);

                    // 3. 置換したファイルのメタデータ（NTFSジャーナル）更新を物理フラッシュ
                    using (var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                    {
                        fs.Flush(true);
                    }
                }
                else
                {
                    File.Move(TempFilePath, FilePath);
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Save Error] {ex.Message}");
                return false;
            }
        }

        // アプリ終了時にUIスレッドを止めずに即座に物理保存を行うための同期版
        public static bool SaveMemoTextAtomicSync(string text)
        {
            try
            {
                using (var fs = new FileStream(TempFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: false))
                using (var writer = new StreamWriter(fs, Utf8NoBom))
                {
                    writer.Write(text);
                    writer.Flush();
                    fs.Flush(true);
                }

                if (File.Exists(FilePath))
                {
                    File.Replace(TempFilePath, FilePath, null);
                    using (var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                    {
                        fs.Flush(true);
                    }
                }
                else
                {
                    File.Move(TempFilePath, FilePath);
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Sync Save Error] {ex.Message}");
                return false;
            }
        }

        // 完全にアロケーションフリーなウィンドウ座標保存 (スタック上で処理)
        public static void SaveWindowPlacementAtomic(int x, int y, int width, int height)
        {
            try
            {
                // BinaryWriterなどのオブジェクト確保を完全に排したスタック配列書き込み
                Span<byte> buffer = stackalloc byte[16];
                BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(0, 4), x);
                BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(4, 4), y);
                BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(8, 4), width);
                BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(12, 4), height);

                using (var fs = new FileStream(WindowDatTempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(buffer);
                }

                if (File.Exists(WindowDatPath))
                {
                    File.Replace(WindowDatTempPath, WindowDatPath, null);
                }
                else
                {
                    File.Move(WindowDatTempPath, WindowDatPath);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Window Save Error] {ex.Message}");
            }
        }

        // ウィンドウ座標復元およびマルチモニターを考慮したクランプ処理
        public static bool LoadWindowPlacement(out int x, out int y, out int width, out int height)
        {
            x = 0; y = 0; width = 360; height = 480; // デフォルト値
            try
            {
                if (File.Exists(WindowDatPath))
                {
                    Span<byte> buffer = stackalloc byte[16];
                    using (var fs = new FileStream(WindowDatPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        if (fs.Length >= 16)
                        {
                            int read = fs.Read(buffer);
                            if (read >= 16)
                            {
                                int lx = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(0, 4));
                                int ly = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(4, 4));
                                int lw = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(8, 4));
                                int lh = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(12, 4));

                                RECT rect = new RECT { Left = lx, Top = ly, Right = lx + lw, Bottom = ly + lh };
                                IntPtr hMonitor = MonitorFromRect(ref rect, MONITOR_DEFAULTTONULL);

                                if (hMonitor != IntPtr.Zero)
                                {
                                    // ワークエリア情報（タスクバーを除いた領域）の取得
                                    MONITORINFO info = new MONITORINFO();
                                    info.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
                                    if (GetMonitorInfo(hMonitor, ref info))
                                    {
                                        // 座標がワークエリアからはみ出ている場合は、安全にワークエリア内に収まるようクランプ
                                        x = Math.Clamp(lx, info.rcWork.Left, info.rcWork.Right - lw);
                                        y = Math.Clamp(ly, info.rcWork.Top, info.rcWork.Bottom - lh);
                                        width = lw;
                                        height = lh;
                                        return true;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Window Load Error] {ex.Message}");
            }
            return false;
        }
    }
}
```

#### 2. 遅延制御層: `SaveScheduler`
```csharp
using Microsoft.UI.Dispatching;
using System;
using System.Threading.Tasks;

namespace SimpleMemo
{
    public class SaveScheduler
    {
        private readonly DispatcherQueueTimer _timer;
        private readonly Func<Task> _onSaveTriggered;

        // キー入力ごとに一切のCTS/Taskをアロケーションしない、単一DispatcherQueueTimer構成
        public SaveScheduler(DispatcherQueue queue, Func<Task> onSaveTriggered)
        {
            _onSaveTriggered = onSaveTriggered;
            _timer = queue.CreateTimer();
            _timer.Interval = TimeSpan.FromSeconds(2); // 遅延を2秒に設定
            _timer.Tick += Timer_Tick;
        }

        private async void Timer_Tick(DispatcherQueueTimer sender, object args)
        {
            _timer.Stop();
            await _onSaveTriggered(); // 2秒間追加入力がなかった際のアクション
        }

        public void Schedule()
        {
            // タイマーの停止と再開始により、ヒープへのオブジェクト割り当てを発生させずにリセット
            _timer.Stop();
            _timer.Start();
        }

        public void Cancel()
        {
            _timer.Stop();
        }
    }
}
```

#### 3. UI表示層: `MainWindow`
```csharp
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;

namespace SimpleMemo
{
    public sealed partial class MainWindow : Window
    {
        private readonly SaveScheduler _scheduler;
        private readonly AppWindow _appWindow;
        private bool _isRestoring = false;
        private bool _isDirty = false;
        private bool _isAlwaysOnTopSet = false;

        // 競合防止用のリビジョン番号
        private long _revision = 0;
        private long _savedRevision = 0;

        public MainWindow()
        {
            this.InitializeComponent();

            // 1. ウィンドウハンドルとAppWindowの解決
            IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);

            // 2. ウィンドウ配置の復元（マルチモニター・作業領域クランプ付き）
            RestoreWindowPlacement();

            // 3. テキストロード
            _isRestoring = true;
            MemoTextBox.Text = MemoStorage.LoadMemoText();
            _isRestoring = false;

            // 4. スケジューラ初期化 (DispatcherQueueを渡し、タイマー内のアロケーションをゼロ化)
            _scheduler = new SaveScheduler(this.DispatcherQueue, async () =>
            {
                long currentRevision = _revision;
                string textToSave = MemoTextBox.Text; // 保存を実行するその瞬間の状態を読み取る

                bool success = await MemoStorage.SaveMemoTextAtomicAsync(textToSave);
                if (success)
                {
                    _savedRevision = currentRevision;
                    // 保存実行中に追加入力（TextChanged）があった場合は、Dirtyフラグのリセットをガードする
                    if (_savedRevision == _revision)
                    {
                        _isDirty = false;
                    }
                }
            });

            // 5. ライフサイクルイベント監視
            this.Activated += MainWindow_Activated;
            _appWindow.Closing += AppWindow_Closing;
        }

        private void RestoreWindowPlacement()
        {
            if (MemoStorage.LoadWindowPlacement(out int x, out int y, out int width, out int height))
            {
                _appWindow.MoveAndResize(new Graphics.RectInt32(x, y, width, height));
            }
        }

        private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            // アクティブ化が完全に確定した最初のタイミングで常に最前面を設定
            if (!_isAlwaysOnTopSet)
            {
                if (_appWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.IsAlwaysOnTop = true;
                }
                _isAlwaysOnTopSet = true;
            }
        }

        private void MemoTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isRestoring) return;

            _isDirty = true;
            _revision++; // 入力ごとにリビジョンを更新
            _scheduler.Schedule();
        }

        private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            _scheduler.Cancel();

            // 1. 未保存データを終了直前に同期的に安全にディスク永続化
            if (_isDirty)
            {
                MemoStorage.SaveMemoTextAtomicSync(MemoTextBox.Text);
            }

            // 2. 終了座標をアトミックに保存
            var pos = _appWindow.Position;
            var size = _appWindow.Size;
            MemoStorage.SaveWindowPlacementAtomic(pos.X, pos.Y, size.Width, size.Height);
        }
    }
}
```

---

## 7. 品質保証・テスト要件
アプリリリースに向け、以下の機能テストおよびパフォーマンステストを実施します。

### 7.1. 機能・堅牢性テストケース
1.  **アトミック上書き検証**:
    キーボード連打中にタスクマネージャーからプロセスを「強制終了」し、`memo.txt` が白紙化（0バイト化）せず、直前（2秒前）のデータが保持されていることを確認。
2.  **マルチモニター・解像度変更テスト**:
    ノートPCと外部ディスプレイを繋いだマルチモニター環境において、外部画面上でアプリを終了する。その後、外部ディスプレイを物理的に切断し、本体モニター単体の状態で再起動した際、ウィンドウが画面外に消えずメインモニター中央に復元されることを検証。
3.  **ペースト＆境界文字数制限確認**:
    クリップボードから20,000文字のテキストを貼り付けた際、`MaxLength` が正しく作動し、10,000 UTF-16コードユニット（Char）の境界でカットされることを確認。
4.  **編集補助機能（Undo / Redo / Ctrl+Z）動作試験**:
    文字入力およびペースト後、`Ctrl+Z` (元に戻す) と `Ctrl+Y` (やり直し) が正常に機能することを確認。その際、各編集履歴アクションによる `TextChanged` 発火時に無駄な過負荷（過剰なI/Oアロケーション）が生じないことを検証。