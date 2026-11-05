using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using BIEngine.UniXD.Editor.ScriptableObject;
using UnityEditor;
using UnityEngine;

namespace BIEngine.UniXD.Xd
{
    [CustomEditor(typeof(XdImportSettings))]
    public class XdImportSettingsEditor : UnityEditor.Editor
    {
        private readonly HistoryHolder _historyHolder = new HistoryHolder("AkyuiXd.History.{name}");
        private bool _showPaths = true;
        private bool _showTexture = true;
        private bool _showAdvanced;
        private bool _showReimport;
        private bool _busy;
        private string _lastImportStatus;

        public override void OnInspectorGUI()
        {
            XdEditorStyles.Ensure();
            serializedObject.Update();
            var oldLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = Mathf.Clamp(EditorGUIUtility.currentViewWidth * .32f, 90f, 142f);
            try
            {
                using (new EditorGUI.DisabledScope(_busy || EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode))
                {
                    XdEditorStyles.Header("XD 导出", "选择文件和画板，生成 Unity UI 资源。");
                    if (!string.IsNullOrEmpty(_lastImportStatus)) EditorGUILayout.HelpBox(_lastImportStatus, MessageType.Info);
                    DrawSource();
                    DrawHistory();
                    DrawPaths();
                    DrawTexture();
                    DrawPluginSettings();
                    DrawReimport();
                    DrawAdvanced();
                }
                serializedObject.ApplyModifiedProperties();
            }
            finally { EditorGUIUtility.labelWidth = oldLabelWidth; }
        }

        private void DrawPluginSettings()
        {
            foreach (var plugin in ((XdImportSettings)target).XdTriggers.Where(t => !string.IsNullOrEmpty(t.settingsTitle)))
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField(plugin.settingsTitle, XdEditorStyles.Section);
                    var config = new SerializedObject(plugin);
                    config.Update();
                    var field = config.GetIterator();
                    if (field.NextVisible(true))
                        while (field.NextVisible(false)) EditorGUILayout.PropertyField(field, true);
                    config.ApplyModifiedProperties();
                }
            }
        }

        private void DrawSource()
        {
            GUILayout.Space(6);
            var dropArea = GUILayoutUtility.GetRect(0, 88, GUILayout.ExpandWidth(true));
            bool hovering = dropArea.Contains(Event.current.mousePosition);
            EditorGUI.DrawRect(dropArea, hovering ? XdEditorStyles.Selected : XdEditorStyles.Surface);
            XdEditorStyles.Border(dropArea, hovering ? XdEditorStyles.Accent : XdEditorStyles.Line);
            GUI.Label(new Rect(dropArea.x + 12, dropArea.y + 13, dropArea.width - 24, 22), "将 .xd 文件拖到这里", XdEditorStyles.CenterTitle);
            GUI.Label(new Rect(dropArea.x + 12, dropArea.y + 39, dropArea.width - 24, 18), "支持多个文件 · 导出前可选择画板", XdEditorStyles.CenterMuted);
            if (GUI.Button(new Rect(dropArea.x, dropArea.y, dropArea.width, dropArea.height), GUIContent.none, GUIStyle.none)) ChooseFile();
            var e = Event.current;
            if (GUI.enabled && hovering && (e.type == EventType.DragUpdated || e.type == EventType.DragPerform))
            {
                var paths = DragAndDrop.paths.Where(IsXdFile).ToArray();
                DragAndDrop.visualMode = paths.Length > 0 ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
                if (e.type == EventType.DragPerform && paths.Length > 0)
                {
                    DragAndDrop.AcceptDrag();
                    QueueSelection(paths);
                }
                e.Use();
            }
            GUILayout.Space(7);
            if (XdEditorStyles.PrimaryButton("选择 XD 文件…", 30)) ChooseFile();
            EditorGUILayout.LabelField("导入过程中，可在进度窗口点击“取消”。", XdEditorStyles.Muted);
            GUILayout.Space(10);
        }

        private void ChooseFile()
        {
            var path = EditorUtility.OpenFilePanel("选择 XD 文件", Application.dataPath, "xd");
            if (!string.IsNullOrEmpty(path)) QueueSelection(new[] { path });
        }

        private void DrawHistory()
        {
            var histories = _historyHolder.GetPaths();
            if (histories.Length == 0) return;
            EditorGUILayout.LabelField("最近使用", XdEditorStyles.Section);
            foreach (var path in histories)
            {
                var row = GUILayoutUtility.GetRect(0, 44, GUILayout.ExpandWidth(true));
                EditorGUI.DrawRect(new Rect(row.x, row.yMax - 1, row.width, 1), XdEditorStyles.Line);
                var openRect = new Rect(row.x, row.y, row.width - 27, row.height);
                bool exists = File.Exists(path);
                if (exists && openRect.Contains(Event.current.mousePosition)) EditorGUI.DrawRect(openRect, XdEditorStyles.Selected);
                GUI.Label(new Rect(row.x + 4, row.y + 4, row.width - 35, 19), new GUIContent(Path.GetFileName(path), path), EditorStyles.boldLabel);
                GUI.Label(new Rect(row.x + 4, row.y + 23, row.width - 35, 16), new GUIContent(exists ? Path.GetDirectoryName(path) : "文件不存在，请重新选择", path), XdEditorStyles.PathLabel);
                using (new EditorGUI.DisabledScope(!exists))
                    if (GUI.Button(openRect, new GUIContent("", "重新选择此文件的导出画板"), GUIStyle.none)) QueueSelection(new[] { path });
                if (GUI.Button(new Rect(row.xMax - 24, row.y + 11, 22, 22), new GUIContent("×", "从最近使用中移除"), EditorStyles.miniButton))
                {
                    _historyHolder.Remove(new[] { path });
                    GUIUtility.ExitGUI();
                }
            }
            GUILayout.Space(12);
        }

        private void DrawPaths()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                _showPaths = EditorGUILayout.Foldout(_showPaths, "输出位置", true, EditorStyles.foldoutHeader);
                if (_showPaths)
                {
                    GUILayout.Space(5);
                    Field("prefabOutputPath", "预制体", "生成的 Unity UI 预制体路径，不含扩展名。");
                    Field("assetOutputDirectoryPath", "图片目录", "切图资源的保存目录。");
                    Field("metaOutputPath", "导入记录", "保存下次增量导入需要的记录，不含扩展名。");
                    Field("fontDirectoryPath", "字体目录", "查找 XD 文字使用的字体。");
                    EditorGUILayout.LabelField("路径中的 {name} 会替换为画板名称。", XdEditorStyles.Muted);
                }
            }
            GUILayout.Space(6);
        }

        private void DrawTexture()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                _showTexture = EditorGUILayout.Foldout(_showTexture, "图片处理", true, EditorStyles.foldoutHeader);
                if (_showTexture)
                {
                    GUILayout.Space(5);
                    Field("spriteSaveScale", "导出倍率", "影响生成图片的分辨率；修改后会自动更新相关图片。");
                    Field("texturePreset", "纹理预设", "统一设置纹理格式、压缩和过滤。预设内容变化后会自动更新。");
                    Field("m_MatchXdShadowAppearance", "透明混合匹配 XD", "为导出图片、矢量和字体使用透明混合补偿材质，在最终 Alpha 叠加后统一处理；不修改源贴图 Alpha，也不改变项目色彩空间。");
                    if (serializedObject.FindProperty("m_MatchXdShadowAppearance").boolValue)
                        Field("m_TransparencyReferenceBackground", "补偿基准底色", "默认白色，适合浅色 UI。匹配此底色上的 XD 混合结果；任意变化的后景无法仅靠透明材质完全复现。");
                    if (serializedObject.FindProperty("spriteSaveScale").floatValue <= 0)
                        EditorGUILayout.HelpBox("导出倍率需要大于 0。", MessageType.Warning);
                }
            }
            GUILayout.Space(6);
        }

        private void DrawReimport()
        {
            var layout = serializedObject.FindProperty("reimportLayout");
            var assets = serializedObject.FindProperty("reimportAsset");
            bool forced = layout.boolValue || assets.boolValue;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.BeginHorizontal();
                _showReimport = EditorGUILayout.Foldout(_showReimport, "导入方式", true, EditorStyles.foldoutHeader);
                GUILayout.Label(forced ? "强制重导" : "自动增量", XdEditorStyles.Status(forced), GUILayout.Width(72));
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.LabelField(forced ? "已启用手动重建，将重新处理所选画板。" : "自动检测改图和配置变化，只更新需要处理的资源。", XdEditorStyles.Muted);
                if (_showReimport)
                {
                    GUILayout.Space(5);
                    EditorGUILayout.PropertyField(layout, new GUIContent("强制重导布局", "重新生成所选画板的预制体，图片仍按变化情况更新。"));
                    EditorGUILayout.PropertyField(assets, new GUIContent("强制重导图片", "重新切图并导入所选画板的图片，无论图片是否变化。"));
                    if (forced && GUILayout.Button("恢复自动增量", EditorStyles.miniButton)) layout.boolValue = assets.boolValue = false;
                }
            }
            GUILayout.Space(6);
        }

        private void DrawAdvanced()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                _showAdvanced = EditorGUILayout.Foldout(_showAdvanced, "高级设置", true, EditorStyles.foldoutHeader);
                if (_showAdvanced)
                {
                    GUILayout.Space(5);
                    Field("triggers", "导入处理器", "按列表顺序执行的资源与预制体处理器。");
                    Field("objectParsers", "对象解析器", "自定义 XD 对象的解析方式。");
                    Field("groupParsers", "分组解析器", "自定义 XD 分组的解析方式。");
                    Field("xdTriggers", "XD 处理器", "解析 XD 时执行的扩展处理。");
                    Field("layoutOutputPath", "AKY 导出目录", "留空时不额外导出 .aky 文件。");
                    Field("logType", "日志级别", "控制导出过程的日志输出。");
                }
            }
        }

        private void Field(string property, string label, string tooltip)
        {
            EditorGUILayout.PropertyField(serializedObject.FindProperty(property), new GUIContent(label, tooltip), true);
        }

        private static bool IsXdFile(string path) => string.Equals(Path.GetExtension(path), ".xd", StringComparison.OrdinalIgnoreCase) && File.Exists(path);

        private void QueueSelection(string[] paths)
        {
            // Commit inspector edits before opening a window or importing on a later GUI event.
            serializedObject.ApplyModifiedProperties();
            var settings = (XdImportSettings) target;
            EditorApplication.delayCall += () => { if (settings != null) OpenSelectionSequential(settings, paths, 0); };
        }

        private void OpenSelectionSequential(XdImportSettings settings, IReadOnlyList<string> paths, int index)
        {
            if (index >= paths.Count || settings == null) return;
            if (!File.Exists(paths[index]))
            {
                EditorUtility.DisplayDialog("XD 导出", "找不到文件：\n" + paths[index], "确定");
                return;
            }
            XdArtboardSelectionWindow.Show(paths[index], selectedArtboards =>
            {
                _busy = true;
                _lastImportStatus = null;
                try
                {
                    XdImporter.Import(settings, paths[index], selectedArtboards);
                    _historyHolder.Save(new[] { paths[index] });
                }
                catch (OperationCanceledException)
                {
                    _lastImportStatus = "导入已取消。已完成的资源会保留，可重新选择画板继续。";
                    Debug.Log("[UniXD.Xd] " + _lastImportStatus);
                    return;
                }
                finally { _busy = false; if (this != null) Repaint(); }
                OpenSelectionSequential(settings, paths, index + 1);
            }, () => { }, settings.ReimportLayout || settings.ReimportAsset, settings);
        }
    }

    internal static class XdEditorStyles
    {
        private static bool _initialized;
        private static bool _dark;
        internal static GUIStyle Title, Section, RowTitle, Muted, PathLabel, CenterTitle, CenterMuted, Badge, Primary;
        internal static Color Accent => new Color(.16f, .42f, .80f);
        internal static Color Surface => EditorGUIUtility.isProSkin ? new Color(.18f, .19f, .21f) : new Color(.92f, .93f, .95f);
        internal static Color Selected => EditorGUIUtility.isProSkin ? new Color(.19f, .27f, .38f) : new Color(.80f, .87f, .98f);
        internal static Color Line => EditorGUIUtility.isProSkin ? new Color(.28f, .29f, .32f) : new Color(.75f, .77f, .80f);
        internal static Color Secondary => EditorGUIUtility.isProSkin ? new Color(.66f, .70f, .76f) : new Color(.34f, .38f, .44f);

        internal static void Ensure()
        {
            if (_initialized && _dark == EditorGUIUtility.isProSkin) return;
            _initialized = true; _dark = EditorGUIUtility.isProSkin;
            Title = new GUIStyle(EditorStyles.boldLabel) { fontSize = 18, clipping = TextClipping.Clip };
            Section = new GUIStyle(EditorStyles.boldLabel) { fontSize = 12 };
            RowTitle = new GUIStyle(Section) { clipping = TextClipping.Clip };
            Muted = new GUIStyle(EditorStyles.wordWrappedMiniLabel) { fontSize = 11, padding = new RectOffset(0, 0, 2, 3) };
            Muted.normal.textColor = Secondary;
            PathLabel = new GUIStyle(EditorStyles.miniLabel) { clipping = TextClipping.Clip }; PathLabel.normal.textColor = Secondary;
            CenterTitle = new GUIStyle(Section) { alignment = TextAnchor.MiddleCenter };
            CenterMuted = new GUIStyle(PathLabel) { alignment = TextAnchor.MiddleCenter };
            Badge = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter, fontSize = 10, padding = new RectOffset(7, 7, 1, 1) };
            Primary = new GUIStyle(EditorStyles.label) { fontStyle = FontStyle.Bold, fontSize = 12, alignment = TextAnchor.MiddleCenter };
            Primary.normal.textColor = Primary.hover.textColor = Primary.active.textColor = Primary.focused.textColor = Color.white;
        }

        internal static GUIStyle Status(bool warning)
        {
            Ensure();
            var style = new GUIStyle(Badge);
            style.normal.textColor = warning ? (EditorGUIUtility.isProSkin ? new Color(1f, .76f, .41f) : new Color(.57f, .31f, .04f)) : (EditorGUIUtility.isProSkin ? new Color(.48f, .81f, .69f) : new Color(.12f, .45f, .34f));
            return style;
        }

        internal static void Header(string title, string subtitle)
        {
            Ensure();
            var rect = GUILayoutUtility.GetRect(0, 64, GUILayout.ExpandWidth(true));
            var mark = new Rect(rect.x + 2, rect.y + 12, 36, 36);
            EditorGUI.DrawRect(mark, new Color(.38f, .13f, .30f));
            var xd = new GUIStyle(CenterTitle); xd.normal.textColor = new Color(1f, .52f, .79f); xd.fontSize = 17;
            GUI.Label(mark, "Xd", xd);
            GUI.Label(new Rect(rect.x + 49, rect.y + 9, rect.width - 51, 26), title, Title);
            GUI.Label(new Rect(rect.x + 49, rect.y + 36, rect.width - 51, 18), subtitle, PathLabel);
        }

        internal static bool PrimaryButton(string label, float height, params GUILayoutOption[] options)
        {
            var rect = GUILayoutUtility.GetRect(new GUIContent(label), Primary,
                options.Concat(new[] { GUILayout.Height(height), GUILayout.ExpandWidth(true) }).ToArray());
            var color = GUI.enabled ? (rect.Contains(Event.current.mousePosition) ? Color.Lerp(Accent, Color.white, .06f) : Accent) : Surface;
            EditorGUI.DrawRect(rect, color);
            Border(rect, GUI.enabled ? Color.Lerp(color, Color.white, .12f) : Line);
            return GUI.Button(rect, label, Primary);
        }

        internal static void Border(Rect rect, Color color)
        {
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, 1), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 1, rect.width, 1), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, 1, rect.height), color);
            EditorGUI.DrawRect(new Rect(rect.xMax - 1, rect.y, 1, rect.height), color);
        }
    }
}
