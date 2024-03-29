﻿using Newtonsoft.Json.Linq;
using NLog;
using System.Text.RegularExpressions;

namespace MultiFunPlayer.Settings.Migrations;

internal sealed class Migration0011 : AbstractConfigMigration
{
    protected override Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    public override void Migrate(JObject settings)
    {
        var prefixMap = new Dictionary<string, string>();
        foreach (var outputTarget in SelectObjects(settings, "$.OutputTarget.Items[?(@.$type =~ /.*NetworkOutputTargetViewModel.*/i)]\""))
        {
            if (!TryGetValue<JValue>(outputTarget, "$index", out var index) || !TryGetValue<JValue>(outputTarget, "Protocol", out var protocol))
                continue;

            prefixMap.Add($"Network/{index.ToObject<int>()}",
                          $"{protocol.ToObject<string>().ToUpper()}/{index.ToObject<int>()}");
        }

        foreach (var action in SelectObjects(settings, "$.Shortcuts.Bindings[*].Actions[?(@.Descriptor =~ /Network\\/\\d+::.*/i)]"))
        {
            EditPropertyByName(action, "Descriptor",
                v => Regex.Replace(v.ToString(), "(^.+?)::", m => $"{prefixMap[m.Groups[1].Value]}::"));
        }

        if (TrySelectProperty(settings, "$.OutputTarget.ActiveItem", out var activeItem))
        {
            var value = activeItem.Value.ToObject<string>();
            if (value.StartsWith("Network")
             && TrySelectObject(settings, "$.OutputTarget.Items[?(@.$type =~ /.*NetworkOutputTargetViewModel.*/i && @.$index == {index})]", out var outputTarget)
             && TryGetValue<JValue>(outputTarget, "Protocol", out var protocol))
            {
                var index = int.Parse(activeItem.Value.ToObject<string>().Split('/')[1]);
                SetProperty(activeItem, $"{protocol.ToObject<string>().ToUpper()}/{index}");
            }
        }

        foreach (var outputTarget in SelectObjects(settings, "$.OutputTarget.Items[?(@.$type =~ /.*NetworkOutputTargetViewModel.*/i)]"))
        {
            if (!TryGetValue<JValue>(outputTarget, "$index", out var index) || !TryGetValue<JValue>(outputTarget, "Protocol", out var protocol))
                continue;

            RemovePropertyByName(outputTarget, "Protocol");
            EditPropertyByName(outputTarget, "$type",
                v => v.ToObject<string>().Replace("Network", protocol.ToObject<string>()));

            SetPropertyByName(outputTarget, "SendDirtyValuesOnly", protocol.ToObject<string>() == "Tcp", addIfMissing: true);
        }

        base.Migrate(settings);
    }
}