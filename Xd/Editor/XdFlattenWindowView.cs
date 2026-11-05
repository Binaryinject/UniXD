using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using BIEngine.UniXD.Xd.Parsing.Internal;

namespace BIEngine.UniXD.Xd
{
    public sealed partial class XdFlattenWindow
    {
        private sealed class EntryView
        {
            internal Entry entry;
            internal bool chosen, covered, inheritedOmission, omitted, vectorEligible, inheritedVector, fromChild;
            internal string manual, exported, path, exportLabel, componentLabel, status;
            internal GUIContent title;
            internal GUIContent pathContent, exportContent, componentContent;
            internal string renameControl;
            internal float height;
        }

        private readonly Dictionary<string, XdObjectJson> m_ResolvedNodes = new Dictionary<string, XdObjectJson>();
        private readonly Dictionary<string, bool> m_FlattenAllowed = new Dictionary<string, bool>();
        private readonly Dictionary<string, bool> m_VectorEligible = new Dictionary<string, bool>();
        private readonly Dictionary<string, bool> m_UsesVector = new Dictionary<string, bool>();
        private readonly List<EntryView> m_Views = new List<EntryView>();
        private readonly List<EntryView> m_Visible = new List<EntryView>();
        private readonly List<float> m_RowOffsets = new List<float>();
        private bool m_DisplayDirty = true, m_VisibleDirty = true;
        private string m_LastSearch, m_BoardLabel, m_Summary, m_FooterLabel;
        private string m_RangeAnchorId;
        private GUIStyle m_ContentStyle;
        private GUIStyle m_RowMutedStyle;
        private bool m_RowStyleDark;
        private int m_DisplayBuildCount, m_VisibleBuildCount, m_LastDrawnRows;
        private static float lineHeight => EditorGUIUtility.singleLineHeight + 2f;
        private static readonly GUIContent m_OmitLabel = new GUIContent("不导出", "跳过此节点及其子节点。");
        private static readonly GUIContent m_RenderLabel = new GUIContent("渲染方式", "纯矢量可保留为 SVGImage，软阴影独立输出。");
        private static readonly GUIContent m_ScrollLabel = new GUIContent("滚动视图", "仅影响此分组。");

        private void InvalidateDisplay() { m_DisplayDirty = true; m_VisibleDirty = true; }
        private void InvalidateVisible() { m_VisibleDirty = true; }

        private bool CanToggleMerge(Entry entry) => _previewIds.Contains(entry.Id) &&
            (entry.Supported || IsChosen(entry.Id)) && !_omitted.Contains(entry.Id) &&
            (!m_FlattenAllowed.TryGetValue(entry.Id, out var allowed) || allowed) &&
            !entry.Ancestors.Any(_omitted.Contains) && !entry.Ancestors.Any(IsChosen);

        private void ApplyMergeSelection(Entry entry, bool value, bool range)
        {
            EnsureVisible();
            var target = m_Visible.FindIndex(x => x.entry.Id == entry.Id);
            var anchor = range && m_RangeAnchorId != null ? m_Visible.FindIndex(x => x.entry.Id == m_RangeAnchorId) : -1;
            if (anchor < 0 || target < 0)
            {
                m_RangeAnchorId = entry.Id;
                SetChosen(entry, value);
                return;
            }
            for (var i = Math.Min(anchor, target); i <= Math.Max(anchor, target); i++)
            {
                var view = m_Visible[i];
                if (view.covered || view.omitted) continue;
                var item = view.entry;
                if (!CanToggleMerge(item)) continue;
                if (value) { _selected.Add(item.Id); _excluded.Remove(item.Id); _collapsed.Add(item.Id); }
                else { _selected.Remove(item.Id); _excluded.Add(item.Id); _collapsed.Remove(item.Id); }
            }
            XdFlattenHistory.Save(_file, _board, _selected);
            XdFlattenHistory.SaveExcluded(_file, _board, _excluded);
            InvalidateDisplay();
        }

        private void OnFocus()
        {
            if (_roots != null && _entries != null) RefreshExportPreview();
        }

        private void EnsureDisplay()
        {
            if (!m_DisplayDirty) return;
            m_DisplayBuildCount++;
            Dictionary<string, string> names;
            try { names = XdExportRules.BuildLayerNames(_previewRoots, _settings, _names, _previewSymbols, m_ScrollViewModes); }
            catch (BIEngine.UniXD.Editor.ImportException) { names = new Dictionary<string, string>(); }
            var sourceNames = _entries.ToDictionary(x => x.Id, x => x.Name);
            m_Views.Clear();
            var selectedCount = 0;
            var omittedCount = 0;
            foreach (var entry in _entries)
            {
                var view = new EntryView { entry = entry, chosen = IsChosen(entry.Id), covered = entry.Ancestors.Any(IsChosen),
                    inheritedOmission = entry.Ancestors.Any(_omitted.Contains), inheritedVector = entry.Ancestors.Any(m_VectorLayers.Contains) };
                view.omitted = view.inheritedOmission || _omitted.Contains(entry.Id);
                _names.TryGetValue(entry.Id, out view.manual);
                XdExportRules.TryGetFinalLayerName(names, entry.Id, out view.exported);
                view.exported = view.exported ?? Preview(entry.Name.Split('@')[0], view.manual);
                view.path = string.Join(" / ", entry.Ancestors.Select(id => sourceNames.TryGetValue(id, out var name) ? name : id).Concat(new[] { entry.Name }));
                view.title = new GUIContent(entry.Name + (view.chosen && !_selected.Contains(entry.Id) ? "  [自动合图]" : ""), view.path);
                view.pathContent = new GUIContent(view.path, view.path);
                view.renameControl = "XdLayerName_" + entry.Id;
                view.vectorEligible = m_VectorEligible.TryGetValue(entry.Id, out var eligible) && eligible;
                var usesVector = m_UsesVector.TryGetValue(entry.Id, out var vector) && vector;
                view.fromChild = !m_VectorLayers.Contains(entry.Id) && usesVector;
                view.exportLabel = view.omitted ? (view.inheritedOmission ? "父级已标记不导出" : "已标记不导出（含子节点）")
                    : !_previewIds.Contains(entry.Id) ? "无可导出内容" : "导出预览：" + view.exported;
                if (!view.omitted && !view.covered && m_ResolvedNodes.TryGetValue(entry.Id, out var node))
                {
                    m_ScrollViewModes.TryGetValue(entry.Id, out var mode);
                    // Already resolved at configuration changes, never resolve symbol trees on scroll/repaint.
                    view.componentLabel = "组件预览：" + XdComponentPreview.Preview(node, view.chosen, mode, _settings,
                        view.exported, XdExportRules.GetFinalLayerPath(names, entry.Id), null, usesVector || view.inheritedVector);
                }
                view.status = view.covered ? "已包含在父级合图（子项不单独导出）" : !entry.Supported
                    ? entry.IsGroup ? "含不支持的图层，不能合图" : "独立图层，可覆盖名称" : null;
                view.exportContent = new GUIContent(view.exportLabel, view.exportLabel);
                if (view.componentLabel != null) view.componentContent = new GUIContent(view.componentLabel, view.componentLabel);
                if (view.chosen && !view.covered) selectedCount++;
                if (_omitted.Contains(entry.Id) && !view.inheritedOmission) omittedCount++;
                m_Views.Add(view);
            }
            XdArtboardExportNameHistory.Load(_file).TryGetValue(_board, out var boardName);
            m_BoardLabel = "画板：" + _boardOriginal + " → " + Preview(boardName ?? _boardOriginal, null);
            m_Summary = $"已选择 {selectedCount} 个合图分组 · {omittedCount} 项不导出 · 自动保存";
            m_DisplayDirty = false;
            m_VisibleDirty = true;
        }

        private void EnsureVisible()
        {
            EnsureDisplay();
            if (!m_VisibleDirty && string.Equals(m_LastSearch, _search, StringComparison.Ordinal)) return;
            m_VisibleBuildCount++;
            m_LastSearch = _search;
            var query = (_search ?? "").Trim();
            var searching = query.Length > 0;
            m_Visible.Clear(); m_RowOffsets.Clear(); m_RowOffsets.Add(0f);
            foreach (var view in m_Views)
            {
                if (searching)
                {
                    if (view.entry.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0 &&
                        (view.manual == null || view.manual.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) &&
                        view.exported.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                }
                else if (view.entry.Ancestors.Any(_collapsed.Contains)) continue;
                var lines = 4 + (searching ? 1 : 0) + (view.vectorEligible ? 1 : 0) +
                    (view.vectorEligible && (view.inheritedVector || view.fromChild) ? 1 : 0) +
                    (view.entry.IsScrollGroup ? 1 : 0) + (view.componentLabel != null ? 1 : 0) + (view.status != null ? 1 : 0);
                view.height = lines * lineHeight + 12f;
                m_Visible.Add(view);
                m_RowOffsets.Add(m_RowOffsets[m_RowOffsets.Count - 1] + view.height + 4f);
            }
            m_FooterLabel = $"显示 {m_Visible.Count}/{_entries.Count} 项 · {m_Summary}";
            m_VisibleDirty = false;
        }

        private IEnumerable<Entry> VisibleEntries()
        {
            EnsureVisible();
            return m_Visible.Select(x => x.entry);
        }

        private int FirstRowAt(float y)
        {
            var left = 0; var right = m_Visible.Count;
            while (left < right)
            {
                var mid = left + (right - left) / 2;
                if (m_RowOffsets[mid + 1] <= y) left = mid + 1; else right = mid;
            }
            return left;
        }

        private void DrawRows(Rect viewport)
        {
            XdEditorStyles.Ensure();
            if (m_RowMutedStyle == null || m_RowStyleDark != EditorGUIUtility.isProSkin)
            {
                m_RowStyleDark = EditorGUIUtility.isProSkin;
                m_RowMutedStyle = new GUIStyle(XdEditorStyles.Muted) { wordWrap = false, clipping = TextClipping.Clip };
            }
            EnsureVisible();
            // Text is saved as it changes. Do not transfer keyboard focus to a recycled row while scrolling.
            if (Event.current.type == EventType.ScrollWheel && viewport.Contains(Event.current.mousePosition)) GUI.FocusControl(null);
            var width = Mathf.Max(80f, viewport.width - 18f);
            var previousScroll = _scroll;
            _scroll = GUI.BeginScrollView(viewport, _scroll, new Rect(0, 0, width,
                Mathf.Max(viewport.height, m_RowOffsets[m_RowOffsets.Count - 1])));
            if (_scroll != previousScroll) GUI.FocusControl(null);
            var first = Mathf.Max(0, FirstRowAt(_scroll.y) - 1);
            var end = Mathf.Min(m_Visible.Count, FirstRowAt(_scroll.y + viewport.height) + 2);
            m_LastDrawnRows = end - first;
            for (var i = first; i < end; i++) DrawEntry(m_Visible[i], new Rect(0, m_RowOffsets[i], width, m_Visible[i].height));
            if (m_Visible.Count == 0) GUI.Label(new Rect(8, 8, width - 16, lineHeight), _entries.Count == 0 ? "此画板没有可设置的图层。" : "没有匹配的图层。", EditorStyles.miniLabel);
            GUI.EndScrollView();
        }

        private void DrawEntry(EntryView view, Rect box)
        {
            var entry = view.entry;
            GUI.Box(box, GUIContent.none, EditorStyles.helpBox);
            var searching = !string.IsNullOrWhiteSpace(_search);
            var x = box.x + 6f + (searching ? 0f : Mathf.Min(entry.Depth * 14f, 84f));
            var y = box.y + 6f;
            if (entry.HasChildren)
                using (new EditorGUI.DisabledScope(searching))
                {
                    var previous = !_collapsed.Contains(entry.Id);
                    var expanded = EditorGUI.Foldout(new Rect(x, y, 14, lineHeight), searching || previous, GUIContent.none);
                    if (!searching && expanded != previous)
                    {
                        if (expanded) _collapsed.Remove(entry.Id); else _collapsed.Add(entry.Id);
                        GUI.FocusControl(null);
                        InvalidateVisible();
                    }
                }
            x += 16f;
            using (new EditorGUI.DisabledScope(view.omitted || view.covered || !CanToggleMerge(entry)))
            {
                var range = Event.current.shift;
                var chosen = EditorGUI.Toggle(new Rect(x, y, 18, lineHeight), view.chosen);
                if (chosen != view.chosen) ApplyMergeSelection(entry, chosen, range);
            }
            x += 22f;
            var width = Mathf.Max(20f, box.xMax - x - 6f);
            Rect Next() { var rect = new Rect(x, y, width, EditorGUIUtility.singleLineHeight); y += lineHeight; return rect; }
            var titleRect = Next();
            GUI.Label(titleRect, view.title, XdEditorStyles.RowTitle);
            var click = Event.current;
            if (click.type == EventType.MouseDown && click.button == 0 && titleRect.Contains(click.mousePosition) && CanToggleMerge(entry))
            {
                ApplyMergeSelection(entry, !view.chosen, click.shift);
                GUI.FocusControl(null); click.Use(); Repaint();
            }
            if (searching) GUI.Label(Next(), view.pathContent, XdEditorStyles.PathLabel);
            using (new EditorGUI.DisabledScope(view.inheritedOmission))
            {
                var omit = EditorGUI.ToggleLeft(Next(), m_OmitLabel, view.omitted);
                if (omit != view.omitted)
                {
                    if (omit) _omitted.Add(entry.Id); else _omitted.Remove(entry.Id);
                    XdFlattenHistory.SaveOmitted(_file, _board, _omitted); RefreshExportPreview();
                }
            }
            if (view.vectorEligible)
            {
                using (new EditorGUI.DisabledScope(view.omitted || view.inheritedVector || view.fromChild))
                {
                    var previous = m_VectorLayers.Contains(entry.Id) || view.inheritedVector || view.fromChild ? 1 : 0;
                    var mode = EditorGUI.Popup(EditorGUI.PrefixLabel(Next(), m_RenderLabel), previous, m_RenderModeNames);
                    if (mode != previous)
                    {
                        if (mode == 1) m_VectorLayers.Add(entry.Id); else m_VectorLayers.Remove(entry.Id);
                        XdFlattenHistory.SaveVectors(_file, _board, m_VectorLayers); RefreshExportPreview();
                    }
                }
                if (view.inheritedVector || view.fromChild)
                    GUI.Label(Next(), view.inheritedVector ? "沿用父级的矢量渲染设置" : "保留矢量子层：此纯矢量合图将输出 SVG", XdEditorStyles.PathLabel);
            }
            if (entry.IsScrollGroup)
                using (new EditorGUI.DisabledScope(view.omitted || view.covered || view.chosen || !_previewIds.Contains(entry.Id)))
                {
                    m_ScrollViewModes.TryGetValue(entry.Id, out var mode);
                    var value = (XdScrollViewMode)EditorGUI.Popup(EditorGUI.PrefixLabel(Next(), m_ScrollLabel), (int)mode, m_ScrollViewModeNames);
                    if (value != mode) SetScrollViewMode(entry, value);
                }
            var renameRect = Next();
            GUI.Label(new Rect(renameRect.x, renameRect.y, 43, renameRect.height), "覆盖名", XdEditorStyles.PathLabel);
            GUI.SetNextControlName(view.renameControl);
            EditorGUI.BeginChangeCheck();
            var name = EditorGUI.TextField(new Rect(renameRect.x + 43, renameRect.y, Mathf.Max(10f, renameRect.width - 86), renameRect.height), view.manual ?? "");
            if (EditorGUI.EndChangeCheck())
            {
                if (string.IsNullOrWhiteSpace(name)) _names.Remove(entry.Id); else _names[entry.Id] = name;
                XdFlattenHistory.SaveNames(_file, _board, _names); InvalidateDisplay();
            }
            if (GUI.Button(new Rect(renameRect.xMax - 38, renameRect.y, 38, renameRect.height), "还原", EditorStyles.miniButton))
            {
                _names.Remove(entry.Id); XdFlattenHistory.SaveNames(_file, _board, _names); InvalidateDisplay(); GUI.FocusControl(null);
            }
            GUI.Label(Next(), view.exportContent, m_RowMutedStyle);
            if (view.componentContent != null) GUI.Label(Next(), view.componentContent, m_RowMutedStyle);
            if (view.status != null) GUI.Label(Next(), view.status, XdEditorStyles.PathLabel);
        }

        private void OnGUI()
        {
            XdEditorStyles.Ensure();
            if (_entries == null) { EditorGUILayout.HelpBox("脚本已重载，请重新打开合图设置。", MessageType.Info); return; }
            EnsureVisible();
            if (m_ContentStyle == null) m_ContentStyle = new GUIStyle { padding = new RectOffset(16, 16, 8, 12) };
            using (new EditorGUILayout.VerticalScope(m_ContentStyle))
            {
                XdEditorStyles.Header("选择需要合成的分组", "仅保存 Unity 导出设置，不修改 XD 文件。");
                EditorGUILayout.LabelField(m_BoardLabel, XdEditorStyles.Muted);
                EditorGUILayout.HelpBox("默认合图为 PNG；纯矢量可保留为 SVGImage。搜索只显示匹配图层，并标明原始路径。", MessageType.Info);
                var search = EditorGUILayout.TextField(_search, EditorStyles.toolbarSearchField);
                if (search != _search) { _search = search; _scroll.y = 0f; InvalidateVisible(); }
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Shift 点击勾选框可批量勾选或取消", XdEditorStyles.PathLabel); GUILayout.FlexibleSpace();
                    if (_settings != null && GUILayout.Button("组件命名规则…", EditorStyles.miniButton, GUILayout.Width(100)))
                    {
                        UnityEditor.Selection.activeObject = _settings.XdTriggers.FirstOrDefault(t => !string.IsNullOrEmpty(t.settingsTitle)) ?? (UnityEngine.Object)_settings;
                        EditorGUIUtility.PingObject(UnityEditor.Selection.activeObject);
                    }
                    if (GUILayout.Button("全部展开", EditorStyles.miniButtonLeft, GUILayout.Width(70))) { _collapsed.Clear(); InvalidateVisible(); }
                    if (GUILayout.Button("全部收起", EditorStyles.miniButtonRight, GUILayout.Width(70)))
                    { _collapsed = new HashSet<string>(_entries.Where(x => x.HasChildren).Select(x => x.Id)); InvalidateVisible(); }
                }
                DrawRows(GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true)));
                EditorGUILayout.LabelField(m_FooterLabel, XdEditorStyles.Muted);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("清除合图设置", GUILayout.Height(28)))
                    {
                        _selected.Clear(); _collapsed.Clear(); _excluded.UnionWith(_entries.Where(x => x.IsGroup).Select(x => x.Id));
                        XdFlattenHistory.Save(_file, _board, _selected); XdFlattenHistory.SaveExcluded(_file, _board, _excluded); InvalidateDisplay();
                    }
                    if (GUILayout.Button("恢复自动规则", GUILayout.Height(28)))
                    {
                        _excluded.Clear(); XdFlattenHistory.SaveExcluded(_file, _board, _excluded);
                        _collapsed = new HashSet<string>(_entries.Where(x => IsChosen(x.Id)).Select(x => x.Id)); InvalidateDisplay();
                    }
                    GUILayout.FlexibleSpace();
                    if (XdEditorStyles.PrimaryButton("完成", 28, GUILayout.Width(100))) Close();
                }
            }
        }
    }
}
