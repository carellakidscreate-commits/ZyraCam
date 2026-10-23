using System;
using System.Collections.Generic;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace IsaiahCameraMod
{
    // Versioned, bounded wire data. No screen, mode, UI, texture or preset is serialized.
    internal sealed class CameraBodyState
    {
        public uint Sequence;
        public bool Visible;
        public Vector3 Position;
        public Quaternion Rotation;
        public float Scale;
        public string Colors;
    }
    internal static class CameraNetworkWire
    {
        public const byte EventCode = 198;
        public const string Property = "com.isaiah.camera.body.v1", Magic = "IsaiahBody/1";
        public static readonly string[] ColorKeys = { "MainFrame", "LeftHandle", "RightHandle", "LeftTopTip",
            "LeftBottomTip", "RightTopTip", "RightBottomTip", "LensHolder", "LensGlass", "LensIndicator" };
        public const string CompactProperty = "com.isaiah.camera.pose.v2";
        // The original 67-byte packet remains readable by 2.5 peers. Only peers
        // advertising CompactProperty receive 37-byte movement-only packets.
        public static byte[] Encode(CameraBodyState s) => EncodePacket(s, true);
        public static byte[] EncodePose(CameraBodyState s) => EncodePacket(s, false);
        private static byte[] EncodePacket(CameraBodyState s, bool colors)
        {
            var bytes = new byte[colors ? 67 : 37];
            WriteUInt(bytes,0,s.Sequence); bytes[4] = s.Visible ? (byte)1 : (byte)0;
            for(int i=0;i<3;i++)WriteFloat(bytes,5+i*4,s.Position[i]);
            for(int i=0;i<4;i++)WriteFloat(bytes,17+i*4,s.Rotation[i]);
            WriteFloat(bytes,33,s.Scale);
            if(colors) for(int i=0;i<30;i++)bytes[37+i]=(byte)s.Colors[i];
            return bytes;
        }
        private static void WriteUInt(byte[] b,int p,uint v)
        { for(int i=0;i<4;i++)b[p+i]=(byte)(v>>(i*8)); }
        private static uint ReadUInt(byte[] b,int p)
            => (uint)b[p] | (uint)b[p+1]<<8 | (uint)b[p+2]<<16 | (uint)b[p+3]<<24;
        private static void WriteFloat(byte[] b,int p,float f) => WriteUInt(b,p,unchecked((uint)BitConverter.SingleToInt32Bits(f)));
        private static float ReadFloat(byte[] b,int p) => BitConverter.Int32BitsToSingle(unchecked((int)ReadUInt(b,p)));
        public static bool TryDecode(object content, out string token, out CameraBodyState state)
        {
            token = null; state = null;
            if (!(content is object[] a) || a.Length != 3 || !(a[0] is string magic) || magic != Magic ||
                !(a[1] is string session) || session.Length != 32 || !(a[2] is byte[] bytes) ||
                (bytes.Length != 67 && bytes.Length != 37) || bytes[4]>1) return false;
            var position=new Vector3(ReadFloat(bytes,5),ReadFloat(bytes,9),ReadFloat(bytes,13));
            var rotation=new Quaternion(ReadFloat(bytes,17),ReadFloat(bytes,21),ReadFloat(bytes,25),ReadFloat(bytes,29));
            float scale=ReadFloat(bytes,33);
            if(!Finite(scale)||scale<.75f||scale>4.5f)return false;
            for(int i=0;i<3;i++)if(!Finite(position[i])||Math.Abs(position[i])>100000)return false;
            float norm=0;for(int i=0;i<4;i++){if(!Finite(rotation[i]))return false;norm+=rotation[i]*rotation[i];}
            if(Math.Abs(norm-1)>.01f)return false;
            if(bytes.Length==67)for(int i=37;i<67;i+=3)
            {
                bool original=bytes[i]==95&&bytes[i+1]==95&&bytes[i+2]==95;
                if(!original)for(int j=0;j<3;j++)if(bytes[i+j]<48||bytes[i+j]>57)return false;
            }
            state=new CameraBodyState {Sequence=ReadUInt(bytes,0),Visible=bytes[4]!=0,
                Position=position,Rotation=rotation,Scale=scale,
                Colors=bytes.Length==67?System.Text.Encoding.ASCII.GetString(bytes,37,30):null};
            token=session;return true;
        }
        public static bool Newer(uint incoming, uint previous) => unchecked((int)(incoming - previous)) > 0;
        private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
    }

    public sealed partial class IsaiahCameraPlugin
    {
        private sealed class RemoteBody
        {
            public GameObject Root;
            public string Token, Colors;
            public CameraBodyState Target;
            public float LastSeen, BaseScale;
            public readonly List<Material> Materials = new List<Material>();
            public readonly List<string> Keys = new List<string>();
            public readonly List<Color> Originals = new List<Color>();
        }
        private readonly Dictionary<int, RemoteBody> remoteBodies = new Dictionary<int, RemoteBody>();
        private readonly List<int> networkRecipients = new List<int>(), removedActors = new List<int>();
        private LoadBalancingClient cameraNetworkClient;
        private Room cameraNetworkRoom;
        private string networkToken;
        private bool networkEnabled;
        private uint networkSequence;
        private float nextNetworkSend, nextNetworkDiscovery, lastNetworkSend, nextNetworkWarning;
        private CameraBodyState lastNetworkState;
        private readonly CameraBodyState currentNetworkState = new CameraBodyState();
        private readonly HashSet<int> previousRecipients = new HashSet<int>();
        private readonly List<int> compactRecipients = new List<int>(), legacyRecipients = new List<int>();
        private RaiseEventOptions compactOptions, legacyOptions;
        private string cachedNetworkColors;
        private int networkColorsRevision = -1;
        private float lastFullNetworkSend;
        private bool forceFullNetworkState;

        private void SetNetworkEnabled(bool enabled)
        {
            if (networkEnabled == enabled) return;
            if (!enabled) StopCameraNetwork();
            networkEnabled = enabled;
            nextNetworkDiscovery = nextNetworkSend = 0;
            networkLabel?.Set("NETWORK: " + (enabled ? "ON" : "OFF"));
        }
        private void TickCameraNetwork()
        {
            // Network failures never tear down the working local camera engine.
            try { UpdateCameraNetwork(); }
            catch (Exception ex)
            {
                if (Time.unscaledTime >= nextNetworkWarning)
                { nextNetworkWarning = Time.unscaledTime + 10; Logger.LogWarning("Camera sharing unavailable: " + ex.Message); }
            }
        }
        private void UpdateCameraNetwork()
        {
            if (!networkEnabled || !ready) return;
            Room room = PhotonNetwork.InRoom && !PhotonNetwork.OfflineMode ? PhotonNetwork.CurrentRoom : null;
            if (room != cameraNetworkRoom)
            {
                ClearRemoteBodies(); networkRecipients.Clear(); cameraNetworkRoom = room; networkToken = null;
                nextNetworkDiscovery = nextNetworkSend = 0; lastNetworkState = null;
            }
            if (room == null) return;
            if (cameraNetworkClient != PhotonNetwork.NetworkingClient)
            {
                if (cameraNetworkClient != null) cameraNetworkClient.EventReceived -= OnCameraNetworkEvent;
                cameraNetworkClient = PhotonNetwork.NetworkingClient;
                cameraNetworkClient.EventReceived += OnCameraNetworkEvent;
            }
            if (networkToken == null)
            {
                string token = Guid.NewGuid().ToString("N");
                if (!PhotonNetwork.LocalPlayer.SetCustomProperties(new Hashtable { [CameraNetworkWire.Property] = token, [CameraNetworkWire.CompactProperty] = true })) return;
                networkToken = token; networkSequence = 0;
            }
            float now = Time.unscaledTime;
            if (now >= nextNetworkDiscovery)
            {
                nextNetworkDiscovery = now + .25f;
                previousRecipients.Clear(); foreach(int id in networkRecipients)previousRecipients.Add(id); networkRecipients.Clear();
                foreach (var kv in room.Players)
                    if (Eligible(kv.Value)) networkRecipients.Add(kv.Key);
                foreach (int actor in networkRecipients) if (!previousRecipients.Contains(actor)) { nextNetworkSend = 0; forceFullNetworkState = true; }
                compactRecipients.Clear(); legacyRecipients.Clear();
                foreach(int actor in networkRecipients)
                    if(room.GetPlayer(actor).CustomProperties[CameraNetworkWire.CompactProperty] is bool compact && compact)compactRecipients.Add(actor);
                    else legacyRecipients.Add(actor);
                compactOptions = RecipientOptions(compactOptions,compactRecipients);
                legacyOptions = RecipientOptions(legacyOptions,legacyRecipients);
                removedActors.Clear();
                foreach (var kv in remoteBodies)
                    if (!networkRecipients.Contains(kv.Key) || now - kv.Value.LastSeen > 5 ||
                        !Equals(room.GetPlayer(kv.Key)?.CustomProperties[CameraNetworkWire.Property], kv.Value.Token)) removedActors.Add(kv.Key);
                foreach (int actor in removedActors) RemoveRemoteBody(actor);
            }
            if (networkRecipients.Count > 0 && now >= nextNetworkSend)
            {
                nextNetworkSend = now + 1f / 15;
                CameraBodyState state = CurrentBodyState();
                bool appearance = forceFullNetworkState || lastNetworkState == null || state.Colors != lastNetworkState.Colors ||
                    state.Visible != lastNetworkState.Visible || state.Scale != lastNetworkState.Scale;
                bool moved = lastNetworkState == null || (state.Position - lastNetworkState.Position).sqrMagnitude > .000001f ||
                    Quaternion.Angle(state.Rotation, lastNetworkState.Rotation) > .1f;
                // Heartbeat also initializes late joiners even when the camera is stationary/hidden.
                if (appearance || (state.Visible && moved) || now - lastNetworkSend >= 1)
                    if (SendCameraState(state, appearance))
                    {
                        if(lastNetworkState==null)lastNetworkState=new CameraBodyState();
                        lastNetworkState.Visible=state.Visible;lastNetworkState.Position=state.Position;
                        lastNetworkState.Rotation=state.Rotation;lastNetworkState.Scale=state.Scale;lastNetworkState.Colors=state.Colors;
                        lastNetworkSend=now;forceFullNetworkState=false;
                    }
            }
            foreach (var remote in remoteBodies.Values)
            {
                if (!remote.Root || remote.Target == null || !remote.Target.Visible) continue;
                float alpha = CameraViewMath.DampingAlpha(.065f, Time.unscaledDeltaTime);
                remote.Root.transform.SetPositionAndRotation(Vector3.Lerp(remote.Root.transform.position, remote.Target.Position, alpha),
                    Quaternion.Slerp(remote.Root.transform.rotation, remote.Target.Rotation, alpha));
            }
        }
        private bool Eligible(Player p) => p != null && !p.IsLocal && !p.IsInactive &&
            p.CustomProperties[CameraNetworkWire.Property] is string token && token.Length == 32;
        private static RaiseEventOptions RecipientOptions(RaiseEventOptions old,List<int> actors)
        {
            bool same=old!=null && old.TargetActors.Length==actors.Count;
            if(same)for(int i=0;i<actors.Count;i++)if(old.TargetActors[i]!=actors[i]){same=false;break;}
            return same?old:new RaiseEventOptions {TargetActors=actors.ToArray(),CachingOption=EventCaching.DoNotCache};
        }
        private CameraBodyState CurrentBodyState()
        {
            if(cachedNetworkColors==null || networkColorsRevision!=appearanceRevision)
            {
                var codes=new string[CameraNetworkWire.ColorKeys.Length];
                for(int i=0;i<codes.Length;i++)
                {string value=colorParts[CameraNetworkWire.ColorKeys[i]].Saved.Value;codes[i]=string.IsNullOrEmpty(value)?"___":value;}
                cachedNetworkColors=string.Concat(codes);networkColorsRevision=appearanceRevision;
            }
            currentNetworkState.Visible=modelRig && modelRig.activeInHierarchy;
            currentNetworkState.Position=modelRig.transform.position;currentNetworkState.Rotation=modelRig.transform.rotation;
            currentNetworkState.Scale=currentAssemblyFactor;currentNetworkState.Colors=cachedNetworkColors;
            return currentNetworkState;
        }
        private bool SendCameraState(CameraBodyState state, bool reliable)
        {
            if (networkToken == null || networkRecipients.Count == 0 || !PhotonNetwork.InRoom || PhotonNetwork.CurrentRoom != cameraNetworkRoom) return false;
            bool hasLegacy=legacyOptions!=null && legacyOptions.TargetActors.Length>0;
            bool hasCompact=compactOptions!=null && compactOptions.TargetActors.Length>0;
            if(!hasLegacy && !hasCompact)return false;
            state.Sequence=++networkSequence;
            bool full=reliable || Time.unscaledTime-lastFullNetworkSend>=1;
            byte[] fullBytes=null; bool sent=true;
            if(hasLegacy)
                sent &= PhotonNetwork.RaiseEvent(CameraNetworkWire.EventCode,
                    new object[]{CameraNetworkWire.Magic,networkToken,fullBytes=CameraNetworkWire.Encode(state)},legacyOptions,
                    reliable?SendOptions.SendReliable:SendOptions.SendUnreliable);
            if(hasCompact)
                sent &= PhotonNetwork.RaiseEvent(CameraNetworkWire.EventCode,
                    new object[]{CameraNetworkWire.Magic,networkToken,full?(fullBytes??CameraNetworkWire.Encode(state)):CameraNetworkWire.EncodePose(state)},compactOptions,
                    reliable?SendOptions.SendReliable:SendOptions.SendUnreliable);
            if(sent && full)lastFullNetworkSend=Time.unscaledTime;
            return sent;
        }
        private void OnCameraNetworkEvent(EventData e)
        {
            if (!networkEnabled || !ready || e.Code != CameraNetworkWire.EventCode || cameraNetworkRoom == null ||
                PhotonNetwork.CurrentRoom != cameraNetworkRoom || !Eligible(cameraNetworkRoom.GetPlayer(e.Sender))) return;
            if (!CameraNetworkWire.TryDecode(e.CustomData, out string token, out CameraBodyState state) ||
                !Equals(cameraNetworkRoom.GetPlayer(e.Sender).CustomProperties[CameraNetworkWire.Property], token)) return;
            try
            {
                if (remoteBodies.TryGetValue(e.Sender, out RemoteBody old) && old.Token != token) { RemoveRemoteBody(e.Sender); old = null; }
                if (old != null && old.Target != null && !CameraNetworkWire.Newer(state.Sequence, old.Target.Sequence)) return;
                // An unreliable compact pose can arrive before its reliable appearance.
                // Wait for a full state; never display an uninitialized body.
                if(state.Colors==null)
                {if(old==null || old.Target==null || old.Target.Colors==null)return;state.Colors=old.Target.Colors;}
                RemoteBody body = old;
                if (body == null) { body = new RemoteBody { Token = token }; remoteBodies.Add(e.Sender, body); }
                body.LastSeen = Time.unscaledTime;
                bool wasVisible = body.Target != null && body.Target.Visible;
                body.Target = state;
                if (!state.Visible) { if (body.Root) body.Root.SetActive(false); return; }
                if (!body.Root) BuildRemoteBody(body, e.Sender);
                body.Root.transform.localScale = Vector3.one * (state.Scale / body.BaseScale);
                if (!wasVisible || (body.Root.transform.position - state.Position).sqrMagnitude > 25)
                    body.Root.transform.SetPositionAndRotation(state.Position, state.Rotation);
                ApplyRemoteColors(body, state.Colors);
                if(!body.Root.activeSelf)body.Root.SetActive(true);
            }
            catch (Exception ex) { RemoveRemoteBody(e.Sender); Logger.LogWarning("Could not display remote camera: " + ex.Message); }
        }
        private void BuildRemoteBody(RemoteBody remote, int actor)
        {
            remote.Root = new GameObject("Isaiah remote body " + actor); remote.Root.SetActive(false); DontDestroyOnLoad(remote.Root);
            remote.BaseScale = currentAssemblyFactor;
            var transforms = new Dictionary<Transform, Transform> { [modelRig.transform] = remote.Root.transform };
            // An allowlist, not a full prefab clone. No child text, screen, button,
            // panel, camera, behaviour, collider, audio source or RenderTexture enters this object.
            foreach (string name in new[] { "Main Frame", "Sphere.001", "LeftHandle", "CameraLens.001", "CameraLens", "Cylinder.002" })
            {
                Transform source = FindUnique(name, true);
                Transform target = CopyTransformChain(source, transforms);
                target.gameObject.AddComponent<MeshFilter>().sharedMesh = source.GetComponent<MeshFilter>().sharedMesh;
                var renderer = target.gameObject.AddComponent<MeshRenderer>();
                Material[] materials = source.GetComponent<Renderer>().sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    Material original = materials[i]; var copy = new Material(original); materials[i] = copy;
                    string key = null; Color defaultColor = ReadColor(original);
                    foreach (string partKey in CameraNetworkWire.ColorKeys)
                    { var part = colorParts[partKey]; int index = part.Materials.IndexOf(original); if (index >= 0) { key = partKey; defaultColor = part.Originals[index]; break; } }
                    remote.Materials.Add(copy); remote.Keys.Add(key); remote.Originals.Add(defaultColor);
                    // A body material must never inherit a live render target.
                    foreach (string textureName in copy.GetTexturePropertyNames()) if (copy.GetTexture(textureName) is RenderTexture) copy.SetTexture(textureName, null);
                }
                renderer.sharedMaterials = materials; target.gameObject.layer = source.gameObject.layer;
            }
        }
        private Transform CopyTransformChain(Transform source, Dictionary<Transform, Transform> copies)
        {
            if (copies.TryGetValue(source, out Transform existing)) return existing;
            Transform parent = CopyTransformChain(source.parent, copies);
            Transform copy = new GameObject(source.name).transform; copy.SetParent(parent, false);
            copy.localPosition = source.localPosition; copy.localRotation = source.localRotation; copy.localScale = source.localScale;
            copies.Add(source, copy); return copy;
        }
        private static void ApplyRemoteColors(RemoteBody remote, string codes)
        {
            if (remote.Colors == codes) return;
            for (int i = 0; i < remote.Materials.Count; i++)
            {
                int index = Array.IndexOf(CameraNetworkWire.ColorKeys, remote.Keys[i]);
                Color c = remote.Originals[i];
                if (index >= 0 && CameraColorMath.TryDigits(codes.Substring(index * 3, 3), out int[] d)) c = new Color(d[0] / 9f, d[1] / 9f, d[2] / 9f, 1);
                SetMaterialColor(remote.Materials[i], c);
            }
            remote.Colors = codes;
        }
        private void RemoveRemoteBody(int actor)
        {
            if (!remoteBodies.TryGetValue(actor, out RemoteBody body)) return;
            if (body.Root) { body.Root.SetActive(false); Destroy(body.Root); }
            foreach (Material m in body.Materials) if (m) Destroy(m);
            remoteBodies.Remove(actor);
        }
        private void ClearRemoteBodies()
        { foreach (int actor in new List<int>(remoteBodies.Keys)) RemoveRemoteBody(actor); }
        private void StopCameraNetwork()
        {
            try
            {
                if (networkToken != null && PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom == cameraNetworkRoom)
                {
                    if (modelRig && colorParts.Count > 0) { var state = CurrentBodyState(); state.Visible = false; SendCameraState(state, true); }
                }
                // Photon retains local custom properties between rooms. Clear the
                // capability even when OFF is pressed after leaving a room.
                if (PhotonNetwork.LocalPlayer != null && PhotonNetwork.LocalPlayer.CustomProperties[CameraNetworkWire.Property] != null)
                    PhotonNetwork.LocalPlayer.SetCustomProperties(new Hashtable { [CameraNetworkWire.Property] = null, [CameraNetworkWire.CompactProperty] = null });
            }
            catch (Exception ex) { Logger.LogWarning("Camera sharing shutdown: " + ex.Message); }
            finally
            {
                if (cameraNetworkClient != null) cameraNetworkClient.EventReceived -= OnCameraNetworkEvent;
                cameraNetworkClient = null; cameraNetworkRoom = null; networkToken = null;
                networkRecipients.Clear(); compactRecipients.Clear(); legacyRecipients.Clear();
                compactOptions=legacyOptions=null;cachedNetworkColors=null;networkColorsRevision=-1;
                ClearRemoteBodies(); lastNetworkState = null;
            }
        }
    }
}
