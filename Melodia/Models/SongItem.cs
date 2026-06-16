using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;

namespace Melodia.Models;

public class SongItem : INotifyPropertyChanged
{
    public StorageFile File { get; set; } = null!;
    public string Title  { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album  { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; }
    public BitmapImage? AlbumArt { get; set; }

    private int _playCount;
    public int PlayCount
    {
        get => _playCount;
        set
        {
            if (_playCount == value) return;
            _playCount = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlayCount)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlayCountText)));
        }
    }

    public string PlayCountText => _playCount switch
    {
        0 => string.Empty,
        1 => "1 play",
        _ => $"{_playCount} plays"
    };

    private bool _isFavorite;
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value) return;
            _isFavorite = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFavorite)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FavoriteVisibility)));
        }
    }

    public Visibility FavoriteVisibility => _isFavorite ? Visibility.Visible : Visibility.Collapsed;

    private int _bitrate;
    public int Bitrate
    {
        get => _bitrate;
        set
        {
            if (_bitrate == value) return;
            _bitrate = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Bitrate)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FormatText)));
        }
    }

    private int _sampleRate;
    public int SampleRate
    {
        get => _sampleRate;
        set
        {
            if (_sampleRate == value) return;
            _sampleRate = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SampleRate)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FormatText)));
        }
    }

    public string FormatText
    {
        get
        {
            var bitratePart    = _bitrate    > 0 ? $"{_bitrate / 1000} kbps"        : null;
            var sampleRatePart = _sampleRate > 0 ? $"{_sampleRate / 1000.0:0.#} kHz" : null;
            return (bitratePart, sampleRatePart) switch
            {
                (not null, not null) => $"{bitratePart} · {sampleRatePart}",
                (not null, null)     => bitratePart,
                (null, not null)     => sampleRatePart,
                _                     => string.Empty
            };
        }
    }

    public int TrackNumber { get; set; }
    public int DiscNumber  { get; set; } = 1;

    public string DiscBadgeText => DiscNumber > 1 ? $"Disc {DiscNumber}" : string.Empty;
    public Visibility DiscBadgeVisibility => DiscNumber > 1 ? Visibility.Visible : Visibility.Collapsed;

    public string DisplayTitle  => !string.IsNullOrWhiteSpace(Title)  ? Title  : File.Name;
    public string DisplayArtist => !string.IsNullOrWhiteSpace(Artist) ? Artist : "Unknown Artist";
    public string DisplayAlbum  => !string.IsNullOrWhiteSpace(Album)  ? Album  : "Unknown Album";

    public string DurationString => Duration.Hours > 0
        ? Duration.ToString(@"h\:mm\:ss")
        : Duration.ToString(@"m\:ss");

    public event PropertyChangedEventHandler? PropertyChanged;
}
