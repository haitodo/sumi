use windows::Win32::Foundation::{BOOL, HWND, LPARAM};
use windows::Win32::System::Threading::GetCurrentThreadId;
use windows::Win32::UI::WindowsAndMessaging::{
    EnumThreadWindows, GetWindowLongPtrW, SetWindowLongPtrW, SetWindowPos, GWL_EXSTYLE,
    HWND_TOPMOST, SWP_NOMOVE, SWP_NOSIZE, SWP_SHOWWINDOW, WS_EX_TOPMOST,
};

/// 呼び出し元（メイン）スレッドに属するすべてのウィンドウハンドルを取得する
pub fn get_thread_hwnds() -> Vec<HWND> {
    let mut hwnds = Vec::new();
    let thread_id = unsafe { GetCurrentThreadId() };
    unsafe {
        let _ = EnumThreadWindows(
            thread_id,
            Some(enum_thread_window_callback),
            LPARAM(&mut hwnds as *mut Vec<HWND> as isize),
        );
    }
    hwnds
}

/// メインウィンドウの HWND を取得する（存在しない場合は None）
pub fn get_window_hwnd() -> Option<HWND> {
    get_thread_hwnds().into_iter().next()
}

/// EnumThreadWindows のコールバック関数
unsafe extern "system" fn enum_thread_window_callback(hwnd: HWND, lparam: LPARAM) -> BOOL {
    let hwnds = &mut *(lparam.0 as *mut Vec<HWND>);
    hwnds.push(hwnd);
    true.into()
}

/// 指定したウィンドウハンドルを常に最前面に表示するように固定する
pub fn ensure_always_on_top(hwnd: HWND) {
    unsafe {
        // 1. 拡張スタイルに WS_EX_TOPMOST を付与
        let current_style = GetWindowLongPtrW(hwnd, GWL_EXSTYLE) as u32;
        if (current_style & WS_EX_TOPMOST.0) == 0 {
            let _ = SetWindowLongPtrW(
                hwnd,
                GWL_EXSTYLE,
                (current_style | WS_EX_TOPMOST.0) as isize,
            );
        }
        // 2. 最前面として位置を確定
        let _ = SetWindowPos(
            hwnd,
            HWND_TOPMOST,
            0,
            0,
            0,
            0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW,
        );
    }
}
