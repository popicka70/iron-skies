using System;
using Godot;
using IronSkies.FlightModel.Net;

namespace IronSkies.Game.Net;

/// <summary>
/// Answers LAN discovery queries so the launcher's server browser can list this server.
/// It binds a separate UDP port and replies unicast to whoever sent the query.
/// </summary>
public sealed partial class ServerBeacon : Node
{
    private readonly PacketPeerUdp _socket = new();
    private readonly string _name;
    private readonly Func<int> _players;
    private readonly int _gamePort;
    private readonly int _discoveryPort;
    private readonly bool _hasPassword;
    private bool _enabled;

    public ServerBeacon(
        string name, int gamePort, Func<int> players, int discoveryPort, bool hasPassword)
    {
        _name = name;
        _gamePort = gamePort;
        _players = players;
        _discoveryPort = discoveryPort;
        _hasPassword = hasPassword;
    }

    public bool Start()
    {
        if (_discoveryPort <= 0)
        {
            return false;
        }

        Error error = _socket.Bind(_discoveryPort);
        if (error != Error.Ok)
        {
            GD.PushWarning(
                $"LAN discovery disabled: could not bind UDP {_discoveryPort} ({error})");
            return false;
        }

        _enabled = true;
        GD.Print($"LAN discovery replying on UDP {_discoveryPort} as \"{_name}\"");
        return true;
    }

    public override void _Process(double delta)
    {
        if (!_enabled)
        {
            return;
        }

        // Bound the work per frame so a query flood cannot stall the server.
        for (int i = 0; i < 32 && _socket.GetAvailablePacketCount() > 0; i++)
        {
            byte[] packet = _socket.GetPacket();
            if (!DiscoveryProtocol.IsQuery(packet))
            {
                continue;
            }

            string address = _socket.GetPacketIP();
            int port = _socket.GetPacketPort();
            if (string.IsNullOrEmpty(address) || port <= 0)
            {
                continue;
            }

            _socket.SetDestAddress(address, port);
            _socket.PutPacket(DiscoveryProtocol.EncodeResponse(
                _name, _gamePort, _players(), NetworkServer.MaxPlayers, _hasPassword));
        }
    }

    public override void _ExitTree()
    {
        if (_enabled)
        {
            _socket.Close();
            _enabled = false;
        }
    }
}
