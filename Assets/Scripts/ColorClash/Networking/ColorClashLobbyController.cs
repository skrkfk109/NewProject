using System;
using System.Collections;
using System.Collections.Generic;
using ColorClash.Core;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace ColorClash.Networking
{
    /// <summary>
    /// Color Clash's own room directory client. It only manages human-facing room
    /// metadata; a future allocator service owns server processes and writes the
    /// connection ticket back into the room's ServerTicket field.
    /// </summary>
    public sealed class ColorClashLobbyController : MonoBehaviour
    {
        public const string ServerStateKey = "cc_server_state";
        public const string ServerTicketKey = "cc_server_ticket";
        public const string MatchSizeKey = "cc_match_size";

        [SerializeField] string defaultRoomName = "Color Clash Room";
        [SerializeField, Range(2, 4)] int maximumPlayers = 4;
        [Tooltip("HTTPS endpoint of the small allocator running on the Color Clash VPS.")]
        [SerializeField] string allocatorEndpoint = "https://2-28-8-212.sslip.io/allocate";

        readonly List<Lobby> visibleRooms = new List<Lobby>();
        bool initialized;
        bool busy;
        string status = "서비스 연결 대기 중";
        string roomName;
        string joinCode;
        Lobby currentRoom;

        [Serializable]
        sealed class AllocationRequest
        {
            public string lobbyId;
        }

        [Serializable]
        sealed class AllocationResponse
        {
            public string relayJoinCode;
            public string error;
        }

        public IReadOnlyList<Lobby> VisibleRooms => visibleRooms;
        public Lobby CurrentRoom => currentRoom;
        public bool IsBusy => busy;
        public string Status => status;

        void Awake()
        {
            roomName = defaultRoomName;
            StartCoroutine(InitializeRoutine());
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void CreateForLobbyScene()
        {
            if (!Application.isPlaying || SceneManager.GetActiveScene().name != "Lobby") return;
            if (FindFirstObjectByType<ColorClashLobbyController>() == null)
                new GameObject("Color Clash Online Lobby").AddComponent<ColorClashLobbyController>();
        }

        IEnumerator InitializeRoutine()
        {
            busy = true;
            status = "Unity 서비스 연결 중…";
            var initialize = UnityServices.InitializeAsync();
            while (!initialize.IsCompleted) yield return null;
            if (initialize.IsFaulted)
            {
                SetFailure(initialize.Exception);
                yield break;
            }

            if (!AuthenticationService.Instance.IsSignedIn)
            {
                var signIn = AuthenticationService.Instance.SignInAnonymouslyAsync();
                while (!signIn.IsCompleted) yield return null;
                if (signIn.IsFaulted)
                {
                    SetFailure(signIn.Exception);
                    yield break;
                }
            }

            initialized = true;
            busy = false;
            status = "방 목록을 불러올 수 있습니다.";
            RefreshRooms();
        }

        public void RefreshRooms()
        {
            if (!CanRequest()) return;
            StartCoroutine(RefreshRoomsRoutine());
        }

        IEnumerator RefreshRoomsRoutine()
        {
            busy = true;
            status = "방 목록 새로고침 중…";
            var query = LobbyService.Instance.QueryLobbiesAsync(new QueryLobbiesOptions { Count = 25 });
            while (!query.IsCompleted) yield return null;
            if (query.IsFaulted)
            {
                SetFailure(query.Exception);
                yield break;
            }

            visibleRooms.Clear();
            visibleRooms.AddRange(query.Result.Results);
            busy = false;
            status = $"참가 가능한 방 {visibleRooms.Count}개";
        }

        public void CreateRoom(string requestedName, bool isPrivate)
        {
            if (!CanRequest()) return;
            StartCoroutine(CreateRoomRoutine(string.IsNullOrWhiteSpace(requestedName) ? defaultRoomName : requestedName, isPrivate));
        }

        IEnumerator CreateRoomRoutine(string requestedName, bool isPrivate)
        {
            busy = true;
            status = "방 생성 요청 중…";
            var data = new Dictionary<string, DataObject>
            {
                { ServerStateKey, new DataObject(DataObject.VisibilityOptions.Public, "allocating", DataObject.IndexOptions.S1) },
                { ServerTicketKey, new DataObject(DataObject.VisibilityOptions.Member, string.Empty) },
                { MatchSizeKey, new DataObject(DataObject.VisibilityOptions.Public, maximumPlayers.ToString(), DataObject.IndexOptions.N1) }
            };
            var options = new CreateLobbyOptions
            {
                IsPrivate = isPrivate,
                Data = data,
                Player = new Player(AuthenticationService.Instance.PlayerId)
            };
            var create = LobbyService.Instance.CreateLobbyAsync(requestedName, maximumPlayers, options);
            while (!create.IsCompleted) yield return null;
            if (create.IsFaulted)
            {
                SetFailure(create.Exception);
                yield break;
            }

            currentRoom = create.Result;
            status = "방이 생성되었습니다. 전용 서버를 할당하는 중…";
            yield return StartCoroutine(AllocateServerRoutine());
        }

        public void JoinRoomByCode(string roomCode)
        {
            if (!CanRequest() || string.IsNullOrWhiteSpace(roomCode)) return;
            StartCoroutine(JoinRoomRoutine(roomCode.Trim()));
        }

        IEnumerator JoinRoomRoutine(string roomCode)
        {
            busy = true;
            status = "방 참가 요청 중…";
            var join = LobbyService.Instance.JoinLobbyByCodeAsync(roomCode, new JoinLobbyByCodeOptions
            {
                Player = new Player(AuthenticationService.Instance.PlayerId)
            });
            while (!join.IsCompleted) yield return null;
            if (join.IsFaulted)
            {
                SetFailure(join.Exception);
                yield break;
            }

            currentRoom = join.Result;
            status = "방에 참가했습니다. 서버 연결 정보를 기다리는 중…";
            yield return StartCoroutine(WaitForServerTicketRoutine());
        }

        IEnumerator AllocateServerRoutine()
        {
            if (string.IsNullOrWhiteSpace(allocatorEndpoint))
            {
                SetFailure(new InvalidOperationException("VPS 할당 API 주소가 비어 있습니다."));
                yield break;
            }

            string body = JsonUtility.ToJson(new AllocationRequest { lobbyId = currentRoom.Id });
            using var request = new UnityWebRequest(allocatorEndpoint, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(body)),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = 30
            };
            request.SetRequestHeader("Content-Type", "application/json");
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
            {
                SetFailure(new InvalidOperationException("전용 서버 할당 실패: " + request.error));
                yield break;
            }

            AllocationResponse response = JsonUtility.FromJson<AllocationResponse>(request.downloadHandler.text);
            if (response == null || string.IsNullOrWhiteSpace(response.relayJoinCode))
            {
                SetFailure(new InvalidOperationException(response?.error ?? "전용 서버가 Relay 코드를 반환하지 않았습니다."));
                yield break;
            }

            var update = LobbyService.Instance.UpdateLobbyAsync(currentRoom.Id, new UpdateLobbyOptions
            {
                Data = new Dictionary<string, DataObject>
                {
                    { ServerStateKey, new DataObject(DataObject.VisibilityOptions.Public, "ready", DataObject.IndexOptions.S1) },
                    { ServerTicketKey, new DataObject(DataObject.VisibilityOptions.Member, response.relayJoinCode.Trim().ToUpperInvariant()) }
                }
            });
            while (!update.IsCompleted) yield return null;
            if (update.IsFaulted)
            {
                SetFailure(update.Exception);
                yield break;
            }

            currentRoom = update.Result;
            BeginDedicatedMatch(response.relayJoinCode);
        }

        IEnumerator WaitForServerTicketRoutine()
        {
            const float timeoutSeconds = 45f;
            float timeoutAt = Time.unscaledTime + timeoutSeconds;
            while (Time.unscaledTime < timeoutAt)
            {
                string ticket = ReadLobbyData(currentRoom, ServerTicketKey, string.Empty);
                if (!string.IsNullOrWhiteSpace(ticket))
                {
                    BeginDedicatedMatch(ticket);
                    yield break;
                }

                yield return new WaitForSecondsRealtime(1f);
                var get = LobbyService.Instance.GetLobbyAsync(currentRoom.Id);
                while (!get.IsCompleted) yield return null;
                if (get.IsFaulted)
                {
                    SetFailure(get.Exception);
                    yield break;
                }
                currentRoom = get.Result;
            }

            SetFailure(new TimeoutException("전용 서버 할당을 기다리는 시간이 초과되었습니다."));
        }

        void BeginDedicatedMatch(string relayJoinCode)
        {
            busy = false;
            status = "전용 서버에 연결하는 중…";
            ColorClashSession.BeginOnline(currentRoom.Id, MatchSettings.Default, relayJoinCode);
            DedicatedRelayClient.Connect(relayJoinCode);
        }

        void SetFailure(Exception exception)
        {
            busy = false;
            status = $"오류: {exception?.GetBaseException().Message}";
            Debug.LogError($"[Color Clash Lobby] {status}");
        }

        bool CanRequest()
        {
            if (!initialized || busy)
            {
                if (!busy) status = "서비스 연결이 아직 완료되지 않았습니다.";
                return false;
            }
            return true;
        }

        // This is deliberately code-built for the prototype; the hierarchy is
        // replaced by designed UGUI prefabs in the UI pass.
        void OnGUI()
        {
            if (!Application.isPlaying) return;
            GUI.Box(new Rect(20, 20, 520, 390), "COLOR CLASH · DEDICATED ONLINE LOBBY");
            GUI.Label(new Rect(36, 58, 480, 24), status);
            GUI.Label(new Rect(36, 94, 120, 24), "방 이름");
            roomName = GUI.TextField(new Rect(120, 94, 220, 24), roomName);
            if (GUI.Button(new Rect(352, 94, 150, 24), "방 만들기")) CreateRoom(roomName, false);

            GUI.Label(new Rect(36, 130, 120, 24), "참가 코드");
            joinCode = GUI.TextField(new Rect(120, 130, 220, 24), joinCode);
            if (GUI.Button(new Rect(352, 130, 150, 24), "코드로 참가")) JoinRoomByCode(joinCode);
            if (GUI.Button(new Rect(36, 166, 160, 26), "방 목록 새로고침")) RefreshRooms();

            if (currentRoom != null)
            {
                GUI.Label(new Rect(36, 205, 460, 22), $"현재 방: {currentRoom.Name}  ·  코드: {currentRoom.LobbyCode}");
                GUI.Label(new Rect(36, 227, 460, 22), "전용 서버: " + ReadLobbyData(currentRoom, ServerStateKey, "할당 대기"));
            }

            float y = 270f;
            foreach (Lobby room in visibleRooms)
            {
                GUI.Label(new Rect(36, y, 280, 22), $"{room.Name}  ({room.Players.Count}/{room.MaxPlayers})");
                if (GUI.Button(new Rect(330, y, 100, 22), "참가")) JoinRoomByCode(room.LobbyCode);
                y += 25f;
                if (y > 380f) break;
            }
        }

        static string ReadLobbyData(Lobby lobby, string key, string fallback)
        {
            return lobby.Data != null && lobby.Data.TryGetValue(key, out DataObject value) ? value.Value : fallback;
        }
    }
}
