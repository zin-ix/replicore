# Replicore

A Windows desktop utility built with .NET 8 and WPF to back up full system images (Master) or perform raw drive-to-drive replication, ensuring your system can be restored easily from physical hardware failure or OS corruption.

## Core Features & Architecture

Replicore is designed to provide both safe, high-level image backups and low-level raw drive replication:

1. **Volume Shadow Copy (VSS) Image Backups**: For backing up active partitions (including your live Windows system drive), Replicore wraps the native Windows `wbadmin` engine. This utilizes VSS snapshotting under the hood to capture hot-backups of files in use without risking OS corruption.
2. **Direct Sector-by-Sector Cloning**: For raw drive replication, Replicore uses P/Invoke Win32 API calls (`CreateFile`, `ReadFile`, `WriteFile`) to copy data directly from sector to sector between drives. This allows creating bootable duplicates of secondary or offline disks.
3. **Task Scheduler Integration**: Automate your backups directly from the interface. It registers weekly Task Scheduler tasks running under the local SYSTEM account to keep system images up-to-date in the background.
4. **Smart Safety Guards**:
   - Prevention of cloning onto the active source drive.
   - Warning prompts when writing to active system boot volumes.
   - S.M.A.R.T. health checks to detect failing disks before operations start.
   - Multi-stage confirm-wipe validation prompts for destructive operations.
5. **Modern Design**: Built with a custom, responsive light/dark zinc-colored UI theme inspired by modern design principles.

## Requirements

- **Windows 10/11**:
  - *Image Backup (VSS)*: Requires Pro, Enterprise, or Education edition (Microsoft limits full `wbadmin` capability on Windows Home).
  - *Direct Sector Mirroring*: Works on **all** Windows editions, including Windows Home.
- **.NET 8 SDK** (for building) — free from https://dotnet.microsoft.com/download
- **Administrator Privileges**: Low-level disk access and backup commands require admin rights. The app manifest forces elevation automatically on launch.

## Building & Publishing

### Build
```bash
dotnet restore
dotnet build -c Release
```

### Publish - Self-Contained (~175MB)
Produces a single executable that runs on any PC without requiring any pre-installed .NET runtimes.
```bash
dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true -p:PublishReadyToRun=true --self-contained true -o ./publish
```

### Publish - Light Build (~550KB)
Produces a tiny single executable, but requires the host computer to have the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) installed.
```bash
dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true --self-contained false -o ./publish_small
```

## Project Structure

```
Replicore/
├── App.xaml / App.xaml.cs       - app entry point, loads the theme
├── app.manifest                  - forces admin elevation
├── Models/
│   ├── DiskInfo.cs                - represents one physical disk
│   └── CloneTaskProgress.cs       - tracks individual parallel cloning progress
├── Services/
│   ├── DiskEnumerationService.cs - lists physical disks via WMI
│   └── CloneService.cs           - wraps wbadmin and implements raw sector cloning
├── Views/
│   ├── MainWindow.xaml            - UI interface (pickers, progress indicators, schedules)
│   └── MainWindow.xaml.cs        - logic wiring, safety guards, Task Scheduler automation
└── Themes/
    └── ShadcnTheme.xaml          - shadcn-inspired zinc theme and control styles
```

## Future Roadmap

- **Restore Wizard UI**: Provide a guided UI workflow to restore system images directly from the app (currently restoration is handled at the service layer and via WinRE/wbadmin).
- **Home Edition Image Engine**: Fallback to DISM (`dism /capture-image`) to allow native image backups on Windows Home edition.
