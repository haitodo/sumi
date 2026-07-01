using System;
using System.Collections.Generic;
using System.Text;

namespace sumi
{
    /// <summary>
    /// RTF（リッチテキスト形式）の文字列からプレーンテキストを抽出するユーティリティクラスです。
    /// UIスレッドに依存しないため、バックグラウンドでのロード・検索インデックス生成に利用できます。
    /// </summary>
    [Obsolete("Use UI thread ITextDocument APIs for Rich Text editing operations. Use this only for background plain text extraction if necessary.", false)]
    public static class RtfToPlainTextConverter
    {
        public static string ConvertRtfToPlainText(string rtf)
        {
            if (string.IsNullOrEmpty(rtf)) return string.Empty;
            if (!rtf.StartsWith("{\\rtf")) return rtf; // RTF形式でない場合はプレーンテキストとして扱う

            var sb = new StringBuilder(rtf.Length / 2); // おおよそのバッファサイズ
            int len = rtf.Length;
            int i = 0;

            // 無視グループ（フォント情報、カラー情報、メタデータなど）の管理用スタック
            var ignoreStack = new Stack<bool>();
            bool currentIgnore = false;

             // Unicodeプレースホルダーの文字数カウント（デフォルトは1）
            int ucValue = 1;
            var ucStack = new Stack<int>();
            int codePage = 932; // デフォルトは Shift-JIS / CP932

            while (i < len)
            {
                char c = rtf[i];

                if (c == '{')
                {
                    ignoreStack.Push(currentIgnore);
                    ucStack.Push(ucValue);
                    i++;
                }
                else if (c == '}')
                {
                    if (ignoreStack.Count > 0) currentIgnore = ignoreStack.Pop();
                    else currentIgnore = false;

                    if (ucStack.Count > 0) ucValue = ucStack.Pop();
                    else ucValue = 1;

                    i++;
                }
                else if (c == '\\')
                {
                    i++;
                    if (i >= len) break;
                    char next = rtf[i];

                    // 特殊なエスケープ（バックスラッシュ、波括弧）
                    if (next == '\\' || next == '{' || next == '}')
                    {
                        if (!currentIgnore)
                        {
                            sb.Append(next);
                        }
                        i++;
                    }
                    else
                    {
                        // コントロールワード（アルファベット）またはコントロールシンボル（記号）の読み込み
                        int start = i;
                        bool isSymbol = !char.IsLetter(rtf[i]);
                        string word;
                        if (!isSymbol)
                        {
                            while (i < len && char.IsLetter(rtf[i]))
                            {
                                i++;
                            }
                            word = rtf.Substring(start, i - start);
                        }
                        else
                        {
                            word = rtf.Substring(start, 1);
                            i++;
                        }

                        // オプションのパラメータ読み込み（コントロールシンボルはパラメータを持たない）
                        bool hasParam = false;
                        int paramValue = 0;
                        if (!isSymbol && i < len && (rtf[i] == '-' || char.IsDigit(rtf[i])))
                        {
                            int paramStart = i;
                            if (rtf[i] == '-') i++;
                            while (i < len && char.IsDigit(rtf[i]))
                            {
                                i++;
                            }
                            if (int.TryParse(rtf.Substring(paramStart, i - paramStart), out int pVal))
                            {
                                paramValue = pVal;
                                hasParam = true;
                            }
                        }

                        // コントロールワード直後のスペース1文字は区切り文字として無視される
                        if (i < len && rtf[i] == ' ')
                        {
                            i++;
                        }

                        // コントロールワードに応じた処理
                        if (word == "fonttbl" || word == "colortbl" || word == "stylesheet" || word == "info" || word == "generator" || word == "themeData")
                        {
                            currentIgnore = true;
                        }
                        else if (word == "ansicpg")
                        {
                            if (hasParam)
                            {
                                codePage = paramValue;
                            }
                        }
                        else if (word == "par" || word == "line")
                        {
                            if (!currentIgnore)
                            {
                                sb.AppendLine();
                            }
                        }
                        else if (word == "uc")
                        {
                            if (hasParam)
                            {
                                ucValue = paramValue;
                            }
                        }
                        else if (word == "u")
                        {
                            if (!currentIgnore)
                            {
                                // Unicode文字コードの復元（負数は16ビット符号付きとして扱うため、明示的にキャスト）
                                char uniChar = (char)(ushort)(short)paramValue;
                                sb.Append(uniChar);
                            }

                            // 指定された文字数分、直後のプレースホルダー文字（通常は '?'）をスキップする
                            for (int skip = 0; skip < ucValue && i < len; skip++)
                            {
                                if (rtf[i] == '\\')
                                {
                                    i++;
                                    if (i < len && rtf[i] == '\'')
                                    {
                                        i += 3; // \'hh をスキップ
                                    }
                                    else
                                    {
                                        i++; // 通常のエスケープ文字をスキップ
                                    }
                                }
                                else
                                {
                                    i++;
                                }
                            }
                        }
                        else if (word == "'")
                        {
                            // 16進数文字コードエスケープ (\'hh)
                            var byteList = new List<byte>();
                            if (i + 1 < len)
                            {
                                string hex = rtf.Substring(i, 2);
                                try
                                {
                                    byteList.Add(Convert.ToByte(hex, 16));
                                }
                                catch
                                {
                                    // デコード失敗時は無視
                                }
                                i += 2;
                            }

                            // 後続する連続の \'hh を先読みして一括で取得（マルチバイト文字デコードのため）
                            while (i + 3 < len && rtf[i] == '\\' && rtf[i + 1] == '\'')
                            {
                                string hex = rtf.Substring(i + 2, 2);
                                try
                                {
                                    byteList.Add(Convert.ToByte(hex, 16));
                                    i += 4;
                                }
                                catch
                                {
                                    break;
                                }
                            }

                            if (byteList.Count > 0 && !currentIgnore)
                            {
                                try
                                {
                                    string decoded = Encoding.GetEncoding(codePage).GetString(byteList.ToArray());
                                    sb.Append(decoded);
                                }
                                catch
                                {
                                    // 指定されたエンコーディングでのデコードに失敗した場合はそのまま各バイトを文字としてキャストしてフォールバック
                                    foreach (byte b in byteList)
                                    {
                                        sb.Append((char)b);
                                    }
                                }
                            }
                        }
                    }
                }
                else
                {
                    // RTF仕様に基づき、生の改行文字（\r, \n）はレイアウトやプレーンテキストに反映させず無視する
                    if (!currentIgnore && c != '\r' && c != '\n')
                    {
                        sb.Append(c);
                    }
                    i++;
                }
            }

            return sb.ToString();
        }
    }
}
