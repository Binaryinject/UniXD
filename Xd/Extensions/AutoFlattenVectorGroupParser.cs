using System.Collections.Generic;
using UnityEngine;
using BIEngine.UniXD.Xd.Parsing.Internal;

namespace BIEngine.UniXD.Xd.Extensions
{
    [CreateAssetMenu(menuName = "UniXD/XD/GroupParsers/AutoFlattenVectorGroupParser", fileName = nameof(AutoFlattenVectorGroupParser))]
    public class AutoFlattenVectorGroupParser : XdGroupParser
    {
        private readonly SvgGroupParser _parser = new SvgGroupParser(true);

        public override bool IsAutomaticFlatten(XdObjectJson node, IReadOnlyDictionary<string, XdObjectJson> symbols = null) =>
            !XdExportRules.IsFlattenExcluded(node) && SvgGroupParser.IsPureVectorGroup(node, symbols);

        public override bool Is(XdObjectJson xdObject, XdObjectJson[] parents) => xdObject.Type == "group" &&
            XdVectorExport.CanFlatten(xdObject) && IsAutomaticFlatten(xdObject);
        public override Rect CalcSize(XdObjectJson xdObject, Rect rect) => _parser.CalcSize(xdObject, rect);
        public override (IComponent[], IAsset[]) Render(XdObjectJson xdObject, XdAssetHolder assetHolder, IObbGetter obbGetter) => _parser.Render(xdObject, assetHolder, obbGetter);
    }
}
