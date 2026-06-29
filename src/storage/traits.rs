pub trait Storage<T> {
    /// データの読み込み
    fn load(&self) -> Result<T, std::io::Error>;
    
    /// データの保存
    fn save(&self, value: &T) -> Result<(), std::io::Error>;
}
