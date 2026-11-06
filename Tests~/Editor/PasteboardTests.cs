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
