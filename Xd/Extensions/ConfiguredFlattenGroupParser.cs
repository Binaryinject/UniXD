using UnityEngine;
using BIEngine.UniXD.Xd.Parsing.Internal;

namespace BIEngine.UniXD.Xd.Extensions
{
    [CreateAssetMenu(menuName = "UniXD/XD/GroupParsers/ConfiguredFlattenGroupParser", fileName = nameof(ConfiguredFlattenGroupParser))]
    public class ConfiguredFlattenGroupParser : XdGroupParser
    {
        private readonly SvgGroupParser _parser = new SvgGroupParser(true);

        public override bool Is(XdObjectJson xdObject, XdObjectJson[] parents) => _parser.Is(xdObject, parents);
        public override Rect CalcSize(XdObjectJson xdObject, Rect rect) => _parser.CalcSize(xdObject, rect);
        public override (IComponent[], IAsset[]) Render(XdObjectJson xdObject, XdAssetHolder assetHolder, IObbGetter obbGetter) => _parser.Render(xdObject, assetHolder, obbGetter);
    }
}
