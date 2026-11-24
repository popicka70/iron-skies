using System;
using System.Collections.Generic;
using Godot;

namespace IronSkies.Game;

/// <summary>Rebindable flight controls. Keys are stored by action name in settings.cfg.</summary>
public enum FlightAction
{
    PitchUp,
    PitchDown,
    RollLeft,
    RollRight,
    RudderLeft,
    RudderRight,
    ThrottleUp,
    ThrottleDown,
    Fire,
    Brake,
    Flaps,
    Gear,
    RadiatorUp,
    RadiatorDown,
    TrimUp,
    TrimDown,
    Camera,
    Extinguish,
    Reset,
}

public static class FlightInput
{
    private static readonly Dictionary<FlightAction, Key> Defaults = new()
    {
        [FlightAction.PitchUp] = Key.Up,
        [FlightAction.PitchDown] = Key.Down,
        [FlightAction.RollLeft] = Key.Left,
        [FlightAction.RollRight] = Key.Right,
        [FlightAction.RudderLeft] = Key.A,
        [FlightAction.RudderRight] = Key.D,
        [FlightAction.ThrottleUp] = Key.W,
        [FlightAction.ThrottleDown] = Key.S,
        [FlightAction.Fire] = Key.Space,
        [FlightAction.Brake] = Key.B,
        [FlightAction.Flaps] = Key.F,
        [FlightAction.Gear] = Key.G,
        [FlightAction.RadiatorUp] = Key.L,
        [FlightAction.RadiatorDown] = Key.K,
        [FlightAction.TrimUp] = Key.Period,
        [FlightAction.TrimDown] = Key.Comma,
        [FlightAction.Camera] = Key.C,
        [FlightAction.Extinguish] = Key.X,
        [FlightAction.Reset] = Key.R,
    };

    private static readonly Dictionary<FlightAction, Key> Keys = new();

    static FlightInput()
    {
        ResetDefaults();
    }

    public static IReadOnlyDictionary<FlightAction, Key> Bindings => Keys;

    public static Key Get(FlightAction action) =>
        Keys.TryGetValue(action, out Key key) ? key : Key.None;

    public static void Set(FlightAction action, Key key)
    {
        if (key != Key.None)
        {
            Keys[action] = key;
        }
    }

    public static FlightAction? Find(FlightAction except, Key key)
    {
        foreach ((FlightAction action, Key bound) in Keys)
        {
            if (action != except && bound == key)
            {
                return action;
            }
        }

        return null;
    }

    public static void ResetDefaults()
    {
        Keys.Clear();
        foreach ((FlightAction action, Key key) in Defaults)
        {
            Keys[action] = key;
        }
    }

    public static void Load(ConfigFile config)
    {
        ResetDefaults();
        foreach (FlightAction action in Defaults.Keys)
        {
            int code = (int)config.GetValue(
                "keys", action.ToString(), (int)Defaults[action]);
            if (Enum.IsDefined(typeof(Key), code))
            {
                Keys[action] = (Key)code;
            }
        }
    }

    public static void Save(ConfigFile config)
    {
        foreach ((FlightAction action, Key key) in Keys)
        {
            config.SetValue("keys", action.ToString(), (int)key);
        }
    }

    public static bool Pressed(FlightAction action) =>
        Input.IsPhysicalKeyPressed(Get(action));

    public static string KeyName(FlightAction action)
    {
        Key key = Get(action);
        string text = OS.GetKeycodeString(key);
        return string.IsNullOrEmpty(text) ? key.ToString() : text;
    }

    public static string Label(FlightAction action) => action switch
    {
        FlightAction.PitchUp => "Pitch up",
        FlightAction.PitchDown => "Pitch down",
        FlightAction.RollLeft => "Roll left",
        FlightAction.RollRight => "Roll right",
        FlightAction.RudderLeft => "Rudder left",
        FlightAction.RudderRight => "Rudder right",
        FlightAction.ThrottleUp => "Throttle up",
        FlightAction.ThrottleDown => "Throttle down",
        FlightAction.Fire => "Fire guns",
        FlightAction.Brake => "Wheel brakes",
        FlightAction.Flaps => "Flaps",
        FlightAction.Gear => "Landing gear",
        FlightAction.RadiatorUp => "Radiator open",
        FlightAction.RadiatorDown => "Radiator close",
        FlightAction.TrimUp => "Trim nose up",
        FlightAction.TrimDown => "Trim nose down",
        FlightAction.Camera => "Camera",
        FlightAction.Extinguish => "Extinguish fire",
        FlightAction.Reset => "Reset flight",
        _ => action.ToString(),
    };
}
