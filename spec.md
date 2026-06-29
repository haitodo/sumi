# 「思考を中断させない」常駐型メモアプリケーション 設計書 (v1.0 確定版)

## 1. 開発理念

本アプリケーションは、単なるベンチマーク上の起動速度の速さではなく、**「ユーザーの思考を1ミリ秒も中断させず、保存やファイル管理の存在すら意識させない」**という最高の使い心地を提供することを究極の設計目標とします。

すべての技術選定（eframe + glowの採用、Stringによる全体上書き保存、非同期I/Oの非採用など）は、この一貫した哲学に基づいて決定されています。

---

## 2. アーキテクチャ意思決定記録 (ADR: Architecture Decision Record)

| 決定対象 (ADR) | 採用した技術・方針 | 意思決定の理由 |
| :--- | :--- | :--- |
| **GUI基盤** | `egui` + `eframe` | 保守・開発効率と、ユーザーが体感できる高水準な応答性を最もバランスよく両立できるため。 |
| **描画システム** | `glow` (OpenGL) | 起動速度とメモリフットプリント、およびコンパイル後のバイナリサイズ低減を重視。 |
| **永続化フォーマット** | プレーンテキスト（UTF-8） | メモ本体の可読性、破損時の手動復旧の容易さ、およびシリアライズ不要によるロードの最大速度化。 |
| **設定保存形式** | JSON (`serde_json`) | 将来的なテーマ、不透明度、ホットキー設定などの拡張に対する保守性と下位互換性を最優先。 |
| **データの格納構造** | Capacity付き `String` | 1万文字（約30KB）のバッファとしてはヒープ再確保を排除した平坦な `String` が最もシンプルかつ高速。 |
| **保存方式** | 全体同期書き込み | 30KB程度であれば差分保存や別スレッド非同期I/Oはコードを複雑化させ、スタック破損のバグを増やすだけでメリットがないため。 |
| **堅牢性確保** | Windows アトミック置換 | 保存中の停電や不意のフリーズでも元のデータを確実に保全するため。 |

---

## 3. ディレクトリ・モジュール構成 (Clean Architecture)

ドメインロジック、永続化、ウィンドウ管理、UIコントロールの各責務をフォルダー構成レベルで厳格に分離し、長期にわたり変更に強い構造を維持します。

```
src/
├── main.rs                 # エントリポイント（初期化・起動）
├── app/
│   ├── mod.rs
│   └── app.rs              # Appオーケストレータ（イベント連携・ライフサイクル）
├── model/
│   ├── mod.rs
│   └── state.rs            # AppState（ドメインモデル：テキストバッファ、 dirtyフラグ）
├── storage/
│   ├── mod.rs
│   ├── traits.rs           # 抽象 Storage<T> トレイト定義
│   ├── memo.rs             # プレーンテキストアトミック保存の実装
│   └── config.rs           # JSON形式の設定ファイル保存の実装
├── ui/
│   ├── mod.rs
│   ├── editor.rs           # テキストエリア描画
│   └── state.rs            # EditorUiState（UI専用状態：初回フォーカス判定等）
├── service/
│   ├── mod.rs
│   └── scheduler.rs        # SaveScheduler（デバウンス制御・書き込み遅延時間管理）
└── platform/
    ├── mod.rs
    ├── window.rs           # マルチモニター領域判定・座標クランプ
    └── font.rs             # システムフォント解決
```

---

## 4. 内部設計

### 4.1 ドメインモデル (model/state.rs)
UIやOSの設定から完全に隔離された、純粋なデータ構造のみを保持します。
```rust
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
```

### 4.2 永続化設計 (storage/traits.rs)
ジェネリクスを用い、アプリが扱う様々な設定やデータを画一的にロード／セーブできるようにトレイト化します。

```rust
pub trait Storage<T> {
    /// データの読み込み
    fn load(&self) -> Result<T, std::io::Error>;
    
    /// データの保存
    fn save(&self, value: &T) -> Result<(), std::io::Error>;
}
```

#### Windows環境における完全なるアトミック置換実装方針
* 保存中のクラッシュや電源喪失時、0バイトデータになる破損を防ぐため、ファイルを一時ファイル（`.tmp`）に書き込みます。
* その後、Windows環境における最も堅牢なファイル置換である **Win32 APIの `ReplaceFileW` 相当の挙動**（または `std::fs::rename`、必要に応じて `windows` クレート経由の呼び出し）を使用し、元のファイルのセキュリティ記述子（ACL）、作成日付などのメタデータを完全に維持したまま安全に置き換えます。

### 4.3 構成情報およびPreferences (storage/config.rs)
設定ファイルは階層構造化されたJSONとしてパースされ、不透明度やテーマなど今後のニーズに柔軟に対応します。

```rust
use serde::{Deserialize, Serialize};

#[derive(Serialize, Deserialize, Clone)]
pub struct AppConfig {
    pub window: WindowState,
    pub preferences: Preferences,
}

#[derive(Serialize, Deserialize, Clone)]
pub struct WindowState {
    pub x: i32,
    pub y: i32,
    pub width: u32,
    pub height: u32,
    pub scale_factor: f64,
}

#[derive(Serialize, Deserialize, Clone)]
pub struct Preferences {
    pub theme: String,
    pub opacity: f32,
    pub always_on_top: bool,
}
```

### 4.4 自動保存スケジューラー (service/scheduler.rs)
アプリケーションの状態管理から「いつ保存すべきか」というスケジューリングロジックを分離します。

```rust
use std::time::{Duration, Instant};

pub struct SaveScheduler {
    pub debounce_duration: Duration,
    pub last_edit: Option<Instant>,
}

impl SaveScheduler {
    pub fn new(debounce_millis: u64) -> Self {
        Self {
            debounce_duration: Duration::from_millis(debounce_millis),
            last_edit: None,
        }
    }

    /// タイピング等による時間更新
    pub fn trigger_edit(&mut self) {
        self.last_edit = Some(Instant::now());
    }

    /// 保存を実行すべきデッドライン（時間）に達したかを判定
    pub fn should_save(&self, ime_composing: bool) -> bool {
        if ime_composing {
            return false; // IME変換中は絶対に保存を保留する
        }
        if let Some(last_edit) = self.last_edit {
            last_edit.elapsed() >= self.debounce_duration
        } else {
            false
        }
    }

    /// 保存が完了したことを通知しタイマーをクリア
    pub fn on_saved(&mut self) {
        self.last_edit = None;
    }
}
```

### 4.5 UI依存状態の完全分離 (ui/state.rs)
```rust
pub struct EditorUiState {
    /// 初回フレームを正確に判定してエディタにフォーカスを与えるフラグ
    pub first_frame: bool,
}

impl Default for EditorUiState {
    fn default() -> Self {
        Self { first_frame: true }
    }
}
```

---

## 5. ウィンドウ制御とイベントハンドリングの洗練

### 5.1 ウィンドウリサイズ・移動時のConfig即時保存
ユーザーがウィンドウを閉じる（正常終了する）タイミング以外にも、**「ウィンドウの移動を終えた瞬間」「サイズ変更を完了した瞬間（Windowsメッセージ `WM_EXITSIZEMOVE` に対応するwinitイベント等）」**に、現在のサイズと配置情報を `AppConfig` を通じて即時保存します。これにより、突然のOSクラッシュ時でも、次回起動時の表示位置が期待通りに復元されます。

### 5.2 フォント検索の優先解決（フォールバック優先順位）
Windows環境に特化し、英語のみのフォントファミリーによる文字化けや不自然な描画を防ぐため、以下の順序でシステム内の日本語フォントをクエリして解決します。

1. **`Yu Gothic UI`**: Windows 10/11 のUIで標準とされる最も可読性が高い等幅ベースのデザイン。
2. **`Meiryo` (メイリオ)**: クリアで柔らかな定番日本語UIフォント。
3. **`Yu Gothic` (遊ゴシック)**: 標準の美しいレンダリング。
4. **`MS Gothic` (ＭＳ ゴシック)**: 最低限のセーフティ用等幅フォールバック。

---

## 6. 非機能要件・制約事項

### 非機能要件
* **保存保証（耐久性）**: アトミックな置換により、万一の書き込み途中の中断であっても元のファイルを100%保護します。
* **CRLF / LF 互換**: Windows標準エディタなど外部ツールでメモファイル（`memo.txt`）が開かれた際に混入する `\r\n` (CRLF) を、読み込み時に内部的に `\n` (LF) へ正規化します。
* **アクセシビリティ**: `egui` が標準で提供するスクリーンリーダー等の支援技術のフックに対応します。

### 制約事項
* **動作環境**: Windows 10 / 11 専用
* **実行環境**: OpenGL（OpenGL ES 3.0 or GL 3.3）動作ドライバが必要
* **配布パッケージ**: 単一バイナリによるインストーラ不要の配布（管理者権限不要）

---

## 7. テストマトリクス（検証計画）

| テストID | 検証カテゴリ | シナリオ・手順 | 期待される動作 |
| :--- | :--- | :--- | :--- |
| **TC-001** | **起動＆境界値** | 初回起動（ファイル未存在） | 空白かつ16,384バイト確保されたバッファで正常起動すること。 |
| **TC-002** | **自動保存タイミング** | 日本語IMEで文字を入力・変換している最中に500msが経過 | 入力中・変換中はディスク保存が一切発生せず、**Enterキーで確定し、500msが経過した瞬間**にアトミック保存されること。 |
| **TC-003** | **フォーカス再復帰** | 他アプリに切り替えた後、Alt+Tab等で再度メモ帳を選択 | `winit` の `WindowEvent::Focused(true)` イベントをトリガーに、UI上のエディタへのフォーカス（キャレット表示）が即座に復旧すること。 |
| **TC-004** | **アトミック検証** | 大量の文字入力中にPCの電源を強制的に切断する（検証環境にてシミュレート） | 元のデータが完全に維持されているか、新データが書き換わっているかのどちらかであり、空データに破損しないこと。 |
| **TC-005** | **マルチモニター** | 外部モニター側でサイズ変更・移動を行い、外部モニターを外して再起動 | `monitor.position()` と `monitor.size()` から得た表示バウンディングボックス内に無い位置情報を検知し、プライマリの安全な中央位置にクランプ（再計算）されて起動すること。 |

---

## 8. ビルド最適化 (Cargo.toml リリース設定)

リリースビルド時の最適化設定。オーバーフローチェックを無効にし、デバッグメタデータも完全にストリップすることで、限界までフットプリントを削ぎ落とします。

```toml
[profile.release]
opt-level = 3
codegen-units = 1
lto = true
panic = "abort"
strip = true
incremental = false
overflow-checks = false  # リリースビルド用の最適化
debug = false            # デバッグ情報を完全排除
```