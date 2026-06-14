# melodia

a native windows music player built with winui 3 and fluent design. no electron, no web views — just the windows app sdk doing its thing.

**version:** 0.2.0 interlude

## features

- three-panel layout — library, now playing, playlist
- panels are resizable via draggable dividers; hide or swap them from the view menu
- synced lyrics with animated transitions — current line scales up and fades in, others dim
- search, sort, shuffle, repeat
- playlist reordering via drag and drop
- keyboard shortcuts — space to play/pause, ctrl+left/right to skip
- drag and drop folders or audio files
- session persistence — restores your playlist on relaunch
- mica backdrop, smtc (keyboard/taskbar media controls)

## lyrics

melodia supports synced and plain lyrics in two ways:

1. **lrc sidecar file** — place a `.lrc` file next to your audio file with the same name (e.g. `song.mp3` + `song.lrc`). lines sync in real time as the song plays. click any line to seek to it.
2. **embedded tag** — `System.Music.Lyrics` shell property inside the audio file. lrc format syncs; plain text displays statically.

lrc files are available on sites like [lrclib.net](https://lrclib.net).

## supported formats

mp3 · flac · wav · m4a · wma · aac · ogg

## requirements

- windows 10 1803 or later
- [windows app sdk 2.2 runtime](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads)

## building

open `Melodia.slnx` in visual studio 2022 with the windows app sdk workload installed, then build and run.

## icon

tba

## license

tba
