using System;
using System.Collections.Generic;
using Godot;
using IronSkies.FlightModel.Input;

namespace IronSkies.Game;

public enum HotasAxis
{
    Pitch,
    Roll,
    Yaw,
    Throttle,
    Radiator,
    TrimElevator,
    Mixture,
    Brake,
}

public enum HotasButton
{
    Fire,
    Brake,
    Flaps,
    Gear,
    Camera,
    ThrottleUp,
    ThrottleDown,
    RadiatorUp,
    RadiatorDown,
    TrimUp,
    TrimDown,
    Extinguish,
    Reset,
}

public sealed class HotasAxisBinding
{
    public bool Bound;
    public string DeviceGuid = "";
    public string DeviceName = "";
    public int DeviceIndex = -1;
    public int AxisIndex;
    public bool Invert;
    public double Min = -1.0;
    public double Max = 1.0;
    public bool Unipolar;
}

public sealed class HotasButtonBinding
{
    public bool Bound;
    public string DeviceGuid = "";
    public string DeviceName = "";
    public int DeviceIndex = -1;
    public int ButtonIndex;
}

public readonly record struct HotasDevice(int Index, string Name, string Guid);

/// <summary>
/// Multi-device HOTAS layer. Every action can be bound to any axis/button on any
/// connected device; devices are resolved by SDL GUID so indexes may change.
/// </summary>
public static class HotasInput
{
    private static readonly Dictionary<HotasAxis, HotasAxisBinding> Axes = new();
    private static readonly Dictionary<HotasButton, HotasButtonBinding> Buttons = new();
    private static readonly Dictionary<HotasAxis, double> Values = new();
    private static readonly Dictionary<HotasButton, bool> PressedStates = new();
    private static readonly Dictionary<HotasButton, bool> PreviousStates = new();
    private static readonly Dictionary<HotasButton, bool> JustPressedStates = new();

    static HotasInput() => ResetAll();

    public static IReadOnlyDictionary<HotasAxis, HotasAxisBinding> AxisBindings => Axes;
    public static IReadOnlyDictionary<HotasButton, HotasButtonBinding> ButtonBindings => Buttons;

    public static bool IsUnipolar(HotasAxis action) => action switch
    {
        HotasAxis.Throttle => true,
        HotasAxis.Radiator => true,
        HotasAxis.Mixture => true,
        HotasAxis.Brake => true,
        _ => false,
    };

    public static string AxisLabel(HotasAxis action) => action switch
    {
        HotasAxis.Pitch => "Pitch",
        HotasAxis.Roll => "Roll",
        HotasAxis.Yaw => "Yaw",
        HotasAxis.Throttle => "Throttle",
        HotasAxis.Radiator => "Radiator",
        HotasAxis.TrimElevator => "Pitch trim",
        HotasAxis.Mixture => "Mixture",
        HotasAxis.Brake => "Brake axis",
        _ => action.ToString(),
    };

    public static string ButtonLabel(HotasButton action) => action switch
    {
        HotasButton.Fire => "Fire guns",
        HotasButton.Brake => "Wheel brakes",
        HotasButton.Flaps => "Flaps",
        HotasButton.Gear => "Landing gear",
        HotasButton.Camera => "Camera",
        HotasButton.ThrottleUp => "Throttle up",
        HotasButton.ThrottleDown => "Throttle down",
        HotasButton.RadiatorUp => "Radiator open",
        HotasButton.RadiatorDown => "Radiator close",
        HotasButton.TrimUp => "Trim nose up",
        HotasButton.TrimDown => "Trim nose down",
        HotasButton.Extinguish => "Extinguish fire",
        HotasButton.Reset => "Reset flight",
        _ => action.ToString(),
    };

    public static void ResetAll()
    {
        Axes.Clear();
        Buttons.Clear();
        Values.Clear();
        PressedStates.Clear();
        PreviousStates.Clear();
        JustPressedStates.Clear();

        foreach (HotasAxis action in Enum.GetValues<HotasAxis>())
        {
            Axes[action] = new HotasAxisBinding { Unipolar = IsUnipolar(action) };
            Values[action] = 0.0;
        }

        foreach (HotasButton action in Enum.GetValues<HotasButton>())
        {
            Buttons[action] = new HotasButtonBinding();
            PressedStates[action] = false;
            PreviousStates[action] = false;
            JustPressedStates[action] = false;
        }
    }

    public static void Load(ConfigFile config)
    {
        ResetAll();
        foreach (HotasAxis action in Enum.GetValues<HotasAxis>())
        {
            string section = $"hotas_axis_{action}";
            if (!(bool)config.GetValue(section, "bound", false))
            {
                continue;
            }

            HotasAxisBinding binding = Axes[action];
            binding.Bound = true;
            binding.DeviceGuid = (string)config.GetValue(section, "guid", "");
            binding.DeviceName = (string)config.GetValue(section, "name", "");
            binding.DeviceIndex = (int)config.GetValue(section, "device", -1);
            binding.AxisIndex = (int)config.GetValue(section, "axis", 0);
            binding.Invert = (bool)config.GetValue(section, "invert", false);
            binding.Min = (double)config.GetValue(section, "min", -1.0);
            binding.Max = (double)config.GetValue(section, "max", 1.0);
            binding.Unipolar = (bool)config.GetValue(
                section, "unipolar", IsUnipolar(action));
        }

        foreach (HotasButton action in Enum.GetValues<HotasButton>())
        {
            string section = $"hotas_button_{action}";
            if (!(bool)config.GetValue(section, "bound", false))
            {
                continue;
            }

            HotasButtonBinding binding = Buttons[action];
            binding.Bound = true;
            binding.DeviceGuid = (string)config.GetValue(section, "guid", "");
            binding.DeviceName = (string)config.GetValue(section, "name", "");
            binding.DeviceIndex = (int)config.GetValue(section, "device", -1);
            binding.ButtonIndex = (int)config.GetValue(section, "button", 0);
        }
    }

    public static void Save(ConfigFile config)
    {
        foreach ((HotasAxis action, HotasAxisBinding binding) in Axes)
        {
            string section = $"hotas_axis_{action}";
            config.SetValue(section, "bound", binding.Bound);
            if (!binding.Bound)
            {
                continue;
            }

            config.SetValue(section, "guid", binding.DeviceGuid);
            config.SetValue(section, "name", binding.DeviceName);
            config.SetValue(section, "device", binding.DeviceIndex);
            config.SetValue(section, "axis", binding.AxisIndex);
            config.SetValue(section, "invert", binding.Invert);
            config.SetValue(section, "min", binding.Min);
            config.SetValue(section, "max", binding.Max);
            config.SetValue(section, "unipolar", binding.Unipolar);
        }

        foreach ((HotasButton action, HotasButtonBinding binding) in Buttons)
        {
            string section = $"hotas_button_{action}";
            config.SetValue(section, "bound", binding.Bound);
            if (!binding.Bound)
            {
                continue;
            }

            config.SetValue(section, "guid", binding.DeviceGuid);
            config.SetValue(section, "name", binding.DeviceName);
            config.SetValue(section, "device", binding.DeviceIndex);
            config.SetValue(section, "button", binding.ButtonIndex);
        }
    }

    public static void Update()
    {
        foreach ((HotasAxis action, HotasAxisBinding binding) in Axes)
        {
            if (!binding.Bound)
            {
                Values[action] = 0.0;
                continue;
            }

            int device = Resolve(binding.DeviceGuid, binding.DeviceName, binding.DeviceIndex);
            binding.DeviceIndex = device;
            Values[action] = device >= 0
                ? AxisMapper.Map(
                    Input.GetJoyAxis(device, (JoyAxis)binding.AxisIndex),
                    binding.Min, binding.Max, binding.Invert, binding.Unipolar,
                    GameSettings.HotasDeadzone)
                : 0.0;
        }

        foreach ((HotasButton action, HotasButtonBinding binding) in Buttons)
        {
            bool pressed = false;
            if (binding.Bound)
            {
                int device = Resolve(binding.DeviceGuid, binding.DeviceName, binding.DeviceIndex);
                binding.DeviceIndex = device;
                pressed = device >= 0
                    && Input.IsJoyButtonPressed(device, (JoyButton)binding.ButtonIndex);
            }

            JustPressedStates[action] = pressed && !PreviousStates[action];
            PreviousStates[action] = pressed;
            PressedStates[action] = pressed;
        }
    }

    public static double GetAxis(HotasAxis action) =>
        Values.TryGetValue(action, out double value) ? value : 0.0;

    public static bool HasAxis(HotasAxis action) => Axes[action].Bound;

    public static bool IsAxisAvailable(HotasAxis action) =>
        Axes[action].Bound
        && Resolve(Axes[action].DeviceGuid, Axes[action].DeviceName,
            Axes[action].DeviceIndex) >= 0;

    public static bool Pressed(HotasButton action) =>
        PressedStates.TryGetValue(action, out bool pressed) && pressed;

    public static bool JustPressed(HotasButton action) =>
        JustPressedStates.TryGetValue(action, out bool pressed) && pressed;

    public static bool BindAxis(HotasAxis action, int device, JoyAxis axis)
    {
        HotasAxisBinding binding = Axes[action];
        binding.Bound = true;
        binding.DeviceIndex = device;
        binding.DeviceGuid = Input.GetJoyGuid(device);
        binding.DeviceName = Input.GetJoyName(device);
        binding.AxisIndex = (int)axis;
        binding.Invert = false;
        binding.Min = -1.0;
        binding.Max = 1.0;
        binding.Unipolar = IsUnipolar(action);
        return true;
    }

    public static void BindButton(HotasButton action, int device, JoyButton button)
    {
        HotasButtonBinding binding = Buttons[action];
        binding.Bound = true;
        binding.DeviceIndex = device;
        binding.DeviceGuid = Input.GetJoyGuid(device);
        binding.DeviceName = Input.GetJoyName(device);
        binding.ButtonIndex = (int)button;
    }

    public static void ClearAxis(HotasAxis action)
    {
        HotasAxisBinding binding = Axes[action];
        binding.Bound = false;
        binding.DeviceGuid = "";
        binding.DeviceName = "";
        binding.DeviceIndex = -1;
        Values[action] = 0.0;
    }

    public static void ClearButton(HotasButton action)
    {
        HotasButtonBinding binding = Buttons[action];
        binding.Bound = false;
        binding.DeviceGuid = "";
        binding.DeviceName = "";
        binding.DeviceIndex = -1;
        PressedStates[action] = false;
        JustPressedStates[action] = false;
    }

    public static string AxisBindingLabel(HotasAxis action)
    {
        HotasAxisBinding binding = Axes[action];
        if (!binding.Bound)
        {
            return "Not bound";
        }

        string name = string.IsNullOrEmpty(binding.DeviceName)
            ? $"Device {binding.DeviceIndex}"
            : binding.DeviceName;
        string suffix = IsAxisAvailable(action) ? "" : " (disconnected)";
        return $"{name} · axis {binding.AxisIndex + 1}{suffix}";
    }

    public static string ButtonBindingLabel(HotasButton action)
    {
        HotasButtonBinding binding = Buttons[action];
        if (!binding.Bound)
        {
            return "Not bound";
        }

        string name = string.IsNullOrEmpty(binding.DeviceName)
            ? $"Device {binding.DeviceIndex}"
            : binding.DeviceName;
        bool connected = Resolve(
            binding.DeviceGuid, binding.DeviceName, binding.DeviceIndex) >= 0;
        string suffix = connected ? "" : " (disconnected)";
        return $"{name} · button {binding.ButtonIndex + 1}{suffix}";
    }

    public static bool ReadRawAxis(HotasAxis action, out double raw)
    {
        raw = 0.0;
        HotasAxisBinding binding = Axes[action];
        if (!binding.Bound)
        {
            return false;
        }

        int device = Resolve(binding.DeviceGuid, binding.DeviceName, binding.DeviceIndex);
        if (device < 0)
        {
            return false;
        }

        raw = Input.GetJoyAxis(device, (JoyAxis)binding.AxisIndex);
        return true;
    }

    public static IReadOnlyList<HotasDevice> Devices()
    {
        var devices = new List<HotasDevice>();
        foreach (int device in Input.GetConnectedJoypads())
        {
            devices.Add(new HotasDevice(
                device, Input.GetJoyName(device), Input.GetJoyGuid(device)));
        }

        return devices;
    }

    public static string Summary()
    {
        var names = new List<string>();
        foreach (HotasAxisBinding binding in Axes.Values)
        {
            if (binding.Bound && !string.IsNullOrEmpty(binding.DeviceName)
                && !names.Contains(binding.DeviceName))
            {
                names.Add(binding.DeviceName);
            }
        }

        foreach (HotasButtonBinding binding in Buttons.Values)
        {
            if (binding.Bound && !string.IsNullOrEmpty(binding.DeviceName)
                && !names.Contains(binding.DeviceName))
            {
                names.Add(binding.DeviceName);
            }
        }

        return names.Count == 0 ? "keyboard" : string.Join(", ", names);
    }

    private static int Resolve(string guid, string name, int index)
    {
        Godot.Collections.Array<int> pads = Input.GetConnectedJoypads();
        if (!string.IsNullOrEmpty(guid))
        {
            foreach (int device in pads)
            {
                if (Input.GetJoyGuid(device) == guid)
                {
                    return device;
                }
            }
        }

        if (!string.IsNullOrEmpty(name))
        {
            foreach (int device in pads)
            {
                if (Input.GetJoyName(device) == name)
                {
                    return device;
                }
            }
        }

        return pads.Contains(index) ? index : -1;
    }
}
