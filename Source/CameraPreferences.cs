using System;
using BepInEx.Configuration;
using UnityEngine;

namespace IsaiahCameraMod
{
    internal static class CameraPreferencesPolicy
    {
        public static float ValidFloat(float value, float fallback, float min, float max)
            => float.IsNaN(value) || float.IsInfinity(value) ? fallback : Math.Max(min, Math.Min(max, value));
    }

    public sealed partial class IsaiahCameraPlugin
    {
        private ConfigEntry<float> savedFov, savedSmoothness, savedNearClip, followMinimumDistance;
        private ConfigEntry<string> savedMode, savedPreviousMode;
        private ConfigEntry<bool> savedSelfie, savedRotation, savedHideFaceHat, savedAutomaticFlip;
        private bool hideFaceHat;
        private CameraLabel hideFaceHatLabel;
        private bool rollLockEnabled;
        private ViewMode beforeFollow = ViewMode.Handheld;
        private CameraLabel timeLabel, rotationLabel;

        private void LoadPreferences()
        {
            savedFov = Config.Bind("View", "FOV", 70f, "Vertical camera FOV, 30..120 degrees.");
            savedSmoothness = Config.Bind("View", "Smoothness", .10f, "Stabilization strength 0..1. First-person position is always the live HMD anchor; rotation is stabilized. Other modes retain position/rotation smoothing. VR headset tracking is never modified.");
            savedHideFaceHat = Config.Bind("View", "HideFaceHat", false, "Hide only local equipped face/hat cosmetics during FIRST PERSON capture; never the avatar face/head/body. Other modes are unaffected. Applies to preview and PC feed. Saved independently per preset.");
            savedNearClip = Config.Bind("View", "NearClip", .03f, "Near clip distance, 0.01..0.5 metres.");
            savedMode = Config.Bind("View", "Mode", "Handheld", "Handheld, FirstPerson, ThirdPerson, or FollowPlayer.");
            savedPreviousMode = Config.Bind("View", "BeforeFollowPlayer", "Handheld", "Mode restored when FOLLOW PLAYER is switched off.");
            savedAutomaticFlip = Config.Bind("View", "AutomaticFlip", false, "Optical reversal in first/third/follow mode; does not change the tracked position.");
            savedSelfie = Config.Bind("View", "Selfie", false, "Stored handheld flip direction.");
            savedRotation = Config.Bind("View", "RollLock", false, "ON follows live HMD yaw in first/third/follow modes. Both handheld front and selfie ignore it. Never modifies the VR headset.");
            followMinimumDistance = Config.Bind("View", "FollowMinimumDistance", 2f, "Follow Player approaches from its placed world position only beyond this distance (0.5..10 m). It never moves to a head-relative chase offset.");
            fov = CameraPreferencesPolicy.ValidFloat(savedFov.Value, 70, 30, 120);
            smoothness = CameraPreferencesPolicy.ValidFloat(savedSmoothness.Value, .1f, 0, 1);
            nearClip = CameraPreferencesPolicy.ValidFloat(savedNearClip.Value, .03f, .01f, .5f);
            followMinimumDistance.Value = CameraPreferencesPolicy.ValidFloat(followMinimumDistance.Value, 2f, .5f, 10f);
            if (!Enum.TryParse(savedMode.Value, out mode) || !Enum.IsDefined(typeof(ViewMode), mode)) mode = ViewMode.Handheld;
            if (!Enum.TryParse(savedPreviousMode.Value, out beforeFollow) || !Enum.IsDefined(typeof(ViewMode), beforeFollow) || beforeFollow == ViewMode.FollowPlayer)
                beforeFollow = ViewMode.Handheld;
            selfie = savedSelfie.Value; automaticViewFlipped = savedAutomaticFlip.Value; rollLockEnabled = savedRotation.Value;
            hideFaceHat = savedHideFaceHat.Value;
            // Preset startup restores Time/Rain/Network separately. Menu visibility,
            // grab state and pending matchmaking requests remain transient.
        }

        private void SavePreferences()
        {
            if (savedFov == null) return;
            savedFov.Value = fov; savedSmoothness.Value = smoothness; savedNearClip.Value = nearClip;
            savedMode.Value = mode.ToString(); savedPreviousMode.Value = beforeFollow.ToString();
            savedSelfie.Value = selfie; savedAutomaticFlip.Value = automaticViewFlipped; savedRotation.Value = rollLockEnabled;
            savedHideFaceHat.Value = hideFaceHat;
            try { Config.Save(); }
            catch (Exception ex) { Logger.LogWarning("Could not save camera preferences: " + ex.Message); }
        }

        private void ToggleFollowPlayer()
        {
            if (mode != ViewMode.FollowPlayer) beforeFollow = mode;
            SelectViewMode(ViewMode.FollowPlayer, true);
        }

        private void ToggleRotation()
        {
            rollLockEnabled = !rollLockEnabled;
            // Only change orientation. Do not reset the translation filter or
            // relocate a Follow Player camera when toggling a horizon control.
            if (captureCamera && nativeCamera && RollLockApplies)
                captureCamera.transform.rotation = OpticalRotation(ResolveViewRotation(filteredRotation));
        }

        private bool RollLockApplies => rollLockEnabled && mode != ViewMode.Handheld;

        private Quaternion ResolveViewRotation(Quaternion desired)
        {
            if (!RollLockApplies || !nativeCamera) return desired;
            headYaw = CameraMotionMath.LevelYaw(nativeCamera.transform.rotation, headYaw);
            return headYaw;
        }

        private void RefreshPreferenceLabels()
        {
            rotationLabel?.Set("ROLL LOCK: " + (rollLockEnabled ? "ON" : "OFF"));
            timeLabel?.Set("TIME: " + TimeCaption);
            hideFaceHatLabel?.Set("HIDE FACE/HAT: " + (hideFaceHat ? "ON" : "OFF"));
        }
    }
}
