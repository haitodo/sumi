/// Windowsのシステムフォントディレクトリから日本語フォントをクエリして解決します。
pub fn load_system_font(ctx: &egui::Context) {
    // Windowsのシステムフォルダ（通常 C:\Windows）を取得
    let windir = std::env::var("SystemRoot")
        .or_else(|_| std::env::var("windir"))
        .unwrap_or_else(|_| "C:\\Windows".to_string());
    
    let font_dir = std::path::Path::new(&windir).join("Fonts");

    // 優先度順にシステムフォントのファイル名を探索
    // 1. Yu Gothic UI (標準UI等幅ベース)
    // 2. Meiryo (メイリオ)
    // 3. Yu Gothic (標準遊ゴシック)
    // 4. MS Gothic (セーフティフォールバック等幅)
    let candidates = [
        "YuGothM.ttc",
        "YuGothR.ttc",
        "meiryo.ttc",
        "msgothic.ttc",
    ];

    let mut font_data = None;
    for filename in &candidates {
        let path = font_dir.join(filename);
        if path.exists() {
            if let Ok(bytes) = std::fs::read(&path) {
                font_data = Some((filename.to_string(), bytes));
                break;
            }
        }
    }

    if let Some((_name, bytes)) = font_data {
        let mut fonts = egui::FontDefinitions::default();
        
        // フォントデータを登録（egui 0.35互換としてArcでラップ）
        fonts.font_data.insert(
            "system_japanese_font".to_owned(),
            std::sync::Arc::new(egui::FontData::from_owned(bytes)),
        );

        // Proportional (プロポーショナル) と Monospace (等幅) ファミリーの最優先（インデックス0）に設定
        fonts
            .families
            .entry(egui::FontFamily::Proportional)
            .or_default()
            .insert(0, "system_japanese_font".to_owned());
            
        fonts
            .families
            .entry(egui::FontFamily::Monospace)
            .or_default()
            .insert(0, "system_japanese_font".to_owned());

        ctx.set_fonts(fonts);
    }
}
