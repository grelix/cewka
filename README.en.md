# Cewka

A minimalist music player for Linux and Windows. A single file, no installer,
no .NET runtime required.

![License](https://img.shields.io/badge/license-MIT-blue)
![Platforms](https://img.shields.io/badge/platforms-Linux%20%7C%20Windows-lightgrey)
![Version](https://img.shields.io/badge/version-0.8.0-brightgreen)

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
- **Five effects**: crossfeed for headphones, loudness compensation for quiet listening, virtual
  bass, dynamic range limiting and stereo widening. Each has a strength slider from 0 to 10, and
  each is off by default
- **The equaliser and the queue show independently** — press `Q` and `L`, or use the buttons in
  the title bar. The window then goes back to the size it had before
- **Volume normalization** using ReplayGain tags or its own EBU R128 analysis, with a choice
  of target level (−23, −18, or −14 LUFS)
- **M3U playlists** — the queue can be saved and loaded, including in another player
- **Background from cover art colors**, animated to the beat; the blobs breathe independently
  of one another, and the colour intensity has three settings
- **Light and dark theme**, following the system by default
- **Seventeen languages**: Polish, English, Czech, German, Greek, Spanish, French, Hungarian,
  Indonesian, Italian, Dutch, Portuguese, Romanian, Russian, Turkish, Ukrainian, and Vietnamese
- **Desktop integration**: media panel (MPRIS on Linux, an overlay on Windows),
  media keys, single instance with file handoff
- **Update check** — on demand or at start-up, off by default. This is the only place the
  application touches the network: one question to GitHub for the number of the latest
  release, no more than once a day
- **Settings**: output device, buffer size, sample rate conversion quality, seek step,
  restoring the previous session, default cover colours (eleven pairs, or a fresh one for every
  track), what happens to a file opened from the file manager

<img src="docs/obrazy/cewka-ustawienia.png" width="640" alt="Settings">

## Installation

Packages are on the [releases page](https://github.com/grelix/cewka/releases). After installation
the app shows up in the application menu.

**Fedora, RHEL, openSUSE**

```bash
sudo dnf install ./cewka-0.8.0-1.x86_64.rpm
```

**Debian, Ubuntu, Linux Mint**

```bash
sudo apt install ./cewka_0.8.0_amd64.deb
```

**Arch, Manjaro**

```bash
sudo pacman -U cewka-0.8.0-1-x86_64.pkg.tar.zst
```

**Windows**

Just download `Cewka.exe` and run it. No installer needed.

The file is not signed with a certificate, so Windows will show a SmartScreen warning the first
time it runs. You can confirm where the file came from with its checksum — every release ships
a `SHA256SUMS.txt`, and `Get-FileHash Cewka.exe` in PowerShell gives you the hash to compare.

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
| `Q` | Equalizer and effects |
| `L` | Queue |
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

364 tests: signal processing including the five effects, view model logic, settings
compatibility across versions, and completeness of the language files.

UI screenshots can be rendered without opening a window — useful for comparing
successive versions of the look:

```bash
dotnet run --project tools/Cewka.Snapshots -- artifacts/snapshots
```

Passing an audio file as the second argument feeds it into the window exactly as opening it from
the file manager would — with title, cover art and duration. Every animation is stopped at a
fixed point while capturing, so two runs of the same code produce the same images and they can
be compared pixel by pixel:

```bash
dotnet run --project tools/Cewka.Snapshots -- artifacts/snapshots ~/Music/Album/track.mp3
```

You don't need music of your own for this — the tool can produce the material itself. Two files
of different lengths, computed from sines, so they come out the same every time:

```bash
dotnet run --project tools/Cewka.Snapshots -- --material artifacts/material
```

The tool does more than draw. It also checks a few things that are easy to break and hard to
notice: whether the "Equaliser" and "Effects" headings sit on the same line, whether the window
returns to its previous size after the equaliser or the queue is hidden, whether the playing
block stays clear of the track list, and whether opening a file from outside really plays that
file. The checks alone, without drawing, take seconds:

```bash
dotnet run --project tools/Cewka.Snapshots -- artifacts/snapshots long.wav short.wav --sprawdzenia
```

The look can also be compared against [reference images](tests/odniesienie/README.md). Where they
differ, a difference image is written with the differing pixels marked in red:

```bash
dotnet run --project tools/Cewka.Snapshots -- artifacts/snapshots long.wav short.wav --porownaj tests/odniesienie
```

### Continuous builds

Every change on the main branch compiles, tests and renders the screenshots with the reference
comparison — separately on Linux and on Windows. The packages are built and trial-installed in
containers at the same time, so broken packaging shows up straight away rather than at release
time.

A release is built from a pushed `vX.Y.Z` tag. The pipeline first checks that the tag matches the
version number in `Directory.Build.props`, builds everything from scratch and leaves a **draft**
release with the files and their checksums. I write the notes by hand and publish it myself —
that part is deliberate.

The installation trials can be run locally too, if docker is available:

```bash
./tools/test-packages.sh
```

## Known limitations

- All three packages install and uninstall cleanly in Ubuntu 24.04, current Fedora and Arch
  containers, and the app from each of them starts under a headless X server. This is checked on
  every change. A container is not a desktop, though, and it will tell you nothing about how the
  app looks or sounds.
- The screenshots above come from 0.7.20 and still show the previous window layout, with the
  queue in the bottom strip.
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
