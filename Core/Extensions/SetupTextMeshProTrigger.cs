#if UNIXD_TEXTMESHPRO_SUPPORT
using System;
using System.Linq;
using BIEngine.UniXD.Editor;
using BIEngine.UniXD.Editor.ScriptableObject;
using BIEngine.UniXD.Generator;
using BIEngine.UniXD.Generator.InternalTrigger;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace BIEngine.UniXD.Extensions
{
    // DefaultGenerateTrigger.csのText部分と合わせる
    [CreateAssetMenu(menuName = "UniXD/Triggers/SetupTextMeshPro", fileName = nameof(SetupTextMeshProTrigger))]
    public class SetupTextMeshProTrigger : ImportTrigger
    {
        [SerializeField] private string fontFilePath = "Assets/Fonts/{name} SDF";

        public override string[] PreviewCreatedComponents(IComponent component, string hierarchyPath)
        {
            if (component is TextComponent) return new[] { "TextMeshProUGUI" };
            if (component is InputFieldComponent) return new[] { "TMP_InputField" };
            return null;
        }

        public override Component CreateComponent(GameObject gameObject, IComponent component, IAssetLoader assetLoader)
        {
            if (component is InputFieldComponent) return CreateInputField(gameObject);
            if (component is TextComponent textComponent) return CreateText(gameObject, textComponent);
            return null;
        }

        private Component CreateText(GameObject gameObject, TextComponent textComponent)
        {
            var text = gameObject.AddComponent<TextMeshProUGUI>();
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Overflow;
            text.richText = false;
            text.raycastTarget = false;

            if (textComponent.Text != null) text.text = textComponent.Text;
            if (textComponent.Size != null) text.fontSize = Mathf.RoundToInt(textComponent.Size.Value);
            if (textComponent.Color != null) text.color = textComponent.Color.Value;
            if (textComponent.Align != null)
            {
                switch (textComponent.Align.Value)
                {
                    case TextComponent.TextAlign.UpperLeft:
                        text.alignment = TextAlignmentOptions.TopLeft;
                        break;
                    case TextComponent.TextAlign.UpperCenter:
                        text.alignment = TextAlignmentOptions.Top;
                        break;
                    case TextComponent.TextAlign.UpperRight:
                        text.alignment = TextAlignmentOptions.TopRight;
                        break;
                    case TextComponent.TextAlign.MiddleLeft:
                        text.alignment = TextAlignmentOptions.MidlineLeft;
                        break;
                    case TextComponent.TextAlign.MiddleCenter:
                        text.alignment = TextAlignmentOptions.Midline;
                        break;
                    case TextComponent.TextAlign.MiddleRight:
                        text.alignment = TextAlignmentOptions.MidlineRight;
                        break;
                    case TextComponent.TextAlign.LowerLeft:
                        text.alignment = TextAlignmentOptions.BottomLeft;
                        break;
                    case TextComponent.TextAlign.LowerCenter:
                        text.alignment = TextAlignmentOptions.Bottom;
                        break;
                    case TextComponent.TextAlign.LowerRight:
                        text.alignment = TextAlignmentOptions.BottomRight;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            TMP_FontAsset fontAsset = null;
            if (textComponent.Font != null)
            {
                // XD font mappings provide the selected TMP asset path directly.
                var fontPath = textComponent.Font.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)
                    && (textComponent.Font.StartsWith("Assets/", StringComparison.Ordinal)
                        || textComponent.Font.StartsWith("Packages/", StringComparison.Ordinal))
                    ? textComponent.Font
                    : fontFilePath.Replace("{name}", textComponent.Font) + ".asset";
                fontAsset = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(fontPath);
                if (fontAsset != null)
                {
                    text.font = fontAsset;
                }
                else
                {
                    Debug.LogWarning($"TextMeshPro Font {fontPath} is not found / name: {ExtensionsUtil.GetGameObjectPath(gameObject)}, text: {textComponent.Text}");
                }
            }

            if (textComponent.Wrap != null)
            {
                text.textWrappingMode = textComponent.Wrap.Value ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
            }

            if (textComponent.LineHeight != null && fontAsset != null)
            {
                var fontLineHeight = fontAsset.faceInfo.lineHeight * text.fontSize / fontAsset.faceInfo.pointSize; // 66
                var targetLineHeight = textComponent.LineHeight.Value; // 50
                var em100 = (targetLineHeight - fontLineHeight) / text.fontSize;
                text.lineSpacing = em100 * 100.0f;
            }

            return text;
        }

        private static Component CreateInputField(GameObject gameObject)
        {
            var inputField = gameObject.AddComponent<TMP_InputField>();
            inputField.transition = Selectable.Transition.None;

            var text = gameObject.GetComponentsInDirectChildren<TextMeshProUGUI>().FirstOrDefault();
            if (text == null)
            {
                // Naming rules can add an input to a flattened image with no editable text child.
                // Supply the complete TMP hierarchy instead of exporting a nonfunctional Selectable.
                var viewport = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
                viewport.transform.SetParent(gameObject.transform, false);
                var viewportRect = (RectTransform)viewport.transform;
                viewportRect.anchorMin = Vector2.zero;
                viewportRect.anchorMax = Vector2.one;
                viewportRect.offsetMin = new Vector2(20, 4);
                viewportRect.offsetMax = new Vector2(-20, -4);
                var label = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
                label.transform.SetParent(viewport.transform, false);
                text = label.GetComponent<TextMeshProUGUI>();
                text.rectTransform.anchorMin = Vector2.zero;
                text.rectTransform.anchorMax = Vector2.one;
                text.rectTransform.offsetMin = Vector2.zero;
                text.rectTransform.offsetMax = Vector2.zero;
                text.font = TMP_Settings.defaultFontAsset;
                if (text.font == null) throw new InvalidOperationException("TMP input export requires a default font asset.");
                text.fontSize = 32;
                text.color = new Color(.263f, .263f, .263f, 1);
                text.alignment = TextAlignmentOptions.MidlineLeft;
                text.text = string.Empty;
            }

            var originalText = text.text;
            text.raycastTarget = false;
            text.richText = false;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            inputField.textViewport = text.transform.parent as RectTransform;
            inputField.textComponent = text;
            inputField.SetTextWithoutNotify(string.Empty);
            var placeholder = Instantiate(text.gameObject, text.transform.parent, false);
            placeholder.name = "Placeholder";
            var placeholderText = placeholder.GetComponent<TextMeshProUGUI>();
            placeholderText.text = originalText;
            inputField.placeholder = placeholderText;

            return inputField;
        }
    }
}
#endif
