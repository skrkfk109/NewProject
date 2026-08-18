using System.Collections;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>Synchronizes the two local battle simulations through the Lobby sample's Relay/NGO session.</summary>
public sealed class ColorClashNetworkBridge : MonoBehaviour
{
    const string StateMessage = "ColorClash/State";
    const string PaintMessage = "ColorClash/Paint";
    const string ClockMessage = "ColorClash/Clock";

    NetworkManager manager;
    BattlePrototypeController battle;
    float nextStateSend;
    float nextClockSend;
    bool connected;

    IEnumerator Start()
    {
        while (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsListening) yield return null;
        manager = NetworkManager.Singleton;
        battle = FindObjectOfType<BattlePrototypeController>();
        while (battle == null || !battle.IsBattleReady)
        {
            battle = FindObjectOfType<BattlePrototypeController>();
            yield return null;
        }

        manager.CustomMessagingManager.RegisterNamedMessageHandler(StateMessage, ReceiveState);
        manager.CustomMessagingManager.RegisterNamedMessageHandler(PaintMessage, ReceivePaint);
        manager.CustomMessagingManager.RegisterNamedMessageHandler(ClockMessage, ReceiveClock);
        battle.OnMultiplayerPaintRequested += RequestPaint;
        battle.EnableMultiplayer(manager.IsHost ? 0 : 1, manager.IsHost);
        connected = true;
    }

    void Update()
    {
        if (!connected || battle == null) return;
        if (Time.unscaledTime >= nextStateSend)
        {
            nextStateSend = Time.unscaledTime + .05f;
            SendState(battle.LocalPlayerPosition);
        }
        if (manager.IsHost && Time.unscaledTime >= nextClockSend)
        {
            nextClockSend = Time.unscaledTime + .15f;
            SendClock();
        }
    }

    void SendState(Vector3 position)
    {
        using (var writer = new FastBufferWriter(sizeof(float) * 3, Allocator.Temp))
        {
            writer.WriteValueSafe(position);
            if (manager.IsHost) SendToClients(StateMessage, writer);
            else manager.CustomMessagingManager.SendNamedMessage(StateMessage, NetworkManager.ServerClientId, writer);
        }
    }

    void ReceiveState(ulong senderClientId, FastBufferReader reader)
    {
        reader.ReadValueSafe(out Vector3 position);
        if (manager.IsHost && senderClientId != manager.LocalClientId) battle.ApplyRemotePlayerState(position);
        else if (!manager.IsHost) battle.ApplyRemotePlayerState(position);
    }

    void RequestPaint(Vector2 uv, int color, int owner)
    {
        if (manager.IsHost)
        {
            battle.ApplyNetworkPaint(uv, color, owner);
            SendPaint(uv, color, owner);
            return;
        }
        using (var writer = new FastBufferWriter(sizeof(float) * 2 + sizeof(int) * 2, Allocator.Temp))
        {
            writer.WriteValueSafe(uv);
            writer.WriteValueSafe(color);
            writer.WriteValueSafe(owner);
            manager.CustomMessagingManager.SendNamedMessage(PaintMessage, NetworkManager.ServerClientId, writer);
        }
    }

    void ReceivePaint(ulong senderClientId, FastBufferReader reader)
    {
        reader.ReadValueSafe(out Vector2 uv);
        reader.ReadValueSafe(out int color);
        reader.ReadValueSafe(out int owner);
        if (!manager.IsHost)
        {
            battle.ApplyNetworkPaint(uv, color, owner);
            return;
        }
        if (senderClientId == manager.LocalClientId) return;
        battle.ApplyNetworkPaint(uv, color, owner);
        SendPaint(uv, color, owner);
    }

    void SendPaint(Vector2 uv, int color, int owner)
    {
        using (var writer = new FastBufferWriter(sizeof(float) * 2 + sizeof(int) * 2, Allocator.Temp))
        {
            writer.WriteValueSafe(uv);
            writer.WriteValueSafe(color);
            writer.WriteValueSafe(owner);
            SendToClients(PaintMessage, writer);
        }
    }

    void SendClock()
    {
        using (var writer = new FastBufferWriter(sizeof(float) + 2, Allocator.Temp))
        {
            writer.WriteValueSafe(battle.RemainingSeconds);
            writer.WriteValueSafe(battle.IsWallDown);
            writer.WriteValueSafe(battle.IsFinished);
            SendToClients(ClockMessage, writer);
        }
    }

    void ReceiveClock(ulong senderClientId, FastBufferReader reader)
    {
        if (manager.IsHost) return;
        reader.ReadValueSafe(out float seconds);
        reader.ReadValueSafe(out bool wallDown);
        reader.ReadValueSafe(out bool finished);
        battle.ApplyNetworkClock(seconds, wallDown, finished);
    }

    void SendToClients(string message, FastBufferWriter writer)
    {
        foreach (ulong clientId in manager.ConnectedClientsIds)
            if (clientId != manager.LocalClientId) manager.CustomMessagingManager.SendNamedMessage(message, clientId, writer);
    }

    void OnDestroy()
    {
        if (battle != null) battle.OnMultiplayerPaintRequested -= RequestPaint;
        if (manager == null || manager.CustomMessagingManager == null) return;
        manager.CustomMessagingManager.UnregisterNamedMessageHandler(StateMessage);
        manager.CustomMessagingManager.UnregisterNamedMessageHandler(PaintMessage);
        manager.CustomMessagingManager.UnregisterNamedMessageHandler(ClockMessage);
    }
}
