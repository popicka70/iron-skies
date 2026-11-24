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
    public const int MaxPlayers = 64;
    private const double RelevanceMeters = 15_000.0;

    private sealed class ServerPlayer
    {
        public required byte Id { get; init; }
        public required AircraftSimulator Sim { get; set; }
        public readonly Dictionary<byte, DamageTarget> Targets = new();
        public Controls Controls = new(GearDown: 0.0, Throttle: 0.6, Mixture: 0.9);
        public uint LastSequence;
        public uint LastAckTick;
        public readonly Dictionary<uint, NetSnapshot> History = new();
        public readonly PoseHistory Poses = new(512);
        public double LagTicks;
        public int LagSteps;
        public bool HasAck;
        public double DeadSeconds;
    }

    private readonly Dictionary<long, ServerPlayer> _players = new();
    private readonly Dictionary<byte, ServerPlayer> _playersById = new();
    private Heightmap _map = null!;
    private AircraftDefinition _definition = null!;
    private SceneMultiplayer _multiplayer = null!;
    private double _accumulator;
    private uint _tick;
    private double _statsTimer;
    private double _bytesThisSecond;
    private long _fullSent;
    private long _deltaSent;
    private bool _hosted;
    private double _emptySeconds;
    private double _tickMsAccum;
    private int _tickSamples;
    private long _simStep;
    private readonly System.Diagnostics.Stopwatch _tickTimer = new();

    public void Start(
        int port, Heightmap map, AircraftDefinition definition, bool hosted = false,
        string name = "Iron Skies Server", int discoveryPort = DiscoveryProtocol.DefaultPort)
    {
        _map = map;
        _definition = definition;
        _hosted = hosted;

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

        var beacon = new ServerBeacon(name, port, () => _players.Count, discoveryPort);
        AddChild(beacon);
        beacon.Start();
    }

    public override void _ExitTree()
    {
        Multiplayer.MultiplayerPeer?.Close();
    }

    public override void _Process(double delta)
    {
        if (_multiplayer is null)
        {
            return;
        }

        if (_players.Count == 0)
        {
            if (_hosted)
            {
                _emptySeconds += delta;
                if (_emptySeconds > 8.0)
                {
                    GD.Print("Host server idle; exiting");
                    GetTree().Quit();
                }
            }

            return;
        }

        _emptySeconds = 0.0;
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
            double lagTicks = 0.0;
            foreach (ServerPlayer player in _players.Values)
            {
                lagTicks += player.LagTicks;
            }

            lagTicks = _players.Count > 0 ? lagTicks / _players.Count : 0.0;
            GD.Print($"[server] players={_players.Count} tick={_tick} " +
                     $"outKBps={_bytesThisSecond / 1024.0:F1} avgTick={averageTick:F2}ms " +
                     $"full={_fullSent} delta={_deltaSent} lag={lagTicks:F1}t");
            _bytesThisSecond = 0.0;
            _fullSent = 0;
            _deltaSent = 0;
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

        byte playerId = 0;
        for (byte candidate = 1; candidate <= MaxPlayers; candidate++)
        {
            bool used = false;
            foreach (ServerPlayer existing in _players.Values)
            {
                if (existing.Id == candidate)
                {
                    used = true;
                    break;
                }
            }

            if (!used)
            {
                playerId = candidate;
                break;
            }
        }

        if (playerId == 0)
        {
            GD.Print($"Rejecting peer {peerId}: no player slot");
            _multiplayer.DisconnectPeer((int)peerId);
            return;
        }

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
        _playersById[playerId] = player;

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

        _playersById.Remove(player.Id);

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
        if (frame.AckTick != 0 && frame.AckTick <= _tick)
        {
            player.LastAckTick = frame.AckTick;

            // The client renders remotes roughly one snapshot behind the newest
            // applied snapshot, so rewind by that plus its network delay.
            double sample = System.Math.Clamp(
                _tick - frame.AckTick + 1.0, 0.0, 20.0);
            player.LagTicks = player.HasAck
                ? player.LagTicks * 0.75 + sample * 0.25
                : sample;
            player.HasAck = true;
        }
    }

    private void ServerTick(float dt)
    {
        _tickTimer.Restart();

        foreach (ServerPlayer player in _players.Values)
        {
            player.LagSteps = player.HasAck
                ? (int)System.Math.Round(
                    System.Math.Clamp(player.LagTicks, 0.0, 20.0) * SimStepsPerTick)
                : 0;
        }

        for (int step = 0; step < SimStepsPerTick; step++)
        {
            long index = _simStep;
            foreach (ServerPlayer player in _players.Values)
            {
                player.Poses.Add(index, player.Sim.State.Position);
            }

            foreach (ServerPlayer player in _players.Values)
            {
                foreach ((byte victimId, DamageTarget target) in player.Targets)
                {
                    if (!_playersById.TryGetValue(victimId, out ServerPlayer? victim))
                    {
                        continue;
                    }

                    // Lag-compensated hitboxes: projectiles are tested against the
                    // pose the shooter saw, not the live authoritative pose.
                    target.Position = player.LagSteps > 0
                        && victim.Poses.TryGet(index - player.LagSteps, out Vec3 historical)
                            ? historical
                            : victim.Sim.State.Position;
                }

                player.Sim.Step(player.Controls, 1.0 / 240.0);
            }

            _simStep++;
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
        player.Poses.Clear();
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

            var snapshot = new NetSnapshot
            {
                Tick = _tick,
                SelfId = recipient.Id,
                Anchor = recipient.Sim.State.Position,
                Players = states.ToArray(),
            };

            // Acknowledge-based delta: the client tells us the newest snapshot it has.
            // Send a full keyframe when it has none, every second, or when the acked
            // snapshot is no longer in our history.
            NetSnapshot? baseline = null;
            bool hasBaseline = recipient.LastAckTick != 0
                && _tick % 30 != 0
                && recipient.History.TryGetValue(recipient.LastAckTick, out baseline);
            byte[] data = hasBaseline
                ? NetProtocol.EncodeSnapshotDelta(snapshot, baseline!)
                : NetProtocol.EncodeSnapshot(snapshot);

            _multiplayer.SendBytes(
                data, (int)peerId, MultiplayerPeer.TransferModeEnum.Unreliable, 1);
            _bytesThisSecond += data.Length;
            if (hasBaseline)
            {
                _deltaSent++;
            }
            else
            {
                _fullSent++;
            }

            recipient.History[_tick] = snapshot;
            if (recipient.History.Count > 90)
            {
                uint oldest = _tick > 90 ? _tick - 90 : 0;
                var stale = new List<uint>();
                foreach (uint tick in recipient.History.Keys)
                {
                    if (tick < oldest)
                    {
                        stale.Add(tick);
                    }
                }

                foreach (uint tick in stale)
                {
                    recipient.History.Remove(tick);
                }
            }
        }
    }
}
