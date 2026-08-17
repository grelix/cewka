# Cewka

A minimalist music player for Linux and Windows. A single file, no installer,
no .NET runtime required.

![License](https://img.shields.io/badge/license-MIT-blue)
![Platforms](https://img.shields.io/badge/platforms-Linux%20%7C%20Windows-lightgrey)
![Version](https://img.shields.io/badge/version-0.6.0-brightgreen)

*[Wersja polska](README.md)*

![Cewka — dark theme](docs/obrazy/cewka-ciemny.png)

![Cewka — light theme](docs/obrazy/cewka-jasny.png)

## Why I built this

I couldn't find a configurable, good-looking music player for my Fedora setup, so I wrote one
myself. I wanted something that just plays files from disk, has an equalizer, and looks good —
no library, no account, no service integrations.

I wrote the code with help from Claude Code, which wrote automated tests and hunted for bugs.

## What Cewka can do

- **Formats**: MP3, FLAC, WAV, Ogg Vorbis, Opus, and through system codecs also AAC, M4A, and ALAC
- **Gapless playback** and click-free seeking
- **Ten-band equalizer** with a preamp and a soft limiter on the output
- **Volume normalization** using ReplayGain tags or its own EBU R128 analysis, with a choice
  of target level (−23, −18, or −14 LUFS)
- **Background from cover art colors**, animated to the beat
- **Light and dark theme**, following the system by default
- **Five languages**: Polish, English, Spanish, German, and French
- **Desktop integration**: media panel (MPRIS on Linux, an overlay on Windows),
  media keys, single instance with file handoff
- **Settings**: output device, buffer size, sample rate conversion quality,
  seek step, restoring the previous session

<img src="docs/obrazy/cewka-ustawienia.png" width="640" alt="Settings">

## Installation

Packages are on the [releases page](https://github.com/grelix/cewka/releases). After installation
the app shows up in the application menu.

**Fedora, RHEL, openSUSE**

```bash
sudo dnf install ./cewka-0.6.0-1.x86_64.rpm
```

**Debian, Ubuntu, Linux Mint**

```bash
sudo apt install ./cewka_0.6.0_amd64.deb
```

**Arch, Manjaro**

```bash
sudo pacman -U cewka-0.6.0-1-x86_64.pkg.tar.zst
```

**Windows**

Just download `Cewka.exe` and run it. No installer needed.

### AAC, M4A, and ALAC on Linux

These three formats are handled by GStreamer, and the AAC decoder comes from a separate package.
Without it the other formats work fine.

```bash
sudo dnf install gstreamer1-libav        # Fedora (from RPM Fusion)
sudo apt install gstreamer1.0-libav      # Debian, Ubuntu
sudo pacman -S gst-libav                 # Arch
```

## Keyboard shortcuts

| Shortcut | Action |
|-------|-----------|
| `Space` | Play or pause |
| `←` `→` | Seek (step configurable in settings: 5, 10, or 30 s) |
| `Ctrl` + `←` `→` | Previous or next track |
| `↑` `↓` | Volume |
| `M` | Mute |
| `Q` | Equalizer and queue |
| `T` | Switch theme |
| `F11` | Fullscreen |
| `Ctrl+O` | Add files |
| `Ctrl+Shift+O` | Add folder |
| `Delete` | Remove from queue |

Media keys on the keyboard also work when the window isn't focused.

## Building from source

You need .NET SDK 9 and a C compiler — `gcc` on Linux or Visual Studio Build Tools
on Windows.

```bash
./native/build-linux.sh
```

Then the app:

```bash
dotnet run --project src/Cewka.App -- ~/Music/Album
```

Single-file release and installer packages:

```bash
./tools/publish-linux.sh
./tools/build-packages.sh
```

On Windows the equivalents are `native\build-windows.cmd` and `tools\publish-windows.cmd`.

### Tests

```bash
dotnet test Cewka.sln
```

103 tests: signal processing, view model logic, settings compatibility across versions,
and completeness of the language files.

UI screenshots can be rendered without opening a window — useful for comparing
successive versions of the look:

```bash
dotnet run --project tools/Cewka.Snapshots -- artifacts/snapshots
```

## Known limitations

- The `.deb` package is verified by installing it on Ubuntu 24.04: the app plays from it and
  uninstalls without leftovers. I checked the `.rpm` and Arch packages by contents, dependencies
  and whether the executable comes out of them untouched — but not by installing them on the
  target distribution.
- The Arch package is built without `makepkg`, so it has no `.MTREE` file. `pacman -U` accepts
  it, though it may mention this. The repository also has [`PKGBUILD`](packaging/arch/PKGBUILD).
- The effects-throttling mode on battery hasn't been tested on a laptop.
- The "low latency" setting is only a hint to the driver. In WASAPI shared mode it doesn't
  always change anything — the settings window then shows the size the device actually accepted.

## How it's built

C# and [Avalonia UI](https://avaloniaui.net/), audio through [miniaudio](https://miniaud.io/)
with a custom interop layer. Decoders: miniaudio for MP3, FLAC, and WAV,
[NVorbis](https://github.com/NVorbis/NVorbis) for Ogg Vorbis,
[Concentus](https://github.com/lostromb/concentus) for Opus, and for the remaining formats
Media Foundation on Windows and GStreamer on Linux.

## License

MIT — full text in [LICENSE](LICENSE). The embedded Cantarell and JetBrains Mono fonts are
licensed under the SIL Open Font License 1.1; details in [docs/licencje.md](docs/licencje.md)
(in Polish).
