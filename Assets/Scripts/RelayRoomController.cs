using System;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Small-room Relay entry point for the prototype. The host receives a short join code;
/// clients enter it to join without exposing either player's IP address.
/// </summary>
public sealed class RelayRoomController : MonoBehaviour
{
    [Header("Prototype Room")]
    [SerializeField, Range(2, 8)] int maxPlayers = 4;
    [SerializeField] string connectionType = "dtls";
    [SerializeField] string battleSceneName = "battle";

    NetworkManager networkManager;
    UnityTransport transport;
    string joinCodeInput = string.Empty;
    string roomCode = string.Empty;
    string status = "방을 만들거나 참가 코드를 입력하세요.";
    bool isConnecting;

    public bool IsConnected => networkManager != null && networkManager.IsListening;
    public bool IsHost => networkManager != null && networkManager.IsHost;
    public string RoomCode => roomCode;

    void Awake()
    {
        EnsureNetworkManager();
    }

    void EnsureNetworkManager()
    {
        networkManager = NetworkManager.Singleton;
        if (networkManager != null)
        {
            transport = networkManager.GetComponent<UnityTransport>();
            return;
        }

        var managerObject = new GameObject("Network Manager");
        transport = managerObject.AddComponent<UnityTransport>();
        networkManager = managerObject.AddComponent<NetworkManager>();
        networkManager.NetworkConfig.NetworkTransport = transport;
        DontDestroyOnLoad(managerObject);
    }

    async void CreateRoom()
    {
        if (isConnecting || IsConnected) return;
        isConnecting = true;
        status = "Relay 방을 만드는 중…";
        try
        {
            await EnsureSignedIn();
            Allocation allocation = await RelayService.Instance.CreateAllocationAsync(maxPlayers - 1);
            // Unity 6 moved the Allocation-to-transport conversion to this
            // extension method; RelayServerData no longer has this constructor.
            transport.SetRelayServerData(allocation.ToRelayServerData(connectionType));
            roomCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);
            if (!networkManager.StartHost()) throw new InvalidOperationException("호스트 시작에 실패했습니다.");
            status = "방 생성 완료 — 아래 코드를 친구에게 공유하세요.";
        }
        catch (Exception exception)
        {
            status = "방 생성 실패: " + exception.Message;
            Debug.LogException(exception, this);
        }
        finally
        {
            isConnecting = false;
        }
    }

    async void JoinRoom()
    {
        if (isConnecting || IsConnected) return;
        string code = joinCodeInput.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(code))
        {
            status = "참가 코드를 입력하세요.";
            return;
        }

        isConnecting = true;
        status = "방에 연결하는 중…";
        try
        {
            await EnsureSignedIn();
            JoinAllocation allocation = await RelayService.Instance.JoinAllocationAsync(code);
#if UNITY_WEBGL && !UNITY_EDITOR
            transport.UseWebSockets = true;
            transport.SetRelayServerData(allocation.ToRelayServerData("wss"));
#else
            transport.UseWebSockets = false;
            transport.SetRelayServerData(allocation.ToRelayServerData(connectionType));
#endif
            if (!networkManager.StartClient()) throw new InvalidOperationException("클라이언트 시작에 실패했습니다.");
            roomCode = code;
            status = "방에 참가했습니다. 게임 동기화를 준비 중입니다.";
        }
        catch (Exception exception)
        {
            status = "참가 실패: " + exception.Message;
            Debug.LogException(exception, this);
        }
        finally
        {
            isConnecting = false;
        }
    }

    async System.Threading.Tasks.Task EnsureSignedIn()
    {
        if (UnityServices.State != ServicesInitializationState.Initialized)
            await UnityServices.InitializeAsync();
        if (!AuthenticationService.Instance.IsSignedIn)
            await AuthenticationService.Instance.SignInAnonymouslyAsync();
    }

    void StartBattle()
    {
        if (!IsHost) return;
        if (!Application.CanStreamedLevelBeLoaded(battleSceneName))
        {
            status = $"Build Settings에 '{battleSceneName}' 씬을 추가해야 합니다.";
            return;
        }
        status = "배틀 필드로 이동 중…";
        networkManager.SceneManager.LoadScene(battleSceneName, LoadSceneMode.Single);
    }

    void OnGUI()
    {
        if (!Application.isPlaying) return;
        const float panelWidth = 460f;
        GUILayout.BeginArea(new Rect((Screen.width - panelWidth) * .5f, (Screen.height - 340f) * .5f, panelWidth, 340f), GUI.skin.box);
        GUILayout.Label("COLOR CLASH · MULTIPLAYER LOBBY");
        GUILayout.Space(4f);
        GUILayout.Label(status);
        GUILayout.Space(16f);

        if (!IsConnected)
        {
            GUI.enabled = !isConnecting;
            if (GUILayout.Button("방 만들기 (Host)", GUILayout.Height(38f))) CreateRoom();
            GUILayout.Space(8f);
            GUILayout.Label("친구에게 받은 참가 코드");
            GUILayout.BeginHorizontal();
            joinCodeInput = GUILayout.TextField(joinCodeInput, 8, GUILayout.Width(220f), GUILayout.Height(28f));
            if (GUILayout.Button("코드로 참가", GUILayout.Height(28f))) JoinRoom();
            GUILayout.EndHorizontal();
            GUI.enabled = true;
        }
        else if (IsHost)
        {
            GUILayout.Label("참가 코드: " + roomCode);
            GUILayout.Label("현재 접속: " + networkManager.ConnectedClientsList.Count + " / " + maxPlayers);
            GUILayout.Space(16f);
            if (GUILayout.Button("게임 시작", GUILayout.Height(38f))) StartBattle();
        }
        else
        {
            GUILayout.Label("참가한 방: " + roomCode);
            GUILayout.Space(16f);
            GUILayout.Label("방장이 게임을 시작할 때까지 기다리는 중입니다.");
        }
        GUILayout.EndArea();
    }
}
