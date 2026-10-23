using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public static class PrepareExactCamera
{
    const string Folder="Assets/IsaiahCamera/";
    [Serializable] public class Report { public Obj[] objects; public Mat[] materials; }
    [Serializable] public class Obj { public string name,parent,text; public float[] min,max,fontRight,fontUp; public string[] materials; public int vertices,polygons; }
    [Serializable] public class Mat { public string name; public float[] color; public float roughness,metallic; public bool unlit; }
    static Transform Find(GameObject root,string name)=>root.GetComponentsInChildren<Transform>(true).Single(t=>t.name==name);

    [MenuItem("Isaiah Camera/Build Supplied Exact FBX V5")]
    public static void Build()
    {
        GameObject root=null;
        try {
            // A bundle-only project still needs the VR shader build flags used by
            // its host game. Never install or enable an XR provider at runtime.
            typeof(PlayerSettings).GetProperty("virtualRealitySupported").SetValue(null,true);
            typeof(PlayerSettings).GetProperty("stereoRenderingPath").SetValue(null,StereoRenderingPath.Instancing);
            var graphics=new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset")[0]);
            graphics.FindProperty("m_InstancingStripping").intValue=2; // Keep All
            graphics.ApplyModifiedPropertiesWithoutUndo();
            StereoShaderAudit.Reset();
            var stereoVariants=new ShaderVariantCollection();
            foreach(string shaderName in new[]{"Isaiah/OriginalLit","Isaiah/OriginalMeshScreen","Isaiah/UI"})
            {
                Shader shader=Shader.Find(shaderName);
                if(!shader)throw new Exception("Missing stereo shader "+shaderName);
                var pass=shaderName=="Isaiah/OriginalLit"?UnityEngine.Rendering.PassType.ScriptableRenderPipeline:UnityEngine.Rendering.PassType.Normal;
                stereoVariants.Add(new ShaderVariantCollection.ShaderVariant(shader,pass,"STEREO_INSTANCING_ON"));
                stereoVariants.Add(new ShaderVariantCollection.ShaderVariant(shader,pass,"STEREO_INSTANCING_ON","INSTANCING_ON"));
            }
            string stereoPath=Folder+"CameraStereoVariants.shadervariants";
            var oldVariants=AssetDatabase.LoadAssetAtPath<ShaderVariantCollection>(stereoPath);
            if(oldVariants){EditorUtility.CopySerialized(stereoVariants,oldVariants);UnityEngine.Object.DestroyImmediate(stereoVariants);}
            else AssetDatabase.CreateAsset(stereoVariants,stereoPath);
            const string audioPath=Folder+"ButtonClick.mp3";
            if(!File.Exists(audioPath))throw new FileNotFoundException("The supplied click recording is required.",audioPath);
            AssetDatabase.ImportAsset(audioPath,ImportAssetOptions.ForceSynchronousImport);
            var audioImporter=(AudioImporter)AssetImporter.GetAtPath(audioPath);
            audioImporter.forceToMono=false;
            audioImporter.loadInBackground=false;
            var audioSettings=audioImporter.defaultSampleSettings;
            audioSettings.loadType=AudioClipLoadType.DecompressOnLoad;
            audioSettings.compressionFormat=AudioCompressionFormat.PCM;
            audioSettings.sampleRateSetting=AudioSampleRateSetting.PreserveSampleRate;
            audioSettings.preloadAudioData=true;
            audioImporter.defaultSampleSettings=audioSettings;
            audioImporter.SaveAndReimport();
            var report=JsonUtility.FromJson<Report>(File.ReadAllText(Folder+"CameraModelV5.json"));
            foreach(string file in new[]{"CameraModelV5.fbx","CameraGlyphs.fbx"}) {
                var importer=(ModelImporter)AssetImporter.GetAtPath(Folder+file);
                importer.isReadable=true; importer.importCameras=false; importer.importLights=false;
                importer.meshCompression=ModelImporterMeshCompression.Off;
                importer.importNormals=ModelImporterNormals.Import; importer.importTangents=ModelImporterTangents.CalculateMikk;
                importer.preserveHierarchy=true; importer.SaveAndReimport();
            }
            Directory.CreateDirectory(Folder+"ExactMaterials");
            var materials=new Dictionary<string,Material>();
            foreach(var def in report.materials) {
                var mat=new Material(Shader.Find(def.unlit?"Unlit/Color":"Isaiah/OriginalLit")){name=def.name,enableInstancing=true};
                // Blender stores linear RGB, Unity material color properties are sRGB.
                mat.color=new Color(def.color[0],def.color[1],def.color[2],def.color[3]).gamma;
                if(!def.unlit){mat.SetFloat("_Glossiness",1-def.roughness);mat.SetFloat("_Metallic",def.metallic);}
                string path=Folder+"ExactMaterials/"+def.name+".mat";
                var old=AssetDatabase.LoadAssetAtPath<Material>(path);
                if(old){EditorUtility.CopySerialized(mat,old);UnityEngine.Object.DestroyImmediate(mat);mat=old;}
                else AssetDatabase.CreateAsset(mat,path);
                materials.Add(def.name,mat);
            }
            root=new GameObject("IsaiahCamera");
            var model=UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Folder+"CameraModelV5.fbx"),root.transform);
            model.name="Model";
            foreach(var obj in report.objects) {
                var t=Find(model,obj.name); var r=t.GetComponent<Renderer>();
                if(!r)throw new Exception("Missing original renderer "+obj.name);
                if(obj.parent!="" && t.parent.name!=obj.parent)throw new Exception("Hierarchy changed: "+obj.name);
                var slots=r.sharedMaterials;
                for(int i=0;i<slots.Length;i++)slots[i]=materials[slots[i].name];
                r.sharedMaterials=slots;
                if(obj.fontRight!=null && obj.fontUp!=null) {
                    // Mesh-local FBX -> Unity handedness flips X. Keep the
                    // authored font plane after affine shear was baked in mesh.
                    var x=new GameObject("IsaiahFontRight").transform;x.SetParent(t,false);
                    x.localPosition=new Vector3(obj.fontRight[0],-obj.fontRight[1],-obj.fontRight[2]);
                    var y=new GameObject("IsaiahFontUp").transform;y.SetParent(t,false);
                    y.localPosition=new Vector3(-obj.fontUp[0],obj.fontUp[1],obj.fontUp[2]);
                }
            }
            var body=Find(model,"Main Frame").GetComponent<Renderer>();
            var screen=Find(model,"CameraScreen"); var sr=screen.GetComponent<Renderer>();
            var fov=Find(model,"Btn_FOV_Up");
            Vector3 forward=(body.bounds.center-sr.bounds.center).normalized;
            // FBX import axes: take the screen's thin mesh axis, not the offset
            // between screen/body centers (the display is above center).
            var mf=screen.GetComponent<MeshFilter>();
            var axes=new[]{screen.TransformVector(Vector3.right),screen.TransformVector(Vector3.up),screen.TransformVector(Vector3.forward)};
            var sz=mf.sharedMesh.bounds.size;
            var lengths=new[]{axes[0].magnitude*sz.x,axes[1].magnitude*sz.y,axes[2].magnitude*sz.z};
            int thin=Array.IndexOf(lengths,lengths.Min()),width=Array.IndexOf(lengths,lengths.Max());
            int height=3-thin-width;
            forward=axes[thin].normalized;
            if(Vector3.Dot(forward,body.bounds.center-sr.bounds.center)<0)forward=-forward;
            Vector3 up=axes[height].normalized;
            if(Vector3.Dot(up,fov.position-sr.bounds.center)>0)up=-up;
            Quaternion rotation=Quaternion.Inverse(Quaternion.LookRotation(forward,up));
            model.transform.rotation=rotation*model.transform.rotation;
            // Baseline body width = 44 cm. Runtime's sole assembly scale is 1.08.
            float scale=.44f/body.bounds.size.x;
            model.transform.localScale*=scale;
            model.transform.position-=body.bounds.center;
            new GameObject("IsaiahModelVersion5").transform.SetParent(root.transform,false);
            new GameObject("CameraForward").transform.SetParent(root.transform,false);
            var grip=new GameObject("GripPoint").transform;grip.SetParent(root.transform,false);
            grip.position=Find(model,"LeftHandle").GetComponent<Renderer>().bounds.center;

            // Preserve the original screen mesh and use projected UV coordinates.
            var b=sr.bounds;
            var sm=new Material(Shader.Find("Isaiah/OriginalMeshScreen")){name="IsaiahOriginalScreen",enableInstancing=true};
            Vector3 localOrigin=screen.TransformPoint(Vector3.zero);
            Vector3 lx=screen.TransformVector(Vector3.right),ly=screen.TransformVector(Vector3.up),lz=screen.TransformVector(Vector3.forward);
            sm.SetVector("_UvU",new Vector4(lx.x/b.size.x,ly.x/b.size.x,lz.x/b.size.x,(localOrigin.x-b.min.x)/b.size.x));
            sm.SetVector("_UvV",new Vector4(lx.y/b.size.y,ly.y/b.size.y,lz.y/b.size.y,(localOrigin.y-b.min.y)/b.size.y));
            sm.SetVector("_FaceDirection",screen.worldToLocalMatrix.transpose.inverse.MultiplyVector(Vector3.back).normalized);
            string screenPath=Folder+"OriginalScreen.mat";
            var oldScreen=AssetDatabase.LoadAssetAtPath<Material>(screenPath);
            if(oldScreen){EditorUtility.CopySerialized(sm,oldScreen);UnityEngine.Object.DestroyImmediate(sm);sm=oldScreen;}
            else AssetDatabase.CreateAsset(sm,screenPath);
            sr.sharedMaterial=sm;
            foreach(var t in root.GetComponentsInChildren<Transform>(true))t.gameObject.isStatic=false;
            PrefabUtility.SaveAsPrefabAsset(root,Folder+"IsaiahCamera.prefab");
            AssetDatabase.SaveAssets();
            Preview(root);
            string output=Path.GetFullPath(Path.Combine(Application.dataPath,"../Build"));Directory.CreateDirectory(output);
            var result=BuildPipeline.BuildAssetBundles(output,new[]{new AssetBundleBuild {assetBundleName="isaiahcamera",assetNames=new[]{Folder+"IsaiahCamera.prefab",Folder+"CameraGlyphs.fbx",Folder+"CameraModelV5.json",audioPath,stereoPath,Folder+"CameraUI.shader"}}},BuildAssetBundleOptions.ChunkBasedCompression|BuildAssetBundleOptions.StrictMode|BuildAssetBundleOptions.ForceRebuildAssetBundle,BuildTarget.StandaloneWindows64);
            if(!result)throw new Exception("Asset bundle failed");
            StereoShaderAudit.Verify();
            var bundle=AssetBundle.LoadFromFile(Path.Combine(output,"isaiahcamera"));
            if(!bundle)throw new Exception("Bundle reopen failed");
            try {
                var prefab=bundle.LoadAsset<GameObject>("assets/isaiahcamera/isaiahcamera.prefab");
                foreach(var o in report.objects)Find(prefab,o.name);
                if(!bundle.LoadAsset<GameObject>("assets/isaiahcamera/cameraglyphs.fbx"))throw new Exception("Glyphs missing");
                var clips=bundle.LoadAllAssets<AudioClip>();
                if(clips.Length!=1||clips[0].samples==0||!clips[0].LoadAudioData())throw new Exception("Supplied click recording failed to load");
                var samples=new float[clips[0].samples*clips[0].channels];
                if(!clips[0].GetData(samples,0)||!samples.Any(x=>Mathf.Abs(x)>.0001f))throw new Exception("Click recording is silent or undecodable");
                File.WriteAllText(Path.Combine(Application.dataPath,"../../audio-build-result.txt"),"PASS: supplied MP3 imported as PCM; original sample rate/channels preserved; decoded nonzero samples. "+clips[0].frequency+" Hz, "+clips[0].channels+" channels, "+clips[0].samples+" samples, "+clips[0].length+" seconds.");
            } finally {bundle.Unload(true);}
            // Publish the verified bundle only to the C# project's build input.
            // The runtime reads it from the DLL, never from this loose file.
            string[] sourceRoots = {
                Path.GetFullPath(Path.Combine(Application.dataPath,"../../Source")),
                Path.GetFullPath(Path.Combine(Application.dataPath,"../../../outputs"))
            };
            foreach (string sourceRoot in sourceRoots)
                if (File.Exists(Path.Combine(sourceRoot,"IsaiahCameraMod.csproj")))
                {
                    string resources = Path.Combine(sourceRoot,"Resources");
                    Directory.CreateDirectory(resources);
                    File.Copy(Path.Combine(output,"isaiahcamera"),Path.Combine(resources,"isaiahcamera"),true);
                    Debug.Log("Updated embedded asset build input. Rebuild IsaiahCameraMod.csproj to distribute ONE DLL: " + resources);
                    break;
                }
            File.WriteAllText(Path.Combine(Application.dataPath,"../../exact-build-result.txt"),"PASS: 190 original objects/hierarchy/material names; original screen mesh; glyph library; Windows bundle reloaded. Body baseline width "+body.bounds.size.x+".\n");
            Debug.Log("ISAIAH_EXACT_BUILD_OK "+output);
        }catch(Exception ex){Debug.LogException(ex);File.WriteAllText(Path.Combine(Application.dataPath,"../../exact-build-result.txt"),ex.ToString());}
        finally{if(root)UnityEngine.Object.DestroyImmediate(root);}
    }
    static void Preview(GameObject root) {
        var cam=new GameObject("ExactPreview").AddComponent<Camera>();cam.transform.position=new Vector3(0,.07f,-.9f);cam.transform.LookAt(new Vector3(0,.07f,0));
        cam.fieldOfView=40;cam.nearClipPlane=.01f;cam.backgroundColor=new Color(.12f,.12f,.12f);cam.clearFlags=CameraClearFlags.SolidColor;
        foreach(var r in root.GetComponentsInChildren<Renderer>(true))r.gameObject.layer=30;
        cam.cullingMask=1<<30;
        var light=new GameObject("PreviewLight").AddComponent<Light>();light.type=LightType.Directional;light.intensity=.8f;light.cullingMask=1<<30;light.transform.rotation=Quaternion.Euler(30,-20,0);
        foreach(var other in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None))if(other!=light)other.cullingMask&=~(1<<30);
        RenderSettings.ambientLight=new Color(.25f,.25f,.25f);
        var rt=new RenderTexture(1280,1000,24);cam.targetTexture=rt;cam.Render();RenderTexture.active=rt;
        var tex=new Texture2D(rt.width,rt.height,TextureFormat.RGB24,false);tex.ReadPixels(new Rect(0,0,rt.width,rt.height),0,0);tex.Apply();
        File.WriteAllBytes(Path.Combine(Application.dataPath,"../../exact-model-preview.png"),tex.EncodeToPNG());
        RenderTexture.active=null;UnityEngine.Object.DestroyImmediate(tex);UnityEngine.Object.DestroyImmediate(cam.gameObject);UnityEngine.Object.DestroyImmediate(light.gameObject);rt.Release();UnityEngine.Object.DestroyImmediate(rt);
    }
}
