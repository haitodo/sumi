using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using System;
using System.Linq;
using sumi.Interop;
using Windows.Graphics;

namespace sumi
{
    public sealed partial class SettingsWindow : Window
    {
        private bool _isInitializingSettings = false;
        private bool _isOwnerSet = false;

        public SettingsWindow()
        {
            this.InitializeComponent();
            ApplyAccessibilityNames();

            this.Activated += SettingsWindow_Activated;

            // 暗いテーマとカスタムタイトルバーの設定
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            int useDarkMode = 1;
            try
            {
                NativeMethods.DwmSetWindowAttribute(hWnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDarkMode, sizeof(int));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DwmSetWindowAttribute Error] {ex.Message}");
            }

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(TitleDragRegion);

            // ウィンドウサイズの初期設定
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            if (appWindow != null)
            {
                appWindow.Resize(new SizeInt32(680, 520));
            }

            // 設定のロード
            LoadSettings();

            // 初期ナビゲーション選択（Editor）
            UpdateNavSelection("Editor");
        }

        private void ApplyAccessibilityNames()
        {
            AutomationProperties.SetName(FontSizeDecreaseButton, "フォントサイズを小さくする");
            AutomationProperties.SetName(FontSizeIncreaseButton, "フォントサイズを大きくする");
            AutomationProperties.SetName(OpacityDecreaseButton, "不透明度を下げる");
            AutomationProperties.SetName(OpacityIncreaseButton, "不透明度を上げる");
            AutomationProperties.SetName(RecentNotesCountDecreaseButton, "最近のメモ表示数を減らす");
            AutomationProperties.SetName(RecentNotesCountIncreaseButton, "最近のメモ表示数を増やす");
            AutomationProperties.SetName(AlwaysOnTopToggle, "常に手前に表示");
            AutomationProperties.SetName(ShowDeleteButtonToggle, "メモ一覧に削除ボタンを表示");
        }

        private void SettingsWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            if (!_isOwnerSet)
            {
                var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                var mainHwnd = MainWindow.Instance != null ? WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance) : IntPtr.Zero;
                if (mainHwnd != IntPtr.Zero)
                {
                    // Win32 GWL_HWNDPARENT によるオーナーシップ設定（前面維持の基本）
                    NativeMethods.SetWindowOwner(hWnd, mainHwnd);
                }
                _isOwnerSet = true;
            }
        }

        private void LoadSettings()
        {
            _isInitializingSettings = true;
            try
            {
                // フォント
                string[] fontItems = new[] { "Noto Sans JP", "Yu Gothic UI", "Segoe UI", "Consolas", "Georgia" };
                int fontIndex = Array.IndexOf(fontItems, MemoStorage.FontFamily);
                if (fontIndex >= 0)
                {
                    FontComboBox.SelectedIndex = fontIndex;
                }

                // 文字の太さ
                string[] weightItems = new[] { "Light", "Normal", "Medium", "SemiBold", "Bold" };
                int weightIndex = Array.IndexOf(weightItems, MemoStorage.FontWeight);
                if (weightIndex >= 0)
                {
                    FontWeightComboBox.SelectedIndex = weightIndex;
                }

                // フォントサイズ
                FontSizeSlider.Value = MemoStorage.FontSize;
                FontSizeValueText.Text = MemoStorage.FontSize.ToString("0.0");

                // 行間
                double currentLS = MemoStorage.LineSpacing;
                string[] lsItems = new[] { "0.8", "0.85", "0.9", "0.95", "1.0" };
                int lsIndex = -1;
                for (int i = 0; i < lsItems.Length; i++)
                {
                    if (double.TryParse(lsItems[i], out double itemVal) && Math.Abs(itemVal - currentLS) < 0.01)
                    {
                        lsIndex = i;
                        break;
                    }
                }
                if (lsIndex >= 0)
                {
                    LineSpacingComboBox.SelectedIndex = lsIndex;
                }
                else
                {
                    LineSpacingComboBox.SelectedItem = null;
                    LineSpacingComboBox.Text = currentLS.ToString("0.##");
                }

                // 段落スペース
                double currentPS = MemoStorage.ParagraphSpacing;
                string[] psItems = new[] { "0", "2", "4", "6", "8", "10", "12" };
                int psIndex = -1;
                for (int i = 0; i < psItems.Length; i++)
                {
                    if (double.TryParse(psItems[i], out double itemVal) && Math.Abs(itemVal - currentPS) < 0.1)
                    {
                        psIndex = i;
                        break;
                    }
                }
                if (psIndex >= 0)
                {
                    ParagraphSpacingComboBox.SelectedIndex = psIndex;
                }
                else
                {
                    ParagraphSpacingComboBox.SelectedItem = null;
                    ParagraphSpacingComboBox.Text = ((int)currentPS).ToString();
                }

                // ウィンドウ
                OpacitySlider.Value = MemoStorage.Opacity;
                OpacityValueText.Text = $"{(int)MemoStorage.Opacity}%";
                AlwaysOnTopToggle.IsOn = MemoStorage.IsAlwaysOnTop;

                RecentNotesCountSlider.Value = MemoStorage.RecentNotesCount;
                RecentNotesCountValueText.Text = MemoStorage.RecentNotesCount.ToString();
                ShowDeleteButtonToggle.IsOn = MemoStorage.ShowDeleteButton;

                // システム
                QuitHotKeyButton.Content = string.IsNullOrEmpty(MemoStorage.QuitHotKey) ? "None" : MemoStorage.QuitHotKey;
                LaunchHotKeyButton.Content = string.IsNullOrEmpty(MemoStorage.LaunchHotKey) ? "None" : MemoStorage.LaunchHotKey;

                // AI
                AiApiKeyBox.Password = MemoStorage.AiApiKey;
                AiModelNameBox.Text = MemoStorage.AiModelName;
                AiTemperatureSlider.Value = MemoStorage.AiTemperature;
                AiTemperatureValueText.Text = MemoStorage.AiTemperature.ToString("0.0");
                AiMaxTokensBox.Text = MemoStorage.AiMaxTokens.ToString();
                AiSystemPromptBox.Text = MemoStorage.AiSystemPrompt;

                RefreshAiPromptsListUI();
            }
            finally
            {
                _isInitializingSettings = false;
            }
        }

        #region Navigation
        private void NavButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                string category = btn.Name.Replace("Nav", "").Replace("Button", "");
                UpdateNavSelection(category);
            }
        }

        private void UpdateNavSelection(string category)
        {
            // パネルの表示切り替え
            EditorSettingsPanel.Visibility = category == "Editor" ? Visibility.Visible : Visibility.Collapsed;
            WindowSettingsPanel.Visibility = category == "Window" ? Visibility.Visible : Visibility.Collapsed;
            SystemSettingsPanel.Visibility = category == "System" ? Visibility.Visible : Visibility.Collapsed;
            AiSettingsPanel.Visibility = category == "Ai" ? Visibility.Visible : Visibility.Collapsed;

            // ナビゲーションボタンとインジケータの更新
            HighlightNavButton(NavEditorButton, NavEditorIndicator, category == "Editor");
            HighlightNavButton(NavWindowButton, NavWindowIndicator, category == "Window");
            HighlightNavButton(NavSystemButton, NavSystemIndicator, category == "System");
            HighlightNavButton(NavAiButton, NavAiIndicator, category == "Ai");
        }

        private void HighlightNavButton(Button btn, Border indicator, bool isSelected)
        {
            if (isSelected)
            {
                btn.Background = new SolidColorBrush(ColorHelper.FromArgb(24, 255, 255, 255));
                indicator.Visibility = Visibility.Visible;
            }
            else
            {
                btn.Background = new SolidColorBrush(Colors.Transparent);
                indicator.Visibility = Visibility.Collapsed;
            }
        }
        #endregion

        #region Editor Settings Event Handlers
        private void FontComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializingSettings) return;
            if (FontComboBox.SelectedItem is string font)
            {
                MemoStorage.FontFamily = font;
                MainWindow.Instance?.ApplySettings();
                MainWindow.Instance?.QueueSaveSettings();
            }
        }

        private void FontWeightComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializingSettings) return;
            if (FontWeightComboBox.SelectedItem is string weight)
            {
                MemoStorage.FontWeight = weight;
                MainWindow.Instance?.ApplySettings();
                MainWindow.Instance?.QueueSaveSettings();
            }
        }

        private void FontSizeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_isInitializingSettings) return;
            double size = FontSizeSlider.Value;
            if (FontSizeValueText != null)
            {
                FontSizeValueText.Text = size.ToString("0.0");
            }
            MemoStorage.FontSize = size;
            MainWindow.Instance?.ApplySettings();
            MainWindow.Instance?.QueueSaveSettings();
        }

        private void FontSizeDecreaseButton_Click(object sender, RoutedEventArgs e)
        {
            FontSizeSlider.Value = Math.Max(FontSizeSlider.Minimum, FontSizeSlider.Value - 0.5);
        }

        private void FontSizeIncreaseButton_Click(object sender, RoutedEventArgs e)
        {
            FontSizeSlider.Value = Math.Min(FontSizeSlider.Maximum, FontSizeSlider.Value + 0.5);
        }

        private void LineSpacingComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializingSettings) return;
            if (LineSpacingComboBox.SelectedItem is string val && double.TryParse(val, out double ls))
            {
                MemoStorage.LineSpacing = ls;
                MainWindow.Instance?.ApplySettings();
                MainWindow.Instance?.QueueSaveSettings();
            }
        }

        private void LineSpacingComboBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (double.TryParse(LineSpacingComboBox.Text, out double ls))
            {
                if (ls < 0.5 || ls > 3.0)
                {
                    RestoreLineSpacingComboBoxSelection();
                    return;
                }
                if (Math.Abs(MemoStorage.LineSpacing - ls) > 0.01)
                {
                    MemoStorage.LineSpacing = ls;
                    MainWindow.Instance?.ApplySettings();
                    MainWindow.Instance?.QueueSaveSettings();
                }
                
                // 選択アイテムとテキストボックスを整合させる
                string? matchingItem = FindMatchingSpacingOption(LineSpacingComboBox, ls, 0.01);
                if (matchingItem != null)
                {
                    if (!object.ReferenceEquals(LineSpacingComboBox.SelectedItem, matchingItem))
                    {
                        LineSpacingComboBox.SelectedItem = matchingItem;
                    }
                }
                else
                {
                    LineSpacingComboBox.SelectedItem = null;
                    string targetText = ls.ToString("0.##");
                    if (LineSpacingComboBox.Text != targetText)
                    {
                        LineSpacingComboBox.Text = targetText;
                    }
                }
            }
            else
            {
                RestoreLineSpacingComboBoxSelection();
            }
        }

        private static string? FindMatchingSpacingOption(ComboBox comboBox, double value, double tolerance)
        {
            foreach (var item in comboBox.Items)
            {
                if (item is string text && double.TryParse(text, out double itemValue) && Math.Abs(itemValue - value) < tolerance)
                {
                    return text;
                }
            }

            return null;
        }

        private void RestoreLineSpacingComboBoxSelection()
        {
            string? matchingItem = FindMatchingSpacingOption(LineSpacingComboBox, MemoStorage.LineSpacing, 0.01);
            if (matchingItem != null)
            {
                LineSpacingComboBox.SelectedItem = matchingItem;
                return;
            }

            LineSpacingComboBox.SelectedItem = null;
            LineSpacingComboBox.Text = MemoStorage.LineSpacing.ToString("0.##");
        }

        private void ParagraphSpacingComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializingSettings) return;
            if (ParagraphSpacingComboBox.SelectedItem is string val && double.TryParse(val, out double ps))
            {
                MemoStorage.ParagraphSpacing = ps;
                MainWindow.Instance?.ApplySettings();
                MainWindow.Instance?.QueueSaveSettings();
            }
        }

        private void ParagraphSpacingComboBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (double.TryParse(ParagraphSpacingComboBox.Text, out double ps))
            {
                if (ps < 0 || ps > 100)
                {
                    RestoreParagraphSpacingComboBoxSelection();
                    return;
                }
                if (Math.Abs(MemoStorage.ParagraphSpacing - ps) > 0.1)
                {
                    MemoStorage.ParagraphSpacing = ps;
                    MainWindow.Instance?.ApplySettings();
                    MainWindow.Instance?.QueueSaveSettings();
                }
                
                string? matchingItem = FindMatchingSpacingOption(ParagraphSpacingComboBox, ps, 0.1);
                if (matchingItem != null)
                {
                    if (!object.ReferenceEquals(ParagraphSpacingComboBox.SelectedItem, matchingItem))
                    {
                        ParagraphSpacingComboBox.SelectedItem = matchingItem;
                    }
                }
                else
                {
                    ParagraphSpacingComboBox.SelectedItem = null;
                    string targetText = ((int)ps).ToString();
                    if (ParagraphSpacingComboBox.Text != targetText)
                    {
                        ParagraphSpacingComboBox.Text = targetText;
                    }
                }
            }
            else
            {
                RestoreParagraphSpacingComboBoxSelection();
            }
        }

        private void RestoreParagraphSpacingComboBoxSelection()
        {
            string? matchingItem = FindMatchingSpacingOption(ParagraphSpacingComboBox, MemoStorage.ParagraphSpacing, 0.1);
            if (matchingItem != null)
            {
                ParagraphSpacingComboBox.SelectedItem = matchingItem;
                return;
            }

            ParagraphSpacingComboBox.SelectedItem = null;
            ParagraphSpacingComboBox.Text = ((int)MemoStorage.ParagraphSpacing).ToString();
        }
        #endregion

        #region Window Settings Event Handlers
        private void OpacitySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_isInitializingSettings) return;
            double opacity = OpacitySlider.Value;
            if (OpacityValueText != null)
            {
                OpacityValueText.Text = $"{(int)opacity}%";
            }
            MemoStorage.Opacity = opacity;
            MainWindow.Instance?.ApplySettings();
            MainWindow.Instance?.QueueSaveSettings();
        }

        private void OpacityDecreaseButton_Click(object sender, RoutedEventArgs e)
        {
            OpacitySlider.Value = Math.Max(OpacitySlider.Minimum, OpacitySlider.Value - 5);
        }

        private void OpacityIncreaseButton_Click(object sender, RoutedEventArgs e)
        {
            OpacitySlider.Value = Math.Min(OpacitySlider.Maximum, OpacitySlider.Value + 5);
        }

        private void AlwaysOnTopToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializingSettings) return;
            if (sender is ToggleSwitch toggle)
            {
                MemoStorage.IsAlwaysOnTop = toggle.IsOn;
                MainWindow.Instance?.ApplySettings();
                MainWindow.Instance?.QueueSaveSettings();
            }
        }

        private void ShowDeleteButtonToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializingSettings) return;
            if (sender is ToggleSwitch ts)
            {
                MemoStorage.ShowDeleteButton = ts.IsOn;
                MainWindow.Instance?.RefreshAllNotesLists();
                MainWindow.Instance?.QueueSaveSettings();
            }
        }

        private void RecentNotesCountSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_isInitializingSettings) return;
            int count = (int)RecentNotesCountSlider.Value;
            if (RecentNotesCountValueText != null)
            {
                RecentNotesCountValueText.Text = count.ToString();
            }
            MemoStorage.RecentNotesCount = count;
            MainWindow.Instance?.RefreshAllNotesLists();
            MainWindow.Instance?.QueueSaveSettings();
        }

        private void RecentNotesCountDecreaseButton_Click(object sender, RoutedEventArgs e)
        {
            RecentNotesCountSlider.Value = Math.Max(RecentNotesCountSlider.Minimum, RecentNotesCountSlider.Value - 1);
        }

        private void RecentNotesCountIncreaseButton_Click(object sender, RoutedEventArgs e)
        {
            RecentNotesCountSlider.Value = Math.Min(RecentNotesCountSlider.Maximum, RecentNotesCountSlider.Value + 1);
        }
        #endregion

        #region System Settings (Hotkey & Action) Event Handlers


        private void HotKeyFlyout_Opened(object sender, object e)
        {
            // 一時的にグローバルホットキーを無効化
            MainWindow.Instance?.UnregisterMainWindowHotKeys();

            if (sender is Flyout flyout)
            {
                if (flyout == LaunchHotKeyFlyout)
                {
                    LaunchHotKeyInput.Text = MemoStorage.LaunchHotKey;
                    LaunchHotKeyErrorText.Text = string.Empty;
                    LaunchHotKeyErrorText.Visibility = Visibility.Collapsed;
                    LaunchHotKeyInput.Focus(FocusState.Programmatic);
                }
                else if (flyout == QuitHotKeyFlyout)
                {
                    QuitHotKeyInput.Text = MemoStorage.QuitHotKey;
                    QuitHotKeyErrorText.Text = string.Empty;
                    QuitHotKeyErrorText.Visibility = Visibility.Collapsed;
                    QuitHotKeyInput.Focus(FocusState.Programmatic);
                }
            }
        }

        private void HotKeyFlyout_Closed(object sender, object e)
        {
            MainWindow.Instance?.UpdateMainWindowHotKeys();
        }

        private void HotKeyInput_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (sender is not TextBox textBox) return;
            var key = e.Key;

            var ctrlState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
            var altState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu);
            var shiftState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
            var winState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.LeftWindows) | 
                           Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.RightWindows);

            bool ctrl = (ctrlState & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
            bool alt = (altState & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
            bool shift = (shiftState & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
            bool win = (winState & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

            e.Handled = true;

            if (key == Windows.System.VirtualKey.Enter)
            {
                if (textBox == LaunchHotKeyInput)
                {
                    SaveLaunchHotKey();
                }
                else if (textBox == QuitHotKeyInput)
                {
                    SaveQuitHotKey();
                }
                return;
            }

            if (key == Windows.System.VirtualKey.Escape)
            {
                if (textBox == LaunchHotKeyInput)
                {
                    LaunchHotKeyFlyout.Hide();
                }
                else if (textBox == QuitHotKeyInput)
                {
                    QuitHotKeyFlyout.Hide();
                }
                return;
            }

            if (key == Windows.System.VirtualKey.Back || key == Windows.System.VirtualKey.Delete)
            {
                textBox.Text = string.Empty;
                return;
            }

            var sb = new System.Text.StringBuilder();
            if (ctrl) sb.Append("Ctrl+");
            if (alt) sb.Append("Alt+");
            if (shift) sb.Append("Shift+");
            if (win) sb.Append("Win+");

            // モディファイアキーそのものは無視
            if (key == Windows.System.VirtualKey.Control || 
                key == Windows.System.VirtualKey.Menu || 
                key == Windows.System.VirtualKey.Shift || 
                key == Windows.System.VirtualKey.LeftWindows || 
                key == Windows.System.VirtualKey.RightWindows)
            {
                textBox.Text = sb.ToString();
                return;
            }

            sb.Append(key.ToString());
            textBox.Text = sb.ToString();
        }

        private void SaveLaunchHotKey_Click(object sender, RoutedEventArgs e)
        {
            SaveLaunchHotKey();
        }

        private void ClearLaunchHotKey_Click(object sender, RoutedEventArgs e)
        {
            LaunchHotKeyInput.Text = string.Empty;
            SaveLaunchHotKey();
        }

        private void CancelLaunchHotKey_Click(object sender, RoutedEventArgs e)
        {
            LaunchHotKeyFlyout.Hide();
        }

        private void SaveQuitHotKey_Click(object sender, RoutedEventArgs e)
        {
            SaveQuitHotKey();
        }

        private void ClearQuitHotKey_Click(object sender, RoutedEventArgs e)
        {
            QuitHotKeyInput.Text = string.Empty;
            SaveQuitHotKey();
        }

        private void CancelQuitHotKey_Click(object sender, RoutedEventArgs e)
        {
            QuitHotKeyFlyout.Hide();
        }

        private void SaveLaunchHotKey()
        {
            string val = LaunchHotKeyInput.Text;
            string previous = MemoStorage.LaunchHotKey;
            MemoStorage.LaunchHotKey = val;
            if (MainWindow.Instance != null && !MainWindow.Instance.UpdateMainWindowHotKeys())
            {
                MemoStorage.LaunchHotKey = previous;
                LaunchHotKeyErrorText.Text = MainWindow.Instance.LastHotKeyError ?? "ショートカットを登録できませんでした。";
                LaunchHotKeyErrorText.Visibility = Visibility.Visible;
                MainWindow.Instance.UpdateMainWindowHotKeys();
                return;
            }
            MemoStorage.SaveSettings();
            LaunchHotKeyButton.Content = string.IsNullOrEmpty(val) ? "None" : val;
            LaunchHotKeyFlyout.Hide();
        }

        private void SaveQuitHotKey()
        {
            string val = QuitHotKeyInput.Text;
            string previous = MemoStorage.QuitHotKey;
            MemoStorage.QuitHotKey = val;
            if (MainWindow.Instance != null && !MainWindow.Instance.UpdateMainWindowHotKeys())
            {
                MemoStorage.QuitHotKey = previous;
                QuitHotKeyErrorText.Text = MainWindow.Instance.LastHotKeyError ?? "ショートカットを登録できませんでした。";
                QuitHotKeyErrorText.Visibility = Visibility.Visible;
                MainWindow.Instance.UpdateMainWindowHotKeys();
                return;
            }
            MemoStorage.SaveSettings();
            QuitHotKeyButton.Content = string.IsNullOrEmpty(val) ? "None" : val;
            QuitHotKeyFlyout.Hide();
        }
        #endregion

        #region AI Settings Event Handlers
        private void AiApiKeyBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (MemoStorage.AiApiKey != AiApiKeyBox.Password)
            {
                MemoStorage.AiApiKey = AiApiKeyBox.Password;
                MainWindow.Instance?.QueueSaveSettings();
            }
        }

        private void AiModelNameBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (MemoStorage.AiModelName != AiModelNameBox.Text)
            {
                MemoStorage.AiModelName = AiModelNameBox.Text;
                MainWindow.Instance?.QueueSaveSettings();
            }
        }

        private void AiTemperatureSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_isInitializingSettings) return;
            double val = AiTemperatureSlider.Value;
            if (AiTemperatureValueText != null)
            {
                AiTemperatureValueText.Text = val.ToString("0.0");
            }
            MemoStorage.AiTemperature = val;
            MainWindow.Instance?.QueueSaveSettings();
        }

        private void AiMaxTokensBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(AiMaxTokensBox.Text, out int tokens))
            {
                if (MemoStorage.AiMaxTokens != tokens)
                {
                    MemoStorage.AiMaxTokens = tokens;
                    MainWindow.Instance?.QueueSaveSettings();
                }
            }
        }

        private void AiSystemPromptBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (MemoStorage.AiSystemPrompt != AiSystemPromptBox.Text)
            {
                MemoStorage.AiSystemPrompt = AiSystemPromptBox.Text;
                MainWindow.Instance?.QueueSaveSettings();
            }
        }

        private static void ApplyAiTextBoxColors(TextBox textBox)
        {
            textBox.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 37, 37, 37));
            textBox.Resources["TextControlBackground"] = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 37, 37, 37));
            textBox.Resources["TextControlBackgroundPointerOver"] = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 45, 45, 45));
            textBox.Resources["TextControlBackgroundFocused"] = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 45, 45, 45));
            textBox.Resources["TextControlForeground"] = new SolidColorBrush(Colors.White);
        }

        private void RefreshAiPromptsListUI()
        {
            if (AiPromptsListPanel == null) return;
            AiPromptsListPanel.Children.Clear();

            foreach (var item in MemoStorage.AiPrompts)
            {
                var container = new Border
                {
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 51, 51, 51)),
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 28, 28, 28)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8),
                    Margin = new Thickness(0, 0, 0, 4)
                };

                var grid = new Grid();
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var nameBox = new TextBox
                {
                    Text = item.Name,
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Height = 28,
                    Padding = new Thickness(6, 4, 6, 4),
                    Margin = new Thickness(0, 0, 8, 0),
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(3)
                };
                ApplyAiTextBoxColors(nameBox);

                nameBox.LostFocus += (s, e) =>
                {
                    item.Name = nameBox.Text;
                    MemoStorage.SaveAiPrompts();
                };

                var deleteBtn = new Button
                {
                    Content = "\uE74D",
                    FontFamily = new FontFamily("Segoe Fluent Icons"),
                    FontSize = 11,
                    Width = 28,
                    Height = 28,
                    Padding = new Thickness(0),
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 69, 58))
                };
                deleteBtn.Click += (s, e) =>
                {
                    MemoStorage.AiPrompts.Remove(item);
                    MemoStorage.SaveAiPrompts();
                    RefreshAiPromptsListUI();
                };

                var promptBox = new TextBox
                {
                    Text = item.Prompt,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    AcceptsReturn = true,
                    Height = 48,
                    Padding = new Thickness(6, 4, 6, 4),
                    Margin = new Thickness(0, 6, 0, 0),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 51, 51, 51)),
                    CornerRadius = new CornerRadius(3)
                };
                ApplyAiTextBoxColors(promptBox);

                promptBox.LostFocus += (s, e) =>
                {
                    item.Prompt = promptBox.Text;
                    MemoStorage.SaveAiPrompts();
                };

                Grid.SetRow(nameBox, 0);
                Grid.SetColumn(nameBox, 0);
                grid.Children.Add(nameBox);

                Grid.SetRow(deleteBtn, 0);
                Grid.SetColumn(deleteBtn, 1);
                grid.Children.Add(deleteBtn);

                Grid.SetRow(promptBox, 1);
                Grid.SetColumn(promptBox, 0);
                Grid.SetColumnSpan(promptBox, 2);
                grid.Children.Add(promptBox);

                container.Child = grid;
                AiPromptsListPanel.Children.Add(container);
            }
        }

        private void AddAiPrompt_Click(object sender, RoutedEventArgs e)
        {
            var newItem = new AiPromptItem
            {
                Id = Guid.NewGuid().ToString(),
                Name = "新しいプロンプト",
                Prompt = "指示を入力してください。"
            };
            MemoStorage.AiPrompts.Add(newItem);
            MemoStorage.SaveAiPrompts();
            RefreshAiPromptsListUI();
        }
        #endregion
    }
}
