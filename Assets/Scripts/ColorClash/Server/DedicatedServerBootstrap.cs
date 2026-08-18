using System;
using System.Collections;
using ColorClash.Core;
using Unity.Collections;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;

namespace ColorClash.Server
{
    /// <summary>
    /// Runtime entry point for the Linux Dedicated Server build.
    /// It is intentionally absent from Web and desktop-player startup. The server
    /// creates the Relay allocation, owns AuthoritativeMatch, and never renders a
    /// battle scene.
    /// </summary>
    public sealed class DedicatedServerBootstrap : MonoBehaviour
    {
        const string ReadyMessage = "ColorClash/Ready";
        const string MoveMessage = "ColorClash/Move";
        const string PaintMessage = "ColorClash/Paint";
        const string SnapshotMessage = "ColorClash/Snapshot";
        const string PlayerStateMessage = "ColorClash/PlayerState";
        const string PaintAppliedMessage = "ColorClash/PaintApplied";

        const int MapWidth = 384;
        const int MapHeight = 384;
        const int PaletteCount = 5;
        const float MapBoardWidth = 180f;
        const float MapBoardDepth = 108f;

        NetworkManager networkManager;
        AuthoritativeMatch match;
        float nextSnapshotAt;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void StartDedicatedServer()
        {
#if UNITY_SERVER
            if (FindFirstObjectByType<DedicatedServerBootstrap>() == null)
                new GameObject("Color Clash Dedicated Server").AddComponent<DedicatedServerBootstrap>();
#endif
        }

        IEnumerator Start()
        {
#if UNITY_SERVER
            yield return StartCoroutine(BootServer());
#else
            yield break;
#endif
        }

        IEnumerator BootServer()
        {
            networkManager = FindFirstObjectByType<NetworkManager>();
            if (networkManager == null)
            {
                var serverObject = new GameObject("Color Clash Network Manager");
                DontDestroyOnLoad(serverObject);
                var transport = serverObject.AddComponent<UnityTransport>();
                networkManager = serverObject.AddComponent<NetworkManager>();
                networkManager.NetworkConfig.NetworkTransport = transport;
            }

            var settings = MatchSettings.Default;
            match = new AuthoritativeMatch(
                settings,
                MapWidth,
                MapHeight,
                MapBoardWidth,
                MapBoardDepth,
                PaletteCount,
                CreatePrototypeTargetMap());

            Exception startupError = null;
            bool complete = false;
            StartCoroutine(InitializeRelayRoutine(
                () => complete = true,
                exception => { startupError = exception; complete = true; }));
            while (!complete) yield return null;

            if (startupError != null)
            {
                Debug.LogError($"[Color Clash Server] Relay startup failed: {startupError.Message}");
                yield break;
            }

            RegisterNetworkCallbacks();
            Debug.Log("[Color Clash Server] Dedicated server is ready for players.");
        }

        IEnumerator InitializeRelayRoutine(Action onSuccess, Action<Exception> onFailure)
        {
            var initialize = UnityServices.InitializeAsync();
            while (!initialize.IsCompleted) yield return null;
            if (initialize.IsFaulted) { onFailure(initialize.Exception); yield break; }

            if (!AuthenticationService.Instance.IsSignedIn)
            {
                var signIn = AuthenticationService.Instance.SignInAnonymouslyAsync();
                while (!signIn.IsCompleted) yield return null;
                if (signIn.IsFaulted) { onFailure(signIn.Exception); yield break; }
            }

            // Relay maxConnections excludes the dedicated server itself.
            var allocationTask = RelayService.Instance.CreateAllocationAsync(MatchSettings.Default.maxPlayers - 1);
            while (!allocationTask.IsCompleted) yield return null;
            if (allocationTask.IsFaulted) { onFailure(allocationTask.Exception); yield break; }

            var allocation = allocationTask.Result;
            var joinCodeTask = RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);
            while (!joinCodeTask.IsCompleted) yield return null;
            if (joinCodeTask.IsFaulted) { onFailure(joinCodeTask.Exception); yield break; }

            var transport = networkManager.GetComponentInChildren<UnityTransport>();
            if (transport == null)
            {
                onFailure(new InvalidOperationException("UnityTransport is missing from the NetworkManager."));
                yield break;
            }
            RelayServerEndpoint endpoint = allocation.ServerEndpoints.Find(candidate =>
                string.Equals(candidate.ConnectionType, "dtls", StringComparison.OrdinalIgnoreCase));
            if (endpoint == null)
            {
                onFailure(new InvalidOperationException("Relay allocation did not return a DTLS endpoint."));
                yield break;
            }
            transport.UseWebSockets = false;
            transport.SetRelayServerData(new Unity.Networking.Transport.Relay.RelayServerData(
                endpoint.Host,
                (ushort)endpoint.Port,
                allocation.AllocationIdBytes,
                allocation.ConnectionData,
                allocation.ConnectionData,
                allocation.Key,
                endpoint.Secure,
                false));
            if (!networkManager.StartServer())
            {
                onFailure(new InvalidOperationException("NetworkManager.StartServer returned false."));
                yield break;
            }

            Debug.Log($"[Color Clash Server] RELAY_JOIN_CODE={joinCodeTask.Result}");
            onSuccess();
        }

        void RegisterNetworkCallbacks()
        {
            networkManager.OnClientConnectedCallback += OnClientConnected;
            networkManager.OnClientDisconnectCallback += OnClientDisconnected;
            var messages = networkManager.CustomMessagingManager;
            messages.RegisterNamedMessageHandler(ReadyMessage, ReceiveReady);
            messages.RegisterNamedMessageHandler(MoveMessage, ReceiveMove);
            messages.RegisterNamedMessageHandler(PaintMessage, ReceivePaint);
        }

        void Update()
        {
#if UNITY_SERVER
            if (match == null || networkManager == null || !networkManager.IsListening) return;
            match.Tick(Time.unscaledDeltaTime);
            if (Time.unscaledTime >= nextSnapshotAt)
            {
                nextSnapshotAt = Time.unscaledTime + .1f;
                BroadcastSnapshot();
            }
#endif
        }

        void OnClientConnected(ulong clientId)
        {
            if (clientId == NetworkManager.ServerClientId) return;
            if (!match.TryJoin(clientId))
            {
                networkManager.DisconnectClient(clientId);
                return;
            }

            if (match.TryGetPlayerState(clientId, out var state)) BroadcastPlayerState(state);
            BroadcastSnapshot();
        }

        void OnClientDisconnected(ulong clientId)
        {
            match?.RemovePlayer(clientId);
            BroadcastSnapshot();
        }

        void ReceiveReady(ulong senderClientId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out bool ready);
            match.SetReady(senderClientId, ready);
            BroadcastSnapshot();
        }

        void ReceiveMove(ulong senderClientId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out Vector3 position);
            reader.ReadValueSafe(out float clientTime);
            var command = new PlayerMoveCommand { playerId = senderClientId, position = position, clientTime = clientTime };
            if (match.TryApplyMove(command) && match.TryGetPlayerState(senderClientId, out var state))
                BroadcastPlayerState(state);
        }

        void ReceivePaint(ulong senderClientId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out Vector2 mapUv);
            reader.ReadValueSafe(out int paletteIndex);
            reader.ReadValueSafe(out float clientTime);
            var command = new PaintCommand { playerId = senderClientId, mapUv = mapUv, paletteIndex = paletteIndex, clientTime = clientTime };
            if (!match.TryApplyPaint(command).accepted) return;

            using var writer = new FastBufferWriter(sizeof(ulong) + sizeof(float) * 2 + sizeof(int) + sizeof(byte), Allocator.Temp);
            writer.WriteValueSafe(senderClientId);
            writer.WriteValueSafe(mapUv);
            writer.WriteValueSafe(paletteIndex);
            writer.WriteValueSafe((byte)match.TeamFor(senderClientId));
            SendToAll(PaintAppliedMessage, writer);
        }

        void BroadcastSnapshot()
        {
            if (networkManager == null || !networkManager.IsListening || match == null) return;
            MatchSnapshot snapshot = match.Snapshot();
            using var writer = new FastBufferWriter(sizeof(byte) + sizeof(float) * 3 + sizeof(bool), Allocator.Temp);
            writer.WriteValueSafe((byte)snapshot.phase);
            writer.WriteValueSafe(snapshot.remainingSeconds);
            writer.WriteValueSafe(snapshot.blueScore);
            writer.WriteValueSafe(snapshot.redScore);
            writer.WriteValueSafe(snapshot.barrierDown);
            SendToAll(SnapshotMessage, writer);
        }

        void BroadcastPlayerState(AuthoritativeMatch.PlayerState state)
        {
            using var writer = new FastBufferWriter(sizeof(ulong) + sizeof(byte) + sizeof(float) * 3, Allocator.Temp);
            writer.WriteValueSafe(state.playerId);
            writer.WriteValueSafe((byte)state.team);
            writer.WriteValueSafe(state.position);
            SendToAll(PlayerStateMessage, writer);
        }

        void SendToAll(string messageName, FastBufferWriter writer)
        {
            foreach (ulong clientId in networkManager.ConnectedClientsIds)
                if (clientId != NetworkManager.ServerClientId)
                    networkManager.CustomMessagingManager.SendNamedMessage(messageName, clientId, writer);
        }

        // Temporary deterministic map for headless networking tests. In the map
        // upload stage this is replaced by the uploaded image's palette-index map.
        static byte[] CreatePrototypeTargetMap()
        {
            var target = new byte[MapWidth * MapHeight];
            for (int z = 0; z < MapHeight; z++)
            for (int x = 0; x < MapWidth; x++)
            {
                float u = (x - MapWidth * .5f) / MapWidth;
                float v = (z - MapHeight * .5f) / MapHeight;
                target[z * MapWidth + x] = (byte)(Mathf.FloorToInt(
                    (Mathf.Atan2(v, u) + Mathf.PI) / (Mathf.PI * 2f) * PaletteCount +
                    (u * u + v * v) * 4f) % PaletteCount);
            }
            return target;
        }

        void OnDestroy()
        {
            if (networkManager == null) return;
            networkManager.OnClientConnectedCallback -= OnClientConnected;
            networkManager.OnClientDisconnectCallback -= OnClientDisconnected;
            if (networkManager.CustomMessagingManager == null) return;
            networkManager.CustomMessagingManager.UnregisterNamedMessageHandler(ReadyMessage);
            networkManager.CustomMessagingManager.UnregisterNamedMessageHandler(MoveMessage);
            networkManager.CustomMessagingManager.UnregisterNamedMessageHandler(PaintMessage);
        }
    }
}
