use winit::window::Window;
use crate::storage::config::WindowState;

/// ウィンドウの位置が現在利用可能なモニターのいずれかに収まっているか検証し、
/// 完全に画面外にある場合はプライマリモニターの中央にクランプ（再計算）します。
pub fn clamp_window_to_screens(window_state: &mut WindowState, window: &Window) {
    let monitors = window.available_monitors();
    let mut is_visible = false;

    // 保存されているサイズが極端に小さい、または大きすぎる場合のセーフティ
    if window_state.width < 100 {
        window_state.width = 320;
    }
    if window_state.height < 100 {
        window_state.height = 400;
    }

    // 各モニターの表示領域（論理座標）を取得して重なり判定を行う
    for monitor in monitors {
        let pos = monitor.position();
        let size = monitor.size();
        let sf = monitor.scale_factor();

        // モニターの物理ピクセル座標を論理ポイント座標に変換
        let mon_left = pos.x as f64 / sf;
        let mon_top = pos.y as f64 / sf;
        let mon_right = mon_left + (size.width as f64 / sf);
        let mon_bottom = mon_top + (size.height as f64 / sf);

        // 保存されているウィンドウ位置（論理ポイント）
        let win_left = window_state.x as f64;
        let win_top = window_state.y as f64;
        let win_right = win_left + window_state.width as f64;
        let win_bottom = win_top + window_state.height as f64;

        // 一部でも交差しているかチェック
        let x_overlap = win_left < mon_right && win_right > mon_left;
        let y_overlap = win_top < mon_bottom && win_bottom > mon_top;

        if x_overlap && y_overlap {
            is_visible = true;
            break;
        }
    }

    // どのモニター上にも存在しない場合は、プライマリモニターの安全な中央へ座標をクランプする
    if !is_visible {
        if let Some(primary) = window.primary_monitor() {
            let pos = primary.position();
            let size = primary.size();
            let sf = primary.scale_factor();

            let mon_left = pos.x as f64 / sf;
            let mon_top = pos.y as f64 / sf;
            let mon_width = size.width as f64 / sf;
            let mon_height = size.height as f64 / sf;

            window_state.x = (mon_left + (mon_width - window_state.width as f64) / 2.0) as i32;
            window_state.y = (mon_top + (mon_height - window_state.height as f64) / 2.0) as i32;
        } else {
            // プライマリモニターが特定できない場合のセーフティ座標
            window_state.x = 100;
            window_state.y = 100;
        }
    }
}
