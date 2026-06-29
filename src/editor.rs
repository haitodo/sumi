use std::ops::Range;
use std::path::PathBuf;

use gpui::{
    actions, div, fill, hsla, point, prelude::*, px, relative, rgba, size, App, Bounds,
    ClipboardItem, Context, CursorStyle, ElementId, ElementInputHandler, Entity,
    EntityInputHandler, FocusHandle, Focusable, GlobalElementId, LayoutId, MouseButton,
    MouseDownEvent, MouseMoveEvent, MouseUpEvent, PaintQuad, Pixels, Point, ShapedLine,
    SharedString, Style, TextRun, UTF16Selection, UnderlineStyle, Window,
};
use unicode_segmentation::UnicodeSegmentation;

use crate::atomic_save::save_text_atomically;

actions!(
    text_input,
    [
        Backspace,
        Delete,
        Left,
        Right,
        Up,
        Down,
        SelectLeft,
        SelectRight,
        SelectUp,
        SelectDown,
        SelectAll,
        Home,
        End,
        ShowCharacterPalette,
        Paste,
        Cut,
        Copy,
        Enter,
        Tab,
    ]
);

#[derive(Clone, Debug)]
pub struct LineInfo {
    pub range: Range<usize>, // 全体テキスト内におけるバイト範囲 (末尾改行は含まない)
    pub text: String,        // 行のテキスト
}

pub struct TextInput {
    pub focus_handle: FocusHandle,
    pub content: SharedString,
    pub placeholder: SharedString,
    pub selected_range: Range<usize>,
    pub selection_reversed: bool,
    pub marked_range: Option<Range<usize>>,
    pub last_layouts: Option<Vec<ShapedLine>>,
    pub last_bounds: Option<Bounds<Pixels>>,
    pub is_selecting: bool,
    pub line_height: Pixels,

    // ファイル保存用設定
    pub memo_path: PathBuf,
    pub tmp_path: PathBuf,
    pub bak_path: PathBuf,
    pub save_task: Option<gpui::Task<()>>,
}

fn offset_from_utf16_in_str(text: &str, offset: usize) -> usize {
    let mut utf8_offset = 0;
    let mut utf16_count = 0;

    for ch in text.chars() {
        if utf16_count >= offset {
            break;
        }
        utf16_count += ch.len_utf16();
        utf8_offset += ch.len_utf8();
    }

    utf8_offset
}

impl TextInput {
    pub fn new(
        initial_text: String,
        memo_path: PathBuf,
        tmp_path: PathBuf,
        bak_path: PathBuf,
        cx: &mut Context<Self>,
    ) -> Self {
        Self {
            focus_handle: cx.focus_handle(),
            content: initial_text.into(),
            placeholder: "文字を入力してください...".into(),
            selected_range: 0..0,
            selection_reversed: false,
            marked_range: None,
            last_layouts: None,
            last_bounds: None,
            is_selecting: false,
            line_height: px(20.0), // デフォルト行高さ
            memo_path,
            tmp_path,
            bak_path,
            save_task: None,
        }
    }

    /// デバウンスを伴ってアトミックに保存を実行する
    pub fn trigger_save(&mut self, cx: &mut Context<Self>) {
        // 既存の保存タスクをキャンセル
        self.save_task.take();

        let content_str = self.content.to_string();
        let memo = self.memo_path.clone();
        let tmp = self.tmp_path.clone();
        let bak = self.bak_path.clone();

        let executor = cx.background_executor().clone();
        self.save_task = Some(cx.background_executor().spawn(async move {
            executor
                .timer(std::time::Duration::from_millis(500))
                .await;
            if let Err(e) = save_text_atomically(&content_str, &memo, &tmp, &bak) {
                eprintln!("自動保存中にエラーが発生しました: {:?}", e);
            }
        }));
    }

    /// デバウンスを無視して即座に同期保存を実行する
    pub fn save_immediately(&mut self) {
        // 保存タスクをクリア
        self.save_task.take();
        let content_str = self.content.to_string();
        let _ = save_text_atomically(&content_str, &self.memo_path, &self.tmp_path, &self.bak_path);
    }

    pub fn grapheme_count(&self) -> usize {
        self.content.graphemes(true).count()
    }

    pub fn get_lines_info(&self) -> Vec<LineInfo> {
        let mut lines = Vec::new();
        let mut start = 0;
        let content_str = self.content.as_ref();
        for (idx, ch) in content_str.char_indices() {
            if ch == '\n' {
                lines.push(LineInfo {
                    range: start..idx,
                    text: content_str[start..idx].to_string(),
                });
                start = idx + 1;
            }
        }
        lines.push(LineInfo {
            range: start..content_str.len(),
            text: content_str[start..content_str.len()].to_string(),
        });
        lines
    }

    fn left(&mut self, _: &Left, _: &mut Window, cx: &mut Context<Self>) {
        if self.marked_range.is_some() {
            return;
        }
        if self.selected_range.is_empty() {
            self.move_to(self.previous_boundary(self.cursor_offset()), cx);
        } else {
            self.move_to(self.selected_range.start, cx);
        }
    }

    fn right(&mut self, _: &Right, _: &mut Window, cx: &mut Context<Self>) {
        if self.marked_range.is_some() {
            return;
        }
        if self.selected_range.is_empty() {
            self.move_to(self.next_boundary(self.selected_range.end), cx);
        } else {
            self.move_to(self.selected_range.end, cx);
        }
    }

    fn up(&mut self, _: &Up, _: &mut Window, cx: &mut Context<Self>) {
        if self.marked_range.is_some() {
            return;
        }
        let lines = self.get_lines_info();
        let cursor = self.cursor_offset();
        let current_line_idx = lines
            .iter()
            .position(|r| cursor >= r.range.start && cursor <= r.range.end)
            .unwrap_or(0);

        if current_line_idx > 0 {
            let prev_line_idx = current_line_idx - 1;
            let mut target_local_offset = None;
            if let Some(layouts) = &self.last_layouts {
                if current_line_idx < layouts.len() && prev_line_idx < layouts.len() {
                    let current_layout = &layouts[current_line_idx];
                    let local_cursor = cursor - lines[current_line_idx].range.start;
                    let cursor_x = current_layout.x_for_index(local_cursor);
                    let prev_layout = &layouts[prev_line_idx];
                    target_local_offset = Some(prev_layout.closest_index_for_x(cursor_x));
                }
            }
            let target_local_offset = target_local_offset.unwrap_or_else(|| {
                let local_cursor = cursor - lines[current_line_idx].range.start;
                local_cursor.min(lines[prev_line_idx].range.end - lines[prev_line_idx].range.start)
            });
            self.move_to(lines[prev_line_idx].range.start + target_local_offset, cx);
        }
    }

    fn down(&mut self, _: &Down, _: &mut Window, cx: &mut Context<Self>) {
        if self.marked_range.is_some() {
            return;
        }
        let lines = self.get_lines_info();
        let cursor = self.cursor_offset();
        let current_line_idx = lines
            .iter()
            .position(|r| cursor >= r.range.start && cursor <= r.range.end)
            .unwrap_or(0);

        if current_line_idx + 1 < lines.len() {
            let next_line_idx = current_line_idx + 1;
            let mut target_local_offset = None;
            if let Some(layouts) = &self.last_layouts {
                if current_line_idx < layouts.len() && next_line_idx < layouts.len() {
                    let current_layout = &layouts[current_line_idx];
                    let local_cursor = cursor - lines[current_line_idx].range.start;
                    let cursor_x = current_layout.x_for_index(local_cursor);
                    let next_layout = &layouts[next_line_idx];
                    target_local_offset = Some(next_layout.closest_index_for_x(cursor_x));
                }
            }
            let target_local_offset = target_local_offset.unwrap_or_else(|| {
                let local_cursor = cursor - lines[current_line_idx].range.start;
                local_cursor.min(lines[next_line_idx].range.end - lines[next_line_idx].range.start)
            });
            self.move_to(lines[next_line_idx].range.start + target_local_offset, cx);
        }
    }

    fn select_left(&mut self, _: &SelectLeft, _: &mut Window, cx: &mut Context<Self>) {
        if self.marked_range.is_some() {
            return;
        }
        self.select_to(self.previous_boundary(self.cursor_offset()), cx);
    }

    fn select_right(&mut self, _: &SelectRight, _: &mut Window, cx: &mut Context<Self>) {
        if self.marked_range.is_some() {
            return;
        }
        self.select_to(self.next_boundary(self.cursor_offset()), cx);
    }

    fn select_up(&mut self, _: &SelectUp, _: &mut Window, cx: &mut Context<Self>) {
        if self.marked_range.is_some() {
            return;
        }
        let lines = self.get_lines_info();
        let cursor = self.cursor_offset();
        let current_line_idx = lines
            .iter()
            .position(|r| cursor >= r.range.start && cursor <= r.range.end)
            .unwrap_or(0);

        if current_line_idx > 0 {
            let prev_line_idx = current_line_idx - 1;
            let mut target_local_offset = None;
            if let Some(layouts) = &self.last_layouts {
                if current_line_idx < layouts.len() && prev_line_idx < layouts.len() {
                    let current_layout = &layouts[current_line_idx];
                    let local_cursor = cursor - lines[current_line_idx].range.start;
                    let cursor_x = current_layout.x_for_index(local_cursor);
                    let prev_layout = &layouts[prev_line_idx];
                    target_local_offset = Some(prev_layout.closest_index_for_x(cursor_x));
                }
            }
            let target_local_offset = target_local_offset.unwrap_or_else(|| {
                let local_cursor = cursor - lines[current_line_idx].range.start;
                local_cursor.min(lines[prev_line_idx].range.end - lines[prev_line_idx].range.start)
            });
            self.select_to(lines[prev_line_idx].range.start + target_local_offset, cx);
        }
    }

    fn select_down(&mut self, _: &SelectDown, _: &mut Window, cx: &mut Context<Self>) {
        if self.marked_range.is_some() {
            return;
        }
        let lines = self.get_lines_info();
        let cursor = self.cursor_offset();
        let current_line_idx = lines
            .iter()
            .position(|r| cursor >= r.range.start && cursor <= r.range.end)
            .unwrap_or(0);

        if current_line_idx + 1 < lines.len() {
            let next_line_idx = current_line_idx + 1;
            let mut target_local_offset = None;
            if let Some(layouts) = &self.last_layouts {
                if current_line_idx < layouts.len() && next_line_idx < layouts.len() {
                    let current_layout = &layouts[current_line_idx];
                    let local_cursor = cursor - lines[current_line_idx].range.start;
                    let cursor_x = current_layout.x_for_index(local_cursor);
                    let next_layout = &layouts[next_line_idx];
                    target_local_offset = Some(next_layout.closest_index_for_x(cursor_x));
                }
            }
            let target_local_offset = target_local_offset.unwrap_or_else(|| {
                let local_cursor = cursor - lines[current_line_idx].range.start;
                local_cursor.min(lines[next_line_idx].range.end - lines[next_line_idx].range.start)
            });
            self.select_to(lines[next_line_idx].range.start + target_local_offset, cx);
        }
    }

    fn select_all(&mut self, _: &SelectAll, _: &mut Window, cx: &mut Context<Self>) {
        if self.marked_range.is_some() {
            return;
        }
        self.move_to(0, cx);
        self.select_to(self.content.len(), cx);
    }

    fn home(&mut self, _: &Home, _: &mut Window, cx: &mut Context<Self>) {
        if self.marked_range.is_some() {
            return;
        }
        let lines = self.get_lines_info();
        let cursor = self.cursor_offset();
        let current_line_idx = lines
            .iter()
            .position(|r| cursor >= r.range.start && cursor <= r.range.end)
            .unwrap_or(0);
        self.move_to(lines[current_line_idx].range.start, cx);
    }

    fn end(&mut self, _: &End, _: &mut Window, cx: &mut Context<Self>) {
        if self.marked_range.is_some() {
            return;
        }
        let lines = self.get_lines_info();
        let cursor = self.cursor_offset();
        let current_line_idx = lines
            .iter()
            .position(|r| cursor >= r.range.start && cursor <= r.range.end)
            .unwrap_or(0);
        self.move_to(lines[current_line_idx].range.end, cx);
    }

    fn backspace(&mut self, _: &Backspace, window: &mut Window, cx: &mut Context<Self>) {
        if self.marked_range.is_some() {
            return;
        }
        if self.selected_range.is_empty() {
            let prev = self.previous_boundary(self.cursor_offset());
            if self.cursor_offset() == prev {
                return;
            }
            self.select_to(prev, cx);
        }
        self.replace_text_in_range(None, "", window, cx);
    }

    fn delete(&mut self, _: &Delete, window: &mut Window, cx: &mut Context<Self>) {
        if self.marked_range.is_some() {
            return;
        }
        if self.selected_range.is_empty() {
            let next = self.next_boundary(self.cursor_offset());
            if self.cursor_offset() == next {
                return;
            }
            self.select_to(next, cx);
        }
        self.replace_text_in_range(None, "", window, cx);
    }

    fn enter(&mut self, _: &Enter, window: &mut Window, cx: &mut Context<Self>) {
        if self.marked_range.is_some() {
            return;
        }
        self.replace_text_in_range(None, "\n", window, cx);
    }

    fn tab(&mut self, _: &Tab, window: &mut Window, cx: &mut Context<Self>) {
        if self.marked_range.is_some() {
            return;
        }
        self.replace_text_in_range(None, "    ", window, cx);
    }

    fn on_mouse_down(
        &mut self,
        event: &MouseDownEvent,
        _window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.is_selecting = true;

        if event.modifiers.shift {
            self.select_to(self.index_for_mouse_position(event.position), cx);
        } else {
            self.move_to(self.index_for_mouse_position(event.position), cx);
        }
    }

    fn on_mouse_up(&mut self, _: &MouseUpEvent, _window: &mut Window, _: &mut Context<Self>) {
        self.is_selecting = false;
    }

    fn on_mouse_move(&mut self, event: &MouseMoveEvent, _: &mut Window, cx: &mut Context<Self>) {
        if self.is_selecting {
            self.select_to(self.index_for_mouse_position(event.position), cx);
        }
    }

    fn show_character_palette(
        &mut self,
        _: &ShowCharacterPalette,
        window: &mut Window,
        _: &mut Context<Self>,
    ) {
        if self.marked_range.is_some() {
            return;
        }
        window.show_character_palette();
    }

    fn paste(&mut self, _: &Paste, window: &mut Window, cx: &mut Context<Self>) {
        if self.marked_range.is_some() {
            return;
        }
        if let Some(text) = cx.read_from_clipboard().and_then(|item| item.text()) {
            // 改行も含めてペーストできるようにする
            self.replace_text_in_range(None, &text, window, cx);
        }
    }

    fn copy(&mut self, _: &Copy, _: &mut Window, cx: &mut Context<Self>) {
        if self.marked_range.is_some() {
            return;
        }
        if !self.selected_range.is_empty() {
            cx.write_to_clipboard(ClipboardItem::new_string(
                self.content[self.selected_range.clone()].to_string(),
            ));
        }
    }

    fn cut(&mut self, _: &Cut, window: &mut Window, cx: &mut Context<Self>) {
        if self.marked_range.is_some() {
            return;
        }
        if !self.selected_range.is_empty() {
            cx.write_to_clipboard(ClipboardItem::new_string(
                self.content[self.selected_range.clone()].to_string(),
            ));
            self.replace_text_in_range(None, "", window, cx);
        }
    }

    fn move_to(&mut self, offset: usize, cx: &mut Context<Self>) {
        self.selected_range = offset..offset;
        cx.notify();
    }

    fn cursor_offset(&self) -> usize {
        if self.selection_reversed {
            self.selected_range.start
        } else {
            self.selected_range.end
        }
    }

    fn index_for_mouse_position(&self, position: Point<Pixels>) -> usize {
        if self.content.is_empty() {
            return 0;
        }

        let (Some(bounds), Some(lines_layout)) =
            (self.last_bounds.as_ref(), self.last_layouts.as_ref())
        else {
            return 0;
        };

        if position.y < bounds.top() {
            return 0;
        }

        let relative_y = position.y - bounds.top();
        let mut line_idx = (relative_y / self.line_height).floor() as usize;
        let lines_info = self.get_lines_info();

        if line_idx >= lines_info.len() {
            line_idx = lines_info.len() - 1;
        }

        let line_info = &lines_info[line_idx];
        let layout = &lines_layout[line_idx];

        let x = position.x - bounds.left();
        let local_offset = layout.closest_index_for_x(x);

        line_info.range.start + local_offset
    }

    fn select_to(&mut self, offset: usize, cx: &mut Context<Self>) {
        if self.selection_reversed {
            self.selected_range.start = offset;
        } else {
            self.selected_range.end = offset;
        }
        if self.selected_range.end < self.selected_range.start {
            self.selection_reversed = !self.selection_reversed;
            self.selected_range = self.selected_range.end..self.selected_range.start;
        }
        cx.notify();
    }

    fn offset_from_utf16(&self, offset: usize) -> usize {
        offset_from_utf16_in_str(&self.content, offset)
    }

    fn offset_to_utf16(&self, offset: usize) -> usize {
        let mut utf16_offset = 0;
        let mut utf8_count = 0;

        for ch in self.content.chars() {
            if utf8_count >= offset {
                break;
            }
            utf8_count += ch.len_utf8();
            utf16_offset += ch.len_utf16();
        }

        utf16_offset
    }

    fn range_to_utf16(&self, range: &Range<usize>) -> Range<usize> {
        self.offset_to_utf16(range.start)..self.offset_to_utf16(range.end)
    }

    fn range_from_utf16(&self, range_utf16: &Range<usize>) -> Range<usize> {
        self.offset_from_utf16(range_utf16.start)..self.offset_from_utf16(range_utf16.end)
    }

    fn previous_boundary(&self, offset: usize) -> usize {
        self.content
            .grapheme_indices(true)
            .rev()
            .find_map(|(idx, _)| (idx < offset).then_some(idx))
            .unwrap_or(0)
    }

    fn next_boundary(&self, offset: usize) -> usize {
        self.content
            .grapheme_indices(true)
            .find_map(|(idx, _)| (idx > offset).then_some(idx))
            .unwrap_or(self.content.len())
    }

    pub fn reset(&mut self, cx: &mut Context<Self>) {
        self.content = "".into();
        self.selected_range = 0..0;
        self.selection_reversed = false;
        self.marked_range = None;
        self.last_layouts = None;
        self.last_bounds = None;
        self.is_selecting = false;
        self.trigger_save(cx);
        cx.notify();
    }
}

impl EntityInputHandler for TextInput {
    fn text_for_range(
        &mut self,
        range_utf16: Range<usize>,
        actual_range: &mut Option<Range<usize>>,
        _window: &mut Window,
        _cx: &mut Context<Self>,
    ) -> Option<String> {
        let range = self.range_from_utf16(&range_utf16);
        actual_range.replace(self.range_to_utf16(&range));
        Some(self.content[range].to_string())
    }

    fn selected_text_range(
        &mut self,
        _ignore_disabled_input: bool,
        _window: &mut Window,
        _cx: &mut Context<Self>,
    ) -> Option<UTF16Selection> {
        Some(UTF16Selection {
            range: self.range_to_utf16(&self.selected_range),
            reversed: self.selection_reversed,
        })
    }

    fn marked_text_range(
        &self,
        _window: &mut Window,
        _cx: &mut Context<Self>,
    ) -> Option<Range<usize>> {
        self.marked_range
            .as_ref()
            .map(|range| self.range_to_utf16(range))
    }

    fn unmark_text(&mut self, _window: &mut Window, _cx: &mut Context<Self>) {
        self.marked_range = None;
    }

    fn replace_text_in_range(
        &mut self,
        range_utf16: Option<Range<usize>>,
        new_text: &str,
        _: &mut Window,
        cx: &mut Context<Self>,
    ) {
        println!("[DEBUG] replace_text_in_range: range_utf16={:?}, new_text={:?}, marked_range={:?}, selected_range={:?}", range_utf16, new_text, self.marked_range, self.selected_range);
        let range = range_utf16
            .as_ref()
            .map(|range_utf16| self.range_from_utf16(range_utf16))
            .or(self.marked_range.clone())
            .unwrap_or(self.selected_range.clone());

        self.content = (self.content[0..range.start].to_owned()
            + new_text
            + &self.content[range.end..])
            .into();
        self.selected_range = range.start + new_text.len()..range.start + new_text.len();
        self.marked_range.take();
        self.trigger_save(cx);
        cx.notify();
    }

    fn replace_and_mark_text_in_range(
        &mut self,
        range_utf16: Option<Range<usize>>,
        new_text: &str,
        new_selected_range_utf16: Option<Range<usize>>,
        _window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        println!("[DEBUG] replace_and_mark_text_in_range: range_utf16={:?}, new_text={:?}, new_selected_range_utf16={:?}, marked_range={:?}, selected_range={:?}", range_utf16, new_text, new_selected_range_utf16, self.marked_range, self.selected_range);
        let range = range_utf16
            .as_ref()
            .map(|range_utf16| self.range_from_utf16(range_utf16))
            .or(self.marked_range.clone())
            .unwrap_or(self.selected_range.clone());

        self.content = (self.content[0..range.start].to_owned()
            + new_text
            + &self.content[range.end..])
            .into();
        if !new_text.is_empty() {
            self.marked_range = Some(range.start..range.start + new_text.len());
        } else {
            self.marked_range = None;
        }

        self.selected_range = new_selected_range_utf16
            .as_ref()
            .map(|range_utf16| {
                let start = offset_from_utf16_in_str(new_text, range_utf16.start) + range.start;
                let end = offset_from_utf16_in_str(new_text, range_utf16.end) + range.start;
                start..end
            })
            .unwrap_or_else(|| range.start + new_text.len()..range.start + new_text.len());

        self.trigger_save(cx);
        cx.notify();
    }

    fn bounds_for_range(
        &mut self,
        range_utf16: Range<usize>,
        bounds: Bounds<Pixels>,
        _window: &mut Window,
        _cx: &mut Context<Self>,
    ) -> Option<Bounds<Pixels>> {
        let layouts = self.last_layouts.as_ref()?;
        let range = self.range_from_utf16(&range_utf16);
        let lines = self.get_lines_info();

        let start_line_idx = lines
            .iter()
            .position(|r| range.start >= r.range.start && range.start <= r.range.end)
            .unwrap_or(0);
        let end_line_idx = lines
            .iter()
            .position(|r| range.end >= r.range.start && range.end <= r.range.end)
            .unwrap_or(start_line_idx);

        if start_line_idx < layouts.len() && end_line_idx < layouts.len() {
            let start_layout = &layouts[start_line_idx];
            let local_start = range.start - lines[start_line_idx].range.start;
            let start_x = start_layout.x_for_index(local_start);
            let start_y = bounds.top() + (start_line_idx as f32) * self.line_height;

            let end_layout = &layouts[end_line_idx];
            let local_end = range.end - lines[end_line_idx].range.start;
            let end_x = end_layout.x_for_index(local_end);
            let end_y = bounds.top() + (end_line_idx as f32) * self.line_height + self.line_height;

            Some(Bounds::from_corners(
                point(bounds.left() + start_x, start_y),
                point(bounds.left() + end_x, end_y),
            ))
        } else {
            None
        }
    }

    fn character_index_for_point(
        &mut self,
        point: gpui::Point<Pixels>,
        _window: &mut Window,
        _cx: &mut Context<Self>,
    ) -> Option<usize> {
        let bounds = self.last_bounds.as_ref()?;
        if !bounds.contains(&point) {
            return None;
        }
        let layouts = self.last_layouts.as_ref()?;
        let lines = self.get_lines_info();

        let relative_y = point.y - bounds.top();
        let line_idx = ((relative_y / self.line_height).floor() as usize).min(lines.len() - 1);

        if line_idx < layouts.len() {
            let layout = &layouts[line_idx];
            let line_info = &lines[line_idx];
            let local_x = point.x - bounds.left();
            let local_offset = layout.closest_index_for_x(local_x);
            Some(self.offset_to_utf16(line_info.range.start + local_offset))
        } else {
            None
        }
    }
}

struct TextElement {
    input: Entity<TextInput>,
}

struct PrepaintState {
    lines: Vec<ShapedLine>,
    cursor_quad: Option<PaintQuad>,
    selection_quads: Vec<PaintQuad>,
}

impl IntoElement for TextElement {
    type Element = Self;

    fn into_element(self) -> Self::Element {
        self
    }
}

impl Element for TextElement {
    type RequestLayoutState = ();
    type PrepaintState = PrepaintState;

    fn id(&self) -> Option<ElementId> {
        None
    }

    fn source_location(&self) -> Option<&'static core::panic::Location<'static>> {
        None
    }

    fn request_layout(
        &mut self,
        _id: Option<&GlobalElementId>,
        _inspector_id: Option<&gpui::InspectorElementId>,
        window: &mut Window,
        cx: &mut App,
    ) -> (LayoutId, Self::RequestLayoutState) {
        let mut style = Style::default();
        style.size.width = relative(1.).into();

        let input = self.input.read(cx);
        let lines = input.get_lines_info();
        let line_count = lines.len().max(1);
        let height = input.line_height * (line_count as f32);

        style.size.height = height.into();
        (window.request_layout(style, [], cx), ())
    }

    fn prepaint(
        &mut self,
        _id: Option<&GlobalElementId>,
        _inspector_id: Option<&gpui::InspectorElementId>,
        bounds: Bounds<Pixels>,
        _request_layout: &mut Self::RequestLayoutState,
        window: &mut Window,
        cx: &mut App,
    ) -> Self::PrepaintState {
        let input = self.input.read(cx);
        let content = input.content.clone();
        let selected_range = input.selected_range.clone();
        let cursor = input.cursor_offset();
        let style = window.text_style();
        let font_size = style.font_size.to_pixels(window.rem_size());
        let line_height = input.line_height;

        let lines_info = input.get_lines_info();
        let mut shaped_lines = Vec::new();
        let mut cursor_quad = None;
        let mut selection_quads = Vec::new();

        let focus_handle = input.focus_handle.clone();

        for (line_idx, line_info) in lines_info.iter().enumerate() {
            let line_top = bounds.top() + (line_idx as f32) * line_height;
            let line_bottom = line_top + line_height;

            let line_text = &line_info.text;
            let is_empty = content.is_empty();
            let display_text = if is_empty {
                input.placeholder.clone()
            } else {
                SharedString::from(line_text.clone())
            };

            let text_color = if is_empty {
                hsla(0.0, 0.0, 0.0, 0.3)
            } else {
                style.color
            };

            let run = TextRun {
                len: display_text.len(),
                font: style.font(),
                color: text_color,
                background_color: None,
                underline: None,
                strikethrough: None,
            };

            let mut runs = Vec::new();
            if let Some(m_range) = input.marked_range.as_ref() {
                let overlap_start = m_range
                    .start
                    .max(line_info.range.start)
                    .min(line_info.range.end);
                let overlap_end = m_range
                    .end
                    .max(line_info.range.start)
                    .min(line_info.range.end);
                if overlap_start < overlap_end {
                    let local_start = overlap_start - line_info.range.start;
                    let local_end = overlap_end - line_info.range.start;
                    if local_start > 0 {
                        runs.push(TextRun {
                            len: local_start,
                            ..run.clone()
                        });
                    }
                    runs.push(TextRun {
                        len: local_end - local_start,
                        underline: Some(UnderlineStyle {
                            color: Some(run.color),
                            thickness: px(1.0),
                            wavy: false,
                        }),
                        ..run.clone()
                    });
                    if local_end < line_text.len() {
                        runs.push(TextRun {
                            len: line_text.len() - local_end,
                            ..run.clone()
                        });
                    }
                } else {
                    runs.push(run);
                }
            } else {
                runs.push(run);
            }

            let line = window
                .text_system()
                .shape_line(display_text, font_size, &runs, None);

            // カーソルの描画判定
            if focus_handle.is_focused(window)
                && cursor >= line_info.range.start
                && cursor <= line_info.range.end
            {
                let local_cursor = cursor - line_info.range.start;
                let cursor_pos = line.x_for_index(local_cursor);
                cursor_quad = Some(fill(
                    Bounds::new(
                        point(bounds.left() + cursor_pos, line_top),
                        size(px(2.), line_height),
                    ),
                    gpui::blue(),
                ));
            }

            // 選択領域の描画判定
            let overlap_start = selected_range
                .start
                .max(line_info.range.start)
                .min(line_info.range.end + 1);
            let overlap_end = selected_range
                .end
                .max(line_info.range.start)
                .min(line_info.range.end + 1);
            if overlap_start < overlap_end {
                let local_start = overlap_start - line_info.range.start;
                let local_end = overlap_end - line_info.range.start;
                let start_x = line.x_for_index(local_start.min(line_text.len()));
                let mut end_x = line.x_for_index(local_end.min(line_text.len()));
                if local_end > line_text.len() {
                    end_x += px(8.0);
                }
                selection_quads.push(fill(
                    Bounds::from_corners(
                        point(bounds.left() + start_x, line_top),
                        point(bounds.left() + end_x, line_bottom),
                    ),
                    rgba(0x3311ff30),
                ));
            }

            shaped_lines.push(line);
        }

        PrepaintState {
            lines: shaped_lines,
            cursor_quad,
            selection_quads,
        }
    }

    fn paint(
        &mut self,
        _id: Option<&GlobalElementId>,
        _inspector_id: Option<&gpui::InspectorElementId>,
        bounds: Bounds<Pixels>,
        _request_layout: &mut Self::RequestLayoutState,
        prepaint: &mut Self::PrepaintState,
        window: &mut Window,
        cx: &mut App,
    ) {
        let focus_handle = self.input.read(cx).focus_handle.clone();
        window.handle_input(
            &focus_handle,
            ElementInputHandler::new(bounds, self.input.clone()),
            cx,
        );

        // 選択背景を描画
        for sel in prepaint.selection_quads.drain(..) {
            window.paint_quad(sel);
        }

        // テキストを描画
        let input = self.input.read(cx);
        let line_height = input.line_height;

        for (line_idx, line) in prepaint.lines.iter().enumerate() {
            let line_top = bounds.top() + (line_idx as f32) * line_height;
            line.paint(
                point(bounds.left(), line_top),
                line_height,
                window,
                cx,
            )
            .unwrap();
        }

        // カーソルを描画
        if let Some(cursor) = prepaint.cursor_quad.take() {
            window.paint_quad(cursor);
        }

        let lines_clone = prepaint.lines.clone();
        self.input.update(cx, |input_state, _cx| {
            input_state.last_layouts = Some(lines_clone);
            input_state.last_bounds = Some(bounds);
        });
    }
}

impl Render for TextInput {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        div()
            .flex()
            .key_context("TextInput")
            .track_focus(&self.focus_handle(cx))
            .cursor(CursorStyle::IBeam)
            .on_action(cx.listener(Self::backspace))
            .on_action(cx.listener(Self::delete))
            .on_action(cx.listener(Self::left))
            .on_action(cx.listener(Self::right))
            .on_action(cx.listener(Self::up))
            .on_action(cx.listener(Self::down))
            .on_action(cx.listener(Self::select_left))
            .on_action(cx.listener(Self::select_right))
            .on_action(cx.listener(Self::select_up))
            .on_action(cx.listener(Self::select_down))
            .on_action(cx.listener(Self::select_all))
            .on_action(cx.listener(Self::home))
            .on_action(cx.listener(Self::end))
            .on_action(cx.listener(Self::show_character_palette))
            .on_action(cx.listener(Self::paste))
            .on_action(cx.listener(Self::cut))
            .on_action(cx.listener(Self::copy))
            .on_action(cx.listener(Self::enter))
            .on_action(cx.listener(Self::tab))
            .on_mouse_down(MouseButton::Left, cx.listener(Self::on_mouse_down))
            .on_mouse_up(MouseButton::Left, cx.listener(Self::on_mouse_up))
            .on_mouse_up_out(MouseButton::Left, cx.listener(Self::on_mouse_up))
            .on_mouse_move(cx.listener(Self::on_mouse_move))
            .size_full()
            .child(TextElement { input: cx.entity() })
    }
}

impl Focusable for TextInput {
    fn focus_handle(&self, _: &App) -> FocusHandle {
        self.focus_handle.clone()
    }
}

impl Drop for TextInput {
    fn drop(&mut self) {
        self.save_immediately();
    }
}

