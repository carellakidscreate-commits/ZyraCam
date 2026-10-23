## Model import/export

The existing workspace work/CameraBundleProject already has the supplied model as Assets/IsaiahCamera/CameraModelV5.fbx, byte-identical to FINAL MODEL! .fbx. No new Blender export is needed. The supplied .blend1 provided original font-axis reference metadata, not replacement geometry. Both supplied videos were reviewed; their PC-only views cannot establish measured hand latency.

Updated editor C# and sanitized V5 metadata are included in Source/AssetBuild. In the existing Unity 6000.1.9f1 project, retain CameraGlyphs.fbx, ButtonClick.mp3, the three camera shaders and ShaderIncludes. Use the exact V5 FBX/metadata and run Isaiah Camera > Build Supplied Exact FBX V5. The builder validates and copies Build/isaiahcamera to the runtime project's Resources/isaiahcamera. Rebuild the DLL afterward. The plugin compiles against the installed game's Unity 6000.2.9f1 managed references.

Raw source FBX files retain original authoring path metadata in the local project. They are not duplicated in this distributable source ZIP; the included prebuilt bundle is sufficient for a full DLL rebuild. This preserves the supplied FBX byte-for-byte and keeps personal authoring paths out of the DLL/source ZIP.


2.9.0 reuses the unchanged 2.8.0 bundle. Rebuilding the DLL needs only the supplied Source/Resources/isaiahcamera input.
