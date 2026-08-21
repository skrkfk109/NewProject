using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Networking.Transport;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LobbyRelaySample.ngo
{
    /// <summary>
    /// Once the local localPlayer is in a localLobby and that localLobby has entered the In-Game state, this will load in whatever is necessary to actually run the game part.
    /// This will exist in the game scene so that it can hold references to scene objects that spawned prefab instances will need.
    /// </summary>
    public class SetupInGame : MonoBehaviour
    {
        [SerializeField]
        GameObject m_IngameRunnerPrefab = default;
        [SerializeField]
        private GameObject[] m_disableWhileInGame = default;

        private InGameRunner m_inGameRunner;

        // Color Clash does not use the imported sample's glyph minigame runner.
        // The NetworkManager itself is the authoritative point that moves every
        // connected peer into the battle scene once the negotiated player count
        // has arrived.
        private int m_expectedPlayerCount;
        private bool m_battleSceneLoading;

        private bool m_doesNeedCleanup = false;
        private bool m_hasConnectedViaNGO = false;

        private LocalLobby m_lobby;

        private void SetMenuVisibility(bool areVisible)
        {
            foreach (GameObject go in m_disableWhileInGame)
                go.SetActive(areVisible);
        }

        /// <summary>
        /// The prefab with the NetworkManager contains all of the assets and logic needed to set up the NGO minigame.
        /// The UnityTransport needs to also be set up with a new Allocation from Relay.
        /// </summary>
        async Task CreateNetworkManager(LocalLobby localLobby, LocalPlayer localPlayer)
        {
            m_lobby = localLobby;
            m_expectedPlayerCount = m_lobby.PlayerCount;
            m_battleSceneLoading = false;

            // Do not instantiate InGameLogic here. It belongs to the downloaded
            // sample minigame and is the source of its Waiting for all players UI.
            // Relay + NGO are enough for Color Clash's own battle scene.
            NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
            NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
            if (localPlayer.IsHost.Value)
            {
                await SetRelayHostData();
                NetworkManager.Singleton.StartHost();
            }
            else
            {
                await AwaitRelayCode(localLobby);
                await SetRelayClientData();
                NetworkManager.Singleton.StartClient();
            }
        }

        private void OnClientConnected(ulong clientId)
        {
            if (!NetworkManager.Singleton.IsHost || m_battleSceneLoading)
                return;

            if (NetworkManager.Singleton.ConnectedClients.Count < m_expectedPlayerCount)
                return;

            m_battleSceneLoading = true;
            NetworkManager.Singleton.SceneManager.LoadScene("battle", UnityEngine.SceneManagement.LoadSceneMode.Single);
        }

        async Task AwaitRelayCode(LocalLobby lobby)
        {
            string relayCode = lobby.RelayCode.Value;
            lobby.RelayCode.onChanged += (code) => relayCode = code;
            while (string.IsNullOrEmpty(relayCode))
            {
                // Task.Delay polling can stall in a Web build. Advance with the
                // Unity player loop so browser clients receive the Relay code.
                await Awaitable.NextFrameAsync();
            }
        }

        async Task SetRelayHostData()
        {
            UnityTransport transport = NetworkManager.Singleton.GetComponentInChildren<UnityTransport>();

            // Relay's maxConnections means joining clients, not total players.
            // A four-player Color Clash room therefore reserves three slots here.
            var allocation = await RelayService.Instance.CreateAllocationAsync(m_lobby.MaxPlayerCount.Value - 1);
            var joincode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);
            GameManager.Instance.HostSetRelayCode(joincode);

            transport.UseWebSockets = false;
            // Unity 6 / Transport 2.x no longer exposes the Relay SDK extension
            // ToRelayServerData(). Construct the transport payload directly.
            transport.SetRelayServerData(new Unity.Networking.Transport.Relay.RelayServerData(allocation, "dtls"));
        }

        async Task SetRelayClientData()
        {
            UnityTransport transport = NetworkManager.Singleton.GetComponentInChildren<UnityTransport>();

            var joinAllocation = await RelayService.Instance.JoinAllocationAsync(m_lobby.RelayCode.Value);
#if UNITY_WEBGL && !UNITY_EDITOR
            // Web players are client-only and use Relay over secure WebSockets.
            transport.UseWebSockets = true;
            transport.SetRelayServerData(new Unity.Networking.Transport.Relay.RelayServerData(joinAllocation, "wss"));
#else
            transport.UseWebSockets = false;
            transport.SetRelayServerData(new Unity.Networking.Transport.Relay.RelayServerData(joinAllocation, "dtls"));
#endif
        }

        /// <summary>
        /// Determine the server endpoint for connecting to the Relay server, for either an Allocation or a JoinAllocation.
        /// If DTLS encryption is available, and there's a secure server endpoint available, use that as a secure connection. Otherwise, just connect to the Relay IP unsecured.
        /// </summary>
        NetworkEndpoint GetEndpointForAllocation(
            List<RelayServerEndpoint> endpoints,
            string ip,
            int port,
            out bool isSecure)
        {
#if ENABLE_MANAGED_UNITYTLS
            foreach (RelayServerEndpoint endpoint in endpoints)
            {
                if (endpoint.Secure && endpoint.Network == RelayServerEndpoint.NetworkOptions.Udp)
                {
                    isSecure = true;
                    return NetworkEndpoint.Parse(endpoint.Host, (ushort)endpoint.Port);
                }
            }
#endif
            isSecure = false;
            return NetworkEndpoint.Parse(ip, (ushort)port);
        }

        string AddressFromEndpoint(NetworkEndpoint endpoint)
        {
            return endpoint.Address.Split(':')[0];
        }

        void OnConnectionVerified()
        {
            m_hasConnectedViaNGO = true;
        }

        public void StartNetworkedGame(LocalLobby localLobby, LocalPlayer localPlayer)
        {
            m_doesNeedCleanup = true;
            SetMenuVisibility(false);
#pragma warning disable 4014
            CreateNetworkManager(localLobby, localPlayer);
#pragma warning restore 4014
        }

        public void OnGameBegin()
        {
            if (!m_hasConnectedViaNGO)
            {
                // If this localPlayer hasn't successfully connected via NGO, forcibly exit the minigame.
                LogHandlerSettings.Instance.SpawnErrorPopup("Failed to join the game.");
                OnGameEnd();
            }
        }

        /// <summary>
        /// Return to the localLobby after the game, whether due to the game ending or due to a failed connection.
        /// </summary>
        public void OnGameEnd()
        {
            if (m_doesNeedCleanup)
            {
                if (NetworkManager.Singleton != null)
                    NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;

                NetworkManager.Singleton.Shutdown(true);
                if (m_inGameRunner != null)
                    Destroy(m_inGameRunner
                        .transform.parent
                        .gameObject); // Kept for compatibility with an old sample-runner session.
                SetMenuVisibility(true);
                m_lobby.RelayCode.Value = "";
                GameManager.Instance.EndGame();
                m_doesNeedCleanup = false;
            }
        }
    }
}
