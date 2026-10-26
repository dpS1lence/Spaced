using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace Spaced
{
    /// <summary>Downloads an image once, keeps it under persistentDataPath, and loads it as a texture.</summary>
    public static class TextureCache
    {
        /// <param name="onBytes">Also hands over the raw file (for reading pixels on the CPU), if given.</param>
        public static IEnumerator Load(string url, Action<Texture2D> onLoaded, Action<byte[]> onBytes = null)
        {
            string path = Path.Combine(Application.persistentDataPath, "textures", Path.GetFileName(new Uri(url).AbsolutePath));
            byte[] bytes = null;

            if (File.Exists(path))
            {
                bytes = File.ReadAllBytes(path);
            }
            else
            {
                using (var request = UnityWebRequest.Get(url))
                {
                    request.timeout = 120;
                    yield return request.SendWebRequest();
                    if (request.result == UnityWebRequest.Result.Success)
                    {
                        bytes = request.downloadHandler.data;
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        File.WriteAllBytes(path, bytes);
                    }
                    else
                    {
                        Debug.LogWarning($"[Spaced] Could not download {url}: {request.error}");
                    }
                }
            }

            if (bytes == null) yield break;
            onBytes?.Invoke(bytes);

            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true)
            {
                name = Path.GetFileNameWithoutExtension(path),
                wrapModeU = TextureWrapMode.Repeat,
                wrapModeV = TextureWrapMode.Clamp,
                anisoLevel = 8,
            };
            if (texture.LoadImage(bytes, false))
            {
                // Block-compress on the GPU-friendly path: a 5400×2700 map drops from ~58 MB to ~10 MB.
                if (texture.width % 4 == 0 && texture.height % 4 == 0) texture.Compress(false);
                texture.Apply(false, true);
                onLoaded(texture);
            }
            else
                Debug.LogWarning($"[Spaced] {path} is not a valid image; delete it to re-download.");
        }
    }
}
