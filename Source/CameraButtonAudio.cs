using BepInEx.Configuration;
using UnityEngine;

namespace IsaiahCameraMod
{
    public sealed partial class IsaiahCameraPlugin
    {
        private ConfigEntry<float> clickVolume;
        private GameObject clickAudioRoot;
        private string pendingConfirmation;
        private float confirmationExpires;
        private bool confirmationNeedsRelease, confirmationFromTouch, dispatchingPhysicalButton;
        private readonly System.Collections.Generic.Dictionary<string, CameraLabel> confirmationLabels =
            new System.Collections.Generic.Dictionary<string, CameraLabel>();
        private readonly System.Collections.Generic.HashSet<string> externalButtonsDown =
            new System.Collections.Generic.HashSet<string>();

        private static bool IsProtectedButton(string name) => name == "Btn_Lobby_Hop" || name == "Btn_Reset" || name == "Btn_Reset_View";
        private static string NormalButtonCaption(string name) => name == "Btn_Lobby_Hop" ? "LOBBY HOP" : name == "Btn_Reset_View" ? "RESET VIEW" : "RESET";
        private void InitializeConfirmationLabels()
        {
            confirmationLabels.Clear();
            confirmationLabels.Add("Btn_Lobby_Hop", MakeOriginalLabel("GorillaTag_Name.070", "Btn_Lobby_Hop"));
            confirmationLabels.Add("Btn_Reset_View", MakeOriginalLabel("GorillaTag_Name.071", "Btn_Reset_View"));
            confirmationLabels.Add("Btn_Reset", MakeOriginalLabel("GorillaTag_Name.089", "Btn_Reset"));
            foreach (var entry in confirmationLabels) entry.Value.Set(NormalButtonCaption(entry.Key));
        }
        private void CancelButtonConfirmation()
        {
            if (pendingConfirmation != null && confirmationLabels.TryGetValue(pendingConfirmation, out CameraLabel label))
                label.Set(NormalButtonCaption(pendingConfirmation));
            pendingConfirmation = null; confirmationNeedsRelease = false;
        }
        private void TickButtonConfirmation()
        {
            if (pendingConfirmation == null) return;
            if (Time.unscaledTime >= confirmationExpires) { CancelButtonConfirmation(); return; }
            if ((confirmationFromTouch ? !leftTouch.Latched && !rightTouch.Latched : externalButtonsDown.Count == 0)) confirmationNeedsRelease = false;
        }
        private bool ConfirmButtonAction(string name)
        {
            TickButtonConfirmation();
            if (pendingConfirmation == name)
            {
                if (confirmationNeedsRelease) return false;
                CancelButtonConfirmation();
                return true;
            }
            CancelButtonConfirmation();
            pendingConfirmation = name;
            confirmationExpires = Time.unscaledTime + 5f;
            confirmationFromTouch = dispatchingPhysicalButton;
            confirmationNeedsRelease = confirmationFromTouch || externalButtonsDown.Count != 0;
            if (confirmationLabels.TryGetValue(name, out CameraLabel label)) label.Set("CONFIRM");
            PlayButtonClick();
            return false;
        }

        // Level-based keyboard/controller adapters must use this edge gate.
        // ActivateButton itself remains an onClick (one event per press) API.
        public bool SetButtonPressed(string name, bool down)
        {
            if (name == "Btn_Hide_Face_Hat") name = "Btn_hide_face_Hat";
            if (!down) { externalButtonsDown.Remove(name); TickButtonConfirmation(); return false; }
            if (!externalButtonsDown.Add(name)) return false;
            return ActivateButton(name);
        }

        private void InitializeButtonAudio()
        {
            // The builder imports the supplied sound as PCM at its source rate.
            // Keep this source outside the tablet so CLOSE/HIDE still play fully.
            AudioClip[] clips = bundle.LoadAllAssets<AudioClip>();
            if (clips.Length == 0)
            {
                Logger.LogWarning("Button sound is unavailable: the bundle contains no supplied click audio. Haptics remain active.");
                return;
            }
            if (clips.Length != 1)
                throw new System.InvalidOperationException("Expected exactly one supplied button-click audio clip in this bundle.");
            clickAudioRoot = new GameObject("IsaiahCamera_ButtonAudio");
            DontDestroyOnLoad(clickAudioRoot);
            buttonAudio = clickAudioRoot.AddComponent<AudioSource>();
            buttonAudio.playOnAwake = false;
            buttonAudio.loop = false;
            buttonAudio.spatialBlend = 0f;
            buttonAudio.pitch = 1f;
            buttonAudio.dopplerLevel = 0f;
            buttonAudio.clip = clips[0];
            buttonAudio.clip.LoadAudioData();
            Logger.LogInfo("Button click loaded: " + clips[0].name + "; " + clips[0].frequency + " Hz, " + clips[0].channels + " channels.");
        }

        private void PlayButtonClick()
        {
            if (!buttonAudio || !buttonAudio.clip || buttonAudio.clip.loadState != AudioDataLoadState.Loaded) return;
            buttonAudio.volume = Mathf.Clamp01(clickVolume.Value);
            // One reusable voice. A new accepted activation retriggers the source;
            // no stacked PlayOneShot voices, pitch changes, or per-click allocation.
            buttonAudio.Stop();
            buttonAudio.Play();
        }

        // Central entry point for physical touches or an existing Unity UI
        // Button.onClick / keyboard adapter. These adapters must not also play
        // their own sound or call Press directly.
        public bool ActivateButton(string objectName)
        {
            // Both the authored asset spelling and the requested logical name
            // resolve to the same single interaction/feedback path.
            if(objectName=="Btn_Hide_Face_Hat")objectName="Btn_hide_face_Hat";
            bool externalMode = !dispatchingPhysicalButton && (objectName == "Btn_FirstPerson" || objectName == "Btn_ThirdPerson" || objectName == "Btn_Follow_Player" || objectName == "Btn_Flip");
            if (!ready || (!externalMode && (placement == PlacementMode.Hidden || !modelRig.activeInHierarchy)) || IsButtonBlockedByGrab(objectName)) return false;
            foreach (Button button in buttons)
            {
                if (button.Name != objectName) continue;
                if ((!externalMode && (!button.Visual.gameObject.activeInHierarchy ||
                    (button.Activation && !button.Activation.gameObject.activeInHierarchy))) ||
                    Time.unscaledTime < button.NextPress || !Press(objectName)) return false;
                button.NextPress = Time.unscaledTime + ButtonCooldown;
                button.FlashUntil = Time.unscaledTime + .18f;
                return true;
            }
            return false;
        }

        private void ReleaseButtonAudio()
        {
            CancelButtonConfirmation(); confirmationLabels.Clear(); externalButtonsDown.Clear();
            if (buttonAudio) buttonAudio.Stop();
            buttonAudio = null;
            if (clickAudioRoot) Destroy(clickAudioRoot);
            clickAudioRoot = null;
        }
    }
}
