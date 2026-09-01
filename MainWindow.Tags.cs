using System;
using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace sumi
{
    public sealed partial class MainWindow : Window, IDisposable
    {
        private static Windows.UI.Color ColorFromHsl(double h, double s, double l)
        {
            s /= 100.0;
            l /= 100.0;
            double c = (1.0 - Math.Abs(2.0 * l - 1.0)) * s;
            double x = c * (1.0 - Math.Abs((h / 60.0) % 2.0 - 1.0));
            double m = l - c / 2.0;
            double r = 0, g = 0, b = 0;

            if (0 <= h && h < 60) { r = c; g = x; b = 0; }
            else if (60 <= h && h < 120) { r = x; g = c; b = 0; }
            else if (120 <= h && h < 180) { r = 0; g = c; b = x; }
            else if (180 <= h && h < 240) { r = 0; g = x; b = c; }
            else if (240 <= h && h < 300) { r = x; g = 0; b = c; }
            else if (300 <= h && h < 360) { r = c; g = 0; b = x; }

            byte red = (byte)Math.Clamp((r + m) * 255, 0, 255);
            byte green = (byte)Math.Clamp((g + m) * 255, 0, 255);
            byte blue = (byte)Math.Clamp((b + m) * 255, 0, 255);

            return ColorHelper.FromArgb(255, red, green, blue);
        }

        private static (Brush Background, Brush Foreground) GetTagBrushes(string tag)
        {
            if (string.IsNullOrEmpty(tag))
            {
                var bg = new SolidColorBrush(ColorHelper.FromArgb(255, 0x33, 0x33, 0x33));
                var fg = new SolidColorBrush(ColorHelper.FromArgb(255, 0xcc, 0xcc, 0xcc));
                return (bg, fg);
            }

            int hash = 0;
            foreach (char c in tag)
            {
                hash = c + (hash << 5) - hash;
            }

            double hue = Math.Abs(hash % 360);
            double saturation = 55.0; // 落ち着いた鮮やかさ
            double lightness = 28.0;   // ダークモードに合う暗めの背景色

            var bgColor = ColorFromHsl(hue, saturation, lightness);
            var fgColor = ColorFromHsl(hue, saturation, 88.0); // 視認性の高い前景色

            return (new SolidColorBrush(bgColor), new SolidColorBrush(fgColor));
        }

        private void PopulateTagsView()
        {
            if (LeftTagsListContainer == null) return;
            LeftTagsListContainer.Children.Clear();

            // 1. "All Notes" item
            AddSidebarTagItem(LeftTagsListContainer, "All Notes", string.Empty, _activeLeftTagFilter == string.Empty);

            // 2. "Untagged" item
            int untaggedCount = 0;
            lock (MemoStorage.Notes)
            {
                foreach (var note in MemoStorage.Notes)
                {
                    if (note.Tags.Count == 0) untaggedCount++;
                }
            }
            AddSidebarTagItem(LeftTagsListContainer, "タグなし", "Untagged", _activeLeftTagFilter == "Untagged", untaggedCount);

            // 3. Regular tags
            var allTags = MemoStorage.GetAllTags();
            foreach (var tag in allTags)
            {
                if (!string.IsNullOrEmpty(_sidebarTagQuery) && !tag.Contains(_sidebarTagQuery, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int tagNoteCount = 0;
                lock (MemoStorage.Notes)
                {
                    foreach (var note in MemoStorage.Notes)
                    {
                        if (note.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) tagNoteCount++;
                    }
                }

                AddSidebarTagItem(LeftTagsListContainer, "# " + tag, tag, _activeLeftTagFilter == tag, tagNoteCount);
            }
        }

        private void PopulateRightTagsView()
        {
            if (RightTagsListContainer == null) return;
            RightTagsListContainer.Children.Clear();

            // 1. "All Notes" item
            AddSidebarTagItem(RightTagsListContainer, "All Notes", string.Empty, _activeRightTagFilter == string.Empty, isRight: true);

            // 2. "Untagged" item
            int untaggedCount = 0;
            lock (MemoStorage.Notes)
            {
                foreach (var note in MemoStorage.Notes)
                {
                    if (note.Tags.Count == 0) untaggedCount++;
                }
            }
            AddSidebarTagItem(RightTagsListContainer, "タグなし", "Untagged", _activeRightTagFilter == "Untagged", untaggedCount, isRight: true);

            // 3. Regular tags
            var allTags = MemoStorage.GetAllTags();
            foreach (var tag in allTags)
            {
                if (!string.IsNullOrEmpty(_rightSidebarTagQuery) && !tag.Contains(_rightSidebarTagQuery, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int tagNoteCount = 0;
                lock (MemoStorage.Notes)
                {
                    foreach (var note in MemoStorage.Notes)
                    {
                        if (note.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) tagNoteCount++;
                    }
                }

                AddSidebarTagItem(RightTagsListContainer, "# " + tag, tag, _activeRightTagFilter == tag, tagNoteCount, isRight: true);
            }
        }

        private void AddSidebarTagItem(StackPanel container, string displayText, string tagValue, bool isSelected, int count = -1, bool isRight = false)
        {
            var grid = new Grid
            {
                Padding = new Thickness(12, 8, 12, 8),
                CornerRadius = new CornerRadius(4),
                Background = isSelected ? new SolidColorBrush(ColorHelper.FromArgb(32, 255, 255, 255)) : new SolidColorBrush(Colors.Transparent)
            };

            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var btn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(4)
            };
            btn.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(ColorHelper.FromArgb(20, 255, 255, 255));
            btn.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(ColorHelper.FromArgb(35, 255, 255, 255));

            // TextBlock for Tag Name
            var txt = new TextBlock
            {
                Text = displayText,
                FontSize = 13,
                Foreground = new SolidColorBrush(Colors.White),
                VerticalAlignment = VerticalAlignment.Center
            };
            
            // Set Color coloring if it is a tag (starts with #)
            if (displayText.StartsWith("# ") && tagValue != "Untagged")
            {
                var (bg, fg) = GetTagBrushes(tagValue);
                txt.Foreground = fg;
            }

            Grid.SetColumn(txt, 0);
            grid.Children.Add(txt);

            // TextBlock for Note Count
            if (count >= 0)
            {
                var cntTxt = new TextBlock
                {
                    Text = count.ToString(),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 153, 153, 153)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0)
                };
                Grid.SetColumn(cntTxt, 1);
                grid.Children.Add(cntTxt);
            }

            btn.Content = grid;
            btn.Click += (s, e) =>
            {
                if (isRight)
                {
                    _activeRightTagFilter = tagValue;
                    MemoStorage.LastSelectedRightTag = tagValue;
                    QueueSaveSettings();
                    SetRightSidebarActiveTagFilterUI();
                    SetRightSidebarView(SidebarView.Notes);
                }
                else
                {
                    _activeLeftTagFilter = tagValue;
                    MemoStorage.LastSelectedTag = tagValue;
                    QueueSaveSettings();
                    SetSidebarActiveTagFilterUI();
                    SetSidebarView(SidebarView.Notes);
                }
            };

            container.Children.Add(btn);
        }

        private void SetSidebarActiveTagFilterUI()
        {
            if (SidebarActiveTagFilterGrid == null || SidebarActiveTagFilterTextBlock == null) return;
            if (string.IsNullOrEmpty(_activeLeftTagFilter))
            {
                SidebarActiveTagFilterGrid.Visibility = Visibility.Collapsed;
            }
            else
            {
                SidebarActiveTagFilterGrid.Visibility = Visibility.Visible;
                SidebarActiveTagFilterTextBlock.Text = _activeLeftTagFilter == "Untagged" ? "タグなし" : _activeLeftTagFilter;
            }
            PopulateSidebarNotesList(SidebarNoteSearchBox != null ? SidebarNoteSearchBox.Text : "");
        }

        private void SetRightSidebarActiveTagFilterUI()
        {
            if (RightSidebarActiveTagFilterGrid == null || RightSidebarActiveTagFilterTextBlock == null) return;
            if (string.IsNullOrEmpty(_activeRightTagFilter))
            {
                RightSidebarActiveTagFilterGrid.Visibility = Visibility.Collapsed;
            }
            else
            {
                RightSidebarActiveTagFilterGrid.Visibility = Visibility.Visible;
                RightSidebarActiveTagFilterTextBlock.Text = _activeRightTagFilter == "Untagged" ? "タグなし" : _activeRightTagFilter;
            }
            PopulateRightSidebarNotesList(RightSidebarNoteSearchBox != null ? RightSidebarNoteSearchBox.Text : "");
        }

        private void ClearSidebarActiveTagFilter_Click(object sender, RoutedEventArgs e)
        {
            _activeLeftTagFilter = string.Empty;
            MemoStorage.LastSelectedTag = string.Empty;
            QueueSaveSettings();
            SetSidebarActiveTagFilterUI();
            SetSidebarView(SidebarView.Tags);
        }

        private void ClearRightSidebarActiveTagFilter_Click(object sender, RoutedEventArgs e)
        {
            _activeRightTagFilter = string.Empty;
            MemoStorage.LastSelectedRightTag = string.Empty;
            QueueSaveSettings();
            SetRightSidebarActiveTagFilterUI();
            SetRightSidebarView(SidebarView.Tags);
        }

        private void SidebarTagSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox tb)
            {
                _sidebarTagQuery = tb.Text.Trim();
                PopulateTagsView();
            }
        }

        private void RightSidebarTagSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox tb)
            {
                _rightSidebarTagQuery = tb.Text.Trim();
                PopulateRightTagsView();
            }
        }

        private void UpdateHeaderTags()
        {
            if (HeaderTagsPanel == null) return;
            HeaderTagsPanel.Children.Clear();

            NoteData? currentNote = null;
            lock (MemoStorage.Notes)
            {
                currentNote = MemoStorage.Notes.Find(n => n.Id == MemoStorage.CurrentNoteId);
            }

            if (currentNote != null)
            {
                foreach (var tag in currentNote.Tags)
                {
                    var border = new Border
                    {
                        CornerRadius = new CornerRadius(10),
                        Padding = new Thickness(8, 2, 8, 2),
                        VerticalAlignment = VerticalAlignment.Center
                    };

                    var (bg, fg) = GetTagBrushes(tag);
                    border.Background = bg;

                    var txt = new TextBlock
                    {
                        Text = tag,
                        FontSize = 10.5,
                        Foreground = fg,
                        VerticalAlignment = VerticalAlignment.Center
                    };

                    border.Child = txt;
                    HeaderTagsPanel.Children.Add(border);
                }
            }
        }

        private void ManageTagsFlyout_Opened(object sender, object e)
        {
            if (NewTagTextBox != null)
            {
                NewTagTextBox.Text = string.Empty;
            }
            PopulateManageTagsList();
        }

        private void PopulateManageTagsList()
        {
            if (ManageTagsListContainer == null) return;
            ManageTagsListContainer.Children.Clear();

            NoteData? currentNote = null;
            lock (MemoStorage.Notes)
            {
                currentNote = MemoStorage.Notes.Find(n => n.Id == MemoStorage.CurrentNoteId);
            }

            if (currentNote == null) return;

            var allTags = MemoStorage.GetAllTags();
            foreach (var tag in allTags)
            {
                var isChecked = currentNote.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase);

                var grid = new Grid { Padding = new Thickness(4, 2, 4, 2) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var cb = new CheckBox
                {
                    Content = tag,
                    IsChecked = isChecked,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center
                };

                var (bg, fg) = GetTagBrushes(tag);
                cb.Foreground = fg;

                cb.Checked += (s, ev) =>
                {
                    if (!currentNote.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                    {
                        currentNote.Tags.Add(tag);
                        MemoStorage.SaveMetadata();
                        UpdateHeaderTags();
                        RefreshAllTagsViews();
                    }
                };

                cb.Unchecked += (s, ev) =>
                {
                    if (currentNote.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                    {
                        currentNote.Tags.Remove(tag);
                        MemoStorage.SaveMetadata();
                        UpdateHeaderTags();
                        RefreshAllTagsViews();
                    }
                };

                Grid.SetColumn(cb, 0);
                grid.Children.Add(cb);

                // AOT/Releaseビルドで動的リソース参照がクラッシュするのを防ぐため安全に取得
                Style? iconButtonStyle = null;
                try
                {
                    if (RootGrid != null)
                    {
                        iconButtonStyle = RootGrid.Resources["IconButtonStyle"] as Style;
                    }
                }
                catch
                {
                    // リソース取得失敗時のフォールバック処理は下部で行います
                }

                var delBtn = new Button
                {
                    Content = "\uE74D",
                    FontFamily = new FontFamily("Segoe Fluent Icons"),
                    Width = 24,
                    Height = 24,
                    FontSize = 10,
                    Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 255, 69, 58)),
                    VerticalAlignment = VerticalAlignment.Center
                };

                if (iconButtonStyle != null)
                {
                    delBtn.Style = iconButtonStyle;
                }
                else
                {
                    // スタイルが見つからない・適用できない場合は手動でスタイルを模倣
                    delBtn.Background = new SolidColorBrush(Colors.Transparent);
                    delBtn.BorderThickness = new Thickness(0);
                    delBtn.CornerRadius = new CornerRadius(4);
                    delBtn.Padding = new Thickness(0);
                }

                delBtn.Click += (s, ev) =>
                {
                    lock (MemoStorage.Notes)
                    {
                        foreach (var note in MemoStorage.Notes)
                        {
                            note.Tags.Remove(tag);
                        }
                    }
                    MemoStorage.SaveMetadata();
                    UpdateHeaderTags();
                    PopulateManageTagsList();
                    RefreshAllTagsViews();
                };

                Grid.SetColumn(delBtn, 1);
                grid.Children.Add(delBtn);

                ManageTagsListContainer.Children.Add(grid);
            }
        }

        private void RefreshAllTagsViews()
        {
            PopulateTagsView();
            PopulateRightTagsView();
            PopulateSidebarNotesList(SidebarNoteSearchBox != null ? SidebarNoteSearchBox.Text : "");
            PopulateRightSidebarNotesList(RightSidebarNoteSearchBox != null ? RightSidebarNoteSearchBox.Text : "");
        }

        private void AddTag_Click(object sender, RoutedEventArgs e)
        {
            AddNewTagFromTextBox();
        }

        private void NewTagTextBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                AddNewTagFromTextBox();
                e.Handled = true;
            }
        }

        private void AddNewTagFromTextBox()
        {
            if (NewTagTextBox == null) return;
            string text = NewTagTextBox.Text.Trim().Replace("|", "_").Replace(",", "_");
            if (!string.IsNullOrEmpty(text))
            {
                NoteData? currentNote = null;
                lock (MemoStorage.Notes)
                {
                    currentNote = MemoStorage.Notes.Find(n => n.Id == MemoStorage.CurrentNoteId);
                }

                if (currentNote != null)
                {
                    if (!currentNote.Tags.Contains(text, StringComparer.OrdinalIgnoreCase))
                    {
                        currentNote.Tags.Add(text);
                        MemoStorage.SaveMetadata();
                        UpdateHeaderTags();
                        RefreshAllTagsViews();
                    }
                    NewTagTextBox.Text = string.Empty;
                    PopulateManageTagsList();
                }
            }
        }
    }
}
