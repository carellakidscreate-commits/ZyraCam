# Security checks: 2.9.0

A normal Microsoft Defender custom scan of this exact release completed on 2026-10-23 with **no threats found, exit code 0**. SHA-256: 62A8A3B04AC54C1258F54E817681471AE8D2DC9CFE4813C44B4F0D84425D7867. See Security/defender-scan.txt. No exclusions, AV settings changes, obfuscation, packing or detection bypasses were used.

Source and compiled metadata review found managed IL only, no native imports/PInvoke, executable entry point, process launch or downloaded code. Dependencies are the existing game/Unity/Photon/BepInEx/Harmony libraries. New lifecycle postfixes use the same installed Harmony library. Network functionality and presets remain unchanged. A clean independent build reproduces the DLL byte-for-byte.

Pattern checks found no personal paths, local identity, tokens or external URLs in the DLL/source/decompressed bundle. The unchanged bundle's sole email-pattern match was previously verified as consecutive glyph triangle indices, not contact data; classification evidence is included. Debug paths are neutral. Raw authoring FBX metadata is preserved in the local project and is not redistributed in the source ZIP.

The earlier Trojan:Win32/Wacatac.C!ml affected path/hash/date was never identified. This successful scan does not establish the cause of that historical warning or guarantee future classification. No exact Defender heuristic cause is claimed and no sample was uploaded.

If a warning returns, record Protection History's affected file, threat name, timestamp and SHA-256. Submit that exact file for review at [Microsoft file submission](https://www.microsoft.com/en-us/wdsi/filesubmission), identifying it as a developer's suspected incorrect detection and including source/build context. Keep the submission ID and follow Microsoft's result. Do not disable protection or add exclusions. A draft is included in Security/Microsoft-review-draft.txt; submit only if needed.
