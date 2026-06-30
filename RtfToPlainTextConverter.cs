using System;
using System.Collections.Generic;
using System.Text;

namespace sumi
{
    /// <summary>
    /// RTF（リッチテキスト形式）の文字列からプレーンテキストを抽出するユーティリティクラスです。
    /// UIスレッドに依存しないため、バックグラウンドでのロード・検索インデックス生成に利用できます。
    /// </summary>
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
                        // コントロールワードの読み込み
                        int start = i;
                        while (i < len && char.IsLetter(rtf[i]))
                        {
                            i++;
                        }
                        string word = rtf.Substring(start, i - start);

                        // オプションのパラメータ読み込み
                        bool hasParam = false;
                        int paramValue = 0;
                        if (i < len && (rtf[i] == '-' || char.IsDigit(rtf[i])))
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
                            if (i + 1 < len)
                            {
                                string hex = rtf.Substring(i, 2);
                                if (!currentIgnore)
                                {
                                    try
                                    {
                                        byte b = Convert.ToByte(hex, 16);
                                        sb.Append((char)b);
                                    }
                                    catch
                                    {
                                        // デコード失敗時は無視
                                    }
                                }
                                i += 2;
                            }
                        }
                    }
                }
                else
                {
                    if (!currentIgnore)
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
