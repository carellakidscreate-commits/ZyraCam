using System;
using System.Collections;
using System.Threading.Tasks;
using GorillaNetworking;
using UnityEngine;

namespace IsaiahCameraMod
{
    internal static class LobbyHopPolicy
    {
        public const int MaxAttempts = 3;
        public static bool IsDifferentPublicRoom(string original, string joined, bool isPrivate)
            => !isPrivate && !string.IsNullOrEmpty(joined) && !string.Equals(original, joined, StringComparison.OrdinalIgnoreCase);
        public static bool Busy(NetSystemState state) => state == NetSystemState.Connecting || state == NetSystemState.Disconnecting ||
            state == NetSystemState.Initialization || state == NetSystemState.PingRecon;
        public static bool Joined(NetSystemState state, bool inRoom) => state == NetSystemState.InGame && inRoom;
        public static bool Ready(NetSystemState state, bool groupJoining) => state == NetSystemState.Idle && !groupJoining;
    }

    public sealed partial class IsaiahCameraPlugin
    {
        private Coroutine lobbyRoutine;
        private bool lobbyBusy;
        private string lobbyStatus;

        private void StartLobbyHop()
        {
            // Room getters can throw during native teardown. A matchmaking
            // failure must never bubble into LateUpdate's camera recovery path.
            try { TryStartLobbyHop(); }
            catch (Exception ex)
            {
                lobbyBusy = false;
                Logger.LogError("Lobby request failed without changing the camera: " + ex);
                SetLobbyStatus("LOBBY HOP: NETWORK NOT READY");
            }
        }

        private void TryStartLobbyHop()
        {
            if (lobbyBusy) return;
            var network = NetworkSystem.Instance;
            var controller = PhotonNetworkController.Instance;
            var computer = GorillaComputer.instance;
            if (network == null || !controller || !computer || !controller.enabled)
            { SetLobbyStatus("LOBBY HOP: NETWORK NOT READY"); return; }
            // attemptingToConnect is a legacy field set TRUE by DisconnectCleanup.
            // The installed native join path never clears it. Do not use or reset it.
            if (LobbyHopPolicy.Busy(network.netState) || network.groupJoinInProgress)
            { SetLobbyStatus("LOBBY HOP: CONNECTION BUSY"); return; }
            // Resolve before leaving: the private trigger is not a public matchmaker.
            GorillaNetworkJoinTrigger target = null;
            bool inSession = LobbyHopPolicy.Joined(network.netState, network.InRoom);
            // Private rooms may cover several maps. Use the active local map's
            // public trigger, not a private-room code or the first mode substring.
            if (nativeCamera)
            {
                float closest = float.MaxValue;
                foreach (var pair in computer.primaryTriggersByZone)
                {
                    var candidate = pair.Value;
                    if (!candidate || candidate == controller.privateTrigger || !candidate.myCollider ||
                        !ZoneManagement.instance || !ZoneManagement.instance.IsZoneActive(candidate.zone)) continue;
                    float distance = (candidate.myCollider.transform.position - nativeCamera.transform.position).sqrMagnitude;
                    if (distance < closest) { closest = distance; target = candidate; }
                }
            }
            // NetworkSystemPUN.GameModeString dereferences CurrentRoom directly.
            // It is not valid while offline, even though the player has a rig.
            string gameMode = inSession ? network.GameModeString : null;
            if (!target && !string.IsNullOrEmpty(gameMode))
                target = computer.GetJoinTriggerFromFullGameModeString(gameMode);
            if (!target || target == controller.privateTrigger)
                target = controller.currentJoinTrigger;
            if (!target || target == controller.privateTrigger)
            { SetLobbyStatus("LOBBY HOP: ENTER A MAP FIRST"); return; }
            if (!target.myCollider || (target.isSubsOnly && !GorillaTagScripts.SubscriptionManager.IsLocalSubscribed()))
            { SetLobbyStatus("PUBLIC MAP JOIN NOT AVAILABLE"); return; }

            lobbyBusy = true;
            SetLobbyStatus("FINDING PUBLIC LOBBY...");
            lobbyRoutine = StartCoroutine(GuardLobbyHop(HopPublicLobbies(network, target)));
        }

        private IEnumerator GuardLobbyHop(IEnumerator operation)
        {
            // Catch exceptions from iterator steps without yielding inside catch.
            yield return null;
            try
            {
                while (true)
                {
                    bool next;
                    try { next = operation.MoveNext(); }
                    catch (Exception ex)
                    {
                        Logger.LogError("Lobby hop failed: " + ex);
                        SetLobbyStatus("LOBBY HOP FAILED - SEE GAME LOG");
                        break;
                    }
                    if (!next) break;
                    yield return operation.Current;
                }
            }
            finally
            {
                (operation as IDisposable)?.Dispose();
                lobbyBusy = false; lobbyRoutine = null;
                // Rebind the live HMD on the next frame and discard old follow state.
                snap = true;
            }
        }

        private IEnumerator HopPublicLobbies(NetworkSystem network, GorillaNetworkJoinTrigger trigger)
        {
            var controller = PhotonNetworkController.Instance;
            bool inSession = LobbyHopPolicy.Joined(network.netState, network.InRoom);
            string original = inSession ? network.RoomName : null;
            string originalRegion = network.CurrentRegion;
            bool wasPrivate = inSession && network.SessionIsPrivate;
            Logger.LogInfo("Lobby hop requested from " + (wasPrivate ? "private" : inSession ? "public" : "offline") +
                "; region=" + originalRegion + "; target mode=" + trigger.GetFullDesiredGameModeString());
            // Keep the game's queue, mode, allowed maps, region preferences and
            // subscription restrictions. Never edit room properties or native filters.
            for (int attempt = 1; attempt <= LobbyHopPolicy.MaxAttempts; attempt++)
            {
                if (NetworkSystem.Instance != network || !controller) { SetLobbyStatus("LOBBY HOP: NETWORK CHANGED"); yield break; }
                if (network.netState == NetSystemState.InGame)
                {
                    Task leave = network.ReturnToSinglePlayer();
                    float until = Time.realtimeSinceStartup + 45f;
                    while (!leave.IsCompleted && Time.realtimeSinceStartup < until) yield return null;
                    if (!leave.IsCompleted) { SetLobbyStatus("LEAVING ROOM TIMED OUT"); yield break; }
                    leave.GetAwaiter().GetResult();
                }
                float readyUntil = Time.realtimeSinceStartup + 30f;
                while (!LobbyHopPolicy.Ready(network.netState, network.groupJoinInProgress) && Time.realtimeSinceStartup < readyUntil) yield return null;
                if (!LobbyHopPolicy.Ready(network.netState, network.groupJoinInProgress))
                { SetLobbyStatus("LOBBY HOP: COULD NOT LEAVE"); yield break; }
                if (!trigger) { SetLobbyStatus("LOBBY HOP: MAP UNAVAILABLE"); yield break; }
                SetLobbyStatus("CONNECTING TO LOBBY " + attempt + "/3");

                // Same supported entry point as touching the map's public join
                // trigger. This honors DisableTriggerJoins, online permissions,
                // fan-club eligibility and the native public-room search filters.
                trigger.SubsPublicJoin();
                float joinUntil = Time.realtimeSinceStartup + 45f;
                bool started = false;
                float requestedAt = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup < joinUntil)
                {
                    // InRoom can become true before the native async join has
                    // committed its final InGame state. Do not read room identity
                    // or start another disconnect while that join is still busy.
                    if (LobbyHopPolicy.Joined(network.netState, network.InRoom)) break;
                    started |= LobbyHopPolicy.Busy(network.netState);
                    if (started && LobbyHopPolicy.Ready(network.netState, network.groupJoinInProgress) && Time.realtimeSinceStartup-requestedAt > 3f) break;
                    yield return null;
                }
                if (!LobbyHopPolicy.Joined(network.netState, network.InRoom))
                {
                    var computer = GorillaComputer.instance;
                    SetLobbyStatus(Time.realtimeSinceStartup>=joinUntil ? "TIMED OUT" :
                        computer && computer.roomFull ? "FAILED: LOBBY FULL" :
                        computer && computer.roomNotAllowed ? "FAILED: JOIN NOT ALLOWED" : "FAILED TO CONNECT");
                    yield break;
                }
                if (LobbyHopPolicy.IsDifferentPublicRoom(original, network.RoomName, network.SessionIsPrivate))
                {
                    SetLobbyStatus("CONNECTED");
                    Logger.LogInfo("Lobby hop completed; different public room, region=" + network.CurrentRegion);
                    yield break;
                }
                if (network.SessionIsPrivate)
                { SetLobbyStatus("PUBLIC JOIN INTERRUPTED BY PRIVATE JOIN"); yield break; }
                if (attempt == LobbyHopPolicy.MaxAttempts)
                {
                    // Native APIs provide no excluded-room parameter. Do not
                    // claim a different lobby or bypass normal filters to force one.
                    SetLobbyStatus("NO DIFFERENT PUBLIC LOBBY FOUND");
                    Logger.LogWarning("Native public matchmaking returned the previous room three times. Remaining in the last valid public room.");
                    yield break;
                }
                SetLobbyStatus("SAME LOBBY - RETRYING...");
                yield return new WaitForSecondsRealtime(1f);
            }
        }

        private void SetLobbyStatus(string message)
        {
            lobbyStatus = message;
            extraReadout?.Set(message);
            Logger.LogInfo(message);
        }
        private void CancelLobbyHop()
        {
            if (lobbyRoutine != null) StopCoroutine(lobbyRoutine);
            lobbyRoutine = null; lobbyBusy = false;
            // Native matchmaking already in progress belongs to the game. Never
            // start another request or forcibly disconnect it during plugin unload.
        }
    }
}
