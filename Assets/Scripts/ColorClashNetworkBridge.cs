using System.Collections;
using System.Linq;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>Host-authoritative battle replication for up to four Color Clash players.</summary>
public sealed class ColorClashNetworkBridge : MonoBehaviour
{
    const string StateRequestMessage = "ColorClash/StateRequest";
    const string StateBroadcastMessage = "ColorClash/StateBroadcast";
    const string PaintRequestMessage = "ColorClash/PaintRequest";
    const string PaintBroadcastMessage = "ColorClash/PaintBroadcast";
    const string ClockMessage = "ColorClash/Clock";
    const string TeamMessage = "ColorClash/Team";

    NetworkManager manager;
    BattlePrototypeController battle;
    float nextStateSend, nextClockSend, nextTeamSend;
    bool connected;
    int localTeam;
    bool teamAssigned;

    IEnumerator Start()
    {
        while (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsListening) yield return null;
        manager = NetworkManager.Singleton;
        while (battle == null || !battle.IsBattleReady)
        {
            battle = FindObjectOfType<BattlePrototypeController>();
            yield return null;
        }

        manager.CustomMessagingManager.RegisterNamedMessageHandler(StateRequestMessage, ReceiveStateRequest);
        manager.CustomMessagingManager.RegisterNamedMessageHandler(StateBroadcastMessage, ReceiveStateBroadcast);
        manager.CustomMessagingManager.RegisterNamedMessageHandler(PaintRequestMessage, ReceivePaintRequest);
        manager.CustomMessagingManager.RegisterNamedMessageHandler(PaintBroadcastMessage, ReceivePaintBroadcast);
        manager.CustomMessagingManager.RegisterNamedMessageHandler(ClockMessage, ReceiveClock);
        manager.CustomMessagingManager.RegisterNamedMessageHandler(TeamMessage, ReceiveTeam);

        localTeam = TeamFor(manager.LocalClientId);
        teamAssigned = manager.IsHost;
        battle.OnMultiplayerPaintRequested += RequestPaint;
        battle.EnableMultiplayer(localTeam, manager.IsHost);
        connected = true;
    }

    void Update()
    {
        if (!connected || battle == null || manager == null || !manager.IsListening) return;
        if (Time.unscaledTime >= nextStateSend)
        {
            nextStateSend = Time.unscaledTime + .05f;
            if (manager.IsHost) BroadcastState(manager.LocalClientId, battle.LocalPlayerPosition);
            else SendStateRequest(battle.LocalPlayerPosition);
        }
        if (manager.IsHost && Time.unscaledTime >= nextClockSend)
        {
            nextClockSend = Time.unscaledTime + .15f;
            SendClock();
        }
        if (manager.IsHost && Time.unscaledTime >= nextTeamSend)
        {
            nextTeamSend = Time.unscaledTime + .5f;
            SendTeamAssignments();
        }
    }

    // Alternating slots gives 2 players = 1v1 and 4 players = 2v2.
    int TeamFor(ulong clientId)
    {
        var ids = manager.ConnectedClientsIds.OrderBy(id => id).ToList();
        int index = ids.IndexOf(clientId);
        return index < 0 ? 0 : index % 2;
    }

    void SendStateRequest(Vector3 position)
    {
        using var writer = new FastBufferWriter(sizeof(float) * 3, Allocator.Temp);
        writer.WriteValueSafe(position);
        manager.CustomMessagingManager.SendNamedMessage(StateRequestMessage, NetworkManager.ServerClientId, writer);
    }

    void ReceiveStateRequest(ulong senderClientId, FastBufferReader reader)
    {
        if (!manager.IsHost || senderClientId == manager.LocalClientId) return;
        reader.ReadValueSafe(out Vector3 position);
        BroadcastState(senderClientId, position);
    }

    void BroadcastState(ulong clientId, Vector3 position)
    {
        if (clientId != manager.LocalClientId)
            battle.ApplyRemotePlayerState(clientId, position, TeamFor(clientId));
        using var writer = new FastBufferWriter(sizeof(ulong) + sizeof(int) + sizeof(float) * 3, Allocator.Temp);
        writer.WriteValueSafe(clientId);
        writer.WriteValueSafe(TeamFor(clientId));
        writer.WriteValueSafe(position);
        SendToClients(StateBroadcastMessage, writer);
    }

    void ReceiveStateBroadcast(ulong senderClientId, FastBufferReader reader)
    {
        if (manager.IsHost || senderClientId != NetworkManager.ServerClientId) return;
        reader.ReadValueSafe(out ulong clientId);
        reader.ReadValueSafe(out int team);
        reader.ReadValueSafe(out Vector3 position);
        if (clientId != manager.LocalClientId) battle.ApplyRemotePlayerState(clientId, position, team);
    }

    void RequestPaint(Vector2 uv, int color, int ignoredOwner)
    {
        if (!teamAssigned) return;
        if (manager.IsHost)
        {
            ApplyAndBroadcastPaint(uv, color, localTeam);
            return;
        }
        using var writer = new FastBufferWriter(sizeof(float) * 2 + sizeof(int), Allocator.Temp);
        writer.WriteValueSafe(uv);
        writer.WriteValueSafe(color);
        manager.CustomMessagingManager.SendNamedMessage(PaintRequestMessage, NetworkManager.ServerClientId, writer);
    }

    void SendTeamAssignments()
    {
        foreach (ulong clientId in manager.ConnectedClientsIds)
        {
            if (clientId == manager.LocalClientId) continue;
            using var writer = new FastBufferWriter(sizeof(ulong) + sizeof(int), Allocator.Temp);
            writer.WriteValueSafe(clientId);
            writer.WriteValueSafe(TeamFor(clientId));
            manager.CustomMessagingManager.SendNamedMessage(TeamMessage, clientId, writer);
        }
    }

    void ReceiveTeam(ulong senderClientId, FastBufferReader reader)
    {
        if (manager.IsHost || senderClientId != NetworkManager.ServerClientId) return;
        reader.ReadValueSafe(out ulong clientId);
        reader.ReadValueSafe(out int team);
        if (clientId != manager.LocalClientId) return;
        localTeam = team;
        teamAssigned = true;
        battle.EnableMultiplayer(localTeam, false);
    }

    void ReceivePaintRequest(ulong senderClientId, FastBufferReader reader)
    {
        if (!manager.IsHost || senderClientId == manager.LocalClientId) return;
        reader.ReadValueSafe(out Vector2 uv);
        reader.ReadValueSafe(out int color);
        ApplyAndBroadcastPaint(uv, color, TeamFor(senderClientId));
    }

    void ApplyAndBroadcastPaint(Vector2 uv, int color, int ownerTeam)
    {
        battle.ApplyNetworkPaint(uv, color, ownerTeam);
        using var writer = new FastBufferWriter(sizeof(float) * 2 + sizeof(int) * 2, Allocator.Temp);
        writer.WriteValueSafe(uv);
        writer.WriteValueSafe(color);
        writer.WriteValueSafe(ownerTeam);
        SendToClients(PaintBroadcastMessage, writer);
    }

    void ReceivePaintBroadcast(ulong senderClientId, FastBufferReader reader)
    {
        if (manager.IsHost || senderClientId != NetworkManager.ServerClientId) return;
        reader.ReadValueSafe(out Vector2 uv);
        reader.ReadValueSafe(out int color);
        reader.ReadValueSafe(out int ownerTeam);
        battle.ApplyNetworkPaint(uv, color, ownerTeam);
    }

    void SendClock()
    {
        using var writer = new FastBufferWriter(sizeof(float) + 2, Allocator.Temp);
        writer.WriteValueSafe(battle.RemainingSeconds);
        writer.WriteValueSafe(battle.IsWallDown);
        writer.WriteValueSafe(battle.IsFinished);
        SendToClients(ClockMessage, writer);
    }

    void ReceiveClock(ulong senderClientId, FastBufferReader reader)
    {
        if (manager.IsHost || senderClientId != NetworkManager.ServerClientId) return;
        reader.ReadValueSafe(out float seconds);
        reader.ReadValueSafe(out bool wallDown);
        reader.ReadValueSafe(out bool finished);
        battle.ApplyNetworkClock(seconds, wallDown, finished);
    }

    void SendToClients(string message, FastBufferWriter writer)
    {
        foreach (ulong clientId in manager.ConnectedClientsIds)
            if (clientId != manager.LocalClientId)
                manager.CustomMessagingManager.SendNamedMessage(message, clientId, writer);
    }

    void OnDestroy()
    {
        if (battle != null) battle.OnMultiplayerPaintRequested -= RequestPaint;
        if (manager == null || manager.CustomMessagingManager == null) return;
        manager.CustomMessagingManager.UnregisterNamedMessageHandler(StateRequestMessage);
        manager.CustomMessagingManager.UnregisterNamedMessageHandler(StateBroadcastMessage);
        manager.CustomMessagingManager.UnregisterNamedMessageHandler(PaintRequestMessage);
        manager.CustomMessagingManager.UnregisterNamedMessageHandler(PaintBroadcastMessage);
        manager.CustomMessagingManager.UnregisterNamedMessageHandler(ClockMessage);
        manager.CustomMessagingManager.UnregisterNamedMessageHandler(TeamMessage);
    }
}
