using System.Collections.Generic;
using System;
using System.Linq;
using BIEngine.UniXD.Generator.InternalTrigger;
using UnityEngine;

namespace BIEngine.UniXD.Generator
{
    public static class PrefabGenerator
    {
        public static (GameObject, uint Hash, IDictionary<uint, GameObject> EidMap) GenerateGameObject(IAssetLoader assetLoader, LayoutInfo layoutInfo, IGenerateTrigger[] triggers)
        {
            var triggersWithDefault = triggers.Concat(new[] { new DefaultGenerateTrigger() }).ToArray();
            var eidMap = new Dictionary<uint, GameObject>();
            var gameObject = CreateGameObject(assetLoader, layoutInfo, layoutInfo.Root, null, eidMap, triggersWithDefault);
            MakeHelperNamesUnique(gameObject.transform, new HashSet<GameObject>(eidMap.Values));
            return (gameObject, layoutInfo.Hash, eidMap);
        }

        private static void MakeHelperNamesUnique(Transform parent, ISet<GameObject> layoutObjects)
        {
            var children = parent.Cast<Transform>().ToArray();
            var reserved = new HashSet<string>(children.Select(x => x.name), StringComparer.OrdinalIgnoreCase);
            var used = new HashSet<string>(children.Where(x => layoutObjects.Contains(x.gameObject)).Select(x => x.name), StringComparer.OrdinalIgnoreCase);
            foreach (var child in children)
            {
                // Keep the names shown in the XD preview; generated helper references
                // use object identity, so a conflicting helper can safely be renamed.
                if (!layoutObjects.Contains(child.gameObject) && !used.Add(child.name))
                {
                    var original = child.name;
                    var number = 0;
                    string candidate;
                    do { candidate = original + "_" + ++number; } while (reserved.Contains(candidate) || !used.Add(candidate));
                    child.name = candidate;
                }
                MakeHelperNamesUnique(child, layoutObjects);
            }
        }

        private static GameObject CreateGameObject(IAssetLoader assetLoader, LayoutInfo layoutInfo, uint eid, Transform parent, IDictionary<uint, GameObject> eidMap, IGenerateTrigger[] triggers)
        {
            (Vector2 Min, Vector2 Max) CalcAnchor(AnchorXType x, AnchorYType y)
            {
                var anchorMin = Vector2.zero;
                var anchorMax = Vector2.zero;

                switch (x)
                {
                    case AnchorXType.Left:
                        anchorMin.x = 0.0f;
                        anchorMax.x = 0.0f;
                        break;
                    case AnchorXType.Center:
                        anchorMin.x = 0.5f;
                        anchorMax.x = 0.5f;
                        break;
                    case AnchorXType.Right:
                        anchorMin.x = 1.0f;
                        anchorMax.x = 1.0f;
                        break;
                    case AnchorXType.Stretch:
                        anchorMin.x = 0.0f;
                        anchorMax.x = 1.0f;
                        break;
                }

                switch (y)
                {
                    case AnchorYType.Top:
                        anchorMin.y = 1.0f;
                        anchorMax.y = 1.0f;
                        break;
                    case AnchorYType.Middle:
                        anchorMin.y = 0.5f;
                        anchorMax.y = 0.5f;
                        break;
                    case AnchorYType.Bottom:
                        anchorMin.y = 0.0f;
                        anchorMax.y = 0.0f;
                        break;
                    case AnchorYType.Stretch:
                        anchorMin.y = 0.0f;
                        anchorMax.y = 1.0f;
                        break;
                }

                return (anchorMin, anchorMax);
            }

            var element = layoutInfo.Elements.Single(x => x.Eid == eid);

            if (element is ObjectElement objectElement)
            {
                var gameObject = new GameObject(objectElement.Name);
                eidMap.Add(element.Eid, gameObject);
                gameObject.transform.SetParent(parent);

                var rectTransform = gameObject.AddComponent<RectTransform>();
                var (anchorMin, anchorMax) = CalcAnchor(objectElement.AnchorX, objectElement.AnchorY);
                gameObject.SetActive(objectElement.Visible);
                rectTransform.anchoredPosition = new Vector2(objectElement.Position.x, -objectElement.Position.y);
                rectTransform.localRotation = Quaternion.AngleAxis(objectElement.Rotation, Vector3.back);
                var p = rectTransform.localPosition;
                rectTransform.anchorMin = anchorMin;
                rectTransform.anchorMax = anchorMax;
                rectTransform.localPosition = p;
                rectTransform.SetSize(objectElement.Size);

                foreach (var child in objectElement.Children)
                {
                    CreateGameObject(assetLoader, layoutInfo, child, rectTransform, eidMap, triggers);
                }

                foreach (var component in objectElement.Components)
                {
                    CreateComponent(assetLoader, gameObject, component, triggers);
                }

                return gameObject;
            }

            Debug.LogError($"Unknown element type {element}");
            return null;
        }

        private static void CreateComponent(IAssetLoader assetLoader, GameObject gameObject, IComponent component, IGenerateTrigger[] triggers)
        {
            foreach (var trigger in triggers)
            {
                var result = trigger.CreateComponent(gameObject, component, assetLoader);
                if (result != null)
                {
                    foreach (var postprocessTrigger in triggers) postprocessTrigger.OnPostprocessComponent(gameObject, component);
                    return;
                }
            }

            Debug.LogError($"Unknown component type {component}");
        }
    }

    public static class PrefabGeneratorExtensions
    {
        public static void SetSize(this RectTransform rectTransform, Vector2 size)
        {
            rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, size.x);
            rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, size.y);
        }
    }
}
