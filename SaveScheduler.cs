using Microsoft.UI.Dispatching;
using System;
using System.Threading.Tasks;

namespace sumi
{
    /// <summary>
    /// DispatcherQueueTimer を使った、再入防止付きのデバウンス保存スケジューラーです。
    /// </summary>
    public sealed class SaveScheduler : IDisposable
    {
        private readonly DispatcherQueueTimer _timer;
        private readonly object _lock = new();
        private Func<Task>? _onSaveTriggered;
        private bool _isDisposed;
        private bool _saveInProgress;
        private bool _saveRequested;

        public SaveScheduler(DispatcherQueue queue, Func<Task> onSaveTriggered)
        {
            _onSaveTriggered = onSaveTriggered ?? throw new ArgumentNullException(nameof(onSaveTriggered));
            _timer = queue.CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(2000);
            _timer.Tick += Timer_Tick;
        }

        public TimeSpan Interval
        {
            get => _timer.Interval;
            set => _timer.Interval = value;
        }

        public void Schedule()
        {
            lock (_lock)
            {
                if (_isDisposed) return;

                _saveRequested = true;
                if (!_saveInProgress)
                {
                    _timer.Stop();
                    _timer.Start();
                }
            }
        }

        private async void Timer_Tick(DispatcherQueueTimer sender, object args)
        {
            Func<Task>? save;
            lock (_lock)
            {
                if (_isDisposed || _saveInProgress) return;

                _timer.Stop();
                _saveRequested = false;
                _saveInProgress = true;
                save = _onSaveTriggered;
            }

            try
            {
                if (save != null)
                {
                    await save();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SaveScheduler Save Error] {ex.Message}");
            }
            finally
            {
                lock (_lock)
                {
                    _saveInProgress = false;
                    if (!_isDisposed && _saveRequested)
                    {
                        _timer.Stop();
                        _timer.Start();
                    }
                }
            }
        }

        public void Cancel()
        {
            lock (_lock)
            {
                _timer.Stop();
                _saveRequested = false;
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_isDisposed) return;

                _isDisposed = true;
                _saveRequested = false;
                _timer.Stop();
                _timer.Tick -= Timer_Tick;
                _onSaveTriggered = null;
            }
        }
    }
}
