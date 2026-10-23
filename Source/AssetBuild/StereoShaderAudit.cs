using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor.Build;
using UnityEditor.Rendering;

public sealed class StereoShaderAudit : IPreprocessShaders
{
    static readonly Dictionary<string,int> Counts=new Dictionary<string,int>();
    public int callbackOrder=>int.MaxValue;
    public static void Reset()=>Counts.Clear();
    public void OnProcessShader(Shader shader,ShaderSnippetData snippet,IList<ShaderCompilerData> data)
    {
        if(!shader.name.StartsWith("Isaiah/",StringComparison.Ordinal))return;
        if(!Counts.ContainsKey(shader.name))Counts.Add(shader.name,0);
        foreach(var variant in data)foreach(var keyword in variant.shaderKeywordSet.GetShaderKeywords())
            if(keyword.name=="STEREO_INSTANCING_ON"){Counts[shader.name]++;break;}
    }
    public static void Verify()
    {
        string result="Stereo variants retained at the final managed shader preprocess stage:\n";
        foreach(string name in new[]{"Isaiah/OriginalLit","Isaiah/OriginalMeshScreen","Isaiah/UI"})
        {
            if(!Counts.TryGetValue(name,out int count)||count==0)throw new BuildFailedException("No stereo variants retained for "+name);
            result+=name+": "+count+"\n";
        }
        File.WriteAllText(Path.Combine(Application.dataPath,"../../stereo-shader-result.txt"),result+"Hardware two-eye verification still required.\n");
    }
}
