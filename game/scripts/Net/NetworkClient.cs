using System;
using Godot;
using IronSkies.FlightModel.Aircraft;
using IronSkies.FlightModel.Net;

namespace IronSkies.Game.Net;

/// <summary>
/// Thin ENet client: sends input frames at 30 Hz and raises decoded welcome/snapshot
/// events. Prediction, interpolation and presentation live in the scene.
/// </summary>
public sealed partial class NetworkClient : Node
{
    public event Action<byte, NetPlayerState>? WelcomeReceived;
    public event Action<byte[]>? SnapshotPacketReceived;

    public bool Connected { get; private set; }
    public bool Failed { get; private set; }
    public string Address { get; private set; } = "";
    public int Port { get; private set; }

    private SceneMultiplayer _multiplayer = null!;
    private uint _sequence;
    private double _sendTimer;
    private double _timeSinceSend;

    public void Start(string address, int port)
    {
        Address = address;
        Port = port;

        var peer = new ENetMultiplayerPeer();
        Error error = peer.CreateClient(address, port);
        if (error != Error.Ok)
        {
            Failed = true;
            GD.PushError($"Could not connect to {address}:{port}: {error}");
            return;
        }

        Multiplayer.MultiplayerPeer = peer;
        _multiplayer = (SceneMultiplayer)Multiplayer;
        _multiplayer.ConnectedToServer += OnConnected;
        _multiplayer.ConnectionFailed += OnConnectionFailed;
        _multiplayer.ServerDisconnected += OnServerDisconnected;
        _multiplayer.PeerPacket += OnPeerPacket;

        GD.Print($"Connecting to {address}:{port} ...");
    }

    public override void _ExitTree()
    {
        Multiplayer.MultiplayerPeer?.Close();
    }

    public void SendControls(Controls controls, double delta, uint ackTick)
    {
        _sendTimer += delta;
        if (!Connected || _sendTimer < 1.0 / 30.0)
        {
            return;
        }

        _sendTimer = 0.0;
        var frame = new NetControlFrame(++_sequence, ackTick, controls);
        byte[] data = NetProtocol.EncodeInput(frame);
        _multiplayer.SendBytes(data, 1, MultiplayerPeer.TransferModeEnum.Unreliable, 1);
    }

    private void OnConnected()
    {
        Connected = true;
        GD.Print($"Connected to {Address}:{Port} as peer {_multiplayer.GetUniqueId()}");
    }

    private void OnConnectionFailed()
    {
        Failed = true;
        GD.PushError($"Connection to {Address}:{Port} failed");
    }

    private void OnServerDisconnected()
    {
        Connected = false;
        GD.Print("Disconnected from server");
    }

    private void OnPeerPacket(long peerId, byte[] packet)
    {
        if (packet.Length == 0)
        {
            return;
        }

        switch (packet[0])
        {
            case NetProtocol.TypeWelcome when NetProtocol.TryDecodeWelcome(
                packet, out byte playerId, out NetPlayerState state):
                WelcomeReceived?.Invoke(playerId, state);
                break;

            case NetProtocol.TypeSnapshot:
            case NetProtocol.TypeSnapshotDelta:
                SnapshotPacketReceived?.Invoke(packet);
                break;
        }
    }
}
