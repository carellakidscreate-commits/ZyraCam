using System;
using System.Collections.Generic;
using HarmonyLib;
using GorillaTag.Rendering;
using GorillaNetworking;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace IsaiahCameraMod
{
    internal static class CaptureCullingMath
    {
        public static Bounds WorldBounds(Bounds local, Matrix4x4 transform)
        {
            Bounds world = new Bounds(transform.MultiplyPoint3x4(local.center), Vector3.zero);
            for (int i = 0; i < 8; i++) world.Encapsulate(transform.MultiplyPoint3x4(local.center +
                Vector3.Scale(local.extents, new Vector3((i&1)==0?-1:1, (i&2)==0?-1:1, (i&4)==0?-1:1))));
            return world;
        }
        public static Matrix4x4 ConservativeMatrix(Bounds world)
        {
            Vector3 e = world.extents + Vector3.one;
            return Matrix4x4.Ortho(-e.x,e.x,-e.y,e.y,-e.z,e.z) * Matrix4x4.Translate(-world.center);
        }
    }

    public sealed partial class IsaiahCameraPlugin
    {
        private readonly List<MeshFilter> headsetBoundMeshes = new List<MeshFilter>();
        private readonly HashSet<MeshFilter> uniqueHeadsetBoundMeshes = new HashSet<MeshFilter>();
        private readonly Vector3[] frustumCorners = new Vector3[4];
        private readonly Plane[] capturePlanes = new Plane[6];
        private float nextCullingDiscovery;
        private bool loggedCullingCompatibility;
        private readonly List<RendererCullerByTriggers> triggerCullers = new List<RendererCullerByTriggers>();
        private readonly HashSet<RendererCullerByTriggers> uniqueTriggerCullers = new HashSet<RendererCullerByTriggers>();
        private readonly HashSet<FirstPersonMeshCullingDisabler> pendingBoundOwners = new HashSet<FirstPersonMeshCullingDisabler>();
        private readonly List<MeshFilter> boundDiscoveryBuffer = new List<MeshFilter>();
        private readonly List<Renderer> rigDiscoveryBuffer = new List<Renderer>();
        private bool cullingSceneDirty = true;
        private Harmony cullingHarmony;
        private static IsaiahCameraPlugin cullingOwner;

        private static void CaptureTriggerEnabled(RendererCullerByTriggers __instance)
        {
            var owner = cullingOwner;
            if (owner && owner.uniqueTriggerCullers.Add(__instance)) owner.triggerCullers.Add(__instance);
        }
        private static void CaptureBoundsEnabled(FirstPersonMeshCullingDisabler __instance)
        { if (cullingOwner) cullingOwner.pendingBoundOwners.Add(__instance); }
        private void DiscoverCaptureOwners()
        {
            foreach (var owner in FindObjectsByType<RendererCullerByTriggers>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (uniqueTriggerCullers.Add(owner)) triggerCullers.Add(owner);
            foreach (var owner in FindObjectsByType<FirstPersonMeshCullingDisabler>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                pendingBoundOwners.Add(owner);
            cullingSceneDirty = false;
        }
        private readonly List<Renderer> captureEnabledRenderers = new List<Renderer>();
        private readonly List<Renderer> firstPersonRigRenderers = new List<Renderer>();
        private readonly List<Renderer> localHeadRenderers = new List<Renderer>();
        private readonly List<Renderer> captureHiddenRigRenderers = new List<Renderer>();
        private bool loggedTriggerCulling;

        private void InitializeCaptureCulling()
        {
            SceneManager.sceneLoaded += CaptureSceneLoaded;
            SceneManager.sceneUnloaded += CaptureSceneUnloaded;
            nextCullingDiscovery = 0f;
            cullingOwner = this;
            try
            {
                var trigger = AccessTools.Method(typeof(RendererCullerByTriggers), "OnEnable");
                var bounds = AccessTools.Method(typeof(FirstPersonMeshCullingDisabler), "OnEnable");
                if (trigger == null || bounds == null) throw new NotSupportedException("Culling lifecycle API changed.");
                cullingHarmony = new Harmony("com.isaiah.handheldcamera.cullingDiscovery");
                cullingHarmony.Patch(trigger, postfix: new HarmonyMethod(typeof(IsaiahCameraPlugin), nameof(CaptureTriggerEnabled)));
                cullingHarmony.Patch(bounds, postfix: new HarmonyMethod(typeof(IsaiahCameraPlugin), nameof(CaptureBoundsEnabled)));
            }
            catch (Exception ex)
            {
                cullingHarmony?.UnpatchSelf(); cullingHarmony = null;
                Logger.LogWarning("Culling discovery uses compatibility polling: " + ex.Message);
            }
        }
        private void CaptureSceneLoaded(Scene scene, LoadSceneMode loadMode) { cullingSceneDirty = true; nextCullingDiscovery = 0f; appliedPreset = -1; snap = true; nextDesktopDiscovery = 0f; }
        private void CaptureSceneUnloaded(Scene scene) { cullingSceneDirty = true; nextCullingDiscovery = 0f; appliedPreset = -1; snap = true; nextDesktopDiscovery = 0f; }

        private void PrepareCaptureCulling()
        {
            captureCamera.ResetCullingMatrix();
            // Global discovery only at startup/scene transitions. Native OnEnable
            // callbacks register objects spawned or activated later without polling.
            if (cullingSceneDirty || (cullingHarmony == null && Time.unscaledTime >= nextCullingDiscovery)) DiscoverCaptureOwners();
            foreach (var owner in pendingBoundOwners)
            {
                if (!owner) continue;
                owner.GetComponentsInChildren(true, boundDiscoveryBuffer);
                foreach (var filter in boundDiscoveryBuffer)
                    if (uniqueHeadsetBoundMeshes.Add(filter)) headsetBoundMeshes.Add(filter);
            }
            pendingBoundOwners.Clear();
            if (Time.unscaledTime >= nextCullingDiscovery)
            {
                nextCullingDiscovery = Time.unscaledTime + 2f;
                // Keep cosmetics current using only the small local rig subtree.
                for (int i = triggerCullers.Count - 1; i >= 0; i--)
                    if (!triggerCullers[i]) { uniqueTriggerCullers.Remove(triggerCullers[i]); triggerCullers.RemoveAt(i); }
                for (int i = headsetBoundMeshes.Count - 1; i >= 0; i--)
                    if (!headsetBoundMeshes[i]) { uniqueHeadsetBoundMeshes.Remove(headsetBoundMeshes[i]); headsetBoundMeshes.RemoveAt(i); }
                firstPersonRigRenderers.Clear();
                localHeadRenderers.Clear();
                var rig = GorillaTagger.Instance ? GorillaTagger.Instance.offlineVRRig : null;
                rigDiscoveryBuffer.Clear();
                if (rig) rig.GetComponentsInChildren(true, rigDiscoveryBuffer);
                foreach (var renderer in rigDiscoveryBuffer)
                {
                    // The avatar face/head/body are never the face-cosmetic toggle.
                    if (renderer == rig.faceSkin || renderer == rig.mainSkin ||
                        (rig.headMesh && renderer.gameObject == rig.headMesh)) continue;
                    if (renderer.gameObject.layer == (int)UnityLayer.FirstPersonOnly ||
                        renderer.gameObject.layer == (int)UnityLayer.NoMirror) firstPersonRigRenderers.Add(renderer);
                }
                DiscoverLocalHeadCosmetics(rig);

            }
            GeometryUtility.CalculateFrustumPlanes(captureCamera, capturePlanes);
            foreach (var owner in triggerCullers)
            {
                if (!owner || !owner.isActiveAndEnabled || owner.renderers == null) continue;
                bool inside = false;
                if (owner.colliders != null) foreach (var trigger in owner.colliders)
                    if (trigger && trigger.enabled && trigger.gameObject.activeInHierarchy &&
                        (trigger.ClosestPoint(captureCamera.transform.position)-captureCamera.transform.position).sqrMagnitude < .01f)
                    { inside = true; break; }
                foreach (var renderer in owner.renderers)
                {
                    if (!renderer || renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                    if (!inside && !GeometryUtility.TestPlanesAABB(capturePlanes, renderer.bounds)) continue;
                    captureEnabledRenderers.Add(renderer);
                    renderer.enabled = true;
                }
            }
            if (captureEnabledRenderers.Count > 0 && !loggedTriggerCulling)
            {
                loggedTriggerCulling = true;
                Logger.LogInfo("Capture-only visibility restored " + captureEnabledRenderers.Count +
                    " headset-trigger-culled renderers; sample=" + captureEnabledRenderers[0].name + ". Restored after this render.");
            }
            if (mode != ViewMode.FirstPerson) foreach (var renderer in firstPersonRigRenderers)
                if (renderer && !renderer.forceRenderingOff)
                { captureHiddenRigRenderers.Add(renderer); renderer.forceRenderingOff = true; }
            if (hideFaceHat && mode == ViewMode.FirstPerson) foreach (var renderer in localHeadRenderers)
                if (renderer && !renderer.forceRenderingOff)
                { captureHiddenRigRenderers.Add(renderer); renderer.forceRenderingOff = true; }
            if (headsetBoundMeshes.Count == 0) return;
            Bounds volume = new Bounds(captureCamera.transform.position, Vector3.zero);
            for (int plane = 0; plane < 2; plane++)
            {
                float distance = plane == 0 ? captureCamera.nearClipPlane : captureCamera.farClipPlane;
                captureCamera.CalculateFrustumCorners(new Rect(0,0,1,1),distance,Camera.MonoOrStereoscopicEye.Mono,frustumCorners);
                foreach (Vector3 corner in frustumCorners) volume.Encapsulate(captureCamera.transform.TransformPoint(corner));
            }
            bool expand = false;
            foreach (MeshFilter filter in headsetBoundMeshes)
            {
                if (!filter || !filter.gameObject.activeInHierarchy || !filter.sharedMesh) continue;
                Bounds reported = CaptureCullingMath.WorldBounds(filter.sharedMesh.bounds, filter.transform.localToWorldMatrix);
                if (GeometryUtility.TestPlanesAABB(capturePlanes, reported)) continue;
                volume.Encapsulate(reported); expand = true;
            }
            if (!expand) return;
            // The game's FirstPersonMeshCullingDisabler deliberately replaces
            // geometry bounds with a 1 m box in front of the HMD on map activation.
            // Opposite-facing cameras then discard visible geometry. Widen only
            // OUR culling volume to include those artificial bounds. The optical
            // projection, actual meshes, native HMD, and map loading stay intact.
            // Reset in RenderView's finally, including when a render throws.
            captureCamera.cullingMatrix = CaptureCullingMath.ConservativeMatrix(volume);
            if (!loggedCullingCompatibility)
            {
                loggedCullingCompatibility = true;
                Logger.LogInfo("Capture culling includes headset-relative map bounds; mesh/zone state unchanged. Tracked meshes=" + headsetBoundMeshes.Count);
            }
        }
        private void DiscoverLocalHeadCosmetics(VRRig rig)
        {
            if (!rig || rig.cosmeticsObjectRegistry == null || rig.mergedSet?.items == null) return;
            foreach (var item in rig.mergedSet.items)
            {
                if (item.itemCategory != CosmeticsController.CosmeticCategory.Face &&
                    item.itemCategory != CosmeticsController.CosmeticCategory.Hat) continue;
                if (string.IsNullOrEmpty(item.displayName)) continue;
                var instance = rig.cosmeticsObjectRegistry.Cosmetic(item.displayName);
                if (instance == null || instance.allRenderers == null) continue;
                foreach (var renderer in instance.allRenderers)
                {
                    if (!renderer || renderer == rig.faceSkin || renderer == rig.mainSkin ||
                        (rig.headMesh && renderer.gameObject == rig.headMesh) || localHeadRenderers.Contains(renderer)) continue;
                    localHeadRenderers.Add(renderer);
                    // Cosmetics remain visible in external modes even if their
                    // native presentation uses a first-person-only layer.
                    firstPersonRigRenderers.Remove(renderer);
                }
            }
        }
        private void ReleaseCaptureCulling()
        {
            RestoreCaptureVisibility();
            cullingHarmony?.UnpatchSelf(); cullingHarmony = null;
            if (cullingOwner == this) cullingOwner = null;
            triggerCullers.Clear(); uniqueTriggerCullers.Clear(); pendingBoundOwners.Clear();
            boundDiscoveryBuffer.Clear(); rigDiscoveryBuffer.Clear(); cullingSceneDirty = true;
            SceneManager.sceneLoaded -= CaptureSceneLoaded;
            SceneManager.sceneUnloaded -= CaptureSceneUnloaded;
            headsetBoundMeshes.Clear(); uniqueHeadsetBoundMeshes.Clear();
            localHeadRenderers.Clear(); firstPersonRigRenderers.Clear();
            if(captureCamera)captureCamera.ResetCullingMatrix();
        }
        private void RestoreCaptureVisibility()
        {
            foreach (var renderer in captureEnabledRenderers) if (renderer) renderer.enabled = false;
            captureEnabledRenderers.Clear();
            foreach (var renderer in captureHiddenRigRenderers) if (renderer) renderer.forceRenderingOff = false;
            captureHiddenRigRenderers.Clear();
        }
    }
}
