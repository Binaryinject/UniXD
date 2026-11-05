using BIEngine.UniXD.Xd.Parsing.Internal;

namespace BIEngine.UniXD.Xd
{
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "AkyuiUnity.Xd", "AkyuiUnity.Xd.Editor", "AkyuiXdImportTrigger")]
    public abstract class XdImportTrigger : UnityEngine.ScriptableObject
    {
        public virtual string settingsTitle => null;
        public virtual IComponent[] OnBuildComponents(XdObjectJson node, IComponent[] components, string exportedName) => components;
        public virtual bool ShouldUseGroupParser(IXdGroupParser parser, XdObjectJson node, XdObjectJson[] parents) => true;

        // Applies to exported artboard names, layer names and manual overrides, never text content.
        public virtual string OnExportName(string name) => name;

        // Called once per artboard after resolving symbols; preview also supplies the source symbol map.
        public virtual void OnBuildLayerNames(XdObjectJson[] roots, System.Collections.Generic.IDictionary<string, string> names,
            System.Collections.Generic.IReadOnlyDictionary<string, XdObjectJson> symbols) { }

        public virtual XdObjectJson OnCreateXdObject(XdObjectJson xdObject)
        {
            return xdObject;
        }
    }
}
