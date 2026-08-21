using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using LobbyRelaySample.lobby;
using LobbyRelaySample.ngo;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using Unity.Services.Samples;
using UnityEngine;
using UnityEngine.SceneManagement;
#if UNITY_EDITOR
using ParrelSync;

#endif

namespace LobbyRelaySample
{
    /// <summary>
    /// Current state of the local game.
    /// Set as a flag to allow for the Inspector to select multiple valid states for various UI features.
    /// </summary>
    [Flags]
    public enum GameState
    {
        Menu = 1,
        Lobby = 2,
        JoinMenu = 4,
    }

    /// <summary>
    /// Sets up and runs the entire sample.
    /// All the Data that is important gets updated in here, the GameManager in the mainScene has all the references
    /// needed to run the game.
    /// </summary>
    public class GameManager : MonoBehaviour
    {
        public LocalLobby LocalLobby => m_LocalLobby;
        public Action<GameState> onGameStateChanged;
        public LocalLobbyList LobbyList { get; private set; } = new LocalLobbyList();

        public GameState LocalGameState { get; private set; }
        public LobbyManager LobbyManager { get; private set; }
        [SerializeField]
        SetupInGame m_setupInGame;
        [SerializeField]
        Countdown m_countdown;
        [Header("Prototype Debug")]
        [Tooltip("F8: Skip the online lobby and launch an offline battle with the built-in AI rival. This is for gameplay testing only.")]
        [SerializeField]
        bool m_enableOfflineBotShortcut = true;

        LocalPlayer m_LocalUser;
        LocalLobby m_LocalLobby;
        Coroutine m_webGlLobbyPolling;
        Task<bool> m_serviceInitializationTask;
        bool m_isQueryingLobbies;

        LobbyColor m_lobbyColorFilter;

        static GameManager m_GameManagerInstance;

        public static GameManager Instance
        {
            get
            {
                if (m_GameManagerInstance != null)
                    return m_GameManagerInstance;
                m_GameManagerInstance = FindObjectOfType<GameManager>();
                return m_GameManagerInstance;
            }
        }

        /// <summary>Rather than a setter, this is usable in-editor. It won't accept an enum, however.</summary>
        public void SetLobbyColorFilter(int color)
        {
            m_lobbyColorFilter = (LobbyColor)color;
        }

        public async Task<LocalPlayer> AwaitLocalUserInitialization()
        {
            while (m_LocalUser == null)
                await Task.Delay(100);
            return m_LocalUser;
        }

        // Color Clash rooms reserve four seats. A match begins only at 2 (1v1)
        // or 4 (2v2), never with an unbalanced odd player count.
        public async void CreateLobby(string name, bool isPrivate, string password = null, int maxPlayers = 4)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            // Unity Transport supports Web builds as Relay clients only. A native
            // Mac/Windows host creates the room; browser players join it.
            LogHandlerSettings.Instance.SpawnErrorPopup("Web build is join-only. Create this room from the native host build.");
            return;
#endif
            try
            {
                var lobby = await LobbyManager.CreateLobbyAsync(
                    name,
                    maxPlayers,
                    isPrivate,
                    m_LocalUser,
                    password);

                LobbyConverters.RemoteToLocal(lobby, m_LocalLobby);
                await CreateLobby();
            }
            catch (LobbyServiceException exception)
            {
                Debug.LogError($"[Color Clash] Lobby creation failed. Code: {exception.ErrorCode}, Message: {exception.Message}");
                LogHandlerSettings.Instance.SpawnErrorPopup($"Error creating lobby : ({exception.ErrorCode}) {exception.Message}");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                LogHandlerSettings.Instance.SpawnErrorPopup("Error creating lobby. Check the Console for details.");
            }
        }

        public async void JoinLobby(string lobbyID, string lobbyCode, string password = null)
        {
            try
            {
                var lobby = await LobbyManager.JoinLobbyAsync(lobbyID, lobbyCode,
                    m_LocalUser, password:password);

                LobbyConverters.RemoteToLocal(lobby, m_LocalLobby);
                await JoinLobby();
            }
            catch (LobbyServiceException exception)
            {
                SetGameState(GameState.JoinMenu);
                LogHandlerSettings.Instance.SpawnErrorPopup($"Error joining lobby : ({exception.ErrorCode}) {exception.Message}");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                SetGameState(GameState.JoinMenu);
                LogHandlerSettings.Instance.SpawnErrorPopup("Error joining lobby. Check the Console for details.");
            }
        }

        public void QueryLobbies()
        {
            if (m_isQueryingLobbies)
                return;

            StartCoroutine(QueryLobbiesRoutine());
        }

        IEnumerator QueryLobbiesRoutine()
        {
            m_isQueryingLobbies = true;
            LobbyList.QueryState.Value = LobbyQueryState.Fetching;
            float deadline = Time.realtimeSinceStartup + 12f;

            while (m_serviceInitializationTask != null && !m_serviceInitializationTask.IsCompleted &&
                   Time.realtimeSinceStartup < deadline)
                yield return null;

            if (m_serviceInitializationTask == null || !m_serviceInitializationTask.IsCompleted ||
                m_serviceInitializationTask.IsFaulted || m_serviceInitializationTask.IsCanceled ||
                !m_serviceInitializationTask.Result)
            {
                SetLobbyQueryError("Unity Services authentication did not complete within 12 seconds.");
                yield break;
            }

            Task<Unity.Services.Lobbies.Models.QueryResponse> queryTask =
                LobbyManager.RetrieveLobbyListAsync(m_lobbyColorFilter);

            deadline = Time.realtimeSinceStartup + 12f;
            while (!queryTask.IsCompleted && Time.realtimeSinceStartup < deadline)
                yield return null;

            if (!queryTask.IsCompleted)
            {
                SetLobbyQueryError("Lobby list request timed out after 12 seconds.");
                yield break;
            }

            if (queryTask.IsFaulted || queryTask.IsCanceled)
            {
                SetLobbyQueryError(queryTask.Exception?.GetBaseException().Message ?? "Lobby list request failed.");
                yield break;
            }

            var qr = queryTask.Result;
            if (qr == null)
            {
                LobbyList.Clear();
                m_isQueryingLobbies = false;
                yield break;
            }

            SetCurrentLobbies(LobbyConverters.QueryToLocalList(qr));
            m_isQueryingLobbies = false;
        }

        void SetLobbyQueryError(string message)
        {
            Debug.LogError($"[Color Clash] Lobby list refresh failed: {message}");
            LobbyList.QueryState.Value = LobbyQueryState.Error;
            m_isQueryingLobbies = false;
        }

        public async void QuickJoin()
        {
            try
            {
                var lobby = await LobbyManager.QuickJoinLobbyAsync(m_LocalUser, m_lobbyColorFilter);
                if (lobby != null)
                {
                    LobbyConverters.RemoteToLocal(lobby, m_LocalLobby);
                    await JoinLobby();
                }
                else
                {
                    SetGameState(GameState.JoinMenu);
                }
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                SetGameState(GameState.JoinMenu);
                LogHandlerSettings.Instance.SpawnErrorPopup("Quick Join failed. Check the Console for details.");
            }
        }

        public void SetLocalUserName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                LogHandlerSettings.Instance.SpawnErrorPopup(
                    "Empty Name not allowed."); // Lobby error type, then HTTP error type.
                return;
            }

            m_LocalUser.DisplayName.Value = name;
            SendLocalUserData();
        }

        public void SetLocalUserEmote(EmoteType emote)
        {
            m_LocalUser.Emote.Value = emote;
            SendLocalUserData();
        }

        public void SetLocalUserStatus(PlayerStatus status)
        {
            m_LocalUser.UserStatus.Value = status;
            SendLocalUserData();
        }

        public void SetLocalLobbyColor(int color)
        {
            if (m_LocalLobby.PlayerCount < 1)
                return;
            m_LocalLobby.LocalLobbyColor.Value = (LobbyColor)color;
            SendLocalLobbyData();
        }

        bool updatingLobby;

        async void SendLocalLobbyData()
        {
            await LobbyManager.UpdateLobbyDataAsync(LobbyConverters.LocalToRemoteLobbyData(m_LocalLobby));
        }

        async void SendLocalUserData()
        {
            await LobbyManager.UpdatePlayerDataAsync(LobbyConverters.LocalToRemoteUserData(m_LocalUser));
        }

        public void UIChangeMenuState(GameState state)
        {
            var isQuittingGame = LocalGameState == GameState.Lobby &&
                m_LocalLobby.LocalLobbyState.Value == LobbyState.InGame;

            if (isQuittingGame)
            {
                //If we were in-game, make sure we stop by the lobby first
                state = GameState.Lobby;
                ClientQuitGame();
            }
            SetGameState(state);
        }

        public void HostSetRelayCode(string code)
        {
            m_LocalLobby.RelayCode.Value = code;
            SendLocalLobbyData();
        }

        bool HasValidMatchPlayerCount => m_LocalLobby != null &&
            (m_LocalLobby.PlayerCount == 2 || m_LocalLobby.PlayerCount == 4);

        // Only the host subscribes to this callback. The lobby may change while a
        // countdown is running, so player-count validity is checked every time.
        void OnPlayersReady(int readyCount)
        {
            if (HasValidMatchPlayerCount && readyCount == m_LocalLobby.PlayerCount &&
                m_LocalLobby.LocalLobbyState.Value != LobbyState.CountDown)
            {
                m_LocalLobby.LocalLobbyState.Value = LobbyState.CountDown;
                SendLocalLobbyData();
            }
            else if (m_LocalLobby.LocalLobbyState.Value == LobbyState.CountDown)
            {
                m_LocalLobby.LocalLobbyState.Value = LobbyState.Lobby;
                SendLocalLobbyData();
            }
        }

        void OnLobbyStateChanged(LobbyState state)
        {
            if (state == LobbyState.Lobby)
                CancelCountDown();
            if (state == LobbyState.CountDown)
                BeginCountDown();
        }

        void BeginCountDown()
        {
            Debug.Log("Beginning Countdown.");
            m_countdown.StartCountDown();
        }

        void CancelCountDown()
        {
            Debug.Log("Countdown Cancelled.");
            m_countdown.CancelCountDown();
        }

        public void FinishedCountDown()
        {
            // A player can leave in the last countdown frame; never allocate Relay
            // or launch a match with 1 or 3 players.
            if (!HasValidMatchPlayerCount || CountReadyPlayers() != m_LocalLobby.PlayerCount)
            {
                m_LocalLobby.LocalLobbyState.Value = LobbyState.Lobby;
                SendLocalLobbyData();
                return;
            }

            m_LocalUser.UserStatus.Value = PlayerStatus.InGame;
            m_LocalLobby.LocalLobbyState.Value = LobbyState.InGame;
            m_setupInGame.StartNetworkedGame(m_LocalLobby, m_LocalUser);
        }

        public void BeginGame()
        {
            if (m_LocalUser.IsHost.Value)
            {
                m_LocalLobby.LocalLobbyState.Value = LobbyState.InGame;
                m_LocalLobby.Locked.Value = true;
                SendLocalLobbyData();
            }
        }

        public void ClientQuitGame()
        {
            EndGame();
            m_setupInGame?.OnGameEnd();
        }

        public void EndGame()
        {
            if (m_LocalUser.IsHost.Value)
            {
                m_LocalLobby.LocalLobbyState.Value = LobbyState.Lobby;
                m_LocalLobby.Locked.Value = false;
                SendLocalLobbyData();
            }

            SetLobbyView();
        }

        #region Setup

        async void Awake()
        {
            Application.wantsToQuit += OnWantToQuit;
            m_LocalUser = new LocalPlayer("", 0, false, "LocalPlayer");
            m_LocalLobby = new LocalLobby { LocalLobbyState = { Value = LobbyState.Lobby } };
            LobbyManager = new LobbyManager();

            m_serviceInitializationTask = InitializeServicesAndAuthenticate();
            await m_serviceInitializationTask;
        }

        async Task<bool> InitializeServicesAndAuthenticate()
        {
            try
            {
                string serviceProfileName = "player";
#if UNITY_EDITOR
                serviceProfileName = $"{serviceProfileName}{LocalProfileTool.LocalProfileSuffix}";
#endif
                if (!await UnityServiceAuthenticator.TrySignInAsync(serviceProfileName))
                    return false;
                AuthenticatePlayer();
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                return false;
            }
        }

        void AuthenticatePlayer()
        {
            var localId = AuthenticationService.Instance.PlayerId;
            var randomName = NameGenerator.GetName(localId);

            m_LocalUser.ID.Value = localId;
            m_LocalUser.DisplayName.Value = randomName;
        }

        #endregion

        void SetGameState(GameState state)
        {
            var isLeavingLobby = (state == GameState.Menu || state == GameState.JoinMenu) &&
                LocalGameState == GameState.Lobby;
            LocalGameState = state;

            Debug.Log($"Switching Game State to : {LocalGameState}");

            if (isLeavingLobby)
                LeaveLobby();
            onGameStateChanged.Invoke(LocalGameState);
        }

        void SetCurrentLobbies(IEnumerable<LocalLobby> lobbies)
        {
            var newLobbyDict = new Dictionary<string, LocalLobby>();
            foreach (var lobby in lobbies)
                newLobbyDict.Add(lobby.LobbyID.Value, lobby);

            LobbyList.CurrentLobbies = newLobbyDict;
            LobbyList.QueryState.Value = LobbyQueryState.Fetched;
        }

        async Task CreateLobby()
        {
            m_LocalUser.IsHost.Value = true;
            m_LocalLobby.onUserReadyChange = OnPlayersReady;
            m_LocalLobby.onUserJoined = _ => OnPlayersReady(CountReadyPlayers());
            m_LocalLobby.onUserLeft = _ => OnPlayersReady(CountReadyPlayers());
            try
            {
                await BindLobby();
            }
            catch (LobbyServiceException exception)
            {
                SetGameState(GameState.JoinMenu);
                LogHandlerSettings.Instance.SpawnErrorPopup($"Couldn't join Lobby : ({exception.ErrorCode}) {exception.Message}");
            }
        }

        int CountReadyPlayers()
        {
            int readyCount = 0;
            foreach (var player in m_LocalLobby.LocalPlayers)
            {
                if (player.UserStatus.Value == PlayerStatus.Ready)
                    readyCount++;
            }
            return readyCount;
        }

        async Task JoinLobby()
        {
            //Trigger UI Even when same value
            m_LocalUser.IsHost.ForceSet(false);
            await BindLobby();
        }

        async Task BindLobby()
        {
            await LobbyManager.BindLocalLobbyToRemote(m_LocalLobby.LobbyID.Value, m_LocalLobby);
            m_LocalLobby.LocalLobbyState.onChanged += OnLobbyStateChanged;
            SetLobbyView();
#if UNITY_WEBGL
            if (m_webGlLobbyPolling == null)
                m_webGlLobbyPolling = StartCoroutine(PollLobbyForWebGl());
#endif
        }

#if UNITY_WEBGL
        IEnumerator PollLobbyForWebGl()
        {
            var wait = new WaitForSeconds(1f);
            while (m_LocalLobby != null && !string.IsNullOrEmpty(m_LocalLobby.LobbyID.Value))
            {
                yield return wait;
                var lobbyTask = LobbyManager.GetLobbyAsync(m_LocalLobby.LobbyID.Value);
                float deadline = Time.realtimeSinceStartup + 12f;
                while (!lobbyTask.IsCompleted && Time.realtimeSinceStartup < deadline)
                    yield return null;

                if (lobbyTask.Status == TaskStatus.RanToCompletion && lobbyTask.Result != null)
                    LobbyConverters.RemoteToLocal(lobbyTask.Result, m_LocalLobby);
                else
                    Debug.LogWarning("[Color Clash] WebGL lobby refresh failed or timed out; will retry.");
            }

            m_webGlLobbyPolling = null;
        }
#endif

        public void LeaveLobby()
        {
            if (m_webGlLobbyPolling != null)
            {
                StopCoroutine(m_webGlLobbyPolling);
                m_webGlLobbyPolling = null;
            }
            m_LocalUser.ResetState();
#pragma warning disable 4014
            LobbyManager.LeaveLobbyAsync();
#pragma warning restore 4014
            ResetLocalLobby();
            LobbyList.Clear();
        }

        void Update()
        {
            if (m_enableOfflineBotShortcut && Input.GetKeyDown(KeyCode.F8))
            {
                Debug.Log("[Color Clash] Offline bot test started. This does not test Relay networking.");
                SceneManager.LoadScene("battle", LoadSceneMode.Single);
            }
        }

        void SetLobbyView()
        {
            Debug.Log($"Setting Lobby user state {GameState.Lobby}");
            SetGameState(GameState.Lobby);
            SetLocalUserStatus(PlayerStatus.Lobby);
        }

        void ResetLocalLobby()
        {
            m_LocalLobby.ResetLobby();
            m_LocalLobby.RelayServer = null;
        }

        #region Teardown

        /// <summary>
        /// In builds, if we are in a lobby and try to send a Leave request on application quit, it won't go through if we're quitting on the same frame.
        /// So, we need to delay just briefly to let the request happen (though we don't need to wait for the result).
        /// </summary>
        IEnumerator LeaveBeforeQuit()
        {
            ForceLeaveAttempt();
            yield return null;
            Application.Quit();
        }

        bool OnWantToQuit()
        {
            bool canQuit = string.IsNullOrEmpty(m_LocalLobby?.LobbyID.Value);
            StartCoroutine(LeaveBeforeQuit());
            return canQuit;
        }

        void OnDestroy()
        {
            Application.wantsToQuit -= OnWantToQuit;
            ForceLeaveAttempt();
            LobbyManager.Dispose();
        }

        void ForceLeaveAttempt()
        {
            if (!string.IsNullOrEmpty(m_LocalLobby?.LobbyID.Value))
            {
#pragma warning disable 4014
                LobbyManager.LeaveLobbyAsync();
#pragma warning restore 4014
                m_LocalLobby = null;
            }
        }

        #endregion
    }
}
