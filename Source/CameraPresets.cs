using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;

namespace IsaiahCameraMod
{
    [DataContract]
    internal sealed class CameraPreset
    {
        [DataMember] public int Version = 1;
        [DataMember] public float Fov = 70, Smoothness = .1f, Near = .03f, FollowDistance = 2;
        [DataMember] public string Mode = "Handheld", BeforeFollow = "Handheld";
        [DataMember] public bool Selfie, RollLock, Network, HoldingHand;
        // Added in 2.5. Missing members in 2.4 JSON safely mean DEFAULT/OFF.
        [DataMember] public int TimeSelection;
        [DataMember] public bool Rain;
        // Added in 2.6. Old snapshots restore normal capture visibility (OFF).
        [DataMember] public bool HideFaceHat;
        // Optional since 2.8: automatic-mode optical reversal. Older snapshots
        // remain forward-facing; the existing handheld Selfie value is unchanged.
        [DataMember] public bool AutomaticViewFlipped;
        [DataMember] public bool Monitor = true;
        [DataMember] public float ModelScale = 1.08f, AssemblyScale = 1.18f, Volume = .65f;
        [DataMember] public float GrabRadius = .12f, TouchRadius = .012f;
        [DataMember] public int Width = 1920, Height = 1080;
        [DataMember] public float[] ThirdOffset = { 0, .5f, -2 }, LeftTouch = { 0, 0, 0 }, RightTouch = { 0, 0, 0 };
        // Empty means ORIGINAL per-material colors, not a rounded RGB substitute.
        [DataMember] public string[] Colors = new string[CameraPresetCodec.ColorKeys.Length];
    }

    internal static class CameraPresetCodec
    {
        public static readonly string[] ColorKeys = { "MainFrame", "ColorMenuFrame", "Buttons", "ButtonHolders",
            "LeftHandle", "RightHandle", "LeftTopTip", "LeftBottomTip", "RightTopTip", "RightBottomTip",
            "LensHolder", "LensGlass", "LensIndicator" };
        public static string Encode(CameraPreset data)
        {
            using (var stream = new MemoryStream())
            { new DataContractJsonSerializer(typeof(CameraPreset)).WriteObject(stream, data); return Encoding.UTF8.GetString(stream.ToArray()); }
        }
        public static CameraPreset Decode(string json)
        {
            if (string.IsNullOrEmpty(json)) return new CameraPreset();
            if (json.Length > 16384) throw new FormatException("Preset exceeds supported size.");
            CameraPreset p;
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                p = (CameraPreset)new DataContractJsonSerializer(typeof(CameraPreset)).ReadObject(stream);
            if (p == null || p.Version != 1 || p.Colors == null || p.Colors.Length != ColorKeys.Length)
                throw new FormatException("Unsupported or incomplete preset.");
            foreach (string code in p.Colors)
                if (!string.IsNullOrEmpty(code) && !CameraColorMath.TryDigits(code, out _)) throw new FormatException("Invalid RGB code.");
            ValidateVector(p.ThirdOffset); ValidateVector(p.LeftTouch); ValidateVector(p.RightTouch);
            p.Fov = Valid(p.Fov, 70, 30, 120); p.Smoothness = Valid(p.Smoothness, .1f, 0, 1);
            p.Near = Valid(p.Near, .03f, .01f, .5f); p.FollowDistance = Valid(p.FollowDistance, 2, .5f, 10);
            p.ModelScale = Valid(p.ModelScale, 1.08f, 1, 3); p.AssemblyScale = Valid(p.AssemblyScale, 1.18f, .75f, 1.5f);
            p.Volume = Valid(p.Volume, .65f, 0, 1); p.GrabRadius = Valid(p.GrabRadius, .12f, .03f, .3f);
            p.TouchRadius = Valid(p.TouchRadius, .012f, .003f, .05f);
            p.Width = Math.Max(320, Math.Min(3840, p.Width)); p.Height = Math.Max(180, Math.Min(2160, p.Height));
            p.TimeSelection = Math.Max(0, Math.Min(CameraTimePolicy.Names.Length-1,p.TimeSelection));
            return p;
        }
        private static float Valid(float v, float fallback, float min, float max) => CameraPreferencesPolicy.ValidFloat(v, fallback, min, max);
        private static void ValidateVector(float[] v)
        {
            if (v == null || v.Length != 3) throw new FormatException("Missing preset position setting.");
            foreach (float f in v) if (float.IsNaN(f) || float.IsInfinity(f) || Math.Abs(f) > 20) throw new FormatException("Invalid preset position setting.");
        }
    }

    public sealed partial class IsaiahCameraPlugin
    {
        private readonly ConfigEntry<string>[] presetSlots = new ConfigEntry<string>[6];
        private ConfigEntry<int> selectedPreset;
        private bool startupPresetApplied;
        private float currentAssemblyFactor;
        private CameraLabel networkLabel, presetStatus;

        private void InitializePresets()
        {
            selectedPreset = Config.Bind("Presets", "Selected", 1, "Last selected slot, remembered immediately. SAVE alone writes slot contents.");
            selectedPreset.Value = Math.Max(1, Math.Min(6, selectedPreset.Value));
            for (int i = 0; i < 6; i++) presetSlots[i] = Config.Bind("Presets", "Slot" + (i + 1), "", "Saved settings JSON. Empty slot loads defaults. Tabs and temporary world poses are never saved.");
        }
        private void ApplyStartupPreset()
        {
            if (startupPresetApplied) return;
            // Keep the pre-preset configuration recoverable on first upgrade.
            // This archive is not a preset and never changes any of the six slots.
            var legacy = Config.Bind("Presets", "BeforePresetUpgrade", "", "One-time archive of settings before preset startup defaults were applied.");
            if (string.IsNullOrEmpty(legacy.Value)) legacy.Value = CameraPresetCodec.Encode(CapturePreset());
            LoadPreset(selectedPreset.Value);
            startupPresetApplied = true;
        }
        private void LoadPreset(int slot)
        {
            if (slot < 1 || slot > 6) throw new ArgumentOutOfRangeException(nameof(slot));
            CameraPreset data;
            try { data = CameraPresetCodec.Decode(presetSlots[slot - 1].Value); }
            catch (Exception ex) { Logger.LogWarning("Preset " + slot + " is invalid; using defaults without overwriting it: " + ex.Message); data = new CameraPreset(); }
            selectedPreset.Value = slot;
            ApplyPreset(data);
            SavePreferences(); // selected index is independent of slot contents
            presetStatus?.Set("PRESET " + slot);
        }
        private void SaveCurrentPreset()
        {
            presetSlots[selectedPreset.Value - 1].Value = CameraPresetCodec.Encode(CapturePreset());
            SavePreferences();
            presetStatus?.Set("SAVED " + selectedPreset.Value);
        }
        private static float[] Values(Vector3 v) => new[] { v.x, v.y, v.z };
        private static Vector3 Vector(float[] v) => new Vector3(v[0], v[1], v[2]);
        private CameraPreset CapturePreset()
        {
            var p = new CameraPreset { Fov = fov, Smoothness = smoothness, Near = nearClip, Mode = mode.ToString(),
                BeforeFollow = beforeFollow.ToString(), Selfie = selfie, AutomaticViewFlipped = automaticViewFlipped, RollLock = rollLockEnabled, Network = networkEnabled,
                TimeSelection = timeSelection, Rain = rainEnabled, HideFaceHat = hideFaceHat,
                FollowDistance = followMinimumDistance.Value, ThirdOffset = Values(thirdPersonOffset.Value),
                ModelScale = modelScale.Value, AssemblyScale = assemblySize.Value, Monitor = desktopOutput.Value,
                Volume = clickVolume.Value, Width = textureWidth.Value, Height = textureHeight.Value,
                HoldingHand = allowHoldingHand.Value, GrabRadius = grabRadius.Value, TouchRadius = touchRadius.Value,
                LeftTouch = Values(leftTouchOffset.Value), RightTouch = Values(rightTouchOffset.Value) };
            for (int i = 0; i < p.Colors.Length; i++) p.Colors[i] = colorParts[CameraPresetCodec.ColorKeys[i]].Saved.Value;
            return p;
        }
        private void ApplyPreset(CameraPreset p)
        {
            CancelButtonConfirmation();
            ViewMode priorMode = mode;
            fov = p.Fov; smoothness = p.Smoothness; nearClip = p.Near;
            hideFaceHat = p.HideFaceHat;
            if (!Enum.TryParse(p.Mode, out mode) || !Enum.IsDefined(typeof(ViewMode), mode)) mode = ViewMode.Handheld;
            if (!Enum.TryParse(p.BeforeFollow, out beforeFollow) || !Enum.IsDefined(typeof(ViewMode), beforeFollow) || beforeFollow == ViewMode.FollowPlayer) beforeFollow = ViewMode.Handheld;
            selfie = p.Selfie; automaticViewFlipped = p.AutomaticViewFlipped; rollLockEnabled = p.RollLock; followMinimumDistance.Value = p.FollowDistance;
            thirdPersonOffset.Value = Vector(p.ThirdOffset); desktopOutput.Value = p.Monitor;
            clickVolume.Value = p.Volume; textureWidth.Value = p.Width; textureHeight.Value = p.Height;
            allowHoldingHand.Value = p.HoldingHand; grabRadius.Value = p.GrabRadius; touchRadius.Value = p.TouchRadius;
            leftTouchOffset.Value = Vector(p.LeftTouch); rightTouchOffset.Value = Vector(p.RightTouch);
            modelScale.Value = p.ModelScale; assemblySize.Value = p.AssemblyScale;
            ResizeAssembly(p.ModelScale * p.AssemblyScale);
            ResetOriginalColors();
            for (int i = 0; i < p.Colors.Length; i++)
                if (CameraColorMath.TryDigits(p.Colors[i], out int[] digits))
                { var part = colorParts[CameraPresetCodec.ColorKeys[i]]; part.Digits = digits; part.Saved.Value = p.Colors[i]; ApplyColor(part); }
            SetNetworkEnabled(p.Network);
            ApplyEnvironmentSelection(p.TimeSelection,p.Rain);
            // Mode switches discard delayed follow targets. Open tabs, grip,
            // physical placement are intentionally untouched. Time/Rain were applied above.
            viewPoseInitialized = false; snap = true;
            if (mode != priorMode) InitializeModePresentation();
            nextCullingDiscovery = 0f;
            RefreshText();
        }
        private void ResizeAssembly(float factor)
        {
            if (!model || currentAssemblyFactor <= 0) return;
            float ratio = factor / currentAssemblyFactor;
            model.transform.parent.localScale *= ratio;
            center.localPosition *= ratio;
            for (int i = 0; i < grabAnchors.Count; i++) grabAnchors[i] *= ratio;
            currentAssemblyFactor = factor;
            leftTouch.Reset(); rightTouch.Reset();
        }
    }
}
