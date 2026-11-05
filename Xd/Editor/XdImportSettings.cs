using System.Linq;
using BIEngine.UniXD.Editor.ScriptableObject;
using UnityEngine;

namespace BIEngine.UniXD.Xd
{
    [CreateAssetMenu(menuName = "UniXD/XD/XdImportSettings", fileName = "XdImportSettings")]
    public class XdImportSettings : ImportSettings, IPrefabAppearanceSettings
    {
        public XdObjectParser[] ObjectParsers => objectParsers?.Where(x => x != null).ToArray() ?? new XdObjectParser[] { };
        [SerializeField] private XdObjectParser[] objectParsers = default;

        public XdGroupParser[] GroupParsers => groupParsers?.Where(x => x != null).ToArray() ?? new XdGroupParser[] { };
        [SerializeField] private XdGroupParser[] groupParsers = default;

        public XdImportTrigger[] XdTriggers => xdTriggers?.Where(x => x != null).ToArray() ?? new XdImportTrigger[] { };
        [SerializeField] private XdImportTrigger[] xdTriggers = default;

        public string LayoutOutputPath => layoutOutputPath;
        [UnityEngine.Serialization.FormerlySerializedAs("akyuiOutputPath")]
        [SerializeField] private string layoutOutputPath = "";

        public bool matchXdShadowAppearance => m_MatchXdShadowAppearance;
        public bool matchXdTransparencyAppearance => m_MatchXdShadowAppearance;
        [SerializeField] private bool m_MatchXdShadowAppearance = true;
        public Color transparencyReferenceBackground => m_TransparencyReferenceBackground;
        [SerializeField] private Color m_TransparencyReferenceBackground = Color.white;

        public void ApplyAppearance(GameObject root, BIEngine.UniXD.Editor.IPathGetter paths) => XdTransparencyMaterials.Apply(this, root, paths);
        public string GetAppearanceFingerprint() => XdTransparencyMaterials.GetFingerprint(this);

    }
}
