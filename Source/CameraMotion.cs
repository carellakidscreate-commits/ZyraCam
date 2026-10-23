using UnityEngine;

namespace IsaiahCameraMod
{
    internal static class CameraMotionMath
    {
        // The existing 0..1 control is strength, not seconds of trailing delay.
        // Maximum response time is 200 ms; zero still means no filtering.
        public static float ResponseTime(float strength)
            => strength <= 0f ? 0f : .02f + .18f * Mathf.Clamp01(strength);

        public static Vector4 PreviewUvTransform(float rollDegrees, float sourceAspect, float panelAspect)
        {
            float angle=-rollDegrees*Mathf.Deg2Rad, c=Mathf.Cos(angle), s=Mathf.Sin(angle);
            float scale=Mathf.Min(sourceAspect/(Mathf.Abs(c)*panelAspect+Mathf.Abs(s)),
                1f/(Mathf.Abs(s)*panelAspect+Mathf.Abs(c)));
            Vector4 cover=CameraViewMath.CoverUv(sourceAspect,panelAspect);
            return new Vector4(c*panelAspect*scale/sourceAspect/cover.x,-s*scale/sourceAspect/cover.x,
                s*panelAspect*scale/cover.y,c*scale/cover.y);
        }

        public static Quaternion LevelYaw(Quaternion tracked, Quaternion previous)
        {
            Vector3 forward = tracked * Vector3.forward;
            forward.y = 0f;
            // Yaw is undefined at the vertical poles. Keep the last valid heading
            // inside a small cone instead of amplifying tracking noise or flipping.
            return forward.sqrMagnitude > .0025f
                ? Quaternion.LookRotation(forward.normalized, Vector3.up) : previous;
        }

        // Exact first-order response to a linearly moving target over this frame.
        // Unlike endpoint-only Lerp, equal motion at 30/90/144 Hz has equal lag.
        public static Vector3 DampPosition(Vector3 current, Vector3 previousTarget,
            Vector3 target, float response, float dt)
        {
            if (response <= 0f) return target;
            if (dt <= 0f) return current;
            float alpha = CameraViewMath.DampingAlpha(response, dt);
            Vector3 velocity = (target - previousTarget) / dt;
            return target + (current - previousTarget) * (1f - alpha) - velocity * (response * alpha);
        }

        // Reference behavior: approach the player from the camera's CURRENT
        // world position, only outside the minimum-distance sphere. No head yaw
        // offset, orbit, shoulder mount or forced position behind the player.
        public static Vector3 FollowPosition(Vector3 current, Vector3 player, float minimumDistance,
            float response, float dt)
        {
            Vector3 delta = player - current;
            float distance = delta.magnitude;
            if (distance <= minimumDistance || distance < .00001f) return current;
            float travel = (distance - minimumDistance) * CameraViewMath.DampingAlpha(response, dt);
            return current + delta * (travel / distance);
        }
    }

    public sealed partial class IsaiahCameraPlugin
    {
        // Presentation state is transient, separate from the optical mode and
        // preset data. A/grip can recall the same assembly for its controls.
        private bool modeModelHidden, controlsRecalled, haveManualPlacement;
        private Pose manualPlacement;
        private Vector3 manualPlacementHead;

        private void RememberManualPlacement()
        {
            if (!modelRig || !nativeCamera) return;
            manualPlacement = new Pose(modelRig.transform.position, modelRig.transform.rotation);
            manualPlacementHead = nativeCamera.transform.position;
            haveManualPlacement = true;
        }
        private void SelectViewMode(ViewMode requested, bool toggleModel)
        {
            CancelButtonConfirmation();
            if (requested == mode && toggleModel)
            {
                modeModelHidden = !modeModelHidden;
                if (!modeModelHidden && placement == PlacementMode.Hidden) placement = PlacementMode.Hover;
                ApplyPhysicalVisibility();
                return; // no optical-mode change or stale filter reset
            }
            if (mode == ViewMode.Handheld || controlsRecalled) RememberManualPlacement();
            mode = requested;
            automaticViewFlipped = false;
            InitializeModePresentation();
        }
        private void InitializeModePresentation()
        {
            CancelButtonConfirmation();
            modeModelHidden = mode == ViewMode.FirstPerson;
            controlsRecalled = false;
            if (!modelRig || !captureCamera || !nativeCamera) { snap = true; return; }
            modelRig.SetActive(false);
            placement = PlacementMode.Hover;
            viewSessionActive = true;
            if (mode == ViewMode.FollowPlayer || mode == ViewMode.Handheld)
            {
                // Follow begins at the user's placed/recall anchor, not the
                // previous first/third-person capture location or a chase offset.
                if (!haveManualPlacement) { FollowHead(true); RememberManualPlacement(); }
                Vector3 shift = nativeCamera.transform.position - manualPlacementHead;
                if (shift.sqrMagnitude <= 25f) shift = Vector3.zero;
                modelRig.transform.SetPositionAndRotation(manualPlacement.position + shift, manualPlacement.rotation);
            }
            hoverPosition = modelRig.transform.position; hoverRotation = modelRig.transform.rotation;
            viewPoseInitialized = false; firstPersonFrame = -1; snap = true;
            nextCullingDiscovery = 0f;
            UpdateView();
            SyncPhysicalPresentation();
            snap = false;
            leftTouch.Latched = rightTouch.Latched = true; // withdraw before another physical press
        }
        private void SyncPhysicalPresentation()
        {
            if (!modelRig || !captureCamera) return;
            if (mode != ViewMode.Handheld && !controlsRecalled &&
                placement != PlacementMode.HeadFollow && placement != PlacementMode.HeldInHand)
            {
                Quaternion rotation = captureCamera.transform.rotation * opticalToModelRotation;
                if ((mode == ViewMode.FollowPlayer || mode == ViewMode.ThirdPerson) && nativeCamera)
                {
                    // Screen normal is opposite the authored optical-forward axis.
                    // Face the SCREEN toward its owner without changing filming pose.
                    Vector3 away = captureCamera.transform.position - nativeCamera.transform.position;
                    if (away.sqrMagnitude > .0001f)
                    {
                        Vector3 up = Vector3.ProjectOnPlane(captureCamera.transform.up, away.normalized);
                        if (up.sqrMagnitude < .0001f) up = Vector3.ProjectOnPlane(nativeCamera.transform.up, away.normalized);
                        if (up.sqrMagnitude < .0001f) up = Vector3.ProjectOnPlane(Vector3.forward, away.normalized);
                        if (up.sqrMagnitude < .0001f) up = Vector3.ProjectOnPlane(Vector3.right, away.normalized);
                        rotation = Quaternion.LookRotation(away, up) * opticalToModelRotation;
                    }
                }
                modelRig.transform.SetPositionAndRotation(captureCamera.transform.position - rotation * center.localPosition, rotation);
                hoverPosition = modelRig.transform.position; hoverRotation = modelRig.transform.rotation;
            }
            ApplyPhysicalVisibility();
        }
        private void ApplyPhysicalVisibility()
        {
            if (!modelRig) return;
            bool visible = placement != PlacementMode.Hidden && !modeModelHidden;
            if (modelRig.activeSelf != visible) modelRig.SetActive(visible);
        }
        private bool automaticViewFlipped;
        private bool OpticalFlipActive => mode == ViewMode.Handheld ? selfie : automaticViewFlipped;
        private Quaternion OpticalRotation(Quaternion stabilized)
            => OpticalFlipActive ? stabilized * Quaternion.Euler(0f, 180f, 0f) : stabilized;

        private void SyncHeldPose()
        {
            if (!modelRig || !captureCamera || !nativeCamera) return;
            if (placement == PlacementMode.HeadFollow) FollowHead(true);
            else if (placement == PlacementMode.HeldInHand) FollowHoldingHand();
            else return;
            // Read late controller samples without advancing a follow/damping
            // clock a second time. First/third-person keep their own viewpoint.
            if ((placement == PlacementMode.HeldInHand || placement == PlacementMode.HeadFollow) && mode == ViewMode.Handheld)
            { UpdateViewPose(0f); UpdatePreviewOrientation(); }
        }

        private bool viewPoseInitialized;
        private ViewMode trackedViewMode;
        private Vector3 previousViewTarget, previousFollowHead, followPosition;
        private Quaternion headYaw = Quaternion.identity;
        private Vector3 filteredPosition;
        private Quaternion filteredRotation = Quaternion.identity;
        private int firstPersonFrame = -1;
        private Vector3 firstPersonFrameTarget;
        private Quaternion firstPersonFrameRotation;
        private bool firstPersonReset;

        private void UpdateFirstPersonPose(float dt)
        {
            // LateUpdate, onBeforeRender and the end-of-frame capture can all run
            // in one frame. Re-evaluate the latest HMD sample from ONE saved filter
            // state, so XR refreshes never apply damping twice or bypass it.
            Transform head = nativeCamera.transform;
            bool reset = !viewPoseInitialized || trackedViewMode != ViewMode.FirstPerson || snap;
            if (firstPersonFrame != Time.frameCount || reset)
            {
                firstPersonFrame = Time.frameCount;
                firstPersonFrameRotation = filteredRotation;
                firstPersonFrameTarget = previousViewTarget;
                firstPersonReset = reset;
            }
            Vector3 target = head.position;
            Quaternion rotation = ResolveViewRotation(head.rotation);
            float response = CameraMotionMath.ResponseTime(smoothness);
            bool discontinuity = firstPersonReset || dt > .25f ||
                (target - firstPersonFrameTarget).sqrMagnitude > 4f;
            // Locomotion must never be filtered in world space: a 12 cm cap still
            // put the spectator behind the head at speed. Stabilize the optical
            // rotation independently, anchored to the newest HMD position in
            // EVERY late-render stage. This never writes to the tracked HMD.
            filteredPosition = target;
            filteredRotation = discontinuity ? rotation : Quaternion.Slerp(firstPersonFrameRotation,
                rotation, CameraViewMath.DampingAlpha(response, dt));
            if (RollLockApplies) filteredRotation = CameraMotionMath.LevelYaw(filteredRotation, headYaw);
            captureCamera.transform.SetPositionAndRotation(filteredPosition, OpticalRotation(filteredRotation));
            previousViewTarget = target;
            trackedViewMode = ViewMode.FirstPerson;
            viewPoseInitialized = true;
        }

        private float previewOpticalRoll;
        private Vector4 lastPreviewUv;
        private bool previewUvInitialized;
        private void UpdatePreviewOrientation()
        {
            if(!screenMaterial || !captureCamera || previewAspect<=0)return;
            Vector4 uv=new Vector4(1,0,0,1);
            if(mode==ViewMode.Handheld)
            {
                // Rolling the optical camera rotates its feed; rolling the
                // physical screen rotates it a second time in the opposite sense.
                // Remove that optical roll ONLY in the tablet material, so the
                // displayed picture rotates with the physical tablet. The PC
                // keeps the untouched capture. No extra camera/render pass.
                Vector3 forward=captureCamera.transform.forward;
                Vector3 horizon=Vector3.ProjectOnPlane(Vector3.up,forward);
                if(horizon.sqrMagnitude>.0025f)
                    previewOpticalRoll=Vector3.SignedAngle(horizon,captureCamera.transform.up,forward);
                uv=CameraMotionMath.PreviewUvTransform(previewOpticalRoll,captureCamera.aspect,previewAspect);
            }
            if(!previewUvInitialized || (uv-lastPreviewUv).sqrMagnitude>.00000001f)
            {
                screenMaterial.SetVector("_PreviewUvTransform",uv);
                lastPreviewUv=uv;previewUvInitialized=true;
            }
        }

        private void UpdateViewPose(float dt)
        {
            if (mode == ViewMode.FirstPerson) { UpdateFirstPersonPose(dt); return; }
            firstPersonFrame = -1;
            Transform head = nativeCamera.transform;
            headYaw = CameraMotionMath.LevelYaw(head.rotation, headYaw);
            bool changed = !viewPoseInitialized || trackedViewMode != mode;
            bool directHeld = (placement == PlacementMode.HeldInHand || placement == PlacementMode.HeadFollow) && mode == ViewMode.Handheld;
            bool reset = changed || snap || directHeld;
            float response = CameraMotionMath.ResponseTime(smoothness);
            float alpha = reset ? 1f : CameraViewMath.DampingAlpha(response, dt);
            Vector3 position;
            Quaternion rotation;

            if (mode == ViewMode.FollowPlayer)
            {
                if (changed || (snap && (placement == PlacementMode.HeadFollow || placement == PlacementMode.HeldInHand)))
                    followPosition = center.position;
                else if ((head.position - previousFollowHead).sqrMagnitude > 25f)
                    followPosition += head.position - previousFollowHead; // preserve framing across a teleport
                if (placement == PlacementMode.HeadFollow || placement == PlacementMode.HeldInHand)
                    followPosition = center.position; // user can reframe with the physical camera
                else if (!changed)
                    followPosition = CameraMotionMath.FollowPosition(followPosition, head.position,
                        followMinimumDistance.Value, response, dt);
                previousFollowHead = head.position;
                position = followPosition;
                Vector3 direction = head.position - position;
                rotation = direction.sqrMagnitude > .0001f
                    ? Quaternion.LookRotation(direction, Vector3.up) : captureCamera.transform.rotation;
            }
            else if (mode == ViewMode.ThirdPerson)
            {
                position = head.position + headYaw * thirdPersonOffset.Value;
                Vector3 direction = head.position + Vector3.down * .2f - position;
                rotation = direction.sqrMagnitude > .0001f ? Quaternion.LookRotation(direction, Vector3.up) : head.rotation;
            }
            else
            {
                position = center.position;
                rotation = forwardMarker ? forwardMarker.rotation : modelRig.transform.rotation;
            }

            rotation = ResolveViewRotation(rotation);
            Vector3 output = position;
            bool reframe = placement == PlacementMode.HeadFollow || placement == PlacementMode.HeldInHand;
            if (!reset && (mode != ViewMode.FollowPlayer || reframe) &&
                (position - previousViewTarget).sqrMagnitude <= 25f)
                output = CameraMotionMath.DampPosition(filteredPosition, previousViewTarget, position, response, dt);
            Quaternion outputRotation = Quaternion.Slerp(filteredRotation, rotation, alpha);
            // Toggling ON removes pitch/roll immediately, even during a damped turn.
            if (RollLockApplies) outputRotation = CameraMotionMath.LevelYaw(outputRotation, headYaw);
            // Filter state belongs to this engine, never to a transform another
            // render callback can overwrite. Late HMD updates only affect FIRST PERSON.
            if (mode == ViewMode.FollowPlayer && reframe) followPosition = output;
            filteredPosition = output; filteredRotation = outputRotation;
            captureCamera.transform.SetPositionAndRotation(output, OpticalRotation(outputRotation));
            previousViewTarget = position;
            trackedViewMode = mode;
            viewPoseInitialized = true;
        }
    }
}
