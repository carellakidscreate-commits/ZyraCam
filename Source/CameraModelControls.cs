using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;

namespace IsaiahCameraMod
{
    internal static class CameraColorMath
    {
        // Authored numbering runs BOTTOM to TOP. RGB array order is RED,GREEN,BLUE.
        public static int ChannelForRow(int row)
        {
            if (row < 1 || row > 3) throw new ArgumentOutOfRangeException(nameof(row));
            return 3 - row;
        }
        public static int Step(int value, int direction) => Math.Max(0, Math.Min(9, value + direction));
        public static bool TryDigits(string text, out int[] digits)
        {
            digits = null;
            if (text == null || text.Length != 3) return false;
            var next = new int[3];
            for (int i = 0; i < 3; i++) { if (text[i] < '0' || text[i] > '9') return false; next[i] = text[i] - '0'; }
            digits = next; return true;
        }
    }

    public sealed partial class IsaiahCameraPlugin
    {
        private sealed class ColorPart
        {
            public string Key;
            public ConfigEntry<string> Saved;
            public int[] Digits;
            public readonly List<Material> Materials = new List<Material>();
            public readonly List<Color> Originals = new List<Color>();
        }
        private sealed class NumberDisplay
        {
            public Transform Root, Source, Surface;
            public Vector2 Size;
            public string Previous;
            public readonly List<GameObject> Glyphs = new List<GameObject>();
            public Mesh GeneratedMesh;
        }
        private readonly Dictionary<string, ColorPart> colorParts = new Dictionary<string, ColorPart>();
        private readonly Dictionary<string, Action> extraActions = new Dictionary<string, Action>();
        private readonly List<GameObject> topObjects = new List<GameObject>();
        private readonly Dictionary<char, GameObject> glyphTemplates = new Dictionary<char, GameObject>();
        private readonly List<NumberDisplay> numberDisplays = new List<NumberDisplay>();
        private readonly NumberDisplay[,] colorNumbers = new NumberDisplay[6,3];
        private NumberDisplay smoothNumber, fovNumber, nearNumber;
        private CameraLabel handleSelector, tipSelector;
        private Transform colorExtras;
        private bool colorMenuOpen;
        private int selectedHandle, selectedTip;
        private int appearanceRevision;
        private bool modelRenderersDirty;
        private readonly List<Renderer> hiddenModelRenderers=new List<Renderer>();
        private void RefreshCaptureRendererCache()
        {
            if(!modelRenderersDirty)return;
            modelRenderers=modelRig.GetComponentsInChildren<Renderer>(true);
            modelRenderersDirty=false;
        }
        private static readonly string[] HandleParts = { "LeftHandle", "RightHandle" };
        private static readonly string[] TipParts = { "LeftTopTip", "LeftBottomTip", "RightTopTip", "RightBottomTip" };

        private void BuildAuthoredControls(Transform body, Transform screen)
        {
            FindUnique("IsaiahModelVersion5", true);
            controls = new GameObject("Isaiah_InteractionFrames").transform;
            controls.SetParent(model.transform.parent, false);
            controls.SetPositionAndRotation(center.position, forwardMarker.rotation);
            Bounds display = BoundsInFrame(screen, controls);
            previewAspect = display.size.x / display.size.y;
            activeKeyMaterial = MakeMaterial("UI feedback", Color.white);

            // The new export supplies ALL physical panels/buttons. Never create,
            // move, thicken or replace any authored mesh.
            foreach (string name in ButtonNames)
                RegisterAuthoredButton(name == "Btn_More" ? "Btn_More.002" : name,
                    name == "Btn_More" ? "Btn_More.002" : name);
            RegisterAuthoredButton("Btn_Follow_Player", "Btn_Follow_Player");
            extraActions.Add("Btn_Follow_Player", ToggleFollowPlayer);
            extraActions.Add("Btn_More.002", () => SetMoreMenu(!morePanel.gameObject.activeSelf));
            BindPart("MainFrame", "Main Frame");
            BindPart("MainFrame", "Main Frame.002");
            foreach (string n in new[] { "Main Frame.001", "Main Frame.003", "Main Frame.004", "Main Frame.005" })
                BindPart("ColorMenuFrame", n);
            BindPart("LensHolder", "CameraLens.001");
            BindPart("LensGlass", "CameraLens");
            BindPart("LensIndicator", "Cylinder.002");
            BindPart("LeftHandle", "Sphere.001", "Material.037");
            BindPart("RightHandle", "LeftHandle", "Material.038");
            BindPart("LeftTopTip", "Sphere.001", "Material.028");
            BindPart("LeftBottomTip", "Sphere.001", "Material.026");
            BindPart("RightTopTip", "LeftHandle", "Material.029");
            BindPart("RightBottomTip", "LeftHandle", "Material.027");
            foreach (string n in new[] { "Cube.001", "Cube.002", "Cube.003", "Cube.004", "Cube.005", "Cube.006",
                "Cube.007", "Cube.008", "Cube.009", "Cube.010", "Cube.011", "Cube.013", "Cube.014",
                "Cube.015", "Cube.016", "Cube.017", "Cube.018" }) BindPart("ButtonHolders", n);
            // Btn_Buttons_Up_3 is the PLUS SYMBOL, not the red physical button.
            // Bind each real button surface once, leaving every font/icon alone.
            foreach (Transform t in model.GetComponentsInChildren<Transform>(true))
                if (t.name.StartsWith("Btn_", StringComparison.Ordinal) && t.name != "Btn_Buttons_Up_3")
                    BindPart("Buttons", t.name);

            var glyphAsset = bundle.LoadAsset<GameObject>("assets/isaiahcamera/cameraglyphs.fbx");
            if (!glyphAsset) throw new InvalidOperationException("Original-font glyph library missing.");
            foreach (Transform t in glyphAsset.GetComponentsInChildren<Transform>(true))
                if (t.name.StartsWith("IsaiahGlyph_", StringComparison.Ordinal) &&
                    int.TryParse(t.name.Substring(12), out int code)) glyphTemplates.Add((char)code, t.gameObject);
            InitializeFontMaterials();
            smoothNumber = MakeNumber("GorillaTag_Name.001");
            fovNumber = MakeNumber("GorillaTag_Name.002");
            nearNumber = MakeNumber("GorillaTag_Name.013");
            string[,] names = {
                {"GorillaTag_Name.023","GorillaTag_Name.024","GorillaTag_Name.025"},
                {"GorillaTag_Name.028","GorillaTag_Name.027","GorillaTag_Name.026"},
                {"GorillaTag_Name.043","GorillaTag_Name.044","GorillaTag_Name.045"},
                {"GorillaTag_Name.048","GorillaTag_Name.047","GorillaTag_Name.046"},
                {"GorillaTag_Name.063","GorillaTag_Name.064","GorillaTag_Name.065"},
                {"GorillaTag_Name.081","GorillaTag_Name.080","GorillaTag_Name.079"} };
            string[] categories = { "Main_Frame", "Buttons", "Button_Holder", "Handles", "Handle_Tip/end", "Lens_Holder" };
            for (int category = 0; category < 6; category++)
            {
                for (int ch = 0; ch < 3; ch++) colorNumbers[category,ch] = MakeNumber(names[category,ch]);
                for (int row = 1; row <= 3; row++) foreach (int direction in new[] { -1, 1 })
                {
                    string n = "Btn_" + categories[category] +
                        (direction > 0 ? "_Up_" : category == 5 ? "_down_" : "_Down_") + row;
                    if (n == "Btn_Handles_Down_1") n = "Btn_Handles_Down_!";
                    if (n == "Btn_Buttons_Up_3") n = "Btn_Near_Clipping_Up.004";
                    RegisterAuthoredButton(n, n);
                    int c = category, ch = CameraColorMath.ChannelForRow(row), d = direction;
                    extraActions.Add(n, () => ChangeColor(SelectedPart(c), ch, d));
                }
            }
            BindAction("Btn_Color_Changer", () => SetColorMenu(!colorMenuOpen));
            BindAction("Btn_Time_Changer", CycleLocalTime);
            BindAction("Btn_Rain", ToggleRain);
            // Verified in BOTH supplied files: actual spelling uses capital H/F/H.
            // Preserve that authored name; expose the user's exact requested name.
            RegisterAuthoredButton("Btn_hide_face_Hat", "Btn_Hide_Face_Hat");
            extraActions.Add("Btn_hide_face_Hat", () => { hideFaceHat = !hideFaceHat; nextCullingDiscovery = 0f; });
            BindAction("Btn_Roll_Lock", ToggleRotation);
            BindAction("Btn_Lobby_Hop", StartLobbyHop);
            BindAction("Btn_Reset_View", () => {
                fov = 70; smoothness = .1f; nearClip = .03f; SelectViewMode(ViewMode.Handheld, false);
                selfie = false; rollLockEnabled = false; snap = true;
            });
            BindAction("Btn_Network", () => SetNetworkEnabled(!networkEnabled));
            BindAction("Btn_Save", SaveCurrentPreset);
            BindAction("Btn_Reset", ResetOriginalColors);
            BindAction("Btn_Handle_Changer", () => selectedHandle = (selectedHandle + 1) % 2);
            BindAction("Btn_Tip_Changer", () => selectedTip = (selectedTip + 1) % 4);
            for (int i = 1; i <= 6; i++)
            { int slot = i; BindAction("Btn_Preset_" + slot, () => LoadPreset(slot)); }
            // Keep the existing handle/tip selectors; LENS is holder-only.
            handleSelector = MakeOriginalLabel("GorillaTag_Name.090", "Btn_Handle_Changer");
            tipSelector = MakeOriginalLabel("GorillaTag_Name.091", "Btn_Tip_Changer");
            // The authored LENS heading stays visible and is not a cycling button.
            MakeOriginalLabel("GorillaTag_Name.030", "Btn_Color_Changer").Set("CAMERA COLOR");
            timeLabel = MakeOriginalLabel("GorillaTag_Name.068", "Btn_Time_Changer");
            rotationLabel = MakeOriginalLabel("GorillaTag_Name.069", "Btn_Roll_Lock");
            networkLabel = MakeOriginalLabel("GorillaTag_Name.072", "Btn_Network");
            rainLabel = MakeOriginalLabel("GorillaTag_Name.092", "Btn_Rain");
            hideFaceHatLabel = MakeOriginalLabel("gorillatag_name073", "Btn_Hide_Face_Hat");
            InitializeConfirmationLabels();
            extraReadout = MakeStatusLabel();
            foreach (ColorPart part in colorParts.Values) InitializePart(part);
            foreach (string name in AuthoredColorPanelNames) topObjects.Add(FindUnique(name,true).gameObject);
            foreach (string name in AuthoredMorePanelNames) moreObjects.Add(FindUnique(name,true).gameObject);
            morePanel = new GameObject("MoreVisibilityState").transform;
            morePanel.SetParent(controls, false);
            SetMoreMenu(false); SetColorMenu(false);
            Logger.LogInfo("Exact model V5: " + buttons.Count + " interactions; no mesh or authored transform edits. Btn_hide_face_Hat maps to authored Btn_Hide_Face_Hat. RGB rows: _1=BLUE, _2=GREEN, _3=RED.");
        }

        private readonly List<GameObject> moreObjects = new List<GameObject>();
        private CameraLabel rainLabel;
        private void BindAction(string name, Action action)
        { RegisterAuthoredButton(name, name); extraActions.Add(name, action); }
        private void SetMoreMenu(bool open)
        {
            if (!open) { CancelButtonConfirmation(); SetColorMenu(false); }
            morePanel.gameObject.SetActive(open);
            foreach (GameObject obj in moreObjects) if (obj) obj.SetActive(open);
            RefreshLabelVisibility();
        }
        private void InitializeFontMaterials()
        {
            if (numberWhite) return;
            foreach (Material source in FindUnique("GorillaTag_Name.023",true).GetComponent<Renderer>().sharedMaterials)
            {
                var copy = new Material(source); convertedMaterials.Add(copy);
                if (source.name.StartsWith("text_black",StringComparison.Ordinal)) numberBlack = copy;
                else numberWhite = copy;
            }
            if (!numberWhite || !numberBlack) throw new InvalidOperationException("Original outlined font materials missing.");
        }
        private CameraLabel MakeOriginalLabel(string source, string button = null)
        {
            NumberDisplay display = MakeNumber(source);
            if (button != null)
            {
                display.Surface=FindUnique(button,true);
                Bounds area = OwnBoundsInFrame(display.Surface, display.Root);
                // Nonuniform FBX parent transforms can displace a text mesh's
                // bounds relative to its key. Keep the live caption centered on
                // that SAME authored surface and fit without stretching glyphs.
                display.Root.position += display.Root.TransformVector(new Vector3(area.center.x, area.center.y, 0f));
                display.Size.x = area.size.x * .90f;
                display.Size.y = Mathf.Min(display.Size.y, area.size.y * .65f);
                if(button=="Btn_Rain")display.Size.y=area.size.y*.72f;
            }
            return new CameraLabel(text => SetNumber(display,button=="Btn_Rain"?text.Replace("RAIN: ","RAIN\n"):text));
        }
        private CameraLabel MakeStatusLabel()
        {
            Transform bar = FindUnique("Cube.012",true);
            Transform text = new GameObject("LobbyStatusText").transform;
            text.SetParent(controls,false);
            // Use the same font plane as TIME on the tilted MORE panel.
            Transform example = numberDisplays.Find(n=>n.Source && n.Source.name=="GorillaTag_Name.068").Root;
            text.rotation = example.rotation;
            text.localScale = Vector3.one;
            Bounds b = OwnBoundsInFrame(bar,text);
            text.position = text.TransformPoint(new Vector3(b.center.x,b.center.y,b.max.z + .0005f));
            b = OwnBoundsInFrame(bar,text);
            var display = new NumberDisplay { Root = text, Source=bar, Size = new Vector2(b.size.x * .94f,b.size.y * .70f) };
            numberDisplays.Add(display);
            return new CameraLabel(value => SetNumber(display,value));
        }

        private void BindThemeMaterial(string key,Material material)
        { colorParts[key].Materials.Add(material);colorParts[key].Originals.Add(ReadColor(material)); }

        private void RegisterAuthoredButton(string logical,string actual)
        {
            Transform source=FindUnique(actual,true);var filter=source.GetComponent<MeshFilter>();
            if(!filter)throw new InvalidOperationException("Button is not the physical mesh: "+actual);
            Vector3 size=filter.sharedMesh.bounds.size;
            Vector3[] axes={source.TransformVector(Vector3.right),source.TransformVector(Vector3.up),source.TransformVector(Vector3.forward)};
            float[] lengths={axes[0].magnitude*size.x,axes[1].magnitude*size.y,axes[2].magnitude*size.z};
            int thin=lengths[0]<lengths[1]?(lengths[0]<lengths[2]?0:2):(lengths[1]<lengths[2]?1:2);
            Vector3 forward=axes[thin].normalized;if(Vector3.Dot(forward,controls.forward)<0)forward=-forward;
            Vector3 up=Vector3.ProjectOnPlane(controls.up,forward).normalized;
            Transform frame=new GameObject("Touch_"+logical).transform;frame.SetParent(controls,false);
            frame.SetPositionAndRotation(source.TransformPoint(filter.sharedMesh.bounds.center),Quaternion.LookRotation(forward,up));
            Bounds bounds=OwnBoundsInFrame(source,frame);
            buttons.Add(new Button{Name=logical,Visual=frame,HalfSize=bounds.extents+new Vector3(0,0,.003f),Face=source.GetComponent<Renderer>(),Activation=source});
        }
        private static Bounds OwnBoundsInFrame(Transform source,Transform frame)
        {
            var mesh=source.GetComponent<MeshFilter>().sharedMesh;Bounds result=new Bounds(frame.InverseTransformPoint(source.TransformPoint(mesh.bounds.center)),Vector3.zero);
            foreach(Vector3 v in mesh.vertices)result.Encapsulate(frame.InverseTransformPoint(source.TransformPoint(v)));
            return result;
        }
        private static Color ReadColor(Material mat)=>mat.HasProperty("_BaseColor")?mat.GetColor("_BaseColor"):mat.HasProperty("_Color")?mat.color:Color.white;
        private void BindPart(string key,string objectName,string materialName=null)
        {
            if(!colorParts.TryGetValue(key,out ColorPart part)){part=new ColorPart{Key=key};colorParts.Add(key,part);}
            Renderer renderer=FindUnique(objectName,true).GetComponent<Renderer>();Material[] slots=renderer.sharedMaterials;int count=0;
            for(int i=0;i<slots.Length;i++)if(materialName==null || slots[i].name==materialName)
            {
                Material instance=new Material(slots[i]){name=slots[i].name};slots[i]=instance;convertedMaterials.Add(instance);
                part.Materials.Add(instance);part.Originals.Add(ReadColor(instance));count++;
            }
            if(count==0)throw new InvalidOperationException("Missing verified material region "+objectName+" / "+materialName);
            renderer.sharedMaterials=slots;
        }
        private void InitializePart(ColorPart part)
        {
            part.Saved=Config.Bind("CameraColors",part.Key,"","Active RGB digits 0..9. Empty preserves exact Blender colors. RESET restores originals; SAVE stores colors in the selected preset.");
            if(CameraColorMath.TryDigits(part.Saved.Value,out int[] digits)){part.Digits=digits;ApplyColor(part);}
            else {part.Saved.Value="";Color c=part.Originals[0];part.Digits=new[]{Mathf.RoundToInt(c.r*9),Mathf.RoundToInt(c.g*9),Mathf.RoundToInt(c.b*9)};}
        }
        private string SelectedPart(int category)=>category==0?"MainFrame":category==1?"Buttons":category==2?"ButtonHolders":category==3?HandleParts[selectedHandle]:category==4?TipParts[selectedTip]:"LensHolder";
        private void ChangeColor(string key,int channel,int direction)
        {
            ColorPart part=colorParts[key];part.Digits[channel]=CameraColorMath.Step(part.Digits[channel],direction);
            ApplyColor(part);part.Saved.Value=string.Concat(part.Digits[0],part.Digits[1],part.Digits[2]);
            if(key=="MainFrame")
            {
                ColorPart top=colorParts["ColorMenuFrame"];top.Digits=(int[])part.Digits.Clone();
                ApplyColor(top);top.Saved.Value=part.Saved.Value;
            }
        }
        private void ApplyColor(ColorPart part)
        {
            appearanceRevision++;
            Color color=new Color(part.Digits[0]/9f,part.Digits[1]/9f,part.Digits[2]/9f,1);
            foreach(Material mat in part.Materials)SetMaterialColor(mat,color);
        }
        private void ResetOriginalColors()
        {
            appearanceRevision++;
            foreach(ColorPart part in colorParts.Values)
            {
                for(int i=0;i<part.Materials.Count;i++)SetMaterialColor(part.Materials[i],part.Originals[i]);
                part.Saved.Value="";Color c=part.Originals[0];part.Digits=new[]{Mathf.RoundToInt(c.r*9),Mathf.RoundToInt(c.g*9),Mathf.RoundToInt(c.b*9)};
            }
        }
        private void SetColorMenu(bool open)
        {
            if (!open) CancelButtonConfirmation();
            colorMenuOpen=open;
            foreach(GameObject obj in topObjects)if(obj)obj.SetActive(open);
            if(colorExtras)colorExtras.gameObject.SetActive(open);
            RefreshLabelVisibility();
            // Keep the current press latched: opening/closing a menu while the
            // finger is still touching must not turn a held touch into new presses.
        }
        private bool PressExtended(string name)
        {
            if(!extraActions.TryGetValue(name,out Action action))return false;
            action();return true;
        }
        private void UpdateButtonFeedback(Button button)
        {
            if(!button.Face)return;
            // Feedback never replaces authored materials or affects child symbols.
            bool flash=Time.unscaledTime<button.FlashUntil;
            bool selected=IsSelected(button.Name);
            int state=flash?2:selected?1:0;
            if(button.FeedbackState==state && button.FeedbackAppearance==appearanceRevision)return;
            button.FeedbackState=state;button.FeedbackAppearance=appearanceRevision;
            if(!flash&&!selected){button.Face.SetPropertyBlock(null);return;}
            Color c=Color.Lerp(ReadColor(button.Face.sharedMaterial),Color.white,flash?.65f:.20f);
            button.FeedbackBlock.Clear();button.FeedbackBlock.SetColor("_BaseColor",c);button.FeedbackBlock.SetColor("_Color",c);
            button.Face.SetPropertyBlock(button.FeedbackBlock);
        }

        private NumberDisplay MakeNumber(string originalName)
        {
            Transform original=FindUnique(originalName,true);Renderer renderer=original.GetComponent<Renderer>();
            // Live numeric values replace only the digit geometry; original headings,
            // signs and all physical components are untouched. Use the packed font.
            Transform axisX=original.Find("IsaiahFontRight"),axisY=original.Find("IsaiahFontUp");
            if(!axisX || !axisY)throw new InvalidOperationException("Authored font basis missing: "+originalName);
            Vector3 x=original.TransformVector(axisX.localPosition).normalized;
            Vector3 y=original.TransformVector(axisY.localPosition).normalized;
            Transform root=new GameObject("LiveNumber_"+originalName).transform;root.SetParent(controls,false);
            root.rotation=Quaternion.LookRotation(Vector3.Cross(x,y).normalized,y);
            Bounds b=OwnBoundsInFrame(original,root);
            root.position=root.TransformPoint(b.center);
            var display=new NumberDisplay{Root=root,Source=original,Size=new Vector2(b.size.x,b.size.y)};
            renderer.enabled=false;numberDisplays.Add(display);return display;
        }
        private void RefreshLabelVisibility()
        { foreach(var n in numberDisplays)if(n.Root && n.Source && n.Root.gameObject.activeSelf!=n.Source.gameObject.activeSelf)n.Root.gameObject.SetActive(n.Source.gameObject.activeSelf); }
        private void SetNumber(NumberDisplay number,string value)
        {
            if(number==null || number.Previous==value)return;
            number.Previous=value;
            // The same authored glyph vertices/materials, batched into two
            // submeshes per label instead of two draw calls per character.
            // No frame-driven text rebuilding or new font/texture is introduced.
            var white=new List<CombineInstance>();var black=new List<CombineInstance>();
            string[] lines=value.Split('\n');int columns=0;
            foreach(string line in lines)columns=Math.Max(columns,line.Length);
            float height=number.Size.y/lines.Length,advance=height*.72f,width=columns*advance;
            if(width>number.Size.x){height*=number.Size.x/width;advance=height*.72f;}
            float zeroHeight=glyphTemplates['0'].GetComponent<MeshFilter>().sharedMesh.bounds.size.y;
            for(int row=0;row<lines.Length;row++)for(int i=0;i<lines[row].Length;i++)
            {
                char character=lines[row][i];width=lines[row].Length*advance;
                if(!glyphTemplates.TryGetValue(character,out GameObject template))continue;
                Mesh mesh=template.GetComponent<MeshFilter>().sharedMesh;Bounds b=mesh.bounds;
                float ratio=height/zeroHeight;
                float baseline=(character=='.' ? -.5f*height+.5f*b.size.y*ratio : 0f)+(lines.Length*.5f-row-.5f)*height*1.15f;
                Vector3 position=new Vector3(width*.5f-advance*(i+.5f),baseline,0)-b.center*ratio;
                Matrix4x4 transform=Matrix4x4.TRS(position,Quaternion.identity,Vector3.one*ratio);
                Material[] materials=template.GetComponent<Renderer>().sharedMaterials;
                for(int slot=0;slot<mesh.subMeshCount;slot++)
                {
                    var group=materials[slot].name.StartsWith("text_black",StringComparison.Ordinal)?black:white;
                    group.Add(new CombineInstance {mesh=mesh,subMeshIndex=slot,transform=transform});
                }
            }
            var fill=new Mesh {name="OriginalFontFill"};var outline=new Mesh {name="OriginalFontOutline"};
            fill.CombineMeshes(white.ToArray(),true,true);outline.CombineMeshes(black.ToArray(),true,true);
            var combined=new Mesh {name="AuthoredFont_"+number.Source.name};
            combined.CombineMeshes(new[]{new CombineInstance{mesh=fill,transform=Matrix4x4.identity},
                new CombineInstance{mesh=outline,transform=Matrix4x4.identity}},false,false);
            Destroy(fill);Destroy(outline);
            if(number.GeneratedMesh)Destroy(number.GeneratedMesh);
            number.GeneratedMesh=combined;
            GameObject go;
            if(number.Glyphs.Count==0)
            {
                go=new GameObject("ValueGlyphBatch");go.transform.SetParent(number.Root,false);
                go.AddComponent<MeshFilter>();go.AddComponent<MeshRenderer>();number.Glyphs.Add(go);
                go.layer=number.Root.parent.gameObject.layer;modelRenderersDirty=true;
            }
            else go=number.Glyphs[0];
            go.GetComponent<MeshFilter>().sharedMesh=combined;
            go.GetComponent<Renderer>().sharedMaterials=new[]{numberWhite,numberBlack};
            if(number.Surface)
            {
                float min=OwnBoundsInFrame(go.transform,number.Root).min.z;
                float front=OwnBoundsInFrame(number.Surface,number.Root).max.z;
                number.Root.position=number.Root.TransformPoint(new Vector3(0,0,front-min+.0003f));
            }
        }
        private Material numberWhite,numberBlack;
        private void RefreshAuthoredValues()
        {
            if(colorParts.Count==0)return;
            if(!numberWhite)
            {
                // Keep the authored font's face culling and outline materials.
                // A double-sided sprite shader draws the outline's back faces
                // over the white fill and turns the digits into black blobs.
                foreach(Material source in FindUnique("GorillaTag_Name.023",true).GetComponent<Renderer>().sharedMaterials)
                {
                    var copy=new Material(source);convertedMaterials.Add(copy);
                    if(source.name.StartsWith("text_black",StringComparison.Ordinal))numberBlack=copy;
                    else numberWhite=copy;
                }
                if(!numberWhite||!numberBlack)throw new InvalidOperationException("Original outlined font materials are missing.");
            }
            SetNumber(smoothNumber,smoothness.ToString("0.00",CultureInfo.InvariantCulture));
            SetNumber(fovNumber,fov.ToString("0",CultureInfo.InvariantCulture));
            SetNumber(nearNumber,nearClip.ToString("0.00",CultureInfo.InvariantCulture));
            for(int group=0;group<6;group++)for(int ch=0;ch<3;ch++)SetNumber(colorNumbers[group,ch],colorParts[SelectedPart(group)].Digits[ch].ToString());
            handleSelector?.Set(selectedHandle==0?"HANDLE: LEFT":"HANDLE: RIGHT");
            tipSelector?.Set(new[]{"TIP: LEFT TOP","TIP: LEFT BOTTOM","TIP: RIGHT TOP","TIP: RIGHT BOTTOM"}[selectedTip]);

        }
        private void ReleaseAuthoredControls()
        {
            foreach(var number in numberDisplays)if(number.GeneratedMesh)Destroy(number.GeneratedMesh);
            hiddenModelRenderers.Clear();modelRenderersDirty=false;previewUvInitialized=false;
            colorParts.Clear();extraActions.Clear();topObjects.Clear();moreObjects.Clear();rainLabel=null;glyphTemplates.Clear();numberDisplays.Clear();
            smoothNumber=fovNumber=nearNumber=null;numberWhite=numberBlack=null;handleSelector=tipSelector=null;
            Array.Clear(colorNumbers,0,colorNumbers.Length);colorExtras=null;colorMenuOpen=false;
            timeLabel=rotationLabel=hideFaceHatLabel=null;
        }
        // Exact authored object lists; no runtime grouping by guessed names.
        private static readonly string[] AuthoredColorPanelNames = { "Main Frame.001", "GorillaTag_Name.016", "GorillaTag_Name.017", "Btn_Main_Frame_Up_1", "Btn_Main_Frame_Down_1", "GorillaTag_Name.018", "Cube.001", "GorillaTag_Name.019", "GorillaTag_Name.020", "Btn_Main_Frame_Up_2", "Btn_Main_Frame_Down_2", "GorillaTag_Name.021", "GorillaTag_Name.022", "Btn_Main_Frame_Up_3", "Btn_Main_Frame_Down_3", "GorillaTag_Name.023", "GorillaTag_Name.024", "GorillaTag_Name.025", "GorillaTag_Name.026", "GorillaTag_Name.027", "GorillaTag_Name.028", "GorillaTag_Name.029", "Btn_Buttons_Up_3", "GorillaTag_Name.031", "GorillaTag_Name.032", "GorillaTag_Name.033", "GorillaTag_Name.034", "GorillaTag_Name.035", "Cube.002", "Btn_Near_Clipping_Up.004", "Btn_Buttons_Up_2", "Btn_Buttons_Up_1", "Btn_Buttons_Down_3", "Btn_Buttons_Down_2", "Btn_Buttons_Down_1", "GorillaTag_Name.036", "GorillaTag_Name.037", "GorillaTag_Name.038", "GorillaTag_Name.039", "GorillaTag_Name.040", "GorillaTag_Name.041", "GorillaTag_Name.042", "GorillaTag_Name.043", "GorillaTag_Name.044", "GorillaTag_Name.045", "Cube.004", "Btn_Button_Holder_Up_1", "Btn_Button_Holder_Up_2", "Btn_Button_Holder_Up_3", "Btn_Button_Holder_Down_1", "Btn_Button_Holder_Down_2", "Btn_Button_Holder_Down_3", "GorillaTag_Name.046", "GorillaTag_Name.047", "GorillaTag_Name.048", "GorillaTag_Name.049", "GorillaTag_Name.050", "GorillaTag_Name.051", "GorillaTag_Name.052", "GorillaTag_Name.053", "GorillaTag_Name.054", "GorillaTag_Name.055", "Cube.005", "Btn_Handles_Up_3", "Btn_Handles_Up_2", "Btn_Handles_Up_1", "Btn_Handles_Down_3", "Btn_Handles_Down_2", "Btn_Handles_Down_!", "GorillaTag_Name.056", "GorillaTag_Name.057", "GorillaTag_Name.058", "GorillaTag_Name.059", "GorillaTag_Name.060", "GorillaTag_Name.061", "GorillaTag_Name.062", "GorillaTag_Name.063", "GorillaTag_Name.064", "GorillaTag_Name.065", "Btn_Handle_Tip/end_Up_1", "Btn_Handle_Tip/end_Up_2", "Btn_Handle_Tip/end_Up_3", "Btn_Handle_Tip/end_Down_1", "Btn_Handle_Tip/end_Down_2", "Btn_Handle_Tip/end_Down_3", "Main Frame.003", "Main Frame.004", "GorillaTag_Name.007", "Btn_Save", "GorillaTag_Name.074", "Btn_Preset_1", "GorillaTag_Name.073", "Btn_Preset_2", "GorillaTag_Name.075", "GorillaTag_Name.076", "Btn_Preset_4", "Btn_Preset_3", "GorillaTag_Name.077", "GorillaTag_Name.078", "Btn_Preset_5", "Btn_Preset_6", "Cube.007", "Cube.011", "Cube.006", "GorillaTag_Name.079", "GorillaTag_Name.080", "GorillaTag_Name.081", "GorillaTag_Name.082", "GorillaTag_Name.083", "GorillaTag_Name.084", "GorillaTag_Name.085", "GorillaTag_Name.086", "GorillaTag_Name.087", "GorillaTag_Name.088", "Cube.013", "Btn_Lens_Holder_Up_3", "Btn_Lens_Holder_Up_2", "Btn_Lens_Holder_Up_1", "Btn_Lens_Holder_down_3", "Btn_Lens_Holder_down_2", "Btn_Lens_Holder_down_1", "Main Frame.005", "Cube.014", "Cube.016", "Cube.017", "Cube.018", "GorillaTag_Name.089", "Btn_Reset", "GorillaTag_Name.090", "Btn_Handle_Changer", "GorillaTag_Name.091", "Btn_Tip_Changer" };
        private static readonly string[] AuthoredMorePanelNames = { "Main Frame.002", "Btn_Color_Changer", "GorillaTag_Name.030", "GorillaTag_Name.068", "Cube.008", "Btn_Time_Changer", "Cube.012", "GorillaTag_Name.069", "Btn_Roll_Lock", "GorillaTag_Name.070", "Btn_Lobby_Hop", "GorillaTag_Name.071", "Btn_Reset_View", "GorillaTag_Name.072", "Btn_Network", "Btn_Rain", "GorillaTag_Name.092", "Btn_Hide_Face_Hat", "gorillatag_name073" };
    }
}
