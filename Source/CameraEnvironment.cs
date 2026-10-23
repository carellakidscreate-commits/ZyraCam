using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace IsaiahCameraMod
{
    // Only presentation methods enter this scope. Never patch the network clock,
    // gameplay weather getters, room properties, or SetTimeOfDay/RPC methods.
    internal struct CameraEnvironmentScope : IDisposable
    {
        private readonly BetterDayNightManager manager;
        private readonly int index;
        private readonly float blend;
        private readonly bool weatherOverride;
        private readonly BetterDayNightManager.WeatherType weatherType;
        private readonly BetterDayNightManager.WeatherType[] cycle;
        private readonly int a, b;
        private readonly BetterDayNightManager.WeatherType oldA, oldB;
        private bool disposed;
        internal CameraEnvironmentScope(BetterDayNightManager m, int time, bool rain)
        {
            this=default;
            manager=m; index=m.currentTimeIndex; blend=m.currentLerp;
            weatherOverride=m.overrideWeather; weatherType=m.overrideWeatherType;
            cycle=m.weatherCycle; a=m.currentWeatherIndex;
            b=cycle!=null && cycle.Length>0 ? (a+1)%cycle.Length : -1;
            if(cycle!=null && a>=0 && a<cycle.Length)
            {
                oldA=cycle[a]; oldB=cycle[b];
                cycle[a]=cycle[b]=rain?BetterDayNightManager.WeatherType.Raining:BetterDayNightManager.WeatherType.None;
            }
            m.overrideWeather=true;
            m.overrideWeatherType=rain?BetterDayNightManager.WeatherType.Raining:BetterDayNightManager.WeatherType.None;
            if(time>=0) {m.currentTimeIndex=time; m.currentLerp=1f;}
        }
        public void Dispose()
        {
            if(disposed)return; disposed=true;
            if(cycle!=null && a>=0 && a<cycle.Length) {cycle[a]=oldA;cycle[b]=oldB;}
            if(!manager)return;
            manager.currentTimeIndex=index;manager.currentLerp=blend;
            manager.overrideWeather=weatherOverride;manager.overrideWeatherType=weatherType;
        }
    }

    public sealed partial class IsaiahCameraPlugin
    {
        private bool rainEnabled, environmentActive, environmentFaulted;
        private string RainCaption => environmentFaulted ? "N/A" : rainEnabled ? (rainSupported?"ON":"N/A") : "OFF";
        private bool rainSupported;
        private Harmony environmentHarmony;
        private static IsaiahCameraPlugin environmentOwner;
        private readonly List<TimeOfDayDependentAudio> environmentalControllers=new List<TimeOfDayDependentAudio>();
        private Action<TimeOfDayDependentAudio> refreshNativeAmbience;
        private float nextEnvironmentScan;
        private bool refreshEnvironmentNow;
        private bool environmentSceneDirty = true;
        private readonly HashSet<TimeOfDayDependentAudio> knownEnvironmentControllers = new HashSet<TimeOfDayDependentAudio>();
        private readonly HashSet<TimeOfDayDependentAudio> pendingEnvironmentControllers = new HashSet<TimeOfDayDependentAudio>();
        private void EnvironmentSceneLoaded(Scene scene, LoadSceneMode mode) { environmentSceneDirty = true; }
        private static void EnvironmentControllerEnabled(TimeOfDayDependentAudio __instance)
        {
            var owner = environmentOwner;
            if (!owner || !owner.environmentActive) return;
            owner.RegisterEnvironmentController(__instance);
            owner.nextEnvironmentScan = 0f;
        }
        private void RegisterEnvironmentController(TimeOfDayDependentAudio controller)
        {
            if (knownEnvironmentControllers.Add(controller)) environmentalControllers.Add(controller);
            pendingEnvironmentControllers.Add(controller);
        }

        private void ToggleRain()
        {
            rainEnabled=!rainEnabled;
            ApplyEnvironmentSelection(timeSelection,rainEnabled);
        }
        private void ApplyEnvironmentSelection(int selectedTime,bool rain)
        {
            timeSelection=Mathf.Clamp(selectedTime,0,CameraTimePolicy.Names.Length-1);
            rainEnabled=rain; environmentFaulted=false;timeUnavailable=false;appliedPreset=-1;
            refreshEnvironmentNow=true;
            EnsureEnvironmentHooks();
            UpdateLocalTimePresentation();
        }
        private void EnsureEnvironmentHooks()
        {
            if(environmentHarmony!=null){environmentActive=true;return;}
            try
            {
                const BindingFlags flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
                MethodInfo enabled=typeof(TimeOfDayDependentAudio).GetMethod("OnEnable",flags);
                MethodInfo audio=typeof(TimeOfDayDependentAudio).GetMethod("UpdateTimeOfDay",flags);
                MethodInfo maps=typeof(BetterDayNightManager).GetMethod("ChangeMaps",flags);
                MethodInfo blend=typeof(BetterDayNightManager).GetMethod("ChangeLerps",flags);
                if(enabled==null||audio==null||maps==null||blend==null)throw new NotSupportedException("Native environment presentation API changed.");
                refreshNativeAmbience=(Action<TimeOfDayDependentAudio>)Delegate.CreateDelegate(typeof(Action<TimeOfDayDependentAudio>),audio);
                environmentHarmony=new Harmony("com.isaiah.handheldcamera.environment");
                environmentOwner=this;
                environmentHarmony.Patch(audio,prefix:new HarmonyMethod(typeof(IsaiahCameraPlugin),nameof(BeforeAmbience)),
                    finalizer:new HarmonyMethod(typeof(IsaiahCameraPlugin),nameof(AfterEnvironment)));
                environmentHarmony.Patch(maps,prefix:new HarmonyMethod(typeof(IsaiahCameraPlugin),nameof(BeforeLightingMaps)),
                    finalizer:new HarmonyMethod(typeof(IsaiahCameraPlugin),nameof(AfterEnvironment)));
                environmentHarmony.Patch(blend,prefix:new HarmonyMethod(typeof(IsaiahCameraPlugin),nameof(BeforeLightingBlend)));
                environmentHarmony.Patch(enabled,postfix:new HarmonyMethod(typeof(IsaiahCameraPlugin),nameof(EnvironmentControllerEnabled)));
                SceneManager.sceneLoaded += EnvironmentSceneLoaded;
                environmentSceneDirty=true;
                environmentActive=true;
            }
            catch(Exception ex)
            {
                environmentActive=false;environmentFaulted=true;
                environmentHarmony?.UnpatchSelf();environmentHarmony=null;environmentOwner=null;
                Logger.LogWarning("Time/Rain unavailable: "+ex);
            }
        }
        private static void BeforeAmbience(ref CameraEnvironmentScope __state)
        {
            var owner=environmentOwner;var m=BetterDayNightManager.instance;
            if(!owner || !owner.environmentActive || !m)return;
            int time=owner.timePresetCache.Resolve(m.dayNightLightmapNames,m.timeOfDayRange,owner.timeSelection);
            __state=new CameraEnvironmentScope(m,time,owner.rainEnabled);
        }
        private static void BeforeLightingMaps(BetterDayNightManager __instance,ref int __0,ref int __1,
            ref CameraEnvironmentScope __state)
        {
            var owner=environmentOwner;
            if(!owner || !owner.environmentActive || __instance!=BetterDayNightManager.instance)return;
            int time=owner.timePresetCache.Resolve(__instance.dayNightLightmapNames,__instance.timeOfDayRange,owner.timeSelection);
            if(time>=0)__0=__1=time;
            __state=new CameraEnvironmentScope(__instance,-1,owner.rainEnabled);
        }
        private static void BeforeLightingBlend(BetterDayNightManager __instance,ref float __0)
        {
            var owner=environmentOwner;
            if(owner && owner.environmentActive && owner.timeSelection>0 && __instance==BetterDayNightManager.instance)__0=0f;
        }
        private static Exception AfterEnvironment(Exception __exception,CameraEnvironmentScope __state)
        { __state.Dispose();return __exception; }

        private void RefreshEnvironmentControllers()
        {
            if(!environmentActive)return;
            if(environmentSceneDirty)
            {
                foreach(var c in UnityEngine.Object.FindObjectsByType<TimeOfDayDependentAudio>(FindObjectsInactive.Include,FindObjectsSortMode.None))
                    RegisterEnvironmentController(c);
                environmentSceneDirty=false;
            }
            if(Time.unscaledTime>=nextEnvironmentScan)
            {
                nextEnvironmentScan=Time.unscaledTime+2f;
                for(int i=environmentalControllers.Count-1;i>=0;i--)
                    if(!environmentalControllers[i]) {knownEnvironmentControllers.Remove(environmentalControllers[i]);environmentalControllers.RemoveAt(i);}
                bool supported=false;
                foreach(var c in environmentalControllers)
                    if(c && c.enabled && c.myWeather==BetterDayNightManager.WeatherType.Raining && c.myParticleSystem &&
                        (!c.transform.parent || c.transform.parent.gameObject.activeInHierarchy))supported=true;
                if(supported!=rainSupported){rainSupported=supported;rainLabel?.Set("RAIN: "+RainCaption);}
            }
            if(!refreshEnvironmentNow && pendingEnvironmentControllers.Count==0)return;
            foreach(var c in environmentalControllers)
            {
                if(!refreshEnvironmentNow && !pendingEnvironmentControllers.Contains(c))continue;
                if(!c || !c.enabled || !c.gameObject.scene.IsValid())continue;
                try {refreshNativeAmbience(c);}
                catch(Exception ex) {Logger.LogWarning("Native ambience "+c.name+": "+ex.Message);}
            }
            refreshEnvironmentNow=false;
            pendingEnvironmentControllers.Clear();
            // Native controllers remain registered with their normal slicer. The
            // scoped hook keeps later updates consistent; no cloned audio/particles.
        }
        private void ReleaseEnvironment()
        {
            environmentActive=false;
            SceneManager.sceneLoaded -= EnvironmentSceneLoaded;
            environmentHarmony?.UnpatchSelf();environmentHarmony=null;
            if(environmentOwner==this)environmentOwner=null;
            foreach(var c in environmentalControllers)
                if(c && c.enabled)try{refreshNativeAmbience?.Invoke(c);}catch(Exception ex){Logger.LogWarning(ex.Message);}
            environmentalControllers.Clear();knownEnvironmentControllers.Clear();pendingEnvironmentControllers.Clear();environmentSceneDirty=true;nextEnvironmentScan=0;
        }
    }
}
