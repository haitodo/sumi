pub struct AppState {
    /// テキスト本体
    pub text: String,
    /// 未保存の変更が存在するかを示すフラグ
    pub dirty: bool,
    /// IMEが現在入力中・変換中（未確定）であるかを示すフラグ
    pub ime_composing: bool,
}

impl AppState {
    pub fn new(initial_text: String) -> Self {
        // 1万字（日本語で約30KB相当）を想定し、初期生成時に予め十分な
        // キャパシティを確保。これによりタイピング中の realloc と memcpy を完全に排除。
        let mut text = String::with_capacity(16384);
        text.push_str(&initial_text);
        
        Self {
            text,
            dirty: false,
            ime_composing: false,
        }
    }
}
