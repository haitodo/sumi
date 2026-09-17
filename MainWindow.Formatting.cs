using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace sumi
{
    public sealed partial class MainWindow : Window, IDisposable
    {
        /// <param name="preserveFormatting">
        /// true の場合、RTF 読み込み直後に呼ばれるケース向けに、個々の文字サイズ・ウェイトの上書きをスキップする。
        /// </param>
        /// <param name="isPlainText">
        /// true の場合、プレーンテキストとして全体に一括適用し、重い UpdateRangeWeight をスキップします。
        /// </param>
        private void ApplyGlobalThemeToEditor(bool preserveFormatting = false, bool isPlainText = false)
        {
            if (MemoTextBox == null) return;

            var doc = MemoTextBox.Document;
            doc.BatchDisplayUpdates();
            try
            {
                // 【修正 1】SetDefaultCharacterFormat はRTFの装飾を破壊するため、必ず !preserveFormatting の中でのみ実行します
                if (!preserveFormatting)
                {
                    var defaultFormat = doc.GetDefaultCharacterFormat();
                    if (defaultFormat != null)
                    {
                        defaultFormat.Name = MemoStorage.FontFamily;
                        defaultFormat.Size = (float)MemoStorage.FontSize;
                        defaultFormat.Weight = GetDefaultFontWeight();
                        doc.SetDefaultCharacterFormat(defaultFormat);
                    }
                }

                var range = doc.GetRange(0, int.MaxValue);

                if (!preserveFormatting)
                {
                    range.CharacterFormat.Name = MemoStorage.FontFamily;
                    range.CharacterFormat.Size = (float)MemoStorage.FontSize;

                    ushort defaultWeight = GetDefaultFontWeight();
                    ushort boldWeight = GetBoldFontWeight();

                    if (isPlainText)
                    {
                        range.CharacterFormat.Weight = defaultWeight;
                    }
                    else
                    {
                        UpdateRangeWeight(doc, 0, range.Length, defaultWeight, boldWeight);
                    }

                    var selection = doc.Selection;
                    if (selection != null)
                    {
                        var selBold = selection.CharacterFormat.Bold;
                        var selSize = selection.CharacterFormat.Size;
                        var selWeight = selection.CharacterFormat.Weight;

                        bool isBoldOrHeading = (selBold == FormatEffect.On || selSize == 24 || selSize == 18);
                        ushort targetWeight = isBoldOrHeading ? boldWeight : defaultWeight;
                        if (selWeight != targetWeight)
                        {
                            selection.CharacterFormat.Weight = targetWeight;
                        }
                    }
                }
                else
                {
                    // 【修正 2】RTFロード時（装飾保護モード）は、一番最後の隠し改行文字とカーソル位置のみをターゲットにする
                    int endPos = range.Length;
                    if (endPos > 0)
                    {
                        // 最後の1文字（自動生成された改行）を取得し、フォント名とサイズだけを設定値に合わせる
                        // ※太字(Weight)や色には触れないため、直前の文字の装飾が消えることはありません
                        var endRange = doc.GetRange(endPos - 1, endPos);
                        endRange.CharacterFormat.Name = MemoStorage.FontFamily;
                        endRange.CharacterFormat.Size = (float)MemoStorage.FontSize;

                        // 末尾のカーソル位置（0文字幅）に対しても適用
                        var endPointRange = doc.GetRange(endPos, endPos);
                        endPointRange.CharacterFormat.Name = MemoStorage.FontFamily;
                        endPointRange.CharacterFormat.Size = (float)MemoStorage.FontSize;
                    }
                }

                float lineSpacing = (float)MemoStorage.LineSpacing;
                if (lineSpacing < 1.0f)
                {
                    // 文書全体の行間を一括で固定値に設定すると、H1やH2の段落で文字が押し潰されて重なってしまいます。
                    // 段落をループ処理し、それぞれのフォントサイズに基づいた適切な Exactly 行高を個別に適用します。
                    var paraRange = doc.GetRange(0, 0);
                    while (true)
                    {
                        paraRange.Expand(TextRangeUnit.Paragraph);

                        float paraFontSize = paraRange.CharacterFormat.Size;

                        // ★修正ポイント：サイズが混在して NaN になる場合、段落の「先頭」のサイズを取得する
                        if (float.IsNaN(paraFontSize) || paraFontSize <= 0)
                        {
                            var temp = paraRange.GetClone();
                            temp.Collapse(true); // 選択範囲を段落の先頭に畳む
                            paraFontSize = temp.CharacterFormat.Size;

                            // それでも取得できない場合はデフォルトサイズ
                            if (float.IsNaN(paraFontSize) || paraFontSize <= 0)
                            {
                                paraFontSize = (float)MemoStorage.FontSize;
                            }
                        }

                        // 先頭の文字が見出しサイズ（H1:24 または H2:18）かどうかで余白を分ける
                        if (paraFontSize == 24 || paraFontSize == 18)
                        {
                            // 見出しの場合は上部余白を0、下部余白を設定値にする
                            paraRange.ParagraphFormat.SpaceBefore = 0.0f;
                            paraRange.ParagraphFormat.SpaceAfter = (float)MemoStorage.ParagraphSpacing;
                        }
                        else
                        {
                            // 通常テキストの場合は上下の余白を詰める
                            paraRange.ParagraphFormat.SpaceBefore = 4.5f;
                            paraRange.ParagraphFormat.SpaceAfter = 1.5f;
                        }

                        // 下側をクリッピングする
                        float exactLineHeight = (float)(paraFontSize * 1.5f * lineSpacing);
                        paraRange.ParagraphFormat.SetLineSpacing(LineSpacingRule.Exactly, exactLineHeight);

                        int moved = paraRange.Move(TextRangeUnit.Paragraph, 1);
                        if (moved <= 0) break;
                    }
                }
                else
                {
                    range.ParagraphFormat.SetLineSpacing(LineSpacingRule.Multiple, lineSpacing);
                    range.ParagraphFormat.SpaceAfter = (float)MemoStorage.ParagraphSpacing;
                }
            }
            finally
            {
                doc.ApplyDisplayUpdates();
            }
        }

        private void UpdateRangeWeight(RichEditTextDocument doc, int start, int end, ushort defaultWeight, ushort boldWeight)
        {
            if (start >= end) return;

            // Stackによるループ構造に置き換え、コールスタック枯渇（StackOverflowException）を防ぎます
            var rangesToProcess = new Stack<(int Start, int End)>();
            rangesToProcess.Push((start, end));

            while (rangesToProcess.Count > 0)
            {
                var (currentStart, currentEnd) = rangesToProcess.Pop();
                if (currentStart >= currentEnd) continue;

                var range = doc.GetRange(currentStart, currentEnd);
                var bold = range.CharacterFormat.Bold;
                var size = range.CharacterFormat.Size;
                var weight = range.CharacterFormat.Weight;

                // 範囲内の太字、サイズ、ウェイトが均一であれば一括で更新
                if (bold != FormatEffect.Toggle &&
                    !float.IsNaN(size) &&
                    size > 0 &&
                    weight != 0)
                {
                    bool isBoldOrHeading = (bold == FormatEffect.On || size == 24 || size == 18);
                    ushort targetWeight = isBoldOrHeading ? boldWeight : defaultWeight;

                    if (weight != targetWeight)
                    {
                        range.CharacterFormat.Weight = targetWeight;
                    }
                }
                else
                {
                    // 範囲の長さが1文字以下の場合は、これ以上分割できないためここで直接更新
                    if (currentEnd - currentStart <= 1)
                    {
                        bool isBoldOrHeading = (bold == FormatEffect.On || size == 24 || size == 18);
                        ushort targetWeight = isBoldOrHeading ? boldWeight : defaultWeight;
                        if (weight != targetWeight)
                        {
                            range.CharacterFormat.Weight = targetWeight;
                        }
                        continue;
                    }

                    // 均一でない場合は半分に分割してスタックに積み直す（非再帰化）
                    int mid = currentStart + (currentEnd - currentStart) / 2;
                    rangesToProcess.Push((mid, currentEnd));
                    rangesToProcess.Push((currentStart, mid));
                }
            }
        }

        private Windows.UI.Text.FontWeight GetFontWeight(string weightStr)
        {
            return weightStr switch
            {
                "Light" => FontWeights.Light,
                "Medium" => FontWeights.Medium,
                "SemiBold" => FontWeights.SemiBold,
                "Bold" => FontWeights.Bold,
                _ => FontWeights.Normal,
            };
        }

        private ushort GetDefaultFontWeight()
        {
            return GetFontWeight(MemoStorage.FontWeight).Weight;
        }

        private FormatEffect GetDefaultBoldEffect()
        {
            return GetDefaultFontWeight() >= FontWeights.Bold.Weight 
                ? FormatEffect.On 
                : FormatEffect.Off;
        }

        private ushort GetBoldFontWeight()
        {
            var defaultWeight = GetDefaultFontWeight();
            if (defaultWeight == FontWeights.Light.Weight)
                return FontWeights.Medium.Weight;
            if (defaultWeight == FontWeights.Normal.Weight)
                return FontWeights.SemiBold.Weight;
            if (defaultWeight == FontWeights.Medium.Weight)
                return FontWeights.Bold.Weight;
            if (defaultWeight == FontWeights.SemiBold.Weight)
                return FontWeights.ExtraBold.Weight;
            
            return FontWeights.Black.Weight;
        }

        /// <summary>
        /// メモテキストの右端折り返し設定を切り替えます。
        /// </summary>
        private void WordWrapButton_Click(object sender, RoutedEventArgs e)
        {
            MemoTextBox.TextWrapping = MemoTextBox.TextWrapping == TextWrapping.Wrap ? TextWrapping.NoWrap : TextWrapping.Wrap;
        }

        private void MemoTextBox_SelectionChanged(object sender, RoutedEventArgs e)
        {
            if (_isRestoring || _isInitializing) return;
            UpdateFormatButtonStates();
        }

        private void UpdateFormatButtonStates()
        {
            if (MemoTextBox == null || FormatBoldBtn == null || FormatItalicBtn == null ||
                FormatUnderlineBtn == null || FormatStrikethroughBtn == null || FormatHighlightBtn == null ||
                FormatBulletListBtn == null || FormatNumberListBtn == null ||
                FormatHeading1Btn == null || FormatHeading2Btn == null)
            {
                return;
            }

            // 既存のSelectionをそのまま使用してCOMオブジェクトの新規生成を回避
            var selection = MemoTextBox.Document.Selection;
            if (selection == null) return;

            var format = selection.CharacterFormat;

            FormatBoldBtn.IsChecked = (format.Bold == FormatEffect.On || format.Weight >= GetBoldFontWeight());
            FormatItalicBtn.IsChecked = format.Italic == FormatEffect.On;
            FormatUnderlineBtn.IsChecked = format.Underline != UnderlineType.None;
            FormatStrikethroughBtn.IsChecked = format.Strikethrough == FormatEffect.On;

            var highlightColor = Microsoft.UI.ColorHelper.FromArgb(255, 120, 100, 0);
            FormatHighlightBtn.IsChecked = format.BackgroundColor == highlightColor;

            var listType = selection.ParagraphFormat.ListType;
            FormatBulletListBtn.IsChecked = (listType == MarkerType.Bullet);
            FormatNumberListBtn.IsChecked = (listType == MarkerType.Arabic);

            FormatHeading1Btn.IsChecked = (format.Size == 24 && (format.Bold == FormatEffect.On || format.Weight >= GetBoldFontWeight()));
            FormatHeading2Btn.IsChecked = (format.Size == 18 && (format.Bold == FormatEffect.On || format.Weight >= GetBoldFontWeight()));
        }

        private void MemoTextBox_PreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            var ctrlState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
            bool isCtrlDown = (ctrlState & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
            var shiftState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
            bool isShiftDown = (shiftState & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

            if (isCtrlDown)
            {
                // Ctrl + B, Ctrl + I, Ctrl + U を検知してドキュメントを変更状態にする
                if (e.Key == Windows.System.VirtualKey.B || e.Key == Windows.System.VirtualKey.I || e.Key == Windows.System.VirtualKey.U)
                {
                    MarkAsDirty();
                }

                // 箇条書き (Ctrl + Shift + L)
                if (isShiftDown && e.Key == Windows.System.VirtualKey.L)
                {
                    FormatBulletList_Click(null, null);
                    e.Handled = true;
                }
                // 番号付きリスト (Ctrl + Shift + N)
                else if (isShiftDown && e.Key == Windows.System.VirtualKey.N)
                {
                    FormatNumberList_Click(null, null);
                    e.Handled = true;
                }
                // ハイライト (Ctrl + Shift + H)
                else if (isShiftDown && e.Key == Windows.System.VirtualKey.H)
                {
                    ToggleHighlight();
                    e.Handled = true;
                }
                // 取り消し線 (Ctrl + Shift + S)
                else if (isShiftDown && e.Key == Windows.System.VirtualKey.S)
                {
                    ToggleStrikethrough();
                    e.Handled = true;
                }
                // 検索バー表示 (Ctrl + F)
                else if (!isShiftDown && e.Key == Windows.System.VirtualKey.F)
                {
                    ShowFindReplace(showReplace: false);
                    e.Handled = true;
                }
                // 置換バー表示 (Ctrl + H)
                else if (!isShiftDown && e.Key == Windows.System.VirtualKey.H)
                {
                    ShowFindReplace(showReplace: true);
                    e.Handled = true;
                }
                // 見出し1 (Ctrl + 1)
                else if (e.Key == Windows.System.VirtualKey.Number1)
                {
                    FormatHeading1_Click(null, null);
                    e.Handled = true;
                }
                // 見出し2 (Ctrl + 2)
                else if (e.Key == Windows.System.VirtualKey.Number2)
                {
                    FormatHeading2_Click(null, null);
                    e.Handled = true;
                }
                else
                {
                    switch (e.Key)
                    {
                        case Windows.System.VirtualKey.Space: // Ctrl + Space で装飾クリア
                            ClearFormatting();
                            e.Handled = true;
                            break;
                    }
                }
            }
        }

        private void FormatBold_Click(object sender, RoutedEventArgs e)
        {
            var format = MemoTextBox.Document.Selection.CharacterFormat;
            bool isBold = format.Bold == FormatEffect.On || format.Weight >= GetBoldFontWeight();
            if (isBold)
            {
                format.Bold = FormatEffect.Off;
                format.Weight = GetDefaultFontWeight();
            }
            else
            {
                format.Bold = FormatEffect.On;
                format.Weight = GetBoldFontWeight();
            }
            UpdateFormatButtonStates();
            MarkAsDirty();
        }

        private void FormatItalic_Click(object sender, RoutedEventArgs e)
        {
            var format = MemoTextBox.Document.Selection.CharacterFormat;
            format.Italic = format.Italic == FormatEffect.On ? FormatEffect.Off : FormatEffect.On;
            UpdateFormatButtonStates();
            MarkAsDirty();
        }

        private void FormatUnderline_Click(object sender, RoutedEventArgs e)
        {
            var format = MemoTextBox.Document.Selection.CharacterFormat;
            format.Underline = format.Underline == UnderlineType.None ? UnderlineType.Single : UnderlineType.None;
            UpdateFormatButtonStates();
            MarkAsDirty();
        }

        private void FormatStrikethrough_Click(object sender, RoutedEventArgs e)
        {
            ToggleStrikethrough();
        }

        private void FormatHighlight_Click(object sender, RoutedEventArgs e)
        {
            ToggleHighlight();
        }

        private void FormatClear_Click(object sender, RoutedEventArgs e)
        {
            ClearFormatting();
        }

        private void FormatBulletList_Click(object? sender, RoutedEventArgs? e)
        {
            var selection = MemoTextBox.Document.Selection;
            selection.ParagraphFormat.ListType = (selection.ParagraphFormat.ListType == MarkerType.Bullet) 
                ? MarkerType.None 
                : MarkerType.Bullet;
            
            MemoTextBox.Focus(FocusState.Programmatic);
            UpdateFormatButtonStates();
            MarkAsDirty();
        }

        private void FormatNumberList_Click(object? sender, RoutedEventArgs? e)
        {
            var selection = MemoTextBox.Document.Selection;
            if (selection.ParagraphFormat.ListType == MarkerType.Arabic)
            {
                selection.ParagraphFormat.ListType = MarkerType.None;
            }
            else
            {
                selection.ParagraphFormat.ListType = MarkerType.Arabic;
                selection.ParagraphFormat.ListStart = 1;
            }
                
            MemoTextBox.Focus(FocusState.Programmatic);
            UpdateFormatButtonStates();
            MarkAsDirty();
        }

        // 表の生成ボタン確定イベント
        private void InsertTableConfirm_Click(object sender, RoutedEventArgs e)
        {
            int rows = 3;
            int cols = 3;

            if (TableRowsComboBox.SelectedItem is string rStr && int.TryParse(rStr, out int r))
            {
                rows = r;
            }
            if (TableColsComboBox.SelectedItem is string cStr && int.TryParse(cStr, out int c))
            {
                cols = c;
            }

            // 指定された行・列数に基づいてRTFテーブルコードを生成
            string tableRtf = CreateTableRtf(rows, cols);

            try
            {
                // 現在の選択範囲（カーソル位置）にテーブルをフォーマットされたRTFとして挿入
                MemoTextBox.Document.Selection.SetText(TextSetOptions.FormatRtf, tableRtf);
                MarkAsDirty();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Insert Table Error] {ex.Message}");
            }

            InsertTableFlyout.Hide();
            MemoTextBox.Focus(FocusState.Programmatic);
        }

        // 境界線の色（グレー）を指定してRTF形式の表データを作成するメソッド
        private string CreateTableRtf(int rows, int cols)
        {
            var sb = new StringBuilder();

            // RTFヘッダーにカラーテーブルを定義し、インデックス1にグレー（R:150, G:150, B:150）を登録します
            sb.Append(@"{\rtf1\ansi\deff0{\colortbl;\red150\green150\blue150;}");

            int colWidth = 1800;

            for (int r = 0; r < rows; r++)
            {
                sb.Append(@"\trowd\trgaph100");

                for (int c = 0; c < cols; c++)
                {
                    int cellX = (c + 1) * colWidth;

                    // 各罫線の定義（clbrdr*）の末尾に、カラーテーブルから色を適用する制御ワード「\brdrcf1」を付加します
                    sb.Append(@"\clbrdrt\brdrs\brdrw15\brdrcf1" +
                              @"\clbrdrl\brdrs\brdrw15\brdrcf1" +
                              @"\clbrdrb\brdrs\brdrw15\brdrcf1" +
                              @"\clbrdrr\brdrs\brdrw15\brdrcf1");

                    sb.Append(@"\clpadt60\clpadl100\clpadb60\clpadr100");
                    sb.Append($@"\cellx{cellX}");
                }

                for (int c = 0; c < cols; c++)
                {
                    sb.Append(@" \intbl\cell");
                }

                sb.Append(@"\row");
            }

            sb.Append(@"}");
            return sb.ToString();
        }

        private void FormatHeading1_Click(object? sender, RoutedEventArgs? e)
        {
            var selection = MemoTextBox.Document.Selection;
            if (selection == null) return;

            // 1. 自動追跡可能なクローンを使って現在の選択範囲を保存
            var savedSelection = selection.GetClone();

            // 2. 選択範囲のサイズからトグル状態を判定
            float currentSize = selection.CharacterFormat.Size;
            if (float.IsNaN(currentSize) || currentSize <= 0)
            {
                var temp = selection.GetClone();
                temp.Collapse(true);
                currentSize = temp.CharacterFormat.Size;
            }

            bool isCurrentlyH1 = (currentSize == 24);

            float targetSize = isCurrentlyH1 ? (float)MemoStorage.FontSize : 24;
            var boldEffect = isCurrentlyH1 ? GetDefaultBoldEffect() : FormatEffect.On;
            ushort targetWeight = isCurrentlyH1 ? GetDefaultFontWeight() : GetBoldFontWeight();

            // 3. 選択された文字列（selection）に対してフォントとウェイトを適用
            selection.CharacterFormat.Size = targetSize;
            selection.CharacterFormat.Bold = boldEffect;
            selection.CharacterFormat.Weight = targetWeight;

            // 4. 選択範囲が含まれる段落全体を特定し、適用した targetSize に基づいて行高をダイレクトに設定
            var paraRange = MemoTextBox.Document.GetRange(selection.StartPosition, selection.EndPosition);
            paraRange.Expand(TextRangeUnit.Paragraph);

            float lineSpacing = (float)MemoStorage.LineSpacing;
            if (lineSpacing < 1.0f)
            {
                float exactLineHeight = (float)(targetSize * 1.5f * lineSpacing);
                paraRange.ParagraphFormat.SetLineSpacing(LineSpacingRule.Exactly, exactLineHeight);
            }
            else
            {
                paraRange.ParagraphFormat.SetLineSpacing(LineSpacingRule.Multiple, lineSpacing);
            }
            if (isCurrentlyH1)
            {
                // H2を解除して通常テキストに戻る場合は、装飾クリアと同じタイトな余白にリセットする
                paraRange.ParagraphFormat.SpaceBefore = 4.5f;
                paraRange.ParagraphFormat.SpaceAfter = 1.5f;
            }
            else
            {
                // H2見出しにする場合
                paraRange.ParagraphFormat.SpaceBefore = 0.0f;
                paraRange.ParagraphFormat.SpaceAfter = (float)MemoStorage.ParagraphSpacing;
            }

            // 5. 選択範囲（カーソル位置）を正確に復元
            selection.SetRange(savedSelection.StartPosition, savedSelection.EndPosition);

            MemoTextBox.Focus(FocusState.Programmatic);
            UpdateFormatButtonStates();
            MarkAsDirty();
        }

        private void FormatHeading2_Click(object? sender, RoutedEventArgs? e)
        {
            var selection = MemoTextBox.Document.Selection;
            if (selection == null) return;

            // 1. 自動追跡可能なクローンを使って現在の選択範囲を保存
            var savedSelection = selection.GetClone();

            // 2. 選択範囲のサイズからトグル状態を判定
            float currentSize = selection.CharacterFormat.Size;
            if (float.IsNaN(currentSize) || currentSize <= 0)
            {
                var temp = selection.GetClone();
                temp.Collapse(true);
                currentSize = temp.CharacterFormat.Size;
            }

            bool isCurrentlyH2 = (currentSize == 18);

            float targetSize = isCurrentlyH2 ? (float)MemoStorage.FontSize : 18;
            var boldEffect = isCurrentlyH2 ? GetDefaultBoldEffect() : FormatEffect.On;
            ushort targetWeight = isCurrentlyH2 ? GetDefaultFontWeight() : GetBoldFontWeight();

            // 3. 選択された文字列（selection）に対してフォントとウェイトを適用
            selection.CharacterFormat.Size = targetSize;
            selection.CharacterFormat.Bold = boldEffect;
            selection.CharacterFormat.Weight = targetWeight;

            // 4. 選択範囲が含まれる段落全体を特定し、適用した targetSize に基づいて行高をダイレクトに設定
            var paraRange = MemoTextBox.Document.GetRange(selection.StartPosition, selection.EndPosition);
            paraRange.Expand(TextRangeUnit.Paragraph);

            float lineSpacing = (float)MemoStorage.LineSpacing;
            if (lineSpacing < 1.0f)
            {
                float exactLineHeight = (float)(targetSize * 1.5f * lineSpacing);
                paraRange.ParagraphFormat.SetLineSpacing(LineSpacingRule.Exactly, exactLineHeight);
            }
            else
            {
                paraRange.ParagraphFormat.SetLineSpacing(LineSpacingRule.Multiple, lineSpacing);
            }
            if (isCurrentlyH2)
            {
                // H1を解除して通常テキストに戻る場合は、装飾クリアと同じタイトな余白にリセットする
                paraRange.ParagraphFormat.SpaceBefore = 4.5f;
                paraRange.ParagraphFormat.SpaceAfter = 1.5f;
            }
            else
            {
                // H1見出しにする場合は、設定に準拠した余白（または見出し用の余白）にする
                paraRange.ParagraphFormat.SpaceBefore = 0.0f;
                paraRange.ParagraphFormat.SpaceAfter = (float)MemoStorage.ParagraphSpacing;
            }

            // 5. 選択範囲（カーソル位置）を正確に復元
            selection.SetRange(savedSelection.StartPosition, savedSelection.EndPosition);

            MemoTextBox.Focus(FocusState.Programmatic);
            UpdateFormatButtonStates();
            MarkAsDirty();
        }

        private void ToggleHighlight()
        {
            var format = MemoTextBox.Document.Selection.CharacterFormat;
            
            // ダークテーマに合う控えめな黄色のハイライト色を設定
            var highlightColor = Microsoft.UI.ColorHelper.FromArgb(255, 120, 100, 0);
            var transparentColor = Microsoft.UI.Colors.Transparent;

            if (format.BackgroundColor == highlightColor)
            {
                format.BackgroundColor = transparentColor; // すでにハイライトされていれば解除
            }
            else
            {
                format.BackgroundColor = highlightColor; // ハイライトを適用
            }
            UpdateFormatButtonStates();
            MarkAsDirty();
        }

        private void ToggleStrikethrough()
        {
            var format = MemoTextBox.Document.Selection.CharacterFormat;
            format.Strikethrough = format.Strikethrough == FormatEffect.On ? FormatEffect.Off : FormatEffect.On;
            UpdateFormatButtonStates();
            MarkAsDirty();
        }

        private void ClearFormatting()
        {
            var selection = MemoTextBox.Document.Selection;
            if (selection == null) return;

            // 1. 自動追跡可能なクローンを使って現在の選択範囲を保存
            var savedSelection = selection.GetClone();

            // 2. 選択部分の文字装飾を標準に戻す
            var format = selection.CharacterFormat;
            format.Bold = GetDefaultBoldEffect();
            format.Weight = GetDefaultFontWeight();
            format.Italic = FormatEffect.Off;
            format.Underline = UnderlineType.None;
            format.Strikethrough = FormatEffect.Off;
            format.BackgroundColor = Microsoft.UI.Colors.Transparent;
            format.Size = (float)MemoStorage.FontSize;

            selection.ParagraphFormat.ListType = MarkerType.None;

            // 3. 選択範囲が含まれる段落全体を特定し、行高をデフォルトにダイレクトにリセット
            var paraRange = MemoTextBox.Document.GetRange(selection.StartPosition, selection.EndPosition);
            paraRange.Expand(TextRangeUnit.Paragraph);

            paraRange.ParagraphFormat.SpaceBefore = 4.5f;
            paraRange.ParagraphFormat.SpaceAfter = 1.5f;

            float lineSpacing = (float)MemoStorage.LineSpacing;

            float exactLineHeight = (float)(MemoStorage.FontSize * 1.5f * lineSpacing);
            paraRange.ParagraphFormat.SetLineSpacing(LineSpacingRule.Exactly, exactLineHeight);
            paraRange.ParagraphFormat.ListType = MarkerType.None;

            // 4. 選択範囲（カーソル位置）を正確に復元
            selection.SetRange(savedSelection.StartPosition, savedSelection.EndPosition);

            UpdateFormatButtonStates();
            MarkAsDirty();
        }

        private void RemoveEmptyLines_Click(object sender, RoutedEventArgs e)
        {
            if (MemoTextBox == null || MemoTextBox.IsReadOnly) return;

            var selection = MemoTextBox.Document.Selection;
            if (selection == null || selection.StartPosition == selection.EndPosition) return;

            var doc = MemoTextBox.Document;
            int start = selection.StartPosition;
            int end = selection.EndPosition;

            doc.BatchDisplayUpdates(); // 画面のちらつきを防止
            try
            {
                var paraRange = doc.GetRange(start, start);
                while (paraRange.StartPosition < end)
                {
                    paraRange.Expand(TextRangeUnit.Paragraph);

                    // 空行判定（空白や改行のみか）
                    string paraText = paraRange.Text?.Replace("\r", "").Replace("\n", "") ?? "";

                    if (string.IsNullOrWhiteSpace(paraText))
                    {
                        int len = paraRange.EndPosition - paraRange.StartPosition;

                        // 段落のテキストを空にすることで、太字などの装飾を維持したまま行を削除
                        paraRange.Text = string.Empty;
                        end -= len;

                        // 削除後は後続の段落が繰り上がるため位置をリセット
                        paraRange.SetRange(paraRange.StartPosition, paraRange.StartPosition);
                    }
                    else
                    {
                        int moved = paraRange.Move(TextRangeUnit.Paragraph, 1);
                        if (moved <= 0) break;
                    }
                }
            }
            finally
            {
                doc.ApplyDisplayUpdates();
            }

            ApplyGlobalThemeToEditor();

            UpdateFormatButtonStates();
            MarkAsDirty();
            MemoTextBox.Focus(FocusState.Programmatic);
        }

        private void RemoveWhitespaceAndNewlines_Click(object sender, RoutedEventArgs e)
        {
            if (MemoTextBox == null || MemoTextBox.IsReadOnly) return;

            var selection = MemoTextBox.Document.Selection;
            if (selection == null || selection.StartPosition == selection.EndPosition) return;

            var doc = MemoTextBox.Document;
            int start = selection.StartPosition;

            doc.BatchDisplayUpdates();
            try
            {
                string originalText = selection.Text;
                if (!string.IsNullOrEmpty(originalText))
                {
                    string mergedText = RemoveWhitespaceAndNewlines(originalText);
                    selection.SetText(TextSetOptions.None, mergedText);
                    selection.SetRange(start, start + mergedText.Length);
                }
            }
            finally
            {
                doc.ApplyDisplayUpdates();
            }

            ApplyGlobalThemeToEditor();

            UpdateFormatButtonStates();
            MarkAsDirty();
            MemoTextBox.Focus(FocusState.Programmatic);
        }

        private static string RemoveWhitespaceAndNewlines(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                // 空白文字（半角スペース、全角スペース、タブ、改行\r\nなどUnicode空白）およびゼロ幅文字を除外
                if (!char.IsWhiteSpace(c) && c != '\u200B' && c != '\uFEFF')
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        private static string TrimTrailingRtfPar(string rtf)
        {
            if (string.IsNullOrEmpty(rtf)) return rtf;

            // RTF の閉じ括弧 '}' の直前に \par が存在するかを後ろから走査して厳密に確認します
            int lastCloseBrace = rtf.LastIndexOf('}');
            if (lastCloseBrace == -1) return rtf;

            // 閉じカッコの直前にある改行文字やスペースをスキップ
            int searchIndex = lastCloseBrace - 1;
            while (searchIndex >= 0 && (rtf[searchIndex] == '\r' || rtf[searchIndex] == '\n' || rtf[searchIndex] == ' '))
            {
                searchIndex--;
            }

            if (searchIndex >= 3)
            {
                // ターゲット位置が本当に "\\par" であるかを部分的に検証して安全にトリム
                int parIndex = rtf.LastIndexOf("\\par", searchIndex, 4, StringComparison.Ordinal);
                if (parIndex != -1 && parIndex == searchIndex - 3)
                {
                    return rtf.Remove(parIndex, 4);
                }
            }

            return rtf;
        }

        private void MemoTextBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (_memoScrollViewer != null)
            {
                _memoScrollViewer.PointerWheelChanged -= ScrollViewer_PointerWheelChanged;
            }
            _memoScrollViewer = FindScrollViewer(MemoTextBox);
            if (_memoScrollViewer != null)
            {
                _memoScrollViewer.PointerWheelChanged += ScrollViewer_PointerWheelChanged;
            }

            // TTFP最適化: コントロールが完全に描画完了・初期化された後に、テキストの適用とテーマ設定を行う。
            if (_pendingNote != null)
            {
                string id = _pendingNote.Id;
                var pendingNoteCopy = _pendingNote;
                _pendingNote = null;

                this.DispatcherQueue.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    () =>
                    {
                        MemoTextBox.TextChanged -= MemoTextBox_TextChanged;
                        _isRestoring = true;

                        string rtfData = MemoStorage.LoadNoteRtf(id);
                        try
                        {
                            if (rtfData.StartsWith("{\\rtf1"))
                            {
                                MemoTextBox.Document.SetText(TextSetOptions.FormatRtf, rtfData);
                                // 個別装飾（太字・ハイライト等）を保護しつつ、新規入力行のためにデフォルト書式を登録する
                                ApplyGlobalThemeToEditor(preserveFormatting: true);
                            }
                            else
                            {
                                MemoTextBox.Document.SetText(TextSetOptions.None, rtfData);
                                ApplyGlobalThemeToEditor(preserveFormatting: false, isPlainText: true);
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[RTF Load Fallback Error] {ex.Message}");
                            MemoTextBox.Document.SetText(TextSetOptions.None, rtfData);
                            ApplyGlobalThemeToEditor(preserveFormatting: false, isPlainText: true);
                        }

                        // RichEditBoxに読み込ませた直後に、OSネイティブの解析結果（プレーンテキスト）を正確に取得
                        MemoTextBox.Document.GetText(TextGetOptions.UseLf, out string plainText);
                        if (plainText.EndsWith("\r") || plainText.EndsWith("\n"))
                            plainText = plainText.Substring(0, plainText.Length - 1);

                        if (PlaceholderTextBlock != null)
                        {
                            PlaceholderTextBlock.Visibility = string.IsNullOrEmpty(plainText) ? Visibility.Visible : Visibility.Collapsed;
                        }

                        // 起動直後、ネイティブ解析結果を用いてタイトルと文字数を確実に同期・更新する
                        if (pendingNoteCopy != null)
                        {
                            lock (MemoStorage.Notes)
                            {
                                pendingNoteCopy.Content = plainText;
                                pendingNoteCopy.Title = MemoStorage.GetTitleFromContent(plainText);
                                pendingNoteCopy.CharCount = plainText.Length;
                            }
                            TitleTextBlock.Text = pendingNoteCopy.Title;
                            UpdateCharCount(pendingNoteCopy.CharCount);
                        }

                        // ネストした TryEnqueue で非同期の TextChanged 処理を完全にやり過ごしてから状態を復帰する
                        this.DispatcherQueue.TryEnqueue(
                            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                            () =>
                            {
                                MemoTextBox.TextChanged -= MemoTextBox_TextChanged;
                                MemoTextBox.TextChanged += MemoTextBox_TextChanged;
                                _isRestoring = false;
                                _isDirty = false; // 起動時の自動汚染を防ぐために確実に false にする
                                MemoTextBox.Focus(FocusState.Programmatic);
                                int pos = GetCaretEndPosition();
                                MemoTextBox.Document.Selection.SetRange(pos, pos);
                                UpdateFormatButtonStates();
                            });
                    });
            }
        }

        private void MemoTextBox_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_memoScrollViewer != null)
            {
                _memoScrollViewer.PointerWheelChanged -= ScrollViewer_PointerWheelChanged;
                _memoScrollViewer = null;
            }
        }

        // メニューが表示される直前に内容を動的に構築する
        private void MemoTextBox_ContextFlyout_Opening(object sender, object e)
        {
            if (sender is not MenuFlyout menu) return;

            // 毎回メニュー項目をリセット
            menu.Items.Clear();

            // 現在選択されているテキストを取得
            var selection = MemoTextBox.Document.Selection;
            string selectedText = (selection?.Text ?? string.Empty).Trim();
            bool hasSelection = !string.IsNullOrEmpty(selectedText);

            // --- 標準テキスト編集コマンド ---
            var cutItem = new MenuFlyoutItem { Text = "切り取り", Icon = new SymbolIcon(Symbol.Cut) };
            cutItem.Click += (s, args) => selection?.Cut();
            cutItem.IsEnabled = hasSelection && !MemoTextBox.IsReadOnly;

            var copyItem = new MenuFlyoutItem { Text = "コピー", Icon = new SymbolIcon(Symbol.Copy) };
            copyItem.Click += (s, args) => selection?.Copy();
            copyItem.IsEnabled = hasSelection;

            var pasteItem = new MenuFlyoutItem { Text = "貼り付け", Icon = new SymbolIcon(Symbol.Paste) };
            pasteItem.Click += (s, args) => selection?.Paste(0);
            pasteItem.IsEnabled = !MemoTextBox.IsReadOnly;

            var selectAllItem = new MenuFlyoutItem { Text = "すべて選択", Icon = new SymbolIcon(Symbol.SelectAll) };
            selectAllItem.Click += (s, args) => selection?.SetRange(0, int.MaxValue);

            menu.Items.Add(cutItem);
            menu.Items.Add(copyItem);
            menu.Items.Add(pasteItem);
            menu.Items.Add(selectAllItem);

            // --- Web連携コマンド ---
            if (hasSelection)
            {
                menu.Items.Add(new MenuFlyoutSeparator());

                // 1. Web検索
                string displaySearchText = selectedText.Length > 15 ? selectedText.Substring(0, 15) + "..." : selectedText;
                var searchItem = new MenuFlyoutItem
                {
                    Text = $"Webで \"{displaySearchText}\" を検索",
                    Icon = new FontIcon { Glyph = "\xE721", FontFamily = new FontFamily("Segoe Fluent Icons") }
                };
                searchItem.Click += (s, args) =>
                {
                    string queryUrl = "https://www.google.com/search?q=" + Uri.EscapeDataString(selectedText);
                    OpenUrlInDefaultBrowser(queryUrl);
                };
                menu.Items.Add(searchItem);

                // 2. Webで開く (URL判定)
                bool isUrl = Uri.TryCreate(selectedText, UriKind.Absolute, out var uriResult)
                             && (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps);

                if (!isUrl && (selectedText.StartsWith("www.", StringComparison.OrdinalIgnoreCase) || selectedText.Contains(".")))
                {
                    isUrl = true; 
                }

                var openItem = new MenuFlyoutItem
                {
                    Text = "Webで開く",
                    Icon = new FontIcon { Glyph = "\xE71B", FontFamily = new FontFamily("Segoe Fluent Icons") },
                    IsEnabled = isUrl 
                };
                openItem.Click += (s, args) =>
                {
                    string url = selectedText;
                    if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        url = "https://" + url; 
                    }
                    OpenUrlInDefaultBrowser(url);
                };
                menu.Items.Add(openItem);
            }

            // --- テキスト整形コマンド ---
            if (hasSelection && !MemoTextBox.IsReadOnly)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
                var removeEmptyLinesItem = new MenuFlyoutItem
                {
                    Text = "選択範囲の空行を削除",
                    Icon = new FontIcon { Glyph = "\xED60", FontFamily = new FontFamily("Segoe Fluent Icons") }
                };
                removeEmptyLinesItem.Click += RemoveEmptyLines_Click;
                menu.Items.Add(removeEmptyLinesItem);

                var removeWhitespaceAndNewlinesItem = new MenuFlyoutItem
                {
                    Text = "選択範囲のスペース・改行を削除して統合",
                    Icon = new FontIcon { Glyph = "\xE16F", FontFamily = new FontFamily("Segoe Fluent Icons") }
                };
                removeWhitespaceAndNewlinesItem.Click += RemoveWhitespaceAndNewlines_Click;
                menu.Items.Add(removeWhitespaceAndNewlinesItem);
            }
        }

        /// <summary>
        /// .NET Core / WinUI 3環境で、安全にOS規定のデフォルトブラウザでURLを開くためのヘルパーです。
        /// </summary>
        private void OpenUrlInDefaultBrowser(string url)
        {
            try
            {
                // .NET Core環境で既定のブラウザを呼び出すには、UseShellExecute = true が必須です
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Browser Open Error] {ex.Message}");
            }
        }

        /// <summary>
        /// ScrollViewer の内部を走査して ScrollViewer コントロールを検索します。
        /// </summary>
        private ScrollViewer? FindScrollViewer(DependencyObject parent)
        {
            if (parent is ScrollViewer sv) return sv;
            int childrenCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childrenCount; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                var result = FindScrollViewer(child);
                if (result != null) return result;
            }
            return null;
        }

        /// <summary>
        /// ホイール操作時に ChangeView を用いて滑らかなスクロールを実行します。
        /// </summary>
        private void ScrollViewer_PointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            var scrollViewer = sender as ScrollViewer;
            if (scrollViewer == null) return;

            var pointerPoint = e.GetCurrentPoint(scrollViewer);
            var properties = pointerPoint.Properties;
            if (properties.IsHorizontalMouseWheel) return;

            int delta = properties.MouseWheelDelta;
            
            // スクロール速度の定義 (1ノッチ delta = 120 につき 48 ピクセル)
            double scrollAmount = -delta / 120.0 * 48.0;

            // ドラッグスクロールなどによる実際の位置のズレを同期
            if (Math.Abs(scrollViewer.VerticalOffset - _targetVerticalOffset) > 1.0)
            {
                _targetVerticalOffset = scrollViewer.VerticalOffset;
            }

            _targetVerticalOffset += scrollAmount;
            _targetVerticalOffset = Math.Clamp(_targetVerticalOffset, 0, scrollViewer.ScrollableHeight);

            // アニメーションを有効 (disableAnimation: false) にしてスクロールを実行
            scrollViewer.ChangeView(null, _targetVerticalOffset, null, false);

            e.Handled = true;
        }

        private int GetCaretEndPosition()
        {
            if (MemoTextBox == null) return 0;
            MemoTextBox.Document.GetText(TextGetOptions.UseLf, out string text);
            if (text.EndsWith("\r") || text.EndsWith("\n"))
                text = text.Substring(0, text.Length - 1);
            return text.Length;
        }
    }
}
