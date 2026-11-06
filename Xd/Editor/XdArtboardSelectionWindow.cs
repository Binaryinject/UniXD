using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BIEngine.UniXD.Loader;
using UnityEditor;
using UnityEngine;

namespace BIEngine.UniXD.Xd
{
    public sealed class XdArtboardSelectionWindow : EditorWindow
    {
        private sealed class ArtboardSelectionState
        {
            public XdImporter.XdArtboardImportOption Option { get; }
            public bool Selected { get; set; }
            public string ExportName { get; set; } = "";
            public ArtboardSelectionState(XdImporter.XdArtboardImportOption option, bool selected) { Option = option; Selected = selected; }
        }

        private string _xdFilePath;
        private Action<string[]> _onConfirm;
        private Action _onCancel;
        private Vector2 _scrollPosition;
        private List<ArtboardSelectionState> _items;
        private string _search = "";
        private bool _onlySelected;
        private bool _forceReimport;
        private bool _completed;
        private Dictionary<string, string> _exportNames;
        private XdImportSettings _settings;
        private string m_RangeAnchorPath;

        private int m_SortMode;
        private bool m_EditingExportName;
        private Dictionary<string,int> m_EditingOrder;
        private static readonly string[] m_SortNames = { "原始顺序", "反向顺序", "已改名优先" };
        private string sortKey => "UniXD.ArtboardSort." + BIEngine.UniXD.Editor.ImportFingerprintSession.Hash(System.Text.Encoding.UTF8.GetBytes(Application.dataPath + "\n" + Path.GetFullPath(_xdFilePath)));

        public static void Show(string xdFilePath, Action<string[]> onConfirm, Action onCancel, bool forceReimport = false, XdImportSettings settings = null)
        {
            XdImporter.XdArtboardImportOption[] options;
            try { options = XdImporter.GetImportOptions(xdFilePath); }
            catch (OperationCanceledException) { onCancel?.Invoke(); return; }
            if (options.Length == 0)
            {
                EditorUtility.DisplayDialog("XD 导出", "没有可导出的画板。", "确定");
                onCancel?.Invoke();
                return;
            }
            var savedSelection = XdArtboardSelectionHistory.Load(xdFilePath);
            var selectedPaths = new HashSet<string>(XdImporter.ResolveSelectedArtboards(options, savedSelection));
            var window = GetWindow<XdArtboardSelectionWindow>(true, "XD · 选择导出画板", true);
            window.minSize = new Vector2(760, 400);
            var mainWindow = EditorGUIUtility.GetMainWindowPosition();
            var size = window.position.width < 600 ? new Vector2(1000, Mathf.Min(720, mainWindow.height - 90)) : window.position.size;
            window.Initialize(xdFilePath, options, selectedPaths, onConfirm, onCancel);
            window._forceReimport = forceReimport;
            window._settings = settings;
            window.ShowUtility();
            window.position = new Rect(mainWindow.center - size * .5f, size);
        }

        private void Initialize(string xdFilePath, IReadOnlyCollection<XdImporter.XdArtboardImportOption> options, HashSet<string> selectedPaths, Action<string[]> onConfirm, Action onCancel)
        {
            _xdFilePath = xdFilePath;
            m_SortMode = Mathf.Clamp(EditorPrefs.GetInt(sortKey,0),0,2); m_EditingOrder=null;
            _onConfirm = onConfirm;
            _onCancel = onCancel;
            _completed = false;
            _search = "";
            _onlySelected = false;
            _scrollPosition = Vector2.zero;
            m_RangeAnchorPath = null;
            _items = options.Select(x => new ArtboardSelectionState(x, selectedPaths.Contains(x.ManifestPath))).ToList();
            _exportNames = XdArtboardExportNameHistory.Load(xdFilePath);
            foreach (var item in _items)
                if (!item.Option.isSystemPasteboard && _exportNames.TryGetValue(item.Option.ManifestPath, out var name)) item.ExportName = name;
        }

        private void OnGUI()
        {
            XdEditorStyles.Ensure();
            if (_items == null)
            {
                EditorGUILayout.HelpBox("脚本已重新加载，请重新选择 XD 文件。", MessageType.Info);
                if (GUILayout.Button("关闭")) Close();
                return;
            }
            m_EditingExportName = (GUI.GetNameOfFocusedControl() ?? "").StartsWith("ExportName/",StringComparison.Ordinal);
            var e = Event.current;
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                e.Use();
                if (!string.IsNullOrEmpty(_search)) { _search = ""; GUI.FocusControl(null); }
                else CancelSelection();
                return;
            }
            if (e.type == EventType.KeyDown && (e.control || e.command) && e.keyCode == KeyCode.Return && _items.Any(x => x.Selected))
            {
                e.Use(); ConfirmSelection(); return;
            }
            using (new EditorGUILayout.VerticalScope())
            {
                using (new EditorGUILayout.VerticalScope(new GUIStyle { padding = new RectOffset(18, 18, 4, 8) }))
                {
                    EditorGUILayout.LabelField("选择导出画板 · " + Path.GetFileName(_xdFilePath),EditorStyles.boldLabel);
                    EditorGUILayout.SelectableLabel(_xdFilePath, XdEditorStyles.PathLabel, GUILayout.Height(18));
                    GUILayout.Space(2);
                    DrawSearch();
                    GUILayout.Space(2);
                    DrawToolbar();
                }
                DrawList();
                DrawRecipe();
                DrawFooter();
            }
        }

        private IEnumerable<ArtboardSelectionState> VisibleItems()
        {
            var ordered = m_SortMode == 1 ? _items.AsEnumerable().Reverse() : m_SortMode == 2
                ? _items.OrderBy(x => string.IsNullOrWhiteSpace(x.ExportName) ? 1 : 0) : _items.AsEnumerable();
            var editing = m_EditingExportName;
            if (editing && m_EditingOrder != null) ordered = ordered.OrderBy(x => m_EditingOrder.TryGetValue(x.Option.ManifestPath,out var index) ? index : int.MaxValue);
            else if (!editing) m_EditingOrder = ordered.Select((item,index)=>new{item.Option.ManifestPath,index}).ToDictionary(x=>x.ManifestPath,x=>x.index);
            return ordered.Where(x => (!_onlySelected || x.Selected) &&
                (string.IsNullOrWhiteSpace(_search) || x.Option.DisplayName.IndexOf(_search.Trim(), StringComparison.OrdinalIgnoreCase) >= 0 ||
                    x.ExportName.IndexOf(_search.Trim(), StringComparison.OrdinalIgnoreCase) >= 0));
        }

        private void DrawRecipe()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("保存自动化配置…"))
                {
                    var path = EditorUtility.SaveFilePanel("保存 XD 自动化配置", "Assets/Settings/Xd", "UiExportRecipe", "json");
                    if (!string.IsNullOrEmpty(path))
                    {
                        try { XdAutomationRecipe.Save(_xdFilePath, path,
                            _items.Where(item => item.Selected).Select(item => item.Option.ManifestPath).ToArray(), _exportNames); }
                        catch (Exception error) { EditorUtility.DisplayDialog("保存失败", error.Message, "确定"); }
                    }
                }
                if (GUILayout.Button("读取自动化配置…"))
                {
                    var path = EditorUtility.OpenFilePanel("读取 XD 自动化配置", "Assets/Settings/Xd", "json");
                    if (!string.IsNullOrEmpty(path))
                    {
                        try
                        {
                            XdAutomationRecipe.Load(_xdFilePath, path, _settings);
                            var selected = new HashSet<string>(XdArtboardSelectionHistory.Load(_xdFilePath));
                            _exportNames = XdArtboardExportNameHistory.Load(_xdFilePath);
                            foreach (var item in _items)
                            {
                                item.Selected = selected.Contains(item.Option.ManifestPath);
                                item.ExportName = !item.Option.isSystemPasteboard && _exportNames.TryGetValue(item.Option.ManifestPath, out var name) ? name : "";
                            }
                        }
                        catch (Exception error) { EditorUtility.DisplayDialog("读取失败", error.Message, "确定"); }
                    }
                }
            }
        }

        private void DrawSearch()
        {
            EditorGUILayout.BeginHorizontal();
            GUI.SetNextControlName("ArtboardSearch");
            EditorGUI.BeginChangeCheck();
            _search = EditorGUILayout.TextField(_search, EditorStyles.toolbarSearchField, GUILayout.Height(24));
            if (EditorGUI.EndChangeCheck()) _scrollPosition = Vector2.zero;
            if (string.IsNullOrEmpty(_search) && GUI.GetNameOfFocusedControl() != "ArtboardSearch")
            {
                var searchRect = GUILayoutUtility.GetLastRect();
                GUI.Label(new Rect(searchRect.x + 19, searchRect.y + 3, searchRect.width - 22, 18), "搜索原名或导出名…", XdEditorStyles.PathLabel);
            }
            GUILayout.Space(6);
            EditorGUI.BeginChangeCheck();
            _onlySelected = GUILayout.Toggle(_onlySelected, "只看已选", EditorStyles.miniButton, GUILayout.Width(80), GUILayout.Height(24));
            if (EditorGUI.EndChangeCheck()) _scrollPosition = Vector2.zero;
            EditorGUILayout.EndHorizontal();
        }

        private void DrawToolbar()
        {
            var visible = VisibleItems().ToArray();
            EditorGUILayout.BeginHorizontal();
            var sort=EditorGUILayout.Popup(m_SortMode,m_SortNames,GUILayout.Width(110));
            if(sort!=m_SortMode){m_SortMode=sort;m_EditingOrder=null;_scrollPosition=Vector2.zero;EditorPrefs.SetInt(sortKey,sort);GUI.FocusControl(null);}

            EditorGUILayout.LabelField($"画板  {visible.Length} / {_items.Count}", XdEditorStyles.Section, GUILayout.MinWidth(95));
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(visible.Length == 0))
            {
                if (GUILayout.Button(new GUIContent("全选", "选中当前筛选结果；保留列表外的选择。"), EditorStyles.miniButtonLeft, GUILayout.Width(48))) SetVisible(true);
                if (GUILayout.Button(new GUIContent("反选", "反转当前筛选结果的选择。"), EditorStyles.miniButtonMid, GUILayout.Width(48))) InvertSelection();
                if (GUILayout.Button(new GUIContent("已标记", "当前筛选结果仅保留在 XD 中标记为导出的画板。"), EditorStyles.miniButtonMid, GUILayout.Width(58))) ApplyMarkedSelection();
                if (GUILayout.Button(new GUIContent("清空", "取消当前筛选结果的选择。"), EditorStyles.miniButtonRight, GUILayout.Width(48))) SetVisible(false);
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField("先点击一项，再按住 Shift 点击另一项，批量勾选或取消中间的画板。", XdEditorStyles.PathLabel);
        }

        private void DrawList()
        {
            var visible = VisibleItems().ToArray();
            using (var scroll = new EditorGUILayout.ScrollViewScope(_scrollPosition, GUILayout.ExpandHeight(true)))
            {
                _scrollPosition = scroll.scrollPosition;
                using (new EditorGUILayout.VerticalScope(new GUIStyle { padding = new RectOffset(18, 18, 2, 4) }))
                {
                    if (visible.Length == 0)
                    {
                        GUILayout.Space(45);
                        EditorGUILayout.LabelField(_onlySelected ? "当前没有符合条件的已选画板" : "没有找到匹配的画板", XdEditorStyles.CenterTitle);
                        GUILayout.Space(2);
                        EditorGUILayout.LabelField("试试其他名称，或清除筛选条件。", XdEditorStyles.CenterMuted);
                        GUILayout.Space(12);
                        EditorGUILayout.BeginHorizontal(); GUILayout.FlexibleSpace();
                        if (GUILayout.Button("清除筛选", GUILayout.Width(100), GUILayout.Height(26))) { _search = ""; _onlySelected = false; GUI.FocusControl(null); }
                        GUILayout.FlexibleSpace(); EditorGUILayout.EndHorizontal();
                    }
                    for (int i = 0; i < visible.Length; i++) DrawRow(visible[i], i);
                }
            }
        }

        private void ApplyArtboardSelection(ArtboardSelectionState item, bool selected, bool range)
        {
            // Snapshot before changing selection: "only selected" can hide the clicked row.
            var visible = VisibleItems().ToArray();
            var target = Array.IndexOf(visible, item);
            var anchor = range ? Array.FindIndex(visible, x => x.Option.ManifestPath == m_RangeAnchorPath) : -1;
            if (anchor < 0 || target < 0)
            {
                item.Selected = selected;
                m_RangeAnchorPath = item.Option.ManifestPath;
            }
            else
                for (var i = Math.Min(anchor, target); i <= Math.Max(anchor, target); i++) visible[i].Selected = selected;
            Repaint();
        }

        private void DrawRow(ArtboardSelectionState item, int index)
        {
            var row=GUILayoutUtility.GetRect(0,24,GUILayout.ExpandWidth(true));
            var background=item.Selected?XdEditorStyles.Selected:XdEditorStyles.Surface;
            if(!item.Selected&&index%2==1)background=Color.Lerp(background,Color.gray,.08f);
            EditorGUI.DrawRect(row,background);EditorGUI.DrawRect(new Rect(row.x,row.yMax-1,row.width,1),XdEditorStyles.Line);
            var y=row.y+3;var range=Event.current.shift;
            var selected=EditorGUI.Toggle(new Rect(row.x+8,y,18,18),item.Selected);if(selected!=item.Selected)ApplyArtboardSelection(item,selected,range);
            var sourceWidth=Mathf.Clamp(row.width*.30f,140,300);
            var nameRect=new Rect(row.x+32,y,sourceWidth-40,18);
            GUI.Label(nameRect,new GUIContent(item.Option.DisplayName,item.Option.DisplayName+"\n"+item.Option.ManifestPath),EditorStyles.label);
            if(Event.current.type==EventType.MouseDown&&Event.current.button==0&&nameRect.Contains(Event.current.mousePosition)){ApplyArtboardSelection(item,!item.Selected,Event.current.shift);GUI.FocusControl(null);Event.current.Use();}
            var input=new Rect(row.x+sourceWidth,y,Mathf.Max(80,row.width-sourceWidth-260),18);
            var control="ExportName/"+item.Option.ManifestPath;
            if (item.Option.isSystemPasteboard) {
                GUI.Label(input, new GUIContent("pasteboard", "XD 系统公共图库，导出名称固定"), EditorStyles.textField);
                using (new EditorGUI.DisabledScope(true))
                    GUI.Button(new Rect(input.xMax+4,y,20,18),new GUIContent("×","系统名称不可修改"),EditorStyles.miniButton);
            }
            else {
            GUI.SetNextControlName(control);EditorGUI.BeginChangeCheck();var value=EditorGUI.TextField(input,item.ExportName);if(EditorGUI.EndChangeCheck())UpdateExportName(item,value);
            if(string.IsNullOrEmpty(item.ExportName)&&GUI.GetNameOfFocusedControl()!=control)GUI.Label(new Rect(input.x+4,y,input.width-8,18),new GUIContent(XdImporter.GetExportName(item.Option,null,_settings),"导出名称；留空沿用原名"),EditorStyles.miniLabel);
            if(GUI.Button(new Rect(input.xMax+4,y,20,18),new GUIContent("×","还原导出名称"),EditorStyles.miniButton)){UpdateExportName(item,"");GUI.FocusControl(null);}
            }
            var status=item.Option.isSystemPasteboard ? "系统图库 · 名称固定" : (item.Option.IsMarkedForExport?"已标记":"")+(item.Option.IsExpandArtboard?" · 展开子项":"");
            GUI.Label(new Rect(input.xMax+30,y,130,18),new GUIContent(status,status),EditorStyles.miniLabel);
            if(GUI.Button(new Rect(row.xMax-92,y,84,18),"合图设置…",EditorStyles.miniButton)){var path=_xdFilePath;var board=item.Option;var settings=_settings;EditorApplication.delayCall+=()=>XdFlattenWindow.Open(path,board,settings);}
        }

        private void UpdateExportName(ArtboardSelectionState item, string name)
        {
            if (item.Option.isSystemPasteboard) return;
            item.ExportName = name;
            if (string.IsNullOrWhiteSpace(name)) _exportNames.Remove(item.Option.ManifestPath);
            else _exportNames[item.Option.ManifestPath] = name.Trim();
            XdArtboardExportNameHistory.Save(_xdFilePath, _exportNames);
        }

        private string GetNameError() => XdImporter.GetExportNameError(_items.Where(x => x.Selected).Select(x => x.Option), _exportNames, _settings);

        private static float DrawBadge(float right, float y, string label, bool marked)
        {
            float width = label.Length * 11 + 16;
            var rect = new Rect(right - width, y, width, 21);
            var textColor = marked ? (EditorGUIUtility.isProSkin ? new Color(.54f, .85f, .72f) : new Color(.12f, .43f, .31f))
                : (EditorGUIUtility.isProSkin ? new Color(.76f, .72f, 1f) : new Color(.38f, .28f, .66f));
            EditorGUI.DrawRect(rect, new Color(textColor.r, textColor.g, textColor.b, EditorGUIUtility.isProSkin ? .12f : .10f));
            var style = new GUIStyle(XdEditorStyles.Badge); style.normal.textColor = textColor;
            GUI.Label(rect, label, style);
            return rect.x - 7;
        }

        private void DrawFooter()
        {
            int selectedCount = _items.Count(x => x.Selected);
            var nameError = GetNameError();
            bool confirm = false, cancel = false;
            using (new EditorGUILayout.VerticalScope(new GUIStyle { padding = new RectOffset(18, 18, 11, 12) }))
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField($"已选 {selectedCount} / {_items.Count} 个画板", XdEditorStyles.Section);
                GUILayout.Label(_forceReimport ? "强制重导" : "自动增量", XdEditorStyles.Status(_forceReimport), GUILayout.Width(72));
                EditorGUILayout.EndHorizontal();
                GUILayout.Space(3);
                EditorGUILayout.LabelField("导出名称会自动保存；留空沿用 XD 原名。", XdEditorStyles.Muted);
                if (nameError != null) EditorGUILayout.HelpBox(nameError, MessageType.Warning);
                int hiddenSelected = selectedCount - VisibleItems().Count(x => x.Selected);
                if (hiddenSelected > 0)
                    EditorGUILayout.LabelField($"筛选隐藏了 {hiddenSelected} 个已选画板，它们也会导出。", XdEditorStyles.Muted);
                GUILayout.Space(2);
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Ctrl + Enter 导出", XdEditorStyles.PathLabel);
                GUILayout.FlexibleSpace();
                cancel = GUILayout.Button("取消", GUILayout.Width(80), GUILayout.Height(32));
                GUILayout.Space(6);
                using (new EditorGUI.DisabledScope(selectedCount == 0 || nameError != null))
                    confirm = XdEditorStyles.PrimaryButton($"导出所选（{selectedCount}）", 32, GUILayout.Width(156));
                EditorGUILayout.EndHorizontal();
            }
            if (confirm) ConfirmSelection();
            else if (cancel) CancelSelection();
        }

        private void SetVisible(bool value) { foreach (var item in VisibleItems().ToArray()) item.Selected = value; }
        private void InvertSelection() { foreach (var item in VisibleItems().ToArray()) item.Selected = !item.Selected; }
        private void ApplyMarkedSelection() { foreach (var item in VisibleItems().ToArray()) item.Selected = item.Option.IsMarkedForExport; }

        private void ConfirmSelection()
        {
            if (_completed || _items == null) return;
            var selectedPaths = _items.Where(x => x.Selected).Select(x => x.Option.ManifestPath).ToArray();
            if (selectedPaths.Length == 0 || GetNameError() != null) return;
            XdArtboardSelectionHistory.Save(_xdFilePath, selectedPaths);
            _completed = true;
            var confirm = _onConfirm;
            Close();
            // Close this utility before import or showing the next file in a batch.
            EditorApplication.delayCall += () => confirm?.Invoke(selectedPaths);
        }

        private void CancelSelection()
        {
            if (_completed) return;
            _completed = true;
            var cancel = _onCancel;
            Close();
            cancel?.Invoke();
        }

        private void OnDestroy()
        {
            if (!_completed) { _completed = true; _onCancel?.Invoke(); }
            _items = null;
        }
    }
    public sealed partial class XdFlattenWindow : EditorWindow
    {
        private sealed class Entry
        {
            public string Id, Name;
            public int Depth;
            public bool Supported;
            public bool IsGroup;
            public bool IsScrollGroup;
            public bool HasChildren;
            public bool AutoEligible;
            public string[] Ancestors;
        }
        private string _file, _board;
        private List<Entry> _entries;
        private HashSet<string> _selected;
        private Vector2 _scroll;
        private string _search = "";
        private XdImportSettings _settings;
        private Dictionary<string, string> _names;
        private string _boardOriginal;
        private HashSet<string> _collapsed = new HashSet<string>();
        private HashSet<string> _excluded = new HashSet<string>();
        private HashSet<string> _omitted = new HashSet<string>();
        private HashSet<string> m_MergedShadowLayers = new HashSet<string>();
        private HashSet<string> m_VectorLayers = new HashSet<string>();
        private static readonly string[] m_GroupRenderModeNames = { "合并为 PNG（Image）", "合并为单个 SVG（SVGImage）" };
        private static readonly string[] m_RenderModeNames = { "转为 PNG（Image）", "保持矢量（SVGImage）" };
        private Dictionary<string, XdScrollViewMode> m_ScrollViewModes = new Dictionary<string, XdScrollViewMode>();
        private static readonly string[] m_ScrollViewModeNames = { "不生成 ScrollView", "普通 ScrollView", "OSA ScrollView" };
        private HashSet<string> _previewIds = new HashSet<string>();
        private Dictionary<string, BIEngine.UniXD.Xd.Parsing.Internal.XdObjectJson> m_PreviewNodes = new Dictionary<string, BIEngine.UniXD.Xd.Parsing.Internal.XdObjectJson>();
        private BIEngine.UniXD.Xd.Parsing.Internal.XdObjectJson[] _previewRoots;
        private Dictionary<string, BIEngine.UniXD.Xd.Parsing.Internal.XdObjectJson> _previewSymbols;
        private HashSet<string> _autoEligible = new HashSet<string>();
        private BIEngine.UniXD.Xd.Parsing.Internal.XdObjectJson[] _roots;
        private Dictionary<string, BIEngine.UniXD.Xd.Parsing.Internal.XdObjectJson> _symbols;

        public static void Open(string file, XdImporter.XdArtboardImportOption option, XdImportSettings settings = null)
        {
            var entries = new List<Entry>();
            BIEngine.UniXD.Xd.Parsing.Internal.XdObjectJson[] roots;
            var symbols = new Dictionary<string, BIEngine.UniXD.Xd.Parsing.Internal.XdObjectJson>();
            try
            {
                using (var progress = new BIEngine.UniXD.Editor.ImportProgressBar("读取合图分组"))
                using (var xd = new BIEngine.UniXD.Xd.Parsing.XdFile(file, progress.Update))
                {
                    var artboard = xd.Artworks.First(x => x.Manifest.Path == option.ManifestPath);
                    foreach (var symbol in artboard.Resources.Resources.Meta.Ux.Symbols) CollectSymbols(symbol, symbols);
                    roots = artboard.Artboard.Children.SelectMany(x => x.Artboard.Children).ToArray();
                    foreach (var child in roots) Collect(child, 0, new List<string>(), entries, symbols, settings);
                }
            }
            catch (OperationCanceledException) { return; }
            var window = CreateInstance<XdFlattenWindow>();
            window.titleContent = new GUIContent("合图设置 · " + option.DisplayName);
            window.minSize = new Vector2(760, 380);
            window._file = file; window._board = option.ManifestPath; window._entries = entries;
            window._roots = roots; window._symbols = symbols;
            window._selected = XdFlattenHistory.Load(file, option.ManifestPath);
            window._excluded = XdFlattenHistory.LoadExcluded(file, option.ManifestPath);
            window._omitted = XdFlattenHistory.LoadOmitted(file, option.ManifestPath);
            window.m_VectorLayers = XdFlattenHistory.LoadVectors(file, option.ManifestPath);
            window.m_MergedShadowLayers = XdFlattenHistory.LoadMergedShadows(file, option.ManifestPath);
            window.m_ScrollViewModes = XdFlattenHistory.LoadScrollViewModes(file, option.ManifestPath);
            window._names = XdFlattenHistory.LoadNames(file, option.ManifestPath);
            window._settings = settings; window._boardOriginal = option.DisplayName;
            window.RefreshExportPreview();
            window._collapsed = new HashSet<string>(entries.Where(x => window.IsChosen(x.Id) ||
                window._omitted.Contains(x.Id) || x.Ancestors.Any(window._omitted.Contains)).Select(x => x.Id));
            window.ShowUtility();
            var main = EditorGUIUtility.GetMainWindowPosition();
            var width = Mathf.Min(1120, main.width - 40);
            window.position = new Rect(main.center - new Vector2(width / 2, 300), new Vector2(width, 600));
        }

        private static void CollectSymbols(BIEngine.UniXD.Xd.Parsing.Internal.XdObjectJson node, Dictionary<string, BIEngine.UniXD.Xd.Parsing.Internal.XdObjectJson> symbols)
        {
            if (!string.IsNullOrEmpty(node.Id)) symbols[node.Id] = node;
            foreach (var child in node.Group?.Children ?? Array.Empty<BIEngine.UniXD.Xd.Parsing.Internal.XdObjectJson>()) CollectSymbols(child, symbols);
            foreach (var state in node.Meta?.Ux?.States ?? Array.Empty<BIEngine.UniXD.Xd.Parsing.Internal.XdObjectJson>()) CollectSymbols(state, symbols);
        }

        private static void Collect(BIEngine.UniXD.Xd.Parsing.Internal.XdObjectJson node, int depth, List<string> ancestors, List<Entry> entries, Dictionary<string, BIEngine.UniXD.Xd.Parsing.Internal.XdObjectJson> symbols = null, XdImportSettings settings = null)
        {
            var id = SvgGroupParser.GroupId(node);
            if (string.IsNullOrEmpty(id)) return;
            var effective = node.Type == "syncRef" ? XdExportRules.ResolvePreviewNode(node, symbols) : node;
            bool group = effective.Group != null;
            var scrollingType = node.Meta?.Ux?.ScrollingType;
            // Match GetRefObject.Copy: only null instance values inherit the symbol's metadata.
            if (scrollingType == null && node.Type == "syncRef" && symbols != null && symbols.TryGetValue(node.SyncSourceGuid ?? "", out var source))
                scrollingType = source.Meta?.Ux?.ScrollingType;
            entries.Add(new Entry { Id = id, Name = effective.Name ?? "未命名图层", Depth = depth, IsGroup = group, IsScrollGroup = !string.IsNullOrWhiteSpace(scrollingType), HasChildren = node.Group?.Children?.Length > 0, Supported = group && SvgGroupParser.HasMergeContent(effective) && SvgGroupParser.CanFlatten(effective), Ancestors = ancestors.ToArray() });
            var children = new List<string>(ancestors) { id };
            foreach (var child in node.Group?.Children ?? Array.Empty<BIEngine.UniXD.Xd.Parsing.Internal.XdObjectJson>()) Collect(child, depth + 1, children, entries, symbols, settings);
        }

        private bool IsChosen(string id) => _previewIds.Contains(id) && m_ResolvedNodes.TryGetValue(id, out var node) && SvgGroupParser.HasMergeContent(node) &&
            (!m_FlattenAllowed.TryGetValue(id, out var allowed) || allowed) &&
            (_selected.Contains(id) || (!_excluded.Contains(id) && _autoEligible.Contains(id)));

        private void RefreshExportPreview()
        {
            _previewRoots = XdExportRules.FilterPreviewNodes(_roots, _omitted);
            _previewSymbols = new Dictionary<string, BIEngine.UniXD.Xd.Parsing.Internal.XdObjectJson>();
            foreach (var pair in _symbols)
            {
                var filtered = XdExportRules.FilterPreviewNodes(new[] { pair.Value }, _omitted);
                if (filtered.Length > 0) _previewSymbols[pair.Key] = filtered[0];
            }
            var nodes = new Dictionary<string, BIEngine.UniXD.Xd.Parsing.Internal.XdObjectJson>();
            void CollectPreview(BIEngine.UniXD.Xd.Parsing.Internal.XdObjectJson node)
            {
                var id = SvgGroupParser.GroupId(node);
                if (!string.IsNullOrEmpty(id)) nodes[id] = node;
                foreach (var child in node.Group?.Children ?? Array.Empty<BIEngine.UniXD.Xd.Parsing.Internal.XdObjectJson>()) CollectPreview(child);
            }
            foreach (var node in _previewRoots) CollectPreview(node);
            _previewIds = new HashSet<string>(nodes.Keys);
            m_PreviewNodes = nodes;
            _autoEligible.Clear();
            m_ResolvedNodes.Clear();
            m_FlattenAllowed.Clear();
            m_VectorEligible.Clear();
            m_UsesVector.Clear();
            var parentsWithChildren = new HashSet<string>(_entries.Where(x=>x.Ancestors.Length>0).Select(x=>x.Ancestors[x.Ancestors.Length-1]));
            foreach (var entry in _entries)
            {
                if (!nodes.TryGetValue(entry.Id, out var node)) continue;
                node = XdExportRules.ResolvePreviewNode(node, _previewSymbols);
                m_ResolvedNodes[entry.Id] = node;
                m_FlattenAllowed[entry.Id] = XdVectorExport.CanFlatten(node, m_VectorLayers);
                m_VectorEligible[entry.Id] = XdVectorExport.IsEligible(node) || XdVectorExport.IsRequested(node, m_VectorLayers);
                m_UsesVector[entry.Id] = XdVectorExport.UsesVector(node, m_VectorLayers);
                entry.IsGroup = node.Group != null;
                entry.HasChildren = parentsWithChildren.Contains(entry.Id);
                entry.Supported = entry.IsGroup && SvgGroupParser.HasMergeContent(node) && SvgGroupParser.CanFlatten(node);
                if (entry.Supported && m_FlattenAllowed[entry.Id] && _settings != null && _settings.GroupParsers.Any(parser => parser.IsAutomaticFlatten(node)))
                    _autoEligible.Add(entry.Id);
            }
            InvalidateDisplay();
        }

        private void SetChosen(Entry entry, bool value)
        {
            if (value && !CanToggleMerge(entry)) return;
            if (value) { _selected.Add(entry.Id); _excluded.Remove(entry.Id); _collapsed.Add(entry.Id); }
            else { _selected.Remove(entry.Id); _excluded.Add(entry.Id); _collapsed.Remove(entry.Id); }
            XdFlattenHistory.Save(_file, _board, _selected);
            XdFlattenHistory.SaveExcluded(_file, _board, _excluded);
            InvalidateDisplay();
        }

        private void SetScrollViewMode(Entry entry, XdScrollViewMode mode)
        {
            if (mode == XdScrollViewMode.None) m_ScrollViewModes.Remove(entry.Id); else m_ScrollViewModes[entry.Id] = mode;
            XdFlattenHistory.SaveScrollViewModes(_file, _board, m_ScrollViewModes);
            InvalidateDisplay();
        }

        private string Preview(string source, string manual)
        {
            try { return XdExportRules.NormalizeName(source, manual, _settings); }
            catch (BIEngine.UniXD.Editor.ImportException) { return "覆盖名含非法字符，请修改"; }
        }
    }

}
