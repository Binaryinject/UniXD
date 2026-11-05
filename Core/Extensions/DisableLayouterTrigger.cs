using BIEngine.UniXD.Editor.ScriptableObject;
using BIEngine.UniXD.Loader;
using UnityEngine;
using UnityEngine.UI;

namespace BIEngine.UniXD.Extensions
{
    [CreateAssetMenu(menuName = "UniXD/Triggers/DisableLayouter", fileName = nameof(DisableLayouterTrigger))]
    public class DisableLayouterTrigger : ImportTrigger
    {
        public override void OnPostprocessPrefab(ILoader loader, ref GameObject prefab)
        {
            foreach (var layoutGroup in prefab.GetComponentsInChildren<LayoutGroup>(true))
            {
                layoutGroup.enabled = false;
            }
        }
    }
}