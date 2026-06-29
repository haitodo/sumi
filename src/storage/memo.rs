use std::path::PathBuf;
use crate::storage::traits::Storage;

pub struct MemoStorage {
    path: PathBuf,
}

impl MemoStorage {
    pub fn new(path: PathBuf) -> Self {
        Self { path }
    }
}

impl Storage<String> for MemoStorage {
    fn load(&self) -> Result<String, std::io::Error> {
        if !self.path.exists() {
            return Ok(String::new());
        }
        let content = std::fs::read_to_string(&self.path)?;
        // Windows標準エディタなどでの改行コード混入対策として、CRLFをLFに正規化する
        let normalized = content.replace("\r\n", "\n");
        Ok(normalized)
    }

    fn save(&self, value: &String) -> Result<(), std::io::Error> {
        // 親ディレクトリが存在しない場合は作成する
        if let Some(parent) = self.path.parent() {
            std::fs::create_dir_all(parent)?;
        }

        // 一時ファイルのパスを作成
        let temp_path = self.path.with_extension("tmp");
        std::fs::write(&temp_path, value)?;

        if !self.path.exists() {
            // 対象ファイルがまだ存在しない場合は通常のrenameを行う
            std::fs::rename(&temp_path, &self.path)?;
        } else {
            // 対象ファイルが既に存在する場合は、Win32 APIのReplaceFileWを使用してアトミックに置換する。
            // これにより、セキュリティ記述子（ACL）や作成日時などのメタデータが完全に維持される。
            use std::os::windows::ffi::OsStrExt;
            
            let temp_wide: Vec<u16> = temp_path.as_os_str().encode_wide().chain(std::iter::once(0)).collect();
            let target_wide: Vec<u16> = self.path.as_os_str().encode_wide().chain(std::iter::once(0)).collect();

            unsafe {
                let success = windows_sys::Win32::Storage::FileSystem::ReplaceFileW(
                    target_wide.as_ptr(),
                    temp_wide.as_ptr(),
                    std::ptr::null(), // バックアップファイルは不要のためnull
                    0,                // 置換フラグ
                    std::ptr::null_mut(),
                    std::ptr::null_mut(),
                );

                if success == 0 {
                    let err = std::io::Error::last_os_error();
                    // ReplaceFileWが失敗した場合のセーフティフォールバックとしてrenameを試みる
                    if let Err(_) = std::fs::rename(&temp_path, &self.path) {
                        return Err(err);
                    }
                }
            }
        }
        Ok(())
    }
}
