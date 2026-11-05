using System.IO;
using BIEngine.UniXD.Editor;
using BIEngine.UniXD.Editor.ScriptableObject;
using BIEngine.UniXD.Loader;
using UnityEngine;

namespace BIEngine.UniXD.Extensions
{
    [CreateAssetMenu(menuName = "UniXD/Triggers/GitKeep", fileName = nameof(GitKeepTrigger))]
    public class GitKeepTrigger : ImportTrigger
    {
        public override void OnPostprocessFile(ILoader loader, IPathGetter pathGetter)
        {
            var gitKeepPath = Path.Combine(pathGetter.AssetOutputDirectoryPath, ".gitkeep");
            if (File.Exists(gitKeepPath)) return;

            File.Create(gitKeepPath).Close();
        }
    }
}
