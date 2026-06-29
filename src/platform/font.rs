/// Windowsのシステムフォントディレクトリから日本語フォントをクエリして解決します。
pub fn load_system_font(ctx: &egui::Context) {
    // Windowsのシステムフォルダ（通常 C:\Windows）を取得
    let windir = std::env::var("SystemRoot")
        .or_else(|_| std::env::var("windir"))
        .unwrap_or_else(|_| "C:\\Windows".to_string());
    
    let font_dir = std::path::Path::new(&windir).join("Fonts");

    let mut fonts = egui::FontDefinitions::default();

    // 1. プロポーショナルフォントの読み込み (Yu Gothic UI / Meiryo / MS PGothic などの日本語フォント)
    let prop_candidates = [
        "YuGothM.ttc",
        "YuGothR.ttc",
        "meiryo.ttc",
        "msgothic.ttc", // フォールバックとしてMS Pゴシック
    ];
    let mut prop_loaded = false;
    for filename in &prop_candidates {
        let path = font_dir.join(filename);
        if path.exists() {
            if let Ok(bytes) = std::fs::read(&path) {
                let mut font_data = egui::FontData::from_owned(bytes);
                if *filename == "msgothic.ttc" {
                    // msgothic.ttc のインデックス 1 は通常 ＭＳ Ｐゴシック (プロポーショナル)
                    font_data.index = 1;
                }
                fonts.font_data.insert(
                    "system_japanese_prop".to_owned(),
                    std::sync::Arc::new(font_data),
                );
                fonts
                    .families
                    .entry(egui::FontFamily::Proportional)
                    .or_default()
                    .insert(0, "system_japanese_prop".to_owned());
                prop_loaded = true;
                break;
            }
        }
    }

    // 2. 等幅フォントの読み込み (ＭＳ ゴシック)
    let mono_candidates = [
        "msgothic.ttc",
    ];
    let mut mono_loaded = false;
    for filename in &mono_candidates {
        let path = font_dir.join(filename);
        if path.exists() {
            if let Ok(bytes) = std::fs::read(&path) {
                let mut font_data = egui::FontData::from_owned(bytes);
                // msgothic.ttc のインデックス 0 は ＭＳ ゴシック (等幅)
                font_data.index = 0;
                fonts.font_data.insert(
                    "system_japanese_mono".to_owned(),
                    std::sync::Arc::new(font_data),
                );
                fonts
                    .families
                    .entry(egui::FontFamily::Monospace)
                    .or_default()
                    .insert(0, "system_japanese_mono".to_owned());
                mono_loaded = true;
                break;
            }
        }
    }

    // どちらか一方しかロードできなかった場合のフォールバック処理
    if prop_loaded && !mono_loaded {
        fonts
            .families
            .entry(egui::FontFamily::Monospace)
            .or_default()
            .insert(0, "system_japanese_prop".to_owned());
    } else if mono_loaded && !prop_loaded {
        fonts
            .families
            .entry(egui::FontFamily::Proportional)
            .or_default()
            .insert(0, "system_japanese_mono".to_owned());
    }

    if prop_loaded || mono_loaded {
        ctx.set_fonts(fonts);
    }
}
