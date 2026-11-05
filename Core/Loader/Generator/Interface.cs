using System.Collections.Generic;
using UnityEngine;

namespace BIEngine.UniXD.Generator
{
    public interface IAssetLoader
    {
        Sprite LoadSprite(string name);
        Font LoadFont(string name);
        Dictionary<string, object> LoadMeta(string name);
    }

    public interface IVectorAssetLoader
    {
        Material LoadVectorMaterial(string name);
    }
}
