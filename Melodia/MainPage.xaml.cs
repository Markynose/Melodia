using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Melodia.Models;
using Melodia.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace Melodia;

public sealed partial class MainPage : Page
{
    public MainViewModel ViewModel { get; }

    private ScrollViewer? _lyricsScrollViewer;
    private LyricLine? _pendingCenter;
    private int _previousLyricIndex = -1;
    private bool _userScrolledLyrics;

    private enum LibraryMode { Songs, Albums, AlbumDetail }
    private LibraryMode _libraryMode = LibraryMode.Songs;

    private bool _libraryVisible = true;
    private bool _playlistVisible = true;
    private bool _panelsSwapped;
    private const double LibraryColWidth = 200;
    private const double PlaylistColWidth = 240;
    private double _libraryWidth = LibraryColWidth;
    private double _playlistWidth = PlaylistColWidth;
    private bool _isDraggingLeft;
    private bool _isDraggingRight;

    public MainPage()
    {
        ViewModel = new MainViewModel();
        InitializeComponent();

        LyricsListView.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(LyricsListView_PointerWheelChanged), true);

        SetLibraryMode(LibraryMode.Albums);

        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModel.CurrentIndex))
            {
                _previousLyricIndex = -1;
                _userScrolledLyrics = false;
                SnapToCurrentButton.Visibility = Visibility.Collapsed;
                SyncPlaylistSelection(ViewModel.CurrentIndex);
            }
            else if (e.PropertyName == nameof(ViewModel.CurrentLyricIndex))
            {
                var newIdx = ViewModel.CurrentLyricIndex;
                if (_previousLyricIndex >= 0)
                    SetLyricState(LyricsListView.ContainerFromIndex(_previousLyricIndex) as ListViewItem, false, true);
                _previousLyricIndex = newIdx;
                SyncLyricsScroll(newIdx);
            }
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
        if (_userScrolledLyrics) return;
        if (index < 0 || index >= ViewModel.Lyrics.Count) return;
        var item = ViewModel.Lyrics[index];
        _pendingCenter = item;
        LyricsListView.ScrollIntoView(item);
        LyricsListView.LayoutUpdated -= OnLyricsLayoutUpdated;
        LyricsListView.LayoutUpdated += OnLyricsLayoutUpdated;
    }

    private void OnLyricsLayoutUpdated(object? sender, object e)
    {
        if (_pendingCenter == null) { LyricsListView.LayoutUpdated -= OnLyricsLayoutUpdated; return; }
        var container = LyricsListView.ContainerFromItem(_pendingCenter) as ListViewItem;
        if (container == null) return;
        LyricsListView.LayoutUpdated -= OnLyricsLayoutUpdated;
        SetLyricState(container, true, true);
        CenterLyricItem(_pendingCenter);
        _pendingCenter = null;
    }

    private void CenterLyricItem(LyricLine item)
    {
        _lyricsScrollViewer ??= FindScrollViewer(LyricsListView);
        if (_lyricsScrollViewer == null) return;
        var container = LyricsListView.ContainerFromItem(item) as UIElement;
        if (container == null) return;
        try
        {
            var transform = container.TransformToVisual(_lyricsScrollViewer);
            var pt = transform.TransformPoint(new Point(0, 0));
            var itemH = ((FrameworkElement)container).ActualHeight;
            var target = _lyricsScrollViewer.VerticalOffset + pt.Y
                         - (_lyricsScrollViewer.ViewportHeight - itemH) / 2.0;
            _lyricsScrollViewer.ChangeView(null, Math.Max(0, target), null, false);
        }
        catch { }
    }

    // ── Lyric animation ───────────────────────────────────────────────────────
    private void LyricsListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is LyricLine line)
            SetLyricState(args.ItemContainer as ListViewItem, line.IsCurrent, false);
    }

    private static void SetLyricState(ListViewItem? container, bool isCurrent, bool animate)
    {
        if (container == null) return;
        var tb = FindChild<TextBlock>(container);
        if (tb == null) return;

        double targetScale   = isCurrent ? 1.0 : 0.85;
        double targetOpacity = isCurrent ? 1.0 : 0.4;

        if (!animate || tb.RenderTransform is not ScaleTransform scale)
        {
            if (tb.RenderTransform is ScaleTransform st)
            { st.ScaleX = targetScale; st.ScaleY = targetScale; }
            tb.Opacity = targetOpacity;
            return;
        }

        var sb   = new Storyboard();
        var dur  = new Duration(TimeSpan.FromMilliseconds(200));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var sx = new DoubleAnimation { To = targetScale, Duration = dur, EasingFunction = ease };
        Storyboard.SetTarget(sx, scale);
        Storyboard.SetTargetProperty(sx, "ScaleX");
        sb.Children.Add(sx);

        var sy = new DoubleAnimation { To = targetScale, Duration = dur, EasingFunction = ease };
        Storyboard.SetTarget(sy, scale);
        Storyboard.SetTargetProperty(sy, "ScaleY");
        sb.Children.Add(sy);

        var op = new DoubleAnimation { To = targetOpacity, Duration = dur };
        Storyboard.SetTarget(op, tb);
        Storyboard.SetTargetProperty(op, "Opacity");
        sb.Children.Add(op);

        sb.Begin();
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T result) return result;
            var found = FindChild<T>(child);
            if (found != null) return found;
        }
        return null;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject element)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);
            if (child is ScrollViewer sv) return sv;
            var found = FindScrollViewer(child);
            if (found != null) return found;
        }
        return null;
    }

    // ── Library view toggle ───────────────────────────────────────────────────
    private void SongsView_Click(object sender, RoutedEventArgs e)  => SetLibraryMode(LibraryMode.Songs);
    private void AlbumsView_Click(object sender, RoutedEventArgs e) => SetLibraryMode(LibraryMode.Albums);
    private void AlbumBack_Click(object sender, RoutedEventArgs e)  => SetLibraryMode(LibraryMode.Albums);

    private void SetLibraryMode(LibraryMode mode)
    {
        _libraryMode = mode;

        SongListView.Visibility        = mode == LibraryMode.Songs       ? Visibility.Visible : Visibility.Collapsed;
        AlbumsListView.Visibility      = mode == LibraryMode.Albums      ? Visibility.Visible : Visibility.Collapsed;
        AlbumDetailListView.Visibility = mode == LibraryMode.AlbumDetail ? Visibility.Visible : Visibility.Collapsed;

        SongsModeHeader.Visibility   = mode == LibraryMode.Songs       ? Visibility.Visible : Visibility.Collapsed;
        AlbumsModeHeader.Visibility  = mode == LibraryMode.Albums      ? Visibility.Visible : Visibility.Collapsed;
        AlbumDetailHeader.Visibility = mode == LibraryMode.AlbumDetail ? Visibility.Visible : Visibility.Collapsed;

        SearchBox.Visibility = mode == LibraryMode.Songs ? Visibility.Visible : Visibility.Collapsed;
        if (mode != LibraryMode.Songs) ViewModel.FilterText = string.Empty;

        var accent = (Style)Application.Current.Resources["AccentButtonStyle"];
        SongsViewBtn.Style  = mode == LibraryMode.Songs  ? accent : null;
        AlbumsViewBtn.Style = mode != LibraryMode.Songs  ? accent : null;
    }

    private void AlbumsListView_Tapped(object sender, TappedRoutedEventArgs e)
    {
        var album = FindAlbumItem(e.OriginalSource);
        if (album == null) return;
        AlbumDetailListView.ItemsSource = album.Songs;
        AlbumDetailTitle.Text = album.Name;
        SetLibraryMode(LibraryMode.AlbumDetail);
    }

    private void AlbumsListView_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var album = FindAlbumItem(e.OriginalSource);
        if (album == null) return;

        var menu = new MenuFlyout();

        var play = new MenuFlyoutItem { Text = "Play album", Icon = new SymbolIcon(Symbol.Play) };
        play.Click += (_, _) => ViewModel.PlayAlbum(album);

        var add = new MenuFlyoutItem { Text = "Add to playlist", Icon = new SymbolIcon(Symbol.Add) };
        add.Click += (_, _) => ViewModel.AddAlbumToPlaylist(album);

        var next = new MenuFlyoutItem { Text = "Play next" };
        next.Click += (_, _) => ViewModel.PlayAlbumNext(album);

        menu.Items.Add(play);
        menu.Items.Add(add);
        menu.Items.Add(next);

        menu.ShowAt((UIElement)sender, e.GetPosition((UIElement)sender));
    }

    private void AlbumDetailListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var song = FindSongItem(e.OriginalSource);
        if (song != null) ViewModel.PlayFromLibrary(song);
    }

    private static AlbumItem? FindAlbumItem(object originalSource)
    {
        var element = originalSource as DependencyObject;
        while (element != null)
        {
            if (element is ListViewItem lvi && lvi.Content is AlbumItem album) return album;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
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

        var fav = new MenuFlyoutItem
        {
            Text = song.IsFavorite ? "Remove from favorites" : "Add to favorites",
            Icon = new SymbolIcon((Symbol)0xE735),
        };
        fav.Click += (_, _) => ViewModel.ToggleFavorite(song);

        var editNote = new MenuFlyoutItem { Text = "Edit note", Icon = new SymbolIcon(Symbol.Edit) };
        editNote.Click += async (_, _) => await EditNoteAsync(song);

        var open = new MenuFlyoutItem { Text = "Open file location" };
        open.Click += async (_, _) => await OpenFileLocationAsync(song);

        menu.Items.Add(play);
        menu.Items.Add(add);
        menu.Items.Add(next);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(fav);
        menu.Items.Add(editNote);
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

    // ── Keyboard shortcuts ────────────────────────────────────────────────────
    private void KeyShortcut_PlayPause(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        ViewModel.PlayPause();
        e.Handled = true;
    }

    private void KeyShortcut_Next(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        ViewModel.SkipNext();
        e.Handled = true;
    }

    private void KeyShortcut_Previous(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        ViewModel.SkipPrevious();
        e.Handled = true;
    }

    // ── Transport ─────────────────────────────────────────────────────────────
    private void PlayPause_Click(object sender, RoutedEventArgs e) => ViewModel.PlayPause();
    private void Previous_Click(object sender, RoutedEventArgs e)  => ViewModel.SkipPrevious();
    private void Next_Click(object sender, RoutedEventArgs e)      => ViewModel.SkipNext();
    private void Shuffle_Click(object sender, RoutedEventArgs e)   => ViewModel.ToggleShuffle();
    private void Repeat_Click(object sender, RoutedEventArgs e)    => ViewModel.CycleRepeat();

    // ── Playlist reorder ──────────────────────────────────────────────────────
    private void PlaylistView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs e)
    {
        ViewModel.ResyncCurrentIndex();
        SyncPlaylistSelection(ViewModel.CurrentIndex);
    }

    // ── Lyrics ────────────────────────────────────────────────────────────────
    private void LyricLine_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is LyricLine line && line.Time > TimeSpan.Zero)
            ViewModel.Position = line.Time.TotalSeconds;
    }

    private void LyricsListView_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        _userScrolledLyrics = true;
        SnapToCurrentButton.Visibility = Visibility.Visible;
    }

    private void SnapToCurrent_Click(object sender, RoutedEventArgs e)
    {
        _userScrolledLyrics = false;
        SnapToCurrentButton.Visibility = Visibility.Collapsed;
        SyncLyricsScroll(ViewModel.CurrentLyricIndex);
    }

    // ── Panel layout ─────────────────────────────────────────────────────────
    private void ToggleLibrary_Click(object sender, RoutedEventArgs e)
    {
        _libraryVisible = !_libraryVisible;
        ApplyPanelLayout();
    }

    private void TogglePlaylist_Click(object sender, RoutedEventArgs e)
    {
        _playlistVisible = !_playlistVisible;
        ApplyPanelLayout();
    }

    private void SwapPanels_Click(object sender, RoutedEventArgs e)
    {
        _panelsSwapped = !_panelsSwapped;
        ApplyPanelLayout();
    }

    private void ApplyPanelLayout()
    {
        var cols = RootGrid.ColumnDefinitions;

        if (_panelsSwapped)
        {
            Grid.SetColumn(LibraryPanel,  4);
            Grid.SetColumn(LeftDivider,   3);
            Grid.SetColumn(PlaylistPanel, 0);
            Grid.SetColumn(RightDivider,  1);
        }
        else
        {
            Grid.SetColumn(LibraryPanel,  0);
            Grid.SetColumn(LeftDivider,   1);
            Grid.SetColumn(PlaylistPanel, 4);
            Grid.SetColumn(RightDivider,  3);
        }

        bool leftVisible  = _panelsSwapped ? _playlistVisible : _libraryVisible;
        bool rightVisible = _panelsSwapped ? _libraryVisible  : _playlistVisible;
        double leftW  = _panelsSwapped ? _playlistWidth : _libraryWidth;
        double rightW = _panelsSwapped ? _libraryWidth  : _playlistWidth;

        cols[0].MinWidth = 0;
        cols[0].Width    = new GridLength(leftVisible  ? leftW : 0);
        cols[1].Width    = new GridLength(leftVisible  ? 6 : 0);
        cols[3].Width    = new GridLength(rightVisible ? 6 : 0);
        cols[4].MinWidth = 0;
        cols[4].Width    = new GridLength(rightVisible ? rightW : 0);

        LibraryToggle.IsChecked  = _libraryVisible;
        PlaylistToggle.IsChecked = _playlistVisible;
        SwapToggle.IsChecked     = _panelsSwapped;
    }

    // ── Divider drag-resize ───────────────────────────────────────────────────
    private void Divider_PointerEntered(object sender, PointerRoutedEventArgs e)
        => ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);

    private void Divider_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingLeft && !_isDraggingRight)
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    }

    private void LeftDivider_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        ((UIElement)sender).CapturePointer(e.Pointer);
        _isDraggingLeft = true;
        e.Handled = true;
    }

    private void LeftDivider_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingLeft) return;
        var w = Math.Clamp(e.GetCurrentPoint(RootGrid).Position.X, 80, 600);
        if (_panelsSwapped) _playlistWidth = w; else _libraryWidth = w;
        RootGrid.ColumnDefinitions[0].Width = new GridLength(w);
        e.Handled = true;
    }

    private void RightDivider_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        ((UIElement)sender).CapturePointer(e.Pointer);
        _isDraggingRight = true;
        e.Handled = true;
    }

    private void RightDivider_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingRight) return;
        var w = Math.Clamp(RootGrid.ActualWidth - e.GetCurrentPoint(RootGrid).Position.X, 80, 600);
        if (_panelsSwapped) _libraryWidth = w; else _playlistWidth = w;
        RootGrid.ColumnDefinitions[4].Width = new GridLength(w);
        e.Handled = true;
    }

    private void Divider_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        _isDraggingLeft = _isDraggingRight = false;
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
        e.Handled = true;
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

    // ── Favorites filter ─────────────────────────────────────────────────────
    private void FavoritesFilter_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.FavoritesOnly = !ViewModel.FavoritesOnly;
        FavoritesFilterBtn.Opacity = ViewModel.FavoritesOnly ? 1.0 : 0.35;
    }

    // ── Last.fm settings ─────────────────────────────────────────────────────
    private async void LastFmSettings_Click(object sender, RoutedEventArgs e)
    {
        var statusText = new TextBlock
        {
            Text   = ViewModel.LastFmStatus,
            Margin = new Microsoft.UI.Xaml.Thickness(0, 0, 0, 16),
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        var apiKeyBox    = new TextBox    { PlaceholderText = "API Key",    Text = ViewModel.LastFmApiKey, Width = 360 };
        var apiSecretBox = new PasswordBox { PlaceholderText = "API Secret", Width = 360 };
        var usernameBox  = new TextBox    { PlaceholderText = "Username",   Text = ViewModel.LastFmUsername, Width = 360 };
        var passwordBox  = new PasswordBox { PlaceholderText = "Password",   Width = 360 };

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(statusText);
        panel.Children.Add(new TextBlock { Text = "API Key" });
        panel.Children.Add(apiKeyBox);
        panel.Children.Add(new TextBlock { Text = "API Secret" });
        panel.Children.Add(apiSecretBox);
        panel.Children.Add(new TextBlock { Text = "Username" });
        panel.Children.Add(usernameBox);
        panel.Children.Add(new TextBlock { Text = "Password" });
        panel.Children.Add(passwordBox);

        var dialog = new ContentDialog
        {
            Title               = "Last.fm",
            Content             = panel,
            PrimaryButtonText   = "Connect",
            SecondaryButtonText = "Disconnect",
            CloseButtonText     = "Cancel",
            XamlRoot            = XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            var ok = await ViewModel.ConnectLastFmAsync(
                usernameBox.Text, passwordBox.Password,
                apiKeyBox.Text, apiSecretBox.Password);
            if (!ok)
            {
                var err = new ContentDialog
                {
                    Title           = "Last.fm",
                    Content         = "Could not connect. Check your credentials and API key.",
                    CloseButtonText = "OK",
                    XamlRoot        = XamlRoot,
                };
                await err.ShowAsync();
            }
        }
        else if (result == ContentDialogResult.Secondary)
        {
            ViewModel.DisconnectLastFm();
        }
    }

    // ── Notes ─────────────────────────────────────────────────────────────────
    private async Task EditNoteAsync(SongItem song)
    {
        var box = new TextBox
        {
            Text            = ViewModel.GetNote(song),
            PlaceholderText = "Write a note…",
            AcceptsReturn   = true,
            TextWrapping    = TextWrapping.Wrap,
            MinHeight       = 120,
            MaxWidth        = 400,
        };
        var dialog = new ContentDialog
        {
            Title             = song.DisplayTitle,
            Content           = box,
            PrimaryButtonText = "Save",
            CloseButtonText   = "Cancel",
            XamlRoot          = XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            ViewModel.SaveNote(song, box.Text);
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
