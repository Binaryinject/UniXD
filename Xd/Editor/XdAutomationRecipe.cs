using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;
using BIEngine.UniXD.Xd.Parsing;
using BIEngine.UniXD.Xd.Parsing.Internal;

namespace BIEngine.UniXD.Xd
{
    /// <summary>Portable snapshot of the decisions made in the artboard and flatten windows.</summary>
    public static class XdAutomationRecipe
    {
        [Serializable]
        public sealed class LayerRename { public string id; public string name; }

        [Serializable]
        public sealed class ScrollMode { public string id; public int mode; }

        [Serializable]
        public sealed class ReviewedBoard
        {
            public string manifestPath;
            public string originalName;
            public string exportName;
            public LayerRename[] renames;
            public string[] omitted;
            public string[] included;
            public ScrollMode[] scrollModes;
        }

        /// <summary>Applies reviewed ID-based edits after preflight, snapshots all local decisions, then uses the normal importer.</summary>
        public static void ExportReviewed(XdImportSettings settings, string xdFilePath, string expectedSha256,
            ReviewedBoard[] boards, string snapshotPath)
        {
            if (settings == null || boards == null || boards.Length == 0)
                throw new ArgumentException("缺少导出配置或已核对画板。");
            if (!string.Equals(Hash(xdFilePath), expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("XD 已变化，请重新检查画板与图层 ID 后更新计划。");
            var options = XdImporter.GetImportOptions(xdFilePath);
            var names = XdArtboardExportNameHistory.Load(xdFilePath);
            var selected = new HashSet<string>();
            using (var file = new XdFile(xdFilePath))
            {
                foreach (var board in boards)
                {
                    var option = board == null ? null : options.SingleOrDefault(item => item.ManifestPath == board.manifestPath);
                    if (option == null || option.DisplayName != board.originalName || !selected.Add(board.manifestPath) || string.IsNullOrWhiteSpace(board.exportName))
                        throw new ArgumentException("计划中有未知、重复、原名不匹配或缺少导出名的画板。");
                    var artwork = file.Artworks.Single(item => item.Manifest.Path == board.manifestPath);
                    var ids = new HashSet<string>();
                    var scrollIds = new HashSet<string>();
                    void Visit(XdObjectJson node)
                    {
                        var id = SvgGroupParser.GroupId(node);
                        if (!string.IsNullOrEmpty(id)) ids.Add(id);
                        if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(node.Meta?.Ux?.ScrollingType)) scrollIds.Add(id);
                        foreach (var child in node.Group?.Children ?? Array.Empty<XdObjectJson>()) Visit(child);
                    }
                    foreach (var root in artwork.Artboard.Children)
                        foreach (var child in root.Artboard?.Children ?? Array.Empty<XdObjectJson>()) Visit(child);
                    var renamed = new HashSet<string>();
                    foreach (var rename in board.renames ?? Array.Empty<LayerRename>())
                    {
                        if (rename == null || !ids.Contains(rename.id) || !renamed.Add(rename.id) || string.IsNullOrWhiteSpace(rename.name))
                            throw new ArgumentException($"画板 {board.originalName} 存在未知或重复的改名图层。");
                        XdExportRules.NormalizeName(rename.name, rename.name, settings);
                    }
                    foreach (var id in board.omitted ?? Array.Empty<string>())
                        if (!ids.Contains(id)) throw new ArgumentException($"画板 {board.originalName} 的排除图层不存在：{id}");
                    foreach (var id in board.included ?? Array.Empty<string>())
                        if (!ids.Contains(id) || (board.omitted ?? Array.Empty<string>()).Contains(id))
                            throw new ArgumentException($"画板 {board.originalName} 的恢复导出图层不存在或与排除冲突：{id}");
                    var modes = new HashSet<string>();
                    foreach (var scroll in board.scrollModes ?? Array.Empty<ScrollMode>())
                        if (scroll == null || !scrollIds.Contains(scroll.id) || !modes.Add(scroll.id) || scroll.mode < 0 || scroll.mode > 2)
                            throw new ArgumentException($"画板 {board.originalName} 的滚动模式无效。");
                    names[board.manifestPath] = board.exportName;
                }
            }
            var error = XdImporter.GetExportNameError(options.Where(option => selected.Contains(option.ManifestPath)), names, settings);
            if (error != null) throw new ArgumentException(error);
            // Preflight every board before modifying EditorPrefs. Unmentioned local decisions are preserved.
            foreach (var board in boards)
            {
                var layerNames = XdFlattenHistory.LoadNames(xdFilePath, board.manifestPath);
                foreach (var rename in board.renames ?? Array.Empty<LayerRename>()) layerNames[rename.id] = rename.name;
                XdFlattenHistory.SaveNames(xdFilePath, board.manifestPath, layerNames);
                var omitted = XdFlattenHistory.LoadOmitted(xdFilePath, board.manifestPath);
                omitted.ExceptWith(board.included ?? Array.Empty<string>());
                omitted.UnionWith(board.omitted ?? Array.Empty<string>());
                XdFlattenHistory.SaveOmitted(xdFilePath, board.manifestPath, omitted);
                var scrollModes = XdFlattenHistory.LoadScrollViewModes(xdFilePath, board.manifestPath);
                foreach (var scroll in board.scrollModes ?? Array.Empty<ScrollMode>()) scrollModes[scroll.id] = (XdScrollViewMode)scroll.mode;
                XdFlattenHistory.SaveScrollViewModes(xdFilePath, board.manifestPath, scrollModes);
            }
            XdArtboardExportNameHistory.Save(xdFilePath, names);
            XdArtboardSelectionHistory.Save(xdFilePath, selected);
            Save(xdFilePath, snapshotPath, selected, names);
            XdImporter.Import(settings, xdFilePath, selected, names);
        }

        [Serializable]
        private sealed class Board
        {
            public string manifestPath;
            public string originalName;
            public string exportName;
            public bool selected;
            public string layerSettings;
        }

        [Serializable]
        private sealed class Recipe
        {
            public int version = 1;
            public string sourceSha256;
            public Board[] boards;
        }

        public static void Save(string xdFilePath, string recipePath, IReadOnlyCollection<string> selectedPaths,
            IReadOnlyDictionary<string, string> exportNames)
        {
            if (Path.GetFullPath(xdFilePath).Equals(Path.GetFullPath(recipePath), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("配置不能覆盖 XD 源文件。");
            var options = XdImporter.GetImportOptions(xdFilePath);
            var recipe = new Recipe
            {
                sourceSha256 = Hash(xdFilePath),
                boards = options.Select(option => new Board
                {
                    manifestPath = option.ManifestPath,
                    originalName = option.DisplayName,
                    exportName = exportNames.TryGetValue(option.ManifestPath, out var name) ? name : "",
                    selected = selectedPaths.Contains(option.ManifestPath),
                    layerSettings = XdFlattenHistory.Capture(xdFilePath, option.ManifestPath)
                }).ToArray()
            };
            File.WriteAllText(recipePath, JsonUtility.ToJson(recipe, true), new System.Text.UTF8Encoding(false));
        }

        /// <summary>Restores local export choices only; does not export assets or generate business code.</summary>
        public static void Load(string xdFilePath, string recipePath, XdImportSettings settings)
        {
            var recipe = JsonUtility.FromJson<Recipe>(File.ReadAllText(recipePath));
            if (recipe == null || recipe.version != 1 || recipe.boards == null || recipe.boards.Length == 0)
                throw new ArgumentException("不支持或缺少画板的 XD 配置。");
            if (!string.Equals(recipe.sourceSha256, Hash(xdFilePath), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("XD 内容已变化。请在窗口重新核对画板、图层 ID 与合图，不套用旧配置。");
            var options = XdImporter.GetImportOptions(xdFilePath);
            var known = new HashSet<string>(options.Select(option => option.ManifestPath));
            var seen = new HashSet<string>();
            foreach (var board in recipe.boards)
            {
                if (board == null || !known.Contains(board.manifestPath) || !seen.Add(board.manifestPath))
                    throw new ArgumentException("配置含未知或重复画板。");
                // Validate every archive before changing any preferences.
                if (string.IsNullOrWhiteSpace(board.layerSettings))
                    throw new ArgumentException("画板缺少有效图层配置。");
                board.layerSettings = XdFlattenHistory.ValidateArchive(board.layerSettings);
            }
            if (!seen.SetEquals(known)) throw new ArgumentException("配置未覆盖当前文件全部画板。");
            var names = recipe.boards.Where(board => !string.IsNullOrWhiteSpace(board.exportName))
                .ToDictionary(board => board.manifestPath, board => board.exportName);
            var selected = recipe.boards.Where(board => board.selected).Select(board => board.manifestPath).ToArray();
            var selectedSet = new HashSet<string>(selected);
            var error = XdImporter.GetExportNameError(options.Where(option => selectedSet.Contains(option.ManifestPath)), names, settings);
            if (error != null) throw new ArgumentException(error);

            // Keep a rollback snapshot so malformed archives never leave a half-restored session.
            var oldLayers = options.ToDictionary(option => option.ManifestPath,
                option => XdFlattenHistory.Capture(xdFilePath, option.ManifestPath));
            var oldNames = XdArtboardExportNameHistory.Load(xdFilePath);
            var oldSelection = XdArtboardSelectionHistory.Load(xdFilePath);
            try
            {
                foreach (var board in recipe.boards)
                    XdFlattenHistory.Restore(xdFilePath, board.manifestPath, board.layerSettings);
                XdArtboardExportNameHistory.Save(xdFilePath, names);
                XdArtboardSelectionHistory.Save(xdFilePath, selected);
            }
            catch
            {
                foreach (var pair in oldLayers) XdFlattenHistory.Restore(xdFilePath, pair.Key, pair.Value);
                XdArtboardExportNameHistory.Save(xdFilePath, oldNames);
                XdArtboardSelectionHistory.Save(xdFilePath, oldSelection);
                throw;
            }
        }

        private static string Hash(string file)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(file))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
    }
}
