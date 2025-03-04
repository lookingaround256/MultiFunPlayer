﻿using MultiFunPlayer.Common;
using MultiFunPlayer.Shortcut;
using MultiFunPlayer.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using Stylet;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace MultiFunPlayer.MediaSource.ViewModels;

[DisplayName("DeoVR")]
internal sealed class DeoVRMediaSource(IShortcutManager shortcutManager, IEventAggregator eventAggregator) : AbstractMediaSource(shortcutManager, eventAggregator)
{
    public override ConnectionStatus Status { get; protected set; }
    public bool IsConnected => Status == ConnectionStatus.Connected;
    public bool IsDisconnected => Status == ConnectionStatus.Disconnected;
    public bool IsConnectBusy => Status is ConnectionStatus.Connecting or ConnectionStatus.Disconnecting;
    public bool CanToggleConnect => !IsConnectBusy;

    public EndPoint Endpoint { get; set; } = new IPEndPoint(IPAddress.Loopback, 23554);

    protected override ValueTask<bool> OnConnectingAsync(ConnectionType connectionType)
    {
        if (connectionType != ConnectionType.AutoConnect)
            Logger.Info("Connecting to {0} at \"{1}\" [Type: {2}]", Name, Endpoint?.ToUriString(), connectionType);

        if (Endpoint == null)
            throw new MediaSourceException("Endpoint cannot be null");
        if (Endpoint.IsLocalhost())
            if (!Process.GetProcesses().Any(p => Regex.IsMatch(p.ProcessName, "(?i)(?>deovr|slr)")))
                throw new MediaSourceException($"Could not find a running {Name} process");

        return ValueTask.FromResult(true);
    }

    protected override async Task RunAsync(ConnectionType connectionType, CancellationToken token)
    {
        using var client = new TcpClient();

        try
        {
            using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(token);
            if (connectionType == ConnectionType.AutoConnect)
                cancellationSource.CancelAfter(500);

            await client.ConnectAsync(Endpoint, cancellationSource.Token);

            Status = ConnectionStatus.Connected;
        }
        catch (Exception e) when (connectionType != ConnectionType.AutoConnect)
        {
            Logger.Error(e, "Error when connecting to {0} at \"{1}\"", Name, Endpoint?.ToUriString());
            _ = DialogHelper.ShowErrorAsync(e, $"Error when connecting to {Name}", "RootDialog");
            return;
        }
        catch
        {
            return;
        }

        try
        {
            await using var stream = client.GetStream();

            using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(token);
            var task = await Task.WhenAny(ReadAsync(client, stream, cancellationSource.Token), WriteAsync(client, stream, cancellationSource.Token));
            cancellationSource.Cancel();

            task.ThrowIfFaulted();
        }
        catch (OperationCanceledException) { }
        catch (IOException e) { Logger.Debug(e, $"{Name} failed with exception"); }
        catch (Exception e)
        {
            Logger.Error(e, $"{Name} failed with exception");
            _ = DialogHelper.ShowErrorAsync(e, $"{Name} failed with exception", "RootDialog");
        }

        if (IsDisposing)
            return;

        PublishMessage(new MediaPathChangedMessage(null));
        PublishMessage(new MediaPlayingChangedMessage(false));
    }

    private async Task ReadAsync(TcpClient client, NetworkStream stream, CancellationToken token)
    {
        try
        {
            var playerState = default(PlayerState);
            while (!token.IsCancellationRequested && client.Connected)
            {
                var lengthBuffer = await stream.ReadExactlyAsync(4, token);
                var length = BitConverter.ToInt32(lengthBuffer, 0);
                if (length <= 0)
                {
                    Logger.Trace("Received \"\" from \"{0}\"", Name);

                    if (playerState != null)
                    {
                        PublishMessage(new MediaPathChangedMessage(null));
                        PublishMessage(new MediaPlayingChangedMessage(false));
                        playerState = null;
                    }

                    continue;
                }

                playerState ??= new PlayerState();

                var dataBuffer = await stream.ReadExactlyAsync(length, token);
                var data = Encoding.UTF8.GetString(dataBuffer);
                Logger.Trace("Received \"{0}\" from \"{1}\"", data, Name);

                try
                {
                    var document = JObject.Parse(data);
                    if (document.TryGetValue("path", out var pathToken) && pathToken.TryToObject<string>(out var path))
                    {
                        if (string.IsNullOrWhiteSpace(path))
                            path = null;

                        if (path != playerState.Path)
                        {
                            PublishMessage(new MediaPathChangedMessage(path));
                            playerState.Path = path;
                        }
                    }

                    if (document.TryGetValue("playerState", out var stateToken) && stateToken.TryToObject<int>(out var state) && state != playerState.State)
                    {
                        PublishMessage(new MediaPlayingChangedMessage(state == 0));
                        playerState.State = state;
                    }

                    if (document.TryGetValue("duration", out var durationToken) && durationToken.TryToObject<double>(out var duration) && duration >= 0 && duration != playerState.Duration)
                    {
                        PublishMessage(new MediaDurationChangedMessage(TimeSpan.FromSeconds(duration)));
                        playerState.Duration = duration;
                    }

                    if (document.TryGetValue("currentTime", out var timeToken) && timeToken.TryToObject<double>(out var position) && position >= 0 && position != playerState.Position)
                    {
                        PublishMessage(new MediaPositionChangedMessage(TimeSpan.FromSeconds(position)));
                        playerState.Position = position;
                    }

                    if (document.TryGetValue("playbackSpeed", out var speedToken) && speedToken.TryToObject<double>(out var speed) && speed > 0 && speed != playerState.Speed)
                    {
                        PublishMessage(new MediaSpeedChangedMessage(speed));
                        playerState.Speed = speed;
                    }
                }
                catch (JsonException) { }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task WriteAsync(TcpClient client, NetworkStream stream, CancellationToken token)
    {
        try
        {
            var keepAliveBuffer = new byte[4];
            while (!token.IsCancellationRequested && client.Connected)
            {
                using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(token);

                var readMessageTask = WaitForMessageAsync(cancellationSource.Token).AsTask();
                var timeoutTask = Task.Delay(1000, cancellationSource.Token);
                var completedTask = await Task.WhenAny(readMessageTask, timeoutTask);

                cancellationSource.Cancel();
                if (completedTask.Exception != null)
                    throw completedTask.Exception;

                if (completedTask == readMessageTask)
                {
                    var message = await ReadMessageAsync(token);
                    var sendState = new PlayerState();

                    if (message is MediaPlayPauseMessage playPauseMessage)
                        sendState.State = playPauseMessage.ShouldBePlaying ? 0 : 1;
                    else if (message is MediaSeekMessage seekMessage)
                        sendState.Position = seekMessage.Position.TotalSeconds;
                    else if (message is MediaChangePathMessage changePathMessage)
                        sendState.Path = changePathMessage.Path;
                    else if (message is MediaChangeSpeedMessage changeSpeedMessage)
                        sendState.Speed = changeSpeedMessage.Speed;
                    else
                        continue;

                    var messageString = JsonConvert.SerializeObject(sendState);

                    Logger.Debug("Sending \"{0}\" to \"{1}\"", messageString, Name);

                    var messageBytes = Encoding.UTF8.GetBytes(messageString);
                    var lengthBytes = BitConverter.GetBytes(messageBytes.Length);

                    var bytes = new byte[lengthBytes.Length + messageBytes.Length];
                    Array.Copy(lengthBytes, 0, bytes, 0, lengthBytes.Length);
                    Array.Copy(messageBytes, 0, bytes, lengthBytes.Length, messageBytes.Length);

                    await stream.WriteAsync(bytes, token);
                }
                else if (completedTask == timeoutTask)
                {
                    Logger.Trace("Sending keep-alive to \"{0}\"", Name);

                    await stream.WriteAsync(keepAliveBuffer, token);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public override void HandleSettings(JObject settings, SettingsAction action)
    {
        base.HandleSettings(settings, action);

        if (action == SettingsAction.Saving)
        {
            settings[nameof(Endpoint)] = Endpoint?.ToUriString();
        }
        else if (action == SettingsAction.Loading)
        {
            if (settings.TryGetValue<EndPoint>(nameof(Endpoint), out var endpoint))
                Endpoint = endpoint;
        }
    }

    protected override void RegisterActions(IShortcutManager s)
    {
        base.RegisterActions(s);

        #region Endpoint
        s.RegisterAction<string>($"{Name}::Endpoint::Set", s => s.WithLabel("Endpoint").WithDescription("ipOrHost:port"), endpointString =>
        {
            if (NetUtils.TryParseEndpoint(endpointString, out var endpoint))
                Endpoint = endpoint;
        });
        #endregion
    }

    private sealed class PlayerState
    {
        [JsonProperty("path")] public string Path { get; set; }
        [JsonProperty("currentTime")] public double? Position { get; set; }
        [JsonProperty("playbackSpeed")] public double? Speed { get; set; }
        [JsonProperty("playerState")] public int? State { get; set; }
        [JsonProperty("duration")] public double? Duration { get; set; }
    }
}
