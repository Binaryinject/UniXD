using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BIEngine.UniXD.Editor;
using TMPro;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace BIEngine.UniXD.Xd
{
    internal static class XdTransparencyMaterials
    {
        private const string m_ShaderRoot = "Packages/com.biengine.unixd/Xd/Runtime/Shaders";
        private const string m_Prefix = "BIEngine/UniXD/";

        internal static string GetFingerprint(XdImportSettings settings) => "XdFinalTransparency-v2-shared\n" +
            AssetDatabase.GetAssetDependencyHash(m_ShaderRoot) + "\n" +
            AssetDatabase.GetAssetDependencyHash("Assets/TextMesh Pro/Shaders") + "\n" +
            AssetDatabase.GetAssetDependencyHash("Packages/com.unity.ugui/Runtime/TMP/Shaders") + "\n" +
            AssetDatabase.GetAssetDependencyHash("Packages/com.unity.vectorgraphics/Runtime/Shaders");

        internal static void Apply(XdImportSettings settings, GameObject root, IPathGetter paths)
        {
            if (!settings.matchXdTransparencyAppearance || QualitySettings.activeColorSpace != ColorSpace.Linear) return;
            var output = settings.AssetOutputDirectoryPath;
            var directory = (string.IsNullOrEmpty(output) ? "Assets/UniXDShared" : output.Replace("{name}", "Shared")).TrimEnd('/', '\\') + "/Materials";
            if (!Directory.Exists(directory)) { Directory.CreateDirectory(directory); AssetDatabase.Refresh(); }
            var settingsPath = AssetDatabase.GetAssetPath(settings);
            var shaderDirectory = string.IsNullOrEmpty(settingsPath) ? "Assets/UniXDGeneratedShaders" :
                Path.GetDirectoryName(settingsPath).Replace('\\', '/') + "/GeneratedShaders";
            if (!Directory.Exists(shaderDirectory)) { Directory.CreateDirectory(shaderDirectory); AssetDatabase.Refresh(); }
            var materials = new Dictionary<Material, Material>();
            foreach (var graphic in root.GetComponentsInChildren<Graphic>(true))
            {
                var source = graphic is TMP_Text text ? text.fontSharedMaterial :
                    graphic is TMP_SubMeshUI submesh ? submesh.sharedMaterial : graphic.material;
                if (source == null || source.shader == null) continue;
                if (!materials.TryGetValue(source, out var material))
                {
                    var shader = source.shader.name.StartsWith(m_Prefix, StringComparison.Ordinal) ? source.shader : Variant(source.shader, shaderDirectory);
                    if (shader == null) throw new ImportException("无法为透明材质生成 XD 混合版本：" + source.shader.name + "。请关闭“透明混合匹配 XD”后导出，或为此自定义着色器添加最终混合支持。");
                    var candidate = new Material(source) { shader = shader, name = "" };
                    candidate.SetVector("_XdReferenceBackground", settings.transparencyReferenceBackground);
                    var signature = Signature(candidate);
                    material = Directory.GetFiles(directory, "*.mat").Select(AssetDatabase.LoadAssetAtPath<Material>)
                        .FirstOrDefault(existing => existing != null && Signature(existing) == signature);
                    if (material == null)
                    {
                        var fontName = graphic is TMP_Text fontLabel && fontLabel.font != null ? fontLabel.font.name : source.name;
                        var family = graphic is TMP_Text || graphic is TMP_SubMeshUI
                            ? "TMP_" + fontName.Replace(" Material", "").Replace(" SDF", "").Replace("XD_TMP_", "").Replace("XD ", "")
                            : shader.name.IndexOf("Vector", StringComparison.OrdinalIgnoreCase) >= 0
                                ? (shader.name.IndexOf("Gradient", StringComparison.OrdinalIgnoreCase) >= 0 ? "VectorGradient" : "Vector") : "UI";
                        var path = AssetDatabase.GenerateUniqueAssetPath(directory + "/XD_" + SafeName(family) + ".mat");
                        candidate.name = Path.GetFileNameWithoutExtension(path);
                        AssetDatabase.CreateAsset(candidate, path);
                        material = candidate;
                    }
                    else Object.DestroyImmediate(candidate);
                    materials[source] = material;
                }
                if (graphic is TMP_Text label) label.fontSharedMaterial = material;
                else if (graphic is TMP_SubMeshUI sub) sub.sharedMaterial = material;
                else graphic.material = material;
            }
        }

        private static string SafeName(string value) => Regex.Replace(value, @"[^\w\-]+", "_").Trim('_');

        private static string Signature(Material material)
        {
            // Sprite, UV and vertex color live on Graphic. Preserve texture/atlas references
            // and every serialized material parameter when deciding whether to share.
            var copy = new Material(material) { name = "" };
            var result = EditorJsonUtility.ToJson(copy);
            Object.DestroyImmediate(copy);
            return result;
        }

        private static Shader Variant(Shader source, string directory)
        {
            if (source.name == "UI/Default" || source.name == "UI/DefaultETC1")
                return AssetDatabase.LoadAssetAtPath<Shader>(m_ShaderRoot + "/XdUiTransparency.shader");
            var sourcePath = AssetDatabase.GetAssetPath(source);
            var disk = DiskPath(sourcePath);
            if (string.IsNullOrEmpty(disk) || !File.Exists(disk)) return null;
            var guid = AssetDatabase.AssetPathToGUID(sourcePath);
            var friendly = SafeName(source.name.Replace("TextMeshPro/", "TMP_"));
            var path = directory + "/XD_" + friendly + ".shader";
            var legacy = directory + "/XDShader_" + guid + ".shader";
            if (File.Exists(legacy) && !File.Exists(path)) AssetDatabase.MoveAsset(legacy, path);
            if (File.Exists(path) && AssetImporter.GetAtPath(path)?.userData != guid && !File.ReadAllText(path).Contains(m_Prefix + "Variant " + guid))
            {
                var owned = Directory.GetFiles(directory, "*.shader").FirstOrDefault(file => AssetImporter.GetAtPath(file)?.userData == guid);
                path = owned ?? AssetDatabase.GenerateUniqueAssetPath(path);
            }
            var code = TransformSource(File.ReadAllText(disk), sourcePath, Path.GetFileNameWithoutExtension(path).Substring(3));
            if (code == null) return null;
            if (!File.Exists(path) || File.ReadAllText(path) != code)
            {
                File.WriteAllText(path, code, new UTF8Encoding(false));
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            }
            var importer = AssetImporter.GetAtPath(path);
            if (importer != null && importer.userData != guid) { importer.userData = guid; importer.SaveAndReimport(); }
            return AssetDatabase.LoadAssetAtPath<Shader>(path);
        }

        internal static string TransformSource(string code, string sourcePath, string identity)
        {
            var fragments = Regex.Matches(code, @"#pragma\s+fragment\s+(\w+)");
            if (fragments.Count != 1) return null;
            var fragment = fragments[0].Groups[1].Value;
            var body = Regex.Match(code, @"\b(?:fixed|half|float)4\s+" + Regex.Escape(fragment) + @"\s*\([^)]*\)\s*:\s*(?:SV_Target\d*|COLOR\d*)\s*\{");
            if (!body.Success) return null;
            var blend = Regex.Match(code, @"\bBlend\s+(One|SrcAlpha)\s+OneMinusSrcAlpha\b", RegexOptions.IgnoreCase);
            if (!blend.Success) return null;
            var open = body.Index + body.Length - 1;
            var end = ClosingBrace(code, open);
            if (end < 0) return null;
            var function = code.Substring(open + 1, end - open - 1);
            var matcher = blend.Groups[1].Value.Equals("One", StringComparison.OrdinalIgnoreCase) ? "XdMatchPremultiplied" : "XdMatchStraight";
            function = Regex.Replace(function, @"\breturn\s+([^;]+);", match => "return " + matcher + "(" + match.Groups[1].Value + ");");
            code = code.Substring(0, open + 1) + function + code.Substring(end);
            code = code.Insert(body.Index, "#include \"" + m_ShaderRoot + "/XdTransparency.cginc\"\n");
            code = Regex.Replace(code, @"\bShader\s+""[^""]+""", "Shader \"" + m_Prefix + identity + "\"", RegexOptions.None, TimeSpan.FromSeconds(1));
            var properties = Regex.Match(code, @"\bProperties\s*\{");
            if (!properties.Success) return null;
            code = code.Insert(properties.Index + properties.Length, "\n[HideInInspector] _XdReferenceBackground (\"Reference background (sRGB)\", Vector) = (1,1,1,1)\n");
            var parent = Path.GetDirectoryName(sourcePath).Replace('\\', '/');
            return Regex.Replace(code, "#include\\s+\"([^\"]+)\"", match =>
            {
                var include = match.Groups[1].Value;
                if (include.StartsWith("Assets/", StringComparison.Ordinal) || include.StartsWith("Packages/", StringComparison.Ordinal)) return match.Value;
                var relative = parent + "/" + include;
                return File.Exists(DiskPath(relative)) ? "#include \"" + relative + "\"" : match.Value;
            });
        }

        private static int ClosingBrace(string source, int start)
        {
            var depth = 0; var line = false; var block = false;
            for (var i = start; i < source.Length; i++)
            {
                if (line) { if (source[i] == '\n') line = false; continue; }
                if (block) { if (source[i] == '*' && i + 1 < source.Length && source[i + 1] == '/') { block = false; i++; } continue; }
                if (source[i] == '/' && i + 1 < source.Length)
                {
                    if (source[i + 1] == '/') { line = true; i++; continue; }
                    if (source[i + 1] == '*') { block = true; i++; continue; }
                }
                if (source[i] == '{') depth++;
                if (source[i] == '}' && --depth == 0) return i;
            }
            return -1;
        }

        private static string DiskPath(string path)
        {
            if (!path.StartsWith("Packages/", StringComparison.Ordinal)) return path;
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(path);
            return package == null ? path : Path.Combine(package.resolvedPath, path.Substring(("Packages/" + package.name + "/").Length));
        }
    }
}
