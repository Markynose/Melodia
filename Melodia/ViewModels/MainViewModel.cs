using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
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
public enum SortMode { Default, Title, Artist, Album, Duration, Plays }

public class MainViewModel : INotifyPropertyChanged
{
    private readonly MediaPlayer _player;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherQueue _dispatcherQueue;
    private bool _updatingFromTimer;
    private int _currentIndex = -1;

    private FileSystemWatcher? _watcher;
    private string? _watchedFolderPath;
    private DispatcherTimer? _reloadDebounceTimer;

    private readonly Dictionary<string, int> _playCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _favoriteKeys = new(StringComparer.OrdinalIgnoreCase);
    private bool _favoritesOnly;

    private string _lastFmApiKey     = string.Empty;
    private string _lastFmApiSecret  = string.Empty;
    private string _lastFmSessionKey = string.Empty;
    private string _lastFmUsername   = string.Empty;
    private bool   _scrobbled;
    private long   _songStartTimestamp;
    private SongItem? _scrobbleSong;

    private readonly Dictionary<string, CachedMeta> _metadataCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _metadataCacheDirty;

    private bool _isLibraryLoading;
    private DispatcherTimer? _loadingDotsTimer;
    private int _dotsCount;

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
                OnPropertyChanged(nameof(NowPlayingPlays));
                OnPropertyChanged(nameof(NowPlayingPlaysVisibility));
                OnPropertyChanged(nameof(NowPlayingFormat));
                OnPropertyChanged(nameof(NowPlayingFormatVisibility));
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

    // ── Favorites filter ─────────────────────────────────────────────────────
    public bool FavoritesOnly
    {
        get => _favoritesOnly;
        set
        {
            if (SetProperty(ref _favoritesOnly, value))
            {
                OnPropertyChanged(nameof(FilteredSongs));
                OnPropertyChanged(nameof(SongCountText));
            }
        }
    }

    // ── Last.fm ───────────────────────────────────────────────────────────────
    public string LastFmStatus   => string.IsNullOrEmpty(_lastFmSessionKey) ? "Not connected" : $"Connected as {_lastFmUsername}";
    public string LastFmApiKey   => _lastFmApiKey;
    public string LastFmUsername => _lastFmUsername;

    // ── Albums ───────────────────────────────────────────────────────────────
    public ObservableCollection<AlbumItem> Albums { get; } = new();
    public string AlbumCountText => Albums.Count == 1 ? "1 album" : $"{Albums.Count} albums";

    // ── Library loading state ─────────────────────────────────────────────────
    public bool IsLibraryLoading
    {
        get => _isLibraryLoading;
        private set
        {
            if (SetProperty(ref _isLibraryLoading, value))
            {
                OnPropertyChanged(nameof(LibraryLoadingVisibility));
                if (value) StartLoadingDots();
                else       StopLoadingDots();
            }
        }
    }

    public Visibility LibraryLoadingVisibility => _isLibraryLoading ? Visibility.Visible : Visibility.Collapsed;

    private string _libraryLoadingLabel = string.Empty;
    public string LibraryLoadingLabel
    {
        get => _libraryLoadingLabel;
        private set => SetProperty(ref _libraryLoadingLabel, value);
    }

    private void StartLoadingDots()
    {
        _dotsCount = 0;
        LibraryLoadingLabel = "Indexing";
        if (_loadingDotsTimer == null)
        {
            _loadingDotsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _loadingDotsTimer.Tick += (_, _) =>
            {
                _dotsCount = (_dotsCount + 1) % 4;
                LibraryLoadingLabel = _dotsCount switch
                {
                    1 => "Indexing.",
                    2 => "Indexing..",
                    3 => "Indexing...",
                    _ => "Indexing",
                };
            };
        }
        _loadingDotsTimer.Start();
    }

    private void StopLoadingDots()
    {
        _loadingDotsTimer?.Stop();
        LibraryLoadingLabel = string.Empty;
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
            {
                OnPropertyChanged(nameof(LyricsVisibility));
                OnPropertyChanged(nameof(LyricsSectionVisibility));
            }
        }
    }

    private bool _lyricsLoading;
    public bool LyricsLoading
    {
        get => _lyricsLoading;
        private set
        {
            if (SetProperty(ref _lyricsLoading, value))
            {
                OnPropertyChanged(nameof(LyricsLoadingVisibility));
                OnPropertyChanged(nameof(LyricsSectionVisibility));
            }
        }
    }

    private bool _lyricsAreSynced;
    public Visibility LyricsVisibility        => _hasLyrics     ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LyricsLoadingVisibility => _lyricsLoading ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LyricsSectionVisibility => (_hasLyrics || _lyricsLoading) ? Visibility.Visible : Visibility.Collapsed;

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
    public string NowPlayingTitle  => _currentSong?.DisplayTitle ?? "No song selected";
    public string NowPlayingPlays  => _currentSong?.PlayCountText ?? string.Empty;
    public Visibility NowPlayingPlaysVisibility =>
        _currentSong?.PlayCount > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string NowPlayingFormat => _currentSong?.FormatText ?? string.Empty;
    public Visibility NowPlayingFormatVisibility =>
        !string.IsNullOrEmpty(_currentSong?.FormatText) ? Visibility.Visible : Visibility.Collapsed;

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
            if (_favoritesOnly || !string.IsNullOrWhiteSpace(_filterText))
            {
                var n = FilteredSongs.Count();
                return $"{n} of {Songs.Count} songs";
            }
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
            if (_favoritesOnly)
                q = q.Where(s => s.IsFavorite);
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
        SortMode.Plays    => q.OrderByDescending(s => s.PlayCount)
                              .ThenBy(s => s.DisplayTitle, StringComparer.OrdinalIgnoreCase),
        _                 => q
    };

    // ── HTTP ─────────────────────────────────────────────────────────────────
    private static readonly HttpClient _http = new();

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
    private static readonly string _lyricsCachePath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Melodia", "lyrics");
    private static readonly string _notesCachePath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Melodia", "notes");
    private static readonly string _playCountsPath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Melodia", "playcounts.json");
    private static readonly string _favoritesPath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Melodia", "favorites.txt");
    private static readonly string _lastFmConfigPath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Melodia", "lastfm.json");
    private static readonly string _metadataCachePath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Melodia", "metadata_cache.json");
    private static readonly HashSet<string> _audioExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".flac", ".wav", ".m4a", ".wma", ".aac", ".ogg" };

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

        _scrobbled          = false;
        _scrobbleSong       = song;
        _songStartTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        _player.Source = MediaSource.CreateFromStorageFile(song.File);
        _player.Play();
        _timer.Start();

        UpdateSmtc(song);
        _ = UpdateNowPlayingAsync(song);
        if (song.Bitrate <= 0 || song.SampleRate <= 0)
            _ = LoadAudioFormatAsync(song);
        await LoadAlbumArtAsync(song);
        await LoadLyricsAsync(song);
    }

    // Double-click in library: append to playlist; play only if nothing is active
    public void PlayFromLibrary(SongItem song)
    {
        Playlist.Add(song);
        _ = SaveTempSessionAsync();
        if (_currentIndex < 0)
            PlaySong(Playlist.Count - 1);
    }

    // Album right-click actions
    public void PlayAlbum(AlbumItem album)
    {
        if (album.Songs.Count == 0) return;
        var startIdx = Playlist.Count;
        foreach (var song in album.Songs)
            Playlist.Add(song);
        _ = SaveTempSessionAsync();
        PlaySong(startIdx);
    }

    public void AddAlbumToPlaylist(AlbumItem album)
    {
        foreach (var song in album.Songs)
            Playlist.Add(song);
        _ = SaveTempSessionAsync();
    }

    public void PlayAlbumNext(AlbumItem album)
    {
        var insertIdx = _currentIndex >= 0 ? _currentIndex + 1 : 0;
        for (int i = album.Songs.Count - 1; i >= 0; i--)
            Playlist.Insert(Math.Min(insertIdx, Playlist.Count), album.Songs[i]);
        _ = SaveTempSessionAsync();
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

    // Called after playlist drag-reorder to keep _currentIndex in sync
    public void ResyncCurrentIndex()
    {
        if (_currentSong == null) return;
        var idx = Playlist.IndexOf(_currentSong);
        if (idx < 0 || idx == _currentIndex) return;
        _currentIndex = idx;
        CurrentIndex  = idx;
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
        IsLibraryLoading = true;
        Songs.Clear();
        Albums.Clear();

        await Task.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_lastFolderPath)!);
            File.WriteAllText(_lastFolderPath, folder.Path, Encoding.UTF8);
        });

        var queryOptions = new QueryOptions(CommonFileQuery.OrderByName, _audioExtensions.ToArray());

        var files = await folder.CreateFileQueryWithOptions(queryOptions).GetFilesAsync().AsTask();

        var lastWriteTimes = await Task.Run(() =>
        {
            var d = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                try { d[f.Path] = File.GetLastWriteTimeUtc(f.Path).Ticks; }
                catch { d[f.Path] = 0; }
            }
            return d;
        });

        foreach (var file in files)
        {
            try
            {
                var path = file.Path;
                var lwt  = lastWriteTimes.TryGetValue(path, out var t) ? t : 0;

                if (lwt != 0 && _metadataCache.TryGetValue(path, out var cached) && cached.LastWriteUtcTicks == lwt)
                {
                    Songs.Add(new SongItem
                    {
                        File        = file,
                        Title       = cached.Title,
                        Artist      = cached.Artist,
                        Album       = cached.Album,
                        Duration    = TimeSpan.FromTicks(cached.DurationTicks),
                        TrackNumber = cached.TrackNumber,
                        DiscNumber  = cached.DiscNumber,
                    });
                }
                else
                {
                    var props       = await file.Properties.GetMusicPropertiesAsync();
                    var title       = props.Title  ?? string.Empty;
                    var artist      = props.Artist ?? string.Empty;
                    var album       = props.Album  ?? string.Empty;
                    var duration    = props.Duration;
                    var trackNumber = (int)props.TrackNumber;
                    var discNumber  = 1;
                    try
                    {
                        var discProps = await file.Properties.RetrievePropertiesAsync(
                            new[] { "System.Music.DiscNumber" });
                        if (discProps.TryGetValue("System.Music.DiscNumber", out var dn) && dn != null)
                        {
                            var d = Convert.ToInt32(dn);
                            if (d > 0) discNumber = d;
                        }
                    }
                    catch { }

                    Songs.Add(new SongItem
                    {
                        File        = file,
                        Title       = title,
                        Artist      = artist,
                        Album       = album,
                        Duration    = duration,
                        TrackNumber = trackNumber,
                        DiscNumber  = discNumber,
                    });
                    if (lwt != 0)
                    {
                        _metadataCache[path] = new CachedMeta(title, artist, album, duration.Ticks, lwt, trackNumber, discNumber);
                        _metadataCacheDirty  = true;
                    }
                }
            }
            catch { }
        }

        if (_metadataCacheDirty)
            _ = SaveMetadataCacheAsync();

        ApplyPlayCountsToSongs();
        ApplyFavoritesToSongs();
        RebuildAlbums();
        SetupFolderWatcher(folder.Path);
        IsLibraryLoading = false;
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
        RebuildAlbums();
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
        LyricsLoading = false;
        _lyricsAreSynced = false;
        _currentLyricIdx = -1;
        CurrentLyricIndex = -1;
        _scrobbled    = true;
        _scrobbleSong = null;
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

    private async Task LoadAudioFormatAsync(SongItem song)
    {
        try
        {
            var props = await song.File.Properties.RetrievePropertiesAsync(
                new[] { "System.Audio.EncodingBitrate", "System.Audio.SampleRate" });
            if (CurrentSong != song) return;

            if (props.TryGetValue("System.Audio.EncodingBitrate", out var br))
                song.Bitrate = Convert.ToInt32(br ?? 0);
            if (props.TryGetValue("System.Audio.SampleRate", out var sr))
                song.SampleRate = Convert.ToInt32(sr ?? 0);

            OnPropertyChanged(nameof(NowPlayingFormat));
            OnPropertyChanged(nameof(NowPlayingFormatVisibility));
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

        if (!_scrobbled && _scrobbleSong != null && _duration > 30 &&
            _position >= Math.Max(30, _duration * 0.5))
        {
            _scrobbled = true;
            IncrementPlayCount(_scrobbleSong);
            _ = ScrobbleAsync(_scrobbleSong, _songStartTimestamp);
        }
    }

    // ── Albums ────────────────────────────────────────────────────────────────
    private void RebuildAlbums()
    {
        Albums.Clear();
        var groups = Songs
            .GroupBy(s => s.Album.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => string.IsNullOrWhiteSpace(g.Key) ? "\xFF" : g.Key,
                     StringComparer.OrdinalIgnoreCase);

        foreach (var g in groups)
        {
            var dominantArtist = g
                .GroupBy(s => s.Artist, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(x => x.Count())
                .First().Key;

            var album = new AlbumItem
            {
                Name   = string.IsNullOrWhiteSpace(g.Key) ? "Unknown Album" : g.Key,
                Artist = string.IsNullOrWhiteSpace(dominantArtist) ? "Unknown Artist" : dominantArtist,
                Songs  = g.OrderBy(s => s.DiscNumber)
                          .ThenBy(s => s.TrackNumber)
                          .ThenBy(s => s.DisplayTitle, StringComparer.OrdinalIgnoreCase)
                          .ToList(),
            };
            Albums.Add(album);
            _ = LoadAlbumThumbnailAsync(album);
        }
        OnPropertyChanged(nameof(AlbumCountText));
    }

    private async Task LoadAlbumThumbnailAsync(AlbumItem album)
    {
        var song = album.Songs.FirstOrDefault();
        if (song == null) return;
        try
        {
            using var thumb = await song.File.GetThumbnailAsync(
                ThumbnailMode.MusicView, 200).AsTask();
            if (thumb?.Type == ThumbnailType.Image)
            {
                var bmp = new BitmapImage();
                await bmp.SetSourceAsync(thumb);
                album.Art = bmp;
            }
        }
        catch { }
    }

    // ── Folder watch ─────────────────────────────────────────────────────────
    private void SetupFolderWatcher(string path)
    {
        _watchedFolderPath = path;
        _watcher?.Dispose();
        _watcher = new FileSystemWatcher(path)
        {
            NotifyFilter = NotifyFilters.FileName,
            EnableRaisingEvents = true
        };
        _watcher.Created += OnFolderFileChanged;
        _watcher.Deleted += OnFolderFileChanged;
        _watcher.Renamed += OnFolderFileChanged;
    }

    private void OnFolderFileChanged(object sender, FileSystemEventArgs e)
    {
        if (e.Name == null || !_audioExtensions.Contains(Path.GetExtension(e.Name))) return;
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (_reloadDebounceTimer == null)
            {
                _reloadDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                _reloadDebounceTimer.Tick += ReloadDebounce_Tick;
            }
            _reloadDebounceTimer.Stop();
            _reloadDebounceTimer.Start();
        });
    }

    private async void ReloadDebounce_Tick(object? sender, object e)
    {
        _reloadDebounceTimer!.Stop();
        if (_watchedFolderPath == null) return;
        try
        {
            var folder = await StorageFolder.GetFolderFromPathAsync(_watchedFolderPath).AsTask();
            await LoadFolderAsync(folder);
        }
        catch { }
    }

    // ── Notes ─────────────────────────────────────────────────────────────────
    public string GetNote(SongItem song)
    {
        try
        {
            var file = GetNotesFile(song);
            return File.Exists(file) ? File.ReadAllText(file, Encoding.UTF8) : string.Empty;
        }
        catch { return string.Empty; }
    }

    public void SaveNote(SongItem song, string note)
    {
        try
        {
            var file = GetNotesFile(song);
            if (string.IsNullOrWhiteSpace(note))
            {
                if (File.Exists(file)) File.Delete(file);
            }
            else
            {
                Directory.CreateDirectory(_notesCachePath);
                File.WriteAllText(file, note.Trim(), Encoding.UTF8);
            }
        }
        catch { }
    }

    private static string GetNotesFile(SongItem song)
    {
        var artist = string.IsNullOrWhiteSpace(song.Artist) ? "Unknown Artist" : song.Artist;
        var title  = string.IsNullOrWhiteSpace(song.Title)  ? song.File.Name   : song.Title;
        var name   = $"{artist} - {title}.txt";
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return Path.Combine(_notesCachePath, name);
    }

    // ── Metadata cache ────────────────────────────────────────────────────────
    private async Task LoadMetadataCacheAsync()
    {
        if (!File.Exists(_metadataCachePath)) return;
        try
        {
            var json = await Task.Run(() => File.ReadAllText(_metadataCachePath, Encoding.UTF8));
            using var doc = JsonDocument.Parse(json);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var v = prop.Value;
                if (!v.TryGetProperty("title",             out var tEl)) continue;
                v.TryGetProperty("artist",             out var aEl);
                v.TryGetProperty("album",              out var alEl);
                v.TryGetProperty("durationTicks",      out var dtEl);
                v.TryGetProperty("lastWriteUtcTicks",  out var lwEl);
                v.TryGetProperty("trackNumber",        out var tnEl);
                v.TryGetProperty("discNumber",         out var dnEl);
                _metadataCache[prop.Name] = new CachedMeta(
                    tEl.GetString()  ?? string.Empty,
                    aEl.ValueKind  == JsonValueKind.String ? aEl.GetString()  ?? string.Empty : string.Empty,
                    alEl.ValueKind == JsonValueKind.String ? alEl.GetString() ?? string.Empty : string.Empty,
                    dtEl.ValueKind == JsonValueKind.Number ? dtEl.GetInt64()  : 0,
                    lwEl.ValueKind == JsonValueKind.Number ? lwEl.GetInt64()  : 0,
                    tnEl.ValueKind == JsonValueKind.Number ? tnEl.GetInt32()  : 0,
                    dnEl.ValueKind == JsonValueKind.Number ? dnEl.GetInt32()  : 1);
            }
        }
        catch { }
    }

    private async Task SaveMetadataCacheAsync()
    {
        if (!_metadataCacheDirty) return;
        _metadataCacheDirty = false;
        var snapshot = _metadataCache.ToDictionary(
            kv => kv.Key,
            kv => new
            {
                title             = kv.Value.Title,
                artist            = kv.Value.Artist,
                album             = kv.Value.Album,
                durationTicks     = kv.Value.DurationTicks,
                lastWriteUtcTicks = kv.Value.LastWriteUtcTicks,
                trackNumber       = kv.Value.TrackNumber,
                discNumber        = kv.Value.DiscNumber,
            });
        await Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_metadataCachePath)!);
                File.WriteAllText(_metadataCachePath, JsonSerializer.Serialize(snapshot), Encoding.UTF8);
            }
            catch { }
        });
    }

    // ── Play counts ───────────────────────────────────────────────────────────
    private void IncrementPlayCount(SongItem song)
    {
        var key = GetSongKey(song);
        _playCounts[key] = _playCounts.TryGetValue(key, out var n) ? n + 1 : 1;
        song.PlayCount = _playCounts[key];
        OnPropertyChanged(nameof(NowPlayingPlays));
        OnPropertyChanged(nameof(NowPlayingPlaysVisibility));
        _ = SavePlayCountsAsync();
    }

    private void ApplyPlayCountsToSongs()
    {
        foreach (var song in Songs)
            if (_playCounts.TryGetValue(GetSongKey(song), out var n))
                song.PlayCount = n;
    }

    private async Task LoadPlayCountsAsync()
    {
        if (!File.Exists(_playCountsPath)) return;
        try
        {
            var json = await Task.Run(() => File.ReadAllText(_playCountsPath, Encoding.UTF8));
            using var doc = JsonDocument.Parse(json);
            foreach (var prop in doc.RootElement.EnumerateObject())
                if (prop.Value.TryGetInt32(out var count))
                    _playCounts[prop.Name] = count;
        }
        catch { }
    }

    private async Task SavePlayCountsAsync()
    {
        var snapshot = _playCounts.ToDictionary(kv => kv.Key, kv => kv.Value);
        await Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_playCountsPath)!);
                File.WriteAllText(_playCountsPath, JsonSerializer.Serialize(snapshot), Encoding.UTF8);
            }
            catch { }
        });
    }

    // ── Favorites ─────────────────────────────────────────────────────────────
    public async void ToggleFavorite(SongItem song)
    {
        song.IsFavorite = !song.IsFavorite;
        if (song.IsFavorite) _favoriteKeys.Add(GetSongKey(song));
        else                 _favoriteKeys.Remove(GetSongKey(song));
        if (_favoritesOnly)
        {
            OnPropertyChanged(nameof(FilteredSongs));
            OnPropertyChanged(nameof(SongCountText));
        }
        await SaveFavoritesAsync();
    }

    private async Task LoadFavoritesAsync()
    {
        if (!File.Exists(_favoritesPath)) return;
        try
        {
            var lines = await Task.Run(() => File.ReadAllLines(_favoritesPath, Encoding.UTF8));
            foreach (var l in lines)
                if (!string.IsNullOrWhiteSpace(l)) _favoriteKeys.Add(l.Trim());
        }
        catch { }
    }

    private void ApplyFavoritesToSongs()
    {
        foreach (var song in Songs)
            song.IsFavorite = _favoriteKeys.Contains(GetSongKey(song));
    }

    private async Task SaveFavoritesAsync()
    {
        var lines = _favoriteKeys.ToArray();
        await Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_favoritesPath)!);
                File.WriteAllLines(_favoritesPath, lines, Encoding.UTF8);
            }
            catch { }
        });
    }

    private static string GetSongKey(SongItem song)
    {
        var artist = string.IsNullOrWhiteSpace(song.Artist) ? "Unknown Artist" : song.Artist;
        var title  = string.IsNullOrWhiteSpace(song.Title)  ? song.File.Name   : song.Title;
        return $"{artist} - {title}";
    }

    // ── Last.fm ───────────────────────────────────────────────────────────────
    public async Task<bool> ConnectLastFmAsync(string username, string password, string apiKey, string apiSecret)
    {
        _lastFmApiKey    = apiKey;
        _lastFmApiSecret = apiSecret;

        var p = new Dictionary<string, string>
        {
            ["method"]   = "auth.getMobileSession",
            ["username"] = username,
            ["password"] = password,
            ["api_key"]  = apiKey,
        };
        p["api_sig"] = LastFmSig(p);
        p["format"]  = "json";

        try
        {
            using var resp = await _http.PostAsync(
                "https://ws.audioscrobbler.com/2.0/",
                new FormUrlEncodedContent(p));
            var json = await resp.Content.ReadAsStringAsync();
            using var doc  = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("session", out var sess) &&
                sess.TryGetProperty("key", out var keyEl))
            {
                _lastFmSessionKey = keyEl.GetString() ?? string.Empty;
                _lastFmUsername   = username;
                OnPropertyChanged(nameof(LastFmStatus));
                await SaveLastFmConfigAsync();
                return true;
            }
        }
        catch { }
        return false;
    }

    public async void DisconnectLastFm()
    {
        _lastFmSessionKey = string.Empty;
        _lastFmUsername   = string.Empty;
        OnPropertyChanged(nameof(LastFmStatus));
        await SaveLastFmConfigAsync();
    }

    private async Task LoadLastFmConfigAsync()
    {
        if (!File.Exists(_lastFmConfigPath)) return;
        try
        {
            var json = await Task.Run(() => File.ReadAllText(_lastFmConfigPath, Encoding.UTF8));
            using var doc  = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("apiKey",     out var k))  _lastFmApiKey     = k.GetString()  ?? "";
            if (root.TryGetProperty("apiSecret",  out var s))  _lastFmApiSecret  = s.GetString()  ?? "";
            if (root.TryGetProperty("username",   out var u))  _lastFmUsername   = u.GetString()  ?? "";
            if (root.TryGetProperty("sessionKey", out var sk)) _lastFmSessionKey = sk.GetString() ?? "";
            OnPropertyChanged(nameof(LastFmStatus));
            OnPropertyChanged(nameof(LastFmApiKey));
            OnPropertyChanged(nameof(LastFmUsername));
        }
        catch { }
    }

    private async Task SaveLastFmConfigAsync()
    {
        var json = JsonSerializer.Serialize(new
        {
            apiKey     = _lastFmApiKey,
            apiSecret  = _lastFmApiSecret,
            username   = _lastFmUsername,
            sessionKey = _lastFmSessionKey,
        });
        await Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_lastFmConfigPath)!);
                File.WriteAllText(_lastFmConfigPath, json, Encoding.UTF8);
            }
            catch { }
        });
    }

    private async Task UpdateNowPlayingAsync(SongItem song)
    {
        if (string.IsNullOrEmpty(_lastFmSessionKey)) return;
        var p = new Dictionary<string, string>
        {
            ["method"]  = "track.updateNowPlaying",
            ["artist"]  = song.DisplayArtist,
            ["track"]   = song.DisplayTitle,
            ["api_key"] = _lastFmApiKey,
            ["sk"]      = _lastFmSessionKey,
        };
        p["api_sig"] = LastFmSig(p);
        p["format"]  = "json";
        try { await _http.PostAsync("https://ws.audioscrobbler.com/2.0/", new FormUrlEncodedContent(p)); }
        catch { }
    }

    private async Task ScrobbleAsync(SongItem song, long timestamp)
    {
        if (string.IsNullOrEmpty(_lastFmSessionKey)) return;
        var p = new Dictionary<string, string>
        {
            ["method"]    = "track.scrobble",
            ["artist"]    = song.DisplayArtist,
            ["track"]     = song.DisplayTitle,
            ["timestamp"] = timestamp.ToString(),
            ["api_key"]   = _lastFmApiKey,
            ["sk"]        = _lastFmSessionKey,
        };
        p["api_sig"] = LastFmSig(p);
        p["format"]  = "json";
        try { await _http.PostAsync("https://ws.audioscrobbler.com/2.0/", new FormUrlEncodedContent(p)); }
        catch { }
    }

    private string LastFmSig(Dictionary<string, string> p)
    {
        var body = string.Concat(p.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                                  .Select(kv => kv.Key + kv.Value));
        return MD5Hash(body + _lastFmApiSecret);
    }

    private static string MD5Hash(string input)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return string.Concat(bytes.Select(b => b.ToString("x2")));
    }

    // ── Lyrics ────────────────────────────────────────────────────────────────
    private async Task LoadLyricsAsync(SongItem song)
    {
        Lyrics.Clear();
        HasLyrics = false;
        LyricsLoading = false;
        _lyricsAreSynced = false;
        _currentLyricIdx = -1;
        CurrentLyricIndex = -1;

        // 1. LRC sidecar next to audio file
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

        // 2. Embedded tag
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
                return;
            }
        }
        catch { }

        // 3. Lyrics cache
        try
        {
            var cacheFile = GetLyricsCacheFile(song);
            if (File.Exists(cacheFile))
            {
                var text  = await Task.Run(() => File.ReadAllText(cacheFile, Encoding.UTF8));
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

        // 4. lrclib.net
        if (string.IsNullOrWhiteSpace(song.Title)) return;
        LyricsLoading = true;
        await FetchLyricsFromNetAsync(song);
        if (CurrentSong == song) LyricsLoading = false;
    }

    private static string GetLyricsCacheFile(SongItem song)
    {
        var artist = string.IsNullOrWhiteSpace(song.Artist) ? "Unknown Artist" : song.Artist;
        var title  = string.IsNullOrWhiteSpace(song.Title)  ? song.File.Name   : song.Title;
        var name   = $"{artist} - {title}.lrc";
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return Path.Combine(_lyricsCachePath, name);
    }

    private async Task FetchLyricsFromNetAsync(SongItem song)
    {
        if (string.IsNullOrWhiteSpace(song.Title)) return;

        try
        {
            var url = "https://lrclib.net/api/get" +
                      $"?artist_name={Uri.EscapeDataString(song.Artist)}" +
                      $"&track_name={Uri.EscapeDataString(song.Title)}" +
                      $"&album_name={Uri.EscapeDataString(song.Album)}" +
                      $"&duration={(int)song.Duration.TotalSeconds}";

            using var response = await _http.GetAsync(url);
            if (CurrentSong != song) return;
            if (!response.IsSuccessStatusCode) return;

            var json = await response.Content.ReadAsStringAsync();
            if (CurrentSong != song) return;

            using var doc  = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("syncedLyrics", out var syncedEl) &&
                syncedEl.ValueKind == JsonValueKind.String)
            {
                var synced = syncedEl.GetString();
                if (!string.IsNullOrWhiteSpace(synced))
                {
                    var lines = ParseLrc(synced);
                    if (lines.Count > 0)
                    {
                        var cacheFile = GetLyricsCacheFile(song);
                        _ = Task.Run(() =>
                        {
                            try
                            {
                                Directory.CreateDirectory(_lyricsCachePath);
                                File.WriteAllText(cacheFile, synced, Encoding.UTF8);
                            }
                            catch { }
                        });
                        foreach (var l in lines) Lyrics.Add(l);
                        _lyricsAreSynced = true;
                        HasLyrics = true;
                        return;
                    }
                }
            }

            if (root.TryGetProperty("plainLyrics", out var plainEl) &&
                plainEl.ValueKind == JsonValueKind.String)
            {
                var plain = plainEl.GetString();
                if (!string.IsNullOrWhiteSpace(plain))
                {
                    var lines = plain
                        .Split('\n')
                        .Select(l => l.Trim())
                        .Where(l => !string.IsNullOrEmpty(l))
                        .Select(l => new LyricLine { Time = TimeSpan.Zero, Text = l, IsCurrent = true });
                    foreach (var l in lines) Lyrics.Add(l);
                    HasLyrics = Lyrics.Count > 0;
                }
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
        await LoadPlayCountsAsync();
        await LoadFavoritesAsync();
        await LoadLastFmConfigAsync();
        await LoadMetadataCacheAsync();

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
                var file = await StorageFile.GetFileFromPathAsync(fp).AsTask();
                long lwt = 0;
                try { lwt = File.GetLastWriteTimeUtc(fp).Ticks; } catch { }

                if (lwt != 0 && _metadataCache.TryGetValue(fp, out var cached) && cached.LastWriteUtcTicks == lwt)
                {
                    Playlist.Add(new SongItem
                    {
                        File        = file,
                        Title       = cached.Title,
                        Artist      = cached.Artist,
                        Album       = cached.Album,
                        Duration    = TimeSpan.FromTicks(cached.DurationTicks),
                        TrackNumber = cached.TrackNumber,
                        DiscNumber  = cached.DiscNumber,
                    });
                }
                else
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

    private sealed record CachedMeta(string Title, string Artist, string Album, long DurationTicks, long LastWriteUtcTicks, int TrackNumber, int DiscNumber);

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
