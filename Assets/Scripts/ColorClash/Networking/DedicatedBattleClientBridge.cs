using System.Collections;
using ColorClash.Core;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace ColorClash.Networking
{
    /// <summary>Client-side adapter for the authoritative Linux match messages.</summary>
    public sealed class DedicatedBattleClientBridge : MonoBehaviour
    {
        const string ReadyMessage = "ColorClash/Ready";
        const string MoveMessage = "ColorClash/Move";
        const string PaintMessage = "ColorClash/Paint";
        const string SnapshotMessage = "ColorClash/Snapshot";
        const string PlayerStateMessage = "ColorClash/PlayerState";
        const string PaintAppliedMessage = "ColorClash/PaintApplied";

        NetworkManager manager;
        BattlePrototypeController battle;
        bool handlersRegistered;
        bool teamAssigned;
        bool ready;
        int localTeam;
        float nextMoveAt;
        MatchPhase phase = MatchPhase.WaitingForPlayers;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AttachWhenOnlineBattleLoads()
        {
            if (!Application.isPlaying || !ColorClashSession.IsOnlineMatch) return;
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "battle") return;
            if (FindFirstObjectByType<DedicatedBattleClientBridge>() == null)
                new GameObject("Color Clash Dedicated Battle Client").AddComponent<DedicatedBattleClientBridge>();
        }

        IEnumerator Start()
        {
            manager = NetworkManager.Singleton;
            while (manager == null || !manager.IsClient || !manager.IsListening)
            {
                manager = NetworkManager.Singleton;
                yield return null;
            }

            while (battle == null || !battle.IsBattleReady)
            {
                battle = FindFirstObjectByType<BattlePrototypeController>();
                yield return null;
            }

            var messages = manager.CustomMessagingManager;
            messages.RegisterNamedMessageHandler(SnapshotMessage, ReceiveSnapshot);
            messages.RegisterNamedMessageHandler(PlayerStateMessage, ReceivePlayerState);
            messages.RegisterNamedMessageHandler(PaintAppliedMessage, ReceivePaintApplied);
            handlersRegistered = true;
            battle.OnMultiplayerPaintRequested += SendPaint;
        }

        void Update()
        {
            if (!handlersRegistered || !teamAssigned || !ready || phase != MatchPhase.Playing) return;
            if (Time.unscaledTime < nextMoveAt) return;
            nextMoveAt = Time.unscaledTime + .05f;
            using var writer = new FastBufferWriter(sizeof(float) * 4, Allocator.Temp);
            writer.WriteValueSafe(battle.LocalPlayerPosition);
            writer.WriteValueSafe(Time.unscaledTime);
            manager.CustomMessagingManager.SendNamedMessage(MoveMessage, NetworkManager.ServerClientId, writer);
        }

        void ReceiveSnapshot(ulong senderClientId, FastBufferReader reader)
        {
            if (senderClientId != NetworkManager.ServerClientId) return;
            reader.ReadValueSafe(out byte phaseByte);
            reader.ReadValueSafe(out float remaining);
            reader.ReadValueSafe(out float ignoredBlueScore);
            reader.ReadValueSafe(out float ignoredRedScore);
            reader.ReadValueSafe(out bool wallDown);
            phase = (MatchPhase)phaseByte;
            battle?.ApplyNetworkClock(remaining, wallDown, phase == MatchPhase.Finished);
        }

        void ReceivePlayerState(ulong senderClientId, FastBufferReader reader)
        {
            if (senderClientId != NetworkManager.ServerClientId) return;
            reader.ReadValueSafe(out ulong clientId);
            reader.ReadValueSafe(out byte teamByte);
            reader.ReadValueSafe(out Vector3 position);
            int team = teamByte;
            if (clientId == manager.LocalClientId)
            {
                localTeam = team;
                teamAssigned = true;
                battle?.EnableMultiplayer(localTeam, false);
            }
            else
            {
                battle?.ApplyRemotePlayerState(clientId, position, team);
            }
        }

        void ReceivePaintApplied(ulong senderClientId, FastBufferReader reader)
        {
            if (senderClientId != NetworkManager.ServerClientId) return;
            reader.ReadValueSafe(out ulong ignoredClientId);
            reader.ReadValueSafe(out Vector2 uv);
            reader.ReadValueSafe(out int paletteIndex);
            reader.ReadValueSafe(out byte ownerTeam);
            battle?.ApplyNetworkPaint(uv, paletteIndex, ownerTeam);
        }

        void SendPaint(Vector2 uv, int paletteIndex, int ignoredOwner)
        {
            if (!handlersRegistered || !ready || phase != MatchPhase.Playing) return;
            using var writer = new FastBufferWriter(sizeof(float) * 3 + sizeof(int), Allocator.Temp);
            writer.WriteValueSafe(uv);
            writer.WriteValueSafe(paletteIndex);
            writer.WriteValueSafe(Time.unscaledTime);
            manager.CustomMessagingManager.SendNamedMessage(PaintMessage, NetworkManager.ServerClientId, writer);
        }

        void SendReady()
        {
            if (!teamAssigned || ready || manager == null) return;
            using var writer = new FastBufferWriter(sizeof(bool), Allocator.Temp);
            writer.WriteValueSafe(true);
            manager.CustomMessagingManager.SendNamedMessage(ReadyMessage, NetworkManager.ServerClientId, writer);
            ready = true;
        }

        void OnGUI()
        {
            if (!Application.isPlaying) return;
            GUI.Box(new Rect(Screen.width * .5f - 170f, 30f, 340f, 86f), GUIContent.none);
            string label = !teamAssigned ? "팀 배정 정보를 받는 중…" : ready ? "다른 플레이어의 준비를 기다리는 중…" : "준비 버튼을 눌러주세요";
            GUI.Label(new Rect(Screen.width * .5f - 145f, 42f, 290f, 24f), label);
            if (teamAssigned && !ready && GUI.Button(new Rect(Screen.width * .5f - 90f, 72f, 180f, 28f), "준비")) SendReady();
        }

        void OnDestroy()
        {
            if (battle != null) battle.OnMultiplayerPaintRequested -= SendPaint;
            if (!handlersRegistered || manager == null || manager.CustomMessagingManager == null) return;
            manager.CustomMessagingManager.UnregisterNamedMessageHandler(SnapshotMessage);
            manager.CustomMessagingManager.UnregisterNamedMessageHandler(PlayerStateMessage);
            manager.CustomMessagingManager.UnregisterNamedMessageHandler(PaintAppliedMessage);
        }
    }
}
