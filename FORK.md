# This fork

[RenanQueiroz/RaceOneHub](https://github.com/RenanQueiroz/RaceOneHub) is a fork of
[LoZazaMastro/RaceOneHub](https://github.com/LoZazaMastro/RaceOneHub). It adds two
kinds of changes on top of upstream's `main`:

- **Changes meant for upstream**, one commit each, described where upstream
  describes such things. Each is a candidate for a pull request to
  LoZazaMastro/RaceOneHub, made from its own branch off upstream's `main`.
  - Developer builds without the original Azimuth 0.9.9, which is no longer
    offered for download: `tools/generate_fm5_includes.py --prepared-host` takes
    the two engine locale routines it needs from the release's prepared host,
    as the bridge hydration already does. The bridge, its template and the
    template manifest built this way match the v0.1.0 release byte for byte
    (`docs/BUILDING.md`). [Upstream PR #3](https://github.com/LoZazaMastro/RaceOneHub/pull/3).
  - A source-built Hub runs the backend with the checkout's `.venv` Python.
    [Upstream PR #4](https://github.com/LoZazaMastro/RaceOneHub/pull/4).
  - The Hub's default output folder is `builds\RaceOne5`; the backend refused
    the old default inside `launcher\`. Not proposed upstream as it is: in the
    packaged Hub the project root is the component cache in `%LOCALAPPDATA%`,
    so that default would put the game there.
  - Impulse triggers: the game's trigger rumble levels, which the engine's
    gamepad setter drops, reach Xbox One and Series controllers through
    Windows.Gaming.Input, under the same gates as the main motors (README,
    Options). Tested in races with a Series controller over Bluetooth: the
    brake trigger buzzes on wheel lock, the throttle trigger on wheel spin.
    [Upstream PR #2](https://github.com/LoZazaMastro/RaceOneHub/pull/2).
- **Fork-only extras** that have no place upstream:
  - `fork\setup.ps1` and `fork\update.ps1`, which set up this checkout from the
    pinned v0.1.0 release components and the sources, publish the Hub to
    `launcher\app` and put a **Forza Motorsport 5** shortcut to it on the
    desktop (`fork\README.md`).
  - In the Hub: **PLAY** builds the game folder in place when it is missing or
    stale and starts it, settings changed on the game's pre-game screen come
    back into the Hub, **Rebuild** builds the folder again, and the fork's new
    commits are offered as an **Update** (`launcher\ForzaMastro.Launcher\*Fork*.cs`,
    `MainWindow.Fork.cs`, and short marked hooks in `MainWindow.xaml`,
    `MainWindow.xaml.cs` and `SelfTests.cs`).

## Testing so far

Everything here was built and run on **Windows 11 only, with an NVIDIA RTX 5080**
(and a Ryzen 7 9800X3D), with an FM5 1.0.0.20 dump whose 3,295 files all match
the SHA-1 lists of its delta indexes. The game reaches the front end at 4K,
60 FPS, MSAA 4×, Ultra. Nothing has been built or run on other hardware yet.

## Building and playing

On Windows, the first time: clone this fork, keep your extracted FM5 1.0.0.20
folder (the one with `Mount` and `EmbeddedXvd`) anywhere outside the
repository, and run

```
powershell -ExecutionPolicy Bypass -File fork\setup.ps1 -GameFolder "<FM5 folder>"
```

It needs Visual Studio 2022 with the C++ x64 tools, the Windows SDK, the .NET
SDK 8 or later, Python 3.11 or later and Git (`docs/BUILDING.md`). Then the
desktop shortcut opens the Hub, and **PLAY** builds and starts the game. Saves
stay in `%USERPROFILE%\Saved Games\Race One Hub\RaceOne5`.

## Keeping up with upstream

The fork takes upstream's changes **only when asked to**, never automatically:
fetch `upstream`, merge `upstream/main` into `main`, run `fork\setup.ps1`, play,
then push. The Hub follows this fork's `main` and only says (on its Credits page)
how many upstream commits the fork has not taken yet. When upstream merges one of
the changes above, the fork's copy of it goes away in that merge.

```
git remote add upstream https://github.com/LoZazaMastro/RaceOneHub.git
git fetch upstream
git merge upstream/main
```
