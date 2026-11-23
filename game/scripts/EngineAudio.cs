using System;
using Godot;
using IronSkies.FlightModel.Aircraft;

namespace IronSkies.Game;

/// <summary>
/// Procedural engine, propeller and wind sound. A V12 firing frequency and propeller
/// blade-pass tone are synthesised from RPM, with broadband wind noise from airspeed.
/// No sample libraries and no paid audio middleware.
/// </summary>
public sealed partial class EngineAudio : AudioStreamPlayer
{
    private const double MixRate = 44_100.0;

    private AudioStreamGeneratorPlayback? _playback;
    private double _phaseProp;
    private double _phaseFiring;
    private double _noiseFiltered;

    private double _rpmTarget;
    private double _throttleTarget;
    private double _speedTarget;
    private double _rpm;
    private double _throttle;
    private double _speed;

    private uint _noiseState = 0x9E3779B9u;

    public void Build()
    {
        Stream = new AudioStreamGenerator
        {
            MixRate = (float)MixRate,
            BufferLength = 0.15f,
        };
        VolumeDb = -5.0f;
        Play();
        _playback = GetStreamPlayback() as AudioStreamGeneratorPlayback;
        if (_playback is null)
        {
            GD.PushWarning("Audio generator unavailable; running silent.");
        }
    }

    public void UpdateFrom(AircraftSimulator sim, double throttle)
    {
        _rpmTarget = sim.EngineRpm;
        _throttleTarget = throttle;
        _speedTarget = sim.Telemetry.SpeedMs;
    }

    public override void _Process(double delta)
    {
        if (_playback is null)
        {
            return;
        }

        double blend = 1.0 - Math.Exp(-6.0 * delta);
        _rpm += (_rpmTarget - _rpm) * blend;
        _throttle += (_throttleTarget - _throttle) * blend;
        _speed += (_speedTarget - _speed) * blend;

        int frames = Math.Min(_playback.GetFramesAvailable(), 4096);
        if (frames <= 0)
        {
            return;
        }

        double propFrequency = Math.Max(10.0, _rpm / 60.0 * 3.0);
        double firingFrequency = Math.Max(15.0, _rpm / 60.0 * 6.0);
        double engineAmplitude = 0.04 + 0.24 * _throttle;
        double windAmplitude = 0.015 + 0.11 * Math.Min(1.0, _speed / 160.0);

        for (int i = 0; i < frames; i++)
        {
            _phaseProp = Fraction(_phaseProp + propFrequency / MixRate);
            _phaseFiring = Fraction(_phaseFiring + firingFrequency / MixRate);

            double propTone = Math.Sin(2.0 * Math.PI * _phaseProp) * 0.55
                + Math.Sin(4.0 * Math.PI * _phaseProp) * 0.22;
            double firingPulse = Math.Sin(2.0 * Math.PI * _phaseFiring) * 0.20
                + Math.Sin(4.0 * Math.PI * _phaseFiring) * 0.08;

            _noiseState ^= _noiseState << 13;
            _noiseState ^= _noiseState >> 17;
            _noiseState ^= _noiseState << 5;
            double white = _noiseState / (double)uint.MaxValue * 2.0 - 1.0;
            _noiseFiltered += (white - _noiseFiltered) * 0.06;

            double sample = (propTone + firingPulse) * engineAmplitude
                + _noiseFiltered * windAmplitude;
            sample = Math.Clamp(sample, -1.0, 1.0);

            _playback.PushFrame(new Vector2((float)sample, (float)sample));
        }
    }

    public override void _ExitTree()
    {
        Stop();
        _playback = null;
    }

    private static double Fraction(double value) => value - Math.Floor(value);
}
