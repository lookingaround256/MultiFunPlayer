﻿using MultiFunPlayer.Settings.Converters;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;

namespace MultiFunPlayer.Common;

[TypeConverter(typeof(DeviceAxisTypeConverter))]
public sealed class DeviceAxis
{
    private readonly int _id;

    public string Name { get; }
    public double DefaultValue { get; }
    public string FriendlyName { get; }
    public IReadOnlyList<string> FunscriptNames { get; }

    public override string ToString() => Name;
    public override int GetHashCode() => _id;

    private DeviceAxis(DeviceAxisSettings settings)
    {
        _id = _count++;

        Name = settings.Name;
        DefaultValue = settings.DefaultValue;
        FriendlyName = settings.FriendlyName;
        FunscriptNames = settings.FunscriptNames;
    }

    private static int _count;
    private static int _outputMaximum;
    private static CompositeFormat _outputFormat;
    private static FrozenDictionary<string, DeviceAxis> _axisNameMap;
    public static ImmutableArray<DeviceAxis> All { get; private set; }

    public static DeviceAxis Parse(string name) => _axisNameMap.GetValueOrDefault(name, null);
    public static bool TryParse(string name, out DeviceAxis axis)
    {
        axis = Parse(name);
        return axis != null;
    }

    public static string ToString(DeviceAxis axis, double value) => $"{axis}{string.Format(null, _outputFormat, value * _outputMaximum)}";
    public static string ToString(DeviceAxis axis, double value, double interval) => $"{ToString(axis, value)}I{(int)Math.Floor(interval + 0.75)}";

    public static string ToString(IEnumerable<KeyValuePair<DeviceAxis, double>> values)
        => $"{values.Aggregate(string.Empty, (s, x) => $"{s} {ToString(x.Key, x.Value)}")}\n".TrimStart();
    public static string ToString(IEnumerable<KeyValuePair<DeviceAxis, double>> values, double interval)
        => $"{values.Aggregate(string.Empty, (s, x) => $"{s} {ToString(x.Key, x.Value, interval)}")}\n".TrimStart();

    public static bool IsValueDirty(double value, double lastValue)
        => Math.Abs(lastValue - value) * (_outputMaximum + 1) >= 1 || (double.IsFinite(value) ^ double.IsFinite(lastValue));
    public static bool IsValueDirty(double value, double lastValue, double epsilon)
        => Math.Abs(lastValue - value) >= epsilon || (double.IsFinite(value) ^ double.IsFinite(lastValue));

    internal static void InitializeFromDevice(DeviceSettings device)
    {
        if (_count != 0)
            throw new NotSupportedException();

        _outputMaximum = (int)(Math.Pow(10, device.OutputPrecision) - 1);
        _outputFormat = CompositeFormat.Parse($"{{0:{new string('0', device.OutputPrecision)}}}");

        All = device.Axes.Where(s => s.Enabled && Regex.IsMatch(s.Name, "^[A-Z][0-9]$"))
                         .DistinctBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                         .Select(s => new DeviceAxis(s))
                         .ToImmutableArray();

        _axisNameMap = All.ToFrozenDictionary(a => a.Name, a => a);
    }
}
