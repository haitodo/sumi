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
    // フォントファミリーに Proportional を指定し、サイズを 15.0 に設定することで、
    // Windowsシステムフォント(Yu Gothic UIやMeiryo等)に完全対応し、カーソルの見た目のズレを防ぎます。
    let editor = egui::TextEdit::multiline(&mut app_state.text)
        .frame(egui::Frame::new()) // 枠線や背景色を完全に非表示にする
        .desired_width(f32::INFINITY)
        .font(egui::FontId::new(15.0, egui::FontFamily::Proportional));
        
    let response = ui.add_sized(ui.available_size(), editor);

    // 初回起動時、または他のアプリからAlt+Tab等でフォーカスが戻った際にエディタにフォーカスを復旧
    if ui_state.first_frame || should_request_focus {
        response.request_focus();
        ui_state.first_frame = false;
    }

    response
}
