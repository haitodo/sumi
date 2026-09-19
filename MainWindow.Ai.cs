using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using sumi.Services;

namespace sumi
{
    public sealed partial class MainWindow : Window, IDisposable
    {
        private void AiRewriteMenuFlyout_Opened(object sender, object e)
        {
            if (AiRewriteMenuFlyout == null) return;
            AiRewriteMenuFlyout.Items.Clear();

            string selectedText = MemoTextBox.Document.Selection.Text;
            bool hasSelection = !string.IsNullOrEmpty(selectedText);

            if (!hasSelection)
            {
                var warningItem = new MenuFlyoutItem
                {
                    Text = "テキストを選択してください",
                    IsEnabled = false
                };
                AiRewriteMenuFlyout.Items.Add(warningItem);
                return;
            }

            foreach (var item in MemoStorage.AiPrompts)
            {
                var menuItem = new MenuFlyoutItem
                {
                    Text = item.Name
                };
                menuItem.Click += (s, ev) =>
                {
                    StartAiRewrite(item.Prompt, item.Name);
                };
                AiRewriteMenuFlyout.Items.Add(menuItem);
            }
        }

        private void StartAiRewrite(string promptText, string promptName)
        {
            if (string.IsNullOrWhiteSpace(MemoStorage.AiApiKey))
            {
                AiRewriteDialogStatusText.Text = "エラー";
                AiRewriteResultTextBox.Text = "【設定エラー】APIキーが設定されていません。\n設定画面の「AI」タブで OpenRouter の API Key を入力してください。";
                AiRewriteDialogOverlay.Visibility = Visibility.Visible;
                AiRewriteStopButton.Visibility = Visibility.Collapsed;
                AiRewriteReplaceButton.IsEnabled = false;
                AiRewriteAdjustButton.IsEnabled = false;
                AiRewriteRegenButton.IsEnabled = false;
                return;
            }

            string selectedText = MemoTextBox.Document.Selection.Text;
            if (string.IsNullOrEmpty(selectedText))
            {
                return;
            }

            _lastSelectedTextForRewrite = selectedText;
            _lastSelectedPromptForRewrite = promptText;
            _lastRewritePromptName = promptName;

            _aiRewriteChatHistory.Clear();
            if (!string.IsNullOrEmpty(MemoStorage.AiSystemPrompt))
            {
                _aiRewriteChatHistory.Add(new AiMessage { Role = "system", Content = MemoStorage.AiSystemPrompt });
            }
            _aiRewriteChatHistory.Add(new AiMessage { Role = "user", Content = $"{promptText}\n\n対象テキスト:\n{selectedText}" });

            AiRewriteDialogStatusText.Text = $"「{promptName}」で実行中...";
            AiRewriteResultTextBox.Text = "AIが思考しています...";
            AiRewriteDialogOverlay.Visibility = Visibility.Visible;
            AiRewriteStopButton.Visibility = Visibility.Visible;
            AiRewriteAdjustPanel.Visibility = Visibility.Collapsed;
            AiRewriteAdjustInput.Text = string.Empty;

            ExecuteAiCompletionsRequest();
        }

        private async void ExecuteAiCompletionsRequest()
        {
            _aiRewriteCts?.Cancel();
            _aiRewriteCts = new CancellationTokenSource();
            var token = _aiRewriteCts.Token;

            AiRewriteStopButton.Visibility = Visibility.Visible;
            AiRewriteReplaceButton.IsEnabled = false;
            AiRewriteAdjustButton.IsEnabled = false;
            AiRewriteRegenButton.IsEnabled = false;

            try
            {
                var request = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions");
                request.Headers.Add("Authorization", $"Bearer {MemoStorage.AiApiKey}");
                request.Headers.Add("HTTP-Referer", "https://github.com/haitodo/sumi");
                request.Headers.Add("X-Title", "sumi");

                // Native AOT対応: 匿名型の代わりに具体的な型 + ソースジェネレーターコンテキストを使用
                var requestBody = new AiRequestBody
                {
                    Model = MemoStorage.AiModelName,
                    Messages = _aiRewriteChatHistory,
                    Temperature = MemoStorage.AiTemperature,
                    MaxTokens = MemoStorage.AiMaxTokens,
                    Stream = true
                };

                string jsonBody = JsonSerializer.Serialize(requestBody, AiJsonContext.Default.AiRequestBody);
                request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                using var response = await _aiHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);

                if (!response.IsSuccessStatusCode)
                {
                    string errorDetail = await response.Content.ReadAsStringAsync(token);
                    this.DispatcherQueue.TryEnqueue(() =>
                    {
                        AiRewriteResultTextBox.Text = $"【APIエラー (ステータス: {response.StatusCode})】\n{errorDetail}";
                        AiRewriteStopButton.Visibility = Visibility.Collapsed;
                        AiRewriteReplaceButton.IsEnabled = false;
                        AiRewriteAdjustButton.IsEnabled = true;
                        AiRewriteRegenButton.IsEnabled = true;
                    });
                    return;
                }

                using var stream = await response.Content.ReadAsStreamAsync(token);
                using var reader = new StreamReader(stream, Encoding.UTF8);

                bool isFirstChunk = true;
                var accumulatedContent = new StringBuilder();

                string? line;
                while ((line = await reader.ReadLineAsync(token)) != null)
                {
                    token.ThrowIfCancellationRequested();
                    if (line.StartsWith("data: "))
                    {
                        string data = line.Substring(6).Trim();
                        if (data == "[DONE]") break;

                        try
                        {
                            using var doc = JsonDocument.Parse(data);
                            if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                            {
                                var choice = choices[0];
                                if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var contentProp))
                                {
                                    string content = contentProp.GetString() ?? "";
                                    accumulatedContent.Append(content);

                                    this.DispatcherQueue.TryEnqueue(() =>
                                    {
                                        if (isFirstChunk)
                                        {
                                            AiRewriteResultTextBox.Text = string.Empty;
                                            isFirstChunk = false;
                                        }
                                        AiRewriteResultTextBox.Text += content;
                                    });
                                }
                            }
                        }
                        catch
                        {
                        }
                    }
                }

                this.DispatcherQueue.TryEnqueue(() =>
                {
                    AiRewriteStopButton.Visibility = Visibility.Collapsed;
                    AiRewriteReplaceButton.IsEnabled = true;
                    AiRewriteAdjustButton.IsEnabled = true;
                    AiRewriteRegenButton.IsEnabled = true;
                    AiRewriteDialogStatusText.Text = "生成完了";
                    
                    _aiRewriteChatHistory.Add(new AiMessage { Role = "assistant", Content = AiRewriteResultTextBox.Text });
                });
            }
            catch (OperationCanceledException)
            {
                this.DispatcherQueue.TryEnqueue(() =>
                {
                    AiRewriteStopButton.Visibility = Visibility.Collapsed;
                    AiRewriteReplaceButton.IsEnabled = true;
                    AiRewriteAdjustButton.IsEnabled = true;
                    AiRewriteRegenButton.IsEnabled = true;
                    AiRewriteDialogStatusText.Text = "生成を停止しました";
                    
                    _aiRewriteChatHistory.Add(new AiMessage { Role = "assistant", Content = AiRewriteResultTextBox.Text });
                });
            }
            catch (Exception ex)
            {
                this.DispatcherQueue.TryEnqueue(() =>
                {
                    AiRewriteResultTextBox.Text = $"【接続エラー】\n{ex.Message}";
                    AiRewriteStopButton.Visibility = Visibility.Collapsed;
                    AiRewriteReplaceButton.IsEnabled = false;
                    AiRewriteAdjustButton.IsEnabled = true;
                    AiRewriteRegenButton.IsEnabled = true;
                    AiRewriteDialogStatusText.Text = "エラー発生";
                });
            }
        }

        private void AiRewriteDialogClose_Click(object sender, RoutedEventArgs e)
        {
            _aiRewriteCts?.Cancel();
            AiRewriteDialogOverlay.Visibility = Visibility.Collapsed;
        }

        private void AiRewriteReplace_Click(object sender, RoutedEventArgs e)
        {
            _aiRewriteCts?.Cancel();
            string newText = AiRewriteResultTextBox.Text;
            MemoTextBox.Document.Selection.SetText(Microsoft.UI.Text.TextSetOptions.None, newText);
            ApplyGlobalThemeToEditor();
            AiRewriteDialogOverlay.Visibility = Visibility.Collapsed;
        }

        private void AiRewriteAdjust_Click(object sender, RoutedEventArgs e)
        {
            if (AiRewriteAdjustPanel.Visibility == Visibility.Visible)
            {
                AiRewriteAdjustPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                AiRewriteAdjustPanel.Visibility = Visibility.Visible;
                AiRewriteAdjustInput.Focus(FocusState.Programmatic);
            }
        }

        private void AiRewriteAdjustSubmit_Click(object sender, RoutedEventArgs e)
        {
            string instruction = AiRewriteAdjustInput.Text.Trim();
            if (string.IsNullOrEmpty(instruction)) return;

            if (_aiRewriteChatHistory.Count > 0 && _aiRewriteChatHistory[^1].Role != "assistant")
            {
                _aiRewriteChatHistory.Add(new AiMessage { Role = "assistant", Content = AiRewriteResultTextBox.Text });
            }

            _aiRewriteChatHistory.Add(new AiMessage { Role = "user", Content = instruction });
            AiRewriteAdjustInput.Text = string.Empty;
            AiRewriteAdjustPanel.Visibility = Visibility.Collapsed;

            AiRewriteDialogStatusText.Text = "指示を適用して再生成中...";
            AiRewriteResultTextBox.Text = "AIが思考しています...";
            
            ExecuteAiCompletionsRequest();
        }

        private void AiRewriteAdjustInput_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                AiRewriteAdjustSubmit_Click(sender, new RoutedEventArgs());
            }
        }

        private void AiRewriteRegen_Click(object sender, RoutedEventArgs e)
        {
            _aiRewriteChatHistory.Clear();
            if (!string.IsNullOrEmpty(MemoStorage.AiSystemPrompt))
            {
                _aiRewriteChatHistory.Add(new AiMessage { Role = "system", Content = MemoStorage.AiSystemPrompt });
            }
            _aiRewriteChatHistory.Add(new AiMessage { Role = "user", Content = $"{_lastSelectedPromptForRewrite}\n\n対象テキスト:\n{_lastSelectedTextForRewrite}" });

            AiRewriteDialogStatusText.Text = $"「{_lastRewritePromptName}」で再生成中...";
            AiRewriteResultTextBox.Text = "AIが思考しています...";
            AiRewriteAdjustPanel.Visibility = Visibility.Collapsed;

            ExecuteAiCompletionsRequest();
        }

        private void AiRewriteStop_Click(object sender, RoutedEventArgs e)
        {
            _aiRewriteCts?.Cancel();
        }

        #region AIで実行

        private void AiRunButton_Click(object sender, RoutedEventArgs e)
        {
            if (AiRunDialogOverlay == null) return;

            string selectedText = MemoTextBox.Document.Selection.Text;
            bool hasSelection = !string.IsNullOrEmpty(selectedText);

            _lastSelectedTextForRun = selectedText;

            AiRunDialogStatusText.Text = "待機中";
            if (hasSelection)
            {
                AiRunResultTextBox.Text = "選択されたテキスト：\n" + (selectedText.Length > 100 ? selectedText.Substring(0, 100) + "..." : selectedText);
                AiRunReplaceIcon.Text = "\uE105"; // 置換アイコン
                AiRunReplaceText.Text = "置換";
            }
            else
            {
                AiRunResultTextBox.Text = "テキストが選択されていません。指示文を入力すると、生成された返答をカーソル位置に挿入します。";
                AiRunReplaceIcon.Text = "\uE109"; // 挿入（追加）アイコン
                AiRunReplaceText.Text = "挿入";
            }
            
            AiRunPromptInputTextBox.Text = string.Empty;
            AiRunPromptInputTextBox.IsEnabled = true;
            AiRunExecuteButton.IsEnabled = true;
            AiRunReplaceButton.IsEnabled = false;
            AiRunStopButton.Visibility = Visibility.Collapsed;
            AiRunDialogOverlay.Visibility = Visibility.Visible;

            AiRunPromptInputTextBox.Focus(FocusState.Programmatic);
        }

        private void AiRunExecuteButton_Click(object sender, RoutedEventArgs e)
        {
            string promptText = AiRunPromptInputTextBox.Text.Trim();
            if (string.IsNullOrEmpty(promptText)) return;

            if (string.IsNullOrWhiteSpace(MemoStorage.AiApiKey))
            {
                AiRunDialogStatusText.Text = "エラー";
                AiRunResultTextBox.Text = "【設定エラー】APIキーが設定されていません。\n設定画面の「AI」タブで OpenRouter の API Key を入力してください。";
                AiRunReplaceButton.IsEnabled = false;
                return;
            }

            _aiRunChatHistory.Clear();
            if (!string.IsNullOrEmpty(MemoStorage.AiSystemPrompt))
            {
                _aiRunChatHistory.Add(new AiMessage { Role = "system", Content = MemoStorage.AiSystemPrompt });
            }

            if (string.IsNullOrEmpty(_lastSelectedTextForRun))
            {
                _aiRunChatHistory.Add(new AiMessage { Role = "user", Content = promptText });
            }
            else
            {
                _aiRunChatHistory.Add(new AiMessage { Role = "user", Content = $"{promptText}\n\n対象テキスト:\n{_lastSelectedTextForRun}" });
            }

            AiRunDialogStatusText.Text = "実行中...";
            AiRunResultTextBox.Text = "AIが思考しています...";
            AiRunReplaceButton.IsEnabled = false;
            AiRunStopButton.Visibility = Visibility.Visible;

            ExecuteAiRunRequest();
        }

        private void AiRunPromptInputTextBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter && !Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            {
                e.Handled = true;
                AiRunExecuteButton_Click(sender, new RoutedEventArgs());
            }
        }

        private async void ExecuteAiRunRequest()
        {
            _aiRunCts?.Cancel();
            _aiRunCts = new CancellationTokenSource();
            var token = _aiRunCts.Token;

            try
            {
                var request = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions");
                request.Headers.Add("Authorization", $"Bearer {MemoStorage.AiApiKey}");
                request.Headers.Add("HTTP-Referer", "https://github.com/haitodo/sumi");
                request.Headers.Add("X-Title", "sumi");

                var requestBody = new AiRequestBody
                {
                    Model = MemoStorage.AiModelName,
                    Messages = _aiRunChatHistory,
                    Temperature = MemoStorage.AiTemperature,
                    MaxTokens = MemoStorage.AiMaxTokens,
                    Stream = true
                };

                string jsonBody = JsonSerializer.Serialize(requestBody, AiJsonContext.Default.AiRequestBody);
                request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                using var response = await _aiHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);

                if (!response.IsSuccessStatusCode)
                {
                    string errorDetail = await response.Content.ReadAsStringAsync(token);
                    this.DispatcherQueue.TryEnqueue(() =>
                    {
                        AiRunResultTextBox.Text = $"【APIエラー (ステータス: {response.StatusCode})】\n{errorDetail}";
                        AiRunStopButton.Visibility = Visibility.Collapsed;
                        AiRunReplaceButton.IsEnabled = false;
                    });
                    return;
                }

                using var stream = await response.Content.ReadAsStreamAsync(token);
                using var reader = new StreamReader(stream, Encoding.UTF8);

                bool isFirstChunk = true;
                string? line;
                while ((line = await reader.ReadLineAsync(token)) != null)
                {
                    token.ThrowIfCancellationRequested();
                    if (line.StartsWith("data: "))
                    {
                        string data = line.Substring(6).Trim();
                        if (data == "[DONE]") break;

                        try
                        {
                            using var doc = JsonDocument.Parse(data);
                            if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                            {
                                var choice = choices[0];
                                if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var contentProp))
                                {
                                    string content = contentProp.GetString() ?? "";
                                    this.DispatcherQueue.TryEnqueue(() =>
                                    {
                                        if (isFirstChunk)
                                        {
                                            AiRunResultTextBox.Text = string.Empty;
                                            isFirstChunk = false;
                                        }
                                        AiRunResultTextBox.Text += content;
                                    });
                                }
                            }
                        }
                        catch
                        {
                        }
                    }
                }

                this.DispatcherQueue.TryEnqueue(() =>
                {
                    AiRunStopButton.Visibility = Visibility.Collapsed;
                    AiRunReplaceButton.IsEnabled = true;
                    AiRunDialogStatusText.Text = "生成完了";
                });
            }
            catch (OperationCanceledException)
            {
                this.DispatcherQueue.TryEnqueue(() =>
                {
                    AiRunStopButton.Visibility = Visibility.Collapsed;
                    AiRunReplaceButton.IsEnabled = true;
                    AiRunDialogStatusText.Text = "生成を停止しました";
                });
            }
            catch (Exception ex)
            {
                this.DispatcherQueue.TryEnqueue(() =>
                {
                    AiRunResultTextBox.Text = $"【接続エラー】\n{ex.Message}";
                    AiRunStopButton.Visibility = Visibility.Collapsed;
                    AiRunReplaceButton.IsEnabled = false;
                    AiRunDialogStatusText.Text = "エラー発生";
                });
            }
        }

        private void AiRunDialogClose_Click(object sender, RoutedEventArgs e)
        {
            _aiRunCts?.Cancel();
            AiRunDialogOverlay.Visibility = Visibility.Collapsed;
        }

        private void AiRunStop_Click(object sender, RoutedEventArgs e)
        {
            _aiRunCts?.Cancel();
        }

        private void AiRunReplace_Click(object sender, RoutedEventArgs e)
        {
            _aiRunCts?.Cancel();
            string newText = AiRunResultTextBox.Text;
            MemoTextBox.Document.Selection.SetText(Microsoft.UI.Text.TextSetOptions.None, newText);
            ApplyGlobalThemeToEditor();
            AiRunDialogOverlay.Visibility = Visibility.Collapsed;
        }

        #endregion

        #region AIタスク生成

        private void AiTaskGenButton_Click(object sender, RoutedEventArgs e)
        {
            if (AiTaskGenDialogOverlay == null) return;

            NoteData? currentNote = null;
            lock (MemoStorage.Notes)
            {
                currentNote = MemoStorage.Notes.Find(n => n.Id == MemoStorage.CurrentNoteId);
            }

            if (currentNote == null)
            {
                return;
            }

            string selectedText = MemoTextBox.Document.Selection.Text;
            bool hasSelection = !string.IsNullOrEmpty(selectedText);

            if (!hasSelection)
            {
                AiTaskGenDialogStatusText.Text = "テキストを選択してください";
                _aiTaskGenPreviewItems.Clear();
                _aiTaskGenPreviewItems.Add(new TaskPreviewItem { Title = "【警告】テキストを選択して実行してください", IsSelected = false });
                AiTaskGenConfirmButton.IsEnabled = false;
                AiTaskGenStopButton.Visibility = Visibility.Collapsed;
                AiTaskGenDialogOverlay.Visibility = Visibility.Visible;
                return;
            }

            _lastSelectedTextForTaskGen = selectedText;
            _aiTaskGenPreviewItems.Clear();
            AiTaskGenDialogStatusText.Text = "タスクを考案中...";
            AiTaskGenConfirmButton.IsEnabled = false;
            AiTaskGenStopButton.Visibility = Visibility.Visible;
            AiTaskGenDialogOverlay.Visibility = Visibility.Visible;

            StartAiTaskGen();
        }

        private void StartAiTaskGen()
        {
            if (string.IsNullOrWhiteSpace(MemoStorage.AiApiKey))
            {
                AiTaskGenDialogStatusText.Text = "エラー";
                _aiTaskGenPreviewItems.Clear();
                _aiTaskGenPreviewItems.Add(new TaskPreviewItem { Title = "【設定エラー】APIキーが設定されていません。設定画面で API Key を入力してください。", IsSelected = false });
                AiTaskGenStopButton.Visibility = Visibility.Collapsed;
                AiTaskGenConfirmButton.IsEnabled = false;
                return;
            }

            ExecuteAiTaskGenRequest();
        }

        private async void ExecuteAiTaskGenRequest()
        {
            _aiTaskGenCts?.Cancel();
            _aiTaskGenCts = new CancellationTokenSource();
            var token = _aiTaskGenCts.Token;

            var chatHistory = new List<AiMessage>();
            if (!string.IsNullOrEmpty(MemoStorage.AiSystemPrompt))
            {
                chatHistory.Add(new AiMessage { Role = "system", Content = MemoStorage.AiSystemPrompt });
            }
            chatHistory.Add(new AiMessage
            {
                Role = "user",
                Content = $"以下のテキストを分析し、メモの内容を進めるうえで最適なタスクに分割してください。\nタスク名のみを1行1タスクの形式で出力してください。番号や記号は不要です。\n\n対象テキスト:\n{_lastSelectedTextForTaskGen}"
            });

            try
            {
                var request = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions");
                request.Headers.Add("Authorization", $"Bearer {MemoStorage.AiApiKey}");
                request.Headers.Add("HTTP-Referer", "https://github.com/haitodo/sumi");
                request.Headers.Add("X-Title", "sumi");

                var requestBody = new AiRequestBody
                {
                    Model = MemoStorage.AiModelName,
                    Messages = chatHistory,
                    Temperature = MemoStorage.AiTemperature,
                    MaxTokens = MemoStorage.AiMaxTokens,
                    Stream = true
                };

                string jsonBody = JsonSerializer.Serialize(requestBody, AiJsonContext.Default.AiRequestBody);
                request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                using var response = await _aiHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);

                if (!response.IsSuccessStatusCode)
                {
                    string errorDetail = await response.Content.ReadAsStringAsync(token);
                    this.DispatcherQueue.TryEnqueue(() =>
                    {
                        AiTaskGenDialogStatusText.Text = "APIエラー";
                        _aiTaskGenPreviewItems.Clear();
                        _aiTaskGenPreviewItems.Add(new TaskPreviewItem { Title = $"【APIエラー】 {errorDetail}", IsSelected = false });
                        AiTaskGenStopButton.Visibility = Visibility.Collapsed;
                    });
                    return;
                }

                using var stream = await response.Content.ReadAsStreamAsync(token);
                using var reader = new StreamReader(stream, Encoding.UTF8);

                var accumulatedContent = new StringBuilder();
                string? line;
                while ((line = await reader.ReadLineAsync(token)) != null)
                {
                    token.ThrowIfCancellationRequested();
                    if (line.StartsWith("data: "))
                    {
                        string data = line.Substring(6).Trim();
                        if (data == "[DONE]") break;

                        try
                        {
                            using var doc = JsonDocument.Parse(data);
                            if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                            {
                                var choice = choices[0];
                                if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var contentProp))
                                {
                                    string content = contentProp.GetString() ?? "";
                                    accumulatedContent.Append(content);
                                }
                            }
                        }
                        catch
                        {
                        }
                    }
                }

                this.DispatcherQueue.TryEnqueue(() =>
                {
                    ParseAndPopulateTaskGenPreview(accumulatedContent.ToString());
                    AiTaskGenStopButton.Visibility = Visibility.Collapsed;
                    AiTaskGenConfirmButton.IsEnabled = _aiTaskGenPreviewItems.Count > 0 && _aiTaskGenPreviewItems.Any(i => i.IsSelected && !string.IsNullOrWhiteSpace(i.Title));
                    AiTaskGenDialogStatusText.Text = "生成完了（登録するタスクを確認・編集してください）";
                });
            }
            catch (OperationCanceledException)
            {
                this.DispatcherQueue.TryEnqueue(() =>
                {
                    AiTaskGenStopButton.Visibility = Visibility.Collapsed;
                    AiTaskGenDialogStatusText.Text = "生成を停止しました";
                });
            }
            catch (Exception ex)
            {
                this.DispatcherQueue.TryEnqueue(() =>
                {
                    AiTaskGenDialogStatusText.Text = "エラー発生";
                    _aiTaskGenPreviewItems.Clear();
                    _aiTaskGenPreviewItems.Add(new TaskPreviewItem { Title = $"【エラー】 {ex.Message}", IsSelected = false });
                    AiTaskGenStopButton.Visibility = Visibility.Collapsed;
                });
            }
        }

        private void ParseAndPopulateTaskGenPreview(string text)
        {
            _aiTaskGenPreviewItems.Clear();
            string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var rawLine in lines)
            {
                string line = rawLine.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                // リストプレフィックス（"- ", "* ", "1. ", "・ " など）の除去
                if (line.StartsWith("- [ ]") || line.StartsWith("- [x]") || line.StartsWith("- [ ]") || line.StartsWith("- [X]"))
                {
                    line = line.Substring(5).Trim();
                }
                
                while (line.Length > 0 && (line[0] == '-' || line[0] == '*' || line[0] == '・' || line[0] == '+' || line[0] == '◦' || line[0] == '▪'))
                {
                    line = line.Substring(1).Trim();
                }

                int digitCount = 0;
                while (digitCount < line.Length && char.IsDigit(line[digitCount]))
                {
                    digitCount++;
                }
                if (digitCount > 0 && digitCount < line.Length && (line[digitCount] == '.' || line[digitCount] == ')' || line[digitCount] == '-'))
                {
                    line = line.Substring(digitCount + 1).Trim();
                }

                if (!string.IsNullOrEmpty(line))
                {
                    var item = new TaskPreviewItem { Title = line, IsSelected = true };
                    item.PropertyChanged += PreviewItem_PropertyChanged;
                    _aiTaskGenPreviewItems.Add(item);
                }
            }
        }

        private void PreviewItem_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TaskPreviewItem.IsSelected) || e.PropertyName == nameof(TaskPreviewItem.Title))
            {
                this.DispatcherQueue.TryEnqueue(() =>
                {
                    AiTaskGenConfirmButton.IsEnabled = _aiTaskGenPreviewItems.Count > 0 && _aiTaskGenPreviewItems.Any(i => i.IsSelected && !string.IsNullOrWhiteSpace(i.Title));
                });
            }
        }

        private void AiTaskGenConfirm_Click(object sender, RoutedEventArgs e)
        {
            _aiTaskGenCts?.Cancel();

            NoteData? currentNote = null;
            lock (MemoStorage.Notes)
            {
                currentNote = MemoStorage.Notes.Find(n => n.Id == MemoStorage.CurrentNoteId);
            }

            if (currentNote != null)
            {
                MemoStorage.LoadTasksForNoteSync(currentNote);
                
                bool anyAdded = false;
                foreach (var previewItem in _aiTaskGenPreviewItems)
                {
                    if (previewItem.IsSelected && !string.IsNullOrWhiteSpace(previewItem.Title))
                    {
                        var newTask = new TaskItemViewModel(
                            Guid.NewGuid().ToString(),
                            currentNote.Id,
                            previewItem.Title.Trim(),
                            false,
                            DateTime.UtcNow,
                            () => OnTaskChanged(currentNote.Id)
                        );
                        lock (currentNote.Tasks)
                        {
                            currentNote.Tasks.Add(newTask);
                        }
                        anyAdded = true;
                    }
                }

                if (anyAdded)
                {
                    OnTaskChanged(currentNote.Id);
                    PopulateCurrentTasks();
                    PopulateRightCurrentTasks();
                }
            }

            AiTaskGenDialogOverlay.Visibility = Visibility.Collapsed;
        }

        private void AiTaskGenDialogClose_Click(object sender, RoutedEventArgs e)
        {
            _aiTaskGenCts?.Cancel();
            AiTaskGenDialogOverlay.Visibility = Visibility.Collapsed;
        }

        private void AiTaskGenStop_Click(object sender, RoutedEventArgs e)
        {
            _aiTaskGenCts?.Cancel();
        }

        #endregion
    }
}
