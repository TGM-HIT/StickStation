# StickStation

Back up. Reset. Repeat.

StickStation is a macOS app for preparing classroom USB sticks between assignments. It backs up and verifies submissions, clears or reformats sticks, copies the next assignment, and ejects completed drives automatically. Process up to 16 sticks at once with individual progress and results.

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

Use a **space**, not a hyphen, after `HIT`. The current empty-state message shows hyphenated examples, but the discovery code accepts the space-separated names above. Processing also checks that the selected volume is an external USB partition and verifies its identity before destructive operations.

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

## Building and development

The app uses Swift, SwiftUI, AppKit, Foundation, and CryptoKit, together with macOS disk-management utilities. It is designed for macOS.

Open the project in Xcode, select the StickStation app scheme and **My Mac** destination, then build and run. Configure signing for your development environment if Xcode requests it. This source checkout does not currently include an `.xcodeproj` or `Package.swift`; a standalone checkout needs a macOS SwiftUI app target containing the source files and app resources.

| File | Responsibility |
| --- | --- |
| `StickStation/MyApp.swift` | App entry point and application icon setup. |
| `StickStation/ContentView.swift` | Dashboard, batch configuration, and UI state. |
| `StickStation/StickService.swift` | Discovery, backup verification, reset, formatting, ejection, and logging. |

When testing reset or formatting changes, use spare USB sticks with disposable data and the supported volume names.
