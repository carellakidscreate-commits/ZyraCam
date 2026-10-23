ZyraCam

ZyraCam is a free, open-source camera mod for the PC version of Gorilla Tag.

ZyraCam provides customizable camera controls, physical camera presentation, presets, and other camera features designed to improve your Gorilla Tag recording experience.

This is an unofficial community mod and is not affiliated with or endorsed by Gorilla Tag or Another Axiom.

Download and Installation (PC)

Install Gorilla Tag for PC.

Install a compatible BepInEx 5 mod loader.

Close Gorilla Tag before installing.

Visit the ZyraCam Releases page.

Download ZyraCam.dll.

Place the DLL inside your Gorilla Tag BepInEx/plugins folder.

Remove older copies of the same camera mod to avoid conflicts.

Launch Gorilla Tag and enjoy ZyraCam!

Platform: PCVR only. Standalone Quest installation is not supported by these instructions.

Camera Features

ZyraCam includes the following documented features:

Camera Tablet: A physical camera tablet with a live preview.

Hide Camera: Tap A while the tablet is visible to hide it while camera capture continues.

Selfie Mode: Hold A for approximately 0.3 seconds to enter selfie mode.

Camera Recall: Press A while the tablet is hidden to bring it back.

Third-Person Camera: Capture your gorilla from another perspective.

Follow Camera: Camera presentation designed to follow the player.

Camera Presets: Save and use up to six camera presets.

Customizable Settings: Adjust camera settings and preferences.

Camera Improvements: Optimizations to camera behavior and resource handling.

Testing and Verification

ZyraCam has been tested inside Gorilla Tag by its developer.

In-Game Testing

✅ The developer tested all documented camera features in Gorilla Tag.

✅ The developer reports that all tested features worked as intended.

✅ Camera controls and camera modes were checked during gameplay.

✅ In-game testing was performed separately from the automated validation process.

Code and Build Testing

Additional testing is documented in VALIDATION.md, including:

Source-code and build verification.

Automated camera behavior tests.

Unity-based rendering and camera checks.

Camera settings and preset verification.

Performance-related checks.

Network-related simulation tests.

Important: The existing validation report states that no live gameplay or physical headset testing occurred during that particular automated verification process. The developer's separate in-game testing is reported above.

Future Gorilla Tag updates may affect compatibility.

Security and Safety

ZyraCam's documented build underwent security checks.

✅ Microsoft Defender reported no threats found in the documented October 23, 2026 scan.

✅ The published v1.0.0 DLL's SHA-256 matches the hash recorded in the security report.

✅ Source-code and compiled-code review results are documented.

✅ Security information is publicly available.

SHA-256 of the documented DLL:

62A8A3B04AC54C1258F54E817681471AE8D2DC9CFE4813C44B4F0D84425D7867

See SECURITY.md for the complete security information, including details about a previously reported antivirus detection.

A clean antivirus scan does not guarantee that software is completely free of security vulnerabilities or bugs.

Never disable antivirus protection just to install a mod.

Open Source

ZyraCam's source code is publicly available for transparency, learning, and community contributions.

You can inspect the source code in the Source directory.

The source includes:

C# camera implementation files.

Camera controls and movement logic.

Preset and preference systems.

Camera networking functionality.

Camera assets and project files.

Building from Source

The source project is located at:

Source/ZyraCameraMod.csproj

The project requires a .NET SDK, compatible Gorilla Tag PC game assemblies, and BepInEx 5.

The public download is named ZyraCam.dll and labeled v1.0.0. The existing source project still uses the internal assembly name IsaiahCameraMod and version 2.9.0.

These naming differences should be resolved or documented when publishing future releases. A reproducible build matching the public download has not been independently verified from this repository snapshot.

For additional build information, visit Source/README.md.

License

ZyraCam includes an MIT License.

You may use, inspect, modify, and redistribute code covered by that license, provided you follow its conditions.

Third-party libraries, Gorilla Tag game files, and other assets may have separate licenses or redistribution restrictions.

Bug Reports and Feedback

If you experience problems with ZyraCam, please report them through GitHub Issues.

When reporting a problem, include your Gorilla Tag version, BepInEx version, ZyraCam version, and a description of the issue.

Do not include passwords, account tokens, or private information in reports.

Disclaimer

ZyraCam is an unofficial Gorilla Tag community modification.

While the developer reports successful in-game testing of all documented camera features, compatibility and functionality may change after game updates.

Use the mod responsibly and follow the applicable Gorilla Tag and community-server rules.

ZyraCam — Free, Open Source, and Made for Gorilla Tag PCVR.
