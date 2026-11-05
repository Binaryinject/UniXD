#if UNIXD_XD_TEXTMESHPRO_SUPPORT
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using BIEngine.UniXD.Xd.Parsing.Internal;

namespace BIEngine.UniXD.Xd.Extensions
{
    [CreateAssetMenu(menuName = "UniXD/XD/ObjectParsers/TextMeshProXdParser", fileName = nameof(TextMeshProXdParser))]
    public class TextMeshProXdParser : XdObjectParser
    {
        [System.Serializable]
        public class FontMapping
        {
            [Tooltip("XD 中的字体 PostScript 名称，例如 SourceHanSansSC-Bold。")]
            public string fontName;

            [Tooltip("将该字体名称转换为此 TMP 字体，同时用于尺寸计算和生成文本。")]
            public TMP_FontAsset fontAsset;
        }

        [SerializeField, Tooltip("按 XD 字体名称精确匹配；同名配置优先使用第一个已指定字体的条目。")]
        private List<FontMapping> m_FontMappings = new List<FontMapping>();

        private TMP_FontAsset FindMappedFont(string fontName)
        {
            if (string.IsNullOrEmpty(fontName) || m_FontMappings == null) return null;

            foreach (var mapping in m_FontMappings)
            {
                if (mapping != null && mapping.fontName == fontName && mapping.fontAsset != null)
                {
                    return mapping.fontAsset;
                }
            }

            return null;
        }

        public override bool Is(XdObjectJson xdObject)
        {
            var textParser = new TextObjectParser();
            return textParser.Is(xdObject);
        }

        public override Rect CalcSize(XdObjectJson xdObject)
        {
            if (xdObject.Text?.Frame?.Type == "positioned")
            {
                return CalcSizeFromText(xdObject, null, FindMappedFont(xdObject.Style.Font.PostscriptName));
            }

            if (xdObject.Text?.Frame?.Type == "autoHeight")
            {
                return CalcSizeAutoHeight(xdObject, FindMappedFont(xdObject.Style.Font.PostscriptName));
            }

            var textParser = new TextObjectParser();
            return textParser.CalcSize(xdObject);
        }

        public static Rect CalcSizeAutoHeight(XdObjectJson xdObject)
        {
            return CalcSizeAutoHeight(xdObject, null);
        }

        private static Rect CalcSizeAutoHeight(XdObjectJson xdObject, TMP_FontAsset fontAsset)
        {
            var calcSizeFromText = CalcSizeFromText(xdObject, xdObject.Text.Frame.Width, fontAsset);
            var size = new Vector2(xdObject.Text.Frame.Width, calcSizeFromText.height);
            return new Rect(Vector2.zero, size);
        }

        public static Rect CalcSizeFromText(XdObjectJson xdObject, float? width)
        {
            return CalcSizeFromText(xdObject, width, null);
        }

        private static Rect CalcSizeFromText(XdObjectJson xdObject, float? width, TMP_FontAsset fontAsset)
        {
            var rawText = xdObject.Text.RawText;

            var font = xdObject.Style.Font;
            if (fontAsset == null)
            {
                fontAsset = AssetDatabase.FindAssets($"{font.PostscriptName}")
                    .Select(guid => AssetDatabase.GUIDToAssetPath(guid))
                    .Select(path => AssetDatabase.LoadAssetAtPath<Object>(path))
                    .OfType<TMP_FontAsset>()
                    .Select(x =>
                    {
                        x.HasCharacters(rawText, out var missingCharacters);
                        return (missingCharacters.Count, x);
                    })
                    .OrderBy(x => x.Count)
                    .FirstOrDefault()
                    .x;
            }

            if (fontAsset == null)
            {
                Debug.LogWarning($"TextMeshPro Asset {font.PostscriptName} is not found");
                var textParser = new TextObjectParser();
                return textParser.CalcSize(xdObject);
            }

            var position = Vector2.zero;
            var fontSize = font.Size;
            position.y -= fontAsset.faceInfo.ascentLine * (fontSize / fontAsset.faceInfo.pointSize);

            var dummyObject = new GameObject("Dummy");
            var dummyRectTransform = dummyObject.AddComponent<RectTransform>();
            dummyRectTransform.sizeDelta = new Vector2(width ?? 0f, 0f);

            var textMeshPro = dummyObject.AddComponent<TextMeshProUGUI>();
            textMeshPro.font = fontAsset;
            textMeshPro.fontSize = fontSize;
            textMeshPro.text = rawText;
            if (width != null) textMeshPro.textWrappingMode = TextWrappingModes.Normal;

            var size = new Vector2(LayoutUtility.GetPreferredSize(dummyRectTransform, 0), LayoutUtility.GetPreferredSize(dummyRectTransform, 1));
            DestroyImmediate(dummyObject);

            var lines = xdObject.Text.Paragraphs.SelectMany(x => x.Lines).ToArray();
            var lineMinX = lines.Min(x => x[0].X); // xは1要素目にだけ入っている
            var lineMinY = lines.SelectMany(l => l).Min(x => x.Y);
            position.x += lineMinX;
            position.y += lineMinY;

            return new Rect(position, size);
        }

        public override (IComponent[], IAsset[]) Render(XdObjectJson xdObject, Obb obb, XdAssetHolder assetHolder)
        {
            var textParser = new TextObjectParser();
            var (components, assets) = textParser.Render(xdObject, obb, assetHolder);
            var fontAsset = FindMappedFont(xdObject.Style.Font.PostscriptName);
            if (fontAsset == null) return (components, assets);

            // Preserve the selected asset path through the intermediate layout data.
            var fontPath = AssetDatabase.GetAssetPath(fontAsset);
            for (var i = 0; i < components.Length; i++)
            {
                if (components[i] is TextComponent text)
                {
                    components[i] = new TextComponent(text.Text, text.Size, text.Color, text.Align,
                        fontPath, text.Wrap, text.LineHeight);
                }
            }

            return (components, assets);
        }
    }
}
#endif
