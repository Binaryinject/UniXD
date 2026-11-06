using System;
using System.Collections.Generic;
using BIEngine.UniXD.Editor;
using BIEngine.UniXD.Editor.ScriptableObject;
using BIEngine.UniXD.Generator;
using BIEngine.UniXD.Loader;
using UnityEngine;
namespace BIEngine.UniXD.Extensions {
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "EBGame.EditorTools", "EBGame.Editor", "OsaScrollViewExportTrigger")]
    [CreateAssetMenu(menuName = "UniXD/Triggers/OSA Scroll View", fileName = "OsaScrollViewExportTrigger")]
    public sealed class OsaScrollViewExportTrigger : ImportTrigger {
        public enum AdapterType { List, Grid }
        [Serializable] public sealed class ScrollViewSetting {
            [Tooltip("相对 Prefab 根节点的完整路径，或滚动节点名称。完整路径优先。")]
            public string path;
            public AdapterType adapterType;
            public string itemTemplateName;
            public int maxCellsPerGroup;
        }
        [SerializeField] private AdapterType m_DefaultAdapterType = AdapterType.List;
        [SerializeField] private List<ScrollViewSetting> m_ScrollViews = new List<ScrollViewSetting>();
        private static Func<OsaScrollViewExportTrigger, ImportTrigger> m_BackendFactory;
        private ImportTrigger m_Backend;
        public AdapterType defaultAdapterType => m_DefaultAdapterType;
        public IReadOnlyList<ScrollViewSetting> scrollViews => m_ScrollViews;
        public static void RegisterBackend(Func<OsaScrollViewExportTrigger, ImportTrigger> factory) => m_BackendFactory = factory;
        private ImportTrigger backend => m_Backend != null ? m_Backend : m_BackendFactory == null ? null : (m_Backend = m_BackendFactory(this));
        public override Component CreateComponent(GameObject target, IComponent component, IAssetLoader assets) {
            if (component is IListComponent list && list.generateOsaScrollView && backend == null)
                throw new ImportException("OSA 导出需要安装 OSA.Core；请安装依赖或改用普通 ScrollView。");
            return backend?.CreateComponent(target, component, assets);
        }
        public override string[] PreviewCreatedComponents(IComponent component, string path) => backend?.PreviewCreatedComponents(component, path);
        public override void OnPostprocessPrefab(ILoader loader, ref GameObject prefab) { if (backend != null) backend.OnPostprocessPrefab(loader, ref prefab); }
        private void OnDisable() { if (m_Backend != null) DestroyImmediate(m_Backend); m_Backend = null; }
    }
}
