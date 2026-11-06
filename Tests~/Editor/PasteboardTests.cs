using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Collections.Generic;
using BIEngine.UniXD.Xd;
using BIEngine.UniXD.Xd.Parsing;
using BIEngine.UniXD.Xd.Parsing.Internal;
using NUnit.Framework;
using UnityEngine;
namespace BIEngine.UniXD.Tests {
    public sealed class PasteboardTests {
        [Test] public void SystemExportNameCannotBeOverriddenOrUsedByAnotherBoard() {
            var option = new XdImporter.XdArtboardImportOption("pasteboard", "pasteboard", false, true, new[] { "child" });
            Assert.That(option.IsExpandArtboard, Is.False);
            Assert.That(XdImporter.GetExportName(option, "Renamed"), Is.EqualTo("pasteboard"));
            var supplied = new Dictionary<string,string> { { "pasteboard", "Renamed" } };
            var request = new XdImporter.XdImportRequest("NamingFixture.xd", new[] { "pasteboard" }, supplied);
            Assert.That(request.ExportNames.ContainsKey("pasteboard"), Is.False);
            Assert.That(supplied["pasteboard"], Is.EqualTo("Renamed"));
            var other = new XdImporter.XdArtboardImportOption("Other", "artboard-other", false, false);
            Assert.That(XdImporter.GetExportNameError(new[] { other }, new Dictionary<string,string> { { "artboard-other", "pasteboard" } }), Is.Not.Null);
        }
        [Test] public void SystemLibraryIsAlwaysPrimaryAndNeverStoredInEditableNames() {
            var trigger = ScriptableObject.CreateInstance<BIEngine.UniXD.CommonTrigger.SharedSpriteResourceTrigger>();
            try {
                var fields = new UnityEditor.SerializedObject(trigger);
                var names = fields.FindProperty("globalPrimaryLibraryNames");
                Assert.That(Enumerable.Range(0, names.arraySize).Select(i => names.GetArrayElementAtIndex(i).stringValue), Does.Not.Contain("pasteboard"));
                names.ClearArray(); fields.ApplyModifiedPropertiesWithoutUndo();
                var method = trigger.GetType().GetMethod("IsPrimaryPanel", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Assert.That(method.Invoke(trigger, new object[] { "pasteboard" }), Is.True);
                Assert.That(method.Invoke(trigger, new object[] { "PASTEBOARD" }), Is.True);
                Assert.That(method.Invoke(trigger, new object[] { "OtherLibrary" }), Is.False);
                names = new UnityEditor.SerializedObject(trigger).FindProperty("globalPrimaryLibraryNames");
                names.arraySize = 2; names.GetArrayElementAtIndex(0).stringValue = "OtherLibrary"; names.GetArrayElementAtIndex(1).stringValue = "pasteboard";
                names.serializedObject.ApplyModifiedPropertiesWithoutUndo();
                trigger.GetType().GetMethod("OnValidate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(trigger, null);
                names = new UnityEditor.SerializedObject(trigger).FindProperty("globalPrimaryLibraryNames");
                Assert.That(names.arraySize, Is.EqualTo(1)); Assert.That(names.GetArrayElementAtIndex(0).stringValue, Is.EqualTo("OtherLibrary"));
                Assert.That(method.Invoke(trigger, new object[] { "pasteboard" }), Is.True);
                Assert.That(method.Invoke(trigger, new object[] { "OtherLibrary" }), Is.True);
            } finally { UnityEngine.Object.DestroyImmediate(trigger); }
        }
        private string m_Path;
        private static XdObjectJson Shape(string id, float x, float y, float w, float h) => new XdObjectJson {
            Id = id, Name = id, Type = "shape", Transform = new XdTransformJson { A = 1, D = 1, Tx = x, Ty = y },
            Meta = new XdObjectMetaJson { Ux = new XdObjectMetaUxJson() },
            Shape = new XdShapeJson { Type = "rect", Width = w, Height = h },
            Style = Utf8Json.JsonSerializer.Deserialize<XdStyleJson>("{\"fill\":{\"type\":\"solid\",\"color\":{\"mode\":\"RGB\",\"value\":{\"r\":255,\"g\":0,\"b\":0}}}}")
        };
        private XdFile FileWith(params XdObjectJson[] layers) {
            m_Path = Path.Combine(Path.GetTempPath(), "unixd-pasteboard-" + Guid.NewGuid().ToString("N") + ".xd");
            using (var zip = ZipFile.Open(m_Path, ZipArchiveMode.Create)) {
                void Write(string name, string content) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(content); }
                Write("manifest", "{\"children\":[{\"path\":\"artwork\",\"children\":[{\"path\":\"pasteboard\",\"name\":\"pasteboard\"}]}]}");
                Write("artwork/pasteboard/graphics/graphicContent.agc", "{\"version\":\"1\",\"resources\":{\"href\":\"/resources/graphics/graphicContent.agc\"},\"children\":" + Utf8Json.JsonSerializer.ToJsonString(layers) + "}");
                Write("resources/graphics/graphicContent.agc", "{\"artboards\":{},\"resources\":{\"meta\":{\"ux\":{\"symbols\":[]}}}}");
            }
            return new XdFile(m_Path);
        }
        private static XdLoader Load(XdFile file, XdArtboard board) => new XdLoader(file, board, "LooseLayers", new Dictionary<string,string>(), Array.Empty<XdObjectParser>(), Array.Empty<XdGroupParser>(), Array.Empty<XdImportTrigger>());
        [TearDown] public void Cleanup() { if (m_Path != null && File.Exists(m_Path)) File.Delete(m_Path); }
        [Test] public void LooseRootsKeepIdsAndUseContentBoundsWithNegativeCoordinates() {
            using var file = FileWith(Shape("first-shape", -50, -30, 10, 20), Shape("second-shape", 20, 10, 30, 40));
            var board = file.Artworks.Single();
            Assert.That(board.Artboard.Children.Single().Artboard.Children.Select(x => x.Id), Is.EqualTo(new[]{"first-shape","second-shape"}));
            Assert.That(board.Resources.Artboards.ContainsKey("pasteboard"), Is.False);
            using var loader = Load(file, board);
            var nodes = loader.LayoutInfo.Elements.OfType<ObjectElement>().ToArray();
            Assert.That(nodes.Single(n => n.Eid == 0).Size, Is.EqualTo(new Vector2(100,80)));
            Assert.That(nodes.Single(n => n.Eid == 0).Name, Is.EqualTo("pasteboard"));
            Assert.That(nodes.Single(n => n.Eid == BIEngine.UniXD.Loader.FastHash.CalculateHash("first-shape")).Position, Is.EqualTo(new Vector2(-45,-30)));
            Assert.That(nodes.Length, Is.EqualTo(3));
        }
        [Test] public void EmptyPasteboardHasFiniteRootAndNoFakeLayers() {
            using var file = FileWith(); using var loader = Load(file, file.Artworks.Single());
            var root = loader.LayoutInfo.Elements.OfType<ObjectElement>().Single(); Assert.That(root.Size, Is.EqualTo(Vector2.one)); Assert.That(root.Children, Is.Empty);
        }
        [Test] public void RealArtboardKeepsItsDeclaredRectangle() {
            using var file = FileWith(Shape("first-shape", -50, -30, 10, 20)); var board=file.Artworks.Single(); board.Manifest.Path="artboard-known";
            board.Resources.Artboards.Add("known",new XdResourcesArtboardsJson {X=-100,Y=-50,Width=320,Height=200});
            using var loader=Load(file,board); Assert.That(loader.LayoutInfo.Elements.OfType<ObjectElement>().Single(n=>n.Eid==0).Size,Is.EqualTo(new Vector2(320,200)));
        }
        [Test] public void MissingRealArtboardRectangleReportsImportError() {
            using var file=FileWith(); var board=file.Artworks.Single();board.Manifest.Path="artboard-missing";
            Assert.Throws<BIEngine.UniXD.Editor.ImportException>(()=>Load(file,board));
        }
    }
}
