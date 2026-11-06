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
            internal bool chosen, covered, inheritedOmission, omitted, vectorEligible, inheritedVector, fromChild, shadowEligible;
            internal string manual, exported, path, exportLabel, componentLabel, status;
            internal GUIContent title, renameHint;
            internal GUIContent icon;
            internal GUIContent pathContent, exportContent, componentContent;
            internal string renameControl;
            internal float height;
            internal float titleWidth;
        }

        private readonly Dictionary<string, XdObjectJson> m_ResolvedNodes = new Dictionary<string, XdObjectJson>();
        private readonly Dictionary<string, bool> m_FlattenAllowed = new Dictionary<string, bool>();
        private readonly Dictionary<string, bool> m_VectorEligible = new Dictionary<string, bool>();
        private readonly Dictionary<string, bool> m_UsesVector = new Dictionary<string, bool>();
        private readonly Dictionary<string,int> m_LastDescendant = new Dictionary<string,int>();
        private static readonly string[] m_CompactRenderModes = { "PNG", "SVG" };
        private static readonly string[] m_CompactShadowModes = { "独立", "合入 PNG" };
        private static readonly Dictionary<string, Texture> m_NodeIcons = new Dictionary<string, Texture>();

        private static GUIContent NodeIcon(string components, bool group, string scrollingType, XdScrollViewMode mode, string path)
        {
            components = components ?? "";
            string key, description;
            if (components.Contains("ScrollRect") || components.Contains("OSA") || components.Contains("ListAdapter") || components.Contains("GridAdapter"))
            {
                key = "ScrollRect Icon";
                description = (mode == XdScrollViewMode.Osa ? "OSA" : "ScrollRect") + (scrollingType == "vertical" ? " · 纵向滚动" : " · 横向滚动");
            }
            else if (components.Contains("TextMeshPro")) { key = "TMP"; description = "TextMeshPro 文本"; }
            else if (components.Contains("SVGImage")) { key = "VectorImage Icon"; description = "SVG 矢量图"; }
            else if (components.Contains("Image")) { key = "Image Icon"; description = "PNG 位图"; }
            else if (components.Contains("Text")) { key = "Text Icon"; description = "文本"; }
            else { key = group ? "Folder Icon" : "TextAsset Icon"; description = group ? "分组" : "图层"; }
            if (!m_NodeIcons.TryGetValue(key, out var texture))
            {
                if (key == "TMP")
                {
                    // Use TMP's own component icon without introducing a mandatory TMP assembly dependency.
                    var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("TMPro.TextMeshProUGUI")).FirstOrDefault(t => t != null);
                    texture = type == null ? EditorGUIUtility.IconContent("Text Icon").image : EditorGUIUtility.ObjectContent(null, type).image;
                }
                else texture = EditorGUIUtility.IconContent(key).image;
                m_NodeIcons[key] = texture;
            }
            return new GUIContent(texture, description + "\n" + path);
        }
        private readonly List<EntryView> m_Views = new List<EntryView>();
        private readonly List<EntryView> m_Visible = new List<EntryView>();
        private readonly List<float> m_RowOffsets = new List<float>();
        private bool m_DisplayDirty = true, m_VisibleDirty = true;
        private string m_LastSearch, m_BoardLabel, m_Summary, m_FooterLabel;
        private string m_RangeAnchorId;
        private string m_EditingLayerId;
        private GUIStyle m_ContentStyle;
        private GUIStyle m_RowMutedStyle;
        private bool m_RowStyleDark;
        private int m_DisplayBuildCount, m_VisibleBuildCount, m_LastDrawnRows;
        private bool m_MarqueeVisible;
        private double m_MarqueeEpoch, m_LastMarqueeGui, m_NextMarqueeRepaint;
        private GUIStyle m_TreeTitleStyle;

        private void OnEnable() {
            m_MarqueeEpoch = EditorApplication.timeSinceStartup;
            EditorApplication.update += RepaintMarquee;
        }
        private void OnDisable() => EditorApplication.update -= RepaintMarquee;
        private void RepaintMarquee() {
            var now = EditorApplication.timeSinceStartup;
            if (!m_MarqueeVisible || now - m_LastMarqueeGui > 1 || now < m_NextMarqueeRepaint) return;
            m_NextMarqueeRepaint = now + 1d / 24;
            Repaint();
        }

        private void DrawTreeTitle(Rect rect, EntryView view) {
            if (m_TreeTitleStyle == null) m_TreeTitleStyle = new GUIStyle(EditorStyles.label) { wordWrap = false, clipping = TextClipping.Clip };
            if (view.titleWidth <= rect.width) { GUI.Label(rect, view.title, m_TreeTitleStyle); return; }
            m_MarqueeVisible = true;
            var period = view.titleWidth + 40f;
            var phase = (float)((EditorApplication.timeSinceStartup - m_MarqueeEpoch) % (1.5 + period / 28));
            var offset = Mathf.Max(0, phase - 1.5f) * 28;
            GUI.BeginClip(rect);
            GUI.Label(new Rect(-offset, 0, view.titleWidth, rect.height), view.title, m_TreeTitleStyle);
            GUI.Label(new Rect(period - offset, 0, view.titleWidth, rect.height), view.title, m_TreeTitleStyle);
            GUI.EndClip();
        }
        private static float lineHeight => EditorGUIUtility.singleLineHeight + 2f;
        private static readonly GUIContent m_OmitLabel = new GUIContent("不导出", "跳过此节点及其子节点。");
        private static readonly GUIContent m_RenderLabel = new GUIContent("渲染方式", "纯矢量可保留为 SVGImage；选择合并阴影时输出包含阴影的 PNG。");
        private static readonly GUIContent m_ShadowLabel = new GUIContent("外阴影处理", "合并时输出 PNG，图片和节点尺寸包含阴影外扩范围。");
        private static readonly string[] m_ShadowModeNames = { "独立导出（默认）", "合并到图片（扩大尺寸）" };
        private static readonly GUIContent m_ScrollLabel = new GUIContent("滚动视图", "仅影响此分组。");

        private void InvalidateDisplay() { m_DisplayDirty = true; m_VisibleDirty = true; }
        private void InvalidateVisible() { m_VisibleDirty = true; }

        private bool CanToggleMerge(Entry entry) => _previewIds.Contains(entry.Id) &&
            entry.Supported && !_omitted.Contains(entry.Id) &&
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
                view.title = new GUIContent(entry.Name.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' '), view.path);
                view.titleWidth = EditorStyles.label.CalcSize(view.title).x;
                view.pathContent = new GUIContent(view.path, view.path);
                view.renameControl = "XdLayerName_" + entry.Id;
                view.vectorEligible = m_VectorEligible.TryGetValue(entry.Id, out var eligible) && eligible;
                var usesVector = m_UsesVector.TryGetValue(entry.Id, out var vector) && vector;
                view.fromChild = !m_VectorLayers.Contains(entry.Id) && usesVector;
                view.exportLabel = view.omitted ? (view.inheritedOmission ? "父级已标记不导出" : "已标记不导出（含子节点）")
                    : !_previewIds.Contains(entry.Id) ? "无可导出内容" : "导出预览：" + view.exported;
                view.shadowEligible = view.chosen && !view.covered && m_ResolvedNodes.TryGetValue(entry.Id, out var shadowNode) && XdShadowRenderer.HasShadows(shadowNode);
                if (m_ResolvedNodes.TryGetValue(entry.Id, out var node))
                {
                    m_ScrollViewModes.TryGetValue(entry.Id, out var mode);
                    // Already resolved at configuration changes, never resolve symbol trees on scroll/repaint.
                    view.componentLabel = "组件预览：" + XdComponentPreview.Preview(node, view.chosen, mode, _settings,
                        view.exported, XdExportRules.GetFinalLayerPath(names, entry.Id), null, usesVector || view.inheritedVector);
                    if (view.chosen && usesVector && (!view.shadowEligible || !m_MergedShadowLayers.Contains(entry.Id))) view.componentLabel = view.componentLabel?.Replace("SVGImage", "SVGImage（单个 SVG 合图）");
                }
                if (view.shadowEligible && m_MergedShadowLayers.Contains(entry.Id))
                    view.componentLabel = view.componentLabel?.Replace("SVGImage", "Image").Replace("软阴影（独立装饰层，不拦截点击）", "阴影合入 PNG（尺寸包含阴影）");
                m_ScrollViewModes.TryGetValue(entry.Id, out var iconScrollMode);
                m_ResolvedNodes.TryGetValue(entry.Id, out var iconNode);
                view.icon = NodeIcon(view.componentLabel, entry.IsGroup, iconNode?.Meta?.Ux?.ScrollingType, iconScrollMode, view.path);
                view.status = view.covered ? "已包含在父级合图（子项不单独导出）" : !entry.Supported
                    ? entry.IsGroup ? "含不支持的图层，不能合图" : "独立图层，可覆盖名称" : null;
                view.exportContent = new GUIContent(view.exportLabel, view.exportLabel);
                view.renameHint = new GUIContent(view.exported, view.exportLabel);
                view.componentContent = new GUIContent(view.covered ? "包含在父级合图" : view.componentLabel?.Replace("组件预览：", "") ?? view.status ?? "", view.path + "\n" + view.exportLabel + "\n" + view.componentLabel + "\n" + view.status);
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
            var searchIds = new HashSet<string>();
            if(searching) foreach(var view in m_Views) {
                if(view.entry.Id != m_EditingLayerId && view.entry.Name.IndexOf(query,StringComparison.OrdinalIgnoreCase)<0 && (view.manual==null||view.manual.IndexOf(query,StringComparison.OrdinalIgnoreCase)<0) && view.exported.IndexOf(query,StringComparison.OrdinalIgnoreCase)<0)continue;
                searchIds.Add(view.entry.Id);foreach(var ancestor in view.entry.Ancestors)searchIds.Add(ancestor);
            }
            foreach (var view in m_Views)
            {
                if (searching) { if(!searchIds.Contains(view.entry.Id))continue; }
                else if (view.entry.Ancestors.Any(_collapsed.Contains)) continue;
                view.height = lineHeight + 4f;
                m_Visible.Add(view);
                m_RowOffsets.Add(m_RowOffsets[m_RowOffsets.Count - 1] + view.height);
            }
            m_LastDescendant.Clear();
            for(var index=0;index<m_Visible.Count;index++)foreach(var ancestor in m_Visible[index].entry.Ancestors)m_LastDescendant[ancestor]=index;
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
            m_MarqueeVisible = false;
            m_LastMarqueeGui = EditorApplication.timeSinceStartup;
            XdEditorStyles.Ensure();
            if (m_RowMutedStyle == null || m_RowStyleDark != EditorGUIUtility.isProSkin)
            {
                m_RowStyleDark = EditorGUIUtility.isProSkin;
                m_RowMutedStyle = new GUIStyle(XdEditorStyles.Muted) { wordWrap = false, clipping = TextClipping.Clip };
            }
            EnsureVisible();
            // Text is saved as it changes. Do not transfer keyboard focus to a recycled row while scrolling.
            if (Event.current.type == EventType.ScrollWheel && viewport.Contains(Event.current.mousePosition)) GUI.FocusControl(null);
            var width = Mathf.Max(1050f, viewport.width - 18f);
            var header = new Rect(viewport.x, viewport.y, viewport.width, lineHeight + 4);
            GUI.BeginGroup(header);
            void Title(float x, float w, string text, string tip) => GUI.Label(new Rect(x - _scroll.x, 2, w, lineHeight), new GUIContent(text, tip), EditorStyles.miniBoldLabel);
            Title(8,265,"XD 图层 / 层级","缩进和连接线表示父子关系；悬停显示完整路径。"); Title(278,32,"合图","仅有可绘制子内容的分组可合图；Shift 点击支持范围选择。");
            Title(314,200,"导出名称","直接编辑名称；× 恢复自动名称。包含在父级合图的子节点不会单独导出。");Title(522,86,"渲染","纯矢量支持单个 SVG；含内阴影使用 PNG。");
            Title(614,84,"外阴影","默认独立；合入 PNG 时扩大图片和节点尺寸。");Title(702,90,"滚动模式","仅滚动分组可设置。");Title(796,44,"不导出","忽略该节点及其子节点。");Title(844,200,"组件 / 状态","悬停查看完整组件及导出状态。");
            GUI.EndGroup();
            viewport.yMin += header.height;
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
            var entry = view.entry; var y = box.y + 2;
            var color = entry.Depth % 2 == 0 ? XdEditorStyles.Surface : Color.Lerp(XdEditorStyles.Surface, Color.gray, .08f);
            EditorGUI.DrawRect(box, color);
            EditorGUI.DrawRect(new Rect(box.x,box.yMax-1,box.width,1),XdEditorStyles.Line);
            var x = box.x + 8 + Mathf.Min(entry.Depth, 12) * 16;
            for(var depth=0;depth<Mathf.Min(entry.Ancestors.Length,12);depth++) {
                var last = m_LastDescendant.TryGetValue(entry.Ancestors[depth],out var index) && m_Visible[index] == view;
                EditorGUI.DrawRect(new Rect(box.x+15+depth*16,box.y,1,last?box.height*.5f:box.height),XdEditorStyles.Line);
            }
            if(entry.HasChildren) {
                var searching=!string.IsNullOrWhiteSpace(_search);
                var expanded = searching || !_collapsed.Contains(entry.Id);
                using(new EditorGUI.DisabledScope(searching)) {
                    var next = EditorGUI.Foldout(new Rect(x,y,14,EditorGUIUtility.singleLineHeight),expanded,GUIContent.none);
                    if(next!=expanded){if(next)_collapsed.Remove(entry.Id);else _collapsed.Add(entry.Id);InvalidateVisible();}
                }
                if(expanded)EditorGUI.DrawRect(new Rect(x+7,box.center.y+6,1,Mathf.Max(0,box.height*.5f-6)),XdEditorStyles.Line);
            }
            var oldColor = GUI.color;if(view.covered||view.omitted)GUI.color=Color.gray;
            GUI.Label(new Rect(x+15,y,16,18),view.icon);
            DrawTreeTitle(new Rect(x+34,y,Mathf.Max(20,270-x-34),18),view);GUI.color=oldColor;
            if(entry.Supported)
                using(new EditorGUI.DisabledScope(!CanToggleMerge(entry))) {
                    var range=Event.current.shift;var chosen=EditorGUI.Toggle(new Rect(284,y,18,18),view.chosen);
                    if(chosen!=view.chosen)ApplyMergeSelection(entry,chosen,range);
                }
            using(new EditorGUI.DisabledScope(view.covered||view.omitted)) {
                GUI.SetNextControlName(view.renameControl);
                EditorGUI.BeginChangeCheck();
                var name=EditorGUI.TextField(new Rect(314,y,178,18),view.manual??"");
                if(EditorGUI.EndChangeCheck()) {if(string.IsNullOrWhiteSpace(name))_names.Remove(entry.Id);else _names[entry.Id]=name;XdFlattenHistory.SaveNames(_file,_board,_names);InvalidateDisplay();}
                if(string.IsNullOrEmpty(view.manual)&&GUI.GetNameOfFocusedControl()!=view.renameControl)
                    GUI.Label(new Rect(318,y,170,18),view.renameHint,EditorStyles.miniLabel);
                if(GUI.Button(new Rect(496,y,20,18),new GUIContent("×","恢复自动名称"),EditorStyles.miniButton)){_names.Remove(entry.Id);XdFlattenHistory.SaveNames(_file,_board,_names);InvalidateDisplay();GUI.FocusControl(null);}
            }
            if(view.vectorEligible)
                using(new EditorGUI.DisabledScope(view.omitted||view.covered||view.inheritedVector||view.fromChild||(view.shadowEligible&&m_MergedShadowLayers.Contains(entry.Id)))) {
                    var previous=m_VectorLayers.Contains(entry.Id)||view.inheritedVector||view.fromChild?1:0;
                    var mode=EditorGUI.Popup(new Rect(522,y,86,18),previous,m_CompactRenderModes);
                    if(mode!=previous){if(mode==1)m_VectorLayers.Add(entry.Id);else m_VectorLayers.Remove(entry.Id);XdFlattenHistory.SaveVectors(_file,_board,m_VectorLayers);RefreshExportPreview();}
                }
            else GUI.Label(new Rect(526,y,80,18),view.entry.IsGroup&&!view.chosen?"—":"PNG",EditorStyles.miniLabel);
            if(view.shadowEligible)
                using(new EditorGUI.DisabledScope(view.omitted)) {
                    var previous=m_MergedShadowLayers.Contains(entry.Id)?1:0;
                    var mode=EditorGUI.Popup(new Rect(614,y,82,18),previous,m_CompactShadowModes);
                    if(mode!=previous){if(mode==1)m_MergedShadowLayers.Add(entry.Id);else m_MergedShadowLayers.Remove(entry.Id);XdFlattenHistory.SaveMergedShadows(_file,_board,m_MergedShadowLayers);InvalidateDisplay();}
                }
            if(entry.IsScrollGroup)
                using(new EditorGUI.DisabledScope(view.omitted||view.covered||view.chosen)) {
                    m_ScrollViewModes.TryGetValue(entry.Id,out var mode);
                    var next=(XdScrollViewMode)EditorGUI.Popup(new Rect(702,y,88,18),(int)mode,m_ScrollViewModeNames);if(next!=mode)SetScrollViewMode(entry,next);
                }
            using(new EditorGUI.DisabledScope(view.inheritedOmission)) {
                var omit=EditorGUI.Toggle(new Rect(806,y,18,18),view.omitted);
                if (omit != view.omitted) {
                    if (omit) {
                        _omitted.Add(entry.Id);
                        _collapsed.Add(entry.Id);
                        foreach (var child in _entries)
                            if (child.Ancestors.Contains(entry.Id)) _collapsed.Add(child.Id);
                    }
                    else _omitted.Remove(entry.Id);
                    XdFlattenHistory.SaveOmitted(_file, _board, _omitted);
                    RefreshExportPreview();
                }
            }
            GUI.Label(new Rect(844,y,box.width-850,18),view.componentContent,EditorStyles.miniLabel);
        }

        private void OnGUI()
        {
            XdEditorStyles.Ensure();
            if (_entries == null) { EditorGUILayout.HelpBox("脚本已重载，请重新打开合图设置。", MessageType.Info); return; }
            var focused = GUI.GetNameOfFocusedControl() ?? "";
            var editing = focused.StartsWith("XdLayerName_",StringComparison.Ordinal) ? focused.Substring("XdLayerName_".Length) : null;
            if (editing != m_EditingLayerId) {m_EditingLayerId=editing;InvalidateVisible();}
            EnsureVisible();
            if (m_ContentStyle == null) m_ContentStyle = new GUIStyle { padding = new RectOffset(16, 16, 8, 12) };
            using (new EditorGUILayout.VerticalScope(m_ContentStyle))
            {
                EditorGUILayout.LabelField(m_BoardLabel, XdEditorStyles.Muted);
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
