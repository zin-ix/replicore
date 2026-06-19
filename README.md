# Replicore

A Windows desktop tool to back up a full disk image (Master) to a target drive,
so a damaged Windows install can be restored instead of reinstalled from scratch.

## Why it's built this way

This tool does **not** implement its own raw sector-copy engine. Instead it wraps
Windows' built-in `wbadmin` command, which uses Volume Shadow Copy (VSS) under the
hood. There's a deliberate reason for this:

- **Safety**: a hand-written byte-copy loop has to either skip disks that are
  currently in use (which rules out backing up your live system disk — the most
  important case) or reimplement VSS-style snapshotting from scratch. Getting that
  wrong risks corrupting the backup or the source. `wbadmin`/VSS is Microsoft-tested
  code that already handles this correctly.
- **"Built from scratch" still applies to the part that matters**: the UI, the
  master/target selection logic, safety guards (same-disk checks, system-disk
  warnings, confirmation dialogs), progress parsing, and backup-version browsing
  are all original code in this project. The risky low-level disk I/O rides on a
  proven OS primitive instead.

## Requirements

- Windows 10/11 Pro, Enterprise, or Education (wbadmin's full feature set is
  **not available on Windows Home edition** — this is a Microsoft limitation, not
  something this app can work around. On Home, you'd need a third-party engine
  instead.)
- .NET 8 SDK — free, from https://dotnet.microsoft.com/download
- Visual Studio 2022 Community (free) or `dotnet build` from the CLI
- Must run as Administrator (the app manifest forces this automatically — Windows
  will show a UAC prompt on launch)

## Building

```
dotnet restore
dotnet build -c Release
```

Or open `Replicore.csproj` in Visual Studio and press F5.

## Important limitations to understand

1. **Restoring to the disk Windows is currently booted from doesn't work from
   inside Windows.** You cannot overwrite the active system disk while it's running.
   Real recovery happens by booting into Windows Recovery Environment (WinRE) —
   hold Shift and click Restart, or boot from a Windows installation USB and choose
   "Repair your computer" — then running the restore from there. This app's
   `RestoreAsync` method issues the correct `wbadmin start sysrecovery` command, but
   you'll typically run it from WinRE's command prompt, not from a normal desktop
   session.
2. **Target needs a drive letter.** This first version backs up to a mounted volume
   (an external drive or second internal disk), not to a completely raw unformatted
   target disk. That covers the most common real-world setup (clone to an external
   USB drive).
3. **wbadmin requires a non-Home Windows edition.** If you're on Windows 11 Home,
   tell me and I can adapt this to use `dism /capture-image` instead, which works
   on Home but produces a slightly different image format (WIM) and restore flow.

## Project structure

```
Replicore/
├── App.xaml / App.xaml.cs       - app entry point, loads the theme
├── app.manifest                  - forces admin elevation
├── Models/
│   └── DiskInfo.cs                - represents one physical disk
├── Services/
│   ├── DiskEnumerationService.cs - lists physical disks via WMI
│   └── CloneService.cs           - wraps wbadmin for backup/restore
├── Views/
│   ├── MainWindow.xaml            - the UI (master/target pickers, progress)
│   └── MainWindow.xaml.cs        - wiring + safety guards
└── Themes/
    └── ShadcnTheme.xaml          - shadcn-inspired zinc color palette & control styles
```

## Next steps you might want

- Scheduled/automatic backups (Windows Task Scheduler integration)
- Email/notification on completion or failure
- Restore wizard UI (currently restore is wired in the service layer but has no
  dedicated screen yet)
- Switch to DISM/WIM if you're on Windows Home
