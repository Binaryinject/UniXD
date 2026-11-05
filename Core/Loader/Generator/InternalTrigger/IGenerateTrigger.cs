using UnityEngine;

namespace BIEngine.UniXD.Generator.InternalTrigger
{
    public interface IGenerateTrigger
    {
        Component CreateComponent(GameObject gameObject, IComponent component, IAssetLoader assetLoader);
        void OnPostprocessComponent(GameObject gameObject, IComponent component);
    }
}