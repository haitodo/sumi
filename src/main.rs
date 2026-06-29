#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

pub mod app;
pub mod model;
pub mod platform;
pub mod service;
pub mod storage;
pub mod ui;

use crate::app::app::SumiApp;
use crate::storage::traits::Storage;
use crate::storage::memo::MemoStorage;
use crate::storage::config::{AppConfig, ConfigStorage};

fn main() -> eframe::Result<()> {
    // 1. APPDATA 配下に SumiMemo フォルダを定義
    let project_dirs = directories::ProjectDirs::from("com", "SumiMemo", "SumiMemo")
        .expect("アプリケーションデータディレクトリの特定に失敗しました。");
    let data_dir = project_dirs.data_dir();
    
    // データディレクトリの作成
    if !data_dir.exists() {
        if let Err(e) = std::fs::create_dir_all(data_dir) {
            eprintln!("AppDataディレクトリの作成に失敗しました: {}", e);
        }
    }
    
    let memo_path = data_dir.join("memo.txt");
    let config_path = data_dir.join("config.json");
    
    // 2. ストレージの読み込みと準備
    let config_storage = ConfigStorage::new(config_path);
    let memo_storage = MemoStorage::new(memo_path);
    
    let config = config_storage.load().unwrap_or_else(|e| {
        eprintln!("設定ファイルの読み込みに失敗しました。デフォルト設定を使用します: {}", e);
        AppConfig::default()
    });
    
    let initial_text = memo_storage.load().unwrap_or_else(|e| {
        eprintln!("メモファイルの読み込みに失敗しました。空のメモを使用します: {}", e);
        String::new()
    });
    
    // 3. ウィンドウ起動オプションの設定
    let mut options = eframe::NativeOptions::default();
    
    // glow（OpenGL）レンダラーを強制し、動作効率とフットプリントの最小化を図る
    options.renderer = eframe::Renderer::Glow;
    
    let mut viewport = egui::ViewportBuilder::default()
        .with_title("Sumi Memo")
        .with_inner_size(egui::vec2(config.window.width as f32, config.window.height as f32))
        .with_position(egui::pos2(config.window.x as f32, config.window.y as f32));
        
    if config.preferences.always_on_top {
        viewport = viewport.with_always_on_top();
    }
    
    options.viewport = viewport;
        
    // 4. アプリケーションの実行開始
    eframe::run_native(
        "Sumi Memo",
        options,
        Box::new(move |cc| {
            Ok(Box::new(SumiApp::new(
                cc,
                config,
                memo_storage,
                config_storage,
                initial_text,
            )) as Box<dyn eframe::App>)
        }),
    )
}
