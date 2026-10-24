# Replicore

A Windows desktop utility built with .NET 8 and WPF to back up full system images (Master) or perform raw drive-to-drive replication, ensuring your system can be restored easily from physical hardware failure or OS corruption.

## Core Features & Architecture

Replicore is designed to provide both safe, high-level image backups and low-level raw drive replication:

1. **Volume Shadow Copy (VSS) Image Backups**: For backing up active partitions (including your live Windows system drive), Replicore wraps the native Windows `wbadmin` engine. This utilizes VSS snapshotting under the hood to capture hot-backups of files in use without risking OS corruption.
2. **Multi-Disk Sector-by-Sector Cloning**: Clones one source disk to several targets at once. The source is read once and each chunk is written to every target in parallel. Mounted source volumes (including the running Windows disk) are read from Volume Shadow Copy snapshots so the copy is consistent; BitLocker-encrypted in-use volumes are refused rather than copied inconsistently. Each target can be verified against the source after copying, and on larger targets the GPT backup table is moved to the real end of the disk. Partition tables are written last, so a cancelled clone leaves the target blank rather than half-valid.
3. **Smart Copy (used space only)**: On by default. Replicore reads the NTFS allocation bitmap of each snapshotted volume and skips free space in 1 MB blocks, both when copying and when verifying. A 1 TB disk holding 200 GB clones roughly five times faster. Partition tables, EFI, Recovery and non-NTFS partitions are always copied in full. Turn it off for an exact copy of every sector.
4. **Clone to a VHDX Image File**: Add an image file as a target (it works alongside disk targets). Replicore creates a dynamically expanding `.vhdx` of the same size and sector size as the source, attaches it without drive letters, and clones into it with the same engine. With smart copy, the file is about the size of the data. The image can be attached later in Disk Management or Hyper-V. Windows may keep it offline while the original disk is connected, because the two disks share the same disk ID. If a clone into an image doesn't finish, the partial file is deleted.
5. **Disk Health (S.M.A.R.T.)**: Health comes from the Windows Storage API: the drive's own health and predictive-failure flags, plus its reliability counters (temperature, SSD wear, power-on hours, unrecoverable errors). The source card shows these readings and warns about problems. Failing targets need extra confirmation, and a failing source gets a "clone it now" prompt. Drives that don't report data (common with USB enclosures) show "Health unknown".
6. **Partition Map**: The source and every target show their partitions to scale, including EFI, Reserved and Recovery partitions without a drive letter. The erase confirmation shows each target's partitions now and after the clone.
7. **Task Scheduler Integration**: Automate your backups directly from the interface. It registers weekly Task Scheduler tasks running under the local SYSTEM account to keep system images up-to-date in the background.
8. **Smart Safety Guards**:
   - Prevention of cloning onto the active source drive.
   - Warning prompts when writing to active system boot volumes.
   - Health checks that flag failing disks before operations start.
   - Image files can't be saved on a disk the same clone erases.
   - Multi-stage confirm-wipe validation prompts for destructive operations.
9. **Windows 11 Design**: Fluent UI built with [WPF-UI](https://github.com/lepoco/wpfui) — Mica backdrop, native title bar, Fluent controls and dialogs, and automatic light/dark theme that follows Windows.

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
│   ├── DiskInfo.cs                - one physical disk (or image file target)
│   ├── DiskHealth.cs              - health level, problems and S.M.A.R.T. readings
│   ├── DiskSegment.cs             - one partition or gap in the partition map
│   └── CloneTaskProgress.cs       - tracks individual parallel cloning progress
├── Services/
│   ├── DiskEnumerationService.cs - lists disks, volumes and full partition layouts via WMI
│   ├── DiskHealthService.cs      - drive health and reliability counters (Storage API)
│   ├── DiskCloneEngine.cs        - multi-target sector cloning, smart copy and verification
│   ├── AllocationMap.cs          - NTFS allocation bitmaps used to skip free space
│   ├── SourceSnapshot.cs         - Volume Shadow Copy snapshots of the source volumes
│   ├── VirtualDisk.cs            - creates and attaches .vhdx image targets
│   ├── GptRepair.cs              - moves the GPT backup to the end of larger targets
│   ├── NativeDisk.cs             - raw disk handles and DeviceIoControl wrappers
│   └── CloneService.cs           - wraps wbadmin and diskpart
├── Views/
│   ├── SplashWindow.xaml          - animated startup screen shown during the first disk scan
│   ├── MainWindow.xaml            - UI interface (pickers, progress indicators, schedules)
│   ├── MainWindow.xaml.cs        - logic wiring, safety guards, Task Scheduler automation
│   └── PartitionMapBar.cs        - to-scale partition bar control
└── Themes/
    └── AppStyles.xaml            - app styles layered on the WPF-UI Fluent theme
```

## Future Roadmap

- **Restore Wizard UI**: Provide a guided UI workflow to restore system images directly from the app (currently restoration is handled at the service layer and via WinRE/wbadmin). This includes opening a Replicore `.vhdx` as the source, so an image can be cloned back onto a disk.
- **Clone to a smaller disk**: Smart copy already knows where the data is; shrinking the last partition would allow HDD → smaller SSD migrations.
- **Home Edition Image Engine**: Fallback to DISM (`dism /capture-image`) to allow native image backups on Windows Home edition.
