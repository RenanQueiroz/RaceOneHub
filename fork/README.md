# Fork tools

Part of this fork (see `..\FORK.md`); Windows only. The desktop shortcut
**Forza Motorsport 5** opens `launcher\app\RaceOneHub.exe`, the Hub built from
this checkout. It opens on the game's settings; **PLAY** starts the game with
them. On start it fetches the fork; when the fork has new commits it offers
**Update**, with the commits folded under *What's new*. The Credits page says
where this copy stands and how many upstream commits the fork has not taken.

```
..\                                   the folder the repository is in
└─ RaceOneHub\                        this checkout
   ├─ fork\
   │  ├─ setup.ps1                    set up or refresh everything below
   │  ├─ update.ps1                   fast-forward to the fork's main, then setup.ps1
   │  ├─ local.json                   the game folder                    (not tracked)
   │  ├─ cache\                       the downloaded v0.1.0 release      (not tracked)
   │  └─ logs\                        one log per setup run              (not tracked)
   ├─ runtime\                        release components + built bridge (not tracked)
   ├─ builds\RaceOne5\                the game PLAY builds and starts    (not tracked)
   ├─ launcher\app\                   the published Hub                  (not tracked)
   └─ launcher\data\                  the Hub's settings and build logs  (not tracked)
```

## setup.ps1

```powershell
.\fork\setup.ps1 -GameFolder "C:\Games\RaceOneHub\Forza Motorsport 5\Forza_1.0.0.20_x64__8wekyb3d8bbwe"
.\fork\setup.ps1              # later runs: the game folder is remembered in fork\local.json
```

Without `-GameFolder`, the first run looks for
`..\Forza Motorsport 5\Forza_1.0.0.20_x64__8wekyb3d8bbwe` next to the checkout.
Each run is idempotent and takes about 10 seconds when nothing changed:

1. **Python.** `.venv` with `requirements.txt`, reinstalled when that file changes.
2. **Release components.** The v0.1.0 `RaceOneHub.exe` (sha256 pinned) is
   downloaded once and started with `--check-backend`, which unpacks its
   components without building or starting anything; they are copied to
   `runtime\release-0.1.0`. Its prepared host and engine files fill
   `runtime\pc`. The original Azimuth 0.9.9 is not needed.
3. **Includes.** The four game-derived includes, generated with
   `--prepared-host` when the generator, the host or the game changes.
4. **Bridge and kernelx.dll**, built from `src\` every run (4 seconds). MSVC
   and the Windows SDK are found with vswhere. Upstream's `build_xg_bridge.py`
   also needs a private modern XG; only its `kernelx.dll` step is used.
5. **The Hub**, published to `launcher\app` when `launcher\ForzaMastro.Launcher`
   changes, and the desktop shortcut. `-SkipHub` leaves the Hub alone (the Hub
   passes it while it is open); `-HubOnly -WaitForPid <pid> -StartHub` is how
   the Hub restarts itself after an update changed it.

On the first run it also points the Hub at the game folder and output folder
(`launcher\data\builder-state.json`), and links the game folder as
`game\Forza_1.0.0.20_x64__8wekyb3d8bbwe` for the research tools that look there.

## PLAY

Upstream's **BUILD EXE** writes a new game folder and refuses an existing one.
PLAY keeps one folder, `builds\RaceOne5` by default, up to date instead. It
builds first when the folder:

- has no `RaceOne5.exe`,
- was built from another runtime: `fork-build.json` in the folder records a
  digest of the prepared host, the bridge, the engine files, `kernelx.dll` and
  the backend code that writes the folder,
- reads another game folder, or
- has other settings than the Hub (`data\RaceOneEngine.ini`, `forzamastro.ini`).

The build is the backend's own, into an empty `RaceOne5.next` beside the folder,
which then replaces it (the old folder comes back if that fails). Logs carry
over; shader, pipeline and layout caches carry over only from the same runtime.
**Rebuild** does the same without starting the game.

The game's pre-game screen saves its choices to `data\RaceOneEngine.ini` when
you start the game. The Hub takes those choices as its own settings (once per
change, the first time it sees a folder all of them), so PLAY then starts the
game as it is instead of building over them.

## Update

`update.ps1` runs `git merge --ff-only --autostash origin/main` on `main` and
then `setup.ps1`. From the Hub it runs with `-SkipHub -ProgressFile`; when the
update changed the Hub's sources, the Hub starts `setup.ps1 -HubOnly` and closes,
and that publishes the new Hub and starts it again. Upstream's changes reach the
fork only when merged on purpose (`..\FORK.md`).
