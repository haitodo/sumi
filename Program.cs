using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System;
using System.Threading;

namespace sumi;

/// <summary>
/// アプリケーションのエントリポイントを定義するカスタムクラスです。
/// </summary>
public static class Program
{
    private static Mutex? _mutex;

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private static readonly IntPtr HWND_BROADCAST = (IntPtr)0xffff;

    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            bool createdNew;
            // 仕様書指定のミューテックス名を使用して多重起動を防止
            _mutex = new Mutex(true, @"Local\SimpleMemo_Instance_Mutex_9981", out createdNew);

            if (!createdNew)
            {
                // 既にインスタンスが存在する場合は、起動済みのインスタンスを表示させてから終了
                uint wmShowMe = RegisterWindowMessage("SUMI_SHOW_ME_MESSAGE");
                PostMessage(HWND_BROADCAST, wmShowMe, IntPtr.Zero, IntPtr.Zero);

                _mutex.Dispose();
                return;
            }

            try
            {
                WinRT.ComWrappersSupport.InitializeComWrappers();
                Application.Start((p) =>
                {
                    var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                    SynchronizationContext.SetSynchronizationContext(context);
                    new App();
                });
            }
            finally
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch (ObjectDisposedException) { }
                catch (ApplicationException) { }
                _mutex.Dispose();
            }
        }
        catch (Exception ex)
        {
            try
            {
                System.IO.File.WriteAllText("crash_report.txt", ex.ToString() + "\n" + ex.InnerException?.ToString());
            }
            catch { }
            throw;
        }
    }
}
