using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using sumi.Interop;

namespace sumi
{
    public static partial class MemoStorage
    {
        /// <summary>
        /// 完全にアロケーションフリーなウィンドウ座標保存 (スタック上で処理) を行います。
        /// </summary>
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

        /// <summary>
        /// ウィンドウ座標の復元およびマルチモニターを考慮したクランプ処理、DPI自動スケーリングの相殺処理を行います。
        /// </summary>
        public static bool LoadWindowPlacement(IntPtr hWnd, out int x, out int y, out int width, out int height)
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

                                NativeMethods.RECT rect = new NativeMethods.RECT { Left = lx, Top = ly, Right = lx + lw, Bottom = ly + lh };
                                IntPtr hMonitor = NativeMethods.MonitorFromRect(ref rect, NativeMethods.MONITOR_DEFAULTTONULL);

                                if (hMonitor != IntPtr.Zero)
                                {
                                    // ワークエリア情報（タスクバーを除いた領域）の取得
                                    NativeMethods.MONITORINFO info = new NativeMethods.MONITORINFO();
                                    info.cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>();
                                    if (NativeMethods.GetMonitorInfo(hMonitor, ref info))
                                    {
                                        // 【重要】
                                        // ウィンドウが画面に表示される前の初期化段階では、GetDpiForWindow は標準の 96 DPI を返します。
                                        // その後、対象モニターに配置される際、OSによって自動的に「targetDpi / initialDpi」倍にリサイズされます。
                                        // 起動時の二重スケーリングを防ぐため、事前にこの拡大比率の逆数を掛けてサイズを補正します。
                                        uint initialDpi = 96;
                                        try
                                        {
                                            initialDpi = NativeMethods.GetDpiForWindow(hWnd);
                                        }
                                        catch (Exception)
                                        {
                                            initialDpi = 96;
                                        }

                                        uint targetDpi = 96;
                                        try
                                        {
                                            if (NativeMethods.GetDpiForMonitor(hMonitor, 0, out uint dpiX, out uint dpiY) == 0)
                                            {
                                                targetDpi = dpiX;
                                            }
                                        }
                                        catch (Exception)
                                        {
                                            targetDpi = 96;
                                        }

                                        if (initialDpi == 0) initialDpi = 96;
                                        if (targetDpi == 0) targetDpi = 96;

                                        // DPIの差分がある場合、OSによる自動スケーリングを事前に相殺する
                                        if (initialDpi != targetDpi)
                                        {
                                            lw = (int)Math.Round(lw * ((double)initialDpi / targetDpi));
                                            lh = (int)Math.Round(lh * ((double)initialDpi / targetDpi));
                                        }

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

        /// <summary>
        /// 設定をロードします。存在しない場合はデフォルト値を使用します。
        /// </summary>
        public static void LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var lines = File.ReadAllLines(SettingsPath, Utf8NoBom);
                    foreach (var line in lines)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var parts = line.Split('=', 2);
                        if (parts.Length == 2)
                        {
                            string key = parts[0].Trim();
                            string val = parts[1].Trim();
                            switch (key)
                            {
                                case "FontFamily":
                                    FontFamily = val;
                                    break;
                                case "FontWeight":
                                    FontWeight = val;
                                    break;
                                case "FontSize":
                                    if (double.TryParse(val, out double fs)) FontSize = fs;
                                    break;
                                case "LineSpacing":
                                    if (double.TryParse(val, out double ls)) LineSpacing = Math.Clamp(ls, 0.5, 4.0);
                                    break;
                                case "ParagraphSpacing":
                                    if (double.TryParse(val, out double ps)) ParagraphSpacing = ps;
                                    break;
                                case "Opacity":
                                    if (double.TryParse(val, out double op)) Opacity = op;
                                    break;
                                case "QuitHotKey":
                                    QuitHotKey = val;
                                    break;
                                case "LaunchHotKey":
                                    LaunchHotKey = val;
                                    break;
                                case "LastNoteId":
                                    LastNoteId = val;
                                    break;
                                case "IsSidebarPinned":
                                    if (bool.TryParse(val, out bool pinned)) IsSidebarPinned = pinned;
                                    break;
                                case "IsSidebarOpen":
                                    if (bool.TryParse(val, out bool open)) IsSidebarOpen = open;
                                    break;
                                case "SidebarWidth":
                                    if (double.TryParse(val, out double w)) SidebarWidth = Math.Clamp(w, 200, 600);
                                    break;
                                case "IsRightSidebarPinned":
                                    if (bool.TryParse(val, out bool rpinned)) IsRightSidebarPinned = rpinned;
                                    break;
                                case "IsRightSidebarOpen":
                                    if (bool.TryParse(val, out bool ropen)) IsRightSidebarOpen = ropen;
                                    break;
                                case "RightSidebarWidth":
                                    if (double.TryParse(val, out double rw)) RightSidebarWidth = Math.Clamp(rw, 200, 600);
                                    break;
                                case "LastSidebarView":
                                    LastSidebarView = val;
                                    break;
                                case "LastRightSidebarView":
                                    LastRightSidebarView = val;
                                    break;
                                case "LastSelectedTag":
                                    LastSelectedTag = val;
                                    break;
                                case "LastSelectedRightTag":
                                    LastSelectedRightTag = val;
                                    break;
                                case "RecentNotesCount":
                                    if (int.TryParse(val, out int rnc)) RecentNotesCount = Math.Max(0, rnc);
                                    break;
                                case "ShowDeleteButton":
                                    if (bool.TryParse(val, out bool sdb)) ShowDeleteButton = sdb;
                                    break;
                                case "AiApiKey":
                                    AiApiKey = val;
                                    break;
                                case "AiModelName":
                                    AiModelName = val;
                                    break;
                                case "AiTemperature":
                                    if (double.TryParse(val, out double temp)) AiTemperature = temp;
                                    break;
                                case "AiMaxTokens":
                                    if (int.TryParse(val, out int tokens)) AiMaxTokens = tokens;
                                    break;
                                case "AiSystemPrompt":
                                    AiSystemPrompt = val.Replace("\\n", "\n");
                                    break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LoadSettings Error] {ex.Message}");
            }
            LoadAiPrompts();
        }

        /// <summary>
        /// 設定をアトミックに保存します。
        /// </summary>
        public static void SaveSettings()
        {
            try
            {
                // CurrentNoteId が確定している場合は LastNoteId を常に最新に保つ
                // （タイマー経由の呼び出しでも確実に現在のメモIDが保存されるようにする）
                if (!string.IsNullOrEmpty(CurrentNoteId))
                {
                    LastNoteId = CurrentNoteId;
                }

                var sb = new StringBuilder();
                sb.AppendLine($"FontFamily={FontFamily}");
                sb.AppendLine($"FontWeight={FontWeight}");
                sb.AppendLine($"FontSize={FontSize}");
                sb.AppendLine($"LineSpacing={LineSpacing}");
                sb.AppendLine($"ParagraphSpacing={ParagraphSpacing}");
                sb.AppendLine($"Opacity={Opacity}");
                sb.AppendLine($"QuitHotKey={QuitHotKey}");
                sb.AppendLine($"LaunchHotKey={LaunchHotKey}");
                sb.AppendLine($"LastNoteId={LastNoteId}");
                sb.AppendLine($"IsSidebarPinned={IsSidebarPinned}");
                sb.AppendLine($"IsSidebarOpen={IsSidebarOpen}");
                sb.AppendLine($"SidebarWidth={SidebarWidth}");
                sb.AppendLine($"IsRightSidebarPinned={IsRightSidebarPinned}");
                sb.AppendLine($"IsRightSidebarOpen={IsRightSidebarOpen}");
                sb.AppendLine($"RightSidebarWidth={RightSidebarWidth}");
                sb.AppendLine($"LastSidebarView={LastSidebarView}");
                sb.AppendLine($"LastRightSidebarView={LastRightSidebarView}");
                sb.AppendLine($"LastSelectedTag={LastSelectedTag}");
                sb.AppendLine($"LastSelectedRightTag={LastSelectedRightTag}");
                sb.AppendLine($"RecentNotesCount={RecentNotesCount}");
                sb.AppendLine($"ShowDeleteButton={ShowDeleteButton}");
                sb.AppendLine($"AiApiKey={AiApiKey}");
                sb.AppendLine($"AiModelName={AiModelName}");
                sb.AppendLine($"AiTemperature={AiTemperature}");
                sb.AppendLine($"AiMaxTokens={AiMaxTokens}");
                sb.AppendLine($"AiSystemPrompt={AiSystemPrompt.Replace("\r", "").Replace("\n", "\\n")}");
                byte[] bytes = Utf8NoBom.GetBytes(sb.ToString());

                string tempPath = SettingsPath + ".tmp";
                using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: false))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush();
                    fs.Flush(true); // 物理フラッシュ
                }

                if (File.Exists(SettingsPath))
                {
                    File.Replace(tempPath, SettingsPath, null);
                    using (var fs = new FileStream(SettingsPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                    {
                        fs.Flush(true);
                    }
                }
                else
                {
                    File.Move(tempPath, SettingsPath);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SaveSettings Error] {ex.Message}");
            }
        }
    }
}
