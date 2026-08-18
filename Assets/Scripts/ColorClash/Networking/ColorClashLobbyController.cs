using System;
using System.Collections;
using System.Collections.Generic;
using ColorClash.Core;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using UnityEngine;

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

        readonly List<Lobby> visibleRooms = new List<Lobby>();
        bool initialized;
        bool busy;
        string status = "서비스 연결 대기 중";
        string roomName;
        string joinCode;
        Lobby currentRoom;

        public IReadOnlyList<Lobby> VisibleRooms => visibleRooms;
        public Lobby CurrentRoom => currentRoom;
        public bool IsBusy => busy;
        public string Status => status;

        void Awake()
        {
            roomName = defaultRoomName;
            StartCoroutine(InitializeRoutine());
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
            busy = false;
            status = "방이 생성되었습니다. 서버를 할당하는 중…";
            ColorClashSession.BeginOnline(currentRoom.Id, MatchSettings.Default);
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
            busy = false;
            status = "방에 참가했습니다. 서버 연결 정보를 기다리는 중…";
            ColorClashSession.BeginOnline(currentRoom.Id, MatchSettings.Default);
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
            GUI.Box(new Rect(20, 20, 520, 390), "COLOR CLASH · ONLINE LOBBY");
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
                GUI.Label(new Rect(36, 227, 460, 22), "서버 상태: " + ReadLobbyData(currentRoom, ServerStateKey, "할당 대기"));
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
