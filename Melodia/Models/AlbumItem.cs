using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Melodia.Models;

public class AlbumItem : INotifyPropertyChanged
{
    public string Name   { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    public List<SongItem> Songs { get; init; } = new();

    private BitmapImage? _art;
    public BitmapImage? Art
    {
        get => _art;
        set
        {
            _art = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ArtVisibility));
            OnPropertyChanged(nameof(PlaceholderVisibility));
        }
    }

    public Visibility ArtVisibility         => _art != null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PlaceholderVisibility => _art == null ? Visibility.Visible : Visibility.Collapsed;

    public string TrackCountText =>
        Songs.Count == 1 ? "1 song" : $"{Songs.Count} songs";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
