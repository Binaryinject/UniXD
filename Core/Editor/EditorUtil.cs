using System.IO;
using System.Linq;
using UnityEditor;

namespace BIEngine.UniXD.Editor
{
    public static class EditorUtil
    {
        public static string ToUniversalPath(this string path)
        {
            return path.Replace(Path.DirectorySeparatorChar.ToString(), "/");
        }

        public static void CreateDirectory(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = Path.GetDirectoryName(path);
            CreateDirectory(parent);
            // Importer creates physical output directories before the first texture
            // import. Register those instead of creating a second "Folder 1".
            if (Directory.Exists(path)) AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            else AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
            if (!AssetDatabase.IsValidFolder(path)) throw new IOException("Cannot register asset directory: " + path);
        }

        public static string ValidFileName(string s)
        {
            return Path.GetInvalidFileNameChars().Aggregate(s, (current, c) => current.Replace(c, '_'));
        }
    }
}
