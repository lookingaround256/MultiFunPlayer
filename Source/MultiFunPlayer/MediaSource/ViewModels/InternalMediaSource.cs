﻿using MultiFunPlayer.Common;
using MultiFunPlayer.Script;
using MultiFunPlayer.Script.Repository;
using MultiFunPlayer.Shortcut;
using MultiFunPlayer.UI;
using Newtonsoft.Json.Linq;
using NLog;
using Stylet;
using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace MultiFunPlayer.MediaSource.ViewModels;

[DisplayName("Internal")]
internal sealed class InternalMediaSource(ILocalScriptRepository localRepository, IShortcutManager shortcutManager, IEventAggregator eventAggregator) : AbstractMediaSource(shortcutManager, eventAggregator)
{
    private readonly object _playlistLock = new();

    private bool _isPlaying;
    private double _position;
    private double _duration;
    private double _speed;
    private PlaylistItem _currentItem;

    public override ConnectionStatus Status { get; protected set; }
    public bool IsConnected => Status == ConnectionStatus.Connected;
    public bool IsDisconnected => Status == ConnectionStatus.Disconnected;
    public bool IsConnectBusy => Status is ConnectionStatus.Connecting or ConnectionStatus.Disconnecting;
    public bool CanToggleConnect => !IsConnectBusy;

    public int PlaylistIndex { get; set; } = 0;
    public Playlist ScriptPlaylist { get; set; } = null;

    public bool IsShuffling { get; set; } = false;
    public bool IsLooping { get; set; } = false;
    public bool LoadAdditionalScripts { get; set; } = false;

    protected override ValueTask<bool> OnConnectingAsync(ConnectionType connectionType)
    {
        if (connectionType != ConnectionType.AutoConnect)
            Logger.Info("Connecting to {0} [Type: {1}]", Name, connectionType);

        return ValueTask.FromResult(true);
    }

    protected override async Task RunAsync(ConnectionType connectionType, CancellationToken token)
    {
        try
        {
            await Task.Delay(250, token);
            PlayIndex(-1);
            SetIsPlaying(false);
            SetSpeed(1);

            Status = ConnectionStatus.Connected;
        }
        catch (Exception e) when (connectionType != ConnectionType.AutoConnect)
        {
            Logger.Error(e, "Error when connecting to {0}", Name);
            _ = DialogHelper.ShowErrorAsync(e, $"Error when connecting to {Name}", "RootDialog");
            return;
        }
        catch
        {
            return;
        }

        try
        {
            var lastTicks = Stopwatch.GetTimestamp();
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            while (!token.IsCancellationRequested && await timer.WaitForNextTickAsync(token))
            {
                lock (_playlistLock)
                {
                    var currentTicks = Stopwatch.GetTimestamp();
                    var elapsed = (currentTicks - lastTicks) / (double)Stopwatch.Frequency;
                    lastTicks = currentTicks;

                    while (TryReadMessage(out var message))
                    {
                        if (message is MediaPlayPauseMessage playPauseMessage) { SetIsPlaying(playPauseMessage.ShouldBePlaying); }
                        else if (message is MediaSeekMessage seekMessage && _currentItem != null) { SetPosition(seekMessage.Position.TotalSeconds); }
                        else if (message is MediaChangeSpeedMessage changeSpeedMessage) { SetSpeed(changeSpeedMessage.Speed); }
                        else if (message is MediaChangePathMessage changePathMessage)
                        {
                            var path = changePathMessage.Path;
                            var playlistIndex = ScriptPlaylist?.FindIndex(path) ?? -1;
                            if (playlistIndex >= 0 && CheckIndexAndRefresh(playlistIndex) == true)
                                PlayIndex(playlistIndex);
                            else
                                SetPlaylist(CreatePlaylist(path));
                        }
                        else if (message is PlayScriptAtIndexMessage playIndexMessage)
                        {
                            var index = playIndexMessage.Index;
                            if (CheckIndexAndRefresh(index) == true)
                                PlayIndex(index);
                        }
                        else if (message is PlayScriptWithOffsetMessage playOffsetMessage)
                        {
                            if (ScriptPlaylist == null)
                                continue;

                            if (IsLooping)
                                PlayIndex(PlaylistIndex);
                            else if (IsShuffling)
                                PlayRandom();
                            else
                                PlayWithOffset(playOffsetMessage.Offset);
                        }
                    }

                    if (ScriptPlaylist == null)
                        continue;

                    if (_currentItem == null)
                    {
                        if (IsShuffling)
                            PlayRandom();
                        else
                            PlayIndexOrNextBest(0);
                    }

                    if (_position > _duration)
                    {
                        if (IsLooping)
                            SetPosition(0);
                        else if (IsShuffling)
                            PlayRandom();
                        else
                            PlayWithOffset(1);
                    }

                    if (!_isPlaying || _currentItem == null)
                        continue;

                    SetPosition(_position + elapsed * _speed);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Logger.Error(e, $"{Name} failed with exception");
            _ = DialogHelper.ShowErrorAsync(e, $"{Name} failed with exception", "RootDialog");
        }

        if (IsDisposing)
            return;

        PlayIndex(-1);
        SetIsPlaying(false);
    }

    private bool? CheckIndexAndRefresh(int index)
    {
        if (!ScriptPlaylist.ValidateIndex(index))
            return null;

        var item = ScriptPlaylist[index];
        return item.AsRefreshed().Exists;
    }

    private void PlayWithOffset(int offset)
    {
        var index = PlaylistIndex + offset;
        while (CheckIndexAndRefresh(index) == false)
            index += Math.Sign(offset);

        index = Math.Clamp(index, -1, ScriptPlaylist.Count);
        if (_currentItem != null && !ScriptPlaylist.ValidateIndex(index))
        {
            SetIsPlaying(false);
            if (index < 0)
                SetPosition(0, forceSeek: true);
            else if (index >= ScriptPlaylist.Count)
                SetPosition(_duration, forceSeek: true);
        }
        else
        {
            PlayIndex(index);
        }
    }

    private void PlayRandom()
    {
        ScriptPlaylist.Refresh();

        var existing = ScriptPlaylist.Where(i => i.Exists).ToList();
        if (existing.Count == 0)
            return;

        var item = existing[Random.Shared.Next(0, existing.Count)];
        var index = ScriptPlaylist.FindIndex(item);

        PlayIndex(index);
    }

    private void PlayIndexOrNextBest(int index)
    {
        var bestIndex = index;
        while (CheckIndexAndRefresh(bestIndex) == false)
            bestIndex++;

        if (!ScriptPlaylist.ValidateIndex(bestIndex))
        {
            bestIndex = index - 1;
            while (CheckIndexAndRefresh(bestIndex) == false)
                bestIndex--;
        }

        PlayIndex(bestIndex);
    }

    private void PlayIndex(int index)
    {
        if (ScriptPlaylist == null)
        {
            PlaylistIndex = -1;
            SetCurrentItem(null);
            return;
        }

        var validIndex = ScriptPlaylist.ValidateIndex(index);
        var item = validIndex ? ScriptPlaylist[index].AsRefreshed() : null;
        if (item?.Exists == false)
            item = null;

        if (_currentItem != item)
        {
            PlaylistIndex = item != null ? index : -1;
            SetCurrentItem(item);
        }
        else if (_currentItem != null)
        {
            PlaylistIndex = index;
            SetPosition(0, forceSeek: true);
        }
    }

    public bool CanPlayNext => IsConnected && ScriptPlaylist != null;
    public bool CanPlayPrevious => IsConnected && ScriptPlaylist != null;
    public void PlayNext() => WriteMessage(new PlayScriptWithOffsetMessage(1));
    public void PlayPrevious() => WriteMessage(new PlayScriptWithOffsetMessage(-1));

    public bool CanClearPlaylist => IsConnected && ScriptPlaylist != null;
    public void ClearPlaylist() => SetPlaylist(null);

    public bool CanRefreshPlaylist => IsConnected && ScriptPlaylist != null;
    public void RefreshPlaylist() => ScriptPlaylist?.Refresh();

    public bool CanCleanupPlaylist => IsConnected && ScriptPlaylist != null;
    public void CleanupPlaylist()
    {
        ScriptPlaylist?.Cleanup();
        PlayIndex(ScriptPlaylist.FindIndex(_currentItem));
    }

    private Playlist CreatePlaylist(params string[] paths)
    {
        if (paths == null)
            return null;

        var isPlaylistFile = paths.Length == 1 && Path.GetExtension(paths[0]) == ".txt";
        return isPlaylistFile ? new Playlist(paths[0]) : new Playlist(paths);
    }

    private void SetPlaylist(Playlist playlist)
    {
        lock (_playlistLock)
        {
            ScriptPlaylist = playlist;
            PlayIndex(-1);
            if (playlist == null)
                SetIsPlaying(false);
        }
    }

    private void SetCurrentItem(PlaylistItem item)
    {
        _currentItem = item;
        if (Status is not ConnectionStatus.Connected and not ConnectionStatus.Disconnecting)
            return;

        if (item == null)
        {
            ResetState();
            return;
        }

        var result = new Dictionary<DeviceAxis, IScriptResource>();
        if (LoadAdditionalScripts)
        {
            var scriptName = DeviceAxisUtils.GetBaseNameWithExtension(item.Name);
            result.Merge(localRepository.SearchForScripts(scriptName, Path.GetDirectoryName(item.FullName), DeviceAxis.All));
        }
        else
        {
            var readerResult = FunscriptReader.Default.FromFileInfo(item.AsFileInfo());
            if (readerResult.IsSuccess)
            {
                if (readerResult.IsMultiAxis)
                    result.Merge(readerResult.Resources);
                else
                    result.Merge(DeviceAxisUtils.FindAxesMatchingName(item.Name, true).ToDictionary(a => a, _ => readerResult.Resource));
            }
        }

        if (result.Count == 0)
        {
            ResetState();
            return;
        }

        SetDuration(result.Values.Max(s => s.Keyframes[^1].Position));
        SetPosition(0, forceSeek: true);

        result.Merge(DeviceAxis.All.Except(result.Keys).ToDictionary(a => a, _ => default(IScriptResource)));
        PublishMessage(new ChangeScriptMessage(result));

        void ResetState()
        {
            SetDuration(double.NaN);
            SetPosition(double.NaN);
            PublishMessage(new ChangeScriptMessage(DeviceAxis.All, null));
        }
    }

    private void SetDuration(double duration)
    {
        _duration = duration;
        if (Status is ConnectionStatus.Connected or ConnectionStatus.Disconnecting)
            PublishMessage(new MediaDurationChangedMessage(double.IsFinite(duration) ? TimeSpan.FromSeconds(duration) : null));
    }

    private void SetPosition(double position, bool forceSeek = false)
    {
        _position = position;
        if (Status is ConnectionStatus.Connected or ConnectionStatus.Disconnecting)
            PublishMessage(new MediaPositionChangedMessage(double.IsFinite(position) ? TimeSpan.FromSeconds(position) : null, forceSeek));
    }

    private void SetSpeed(double speed)
    {
        _speed = speed;
        if (Status is ConnectionStatus.Connected or ConnectionStatus.Disconnecting)
            PublishMessage(new MediaSpeedChangedMessage(speed));
    }

    private void SetIsPlaying(bool isPlaying)
    {
        _isPlaying = isPlaying;
        if (Status is ConnectionStatus.Connected or ConnectionStatus.Disconnecting)
            PublishMessage(new MediaPlayingChangedMessage(isPlaying));
    }

    public void OnDrop(object sender, DragEventArgs e)
    {
        var drop = e.Data.GetData(DataFormats.FileDrop);
        if (drop is not string[] paths)
            return;

        SetPlaylist(CreatePlaylist(paths));
    }

    public void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.Link;
    }

    public override void HandleSettings(JObject settings, SettingsAction action)
    {
        base.HandleSettings(settings, action);

        if (action == SettingsAction.Saving)
        {
            settings[nameof(IsShuffling)] = IsShuffling;
            settings[nameof(IsLooping)] = IsLooping;

            settings[nameof(ScriptPlaylist)] = ScriptPlaylist switch
            {
                { SourceFile: not null } => JToken.FromObject(ScriptPlaylist.SourceFile.FullName),
                { Count: > 0 } => JArray.FromObject(ScriptPlaylist.Select(x => x.AsFileInfo())),
                _ => null,
            };
        }
        else if (action == SettingsAction.Loading)
        {
            if (settings.TryGetValue<bool>(nameof(IsShuffling), out var isShuffling))
                IsShuffling = isShuffling;
            if (settings.TryGetValue<bool>(nameof(IsLooping), out var isLooping))
                IsLooping = isLooping;

            if (settings.TryGetValue(nameof(ScriptPlaylist), out var scriptPlaylistToken))
            {
                if (scriptPlaylistToken.Type == JTokenType.String && scriptPlaylistToken.TryToObject<string>(out var playlistFile) && Path.GetExtension(playlistFile) == ".txt")
                    SetPlaylist(new Playlist(playlistFile));
                else if (scriptPlaylistToken.Type == JTokenType.Array && scriptPlaylistToken.TryToObject<List<string>>(out var playlistFiles))
                    SetPlaylist(new Playlist(playlistFiles));
            }
        }
    }

    protected override void RegisterActions(IShortcutManager s)
    {
        base.RegisterActions(s);

        void WhenConnected(Action callback)
        {
            if (Status == ConnectionStatus.Connected)
                callback();
        }

        #region IsShuffling
        s.RegisterAction<bool>($"{Name}::Shuffle::Set", s => s.WithLabel("Enable shuffle"), enabled => IsShuffling = enabled);
        s.RegisterAction($"{Name}::Shuffle::Toggle", () => IsShuffling = !IsShuffling);
        #endregion

        #region IsLooping
        s.RegisterAction<bool>($"{Name}::Looping::Set", s => s.WithLabel("Enable looping"), enabled => IsLooping = enabled);
        s.RegisterAction($"{Name}::Looping::Toggle", () => IsLooping = !IsLooping);
        #endregion

        #region Playlist
        s.RegisterAction($"{Name}::Playlist::Clear", () => WhenConnected(ClearPlaylist));
        s.RegisterAction($"{Name}::Playlist::Prev", () => WhenConnected(PlayPrevious));
        s.RegisterAction($"{Name}::Playlist::Next", () => WhenConnected(PlayNext));
        s.RegisterAction<int>($"{Name}::Playlist::PlayByIndex",
            s => s.WithLabel("Index").AsNumericUpDown(minimum: 0),
            index => WhenConnected(() => WriteMessage(new PlayScriptAtIndexMessage(index))));
        s.RegisterAction<string>($"{Name}::Playlist::PlayByName", s => s.WithLabel("File name/path"), name => WhenConnected(() =>
        {
            var playlist = ScriptPlaylist;
            if (playlist == null)
                return;

            var index = playlist.FindIndex(name);
            if (index >= 0)
                WriteMessage(new PlayScriptAtIndexMessage(index));
        }));
        #endregion
    }

    public void OnPlayScript(object sender, EventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not PlaylistItem item)
            return;

        var playlist = ScriptPlaylist;
        if (playlist == null)
            return;

        var index = playlist.FindIndex(item);
        if (index < 0)
            return;

        WriteMessage(new PlayScriptAtIndexMessage(index));
    }

    public void OnRemoveItem(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not PlaylistItem item)
            return;

        var playlist = ScriptPlaylist;
        if (playlist == null)
            return;

        var index = playlist.FindIndex(item);
        playlist.RemoveItem(item);

        if (index == PlaylistIndex)
            PlayIndexOrNextBest(PlaylistIndex);
        else if (index < PlaylistIndex)
            PlaylistIndex--;
    }

    public void OnIsLoopingChanged()
    {
        if (IsLooping && IsShuffling)
            IsShuffling = false;
    }

    public void OnIsShufflingChanged()
    {
        if (IsShuffling && IsLooping)
            IsLooping = false;
    }

    public void OnLoadAdditionalScriptsChanged()
        => SetCurrentItem(_currentItem);

    private sealed record PlayScriptAtIndexMessage(int Index) : IMediaSourceControlMessage;
    private sealed record PlayScriptWithOffsetMessage(int Offset) : IMediaSourceControlMessage;

    internal sealed class Playlist : IReadOnlyList<PlaylistItem>, INotifyCollectionChanged, INotifyPropertyChanged
    {
        private readonly List<PlaylistItem> _items;

        public FileInfo SourceFile { get; }

        public Playlist(string sourceFile)
        {
            SourceFile = new FileInfo(sourceFile);

            if (SourceFile.Exists)
                _items = File.ReadAllLines(SourceFile.FullName)
                             .Select(PlaylistItem.CreateFromPath)
                             .NotNull()
                             .Where(f => string.Equals(f.Extension, ".funscript", StringComparison.OrdinalIgnoreCase))
                             .ToList();

            _items ??= [];
        }

        public Playlist(IEnumerable<string> files)
        {
            _items = files.Select(PlaylistItem.CreateFromPath)
                          .NotNull()
                          .Where(f => string.Equals(f.Extension, ".funscript", StringComparison.OrdinalIgnoreCase))
                          .ToList();
        }

        private void NotifyObserversOfChange()
        {
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
        }

        public PlaylistItem this[int index] => _items[index];
        public int Count => _items.Count;
        public IEnumerator<PlaylistItem> GetEnumerator() => _items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

        public void Refresh()
        {
            foreach (var item in _items)
                _ = item.AsRefreshed();
        }

        public void Cleanup()
        {
            Refresh();

            _items.RemoveAll(i => !i.Exists);
            NotifyObserversOfChange();
        }

        public void RemoveItem(PlaylistItem item)
        {
            _items.Remove(item);
            NotifyObserversOfChange();
        }

        public int FindIndex(Predicate<PlaylistItem> match) => _items.FindIndex(match);
        public int FindIndex(PlaylistItem item) => item != null ? FindIndex(f => string.Equals(f.Name, item.Name, StringComparison.OrdinalIgnoreCase)
                                                                              || string.Equals(f.FullName, item.FullName, StringComparison.OrdinalIgnoreCase))
                                                                : -1;
        public int FindIndex(string path) => FindIndex(f => string.Equals(f.Name, path, StringComparison.OrdinalIgnoreCase)
                                                         || string.Equals(f.FullName, path, StringComparison.OrdinalIgnoreCase));

        public event NotifyCollectionChangedEventHandler CollectionChanged;
        public event PropertyChangedEventHandler PropertyChanged;
    }

    internal sealed class PlaylistItem : PropertyChangedBase
    {
        private readonly FileInfo _source;

        private PlaylistItem(string path) => _source = new(path);

        public string Name => _source.Name;
        public string FullName => _source.FullName;
        public string Extension => _source.Extension;
        public bool Exists => _source.Exists;

        public FileInfo AsFileInfo() => _source;
        public PlaylistItem AsRefreshed()
        {
            var before = _source.Exists;
            var after = _source.AsRefreshed().Exists;
            if (before ^ after)
                NotifyOfPropertyChange(nameof(Exists));
            return this;
        }

        public static PlaylistItem CreateFromPath(string path)
        {
            try { return new PlaylistItem(path); }
            catch { return null; }
        }
    }
}
