using System;
using System.Threading.Tasks;
using ColorClash.Core;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ColorClash.Networking
{
    /// <summary>
    /// Connects a Web/Desktop client to the Linux dedicated server's Relay
    /// allocation. This deliberately starts a client only: no browser player can
    /// become the match host.
    /// </summary>
    public sealed class DedicatedRelayClient : MonoBehaviour
    {
        static DedicatedRelayClient instance;

        NetworkManager networkManager;
        UnityTransport transport;
        bool connecting;
        bool battleLoadRequested;

        public static void Connect(string relayJoinCode)
        {
            if (string.IsNullOrWhiteSpace(relayJoinCode))
            {
                Debug.LogError("[Color Clash] Dedicated server did not provide a Relay join code.");
                return;
            }

            ColorClashSession.SetRelayJoinCode(relayJoinCode);
            if (instance == null)
            {
                var go = new GameObject("Color Clash Dedicated Relay Client");
                instance = go.AddComponent<DedicatedRelayClient>();
            }
            instance.BeginConnect();
        }

        void Awake()
        {
            if (instance != null && instance != this)
            {
                Destroy(gameObject);
                return;
            }
            instance = this;
            DontDestroyOnLoad(gameObject);
        }

        async void BeginConnect()
        {
            if (connecting) return;
            connecting = true;
            try
            {
                await EnsureSignedIn();
                EnsureNetworkManager();

                if (networkManager.IsListening)
                {
                    if (networkManager.IsClient) RequestBattleLoad();
                    return;
                }

                JoinAllocation allocation = await RelayService.Instance.JoinAllocationAsync(ColorClashSession.RelayJoinCode);
                transport.UseWebSockets = true;
                transport.SetRelayServerData(new Unity.Networking.Transport.Relay.RelayServerData(allocation, "wss"));
                networkManager.OnClientConnectedCallback -= OnClientConnected;
                networkManager.OnClientConnectedCallback += OnClientConnected;
                networkManager.OnClientDisconnectCallback -= OnClientDisconnected;
                networkManager.OnClientDisconnectCallback += OnClientDisconnected;
                if (!networkManager.StartClient())
                    throw new InvalidOperationException("전용 서버 클라이언트를 시작하지 못했습니다.");
            }
            catch (Exception exception)
            {
                Debug.LogError("[Color Clash] Dedicated server connection failed: " + exception.GetBaseException().Message);
            }
            finally
            {
                connecting = false;
            }
        }

        void EnsureNetworkManager()
        {
            networkManager = NetworkManager.Singleton;
            if (networkManager == null)
            {
                var go = new GameObject("Color Clash Network Manager");
                transport = go.AddComponent<UnityTransport>();
                networkManager = go.AddComponent<NetworkManager>();
                networkManager.NetworkConfig ??= new NetworkConfig();
                networkManager.NetworkConfig.NetworkTransport = transport;
                DontDestroyOnLoad(go);
            }
            else
            {
                transport = networkManager.GetComponent<UnityTransport>();
                if (transport == null) transport = networkManager.gameObject.AddComponent<UnityTransport>();
                networkManager.NetworkConfig ??= new NetworkConfig();
                networkManager.NetworkConfig.NetworkTransport = transport;
            }
        }

        static async Task EnsureSignedIn()
        {
            if (UnityServices.State != ServicesInitializationState.Initialized)
                await UnityServices.InitializeAsync();
            if (!AuthenticationService.Instance.IsSignedIn)
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
        }

        void OnClientConnected(ulong clientId)
        {
            if (networkManager == null || clientId != networkManager.LocalClientId) return;
            RequestBattleLoad();
        }

        void OnClientDisconnected(ulong clientId)
        {
            if (networkManager != null && clientId == networkManager.LocalClientId)
                Debug.LogWarning("[Color Clash] Dedicated server connection was closed.");
        }

        void RequestBattleLoad()
        {
            if (battleLoadRequested) return;
            battleLoadRequested = true;
            if (SceneManager.GetActiveScene().name != "battle")
                SceneManager.LoadScene("battle", LoadSceneMode.Single);
        }
    }
}
