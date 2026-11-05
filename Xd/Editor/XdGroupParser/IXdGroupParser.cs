using JetBrains.Annotations;
using UnityEngine;
using BIEngine.UniXD.Xd.Parsing.Internal;

namespace BIEngine.UniXD.Xd
{
    public interface IXdGroupParser
    {
        bool Is(XdObjectJson xdObject, XdObjectJson[] parents);
        Rect CalcSize(XdObjectJson xdObject, Rect rect);
        (IComponent[], IAsset[]) Render(XdObjectJson xdObject, XdAssetHolder assetHolder, IObbGetter obbGetter);
    }

    public abstract class XdGroupParser : ScriptableObject, IXdGroupParser
    {
        public virtual bool IsAutomaticFlatten(XdObjectJson node, System.Collections.Generic.IReadOnlyDictionary<string, XdObjectJson> symbols = null) => false;
        public abstract bool Is(XdObjectJson xdObject, XdObjectJson[] parents);
        public abstract Rect CalcSize(XdObjectJson xdObject, Rect rect);
        public abstract (IComponent[], IAsset[]) Render(XdObjectJson xdObject, XdAssetHolder assetHolder, IObbGetter obbGetter);
    }
}
