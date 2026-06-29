using Microsoft.UI.Dispatching;
using System;
using System.Threading.Tasks;

namespace sumi
{
    /// <summary>
    /// DispatcherQueueTimer を使用し、アロケーションなしでキー入力の遅延保存（デバウンス）を制御するクラスです。
    /// </summary>
    public class SaveScheduler : IDisposable
    {
        private readonly DispatcherQueueTimer _timer;
        private readonly Func<Task> _onSaveTriggered;

        /// <summary>
        /// 単一の DispatcherQueueTimer インスタンスを用いて SaveScheduler を初期化します。
        /// </summary>
        public SaveScheduler(DispatcherQueue queue, Func<Task> onSaveTriggered)
        {
            _onSaveTriggered = onSaveTriggered;
            _timer = queue.CreateTimer();
            _timer.Interval = TimeSpan.FromSeconds(2); // 遅延時間は2秒に固定
            _timer.Tick += Timer_Tick;
        }

        private async void Timer_Tick(DispatcherQueueTimer sender, object args)
        {
            _timer.Stop();
            await _onSaveTriggered();
        }

        /// <summary>
        /// 保存処理をスケジュールします。既にスケジュールされている場合はタイマーをリセットします。
        /// </summary>
        public void Schedule()
        {
            _timer.Stop();
            _timer.Start();
        }

        /// <summary>
        /// スケジュールされている保存処理をキャンセルします。
        /// </summary>
        public void Cancel()
        {
            _timer.Stop();
        }

        /// <summary>
        /// タイマーを停止し、イベント購読を解除してリソースを解放します。
        /// </summary>
        public void Dispose()
        {
            _timer.Stop();
            _timer.Tick -= Timer_Tick;
        }
    }
}
