using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using BIEngine.UniXD.Editor;
using BIEngine.UniXD.Editor.Extensions;
using BIEngine.UniXD.Loader;
using UnityEditor;
using UnityEngine;
using BIEngine.UniXD.Xd.Parsing;
using BIEngine.UniXD.Xd.Parsing.Internal;

namespace BIEngine.UniXD.Xd
{
    public static class XdImporter
    {
        public sealed class XdImportRequest
        {
            public string XdFilePath { get; }
            public IReadOnlyCollection<string> SelectedArtboardPaths { get; }
            public IReadOnlyDictionary<string, string> ExportNames { get; }

            public XdImportRequest(string xdFilePath, IReadOnlyCollection<string> selectedArtboardPaths = null, IReadOnlyDictionary<string, string> exportNames = null)
            {
                XdFilePath = xdFilePath;
                SelectedArtboardPaths = selectedArtboardPaths;
                ExportNames = (exportNames ?? XdArtboardExportNameHistory.Load(xdFilePath)).ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
            }
        }

        public sealed class XdArtboardImportOption
        {
            public string DisplayName { get; }
            public string ManifestPath { get; }
            public bool IsMarkedForExport { get; }
            public bool IsExpandArtboard { get; }
            public IReadOnlyList<string> ExpandedChildNames { get; }

            public XdArtboardImportOption(string displayName, string manifestPath, bool isMarkedForExport, bool isExpandArtboard, string[] expandedChildNames = null)
            {
                DisplayName = displayName;
                ManifestPath = manifestPath;
                IsMarkedForExport = isMarkedForExport;
                IsExpandArtboard = isExpandArtboard;
                ExpandedChildNames = expandedChildNames ?? Array.Empty<string>();
            }
        }

        public static XdImportSettings Settings { get; private set; }
        public static XdFile XdFile { get; private set; }
        public static ImportLogger Logger { get; private set; }
        internal static HashSet<string> FlattenGroupIds { get; private set; } = new HashSet<string>();
        internal static HashSet<string> FlattenExcludedIds { get; private set; } = new HashSet<string>();
        internal static HashSet<string> OmittedLayerIds { get; private set; } = new HashSet<string>();
        internal static HashSet<string> VectorLayerIds { get; private set; } = new HashSet<string>();
        internal static Dictionary<string, XdScrollViewMode> ScrollViewModes { get; private set; } = new Dictionary<string, XdScrollViewMode>();
        internal static Dictionary<string, string> LayerNameOverrides { get; set; } = new Dictionary<string, string>();

        public static void Import(XdImportSettings xdSettings, string[] xdFilePaths, CancellationToken cancellationToken = default)
        {
            var requests = xdFilePaths.Select(x => new XdImportRequest(x)).ToArray();
            Import(xdSettings, requests, cancellationToken);
        }

        public static void Import(XdImportSettings xdSettings, string xdFilePath, IReadOnlyCollection<string> selectedArtboardPaths, IReadOnlyDictionary<string, string> exportNames = null, CancellationToken cancellationToken = default)
        {
            Import(xdSettings, new[] { new XdImportRequest(xdFilePath, selectedArtboardPaths, exportNames) }, cancellationToken);
        }

        public static XdArtboardImportOption[] GetImportOptions(string xdFilePath, CancellationToken cancellationToken = default)
        {
            using (var progress = new ImportProgressBar("读取 XD 画板", cancellationToken))
            using (var file = new XdFile(xdFilePath, progress.Update))
                return BuildImportOptions(file.Artworks).ToArray();
        }

        public static string[] ResolveSelectedArtboards(IReadOnlyCollection<XdArtboardImportOption> options, IReadOnlyCollection<string> savedArtboardPaths)
        {
            var validPaths = new HashSet<string>(options.Select(x => x.ManifestPath));
            var savedSelections = savedArtboardPaths?
                .Where(x => !string.IsNullOrWhiteSpace(x) && validPaths.Contains(x))
                .Distinct()
                .ToArray();

            if (savedSelections != null && savedSelections.Length > 0) return savedSelections;
            return options.Select(x => x.ManifestPath).ToArray();
        }

        public static XdArtboard[] FilterImportTargets(IEnumerable<XdArtboard> artworks, IReadOnlyCollection<string> selectedArtboardPaths)
        {
            var selectedPathSet = selectedArtboardPaths != null
                ? new HashSet<string>(selectedArtboardPaths.Where(x => !string.IsNullOrWhiteSpace(x)))
                : null;

            return artworks
                .Where(x => x.Artboard.Children.Length > 0)
                .Where(x => selectedPathSet != null ? selectedPathSet.Contains(x.Manifest.Path) : IsMarkedForExport(x))
                .ToArray();
        }

        public static IEnumerable<XdArtboardImportOption> BuildImportOptions(IEnumerable<XdArtboard> artworks)
        {
            foreach (var artwork in artworks)
            {
                if (artwork.Artboard.Children.Length == 0) continue;
                bool expanded = artwork.HasParameter("expand");
                yield return new XdArtboardImportOption(artwork.Name, artwork.Manifest.Path, IsMarkedForExport(artwork), expanded,
                    expanded ? artwork.Artboard.Children.SelectMany(x => x.Artboard.Children).Select(x => x.GetSimpleName()).ToArray() : null);
            }
        }

        public static string GetExportName(XdArtboardImportOption option, string configuredName, XdImportSettings settings = null)
        {
            if (!string.IsNullOrWhiteSpace(configuredName)) return XdExportRules.NormalizeName(configuredName.Trim(), settings: settings);
            return XdExportRules.NormalizeName(option.IsExpandArtboard ? EditorUtil.ValidFileName(option.DisplayName.Split('@')[0]) : option.DisplayName.ToSafeString(), settings: settings);
        }

        public static string GetExportNameError(IEnumerable<XdArtboardImportOption> options, IReadOnlyDictionary<string, string> exportNames, XdImportSettings settings = null)
        {
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var option in options)
            {
                string configuredName = null;
                exportNames?.TryGetValue(option.ManifestPath, out configuredName);
                var name = GetExportName(option, configuredName, settings);
                var names = option.IsExpandArtboard ? option.ExpandedChildNames.Select(x => (name + x).ToSafeString()) : new[] { name };
                foreach (var outputName in names)
                {
                    var error = GetFileNameError(outputName);
                    if (error != null) return $"画板“{option.DisplayName}”：{error}";
                    if (!usedNames.Add(outputName)) return $"导出名称“{outputName}”重复，请为所选画板设置不同名称。";
                }
                // Validate the prefix itself even when an expanded board currently has no children.
                if (!string.IsNullOrWhiteSpace(configuredName) && GetFileNameError(name) is string prefixError)
                    return $"画板“{option.DisplayName}”：{prefixError}";
            }
            return null;
        }

        private static string GetFileNameError(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name == "." || name == "..") return "导出名称不能为空或仅包含点号。";
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) >= 0 || name.Any(char.IsControl))
                return "导出名称不能包含路径分隔符或文件名非法字符。";
            if (name.EndsWith(".", StringComparison.Ordinal) || name.EndsWith(" ", StringComparison.Ordinal)) return "导出名称不能以点号或空格结尾。";
            var stem = name.Split('.')[0].ToUpperInvariant();
            if (new[] { "CON", "PRN", "AUX", "NUL" }.Contains(stem) ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] >= '1' && stem[3] <= '9'))
                return "此名称是系统保留文件名，请换一个名称。";
            return null;
        }

        private static bool IsMarkedForExport(XdArtboard artwork)
        {
            return artwork.Artboard.Children[0].Meta?.Ux?.MarkedForExport ?? false;
        }

        private static void Import(XdImportSettings xdSettings, IReadOnlyCollection<XdImportRequest> requests, CancellationToken cancellationToken)
        {
            var stopWatch = Stopwatch.StartNew();
            Logger = new ImportLogger("UniXD.Xd", xdSettings.LogType);
            var loaders = new List<ILoader>();
            try
            {
                Settings = xdSettings;
                using (Disposable.Create(() => Settings = null))
                using (var progressBar = new ImportProgressBar("解析 XD", cancellationToken))
                {
                    progressBar.SetTotal(requests.Count);
                    foreach (var request in requests)
                    {
                        using (var progress = progressBar.TaskStart(Path.GetFileName(request.XdFilePath)))
                        using (Logger.SetCategory(Path.GetFileName(request.XdFilePath)))
                        {
                            var (imported, skipped) = ImportedArtboards(xdSettings, Logger, request.XdFilePath, progress, loaders, request.SelectedArtboardPaths, request.ExportNames);
                            if (imported == 0 && skipped == 0)
                            {
                                Logger.Warning($"The artboard to be imported was not found. Please set Mark for Export.");
                            }
                        }
                    }
                    progressBar.Update("画板解析完成", 1f);
                }
    
                Importer.Import(xdSettings, loaders.ToArray(), cancellationToken);
                ExportLayout(xdSettings, loaders, Logger, cancellationToken);
    
                Logger.Log($"Xd Import Finish", ("Time", $"{stopWatch.Elapsed.TotalSeconds:0.00}s"));
            }
            finally
            {
                foreach (var loader in loaders) loader.Dispose();
                Logger = null;
                FlattenGroupIds.Clear();
                FlattenExcludedIds.Clear();
                OmittedLayerIds.Clear();
                ScrollViewModes.Clear();
                LayerNameOverrides.Clear();
            }
        }

        private static void ExportLayout(XdImportSettings xdSettings, List<ILoader> loaders, ImportLogger logger, CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(xdSettings.LayoutOutputPath))
            {
                var stopWatch = Stopwatch.StartNew();
                using (var progress = new ImportProgressBar("导出 AKY", cancellationToken))
                {
                    progress.SetTotal(loaders.Count);
                    foreach (var loader in loaders)
                    {
                        using (var task = progress.TaskStart(loader.LayoutInfo.Name))
                        using (logger.SetCategory(loader.LayoutInfo.Name))
                        {
                            var bytes = LayoutCompressor.Compress(loader);
                            var outputPath = Path.Combine(xdSettings.LayoutOutputPath, loader.LayoutInfo.Name + ".aky");
                            File.WriteAllBytes(outputPath, bytes);
                            task.Update("文件导出完成", 1f);
                        }
                    }
                }
                stopWatch.Stop();
                logger.Log($"Export layout", ("Time", $"{stopWatch.Elapsed.TotalSeconds:0.00}s"));
            }
        }

        private static (int Imported, int Skipped) ImportedArtboards(XdImportSettings xdSettings, ImportLogger logger, string xdFilePath, IImportProgress progress, List<ILoader> loaders, IReadOnlyCollection<string> selectedArtboardPaths, IReadOnlyDictionary<string, string> exportNames)
        {
            var stopWatch = Stopwatch.StartNew();
            var file = new XdFile(xdFilePath, progress.Update);
            int originalLoaderCount = loaders.Count;
            XdFile = file;
            using (Disposable.Create(() => { XdFile = null; if (loaders.Count == originalLoaderCount) file.Dispose(); }))
            {
                var imported = 0;
                var skipped = 0;

                var targets = FilterImportTargets(file.Artworks, selectedArtboardPaths).ToList();
                // The archive is separate from the XD document and applies before parsing.
                var nameError = GetExportNameError(BuildImportOptions(targets), exportNames);
                if (nameError != null) { file.Dispose(); throw new ArgumentException(nameError); }

                progress.SetTotal(targets.Count);

                var xdObjectParsers = xdSettings.ObjectParsers ?? new XdObjectParser[] { };
                var xdGroupParsers = xdSettings.GroupParsers ?? new XdGroupParser[] { };
                var triggers = xdSettings.XdTriggers ?? new XdImportTrigger[] { };

                var expandTargets = new List<XdArtboard>();
                foreach (var artwork in targets)
                {
                    OmittedLayerIds = XdFlattenHistory.LoadOmitted(xdFilePath, artwork.Manifest.Path);
                    VectorLayerIds = XdFlattenHistory.LoadVectors(xdFilePath, artwork.Manifest.Path);
                    ScrollViewModes = XdFlattenHistory.LoadScrollViewModes(xdFilePath, artwork.Manifest.Path);
                    LayerNameOverrides = XdFlattenHistory.LoadNames(xdFilePath, artwork.Manifest.Path);
                    string configuredName = null;
                    exportNames?.TryGetValue(artwork.Manifest.Path, out configuredName);
                    configuredName = string.IsNullOrWhiteSpace(configuredName) ? null : configuredName.Trim();
                    if (!artwork.HasParameter("expand"))
                    {
                        expandTargets.Add(configuredName == null ? artwork : new XdArtboard(configuredName, artwork.Manifest, artwork.Artboard, artwork.Resources, artwork.Hash));
                        continue;
                    }

                    foreach (var child in artwork.Artboard.Children.SelectMany(x => x.Artboard.Children))
                    {
                        if (OmittedLayerIds.Contains(SvgGroupParser.GroupId(child))) continue;
                        var childArtboardJson = new XdArtboardJson
                        {
                            Version = artwork.Artboard.Version,
                            Artboards = artwork.Artboard.Artboards,
                            Resources = artwork.Artboard.Resources,
                            Children = new[]
                            {
                                new XdArtboardChildJson
                                {
                                    Artboard = new XdArtboardChildArtboardJson
                                    {
                                        Children = new[] { child }
                                    }
                                }
                            }
                        };

                        var childArtboard = new XdArtboard($"{configuredName ?? artwork.GetSimpleName()}{child.GetSimpleName()}", artwork.Manifest, childArtboardJson, artwork.Resources, artwork.Hash);
                        expandTargets.Add(childArtboard);
                    }
                }

                progress.SetTotal(expandTargets.Count);
                foreach (var artwork in expandTargets)
                {
                    using (var task = progress.TaskStart(artwork.Name))
                    using (var _ = logger.SetCategory(artwork.Name))
                    {
                        LayerNameOverrides = XdFlattenHistory.LoadNames(xdFilePath, artwork.Manifest.Path);
                        var name = XdExportRules.NormalizeName(artwork.Name.ToSafeString());
                        var translatedNameError = GetFileNameError(name);
                        if (translatedNameError != null) throw new ArgumentException(translatedNameError);
                        if (loaders.Any(x => string.Equals(x.LayoutInfo.Name, name, StringComparison.OrdinalIgnoreCase)))
                        {
                            file.Dispose();
                            throw new ArgumentException($"导出名称“{name}”重复，多个 XD 文件不能导出到同一名称。");
                        }
                        var xdHash = artwork.Hash;

                        var userData = new Dictionary<string, string>
                        {
                            { "xd_hash", xdHash.ToString() }
                        };

                        // JSON/timestamps cannot prove that embedded image bytes are unchanged.
                        // Always enumerate the selected artboard; the importer checks each source.
                        FlattenGroupIds = XdFlattenHistory.Load(xdFilePath, artwork.Manifest.Path);
                        FlattenExcludedIds = XdFlattenHistory.LoadExcluded(xdFilePath, artwork.Manifest.Path);
                        OmittedLayerIds = XdFlattenHistory.LoadOmitted(xdFilePath, artwork.Manifest.Path);
                        VectorLayerIds = XdFlattenHistory.LoadVectors(xdFilePath, artwork.Manifest.Path);
                        ScrollViewModes = XdFlattenHistory.LoadScrollViewModes(xdFilePath, artwork.Manifest.Path);
                        loaders.Add(new XdLoader(file, artwork, name, userData, xdObjectParsers, xdGroupParsers, triggers, task));
                        imported++;
                        task.Update("画板解析完成", 1f);
                    }
                }

                stopWatch.Stop();
                logger.Log($"Xd Parse Finish", ("ImportArtboard", imported), ("SkipArtboard", skipped), ("Time", $"{stopWatch.Elapsed.TotalSeconds:0.00}s"));
                return (imported, skipped);
            }
        }
    }
}
