using System.Linq;
using BIEngine.UniXD.Editor;
using UnityEngine;
using BIEngine.UniXD.Xd.Parsing;
using BIEngine.UniXD.Xd.Parsing.Internal;

namespace BIEngine.UniXD.Xd
{
    public static class XdJsonExtensions
    {
        public static string GetSimpleName(this XdObjectJson xdObjectJson)
        {
            var id = SvgGroupParser.GroupId(xdObjectJson) ?? "";
            if (XdExportRules.TryGetFinalLayerName(XdImporter.LayerNameOverrides, id, out var final)) return final;
            XdImporter.LayerNameOverrides.TryGetValue(id, out var manual);
            return XdExportRules.NormalizeName(GetSimpleName(xdObjectJson.Name), manual);
        }
        public static string GetOriginalSimpleName(this XdObjectJson node) => GetSimpleName(node.Name);
        public static string GetSimpleName(this XdArtboard xdObjectJson) => GetSimpleName(xdObjectJson.Name);

        private static string GetSimpleName(string name)
        {
            if (name == null) return string.Empty;
            return EditorUtil.ValidFileName(name.Split('@')[0]);
        }

        public static bool NameEndsWith(this XdObjectJson xdObjectJson, string name)
        {
            return GetSimpleName(xdObjectJson.Name).ToLowerInvariant().EndsWith(name.ToLowerInvariant());
        }

        public static bool HasParameter(this XdObjectJson xdObjectJson, string name) => HasParameter(xdObjectJson.Name, name);
        public static bool HasParameter(this XdArtboard xdObjectJson, string name) => HasParameter(xdObjectJson.Name, name);

        private static bool HasParameter(string parentName, string name)
        {
            return GetParameters(parentName).Contains(name.ToLowerInvariant());
        }

        private static string[] GetParameters(string name)
        {
            if (name == null) return new string[] { };

            var e = name.Split('@');
            if (e.Length <= 1) return new string[] { };

            return e[1].Split(',').Select(x => x.ToLowerInvariant().Trim()).ToArray();
        }

        public static float GetRepeatGridSpacing(this XdObjectJson xdObjectJson, string scrollingType)
        {
            float spacing;

            if (scrollingType == "vertical")
            {
                spacing = xdObjectJson.Meta?.Ux?.RepeatGrid?.PaddingY ?? 0f;
            }
            else
            {
                spacing = xdObjectJson.Meta?.Ux?.RepeatGrid?.PaddingX ?? 0f;
            }

            return spacing;
        }

        public static void RemoveConstraint(this XdObjectJson xdObjectJson)
        {
            if (xdObjectJson?.Meta?.Ux != null)
            {
                xdObjectJson.Meta.Ux.ConstraintRight = false;
                xdObjectJson.Meta.Ux.ConstraintLeft = false;
                xdObjectJson.Meta.Ux.ConstraintTop = false;
                xdObjectJson.Meta.Ux.ConstraintBottom = false;
            }
        }

        public static string ToSvgColorString(this Color color)
        {
            var color32 = (Color32) color;
            return $"rgba({color32.r},{color32.g},{color32.b},{color.a:0.###})";
        }

        public static string ToSvgColorStringRgb(this Color color)
        {
            var color32 = (Color32) color;
            return $"rgb({color32.r},{color32.g},{color32.b})";
        }

        public static Color ToUnityColor(this XdColorJson xdColorJson)
        {
            var color = new Color32 { r = 255, g = 255, b = 255, a = 255 };

            color.r = (byte) xdColorJson.Value.R;
            color.g = (byte) xdColorJson.Value.G;
            color.b = (byte) xdColorJson.Value.B;
            color.a = xdColorJson.Alpha == null ? (byte) 255 : (byte) (255 * xdColorJson.Alpha);

            return color;
        }

        public static Color ToUnityColor(this XdStyleFillJson xdStyleFillJson)
        {
            var color = new Color32 { r = 255, g = 255, b = 255, a = 255 };
            if (xdStyleFillJson == null || xdStyleFillJson.Type == "none") return color;

            var xdColorJson = xdStyleFillJson.Color;
            if (xdColorJson?.Value == null) return color;

            return xdColorJson.ToUnityColor();
        }

        public static Color ToUnityColor(this XdStyleStrokeJson xdStyleStrokeJson)
        {
            var color = new Color32 { r = 255, g = 255, b = 255, a = 255 };
            if (xdStyleStrokeJson == null || xdStyleStrokeJson.Type == "none") return color;

            var xdColorJson = xdStyleStrokeJson.Color;
            if (xdColorJson?.Value == null) return color;

            return xdColorJson.ToUnityColor();
        }

        public static Color GetFillUnityColor(this XdObjectJson xdObjectJson)
        {
            var color = xdObjectJson.Style.Fill.ToUnityColor();
            color.a *= xdObjectJson.Style?.Opacity ?? 1f;
            return color;
        }

        public static Color GetFillUnityColor(this XdArtboardChildJson xdArtboardChildJson)
        {
            var color = xdArtboardChildJson.Style.Fill.ToUnityColor();
            color.a *= xdArtboardChildJson.Style?.Opacity ?? 1f;
            return color;
        }
    }
}
