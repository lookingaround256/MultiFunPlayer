﻿using Newtonsoft.Json;
using System.IO;

namespace MultiFunPlayer.Settings.Converters;

[GlobalJsonConverter]
internal sealed class FileSystemInfoConverter : JsonConverter
{
    public override bool CanConvert(Type objectType)
        => typeof(FileSystemInfo).IsAssignableFrom(objectType);

    public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        => reader.Value is string s ? Activator.CreateInstance(objectType, s) : null;

    public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        => writer.WriteValue((value as FileSystemInfo)?.FullName);
}
