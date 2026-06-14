using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Melodia.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Search;
using Windows.Storage.Streams;

namespace Melodia.ViewModels;

public enum RepeatMode { Off, All, One }
public enum SortMode { Default, Title, Artist, Album, Duration }

public class MainViewModel : INotifyPropertyChanged
{
    private readonly MediaPlayer _player;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherQueue _dispatcherQueue;
    private bool _updatingFromTimer;
    private int _currentIndex = -1;

    // ── Collections ──────────────────────────────────────────────────────────
    public ObservableCollection<SongItem> Songs    { get; } = new(); // library (left)
    public ObservableCollection<SongItem> Playlist { get; } = new(); // queue   (right)

    // ── CurrentIndex ─────────────────────────────────────────────────────────
    private int _exposedIndex = -1;
    public int CurrentIndex
    {
        get => _exposedIndex;
        private set
        {
            if (SetProperty(ref _exposedIndex, value))
                OnPropertyChanged(nameof(HasCurrentSong));
        }
    }

    public bool HasCurrentSong => _currentIndex >= 0;

    // ── CurrentSong ──────────────────────────────────────────────────────────
    private SongItem? _currentSong;
    public SongItem? CurrentSong
    {
        get => _currentSong;
        private set
        {
            if (SetProperty(ref _currentSong, value))
            {
                OnPropertyChanged(nameof(NowPlayingTitle));
                OnPropertyChanged(nameof(NowPlayingSubtitle));
            }
        }
    }

    // ── Album art ────────────────────────────────────────────────────────────
    private BitmapImage? _albumArt;
    public BitmapImage? AlbumArt
    {
        get => _albumArt;
        private set
        {
            if (SetProperty(ref _albumArt, value))
            {
                OnPropertyChanged(nameof(AlbumArtVisibility));
                OnPropertyChanged(nameof(PlaceholderVisibility));
            }
        }
    }

    public Visibility AlbumArtVisibility   => _albumArt != null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PlaceholderVisibility => _albumArt == null ? Visibility.Visible : Visibility.Collapsed;

    // ── Playback state ───────────────────────────────────────────────────────
    private bool _isPlaying;
    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (SetProperty(ref _isPlaying, value))
                OnPropertyChanged(nameof(PlayPauseGlyph));
        }
    }

    public string PlayPauseGlyph => _isPlaying ? "" : "";

    // ── Shuffle ──────────────────────────────────────────────────────────────
    private bool _isShuffled;
    public bool IsShuffled
    {
        get => _isShuffled;
        private set
        {
            if (SetProperty(ref _isShuffled, value))
                OnPropertyChanged(nameof(ShuffleOpacity));
        }
    }

    public double ShuffleOpacity => _isShuffled ? 1.0 : 0.35;

    // ── Repeat ───────────────────────────────────────────────────────────────
    private RepeatMode _repeatMode = RepeatMode.Off;
    public RepeatMode RepeatMode
    {
        get => _repeatMode;
        private set
        {
            if (SetProperty(ref _repeatMode, value))
            {
                OnPropertyChanged(nameof(RepeatGlyph));
                OnPropertyChanged(nameof(RepeatOpacity));
            }
        }
    }

    public string RepeatGlyph  => _repeatMode == RepeatMode.One ? "" : "";
    public double RepeatOpacity => _repeatMode != RepeatMode.Off ? 1.0 : 0.35;

    // ── Sort (library display only — does not affect playback) ───────────────
    private SortMode _sortMode = SortMode.Default;
    public SortMode SortMode
    {
        get => _sortMode;
        set
        {
            if (SetProperty(ref _sortMode, value))
                OnPropertyChanged(nameof(FilteredSongs));
        }
    }

    // ── Lyrics ───────────────────────────────────────────────────────────────
    public ObservableCollection<LyricLine> Lyrics { get; } = new();

    private int _currentLyricIdx = -1;
    private int _exposedLyricIdx = -1;
    public int CurrentLyricIndex
    {
        get => _exposedLyricIdx;
        private set => SetProperty(ref _exposedLyricIdx, value);
    }

    private bool _hasLyrics;
    public bool HasLyrics
    {
        get => _hasLyrics;
        private set
        {
            if (SetProperty(ref _hasLyrics, value))
                OnPropertyChanged(nameof(LyricsVisibility));
        }
    }
    private bool _lyricsAreSynced;
    public Visibility LyricsVisibility => _hasLyrics ? Visibility.Visible : Visibility.Collapsed;

    // ── Seek ─────────────────────────────────────────────────────────────────
    private double _position;
    public double Position
    {
        get => _position;
        set
        {
            if (SetProperty(ref _position, value) && !_updatingFromTimer)
            {
                if (_player.PlaybackSession.CanSeek)
                    _player.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Max(0, value));
            }
        }
    }

    // ── Duration ─────────────────────────────────────────────────────────────
    private double _duration = 1;
    public double Duration
    {
        get => _duration;
        private set => SetProperty(ref _duration, value > 0 ? value : 1);
    }

    // ── Volume ───────────────────────────────────────────────────────────────
    private double _volume = 1.0;
    public double Volume
    {
        get => _volume;
        set
        {
            if (SetProperty(ref _volume, value))
                _player.Volume = value;
        }
    }

    // ── Display strings ──────────────────────────────────────────────────────
    public string NowPlayingTitle => _currentSong?.DisplayTitle ?? "No song selected";

    public string NowPlayingSubtitle
    {
        get
        {
            if (_currentSong == null) return string.Empty;
            var artist = _currentSong.DisplayArtist;
            var album  = _currentSong.DisplayAlbum;
            return (artist == "Unknown Artist", album == "Unknown Album") switch
            {
                (true,  true)  => string.Empty,
                (true,  false) => album,
                (false, true)  => artist,
                _              => $"{artist} · {album}"
            };
        }
    }

    public string SongCountText
    {
        get
        {
            if (Songs.Count == 0) return "No songs loaded";
            if (!string.IsNullOrWhiteSpace(_filterText))
                return $"{FilteredSongs.Count()} of {Songs.Count} songs";
            return Songs.Count == 1 ? "1 song" : $"{Songs.Count} songs";
        }
    }

    public string PlaylistCountText => Playlist.Count == 0
        ? string.Empty
        : Playlist.Count == 1 ? "1 song" : $"{Playlist.Count} songs";

    // ── Filter (library only) ─────────────────────────────────────────────────
    private string _filterText = string.Empty;
    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value))
            {
                OnPropertyChanged(nameof(FilteredSongs));
                OnPropertyChanged(nameof(SongCountText));
            }
        }
    }

    public IEnumerable<SongItem> FilteredSongs
    {
        get
        {
            var q = string.IsNullOrWhiteSpace(_filterText)
                ? Songs.AsEnumerable()
                : Songs.Where(s =>
                    s.DisplayTitle.Contains(_filterText,  StringComparison.OrdinalIgnoreCase) ||
                    s.DisplayArtist.Contains(_filterText, StringComparison.OrdinalIgnoreCase) ||
                    s.DisplayAlbum.Contains(_filterText,  StringComparison.OrdinalIgnoreCase));
            return ApplySort(q);
        }
    }

    private IEnumerable<SongItem> ApplySort(IEnumerable<SongItem> q) => _sortMode switch
    {
        SortMode.Title    => q.OrderBy(s => s.DisplayTitle,  StringComparer.OrdinalIgnoreCase),
        SortMode.Artist   => q.OrderBy(s => s.DisplayArtist, StringComparer.OrdinalIgnoreCase)
                              .ThenBy(s => s.DisplayTitle,   StringComparer.OrdinalIgnoreCase),
        SortMode.Album    => q.OrderBy(s => s.DisplayAlbum,  StringComparer.OrdinalIgnoreCase)
                              .ThenBy(s => s.DisplayTitle,   StringComparer.OrdinalIgnoreCase),
        SortMode.Duration => q.OrderBy(s => s.Duration),
        _                 => q
    };

    // ── Persistence paths ─────────────────────────────────────────────────────
    private static readonly string _tempSessionPath =
        Path.Combine(Path.GetTempPath(), "Melodia", "session.m3u");
    private static readonly string _lastSessionPath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Melodia", "last_session.m3u");
    private static readonly string _lastFolderPath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Melodia", "last_folder.txt");

    // ── Constructor ──────────────────────────────────────────────────────────
    public MainViewModel()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        _player = new MediaPlayer();
        _player.AudioCategory = MediaPlayerAudioCategory.Media;
        _player.MediaEnded += OnMediaEnded;
        _player.PlaybackSession.PlaybackStateChanged  += OnPlaybackStateChanged;
        _player.PlaybackSession.NaturalDurationChanged += OnNaturalDurationChanged;

        var smtc = _player.SystemMediaTransportControls;
        smtc.IsEnabled       = true;
        smtc.IsPlayEnabled   = true;
        smtc.IsPauseEnabled  = true;
        smtc.IsNextEnabled   = true;
        smtc.IsPreviousEnabled = true;
        smtc.ButtonPressed   += OnSmtcButtonPressed;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += OnTimerTick;

        Songs.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(SongCountText));
            OnPropertyChanged(nameof(FilteredSongs));
        };

        Playlist.CollectionChanged += (_, _) =>
            OnPropertyChanged(nameof(PlaylistCountText));
    }

    // ── Public API ────────────────────────────────────────────────────────────

    // Plays from the Playlist by index
    public async void PlaySong(int index)
    {
        if (index < 0 || index >= Playlist.Count) return;

        _currentIndex = index;
        CurrentIndex  = index;
        var song = Playlist[index];

        AlbumArt    = null;
        CurrentSong = song;
        Position    = 0;

        _player.Source = MediaSource.CreateFromStorageFile(song.File);
        _player.Play();
        _timer.Start();

        UpdateSmtc(song);
        await LoadAlbumArtAsync(song);
        await LoadLyricsAsync(song);
    }

    // Double-click in library: append to playlist and play
    public void PlayFromLibrary(SongItem song)
    {
        Playlist.Add(song);
        _ = SaveTempSessionAsync();
        PlaySong(Playlist.Count - 1);
    }

    // Right-click → Add to playlist (no autoplay)
    public void AddToPlaylist(SongItem song)
    {
        Playlist.Add(song);
        _ = SaveTempSessionAsync();
    }

    // Right-click → Play next
    public void PlayNext(SongItem song)
    {
        var insertIdx = _currentIndex >= 0 ? _currentIndex + 1 : 0;
        Playlist.Insert(Math.Min(insertIdx, Playlist.Count), song);
        _ = SaveTempSessionAsync();
    }

    // Right-click on playlist → Remove from playlist
    public void RemoveFromPlaylist(int index)
    {
        if (index < 0 || index >= Playlist.Count) return;

        if (index < _currentIndex)
        {
            Playlist.RemoveAt(index);
            _currentIndex--;
            CurrentIndex = _currentIndex;
        }
        else if (index == _currentIndex)
        {
            Playlist.RemoveAt(index);
            if (Playlist.Count == 0)
            {
                StopAndReset();
            }
            else
            {
                var next = Math.Min(index, Playlist.Count - 1);
                _currentIndex = -1;
                PlaySong(next);
            }
        }
        else
        {
            Playlist.RemoveAt(index);
        }
        _ = SaveTempSessionAsync();
    }

    // Right-click on playlist → Clear playlist
    public void ClearPlaylist()
    {
        StopAndReset();
        Playlist.Clear();
        _ = Task.Run(() =>
        {
            try { File.Delete(_tempSessionPath); } catch { }
            try { File.Delete(_lastSessionPath); } catch { }
        });
    }

    public void PlayPause()
    {
        if (_currentIndex < 0)
        {
            if (Playlist.Count > 0) PlaySong(0);
            return;
        }
        if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
            _player.Pause();
        else
            _player.Play();
    }

    public void SkipPrevious()
    {
        if (Playlist.Count == 0) return;
        if (_isShuffled) { PlaySong(Random.Shared.Next(0, Playlist.Count)); return; }
        PlaySong((_currentIndex - 1 + Playlist.Count) % Playlist.Count);
    }

    public void SkipNext()
    {
        if (Playlist.Count == 0) return;
        if (_isShuffled) { PlaySong(Random.Shared.Next(0, Playlist.Count)); return; }
        PlaySong((_currentIndex + 1) % Playlist.Count);
    }

    public void ToggleShuffle() => IsShuffled = !_isShuffled;

    public void CycleRepeat() => RepeatMode = _repeatMode switch
    {
        RepeatMode.Off => RepeatMode.All,
        RepeatMode.All => RepeatMode.One,
        _              => RepeatMode.Off
    };

    // Loads folder into library (left panel). Does not touch playlist or playback.
    public async Task LoadFolderAsync(StorageFolder folder)
    {
        await Task.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_lastFolderPath)!);
            File.WriteAllText(_lastFolderPath, folder.Path, Encoding.UTF8);
        });

        Songs.Clear();

        var queryOptions = new QueryOptions(
            CommonFileQuery.OrderByName,
            new[] { ".mp3", ".flac", ".wav", ".m4a", ".wma", ".aac", ".ogg" });

        var files = await folder.CreateFileQueryWithOptions(queryOptions).GetFilesAsync().AsTask();

        foreach (var file in files)
        {
            try
            {
                var props = await file.Properties.GetMusicPropertiesAsync();
                Songs.Add(new SongItem
                {
                    File     = file,
                    Title    = props.Title  ?? string.Empty,
                    Artist   = props.Artist ?? string.Empty,
                    Album    = props.Album  ?? string.Empty,
                    Duration = props.Duration,
                });
            }
            catch { }
        }
    }

    // Drag-and-drop of individual audio files → append to playlist
    public async Task LoadFilesAsync(IList<StorageFile> files)
    {
        foreach (var file in files)
        {
            try
            {
                var props = await file.Properties.GetMusicPropertiesAsync();
                Playlist.Add(new SongItem
                {
                    File     = file,
                    Title    = props.Title  ?? string.Empty,
                    Artist   = props.Artist ?? string.Empty,
                    Album    = props.Album  ?? string.Empty,
                    Duration = props.Duration,
                });
            }
            catch { }
        }
        await SaveTempSessionAsync();
    }

    // ── Private ──────────────────────────────────────────────────────────────
    private void StopAndReset()
    {
        _timer.Stop();
        _player.Pause();
        IsPlaying   = false;
        Position    = 0;
        Duration    = 1;
        AlbumArt    = null;
        CurrentSong = null;
        _currentIndex = -1;
        CurrentIndex  = -1;
        Lyrics.Clear();
        HasLyrics = false;
        _lyricsAreSynced = false;
        _currentLyricIdx = -1;
        CurrentLyricIndex = -1;
    }

    private void UpdateSmtc(SongItem song)
    {
        var updater = _player.SystemMediaTransportControls.DisplayUpdater;
        updater.Type = MediaPlaybackType.Music;
        updater.MusicProperties.Title       = song.DisplayTitle;
        updater.MusicProperties.Artist      = song.DisplayArtist;
        updater.MusicProperties.AlbumTitle  = song.DisplayAlbum;
        try { updater.Thumbnail = RandomAccessStreamReference.CreateFromFile(song.File); }
        catch { updater.Thumbnail = null; }
        updater.Update();
    }

    private async Task LoadAlbumArtAsync(SongItem song)
    {
        try
        {
            using var thumb = await song.File.GetThumbnailAsync(ThumbnailMode.MusicView, 300).AsTask();
            if (thumb?.Type == ThumbnailType.Image)
            {
                var bmp = new BitmapImage();
                await bmp.SetSourceAsync(thumb);
                if (CurrentSong == song)
                    AlbumArt = bmp;
            }
        }
        catch { }
    }

    private void OnMediaEnded(MediaPlayer sender, object args)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (_repeatMode == RepeatMode.One)
            {
                _player.PlaybackSession.Position = TimeSpan.Zero;
                _player.Play();
                return;
            }
            if (_repeatMode == RepeatMode.Off && !_isShuffled && _currentIndex >= Playlist.Count - 1)
                return;
            SkipNext();
        });
    }

    private void OnSmtcButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            switch (args.Button)
            {
                case SystemMediaTransportControlsButton.Play:     _player.Play();  break;
                case SystemMediaTransportControlsButton.Pause:    _player.Pause(); break;
                case SystemMediaTransportControlsButton.Next:     SkipNext();      break;
                case SystemMediaTransportControlsButton.Previous: SkipPrevious();  break;
            }
        });
    }

    private void OnPlaybackStateChanged(MediaPlaybackSession sender, object args)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            IsPlaying = sender.PlaybackState == MediaPlaybackState.Playing;
            _player.SystemMediaTransportControls.PlaybackStatus = sender.PlaybackState switch
            {
                MediaPlaybackState.Playing => MediaPlaybackStatus.Playing,
                MediaPlaybackState.Paused  => MediaPlaybackStatus.Paused,
                _                          => MediaPlaybackStatus.Stopped
            };
        });
    }

    private void OnNaturalDurationChanged(MediaPlaybackSession sender, object args)
        => _dispatcherQueue.TryEnqueue(() => Duration = sender.NaturalDuration.TotalSeconds);

    private void OnTimerTick(object? sender, object e)
    {
        _updatingFromTimer = true;
        var pos = _player.PlaybackSession.Position;
        Position = pos.TotalSeconds;
        _updatingFromTimer = false;

        if (_lyricsAreSynced && Lyrics.Count > 0)
            UpdateCurrentLyric(pos);
    }

    // ── Lyrics ────────────────────────────────────────────────────────────────
    private async Task LoadLyricsAsync(SongItem song)
    {
        Lyrics.Clear();
        HasLyrics = false;
        _lyricsAreSynced = false;
        _currentLyricIdx = -1;
        CurrentLyricIndex = -1;

        try
        {
            var lrcPath = Path.ChangeExtension(song.File.Path, ".lrc");
            if (File.Exists(lrcPath))
            {
                var text  = await Task.Run(() => File.ReadAllText(lrcPath, Encoding.UTF8));
                var lines = ParseLrc(text);
                if (lines.Count > 0)
                {
                    foreach (var l in lines) Lyrics.Add(l);
                    _lyricsAreSynced = true;
                    HasLyrics = true;
                    return;
                }
            }
        }
        catch { }

        try
        {
            var props = await song.File.Properties.RetrievePropertiesAsync(
                new[] { "System.Music.Lyrics" });
            if (props.TryGetValue("System.Music.Lyrics", out var raw) &&
                raw is string lyricsText && !string.IsNullOrWhiteSpace(lyricsText))
            {
                var parsed = ParseLrc(lyricsText);
                if (parsed.Count > 0)
                {
                    foreach (var l in parsed) Lyrics.Add(l);
                    _lyricsAreSynced = true;
                    HasLyrics = true;
                    return;
                }

                var plain = lyricsText
                    .Split('\n')
                    .Select(l => l.Trim())
                    .Where(l => !string.IsNullOrEmpty(l))
                    .Select(l => new LyricLine { Time = TimeSpan.Zero, Text = l, IsCurrent = true });
                foreach (var l in plain) Lyrics.Add(l);
                HasLyrics = Lyrics.Count > 0;
            }
        }
        catch { }
    }

    private static readonly Regex _lrcTimestamp =
        new(@"\[(\d{2}):(\d{2})\.(\d{2,3})\]", RegexOptions.Compiled);

    private static List<LyricLine> ParseLrc(string content)
    {
        var result = new List<LyricLine>();
        foreach (var rawLine in content.Split('\n'))
        {
            var line    = rawLine.Trim();
            var matches = _lrcTimestamp.Matches(line);
            if (matches.Count == 0) continue;

            var textStart = matches[matches.Count - 1].Index + matches[matches.Count - 1].Length;
            var text      = line[textStart..].Trim();
            if (string.IsNullOrEmpty(text)) continue;

            foreach (Match m in matches)
            {
                var min = int.Parse(m.Groups[1].Value);
                var sec = int.Parse(m.Groups[2].Value);
                var raw = m.Groups[3].Value;
                var ms  = int.Parse(raw) * (raw.Length == 2 ? 10 : 1);
                result.Add(new LyricLine
                {
                    Time = new TimeSpan(0, 0, min, sec, ms),
                    Text = text,
                });
            }
        }
        return result.OrderBy(l => l.Time).ToList();
    }

    private void UpdateCurrentLyric(TimeSpan position)
    {
        var newIdx = -1;
        for (var i = Lyrics.Count - 1; i >= 0; i--)
        {
            if (Lyrics[i].Time <= position) { newIdx = i; break; }
        }
        if (newIdx == _currentLyricIdx) return;

        if (_currentLyricIdx >= 0 && _currentLyricIdx < Lyrics.Count)
            Lyrics[_currentLyricIdx].IsCurrent = false;

        _currentLyricIdx  = newIdx;
        CurrentLyricIndex = newIdx;

        if (_currentLyricIdx >= 0 && _currentLyricIdx < Lyrics.Count)
            Lyrics[_currentLyricIdx].IsCurrent = true;
    }

    // ── Session save / restore ────────────────────────────────────────────────
    public async Task SaveTempSessionAsync()
    {
        if (Playlist.Count == 0) return;
        var content = BuildM3U();
        await Task.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_tempSessionPath)!);
            File.WriteAllText(_tempSessionPath, content, Encoding.UTF8);
        });
    }

    public async Task SaveLastSessionAsync()
    {
        if (Playlist.Count == 0) return;
        var content = BuildM3U();
        await Task.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_lastSessionPath)!);
            File.WriteAllText(_lastSessionPath, content, Encoding.UTF8);
            try { File.Delete(_tempSessionPath); } catch { }
        });
    }

    public async Task<int> RestoreStartupAsync()
    {
        // 1. Reload library from last-opened folder
        try
        {
            var savedPath = File.Exists(_lastFolderPath)
                ? File.ReadAllText(_lastFolderPath).Trim()
                : null;
            if (savedPath != null)
            {
                var folder = await StorageFolder.GetFolderFromPathAsync(savedPath).AsTask();
                await LoadFolderAsync(folder);
            }
        }
        catch { }

        // 2. Restore playlist (crash recovery takes priority over clean-close)
        if (File.Exists(_tempSessionPath))
            return await RestoreSessionFromFileAsync(_tempSessionPath);
        if (File.Exists(_lastSessionPath))
            return await RestoreSessionFromFileAsync(_lastSessionPath);

        return Playlist.Count;
    }

    private async Task<int> RestoreSessionFromFileAsync(string path)
    {
        string[] lines;
        try { lines = await Task.Run(() => File.ReadAllLines(path, Encoding.UTF8)); }
        catch { return 0; }

        var filePaths = lines.Where(l => l.Length > 0 && l[0] != '#').ToList();
        if (filePaths.Count == 0) return 0;

        foreach (var fp in filePaths)
        {
            try
            {
                var file  = await StorageFile.GetFileFromPathAsync(fp).AsTask();
                var props = await file.Properties.GetMusicPropertiesAsync();
                Playlist.Add(new SongItem
                {
                    File     = file,
                    Title    = props.Title  ?? string.Empty,
                    Artist   = props.Artist ?? string.Empty,
                    Album    = props.Album  ?? string.Empty,
                    Duration = props.Duration,
                });
            }
            catch { }
        }

        return Playlist.Count;
    }

    private string BuildM3U()
    {
        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        foreach (var s in Playlist)
        {
            var secs  = (int)s.Duration.TotalSeconds;
            var label = string.IsNullOrWhiteSpace(s.Artist)
                ? s.DisplayTitle
                : $"{s.Artist} - {s.DisplayTitle}";
            sb.AppendLine($"#EXTINF:{secs},{label}");
            sb.AppendLine(s.File.Path);
        }
        return sb.ToString();
    }

    // ── INotifyPropertyChanged ────────────────────────────────────────────────
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
