using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using sumi.Interop;
using Windows.Graphics;
using WinRT;

namespace sumi
{
    public sealed partial class MainWindow : Window, IDisposable
    {
        private static readonly uint WM_SHOWME = NativeMethods.RegisterWindowMessage("SUMI_SHOW_ME_MESSAGE");

        private void InitializeTrayAndHotKeys()
        {
            _hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            
            _subclassProc = new NativeMethods.SUBCLASSPROC(WindowSubclassProc);
            NativeMethods.SetWindowSubclass(_hWnd, _subclassProc, (IntPtr)NativeMethods.SUBCLASS_ID, IntPtr.Zero);

            UpdateTrayIconAndHotKeys();
        }

        private void UpdateTrayIconAndHotKeys()
        {
            NativeMethods.UnregisterHotKey(_hWnd, NativeMethods.HOTKEY_ID_QUIT);
            NativeMethods.UnregisterHotKey(_hWnd, NativeMethods.HOTKEY_ID_LAUNCH);
            if (TryParseHotKey(MemoStorage.QuitHotKey, out uint quitMod, out uint quitVk))
            {
                NativeMethods.RegisterHotKey(_hWnd, NativeMethods.HOTKEY_ID_QUIT, quitMod, quitVk);
            }
            if (TryParseHotKey(MemoStorage.LaunchHotKey, out uint launchMod, out uint launchVk))
            {
                NativeMethods.RegisterHotKey(_hWnd, NativeMethods.HOTKEY_ID_LAUNCH, launchMod, launchVk);
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

        public void UnregisterMainWindowHotKeys()
        {
            NativeMethods.UnregisterHotKey(_hWnd, NativeMethods.HOTKEY_ID_QUIT);
            NativeMethods.UnregisterHotKey(_hWnd, NativeMethods.HOTKEY_ID_LAUNCH);
        }

        public void UpdateMainWindowHotKeys()
        {
            UpdateTrayIconAndHotKeys();
        }

        private void AddOrModifyTrayIcon()
        {
            var nid = new NativeMethods.NOTIFYICONDATA();
            nid.cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>();
            nid.hWnd = _hWnd;
            nid.uID = NativeMethods.TRAY_ICON_ID;
            nid.uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP;
            nid.uCallbackMessage = NativeMethods.WM_TRAYICON;

            IntPtr hIcon = IntPtr.Zero;

            // 1. 実行ファイルの埋め込みリソースからカスタムアイコンの読み込みを試行 (ID: 32512 は IDI_APPLICATION に対応)
            IntPtr hInst = NativeMethods.GetModuleHandle(null);
            if (hInst != IntPtr.Zero)
            {
                hIcon = NativeMethods.LoadIcon(hInst, (IntPtr)32512);
            }

            // 2. 埋め込みリソースからの読み込みが失敗した場合は、ファイルシステムから直接読み込む
            if (hIcon == IntPtr.Zero)
            {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "AppIcon.ico");
                if (File.Exists(iconPath))
                {
                    hIcon = NativeMethods.LoadImage(IntPtr.Zero, iconPath, NativeMethods.IMAGE_ICON, 16, 16, NativeMethods.LR_LOADFROMFILE);
                }
            }

            // 3. すべて失敗した場合は、システム既定のアプリケーションアイコンをフォールバックとして使用
            if (hIcon == IntPtr.Zero)
            {
                hIcon = NativeMethods.LoadIcon(IntPtr.Zero, (IntPtr)32512);
            }

            nid.hIcon = hIcon;
            nid.szTip = "Sumi Memo";

            if (_isTrayIconAdded)
            {
                NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref nid);
            }
            else
            {
                if (NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref nid))
                {
                    _isTrayIconAdded = true;
                }
            }
        }

        private void RemoveTrayIcon()
        {
            if (_isTrayIconAdded)
            {
                var nid = new NativeMethods.NOTIFYICONDATA();
                nid.cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>();
                nid.hWnd = _hWnd;
                nid.uID = NativeMethods.TRAY_ICON_ID;
                NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref nid);
                _isTrayIconAdded = false;
            }
        }

        private IntPtr WindowSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData)
        {
            if (uMsg == NativeMethods.WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (id == NativeMethods.HOTKEY_ID_QUIT)
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
                        SaveCurrentWindowPlacement();
                        // 現在表示しているメモIDを記録して設定に保存
                        MemoStorage.LastNoteId = MemoStorage.CurrentNoteId;
                        MemoStorage.SaveSettings();
                        _appWindow.Hide();
                    }
                    return IntPtr.Zero;
                }
                else if (id == NativeMethods.HOTKEY_ID_LAUNCH)
                {
                    ShowAndActivateWindow();
                    return IntPtr.Zero;
                }
            }
            else if (uMsg == NativeMethods.WM_TRAYICON)
            {
                uint mouseMsg = (uint)lParam.ToInt32();
                if (mouseMsg == NativeMethods.WM_LBUTTONUP || mouseMsg == NativeMethods.WM_LBUTTONDBLCLK)
                {
                    ShowAndActivateWindow();
                }
                else if (mouseMsg == NativeMethods.WM_RBUTTONUP)
                {
                    ShowTrayContextMenu();
                }
            }
            else if (uMsg == WM_SHOWME)
            {
                ShowAndActivateWindow();
                return IntPtr.Zero;
            }

            return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
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

            NativeMethods.SetForegroundWindow(_hWnd);

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
            NativeMethods.POINT pos;
            NativeMethods.GetCursorPos(out pos);

            IntPtr hMenu = NativeMethods.CreatePopupMenu();
            NativeMethods.AppendMenu(hMenu, NativeMethods.MF_STRING, (IntPtr)1, "Show");
            NativeMethods.AppendMenu(hMenu, NativeMethods.MF_STRING, (IntPtr)2, "Quit");

            NativeMethods.SetForegroundWindow(_hWnd);
            int selected = NativeMethods.TrackPopupMenu(hMenu, NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_LEFTALIGN, pos.X, pos.Y, 0, _hWnd, IntPtr.Zero);
            NativeMethods.PostMessage(_hWnd, 0, IntPtr.Zero, IntPtr.Zero);
            NativeMethods.DestroyMenu(hMenu);

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
                        fsModifiers |= NativeMethods.MOD_CONTROL;
                    }
                    else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
                    {
                        fsModifiers |= NativeMethods.MOD_ALT;
                    }
                    else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                    {
                        fsModifiers |= NativeMethods.MOD_SHIFT;
                    }
                    else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase) || part.Equals("Windows", StringComparison.OrdinalIgnoreCase))
                    {
                        fsModifiers |= NativeMethods.MOD_WIN;
                    }
                    else
                    {
                        return false;
                    }
                }
            }

            return vk != 0;
        }
    }
}
