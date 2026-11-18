# StickStation

Back up. Reset. Repeat.

StickStation is a macOS app for preparing classroom USB sticks between assignments. It backs up and verifies submissions, clears or reformats sticks, copies the next assignment, and ejects completed drives automatically. Process up to 16 sticks at once with individual progress and results. A Windows version (WinUI 3) lives in [`windows/`](windows/); see [Windows version](#windows-version).

[![Release macOS app](https://github.com/TGM-HIT/StickStation/actions/workflows/release-macos.yaml/badge.svg)](https://github.com/TGM-HIT/StickStation/actions/workflows/release-macos.yaml)
[![Windows build](https://github.com/TGM-HIT/StickStation/actions/workflows/windows-release.yaml/badge.svg)](https://github.com/TGM-HIT/StickStation/actions/workflows/windows-release.yaml)

## Features

- Automatic discovery of supported, mounted volumes.
- Verified backups of the `Abgabe` folder and all root-level files using SHA-256 checksums.
- Reset while keeping the existing filesystem, or optionally format the volume as FAT32.
- Optional assignment distribution into `Angabe`.
- Automatic ejection and a “Safe to remove” status.
- Separate backup folders per stick and run, with a shared operation log.

## Supported sticks

Volume names must use `HIT ` followed by a letter from `A` through `D` and a two-digit number from `01` through `40`:

```text
HIT A01
HIT B12
HIT C30
HIT D40
```

Use a **space**, not a hyphen, after `HIT`. Processing also checks that the selected volume is an external USB partition and verifies its identity before destructive operations.

The stick uses two folders:

| Folder | Purpose |
| --- | --- |
| `Abgabe` | Submissions to back up; recreated empty after reset. |
| `Angabe` | Assignment materials; populated from the optional source folder, or recreated empty. |

## Usage

1. Connect the named USB sticks, directly or through a USB hub.
2. Choose a **Backup directory** with enough free space for the backups.
3. Optionally choose an **Assignment source** folder. Its contents will be copied into `Angabe` on each stick.
4. Select the sticks to process, up to 16 per batch.
5. Choose whether to enable **Format as FAT32**. When disabled, the app clears files while preserving the existing filesystem.
6. Start the batch and confirm the backup and reset operation.
7. Keep the sticks connected until each displays **Safe to remove**.

Keep the backup directory and assignment source off all managed HIT sticks. The backup directory must not be inside the assignment source folder.

## What is backed up and deleted

**Reset deletes the stick’s contents. Only `Abgabe` and root-level files are backed up. Other folders, including the existing `Angabe`, are not backed up.** Copy anything else you need elsewhere before starting.

The app verifies the backup before beginning cleanup or formatting. If an assignment source is selected, it also stages and verifies that source before deleting data from the stick. Symbolic links and special files in the backup or assignment contents are unsupported and cause processing to stop.

In clear-files mode, macOS may request administrator permission to remove protected files. A protected `.Spotlight-V100` folder may be retained. FAT32 mode reformats the selected volume.

## Backup output

Each run creates a uniquely named folder for each stick under the selected backup directory:

```text
Backup directory/
├── StickStation.log
└── HIT A01_<timestamp>_<unique suffix>/
    ├── Files/          # Abgabe, when present, and root-level files
    ├── Assignment/     # Staged assignment, when a source was selected
    ├── Backup.txt      # Written after the initial backup copies are verified
    └── Reset.txt       # Written after reset, before ejection
```

Failed operations may leave partial backup folders. Check the app’s result and `StickStation.log` to determine which steps completed. `Reset.txt` does not confirm successful ejection.

## Languages

StickStation supports English and German. macOS selects the interface language from your language preferences; you can set a language specifically for StickStation in System Settings > General > Language & Region > Applications, then relaunch the app.

Translations are maintained in `StickStation/Localizable.xcstrings` and `StickStation/AppInfoPlist.xcstrings`. Build in Xcode to extract new localizable strings. Keep `StickStation`, `Angabe`, `Abgabe`, volume labels, and filenames unchanged when translating.

To check German in Xcode, set the Run scheme's App Language to German. A German SwiftUI preview is also available in `ContentView.swift`. Check both singular and plural stick counts, confirmation messages, folder pickers, and error states.

## Building and development

The app uses Swift, SwiftUI, AppKit, Foundation, and CryptoKit, together with macOS disk-management utilities. It is designed for macOS.

Open the project in Xcode, select the StickStation app scheme and **My Mac** destination, then build and run. Configure signing for your development environment if Xcode requests it. This source checkout does not currently include an `.xcodeproj` or `Package.swift`; a standalone checkout needs a macOS SwiftUI app target containing the source files and app resources.

| File | Responsibility |
| --- | --- |
| `StickStation/MyApp.swift` | App entry point and application icon setup. |
| `StickStation/ContentView.swift` | Dashboard, batch configuration, and UI state. |
| `StickStation/StickService.swift` | Discovery, backup verification, reset, formatting, ejection, and logging. |

When testing reset or formatting changes, use spare USB sticks with disposable data and the supported volume names.

## Windows version

The Windows app in [`windows/`](windows/) is a WinUI 3 (C#, Windows App SDK, MVVM) port with the same workflow as the macOS app: discovery of mounted removable USB volumes named `HIT <A-D><01-40>`, USB identity checks before destructive operations, batches of up to 16 sticks with per-stick progress, SHA-256-verified backups of `Abgabe` and root-level files into unique per-stick/run folders, an optional assignment source copied into `Angabe`, clear-files or FAT32 reset, a **Back up only** mode, automatic ejection with a "Safe to remove" status, and English/German localization (follows the Windows display language).

Differences from macOS:

- Formatting as FAT32 runs `Format-Volume` and requests administrator permission through UAC (once per stick). Windows can only format volumes up to 32 GB as FAT32; use clear-files mode for larger sticks. Clear-files mode needs no administrator rights.
- `System Volume Information` and `$RECYCLE.BIN` are left in place instead of the Spotlight folder.
- Only volumes with a drive letter are supported.
- Safety rules are identical: the backup directory must not be on a managed stick or inside the assignment source, and symbolic links, junctions and special files are rejected.

Layout:

| Project | Responsibility |
| --- | --- |
| `windows/StickStation.Core` | Testable library: backup/verify/reset pipeline, path safety, logging, view models, Windows volume service (WMI, `Format-Volume`, shell eject). |
| `windows/StickStation.Core.Tests` | xUnit tests using temporary folders and fakes; no real drives are touched. |
| `windows/StickStation.App` | WinUI 3 app (XAML, `Strings/en-US` and `Strings/de-DE` resources). |

Requirements: Windows 10 1809+ or Windows 11, .NET 10 SDK, and for running the packaged debug build Developer Mode plus the [winapp CLI](https://github.com/microsoft/winappCli).

```powershell
cd windows
dotnet test StickStation.Core.Tests                               # unit tests
dotnet build StickStation.App/StickStation.csproj -p:Platform=x64 # build the app
winapp run StickStation.App                                       # build and launch with package identity

# Self-contained, unpackaged build (what CI publishes as a zip)
dotnet publish StickStation.App/StickStation.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true -o publish
```

The `Windows` workflow (`.github/workflows/windows-release.yaml`) builds, tests and publishes `StickStation-Windows-x64-v<version>.zip` (plus SHA-256 file) as a build artifact, and attaches it to the GitHub Release when a `vX.Y.Z` tag is pushed. Translations live in `windows/StickStation.App/Strings/<language>/Resources.resw`.

