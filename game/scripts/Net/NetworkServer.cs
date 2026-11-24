using System.Collections.Generic;
using Godot;
using IronSkies.FlightModel.Aircraft;
using IronSkies.FlightModel.Combat;
using IronSkies.FlightModel.Math;
using IronSkies.FlightModel.Net;
using IronSkies.Game.World;

namespace IronSkies.Game.Net;

/// <summary>
/// Authoritative headless server. It owns one <see cref="AircraftSimulator"/> per client,
/// applies their latest inputs at 240 Hz, resolves projectiles/damage, and broadcasts
/// 30 Hz state snapshots over ENet.
/// </summary>
public sealed partial class NetworkServer : Node
{
    private const double NetworkTick = 1.0 / 30.0;
    private const int SimStepsPerTick = 8; // 240 Hz / 30 Hz
    private const int MaxPlayers = 16;
    private const double RelevanceMeters = 15_000.0;

    private sealed class ServerPlayer
    {
        public required byte Id { get; init; }
        public required AircraftSimulator Sim { get; set; }
        public readonly Dictionary<byte, DamageTarget> Targets = new();
        public Controls Controls = new(GearDown: 0.0, Throttle: 0.6, Mixture: 0.9);
        public uint LastSequence;
        public double DeadSeconds;
    }

    private readonly Dictionary<long, ServerPlayer> _players = new();
    private Heightmap _map = null!;
    private AircraftDefinition _definition = null!;
    private SceneMultiplayer _multiplayer = null!;
    private double _accumulator;
    private uint _tick;
    private int _nextPlayerId = 1;
    private double _statsTimer;
    private double _bytesThisSecond;
    private double _tickMsAccum;
    private int _tickSamples;
    private readonly System.Diagnostics.Stopwatch _tickTimer = new();

    public void Start(int port, Heightmap map, AircraftDefinition definition)
    {
        _map = map;
        _definition = definition;

        var peer = new ENetMultiplayerPeer();
        Error error = peer.CreateServer(port, MaxPlayers);
        if (error != Error.Ok)
        {
            GD.PushError($"Could not listen on port {port}: {error}");
            return;
        }

        Multiplayer.MultiplayerPeer = peer;
        _multiplayer = (SceneMultiplayer)Multiplayer;
        _multiplayer.PeerConnected += OnPeerConnected;
        _multiplayer.PeerDisconnected += OnPeerDisconnected;
        _multiplayer.PeerPacket += OnPeerPacket;

        GD.Print($"Iron Skies server listening on UDP {port} (max {MaxPlayers} players)");
    }

    public override void _ExitTree()
    {
        Multiplayer.MultiplayerPeer?.Close();
    }

    public override void _Process(double delta)
    {
        if (_multiplayer is null || _players.Count == 0)
        {
            return;
        }

        _accumulator += delta;
        int guard = 0;
        while (_accumulator >= NetworkTick && guard++ < 5)
        {
            ServerTick((float)NetworkTick);
            _accumulator -= NetworkTick;
        }

        _statsTimer += delta;
        if (_statsTimer >= 1.0 && _players.Count > 0)
        {
            _statsTimer = 0.0;
            double averageTick = _tickSamples > 0 ? _tickMsAccum / _tickSamples : 0.0;
            GD.Print($"[server] players={_players.Count} tick={_tick} " +
                     $"outKBps={_bytesThisSecond / 1024.0:F1} avgTick={averageTick:F2}ms");
            _bytesThisSecond = 0.0;
            _tickMsAccum = 0.0;
            _tickSamples = 0;
        }
    }

    private void OnPeerConnected(long peerId)
    {
        if (_players.Count >= MaxPlayers)
        {
            GD.Print($"Rejecting peer {peerId}: server full");
            _multiplayer.DisconnectPeer((int)peerId);
            return;
        }

        byte playerId = (byte)_nextPlayerId;
        _nextPlayerId++;

        AircraftState spawn = SpawnState(playerId);
        var sim = new AircraftSimulator(
            _definition, spawn, _map, (ulong)(0x9E3779B9u ^ (uint)playerId * 7919u));
        var player = new ServerPlayer { Id = playerId, Sim = sim };

        foreach (ServerPlayer other in _players.Values)
        {
            AttachTarget(player, other);
            AttachTarget(other, player);
        }

        _players[peerId] = player;

        NetPlayerState state = NetPlayerState.FromSimulator(playerId, sim);
        byte[] welcome = NetProtocol.EncodeWelcome(playerId, state);
        _multiplayer.SendBytes(welcome, (int)peerId,
            MultiplayerPeer.TransferModeEnum.Reliable, 0);

        GD.Print($"Player {playerId} joined (peer {peerId}); {_players.Count} online");
        Broadcast();
    }

    private void OnPeerDisconnected(long peerId)
    {
        if (!_players.Remove(peerId, out ServerPlayer? player))
        {
            return;
        }

        foreach (ServerPlayer other in _players.Values)
        {
            if (other.Targets.TryGetValue(player.Id, out DamageTarget? target))
            {
                target.Health = 0.0;
            }
        }

        GD.Print($"Player {player.Id} left; {_players.Count} online");
        Broadcast();
    }

    private void OnPeerPacket(long peerId, byte[] packet)
    {
        if (!_players.TryGetValue(peerId, out ServerPlayer? player))
        {
            return;
        }

        if (!NetProtocol.TryDecodeInput(packet, out NetControlFrame frame))
        {
            return;
        }

        if (frame.Sequence <= player.LastSequence)
        {
            return;
        }

        player.LastSequence = frame.Sequence;
        player.Controls = frame.Controls;
    }

    private void ServerTick(float dt)
    {
        _tickTimer.Restart();
        for (int step = 0; step < SimStepsPerTick; step++)
        {
            foreach (ServerPlayer player in _players.Values)
            {
                foreach ((byte victimId, DamageTarget target) in player.Targets)
                {
                    if (FindPlayer(victimId) is { } victim)
                    {
                        target.Position = victim.Sim.State.Position;
                    }
                }

                player.Sim.Step(player.Controls, 1.0 / 240.0);
            }
        }

        foreach (ServerPlayer victim in _players.Values)
        {
            double damage = 0.0;
            foreach (ServerPlayer attacker in _players.Values)
            {
                if (attacker.Id == victim.Id)
                {
                    continue;
                }

                if (attacker.Targets.TryGetValue(victim.Id, out DamageTarget? target))
                {
                    damage += target.MaxHealth - target.Health;
                }
            }

            victim.Sim.Health = DeterministicMath.Clamp(
                _definition.StructuralHealth - damage, 0.0,
                _definition.StructuralHealth);

            if (victim.Sim.Destroyed)
            {
                victim.DeadSeconds += dt;
                if (victim.DeadSeconds > 5.0)
                {
                    Respawn(victim);
                }
            }
        }

        _tick++;
        Broadcast();
        _tickTimer.Stop();
        _tickMsAccum += _tickTimer.Elapsed.TotalMilliseconds;
        _tickSamples++;
    }

    private void Respawn(ServerPlayer player)
    {
        AircraftState spawn = SpawnState(player.Id);
        var newSim = new AircraftSimulator(
            _definition, spawn, _map, (ulong)(0xA5A5A5A5u ^ (uint)player.Id * 7919u));
        foreach (DamageTarget target in player.Targets.Values)
        {
            target.Health = target.MaxHealth;
            newSim.AddTarget(target);
        }

        player.Sim = newSim;
        player.DeadSeconds = 0.0;
        player.Controls = new Controls(GearDown: 0.0, Throttle: 0.6, Mixture: 0.9);
        foreach (ServerPlayer other in _players.Values)
        {
            if (other.Targets.TryGetValue(player.Id, out DamageTarget? target))
            {
                target.Health = target.MaxHealth;
                target.Position = spawn.Position;
            }
        }

        GD.Print($"Player {player.Id} respawned");
    }

    private void AttachTarget(ServerPlayer owner, ServerPlayer victim)
    {
        var target = new DamageTarget
        {
            Position = victim.Sim.State.Position,
            Radius = 6.0,
            MaxHealth = _definition.StructuralHealth,
            Health = _definition.StructuralHealth,
            Static = false,
        };
        owner.Targets[victim.Id] = target;
        owner.Sim.AddTarget(target);
    }

    private ServerPlayer? FindPlayer(byte id)
    {
        foreach (ServerPlayer player in _players.Values)
        {
            if (player.Id == id)
            {
                return player;
            }
        }

        return null;
    }

    private AircraftState SpawnState(byte playerId)
    {
        (double east, double north, double heading)[] spawns =
        {
            (0.0, 5200.0, 0.0),
            (0.0, 6800.0, DeterministicMath.Pi),
            (-700.0, 6000.0, DeterministicMath.HalfPi),
            (700.0, 6000.0, -DeterministicMath.HalfPi),
        };

        double east;
        double north;
        double heading;
        if (playerId <= spawns.Length)
        {
            (east, north, heading) = spawns[playerId - 1];
        }
        else
        {
            double angle = (playerId - spawns.Length - 1)
                * DeterministicMath.TwoPi / (MaxPlayers - spawns.Length);
            east = 1200.0 * DeterministicMath.Sin(angle);
            north = 6000.0 + 1200.0 * DeterministicMath.Cos(angle);
            heading = angle + DeterministicMath.Pi;
        }

        double ground = _map.HeightAt(east, north);
        return AircraftState.CreateAirborne(
            _definition, ground + 1500.0, 120.0, heading, east, north);
    }

    private void Broadcast()
    {
        if (_players.Count == 0)
        {
            return;
        }

        // Interest management: each client receives itself plus aircraft within range.
        foreach ((long peerId, ServerPlayer recipient) in _players)
        {
            var states = new List<NetPlayerState>(_players.Count);
            foreach (ServerPlayer candidate in _players.Values)
            {
                bool self = candidate.Id == recipient.Id;
                bool inRange = (candidate.Sim.State.Position - recipient.Sim.State.Position)
                    .Length <= RelevanceMeters;
                if (self || inRange)
                {
                    states.Add(NetPlayerState.FromSimulator(candidate.Id, candidate.Sim));
                }
            }

            var snapshot = new NetSnapshot { Tick = _tick, Players = states.ToArray() };
            byte[] data = NetProtocol.EncodeSnapshot(snapshot);
            _multiplayer.SendBytes(
                data, (int)peerId, MultiplayerPeer.TransferModeEnum.Unreliable, 1);
            _bytesThisSecond += data.Length;
        }
    }
}
