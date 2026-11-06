#if UNIXD_OSA_SUPPORT
using System;
using System.Collections.Generic;
using System.Linq;
using BIEngine.UniXD;
using BIEngine.UniXD.Editor.ScriptableObject;
using BIEngine.UniXD.Generator;
using BIEngine.UniXD.Loader;
using Com.TheFallenGames.OSA.Core;
using Com.TheFallenGames.OSA.CustomAdapters.GridView;
using frame8.Logic.Misc.Visual.UI.MonoBehaviours;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using BIEngine.UniXD.Osa;
using AdapterType = BIEngine.UniXD.Extensions.OsaScrollViewExportTrigger.AdapterType;
using ScrollViewSetting = BIEngine.UniXD.Extensions.OsaScrollViewExportTrigger.ScrollViewSetting;

namespace BIEngine.UniXD.Extensions {
    internal sealed class OsaScrollViewExportBackend : ImportTrigger {
        private static Type FindAdapter(string name, Type fallback, params Type[] arguments) =>
            TypeCache.GetTypesDerivedFrom<Component>().FirstOrDefault(t => t.FullName == name && !t.IsAbstract && t.GetMethod("ConfigureForExport", arguments) != null) ?? fallback;
        private static readonly Type ListType = FindAdapter("UIListAdapter", typeof(UniXDListAdapter), typeof(BaseParams), typeof(RectTransform));
        private static readonly Type GridType = FindAdapter("UIGridAdapter", typeof(UniXDGridAdapter), typeof(GridParams));
        internal OsaScrollViewExportTrigger owner;
        private OsaScrollViewExportTrigger.AdapterType m_DefaultAdapterType => owner.defaultAdapterType;
        private IReadOnlyList<OsaScrollViewExportTrigger.ScrollViewSetting> m_ScrollViews => owner.scrollViews;
        [InitializeOnLoadMethod] private static void Register() {
            OsaScrollViewExportTrigger.RegisterBackend(owner => {
                var instance = CreateInstance<OsaScrollViewExportBackend>();
                instance.hideFlags = HideFlags.HideAndDontSave;
                instance.owner = owner;
                return instance;
            });
        }
        // OSA exposes DefaultItemSize with a protected setter. Unity saves these
        // inherited values inline in BaseParams, so no editor type is needed at runtime.
        [Serializable]
        private sealed class ExportListParams : BaseParams {
            public ExportListParams(float itemSize) {
                DefaultItemSize = itemSize;
            }
        }

        public override Component CreateComponent(GameObject gameObject, IComponent component, IAssetLoader assetLoader) {
            if (component is IListComponent list && !list.generateOsaScrollView) return null;

            if (component is VerticalListComponent vertical) {
                return CreateScrollView(gameObject, vertical, false,
                    new RectOffset(0, 0, Mathf.RoundToInt(vertical.PaddingTop ?? 0f), Mathf.RoundToInt(vertical.PaddingBottom ?? 0f)));
            }

            if (component is HorizontalListComponent horizontal) {
                return CreateScrollView(gameObject, horizontal, true,
                    new RectOffset(Mathf.RoundToInt(horizontal.PaddingLeft ?? 0f), Mathf.RoundToInt(horizontal.PaddingRight ?? 0f), 0, 0));
            }

            // A scrolling node is laid out by OSA rather than a Unity LayoutGroup.
            if (component is GridLayoutComponent || component is VerticalLayoutComponent || component is HorizontalLayoutComponent) {
                var adapter = gameObject.GetComponents<Component>().FirstOrDefault(x => x != null && (x.GetType() == ListType || x.GetType() == GridType));
                if (adapter != null) return adapter;
            }

            return null;
        }

        public override string[] PreviewCreatedComponents(IComponent component, string hierarchyPath) {
            if (!(component is IListComponent list) || !list.generateOsaScrollView) return null;
            var name = (hierarchyPath ?? "").Split('/').LastOrDefault();
            var setting = FindSetting(hierarchyPath, name);
            return new[] { (setting?.adapterType ?? m_DefaultAdapterType) == AdapterType.Grid ? GridType.Name : ListType.Name, "Image", "RectMask2D" };
        }

        public override void OnPostprocessPrefab(ILoader loader, ref GameObject prefab) {
            foreach (var adapter in prefab.GetComponentsInChildren<Component>(true))
                if (adapter != null && (adapter.GetType() == ListType || adapter.GetType() == GridType)) RemoveRootLayout(adapter.gameObject);
        }

        private Component CreateScrollView(GameObject gameObject, IListComponent list, bool horizontal, RectOffset padding) {
            var root = (RectTransform)gameObject.transform;
            var setting = FindSetting(root);
            var adapterType = setting != null ? setting.adapterType : m_DefaultAdapterType;
            var children = Enumerable.Range(0, root.childCount).Select(i => root.GetChild(i)).ToArray();
            var itemRoots = children.Where(x => x.GetComponent<Scrollbar>() == null).ToArray();
            var sourceGrid = itemRoots.Select(x => x.GetComponent<GridLayoutGroup>()).FirstOrDefault(x => x != null);
            var templates = CreateRectTransform("Templates", root);
            // Keep the original parent rectangle while collecting stretched XD templates.
            templates.pivot = root.pivot;
            templates.anchorMin = Vector2.zero;
            templates.anchorMax = Vector2.one;
            templates.offsetMin = Vector2.zero;
            templates.offsetMax = Vector2.zero;
            templates.gameObject.SetActive(false);
            foreach (var child in itemRoots) child.SetParent(templates, false);

            var template = FindTemplate(templates, itemRoots, setting?.itemTemplateName, adapterType, sourceGrid);
            if (template == null) {
                template = CreateRectTransform("ItemTemplate", templates);
                template.sizeDelta = new Vector2(Mathf.Max(1f, root.rect.width), Mathf.Max(1f, root.rect.height));
            }

            var content = CreateRectTransform("Content", root);
            content.anchorMin = Vector2.zero;
            content.anchorMax = Vector2.one;
            content.offsetMin = Vector2.zero;
            content.offsetMax = Vector2.zero;
            content.pivot = horizontal ? new Vector2(0f, 0.5f) : new Vector2(0.5f, 1f);

            var graphic = gameObject.GetComponent<Graphic>();
            if (graphic == null) {
                var image = gameObject.AddComponent<Image>();
                image.color = Color.clear;
                graphic = image;
            }
            graphic.raycastTarget = true;
            if (gameObject.GetComponent<RectMask2D>() == null) gameObject.AddComponent<RectMask2D>();

            var size = GetTemplateSize(template);
            BaseParams parameters;
            Component result;
            if (adapterType == AdapterType.Grid) {
                var cellTemplate = CreateCellTemplate(template, templates, size);
                var gridParams = new GridParams();
                gridParams.Grid.CellPrefab = cellTemplate;
                gridParams.Grid.MaxCellsPerGroup = setting?.maxCellsPerGroup > 0 ? setting.maxCellsPerGroup : -1;
                gridParams.Grid.SpacingInGroup = sourceGrid != null
                    ? (horizontal ? sourceGrid.spacing.y : sourceGrid.spacing.x)
                    : list.Spacing ?? 0f;
                if (sourceGrid != null) {
                    gridParams.ContentSpacing = horizontal ? sourceGrid.spacing.x : sourceGrid.spacing.y;
                }
                else gridParams.ContentSpacing = list.Spacing ?? 0f;

                var adapter = gameObject.AddComponent(GridType);
                GridType.GetMethod("ConfigureForExport").Invoke(adapter, new object[] { gridParams });
                parameters = gridParams;
                result = adapter;
            }
            else {
                var listParams = new ExportListParams(Mathf.Max(1f, horizontal ? size.x : size.y));
                listParams.ContentSpacing = list.Spacing ?? 0f;
                var adapter = gameObject.AddComponent(ListType);
                ListType.GetMethod("ConfigureForExport").Invoke(adapter, new object[] { listParams, template });
                parameters = listParams;
                result = adapter;
            }

            parameters.Content = content;
            parameters.Viewport = root;
            parameters.Orientation = horizontal ? BaseParams.OrientationEnum.HORIZONTAL : BaseParams.OrientationEnum.VERTICAL;
            parameters.ContentPadding = padding;
            // GridAdapter's OSA items are full rows/columns, not individual cells.
            parameters.ItemTransversalSize = adapterType == AdapterType.Grid ? 0f : horizontal ? size.y : size.x;
            parameters.UseUnscaledTime = true;

            var scrollbar = children.Select(x => x.GetComponent<Scrollbar>()).FirstOrDefault(x => x != null);
            parameters.Scrollbar = scrollbar;
            if (scrollbar != null) {
                var fixer = scrollbar.GetComponent<ScrollbarFixer8>() ?? scrollbar.gameObject.AddComponent<ScrollbarFixer8>();
                fixer.viewport = root;
            }

            return result;
        }

        private ScrollViewSetting FindSetting(Transform root) {
            var path = AnimationUtility.CalculateTransformPath(root, root.root);
            return FindSetting(path, root.name);
        }

        private ScrollViewSetting FindSetting(string path, string name) {
            if (m_ScrollViews == null) return null;
            return m_ScrollViews.FirstOrDefault(x => x != null && !string.IsNullOrEmpty(x.path) && x.path == path)
                ?? m_ScrollViews.FirstOrDefault(x => x != null && !string.IsNullOrEmpty(x.path) && x.path == name);
        }

        private static RectTransform FindTemplate(RectTransform templates, Transform[] itemRoots, string templateName, AdapterType adapterType, GridLayoutGroup sourceGrid) {
            if (!string.IsNullOrEmpty(templateName)) {
                var selected = templates.GetComponentsInChildren<RectTransform>(true).FirstOrDefault(x => x != templates && x.name == templateName);
                if (selected != null) return selected;
                throw new InvalidOperationException($"OSA scroll view '{templates.parent.name}' has no item template named '{templateName}'.");
            }

            if (adapterType == AdapterType.Grid && sourceGrid != null && sourceGrid.transform.childCount > 0)
                return sourceGrid.transform.GetChild(0) as RectTransform;
            return itemRoots.FirstOrDefault() as RectTransform;
        }

        private static RectTransform CreateCellTemplate(RectTransform template, RectTransform templates, Vector2 size) {
            var cell = CreateRectTransform("CellTemplate", templates);
            cell.sizeDelta = size;
            var layout = cell.gameObject.AddComponent<LayoutElement>();
            layout.preferredWidth = size.x;
            layout.preferredHeight = size.y;
            layout.flexibleWidth = 0f;
            layout.flexibleHeight = 0f;
            var views = CreateRectTransform("Views", cell);
            views.anchorMin = Vector2.zero;
            views.anchorMax = Vector2.one;
            views.offsetMin = Vector2.zero;
            views.offsetMax = Vector2.zero;
            template.SetParent(views, false);
            template.anchorMin = template.anchorMax = new Vector2(0.5f, 0.5f);
            template.anchoredPosition = Vector2.zero;
            template.sizeDelta = size;
            return cell;
        }

        private static Vector2 GetTemplateSize(RectTransform template) {
            var size = template.rect.size;
            if (size.x <= 0f || size.y <= 0f) size = RectTransformUtility.CalculateRelativeRectTransformBounds(template).size;
            return new Vector2(Mathf.Max(1f, size.x), Mathf.Max(1f, size.y));
        }

        private static RectTransform CreateRectTransform(string name, Transform parent) {
            var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            return rect;
        }

        private static void RemoveRootLayout(GameObject gameObject) {
            foreach (var layout in gameObject.GetComponents<LayoutGroup>()) UnityEngine.Object.DestroyImmediate(layout);
            var fitter = gameObject.GetComponent<ContentSizeFitter>();
            if (fitter != null) UnityEngine.Object.DestroyImmediate(fitter);
        }
    }
}

#endif
