using System;
using System.Collections.Generic;
using UnityEngine;
using BIEngine.UniXD.Xd.Parsing;
using BIEngine.UniXD.Xd.Parsing.Internal;

namespace BIEngine.UniXD.Xd
{
    public class XdAssetHolder
    {
        private readonly XdFile _xdFile;
        private readonly Dictionary<string, XdStyleFillPatternMetaJson> _fileNameToMeta;
        private readonly Dictionary<string, byte[]> _fileNameToBytes;
        private readonly Dictionary<string, Func<byte[]>> _fileNameToGenerators;
        private readonly Dictionary<uint, CachedSvg> _svgHash;
        private readonly Dictionary<string, string> _sourceFingerprints = new Dictionary<string, string>();
        private readonly HashSet<XdObjectJson> m_VectorNodes = new HashSet<XdObjectJson>();

        internal bool IsVector(XdObjectJson node) => m_VectorNodes.Contains(node);

        internal void ConfigureVectorNodes(IEnumerable<XdObjectJson> roots)
        {
            bool Visit(XdObjectJson node, bool inherited)
            {
                var requested = XdVectorExport.IsRequested(node, XdImporter.VectorLayerIds);
                if (requested && !XdVectorExport.IsEligible(node))
                    throw new BIEngine.UniXD.Editor.ImportException("图层“" + node.Name + "”包含文字、位图或不支持的形状，无法保持矢量渲染。请在合图设置中改为 PNG。");
                var vector = requested || inherited;
                var descendant = false;
                foreach (var child in node.Group?.Children ?? Array.Empty<XdObjectJson>()) descendant |= Visit(child, vector);
                if ((vector || descendant) && XdVectorExport.IsEligible(node)) m_VectorNodes.Add(node);
                return vector || descendant;
            }
            m_VectorNodes.Clear();
            foreach (var root in roots) Visit(root, false);
        }

        public XdAssetHolder(XdFile xdFile)
        {
            _xdFile = xdFile;
            _fileNameToMeta = new Dictionary<string, XdStyleFillPatternMetaJson>();
            _fileNameToBytes = new Dictionary<string, byte[]>();
            _fileNameToGenerators = new Dictionary<string, Func<byte[]>>();
            _svgHash = new Dictionary<uint, CachedSvg>();
        }

        public byte[] Load(string fileName)
        {
            if (_fileNameToBytes.TryGetValue(fileName, out var cachedBytes))
                return _fileNameToMeta.ContainsKey(fileName) ? (byte[]) cachedBytes?.Clone() : cachedBytes;

            if (_fileNameToMeta.ContainsKey(fileName))
            {
                var meta = _fileNameToMeta[fileName];
                var bytes = _xdFile.GetResource(meta);
                _fileNameToBytes[fileName] = bytes;
                return (byte[]) bytes?.Clone();
            }

            if (_fileNameToGenerators.ContainsKey(fileName))
            {
                var bytes = _fileNameToGenerators[fileName].Invoke();
                _fileNameToBytes[fileName] = bytes;
                _fileNameToGenerators.Remove(fileName);
                return bytes;
            }

            throw new Exception($"Unknown asset {fileName}");
        }

        public void Save(string key, XdStyleFillPatternMetaJson meta)
        {
            _fileNameToMeta[key] = meta;
            _fileNameToBytes.Remove(key);
            _sourceFingerprints.Remove(key);
            _fileNameToGenerators.Remove(key);
        }

        public void Save(string key, Func<byte[]> generator, string sourceDescription = null)
        {
            _fileNameToBytes.Remove(key);
            _fileNameToMeta.Remove(key);
            _sourceFingerprints.Remove(key);
            _fileNameToGenerators[key] = generator;
            if (sourceDescription != null)
                _sourceFingerprints[key] = BIEngine.UniXD.Editor.ImportFingerprintSession.Hash(System.Text.Encoding.UTF8.GetBytes(sourceDescription));
        }

        public string GetSourceFingerprint(string fileName)
        {
            if (_sourceFingerprints.TryGetValue(fileName, out var fingerprint)) return fingerprint;
            if (_fileNameToMeta.TryGetValue(fileName, out var meta))
                return _sourceFingerprints[fileName] = _xdFile.GetResourceFingerprint(meta);
            return _sourceFingerprints[fileName] = BIEngine.UniXD.Editor.ImportFingerprintSession.Hash(Load(fileName));
        }

        public CachedSvg GetCachedSvg(uint svgHash)
        {
            if (!_svgHash.ContainsKey(svgHash)) return null;
            return _svgHash[svgHash];
        }

        public void SaveCacheSvg(string spriteUid, uint svgHash)
        {
            _svgHash[svgHash] = new CachedSvg(spriteUid);
        }
    }

    public class CachedSvg
    {
        public string SpriteUid { get; }

        public CachedSvg(string spriteUid)
        {
            SpriteUid = spriteUid;
        }
    }
}
