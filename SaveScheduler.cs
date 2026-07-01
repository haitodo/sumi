using Microsoft.UI.Dispatching;
using System;
using System.Threading.Tasks;

namespace sumi
{
    /// <summary>
    /// DispatcherQueueTimer を使用し、アロケーションを抑えながらキー入力の遅延保存（デバウンス）を制御するクラスです。
    /// </summary>
    public class SaveScheduler : IDisposable
    {
        private readonly DispatcherQueueTimer _timer;
        private readonly Func<Task> _onSaveTriggered;
        private readonly object _lock = new object();
        private bool _isDisposed = false;

        public SaveScheduler(DispatcherQueue queue, Func<Task> onSaveTriggered)
        {
            _onSaveTriggered = onSaveTriggered;

            // DispatcherQueueTimer を再利用することで、アロケーションを完全に抑制します
            _timer = queue.CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(2000);
            _timer.Tick += Timer_Tick;
        }

        public void Schedule()
        {
            lock (_lock)
            {
                if (_isDisposed) return;

                // タイマーを再起動（デバウンスをリセット）
                _timer.Stop();
                _timer.Start();
            }
        }

        private async void Timer_Tick(DispatcherQueueTimer sender, object args)
        {
            _timer.Stop();
            try
            {
                await _onSaveTriggered();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SaveScheduler Save Error] {ex.Message}");
            }
        }

        public void Cancel()
        {
            lock (_lock)
            {
                _timer.Stop();
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_isDisposed) return;
                _isDisposed = true;
                _timer.Stop();
            }
        }
    }
}