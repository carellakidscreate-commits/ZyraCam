# IsaiahCameraMod 2.9.0

Install only **plugins/ZyraCameraMod.dll**. Close Gorilla Tag, back up and replace the existing ZyraCameraMod DLL in BepInEx/plugins. Keep one installed version. Keep the existing configuration to retain all six presets and the selected slot. This delivery has not installed the DLL into the live game.

- Tap A while visible: hide the tablet; capture continues.
- Hold A for 0.3 seconds: enter Selfie and hold the tablet 0.5 m ahead of the live headset. Release: park it there. Pressing A while hidden recalls immediately.
- Third Person/Follow: physical screen faces its owner, independently of filming direction.
- Recurring global searches and unnecessary forced ambience updates replaced by lifecycle registration and caches. No quality reduction or new runtime dependency.

The exact model, audio, glyphs and shaders remain embedded and byte-identical. All other runtime files, including presets, colors, networking, panels and button audio, are unchanged. Details, root causes, test scope and limitations: VALIDATION.md. Defender scan: no threats found for this build; historical detection status: SECURITY.md.

## Build

Source contains all 13 C# files, project and embedded bundle. With a .NET SDK and the existing Gorilla Tag/BepInEx 5 installation:

   dotnet build ZyraCameraMod.csproj -c Release -o release -p:UseSharedCompilation=false

For another installation add -p:GameDir="D:\SteamLibrary\steamapps\common\Gorilla Tag". No Blender/Unity asset export is needed for these code fixes. Original import/export instructions are in Source/AssetBuild/README.md.

DLL SHA-256: 62A8A3B04AC54C1258F54E817681471AE8D2DC9CFE4813C44B4F0D84425D7867
Bundle SHA-256: 67BF65453D617EB51C93AD80D0B2B4858EE3F98B8B10BF81F5E15BA7B761404C
FBX SHA-256: 263D019A5F28FA92B90BE4941CE7083C23775001713EDECB7B38FA9EC4326870
