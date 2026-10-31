# Cewka

A minimalist music player for Linux and Windows. A single file, no installer and no separate
.NET runtime.

![License](https://img.shields.io/badge/license-MIT-blue)
![Platforms](https://img.shields.io/badge/platforms-Linux%20%7C%20Windows-lightgrey)
![Version](https://img.shields.io/badge/version-0.9.0-brightgreen)

*[Wersja polska](README.md)*

![Cewka with the dark theme](docs/obrazy/cewka-ciemny.png)

## Why I built this

I couldn't find a configurable, good-looking music player for my Fedora setup, so I wrote one
myself. I wanted something that just plays files from disk, has an equalizer and looks good,
without a library, an account or service integrations.

You open files or whole folders, it puts them in a queue and plays them. The window background
takes its colors from the album cover.

![Cewka with the light theme](docs/obrazy/cewka-jasny.png)

## Features

- MP3, FLAC, WAV, Ogg Vorbis and Opus. AAC, M4A and ALAC through the system codecs: Media
  Foundation on Windows, GStreamer on Linux.
- Gapless playback.
- Ten-band equalizer with a preamp and a limiter.
- Five effects, all off by default: headphone crossfeed, loudness compensation for quiet
  listening, virtual bass, dynamic range compression and stereo widening.
- Volume leveling from ReplayGain tags or Cewka's own EBU R128 measurement, to −23, −18 or
  −14 LUFS.
- M3U playlists.
- Light and dark themes, seventeen interface languages.
- System media panel (MPRIS on Linux, the overlay on Windows) and media keys.
- Keyboard and screen reader support.
- Update check, off by default. It is the only time Cewka goes online: it asks GitHub for the
  latest release number, at most once a day.

The equalizer with effects and the queue are shown with the buttons in the top right corner or
with the Q and L keys. The other shortcuts are listed in Settings, under About.

![Cewka with the equalizer, effects and queue](docs/obrazy/cewka-pelny.png)

<img src="docs/obrazy/cewka-ustawienia.png" width="640" alt="Settings window">

## Installation

Packages are on the [releases page](https://github.com/grelix/cewka/releases). After installing,
Cewka shows up in the application menu.

Fedora, RHEL, openSUSE:

```bash
sudo dnf install ./cewka-0.9.0-1.x86_64.rpm
```

Debian, Ubuntu, Linux Mint:

```bash
sudo apt install ./cewka_0.9.0_amd64.deb
```

Arch, Manjaro:

```bash
sudo pacman -U cewka-0.9.0-1-x86_64.pkg.tar.zst
```

On Windows, download `Cewka.exe` and run it. The file isn't signed, so SmartScreen will warn you
the first time. You can check the download against `SHA256SUMS.txt` from the release, for
example with `Get-FileHash Cewka.exe` in PowerShell.

### AAC, M4A and ALAC on Linux

These formats need the libav decoder for GStreamer. Everything else works without it.

```bash
sudo dnf install gstreamer1-libav        # Fedora, from RPM Fusion
sudo apt install gstreamer1.0-libav      # Debian, Ubuntu
sudo pacman -S gst-libav                 # Arch
```

## Building

You need the .NET 9 SDK and a C compiler: gcc on Linux, Visual Studio Build Tools on Windows.

```bash
./native/build-linux.sh
dotnet run --project src/Cewka.App -- ~/Music/Album
```

`./tools/publish-linux.sh` and `./tools/build-packages.sh` build the single-file release and the
packages. On Windows use `native\build-windows.cmd` and `tools\publish-windows.cmd`.

## What it's made of

C# and [Avalonia](https://avaloniaui.net/). Audio goes through [miniaudio](https://miniaud.io/),
which also decodes MP3, FLAC and WAV. Ogg Vorbis is handled by [NVorbis](https://github.com/NVorbis/NVorbis),
Opus by [Concentus](https://github.com/lostromb/concentus), and everything else by the system codecs.

## License

MIT, see [LICENSE](LICENSE). Third-party notices are in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt),
with a list of components in [docs/licencje.md](docs/licencje.md) (in Polish). The screenshots show
the 1924 recording of Rhapsody in Blue with the cover of the first sheet music edition, which is in
the public domain.
