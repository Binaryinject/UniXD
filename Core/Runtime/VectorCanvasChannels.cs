using UnityEngine;

namespace BIEngine.UniXD
{
    // SVG gradients store their paint index in UV2. Canvas strips it unless the
    // channel is enabled, including when a generated prefab is parented at runtime.
    [ExecuteAlways, DisallowMultipleComponent, AddComponentMenu("")]
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "AkyuiUnity", "AkyuiUnity", "AkyuiVectorCanvasChannels")]
    public sealed class VectorCanvasChannels : MonoBehaviour
    {
        private void OnEnable() => EnsureChannels();
        private void OnTransformParentChanged() => EnsureChannels();
        private void OnCanvasHierarchyChanged() => EnsureChannels();

        private void EnsureChannels()
        {
            foreach (var parent in GetComponentsInParent<Canvas>(true))
                if ((parent.additionalShaderChannels & AdditionalCanvasShaderChannels.TexCoord2) == 0)
                    parent.additionalShaderChannels |= AdditionalCanvasShaderChannels.TexCoord2;
        }
    }
}
