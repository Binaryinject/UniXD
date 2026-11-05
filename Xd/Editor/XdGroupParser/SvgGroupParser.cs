using System.Collections.Generic;
using System.Linq;
using BIEngine.UniXD.Loader;
using Unity.VectorGraphics;
using UnityEngine;
using BIEngine.UniXD.Xd.Parsing;
using BIEngine.UniXD.Xd.Parsing.Internal;

namespace BIEngine.UniXD.Xd
{
    public class SvgGroupParser : IXdGroupParser
    {
        private readonly bool _configuredOnly;
        public SvgGroupParser(bool configuredOnly = false) { _configuredOnly = configuredOnly; }

        public static string GroupId(XdObjectJson node) => node.Id ?? node.Guid;

        public bool Is(XdObjectJson xdObject, XdObjectJson[] parents)
        {
            if (!XdVectorExport.CanFlatten(xdObject, XdImporter.VectorLayerIds)) return false;
            if (_configuredOnly)
            {
                if (xdObject.Type != "group") return false;
                if (!XdImporter.FlattenGroupIds.Contains(GroupId(xdObject))) return false;
                if (!CanFlatten(xdObject)) throw new BIEngine.UniXD.Editor.ImportException($"合图分组“{xdObject.Name}”含文字或不支持的图层，请将这些图层移出合图分组。");
                return true;
            }
            var isLinkedElement = new[] { xdObject }.Concat(parents).Any(x =>
            {
                var hasParameter = x.HasParameter("vector");
                return hasParameter;
            });

            bool IsShapeOnly(XdObjectJson x)
            {
                if (x.Type != "group" && !ShapeObjectParser.Is(x)) return false;
                return (x.Group?.Children ?? new XdObjectJson[] { }).All(IsShapeOnly);
            }

            return isLinkedElement && IsShapeOnly(xdObject);
        }

        public static bool CanFlatten(XdObjectJson node)
        {
            if (node.Visible == false) return true;
            if (node.Type == "syncRef") return true; // Resolved instances are validated again during import.
            if (node.Group != null) return node.Group.Children.All(CanFlatten);
            return node.Type == "shape" && node.Shape != null && SvgUtil.Types.Contains(node.Shape.Type);
        }

        public static bool IsPureVectorGroup(XdObjectJson node, IReadOnlyDictionary<string, XdObjectJson> symbols = null)
        {
            var root = node;
            if (node.Type == "syncRef" && symbols != null) symbols.TryGetValue(node.SyncSourceGuid ?? "", out root);
            return node.Visible != false && root?.Group != null && CountVectors(node, symbols, new HashSet<string>(), out var count) && count > 0;
        }

        private static bool CountVectors(XdObjectJson node, IReadOnlyDictionary<string, XdObjectJson> symbols, HashSet<string> references, out int count)
        {
            count = 0;
            if (node.Visible == false) return true;
            if (node.Style?.Fill?.Type == "pattern" || node.Style?.Fill?.Pattern != null) return false;
            if (node.Type == "syncRef")
            {
                var id = node.SyncSourceGuid;
                if (id == null || symbols == null || !symbols.TryGetValue(id, out var source) || !references.Add(id)) return false;
                try { return CountVectors(source, symbols, references, out count); }
                finally { references.Remove(id); }
            }
            if (node.Group != null)
            {
                foreach (var child in node.Group.Children ?? System.Array.Empty<XdObjectJson>())
                {
                    if (!CountVectors(child, symbols, references, out var childCount)) return false;
                    count += childCount;
                }
                return true;
            }
            if (node.Type != "shape" || node.Shape == null || !SvgUtil.Types.Contains(node.Shape.Type)) return false;
            count = 1;
            return true;
        }

        public Rect CalcSize(XdObjectJson xdObject, Rect rect)
        {
            var svg = SvgUtil.CreateSvg(xdObject, null, true, _configuredOnly);
            var bounds = SvgUtil.CalcBounds(svg);
            return bounds;
        }

        public (IComponent[], IAsset[]) Render(XdObjectJson xdObject, XdAssetHolder assetHolder, IObbGetter obbGetter)
        {
            var components = new List<IComponent>();
            var assets = new List<IAsset>();

            var obb = obbGetter.Get(xdObject);
            var size = obb.Size;
            var svg = SvgUtil.CreateSvg(xdObject, obb, false, _configuredOnly);

            if (assetHolder.IsVector(xdObject))
            {
                var (image, vectorAssets) = XdVectorExport.Render(xdObject, svg, size, true, assetHolder);
                xdObject.Group.Children = new XdObjectJson[] { };
                return (new IComponent[] { image }, vectorAssets);
            }
            xdObject.Group.Children = new XdObjectJson[] { };

            var spriteUid = $"{xdObject.GetSimpleName()}_{xdObject.Id.Substring(0, 8)}.png";
            var svgHash = FastHash.CalculateHash(svg);

            var cachedSvg = assetHolder.GetCachedSvg(svgHash);
            if (cachedSvg != null)
            {
                spriteUid = cachedSvg.SpriteUid;
            }
            else
            {
                // A non-null border disables BIEngine.UniXD.Slicing, even when all four values are zero.
                var border = xdObject.HasParameter("NoSlice") ? new BIEngine.UniXD.Border(0, 0, 0, 0) : null;
                assets.Add(new SpriteAsset(spriteUid, svgHash, size, null, border));
                var xdImportSettings = XdImporter.Settings;
                assetHolder.Save(spriteUid, () => SvgToPng.Convert(svg, size, ViewportOptions.PreserveViewport, xdImportSettings), "PreserveViewport\n" + svg);
                assetHolder.SaveCacheSvg(spriteUid, svgHash);
            }
            components.Add(new ImageComponent(
                spriteUid,
                new Color(1f, 1f, 1f, xdObject.Style?.Opacity ?? 1f),
                Vector2Int.one,
                svgHash
            ));

            return (components.ToArray(), assets.ToArray());
        }
    }
}
