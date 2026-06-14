using System;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;

namespace Melodia.Models;

public class SongItem
{
    public StorageFile File { get; set; } = null!;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; }
    public BitmapImage? AlbumArt { get; set; }

    public string DisplayTitle => !string.IsNullOrWhiteSpace(Title) ? Title : File.Name;
    public string DisplayArtist => !string.IsNullOrWhiteSpace(Artist) ? Artist : "Unknown Artist";
    public string DisplayAlbum => !string.IsNullOrWhiteSpace(Album) ? Album : "Unknown Album";

    public string DurationString => Duration.Hours > 0
        ? Duration.ToString(@"h\:mm\:ss")
        : Duration.ToString(@"m\:ss");
}
