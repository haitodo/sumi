use std::time::{Duration, Instant};

pub struct SaveScheduler {
    pub debounce_duration: Duration,
    pub last_edit: Option<Instant>,
}

impl SaveScheduler {
    pub fn new(debounce_millis: u64) -> Self {
        Self {
            debounce_duration: Duration::from_millis(debounce_millis),
            last_edit: None,
        }
    }

    /// タイピング等による時間更新
    pub fn trigger_edit(&mut self) {
        self.last_edit = Some(Instant::now());
    }

    /// 保存を実行すべきデッドライン（時間）に達したかを判定
    pub fn should_save(&self, ime_composing: bool) -> bool {
        if ime_composing {
            return false; // IME変換中は絶対に保存を保留する
        }
        if let Some(last_edit) = self.last_edit {
            last_edit.elapsed() >= self.debounce_duration
        } else {
            false
        }
    }

    /// 保存が完了したことを通知しタイマーをクリア
    pub fn on_saved(&mut self) {
        self.last_edit = None;
    }
}
