use std::fs::File;
use std::io::Write;
use std::path::Path;
use windows::core::PCWSTR;
use windows::Win32::Storage::FileSystem::{
    MoveFileExW, ReplaceFileW, MOVEFILE_REPLACE_EXISTING, REPLACE_FILE_FLAGS,
};

/// パスを Windows API 用のヌル終端 UTF-16 ベクタに変換する
fn to_u16_vec(path: &Path) -> Vec<u16> {
    use std::os::windows::ffi::OsStrExt;
    path.as_os_str()
        .encode_wide()
        .chain(std::iter::once(0))
        .collect()
}

/// テキストデータを一時ファイルを介してアトミックに書き込み、
/// 失敗時のフォールバックおよびバックアップ作成を含めて保存する。
pub fn save_text_atomically(
    content: &str,
    memo_path: &Path,
    tmp_path: &Path,
    bak_path: &Path,
) -> std::io::Result<()> {
    // 1. 一時ファイル "memo.tmp" を生成し、そこに新規データを書き出し
    {
        let mut file = File::create(tmp_path)?;
        file.write_all(content.as_bytes())?;
        file.flush()?;
        file.sync_all()?; // 物理ディスクへの書き込み完了を保証
    }

    // 2. 既存の "memo.txt" を "memo.bak" にコピー（バックアップの確保）
    if memo_path.exists() {
        std::fs::copy(memo_path, bak_path)?;
    }

    let memo_path_w = to_u16_vec(memo_path);
    let tmp_path_w = to_u16_vec(tmp_path);
    let bak_path_w = to_u16_vec(bak_path);

    // 3. Windows API "ReplaceFileW" を呼び出し、"memo.tmp" を "memo.txt" にアトミック置換
    let replaced = unsafe {
        ReplaceFileW(
            PCWSTR(memo_path_w.as_ptr()),
            PCWSTR(tmp_path_w.as_ptr()),
            PCWSTR(if bak_path.exists() {
                bak_path_w.as_ptr()
            } else {
                std::ptr::null()
            }),
            REPLACE_FILE_FLAGS(0),
            None,
            None,
        )
    };

    if replaced.is_ok() {
        return Ok(());
    }

    // 4. フォールバック1: "MoveFileExW" を試行
    let moved = unsafe {
        MoveFileExW(
            PCWSTR(tmp_path_w.as_ptr()),
            PCWSTR(memo_path_w.as_ptr()),
            MOVEFILE_REPLACE_EXISTING,
        )
    };

    if moved.is_ok() {
        return Ok(());
    }

    // 5. フォールバック2: Rust標準の "std::fs::rename" を試行
    std::fs::rename(tmp_path, memo_path)?;

    Ok(())
}
