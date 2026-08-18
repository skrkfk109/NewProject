using System;
using System.Collections.Generic;
using ColorClash.Core;
using UnityEngine;

namespace ColorClash.Server
{
    /// <summary>
    /// The single source of truth for a Color Clash round.
    ///
    /// It deliberately contains no MonoBehaviour, scene lookup, Lobby, Relay, or
    /// Netcode call. A Linux server will own one instance; Web clients only submit
    /// commands and render the snapshots/results produced here.
    /// </summary>
    public sealed class AuthoritativeMatch
    {
        public readonly struct PaintResult
        {
            public readonly bool accepted;
            public readonly int[] changedCells;

            public PaintResult(bool accepted, int[] changedCells)
            {
                this.accepted = accepted;
                this.changedCells = changedCells ?? Array.Empty<int>();
            }
        }

        public readonly struct PlayerState
        {
            public readonly ulong playerId;
            public readonly Team team;
            public readonly Vector3 position;

            public PlayerState(ulong playerId, Team team, Vector3 position)
            {
                this.playerId = playerId;
                this.team = team;
                this.position = position;
            }
        }

        readonly MatchSettings settings;
        readonly int width;
        readonly int height;
        readonly float boardWidth;
        readonly float boardDepth;
        readonly int paletteCount;
        readonly byte[] targetPalette;
        readonly sbyte[] paintedPalette;
        readonly sbyte[] paintOwner;
        readonly List<ulong> joinOrder = new List<ulong>(4);
        readonly HashSet<ulong> readyPlayers = new HashSet<ulong>();
        readonly Dictionary<ulong, Vector3> positions = new Dictionary<ulong, Vector3>();
        readonly Dictionary<ulong, float> lastMoveAt = new Dictionary<ulong, float>();
        readonly Dictionary<ulong, float> lastPaintAt = new Dictionary<ulong, float>();

        const float CountdownSeconds = 3f;
        const float MaxMoveSpeed = 9f;
        const float MoveGraceDistance = 1.5f;
        // Keep server validation aligned with BattlePrototypeController's default.
        const float PaintRadius = 1.25f;
        const float PaintReach = 2.25f;
        const float PaintInterval = .06f;

        float serverTime;
        float phaseTime;
        int[] scoreTotals = new int[2];
        int[] scoreCorrect = new int[2];
        bool scoreDirty = true;

        public MatchPhase Phase { get; private set; } = MatchPhase.WaitingForPlayers;
        public bool BarrierDown { get; private set; }
        public float RemainingSeconds { get; private set; }
        public int PlayerCount => joinOrder.Count;
        public IReadOnlyList<ulong> Players => joinOrder;

        public AuthoritativeMatch(
            MatchSettings matchSettings,
            int mapWidth,
            int mapHeight,
            float mapBoardWidth,
            float mapBoardDepth,
            int mapPaletteCount,
            byte[] mapTargetPalette)
        {
            if (mapWidth <= 0 || mapHeight <= 0)
                throw new ArgumentOutOfRangeException(nameof(mapWidth));
            if (mapTargetPalette == null || mapTargetPalette.Length != mapWidth * mapHeight)
                throw new ArgumentException("Target palette must contain one entry per map cell.", nameof(mapTargetPalette));
            if (mapPaletteCount < 1)
                throw new ArgumentOutOfRangeException(nameof(mapPaletteCount));

            settings = matchSettings.maxPlayers > 0 ? matchSettings : MatchSettings.Default;
            width = mapWidth;
            height = mapHeight;
            boardWidth = Mathf.Max(.01f, mapBoardWidth);
            boardDepth = Mathf.Max(.01f, mapBoardDepth);
            paletteCount = mapPaletteCount;
            targetPalette = (byte[])mapTargetPalette.Clone();
            paintedPalette = new sbyte[targetPalette.Length];
            paintOwner = new sbyte[targetPalette.Length];
            Array.Fill(paintedPalette, (sbyte)-1);
            Array.Fill(paintOwner, (sbyte)-1);
            RemainingSeconds = settings.roundSeconds;
            RecalculateScores();
        }

        // Join order is intentional: it yields Blue/Red for 1v1 and Blue/Red/Blue/Red for 2v2.
        public bool TryJoin(ulong playerId)
        {
            if (Phase != MatchPhase.WaitingForPlayers || joinOrder.Contains(playerId) || joinOrder.Count >= settings.maxPlayers)
                return false;

            joinOrder.Add(playerId);
            Team team = TeamFor(playerId);
            positions[playerId] = SpawnFor(team);
            lastMoveAt[playerId] = serverTime;
            lastPaintAt[playerId] = -PaintInterval;
            return true;
        }

        public void RemovePlayer(ulong playerId)
        {
            joinOrder.Remove(playerId);
            readyPlayers.Remove(playerId);
            positions.Remove(playerId);
            lastMoveAt.Remove(playerId);
            lastPaintAt.Remove(playerId);
            if (Phase is MatchPhase.Countdown or MatchPhase.Playing)
                Phase = MatchPhase.WaitingForPlayers;
        }

        public bool SetReady(ulong playerId, bool ready)
        {
            if (Phase != MatchPhase.WaitingForPlayers || !joinOrder.Contains(playerId)) return false;
            if (ready) readyPlayers.Add(playerId);
            else readyPlayers.Remove(playerId);
            return TryBeginCountdown();
        }

        public bool TryGetPlayerState(ulong playerId, out PlayerState state)
        {
            if (!positions.TryGetValue(playerId, out Vector3 position))
            {
                state = default;
                return false;
            }
            state = new PlayerState(playerId, TeamFor(playerId), position);
            return true;
        }

        public Team TeamFor(ulong playerId)
        {
            int index = joinOrder.IndexOf(playerId);
            return index < 0 || index % 2 == 0 ? Team.Blue : Team.Red;
        }

        public bool TryApplyMove(PlayerMoveCommand command)
        {
            if (Phase != MatchPhase.Playing || !positions.TryGetValue(command.playerId, out Vector3 current))
                return false;

            float elapsed = Mathf.Max(0f, serverTime - lastMoveAt[command.playerId]);
            float maxDistance = MaxMoveSpeed * elapsed + MoveGraceDistance;
            Vector3 requested = ConstrainPosition(command.position, TeamFor(command.playerId));
            if (Vector3.Distance(current, requested) > maxDistance)
                return false;

            positions[command.playerId] = requested;
            lastMoveAt[command.playerId] = serverTime;
            return true;
        }

        public PaintResult TryApplyPaint(PaintCommand command)
        {
            if (Phase != MatchPhase.Playing || command.paletteIndex < 0 || command.paletteIndex >= paletteCount ||
                !positions.TryGetValue(command.playerId, out Vector3 playerPosition))
                return new PaintResult(false, null);
            if (command.mapUv.x < 0f || command.mapUv.x > 1f || command.mapUv.y < 0f || command.mapUv.y > 1f ||
                serverTime - lastPaintAt[command.playerId] < PaintInterval)
                return new PaintResult(false, null);

            Vector3 targetWorld = UvToWorld(command.mapUv);
            if (Vector2.Distance(new Vector2(playerPosition.x, playerPosition.z), new Vector2(targetWorld.x, targetWorld.z)) > PaintReach)
                return new PaintResult(false, null);

            lastPaintAt[command.playerId] = serverTime;
            int centerX = Mathf.Clamp(Mathf.FloorToInt(command.mapUv.x * width), 0, width - 1);
            int centerZ = Mathf.Clamp(Mathf.FloorToInt(command.mapUv.y * height), 0, height - 1);
            int radiusX = Mathf.CeilToInt(PaintRadius / (boardWidth / width));
            int radiusZ = Mathf.CeilToInt(PaintRadius / (boardDepth / height));
            var changed = new List<int>((radiusX * 2 + 1) * (radiusZ * 2 + 1));
            Team team = TeamFor(command.playerId);

            for (int z = centerZ - radiusZ; z <= centerZ + radiusZ; z++)
            for (int x = centerX - radiusX; x <= centerX + radiusX; x++)
            {
                if (x < 0 || z < 0 || x >= width || z >= height) continue;
                float dx = ((x + .5f) / width - command.mapUv.x) * boardWidth;
                float dz = ((z + .5f) / height - command.mapUv.y) * boardDepth;
                if (dx * dx + dz * dz > PaintRadius * PaintRadius) continue;

                int index = z * width + x;
                if (paintedPalette[index] == command.paletteIndex && paintOwner[index] == (sbyte)team) continue;
                paintedPalette[index] = (sbyte)command.paletteIndex;
                paintOwner[index] = (sbyte)team;
                changed.Add(index);
            }

            if (changed.Count > 0) scoreDirty = true;
            return new PaintResult(true, changed.ToArray());
        }

        public void Tick(float deltaSeconds)
        {
            if (deltaSeconds <= 0f || Phase == MatchPhase.Finished) return;
            serverTime += deltaSeconds;
            phaseTime += deltaSeconds;

            if (Phase == MatchPhase.Countdown && phaseTime >= CountdownSeconds)
            {
                Phase = MatchPhase.Playing;
                phaseTime = 0f;
            }
            else if (Phase == MatchPhase.Playing)
            {
                RemainingSeconds = Mathf.Max(0f, RemainingSeconds - deltaSeconds);
                BarrierDown = RemainingSeconds <= settings.barrierFallsWithSecondsRemaining;
                if (RemainingSeconds <= 0f) Phase = MatchPhase.Finished;
            }
        }

        public MatchSnapshot Snapshot()
        {
            RecalculateScores();
            return new MatchSnapshot
            {
                phase = Phase,
                remainingSeconds = RemainingSeconds,
                blueScore = ScoreFor(Team.Blue),
                redScore = ScoreFor(Team.Red),
                barrierDown = BarrierDown
            };
        }

        public float ScoreFor(Team team)
        {
            RecalculateScores();
            int index = (int)team;
            return scoreTotals[index] == 0 ? 0f : 100f * scoreCorrect[index] / scoreTotals[index];
        }

        void RecalculateScores()
        {
            if (!scoreDirty) return;
            Array.Clear(scoreTotals, 0, scoreTotals.Length);
            Array.Clear(scoreCorrect, 0, scoreCorrect.Length);
            for (int z = 0; z < height; z++)
            for (int x = 0; x < width; x++)
            {
                // Same equal-area split as the prototype. The final map generator
                // will supply a balanced territory mask instead of this default.
                Team territory = x >= width / 2 ? Team.Blue : Team.Red;
                int team = (int)territory;
                scoreTotals[team]++;
                int index = z * width + x;
                if (paintedPalette[index] == targetPalette[index]) scoreCorrect[team]++;
            }
            scoreDirty = false;
        }

        bool TryBeginCountdown()
        {
            if (!settings.IsSupportedPlayerCount(joinOrder.Count) || readyPlayers.Count != joinOrder.Count)
                return false;
            Phase = MatchPhase.Countdown;
            phaseTime = 0f;
            return true;
        }

        Vector3 SpawnFor(Team team)
        {
            float x = team == Team.Blue ? -boardWidth * .37f : boardWidth * .37f;
            return new Vector3(x, 0f, team == Team.Blue ? -boardDepth * .28f : boardDepth * .28f);
        }

        Vector3 ConstrainPosition(Vector3 position, Team team)
        {
            position.x = Mathf.Clamp(position.x, -boardWidth * .5f + .5f, boardWidth * .5f - .5f);
            position.z = Mathf.Clamp(position.z, -boardDepth * .5f + .5f, boardDepth * .5f - .5f);
            if (!BarrierDown)
            {
                if (team == Team.Blue) position.x = Mathf.Min(position.x, -.45f);
                else position.x = Mathf.Max(position.x, .45f);
            }
            return position;
        }

        // Unity's Plane mirrors U in the client prototype: world-left is high-U.
        Vector3 UvToWorld(Vector2 uv) => new Vector3((.5f - uv.x) * boardWidth, 0f, (uv.y - .5f) * boardDepth);
    }
}
