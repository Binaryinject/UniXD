#if UNIXD_ANKUCHEN_SUPPORT
using System.Collections.Generic;
using BIEngine.UniXD.Editor.ScriptableObject;
using BIEngine.UniXD.Loader;
using AnKuchen.AdditionalInfo;
using UnityEngine;

namespace BIEngine.UniXD.Extensions
{
    [CreateAssetMenu(menuName = "UniXD/Triggers/SetupAnKuchen", fileName = nameof(SetupAnKuchenTrigger))]
    public class SetupAnKuchenTrigger : ImportTrigger
    {
        public override void OnPostprocessPrefab(ILoader loader, ref GameObject prefab)
        {
            var uiCache = prefab.AddComponent<UICache>();
            uiCache.CreateCache();
        }

        public override void OnPostprocessComponent(GameObject gameObject, IComponent component)
        {
            if (component is IListComponent listComponent)
            {
                if (gameObject.GetComponent<ListAdditionalInfo>() != null) return;

                var additionalInfo = gameObject.AddComponent<ListAdditionalInfo>();

                var spacialSpacings = new List<AnKuchen.AdditionalInfo.SpecialSpacing>();
                if (listComponent.SpacialSpacings != null)
                {
                    foreach (var a in listComponent.SpacialSpacings)
                    {
                        spacialSpacings.Add(new AnKuchen.AdditionalInfo.SpecialSpacing
                        {
                            item1 = a.Item1,
                            item2 = a.Item2,
                            spacing = a.Spacing,
                        });
                    }
                }

                additionalInfo.specialSpacings = spacialSpacings.ToArray();
            }
        }
    }
}
#endif
