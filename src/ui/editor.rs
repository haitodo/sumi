use egui::Ui;
use crate::model::state::AppState;
use crate::ui::state::EditorUiState;

pub fn draw_editor(
    ui: &mut Ui,
    app_state: &mut AppState,
    ui_state: &mut EditorUiState,
    should_request_focus: bool,
) -> egui::Response {
    // 画面全体を占有し、枠線や背景を持たないプレーンなテキストエリアを定義
    let editor = egui::TextEdit::multiline(&mut app_state.text)
        .frame(egui::Frame::new()) // egui 0.35 互換の枠線なしフレーム設定
        .desired_width(f32::INFINITY);
        
    let response = ui.add_sized(ui.available_size(), editor);

    // 初回起動時、または他のアプリからAlt+Tab等でフォーカスが戻った際にエディタにフォーカスを復旧
    if ui_state.first_frame || should_request_focus {
        response.request_focus();
        ui_state.first_frame = false;
    }

    response
}
