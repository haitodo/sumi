use std::time::{Duration, Instant};
use crate::model::state::AppState;
use crate::ui::state::EditorUiState;
use crate::storage::traits::Storage;
use crate::storage::memo::MemoStorage;
use crate::storage::config::{AppConfig, ConfigStorage};
use crate::service::scheduler::SaveScheduler;

pub struct SumiApp {
    app_state: AppState,
    ui_state: EditorUiState,
    config: AppConfig,
    memo_storage: MemoStorage,
    config_storage: ConfigStorage,
    save_scheduler: SaveScheduler,
    
    // ウィンドウ状態の変更検知用
    last_window_pos: Option<egui::Pos2>,
    last_window_size: Option<egui::Vec2>,
    last_window_change: Option<Instant>,
    config_dirty: bool,
    
    // フォーカス状態の変更検知用
    last_focused: Option<bool>,
    should_request_focus: bool,
}

impl SumiApp {
    pub fn new(
        cc: &eframe::CreationContext<'_>,
        config: AppConfig,
        memo_storage: MemoStorage,
        config_storage: ConfigStorage,
        initial_text: String,
    ) -> Self {
        // 日本語システムフォントの自動解決と登録
        crate::platform::font::load_system_font(&cc.egui_ctx);
        
        // 常に最前面表示の設定を適用
        cc.egui_ctx.send_viewport_cmd(egui::ViewportCommand::WindowLevel(
            if config.preferences.always_on_top {
                egui::WindowLevel::AlwaysOnTop
            } else {
                egui::WindowLevel::Normal
            }
        ));
        
        let app_state = AppState::new(initial_text);
        
        Self {
            app_state,
            ui_state: EditorUiState::default(),
            config,
            memo_storage,
            config_storage,
            save_scheduler: SaveScheduler::new(500), // 500ms デバウンス遅延
            
            last_window_pos: None,
            last_window_size: None,
            last_window_change: None,
            config_dirty: false,
            
            last_focused: None,
            should_request_focus: false,
        }
    }
}

impl eframe::App for SumiApp {
    fn ui(&mut self, ui: &mut egui::Ui, frame: &mut eframe::Frame) {
        // ボローチェッカー回避のため、Contextをクローンして借用を切断する
        let ctx = ui.ctx().clone();

        // 1. 初回フレーム時のモニター境界判定とウィンドウ配置・サイズ復元
        if self.ui_state.first_frame {
            if let Some(window) = frame.winit_window() {
                crate::platform::window::clamp_window_to_screens(&mut self.config.window, &window);
                
                // クランプ後の安全なウィンドウ位置とサイズを適用
                ctx.send_viewport_cmd(egui::ViewportCommand::OuterPosition(egui::pos2(
                    self.config.window.x as f32,
                    self.config.window.y as f32,
                )));
                ctx.send_viewport_cmd(egui::ViewportCommand::InnerSize(egui::vec2(
                    self.config.window.width as f32,
                    self.config.window.height as f32,
                )));
                
                // クランプ調整後の設定を即時保存
                if let Err(e) = self.config_storage.save(&self.config) {
                    eprintln!("初期位置調整後の設定保存に失敗しました: {}", e);
                }
                
                // 起動時の初期移動検知を防ぐため、基準値をセット
                self.last_window_pos = Some(egui::pos2(self.config.window.x as f32, self.config.window.y as f32));
                self.last_window_size = Some(egui::vec2(self.config.window.width as f32, self.config.window.height as f32));
            }
        }

        // 2. IMEの入力・変換状態イベントを監視 (egui 0.35 構造体パターン対応)
        ctx.input(|i| {
            for event in &i.events {
                match event {
                    egui::Event::Ime(egui::ImeEvent::Preedit { text, .. }) => {
                        self.app_state.ime_composing = !text.is_empty();
                    }
                    egui::Event::Ime(egui::ImeEvent::Commit { .. }) => {
                        self.app_state.ime_composing = false;
                        self.app_state.dirty = true;
                        self.save_scheduler.trigger_edit();
                    }
                    _ => {}
                }
            }
        });
        
        // 3. ウィンドウのフォーカス変化・リサイズ・移動を監視
        let (viewport_focused, viewport_outer_rect, viewport_scale_factor) = ctx.input(|i| {
            let vp = i.viewport();
            (vp.focused, vp.outer_rect, vp.native_pixels_per_point)
        });

        // フォーカス復旧の検知
        if let Some(focused) = viewport_focused {
            if let Some(last) = self.last_focused {
                if !last && focused {
                    self.should_request_focus = true; // フォーカス喪失状態から復帰した瞬間
                }
            }
            self.last_focused = Some(focused);
        }
        
        // 位置・サイズ変更の検知
        if let (Some(outer_rect), Some(scale_factor)) = (viewport_outer_rect, viewport_scale_factor) {
            let current_pos = outer_rect.min;
            let current_size = outer_rect.size();
            
            let pos_changed = self.last_window_pos.map_or(true, |p| p != current_pos);
            let size_changed = self.last_window_size.map_or(true, |s| s != current_size);
            
            if pos_changed || size_changed {
                self.last_window_pos = Some(current_pos);
                self.last_window_size = Some(current_size);
                self.last_window_change = Some(Instant::now());
                self.config_dirty = true;
                
                // ウィンドウ設定情報を更新（論理ポイント）
                self.config.window.x = current_pos.x as i32;
                self.config.window.y = current_pos.y as i32;
                self.config.window.width = current_size.x as u32;
                self.config.window.height = current_size.y as u32;
                self.config.window.scale_factor = scale_factor as f64;
            }
        }
        
        // 4. ウィンドウサイズ変更・移動完了時の自動設定保存（500ms デバウンス）
        if self.config_dirty {
            if let Some(last_change) = self.last_window_change {
                if last_change.elapsed() >= Duration::from_millis(500) {
                    if let Err(e) = self.config_storage.save(&self.config) {
                        eprintln!("設定ファイルの自動保存に失敗しました: {}", e);
                    }
                    self.config_dirty = false;
                    self.last_window_change = None;
                }
            }
        }
        
        // 5. メモ自動保存スケジューリングの判定と実行
        if self.app_state.dirty && self.save_scheduler.should_save(self.app_state.ime_composing) {
            if let Err(e) = self.memo_storage.save(&self.app_state.text) {
                eprintln!("メモの自動保存に失敗しました: {}", e);
            }
            self.app_state.dirty = false;
            self.save_scheduler.on_saved();
        }
        
        // 6. テキストエディタUIの描画 (egui 0.35のshowを使用)
        egui::CentralPanel::default().show(ui, |ui| {
            let response = crate::ui::editor::draw_editor(
                ui,
                &mut self.app_state,
                &mut self.ui_state,
                self.should_request_focus,
            );
            
            if response.changed() {
                self.app_state.dirty = true;
                self.save_scheduler.trigger_edit();
            }
            
            // フォーカス復旧トリガーのリセット
            self.should_request_focus = false;
        });
        
        // デバウンスタイマーなどを正しくカウントするため、一定時間毎に再描画を要求
        ctx.request_repaint_after(Duration::from_millis(100));
    }
    
    fn on_exit(&mut self, _gl: Option<&eframe::glow::Context>) {
        // アプリケーション終了時の最終保存保証
        if self.app_state.dirty {
            if let Err(e) = self.memo_storage.save(&self.app_state.text) {
                eprintln!("終了時のメモ最終保存に失敗しました: {}", e);
            }
        }
        if self.config_dirty {
            if let Err(e) = self.config_storage.save(&self.config) {
                eprintln!("終了時の設定最終保存に失敗しました: {}", e);
            }
        }
    }
}
