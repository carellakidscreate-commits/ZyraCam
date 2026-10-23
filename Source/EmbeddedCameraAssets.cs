using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace IsaiahCameraMod
{
    // Runtime assets are part of the plugin assembly. Never extract them, search
    // beside the DLL, or fall back to an external bundle. Keep future resources
    // in this same assembly so deployment remains a single DLL.
    internal static class EmbeddedCameraAssets
    {
        internal const string ResourceName = "IsaiahCameraMod.Assets.isaiahcamera";

        internal static byte[] ReadBytes(Assembly assembly)
        {
            if (assembly == null) throw new ArgumentNullException(nameof(assembly));
            using (Stream resource = assembly.GetManifestResourceStream(ResourceName))
            {
                if (resource == null)
                    throw new InvalidOperationException("This camera DLL is missing its embedded assets. Replace it with the complete IsaiahCameraMod.dll release.");
                using (var bytes = new MemoryStream())
                {
                    resource.CopyTo(bytes);
                    if (bytes.Length == 0)
                        throw new InvalidOperationException("The camera DLL contains an empty embedded asset bundle. Rebuild the complete DLL.");
                    return bytes.ToArray();
                }
            }
        }

        internal static AssetBundle Load()
        {
            // This class's assembly is the plugin, also when called through a
            // loader or reflection. GetExecutingAssembly in a caller is unsafe.
            byte[] bytes = ReadBytes(typeof(EmbeddedCameraAssets).Assembly);
            AssetBundle result = AssetBundle.LoadFromMemory(bytes);
            if (!result)
                throw new InvalidOperationException("Unity could not load this camera DLL's embedded Windows assets. Check the supported Unity version and reinstall the complete DLL.");
            // Unity owns the loaded bundle. The temporary managed byte array can
            // be collected; ReleaseResources calls Unload(true) on shutdown.
            return result;
        }
    }
}
