﻿using Newtonsoft.Json.Linq;
using NLog;

namespace MultiFunPlayer.Settings.Migrations;

internal sealed class Migration0006 : AbstractConfigMigration
{
    protected override Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    public override void Migrate(JObject settings)
    {
        foreach (var action in SelectObjects(settings, "$.Shortcuts.Bindings[*].Actions[?(@.Descriptor =~ /Axis::SmartLimitEnabled::Set.*/i)]"))
        {
            SetPropertyByName(action, "Descriptor", "Axis::SmartLimitInputAxis::Set");

            EditPropertiesByPaths(action, new Dictionary<string, Func<JToken, JToken>>()
            {
                ["$.Settings[1].$type"] = _ => "MultiFunPlayer.Common.DeviceAxis, MultiFunPlayer",
                ["$.Settings[1].Value"] = v => v.ToObject<bool>() ? "L0" : null
            }, selectMultiple: false);
        }

        foreach (var action in SelectObjects(settings, "$.Shortcuts.Bindings[*].Actions[?(@.Descriptor =~ /Axis::SmartLimitEnabled::Toggle.*/i)]"))
            RemoveToken(action);

        var defaultPoints = new string[] { "25,100", "90,0" };
        foreach (var axisSettings in SelectObjects(settings, "$.Script.AxisSettings[*]"))
        {
            SetPropertyByName(axisSettings, "SmartLimitPoints", JArray.FromObject(defaultPoints), addIfMissing: true);

            if (RemovePropertyByName(axisSettings, "SmartLimitEnabled", out var property))
                AddPropertyByName(axisSettings, "SmartLimitInputAxis", property.ToObject<bool>() ? "L0" : null);
        }

        base.Migrate(settings);
    }
}