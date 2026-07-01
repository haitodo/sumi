using Microsoft.UI.Dispatching;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace sumi
{
    /// <summary>
    /// Task.Delay と CancellationTokenSource を使用し、キー入力の遅延保存（デバウンス）を制御するクラスです。
    /// </summary>
    public class SaveScheduler : IDisposable
    {
        private readonly DispatcherQueue _queue;
        private readonly Func<Task> _onSaveTriggered;
        private CancellationTokenSource? _cts;
        private readonly object _lock = new object();
        private bool _isDisposed = false;

        /// <summary>
        /// SaveScheduler を初期化します。
        /// </summary>
        public SaveScheduler(DispatcherQueue queue, Func<Task> onSaveTriggered)
        {
            _queue = queue;
            _onSaveTriggered = onSaveTriggered;
        }

        /// <summary>
        /// 保存処理をスケジュールします。既にスケジュールされている場合はタイマーをリセットします。
        /// </summary>
        public void Schedule()
        {
            lock (_lock)
            {
                if (_isDisposed) return;

                // 既存のスケジュールをキャンセル
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = new CancellationTokenSource();

                var token = _cts.Token;

                // 遅延時間は2秒に固定
                Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(2000, token);

                        if (token.IsCancellationRequested) return;

                        // 保存処理の発火はUIスレッドで行う（GetTextがUIスレッドで動く必要があるため）
                        _queue.TryEnqueue(async () =>
                        {
                            try
                            {
                                if (!token.IsCancellationRequested)
                                {
                                    await _onSaveTriggered();
                                }
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[SaveScheduler Save Error] {ex.Message}");
                            }
                        });
                    }
                    catch (TaskCanceledException)
                    {
                        // キャンセル時は何もしない
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[SaveScheduler Delay Error] {ex.Message}");
                    }
                }, token);
            }
        }

        /// <summary>
        /// スケジュールされている保存処理をキャンセルします。
        /// </summary>
        public void Cancel()
        {
            lock (_lock)
            {
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;
            }
        }

        /// <summary>
        /// リソースを解放します。
        /// </summary>
        public void Dispose()
        {
            lock (_lock)
            {
                if (_isDisposed) return;
                _isDisposed = true;
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;
            }
        }
    }
}
