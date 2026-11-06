using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BIEngine.UniXD.Loader;
using UnityEngine;
using BIEngine.UniXD.Xd.Parsing;
using BIEngine.UniXD.Xd.Parsing.Internal;

namespace BIEngine.UniXD.Xd
{
    public static class XdVectorExport
    {
        public static bool IsEligible(XdObjectJson node, IReadOnlyDictionary<string, XdObjectJson> symbols = null)
        {
            node = XdExportRules.ResolvePreviewNode(node, symbols);
            if (node.Type == "shape") return !XdInnerShadowRenderer.HasOwnShadow(node) && SvgUtil.Types.Contains(node.Shape?.Type) &&
                node.Style?.Fill?.Pattern == null && node.Style?.Fill?.Type != "pattern";
            return node.Group?.Children?.Length > 0 && node.Group.Children.All(x => x.Visible == false || IsEligible(x));
        }

        internal static bool IsRequested(XdObjectJson node, ISet<string> ids) =>
            (node.Id != null && ids.Contains(node.Id)) || (node.Guid != null && ids.Contains(node.Guid));

        public static bool UsesVector(XdObjectJson node, ISet<string> ids,
            IReadOnlyDictionary<string, XdObjectJson> symbols = null)
        {
            node = XdExportRules.ResolvePreviewNode(node, symbols);
            bool Contains(XdObjectJson child) => IsRequested(child, ids) ||
                (child.Group?.Children ?? Array.Empty<XdObjectJson>()).Any(Contains);
            return IsEligible(node) && Contains(node);
        }

        // A PNG merge must not swallow a child explicitly selected for vector output.
        public static bool CanFlatten(XdObjectJson node, ISet<string> ids,
            IReadOnlyDictionary<string, XdObjectJson> symbols = null)
        {
            node = XdExportRules.ResolvePreviewNode(node, symbols);
            bool Contains(XdObjectJson child) => IsRequested(child, ids) ||
                (child.Group?.Children ?? Array.Empty<XdObjectJson>()).Any(Contains);
            return IsEligible(node) || !(node.Group?.Children ?? Array.Empty<XdObjectJson>()).Any(Contains);
        }

        public static bool CanFlatten(XdObjectJson node, IReadOnlyDictionary<string, XdObjectJson> symbols = null) =>
            CanFlatten(node, XdImporter.VectorLayerIds, symbols);

        internal static (ImageComponent, IAsset[]) Render(XdObjectJson node, string svg, Vector2 size,
            bool preserveViewport, XdAssetHolder holder)
        {
            // XD frame/hit-area helpers may contain no painted geometry. Keep
            // their transparent hit graphic instead of importing an empty SVG.
            if (string.IsNullOrWhiteSpace(svg) || SvgUtil.IsAlphaOnly(node))
                return (new ImageComponent(null, Color.clear, Vector2Int.one, null), Array.Empty<IAsset>());
            var hash = FastHash.CalculateHash("Vector-v1\n" + preserveViewport + "\n" + svg);
            var file = node.GetSimpleName() + "_" + (node.Id ?? node.Guid).Substring(0, 8) + ".svg";
            var cached = holder.GetCachedSvg(hash);
            var assets = new List<IAsset>();
            if (cached != null) file = cached.SpriteUid;
            else
            {
                assets.Add(new VectorSpriteAsset(file, hash, size, preserveViewport));
                holder.Save(file, () => Encoding.UTF8.GetBytes(svg), "UISVGImage\n" + preserveViewport + "\n" + svg);
                holder.SaveCacheSvg(file, hash);
            }
            return (new VectorImageComponent(file, new Color(1f, 1f, 1f, node.Style?.Opacity ?? 1f),
                Vector2Int.one, hash), assets.ToArray());
        }
    }
}
