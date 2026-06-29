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
                // 既にインスタンスが存在する場合は即座に終了
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
