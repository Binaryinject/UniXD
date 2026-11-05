using System;
using System.Collections.Generic;
using System.Linq;
using BIEngine.UniXD.Editor;
using BIEngine.UniXD.Loader;
using JetBrains.Annotations;
using UnityEngine;
using Utf8Json;
using BIEngine.UniXD.Xd.Parsing;
using BIEngine.UniXD.Xd.Parsing.Internal;

namespace BIEngine.UniXD.Xd
{
    public static class XdConst
    {
        public const string FormatVersion = "16";
    }

    public class XdLoader : ILoader, IAssetFingerprintProvider
    {
        private XdFile _xdFile;
        private readonly IXdObjectParser[] _objectParsers;
        private readonly IXdGroupParser[] _groupParsers;
        private readonly XdImportTrigger[] _triggers;
        private readonly XdAssetHolder _assetHolder;

        private static readonly IXdObjectParser[] DefaultObjectParsers =
        {
            new ScrollbarObjectParser(), // ShapeObjectParserより前
            new ShapeObjectParser(),
            new TextObjectParser(),
        };

        private static readonly IXdGroupParser[] DefaultGroupParsers =
        {
            new ButtonGroupParser(),
            new ToggleGroupParser(),
            new RepeatGridGroupParser(),
            new ScrollGroupParser(),
            new InputFieldGroupParser(),

            new SvgGroupParser(),
            new AlphaGroupParser(), // SvgGroupParserより後
            new MaskGroupParser(),
        };

        public XdLoader(XdFile xdFile, XdArtboard xdArtboard, string name, Dictionary<string, string> userData, XdObjectParser[] objectParsers, XdGroupParser[] groupParsers,
            XdImportTrigger[] triggers, IImportProgress progress = null)
        {
            _xdFile = xdFile;
            _objectParsers = objectParsers.Concat(DefaultObjectParsers).ToArray();
            _groupParsers = groupParsers.Concat(DefaultGroupParsers).ToArray();
            _triggers = triggers;
            _assetHolder = new XdAssetHolder(_xdFile);
            var previousTriggers = XdExportRules.ActiveTriggers;
            try
            {
                XdExportRules.ActiveTriggers = triggers;
                (LayoutInfo, AssetsInfo) = Create(xdArtboard, _assetHolder, XdExportRules.NormalizeName(name), userData, progress);
            }
            finally { XdExportRules.ActiveTriggers = previousTriggers; }
        }

        public void Dispose()
        {
            _xdFile?.Dispose();
            _xdFile = null;
        }

        public LayoutInfo LayoutInfo { get; }
        public AssetsInfo AssetsInfo { get; }

        public byte[] LoadAsset(string fileName)
        {
            return _assetHolder.Load(fileName);
        }

        public string GetAssetSourceFingerprint(string fileName) => _assetHolder.GetSourceFingerprint(fileName);

        private (LayoutInfo, AssetsInfo) Create(XdArtboard xdArtboard, XdAssetHolder assetHolder, string name, Dictionary<string, string> userData, IImportProgress progress)
        {
            var renderer = new XdRenderer(xdArtboard, assetHolder, _objectParsers, _groupParsers, _triggers, progress, name);

            var layoutInfoForCalcHash = new LayoutInfo(
                name,
                0,
                renderer.Meta,
                new Dictionary<string, string>(),
                renderer.Root,
                renderer.Elements.ToArray()
            );
            var hash = FastHash.CalculateHash(JsonSerializer.Serialize(LayoutCompressor.ToSerializable(layoutInfoForCalcHash)));

            var layoutInfo = new LayoutInfo(
                name,
                hash,
                renderer.Meta,
                userData,
                renderer.Root,
                renderer.Elements.ToArray()
            );

            var assetsInfo = new AssetsInfo(
                renderer.Assets.ToArray()
            );
            return (layoutInfo, assetsInfo);
        }

        private class XdRenderer
        {
            public Meta Meta => new Meta(Const.FormatVersion, "BIEngine.UniXD.Xd", XdConst.FormatVersion);
            public uint Root => 0;
            public List<IElement> Elements { get; }
            public List<IAsset> Assets { get; }

            private readonly XdAssetHolder _xdAssetHolder;
            private readonly IXdObjectParser[] _objectParsers;
            private readonly IXdGroupParser[] _groupParsers;
            private readonly XdImportTrigger[] m_ComponentTriggers;
            private readonly ObbHolder _obbHolder;
            private Dictionary<string, XdObjectJson> _sourceGuidToObject;
            private readonly IImportProgress _progress;
            private int _progressTicks;

            public XdRenderer(XdArtboard xdArtboard, XdAssetHolder xdAssetHolder, IXdObjectParser[] objectParsers, IXdGroupParser[] groupParsers, XdImportTrigger[] triggers, IImportProgress progress, string exportName)
            {
                m_ComponentTriggers = triggers ?? Array.Empty<XdImportTrigger>();
                _progress = progress;
                var resources = xdArtboard.Resources;
                _xdAssetHolder = xdAssetHolder;
                _objectParsers = objectParsers;
                _groupParsers = groupParsers;
                Elements = new List<IElement>();
                Assets = new List<IAsset>();
                _obbHolder = new ObbHolder();

                CreateRefObjectMap(resources.Resources);

                var xdResourcesArtboardsJson = resources.Artboards[xdArtboard.Manifest.Path.Replace("artboard-", "")];
                var rootObb = new Obb { Size = new Vector2(xdResourcesArtboardsJson.Width, xdResourcesArtboardsJson.Height) };

                var rootArtboard = xdArtboard.Artboard.Children[0];
                var xdObjectJsons = rootArtboard.Artboard.Children;
                var convertedXdObjectJsons = ConvertRefObject(xdObjectJsons, triggers);
                _xdAssetHolder.ConfigureVectorNodes(convertedXdObjectJsons);
                var previousNames = XdImporter.LayerNameOverrides;
                IElement[] children;
                try
                {
                    XdImporter.LayerNameOverrides = XdExportRules.BuildLayerNames(convertedXdObjectJsons, XdImporter.Settings, previousNames);
                    var childrenObbs = CalcPosition(convertedXdObjectJsons, rootObb, new XdObjectJson[] { });
                    foreach (var childObb in childrenObbs) childObb.LocalLeftTopPosition -= new Vector2(xdResourcesArtboardsJson.X, xdResourcesArtboardsJson.Y);
                    children = Render(convertedXdObjectJsons, rootObb, new XdObjectJson[] { });
                }
                finally { XdImporter.LayerNameOverrides = previousNames; }

                var rootComponents = new List<IComponent>();
                if (rootArtboard.Style?.Fill != null && rootArtboard.Style.Fill.Type == "solid")
                {
                    var color = rootArtboard.GetFillUnityColor();
                    // Keep the root Image for interaction without drawing the XD artboard background.
                    color.a = 0f;
                    rootComponents.Add(new ImageComponent(null, color, Vector2Int.one, null));
                }

                var root = new ObjectElement(
                    0,
                    exportName,
                    Vector2.zero,
                    rootObb.Size,
                    AnchorXType.Center,
                    AnchorYType.Middle,
                    0f,
                    true,
                    rootComponents.ToArray(),
                    children.Select(x => x.Eid).ToArray()
                );
                Elements.Add(root);
            }

            private void CheckProgress(string name)
            {
                if ((_progressTicks++ & 31) == 0) _progress?.Update("解析图层 " + name, 0f);
            }

            private void CreateRefObjectMap(XdResourcesResourcesJson resource)
            {
                _sourceGuidToObject = new Dictionary<string, XdObjectJson>();

                var symbols = resource.Meta.Ux.Symbols;
                foreach (var symbol in symbols)
                {
                    CreateRefObjectMapInternal(symbol);
                }
            }

            private void CreateRefObjectMapInternal(XdObjectJson xdObject)
            {
                _sourceGuidToObject[xdObject.Id] = xdObject;
                if (xdObject.Group != null)
                {
                    foreach (var child in xdObject.Group.Children)
                    {
                        CreateRefObjectMapInternal(child);
                    }
                }

                if (xdObject.Meta?.Ux?.States != null)
                {
                    foreach (var child in xdObject.Meta.Ux.States)
                    {
                        CreateRefObjectMapInternal(child);
                    }
                }
            }

            private XdObjectJson ConvertRefObject(XdObjectJson xdObject, XdImportTrigger[] triggers)
            {
                CheckProgress(xdObject.Name);
                if (XdImporter.OmittedLayerIds.Contains(SvgGroupParser.GroupId(xdObject))) return null;
                var newXdObjectJson = GetRefObject(xdObject, triggers);
                if (newXdObjectJson != null && XdImporter.OmittedLayerIds.Contains(SvgGroupParser.GroupId(newXdObjectJson))) return null;

                if (newXdObjectJson?.Group != null)
                {
                    var sourceChildren = newXdObjectJson.Group.Children;
                    var children = ConvertRefObject(sourceChildren, triggers);
                    // Do not rasterize an empty group after all its children were omitted.
                    if (sourceChildren.Length > 0 && children.Length == 0) return null;
                    newXdObjectJson.Group = new XdObjectGroupJson { Children = children };
                }

                return newXdObjectJson;
            }

            private XdObjectJson[] ConvertRefObject(XdObjectJson[] xdObject, XdImportTrigger[] triggers)
            {
                var a = new List<XdObjectJson>();
                foreach (var x in xdObject)
                {
                    var tmp = ConvertRefObject(x, triggers);
                    if (tmp != null) a.Add(tmp);
                }

                return a.ToArray();
            }

            private XdObjectJson GetRefObject(XdObjectJson xdObject, XdImportTrigger[] triggers)
            {
                IXdJsonElement Copy(Type type, IXdJsonElement instance, IXdJsonElement source)
                {
                    if (instance == null || source == null) return source;

                    var @new = (IXdJsonElement) Activator.CreateInstance(type);

                    var propertyInfos = type.GetProperties();
                    foreach (var propertyInfo in propertyInfos)
                    {
                        var instanceValue = propertyInfo.GetValue(instance);
                        var sourceValue =  propertyInfo.GetValue(source);
                        var t = propertyInfo.PropertyType;

                        if (typeof(IXdJsonElement).IsAssignableFrom(t))
                        {
                            var value = Copy(t, (IXdJsonElement) instanceValue, (IXdJsonElement) sourceValue);
                            propertyInfo.SetValue(@new, value);
                        }
                        else
                        {
                            var value = instanceValue;
                            if (value == null) value = sourceValue;
                            propertyInfo.SetValue(@new, value);
                        }
                    }

                    return @new;
                }

                var newXdObjectJson = xdObject;

                if (xdObject.Type == "syncRef")
                {
                    var source = _sourceGuidToObject[xdObject.SyncSourceGuid];
                    newXdObjectJson = (XdObjectJson) Copy(typeof(XdObjectJson), xdObject, source);
                    newXdObjectJson.Name = source.Name;
                    newXdObjectJson.Type = source.Type;
                    newXdObjectJson.Shape = source.Shape; // compoundのchildrenだけ上書きされるケースがあるが計算出来ないので戻す
                }

                foreach (var trigger in triggers)
                {
                    newXdObjectJson = trigger.OnCreateXdObject(newXdObjectJson);
                    if (newXdObjectJson == null) break;
                }

                return newXdObjectJson;
            }

            // CalcPositionは子供から確定させていく
            private Obb[] CalcPosition(XdObjectJson[] xdObjects, Obb parentObb, XdObjectJson[] parents)
            {
                var obbList = new List<Obb>();
                foreach (var xdObject in xdObjects)
                {
                    try
                    {
                        obbList.Add(CalcPosition(xdObject, parentObb, parents));
                    }
                    catch (Exception e)
                    {
                        if (e is ImportException || e is OperationCanceledException) throw;
                        Debug.LogError($"Cause exception in CalcPosition {xdObject.Name} {e}(id: {xdObject.Id}, guid: {xdObject.Guid})");
                        throw;
                    }
                }
                return obbList.ToArray();
            }

            private Obb CalcPosition(XdObjectJson xdObject, Obb parentObb, XdObjectJson[] parents)
            {
                CheckProgress(xdObject.Name);
                var obb = new Obb
                {
                    Parent = parentObb,
                    LocalLeftTopPosition = new Vector2(xdObject.Transform?.Tx ?? 0f, xdObject.Transform?.Ty ?? 0f),
                    Rotation = xdObject.Meta?.Ux?.Rotation ?? 0f
                    // サイズは子供のサイズが無いと決まらない
                };

                var children = new Obb[] { };
                if (xdObject.Group != null)
                {
                    children = CalcPosition(xdObject.Group.Children, obb, parents.Concat(new[] { xdObject }).ToArray());
                }

                foreach (var parser in _objectParsers)
                {
                    if (!parser.Is(xdObject)) continue;
                    var rect = parser.CalcSize(xdObject);

                    obb.ApplyRect(rect);
                    foreach (var child in children) child.LocalLeftTopPosition -= rect.position;
                    _obbHolder.Set(xdObject, obb);
                    return obb;
                }

                if (xdObject.Type == "group")
                {
                    var rect = Obb.MinMaxRect(children);

                    var pivotObject = xdObject.Group?.Children?.FirstOrDefault(x => x.HasParameter("pivot"));
                    if (pivotObject != null)
                    {
                        var pivotObb = _obbHolder.Get(pivotObject);
                        var pivotCenter = pivotObb.CalcLocalRect().center;

                        var delta = rect.center - pivotCenter;
                        var newCenter = pivotCenter;
                        var newWidth = rect.width + Mathf.Abs(delta.x) * 2f;
                        var newHeight = rect.height + Mathf.Abs(delta.y) * 2f;

                        rect.width = newWidth;
                        rect.height = newHeight;
                        rect.center = newCenter;
                    }

                    foreach (var parser in _groupParsers)
                    {
                        if (!m_ComponentTriggers.All(t => t.ShouldUseGroupParser(parser, xdObject, parents)) || !parser.Is(xdObject, parents)) continue;
                        rect = parser.CalcSize(xdObject, rect);
                        break;
                    }

                    obb.ApplyRect(rect);
                    foreach (var child in children) child.LocalLeftTopPosition -= rect.position;
                    _obbHolder.Set(xdObject, obb);
                    return obb;
                }

                throw new Exception($"Unknown object type {xdObject.Type}");
            }

            private IElement[] Render(XdObjectJson[] xdObjects, [CanBeNull] Obb parentObb, XdObjectJson[] parents)
            {
                var children = new List<IElement>();
                foreach (var xdObject in xdObjects)
                {
                    try
                    {
                        children.AddRange(Render(xdObject, parentObb, parents));
                    }
                    catch (Exception e)
                    {
                        if (e is ImportException || e is OperationCanceledException) throw;
                        Debug.LogError($"Cause exception in Render {xdObject.Name}(id: {xdObject.Id}, guid: {xdObject.Guid})");
                        throw;
                    }
                }

                return children.ToArray();
            }

            private IComponent[] BuildComponents(XdObjectJson node, IEnumerable<IComponent> components)
            {
                var result = components.ToArray();
                foreach (var trigger in m_ComponentTriggers) result = trigger.OnBuildComponents(node, result, node.GetSimpleName());
                return result;
            }

            // Renderは親から子供の順番
            private IElement[] Render(XdObjectJson xdObject, [CanBeNull] Obb parentObb, XdObjectJson[] parents)
            {
                CheckProgress(xdObject.Name);
                var eid = FastHash.CalculateHash(xdObject.Guid ?? xdObject.Id);
                var originalObb = _obbHolder.Get(xdObject);
                var obb = originalObb.CalcObbInWorld(parentObb);
                var position = obb.CalcLocalRect().center - (parentObb?.Size ?? Vector2.zero) / 2f;
                var size = obb.Size;
                var anchorX = AnchorXType.Center;
                var anchorY = AnchorYType.Middle;
                var rotation = obb.Rotation;
                if (Mathf.Abs(rotation) < 0.0001f) rotation = 0f;
                var constRight = xdObject.Meta.Ux.ConstraintRight ?? false;
                var constLeft = xdObject.Meta.Ux.ConstraintLeft ?? false;
                if (constRight && constLeft) anchorX = AnchorXType.Stretch;
                else if (constRight) anchorX = AnchorXType.Right;
                else if (constLeft) anchorX = AnchorXType.Left;

                var constTop = xdObject.Meta.Ux.ConstraintTop ?? false;
                var constBottom = xdObject.Meta.Ux.ConstraintBottom ?? false;
                if (constTop && constBottom) anchorY = AnchorYType.Stretch;
                else if (constTop) anchorY = AnchorYType.Top;
                else if (constBottom) anchorY = AnchorYType.Bottom;

                var shadows = new List<IElement>(XdShadowRenderer.Create(xdObject, obb, parentObb,
                    anchorX, anchorY, _xdAssetHolder, Assets));

                foreach (var parser in _objectParsers)
                {
                    if (!parser.Is(xdObject)) continue;
                    var (components, assets) = parser.Render(xdObject, obb, _xdAssetHolder);
                    components = BuildComponents(xdObject, components);

                    var children = new IElement[] { };
                    if (xdObject.Group != null) children = Render(xdObject.Group.Children, originalObb, parents.Concat(new[] { xdObject }).ToArray());

                    var element = new ObjectElement(
                        eid,
                        xdObject.GetSimpleName(),
                        position,
                        size,
                        anchorX,
                        anchorY,
                        rotation,
                        xdObject.Visible ?? true,
                        components,
                        children.Select(x => x.Eid).ToArray()
                    );

                    foreach (var asset in assets)
                    {
                        if (Assets.Any(x => x.FileName == asset.FileName)) continue;
                        Assets.Add(asset);
                    }

                    Elements.AddRange(shadows);
                    Elements.Add(element);
                    return shadows.Concat(new IElement[] { element }).ToArray();
                }

                if (xdObject.Type == "group")
                {
                    var components = new List<IComponent>();
                    foreach (var parser in _groupParsers)
                    {
                        if (!m_ComponentTriggers.All(t => t.ShouldUseGroupParser(parser, xdObject, parents)) || !parser.Is(xdObject, parents)) continue;
                        var sourceChildren = xdObject.Group?.Children;
                        var (c, assets) = parser.Render(xdObject, _xdAssetHolder, _obbHolder);
                        components.AddRange(c);

                        // Retain AO from consumed vector layers while leaving the
                        // merged foreground, its Image and its hit area unchanged.
                        if (xdObject.Visible != false && sourceChildren != null && sourceChildren.Length > 0 && xdObject.Group.Children.Length == 0)
                            CollectFlattenedShadows(sourceChildren, parentObb, anchorX, anchorY,
                                xdObject.Style?.Opacity ?? 1f, shadows);

                        foreach (var asset in assets)
                        {
                            if (Assets.Any(x => x.FileName == asset.FileName)) continue;
                            Assets.Add(asset);
                        }

                        break;
                    }

                    var generatedChildren = new IElement[] { };
                    if (xdObject.Group != null)
                    {
                        generatedChildren = Render(xdObject.Group.Children, originalObb, parents.Concat(new[] { xdObject }).ToArray());
                    }

                    var group = new ObjectElement(
                        eid,
                        xdObject.GetSimpleName(),
                        position,
                        size,
                        anchorX,
                        anchorY,
                        rotation,
                        xdObject.Visible ?? true,
                        BuildComponents(xdObject, components),
                        generatedChildren.Select(x => x.Eid).ToArray()
                    );

                    Elements.AddRange(shadows);
                    Elements.Add(group);
                    return shadows.Concat(new IElement[] { group }).ToArray();
                }

                throw new Exception($"Unknown object type {xdObject.Type}");
            }

            private void CollectFlattenedShadows(XdObjectJson[] nodes, Obb parent, AnchorXType anchorX,
                AnchorYType anchorY, float opacity, List<IElement> shadows)
            {
                foreach (var node in nodes)
                {
                    if (node.Visible == false) continue;
                    shadows.AddRange(XdShadowRenderer.Create(node, _obbHolder.Get(node).CalcObbInWorld(parent),
                        parent, anchorX, anchorY, _xdAssetHolder, Assets, opacity));
                    if (node.Group != null)
                        CollectFlattenedShadows(node.Group.Children, parent, anchorX, anchorY,
                            opacity * (node.Style?.Opacity ?? 1f), shadows);
                }
            }
        }
    }
}
