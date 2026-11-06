using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BIEngine.UniXD.Xd;
using BIEngine.UniXD.Xd.Parsing.Internal;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BIEngine.UniXD.Tests {
    public sealed class FlattenReferenceTests {
        private const BindingFlags m_Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static XdObjectJson Master() => Utf8Json.JsonSerializer.Deserialize<XdObjectJson>("{\"type\":\"shape\",\"id\":\"master\",\"name\":\"Vector\",\"shape\":{\"type\":\"rect\",\"x\":0,\"y\":0,\"width\":10,\"height\":10},\"style\":{\"fill\":{\"type\":\"solid\",\"color\":{\"mode\":\"RGB\",\"value\":{\"r\":255,\"g\":255,\"b\":255}}}}}");
        [Test] public void ReferenceNamesAndRenamesUseDistinctInstanceIds() {
            var source=Master(); var a=new XdObjectJson {Type="syncRef",Guid="instance-a",SyncSourceGuid="master"};
            var b=new XdObjectJson {Type="syncRef",Guid="instance-b",SyncSourceGuid="master"};
            var symbols=new Dictionary<string,XdObjectJson>{{"master",source}};
            var names=XdExportRules.BuildLayerNames(new[]{a,b},null,new Dictionary<string,string>{{"instance-a","Btn_Last"},{"instance-b","Btn_Next"}},symbols);
            Assert.That(names["instance-a"],Is.EqualTo("Btn_Last"));Assert.That(names["instance-b"],Is.EqualTo("Btn_Next"));
            var resolve=typeof(XdExportRules).GetMethod("ResolvePreviewNode",BindingFlags.Static|BindingFlags.NonPublic);
            var effective=(XdObjectJson)resolve.Invoke(null,new object[]{a,symbols});
            Assert.That(effective.Name,Is.EqualTo("Vector"));Assert.That(effective.Id,Is.EqualTo("instance-a"));Assert.That(effective.Guid,Is.EqualTo(a.Guid));
            Assert.That(source.Id,Is.EqualTo("master"));Assert.That(a.Name,Is.Null);
            a.Name="Instance name";effective=(XdObjectJson)resolve.Invoke(null,new object[]{a,symbols});Assert.That(effective.Name,Is.EqualTo(a.Name));
        }
        [Test] public void LeafEmptyAndInvisibleGroupsCannotMerge() {
            Assert.That(SvgGroupParser.HasMergeContent(Master()),Is.False);
            var group=new XdObjectJson{Type="group",Name="Empty@vector",Id="group",Group=new XdObjectGroupJson{Children=Array.Empty<XdObjectJson>()}};
            Assert.That(SvgGroupParser.HasMergeContent(group),Is.False);Assert.That(new SvgGroupParser().Is(group,Array.Empty<XdObjectJson>()),Is.False);
            var child=Master();child.Visible=false;group.Group.Children=new[]{child};Assert.That(SvgGroupParser.HasMergeContent(group),Is.False);
            child.Visible=true;Assert.That(SvgGroupParser.HasMergeContent(group),Is.True);
            child.Style.Fill.Type="none";Assert.That(SvgGroupParser.HasMergeContent(group),Is.False);
        }
        [Test] public void ArtboardOrderingPreservesSelectionAndFreezesDuringRename() {
            var window=ScriptableObject.CreateInstance<XdArtboardSelectionWindow>();
            try {
                var options=new[]{new XdImporter.XdArtboardImportOption("First","a",false,false),new XdImporter.XdArtboardImportOption("Second","b",false,false),new XdImporter.XdArtboardImportOption("Third","c",false,false)};
                var type=window.GetType();type.GetMethod("Initialize",m_Flags).Invoke(window,new object[]{"SortFixture.xd",options,new HashSet<string>{"b"},null,null});
                var items=((IEnumerable)type.GetField("_items",m_Flags).GetValue(window)).Cast<object>().ToArray();
                items[1].GetType().GetProperty("ExportName").SetValue(items[1],"Renamed");
                string[] Order() => ((IEnumerable)type.GetMethod("VisibleItems",m_Flags).Invoke(window,null)).Cast<object>()
                    .Select(x=>((XdImporter.XdArtboardImportOption)x.GetType().GetProperty("Option").GetValue(x)).ManifestPath).ToArray();
                type.GetField("m_SortMode",m_Flags).SetValue(window,0);Assert.That(Order(),Is.EqualTo(new[]{"a","b","c"}));
                type.GetField("m_SortMode",m_Flags).SetValue(window,1);Assert.That(Order(),Is.EqualTo(new[]{"c","b","a"}));
                type.GetField("m_SortMode",m_Flags).SetValue(window,2);Assert.That(Order(),Is.EqualTo(new[]{"b","a","c"}));
                type.GetField("m_EditingExportName",m_Flags).SetValue(window,true);items[0].GetType().GetProperty("ExportName").SetValue(items[0],"Editing");Assert.That(Order(),Is.EqualTo(new[]{"b","a","c"}));
                type.GetField("m_EditingExportName",m_Flags).SetValue(window,false);Assert.That(Order(),Is.EqualTo(new[]{"a","b","c"}));
                Assert.That((bool)items[1].GetType().GetProperty("Selected").GetValue(items[1]),Is.True);
            } finally {Object.DestroyImmediate(window);}
        }
    }
}
