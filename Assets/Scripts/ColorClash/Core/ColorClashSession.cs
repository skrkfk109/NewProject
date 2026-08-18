using UnityEngine;

namespace ColorClash.Core
{
    /// <summary>
    /// A small hand-off point between the lobby and battle. It deliberately stores
    /// no connection object: network transports belong to the client/server layer.
    /// </summary>
    public static class ColorClashSession
    {
        public static MatchSettings Settings { get; private set; } = MatchSettings.Default;
        public static bool IsOnlineMatch { get; private set; }
        public static string RoomId { get; private set; } = string.Empty;

        public static void BeginOffline()
        {
            Settings = MatchSettings.Default;
            IsOnlineMatch = false;
            RoomId = string.Empty;
        }

        public static void BeginOnline(string roomId, MatchSettings settings)
        {
            Settings = settings.maxPlayers > 0 ? settings : MatchSettings.Default;
            IsOnlineMatch = true;
            RoomId = roomId ?? string.Empty;
        }
    }
}
