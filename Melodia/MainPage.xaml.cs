using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Melodia.Models;
using Melodia.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace Melodia;

public sealed partial class MainPage : Page
{
    public MainViewModel ViewModel { get; }

    public MainPage()
    {
        ViewModel = new MainViewModel();
        InitializeComponent();

        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModel.CurrentIndex))
                SyncPlaylistSelection(ViewModel.CurrentIndex);
            else if (e.PropertyName == nameof(ViewModel.CurrentLyricIndex))
                SyncLyricsScroll(ViewModel.CurrentLyricIndex);
        };
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (App.MainWindow is { } win)
            win.Closed += async (_, _) => await ViewModel.SaveLastSessionAsync();
        await ViewModel.RestoreStartupAsync();
    }

    // ── Now-playing vertical centering ────────────────────────────────────────
    private void NowPlayingScroller_SizeChanged(object sender, SizeChangedEventArgs e)
        => NowPlayingGrid.MinHeight = e.NewSize.Height;

    // ── Playlist sync ─────────────────────────────────────────────────────────
    private void SyncPlaylistSelection(int index)
    {
        var item = index >= 0 && index < ViewModel.Playlist.Count ? ViewModel.Playlist[index] : null;

        PlaylistView.SelectionChanged -= PlaylistView_SelectionChanged;
        PlaylistView.SelectedItem = item;
        if (item != null) PlaylistView.ScrollIntoView(item);
        PlaylistView.SelectionChanged += PlaylistView_SelectionChanged;
    }

    private void SyncLyricsScroll(int index)
    {
        if (index >= 0 && index < ViewModel.Lyrics.Count)
            LyricsListView.ScrollIntoView(ViewModel.Lyrics[index]);
    }

    // ── Library (left panel) ──────────────────────────────────────────────────
    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.SuggestedStartLocation = PickerLocationId.MusicLibrary;
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!));

        var folder = await picker.PickSingleFolderAsync().AsTask();
        if (folder != null)
            await ViewModel.LoadFolderAsync(folder);
    }

    private void Sort_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => ViewModel.SortMode = (SortMode)((ComboBox)sender).SelectedIndex;

    // Double-click in library → add to playlist and play
    private void SongListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var song = FindSongItem(e.OriginalSource);
        if (song != null) ViewModel.PlayFromLibrary(song);
    }

    // Right-click in library
    private void SongListView_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var song = FindSongItem(e.OriginalSource);
        if (song == null) return;

        var menu = new MenuFlyout();

        var play = new MenuFlyoutItem { Text = "Play", Icon = new SymbolIcon(Symbol.Play) };
        play.Click += (_, _) => ViewModel.PlayFromLibrary(song);

        var add = new MenuFlyoutItem { Text = "Add to playlist", Icon = new SymbolIcon(Symbol.Add) };
        add.Click += (_, _) => ViewModel.AddToPlaylist(song);

        var next = new MenuFlyoutItem { Text = "Play next" };
        next.Click += (_, _) => ViewModel.PlayNext(song);

        var open = new MenuFlyoutItem { Text = "Open file location" };
        open.Click += async (_, _) => await OpenFileLocationAsync(song);

        menu.Items.Add(play);
        menu.Items.Add(add);
        menu.Items.Add(next);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(open);

        menu.ShowAt((UIElement)sender, e.GetPosition((UIElement)sender));
    }

    // ── Playlist (right panel) ────────────────────────────────────────────────
    private void JumpToCurrent_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.FilterText = string.Empty;
        SyncPlaylistSelection(ViewModel.CurrentIndex);
    }

    private void PlaylistView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlaylistView.SelectedItem is SongItem song)
        {
            var idx = ViewModel.Playlist.IndexOf(song);
            if (idx >= 0) ViewModel.PlaySong(idx);
        }
    }

    // Right-click in playlist
    private void PlaylistView_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var song = FindSongItem(e.OriginalSource);
        if (song == null) return;

        var idx  = ViewModel.Playlist.IndexOf(song);
        var menu = new MenuFlyout();

        var play = new MenuFlyoutItem { Text = "Play", Icon = new SymbolIcon(Symbol.Play) };
        play.Click += (_, _) => { if (idx >= 0) ViewModel.PlaySong(idx); };

        var remove = new MenuFlyoutItem { Text = "Remove from playlist", Icon = new SymbolIcon(Symbol.Remove) };
        remove.Click += (_, _) => { if (idx >= 0) ViewModel.RemoveFromPlaylist(idx); };

        var clear = new MenuFlyoutItem { Text = "Clear playlist", Icon = new SymbolIcon(Symbol.Delete) };
        clear.Click += (_, _) => ViewModel.ClearPlaylist();

        menu.Items.Add(play);
        menu.Items.Add(remove);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(clear);

        menu.ShowAt((UIElement)sender, e.GetPosition((UIElement)sender));
    }

    // ── Transport ─────────────────────────────────────────────────────────────
    private void PlayPause_Click(object sender, RoutedEventArgs e) => ViewModel.PlayPause();
    private void Previous_Click(object sender, RoutedEventArgs e)  => ViewModel.SkipPrevious();
    private void Next_Click(object sender, RoutedEventArgs e)      => ViewModel.SkipNext();
    private void Shuffle_Click(object sender, RoutedEventArgs e)   => ViewModel.ToggleShuffle();
    private void Repeat_Click(object sender, RoutedEventArgs e)    => ViewModel.CycleRepeat();

    // ── Lyrics ────────────────────────────────────────────────────────────────
    private void LyricLine_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is LyricLine line && line.Time > TimeSpan.Zero)
            ViewModel.Position = line.Time.TotalSeconds;
    }

    // ── Drag & drop ───────────────────────────────────────────────────────────
    private void Grid_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Open in Melodia";
            e.DragUIOverride.IsGlyphVisible = false;
        }
    }

    private async void Grid_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync().AsTask();

        foreach (var item in items)
        {
            if (item is StorageFolder folder)
            {
                await ViewModel.LoadFolderAsync(folder);
                return;
            }
        }

        var audioExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".mp3", ".flac", ".wav", ".m4a", ".wma", ".aac", ".ogg" };
        var files = items
            .OfType<StorageFile>()
            .Where(f => audioExts.Contains(f.FileType))
            .ToList();
        if (files.Count > 0)
            await ViewModel.LoadFilesAsync(files);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static SongItem? FindSongItem(object originalSource)
    {
        var element = originalSource as DependencyObject;
        while (element != null)
        {
            if (element is ListViewItem lvi && lvi.Content is SongItem song)
                return song;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private static async Task OpenFileLocationAsync(SongItem song)
    {
        try
        {
            var folder = await song.File.GetParentAsync().AsTask();
            if (folder != null)
            {
                var options = new FolderLauncherOptions();
                options.ItemsToSelect.Add(song.File);
                await Launcher.LaunchFolderAsync(folder, options).AsTask();
            }
        }
        catch { }
    }
}
