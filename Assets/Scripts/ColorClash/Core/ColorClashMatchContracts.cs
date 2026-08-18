using System;
using UnityEngine;

namespace ColorClash.Core
{
    /// <summary>
    /// Shared, transport-agnostic match data. Both the Web client and the Linux
    /// dedicated-server build use these types; no Lobby, Relay or Netcode API is
    /// allowed in this folder.
    /// </summary>
    public enum Team : byte
    {
        Blue = 0,
        Red = 1
    }

    public enum MatchPhase : byte
    {
        WaitingForPlayers,
        Countdown,
        Playing,
        Finished
    }

    [Serializable]
    public struct MatchSettings
    {
        [Min(1)] public int maxPlayers;
        [Min(1f)] public float roundSeconds;
        [Min(0f)] public float barrierFallsWithSecondsRemaining;

        public static MatchSettings Default => new MatchSettings
        {
            maxPlayers = 4,
            roundSeconds = 75f,
            barrierFallsWithSecondsRemaining = 30f
        };

        public bool IsSupportedPlayerCount(int playerCount) => playerCount == 2 || playerCount == 4;
    }

    [Serializable]
    public struct PlayerMoveCommand
    {
        public ulong playerId;
        public Vector3 position;
        public float clientTime;
    }

    [Serializable]
    public struct PaintCommand
    {
        public ulong playerId;
        public Vector2 mapUv;
        public int paletteIndex;
        public float clientTime;
    }

    [Serializable]
    public struct MatchSnapshot
    {
        public MatchPhase phase;
        public float remainingSeconds;
        public float blueScore;
        public float redScore;
        public bool barrierDown;
    }
}
