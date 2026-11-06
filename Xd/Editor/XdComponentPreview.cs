using System;
using System.Collections.Generic;
using System.Linq;
using BIEngine.UniXD.Editor.ScriptableObject;
using BIEngine.UniXD.Xd.Parsing.Internal;
namespace BIEngine.UniXD.Xd
{
    internal static class XdComponentPreview
    {
        internal static string Preview(XdObjectJson node, bool flattened, XdScrollViewMode mode, XdImportSettings settings,
            string exported, string path, IReadOnlyDictionary<string, XdObjectJson> symbols, bool keepVector = false)
        {
            node = XdExportRules.ResolvePreviewNode(node, symbols);
            var hasShadows = XdShadowRenderer.HasShadows(node);
            var planned = new List<IComponent>();
            if (flattened || node.Type == "shape" || node.Type == "image")
                planned.Add(keepVector ? new VectorImageComponent(null, null, null, null) : new ImageComponent(null, null, null, null));
            else if (node.Type == "text")
                planned.Add(new TextComponent(null, null, null, null, null, null, null));

            // A flattened node has no live children, and cannot also be a scrolling container.
            if (!flattened && !string.IsNullOrWhiteSpace(node.Meta?.Ux?.ScrollingType) && mode != XdScrollViewMode.None)
            {
                if (node.Meta.Ux.ScrollingType == "vertical")
                    planned.Add(new VerticalListComponent(null, null, null, null) { generateOsaScrollView = mode == XdScrollViewMode.Osa });
                else planned.Add(new HorizontalListComponent(null, null, null, null) { generateOsaScrollView = mode == XdScrollViewMode.Osa });
            }
            var components = planned.ToArray();
            foreach (var trigger in settings?.XdTriggers ?? Array.Empty<XdImportTrigger>())
                components = trigger.OnBuildComponents(node, components, exported);
            var names = new List<string>();
            var triggers = settings?.Triggers.OfType<ImportTrigger>().ToArray() ?? Array.Empty<ImportTrigger>();
            foreach (var component in components)
            {
                string[] custom = null;
                foreach (var trigger in triggers)
                {
                    custom = trigger.PreviewCreatedComponents(component, path);
                    if (custom != null) break;
                }
                if (custom != null) { names.AddRange(custom); continue; }
                if (component is VectorImageComponent) names.Add("SVGImage");
                else if (component is ImageComponent) names.Add("Image");
                else if (component is TextComponent) names.Add("Text");
                else if (component is ButtonComponent)
                {
                    if (!components.Any(x => x is ImageComponent || x is TextComponent || x is MaskComponent)) names.Add("Image");
                    names.Add("Button");
                }
                else if (component is ToggleComponent) names.Add("Toggle");
                else if (component is InputFieldComponent) names.Add("InputField");
                else if (component is IListComponent) names.AddRange(new[] { "ScrollRect", "RectMask2D" });
            }
            foreach (var trigger in triggers) trigger.PreviewAdditionalComponents(names);
            if (XdInnerShadowRenderer.HasShadows(node)) names.Add("内阴影（合入 PNG，尺寸不变）");
            if (hasShadows) names.Add("软阴影（独立装饰层，不拦截点击）");
            return names.Count == 0 ? "无（普通分组）" : string.Join("、", names.Distinct());
        }
    }
}
