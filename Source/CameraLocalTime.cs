using System;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace IsaiahCameraMod
{
    internal static class CameraTimePolicy
    {
        public static readonly string[] Names = { "DEFAULT", "MIDNIGHT", "DAWN", "SUNRISE", "MORNING", "DAYTIME", "NOON", "AFTERNOON", "SUNSET", "EVENING", "NIGHT" };
        public static readonly float[] Hours = { 0, 0, 5, 6, 9, 10, 12, 15, 18, 20, 22 };
        public static float NamedHour(string name, float fallback)
        {
            string value = name ?? "";
            if (value.IndexOf(' ') >= 0) value = value.Replace(" ", "");
            for (int i=1;i<Names.Length;i++) if (string.Equals(value, Names[i], StringComparison.OrdinalIgnoreCase)) return Hours[i];
            bool pm = value.EndsWith("pm", StringComparison.OrdinalIgnoreCase), am = value.EndsWith("am", StringComparison.OrdinalIgnoreCase);
            if ((pm || am) && float.TryParse(value.Substring(0,value.Length-2), NumberStyles.Float, CultureInfo.InvariantCulture, out float hour))
                return hour % 12 + (pm ? 12 : 0);
            return fallback;
        }
        public static int ClosestPreset(string[] names, double[] durations, int selection)
        {
            if (selection <= 0 || selection >= Names.Length || names == null || names.Length == 0) return -1;
            float elapsed=0, distance=float.MaxValue; int best=-1;
            for(int i=0;i<names.Length;i++)
            {
                float hour=NamedHour(names[i], durations != null && durations.Length == names.Length ? elapsed : float.NaN);
                if (!float.IsNaN(hour))
                {
                    float d=Math.Abs((hour % 24)-Hours[selection]); d=Math.Min(d,24-d);
                    if(d<distance){distance=d;best=i;}
                }
                if(durations != null && i<durations.Length)elapsed+=(float)durations[i];
            }
            return best;
        }
    }

    // The same selection is read every frame and by native sliced ambience.
    // Detect both replacement arrays and in-place edits without per-frame allocation.
    internal sealed class CameraTimePresetCache
    {
        private string[] names, savedNames;
        private double[] durations, savedDurations;
        private int selection = -1, result;
        public int Resolve(string[] currentNames, double[] currentDurations, int selected)
        {
            bool changed = selection != selected || names != currentNames || durations != currentDurations;
            if (!changed && currentNames != null)
                for (int i=0;i<currentNames.Length;i++) if (currentNames[i] != savedNames[i]) { changed=true;break; }
            if (!changed && currentDurations != null)
                for (int i=0;i<currentDurations.Length;i++) if (!currentDurations[i].Equals(savedDurations[i])) { changed=true;break; }
            if (changed)
            {
                names=currentNames; durations=currentDurations; selection=selected;
                if (names == null) savedNames=null;
                else { if(savedNames==null || savedNames.Length!=names.Length)savedNames=new string[names.Length]; Array.Copy(names,savedNames,names.Length); }
                if (durations == null) savedDurations=null;
                else { if(savedDurations==null || savedDurations.Length!=durations.Length)savedDurations=new double[durations.Length]; Array.Copy(durations,savedDurations,durations.Length); }
                result=CameraTimePolicy.ClosestPreset(names,durations,selected);
            }
            return result;
        }
    }

    public sealed partial class IsaiahCameraPlugin
    {
        private readonly CameraTimePresetCache timePresetCache = new CameraTimePresetCache();
        private int timeSelection; // Restored independently for each preset.
        private string timeCaption = "DEFAULT";
        private string TimeCaption => timeCaption;
        private BetterDayNightManager presentationManager;
        private Action<int,int> changeLightingMaps;
        private Action<float> changeLightingBlend;
        private int appliedPreset=-1, observedNativeTime=-1, observedWeather=-1, observedSceneCount=-1;
        private bool ownsLocalLighting;
        private bool timeUnavailable;

        private void CycleLocalTime()
        {
            ApplyEnvironmentSelection((timeSelection+1)%CameraTimePolicy.Names.Length,rainEnabled);
        }

        private void UpdateLocalTimePresentation()
        {
            if(!environmentActive||timeUnavailable)return;
            try
            {
                var manager=BetterDayNightManager.instance;
                if(!manager || manager.dayNightLightmapNames==null || manager.dayNightLightmapNames.Length==0 ||
                    BetterDayNightManager.allScenesRenderData.Count==0)
                { SetTimeCaption(CameraTimePolicy.Names[timeSelection]+" N/A"); return; }
                if(presentationManager!=manager)
                {
                    RestoreLocalLightingBindings();
                    presentationManager=manager;
                    // Inspected installed game IL: these two presentation methods
                    // bind local lightmaps/sky textures and shader colors only.
                    // SetTimeOfDay changes lastSentTimeIndex/currentSetting and can
                    // broadcast on OnPlayerJoined; deliberately never call it.
                    const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
                    var maps=typeof(BetterDayNightManager).GetMethod("ChangeMaps",flags,null,new[]{typeof(int),typeof(int)},null);
                    var blend=typeof(BetterDayNightManager).GetMethod("ChangeLerps",flags,null,new[]{typeof(float)},null);
                    if(maps==null||blend==null)throw new NotSupportedException("Game lighting presentation API changed.");
                    changeLightingMaps=(Action<int,int>)Delegate.CreateDelegate(typeof(Action<int,int>),manager,maps);
                    changeLightingBlend=(Action<float>)Delegate.CreateDelegate(typeof(Action<float>),manager,blend);
                }
                int preset=timeSelection==0?manager.currentTimeIndex:
                    timePresetCache.Resolve(manager.dayNightLightmapNames,manager.timeOfDayRange,timeSelection);
                if(preset<0 || preset>=manager.dayNightSkyboxTextures.Length)
                {SetTimeCaption(CameraTimePolicy.Names[timeSelection]+" N/A");return;}
                if(preset!=appliedPreset || observedNativeTime!=manager.currentTimeIndex || observedWeather!=manager.currentWeatherIndex ||
                    observedSceneCount!=BetterDayNightManager.allScenesRenderData.Count)
                {
                    ownsLocalLighting=true;
                    changeLightingMaps(preset,timeSelection==0?(preset+1)%manager.dayNightLightmapNames.Length:preset);
                    appliedPreset=preset;observedNativeTime=manager.currentTimeIndex;observedWeather=manager.currentWeatherIndex;
                    observedSceneCount=BetterDayNightManager.allScenesRenderData.Count;
                    Logger.LogInfo("Local lighting: "+CameraTimePolicy.Names[timeSelection]+" uses native preset "+manager.dayNightLightmapNames[preset]+"; network time state unchanged.");
                }
                changeLightingBlend(timeSelection==0?manager.currentLerp:0f);
                SetTimeCaption(CameraTimePolicy.Names[timeSelection]);
                RefreshEnvironmentControllers();
            }
            catch(Exception ex)
            {
                ReleaseEnvironment();
                environmentFaulted=true;
                RestoreLocalLightingBindings();
                SetTimeCaption(CameraTimePolicy.Names[timeSelection]+" N/A");
                // Leave the selected label available to cycle again; don't retry
                // failing reflection every frame or rebuild the camera.
                Logger.LogWarning("Local lighting unavailable: "+ex.Message);
                timeUnavailable=true;
            }
        }
        private void SetTimeCaption(string text)
        { if(timeCaption==text)return;timeCaption=text;RefreshPreferenceLabels(); }
        private void RestoreLocalLightingBindings()
        {
            try
            {
                if(ownsLocalLighting && presentationManager && changeLightingMaps!=null)
                {
                    int n=presentationManager.dayNightLightmapNames.Length,i=presentationManager.currentTimeIndex;
                    if(i>=0&&i<n){changeLightingMaps(i,(i+1)%n);changeLightingBlend(presentationManager.currentLerp);}
                }
            }
            catch(Exception ex){Logger.LogWarning("Restoring normal lighting: "+ex.Message);}
            ownsLocalLighting=false;presentationManager=null;changeLightingMaps=null;changeLightingBlend=null;appliedPreset=-1;
        }
        private void RestoreLocalTimePresentation()
        { ReleaseEnvironment();RestoreLocalLightingBindings();timeSelection=0;SetTimeCaption("DEFAULT"); }
    }
}
