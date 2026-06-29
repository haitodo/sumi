use std::path::PathBuf;
use crate::storage::traits::Storage;
use serde::{Deserialize, Serialize};

#[derive(Serialize, Deserialize, Clone, Debug)]
pub struct AppConfig {
    pub window: WindowState,
    pub preferences: Preferences,
}

#[derive(Serialize, Deserialize, Clone, Debug)]
pub struct WindowState {
    pub x: i32,
    pub y: i32,
    pub width: u32,
    pub height: u32,
    pub scale_factor: f64,
}

#[derive(Serialize, Deserialize, Clone, Debug)]
pub struct Preferences {
    pub theme: String,
    pub opacity: f32,
    pub always_on_top: bool,
}

impl Default for AppConfig {
    fn default() -> Self {
        Self {
            window: WindowState {
                x: 100,
                y: 100,
                width: 320,
                height: 400,
                scale_factor: 1.0,
            },
            preferences: Preferences {
                theme: "Dark".to_string(),
                opacity: 1.0,
                always_on_top: true,
            },
        }
    }
}

pub struct ConfigStorage {
    path: PathBuf,
}

impl ConfigStorage {
    pub fn new(path: PathBuf) -> Self {
        Self { path }
    }
}

impl Storage<AppConfig> for ConfigStorage {
    fn load(&self) -> Result<AppConfig, std::io::Error> {
        if !self.path.exists() {
            return Ok(AppConfig::default());
        }
        let content = std::fs::read_to_string(&self.path)?;
        let config: AppConfig = serde_json::from_str(&content)
            .map_err(|e| std::io::Error::new(std::io::ErrorKind::InvalidData, e))?;
        Ok(config)
    }

    fn save(&self, value: &AppConfig) -> Result<(), std::io::Error> {
        // 親ディレクトリの作成
        if let Some(parent) = self.path.parent() {
            std::fs::create_dir_all(parent)?;
        }

        // シリアライズ
        let serialized = serde_json::to_string_pretty(value)
            .map_err(|e| std::io::Error::new(std::io::ErrorKind::InvalidData, e))?;

        // 一時ファイルへの書き出し
        let temp_path = self.path.with_extension("tmp");
        std::fs::write(&temp_path, serialized)?;

        if !self.path.exists() {
            std::fs::rename(&temp_path, &self.path)?;
        } else {
            // ReplaceFileWを用いたアトミック置換
            use std::os::windows::ffi::OsStrExt;
            
            let temp_wide: Vec<u16> = temp_path.as_os_str().encode_wide().chain(std::iter::once(0)).collect();
            let target_wide: Vec<u16> = self.path.as_os_str().encode_wide().chain(std::iter::once(0)).collect();

            unsafe {
                let success = windows_sys::Win32::Storage::FileSystem::ReplaceFileW(
                    target_wide.as_ptr(),
                    temp_wide.as_ptr(),
                    std::ptr::null(),
                    0,
                    std::ptr::null_mut(),
                    std::ptr::null_mut(),
                );

                if success == 0 {
                    let err = std::io::Error::last_os_error();
                    if let Err(_) = std::fs::rename(&temp_path, &self.path) {
                        return Err(err);
                    }
                }
            }
        }
        Ok(())
    }
}
