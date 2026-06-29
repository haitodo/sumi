pub struct EditorUiState {
    /// 初回フレームを正確に判定してエディタにフォーカスを与えるフラグ
    pub first_frame: bool,
}

impl Default for EditorUiState {
    fn default() -> Self {
        Self { first_frame: true }
    }
}
