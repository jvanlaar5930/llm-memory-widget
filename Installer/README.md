# LLM Memory Widget MSI Installer

This folder contains the WiX installer project for creating a Windows MSI.

## Build the MSI

From the project root:

```powershell
.\Build-MSI.ps1
```

The MSI will be created under:

```text
artifacts\LLM-Memory-Widget-Setup-1.0.0.msi
```

## Install

```powershell
msiexec /i ".\artifacts\LLM-Memory-Widget-Setup-1.0.0.msi"
```

Or run:

```powershell
.\Install-MSI.ps1
```

## Silent install

```powershell
msiexec /i ".\artifacts\LLM-Memory-Widget-Setup-1.0.0.msi" /qn
```

## What the MSI installs

- Self-contained `LlmMemoryWidget.exe`
- Start Menu shortcut
- Desktop shortcut
- Custom app icon
- Install location: `C:\Program Files\LLM Memory Widget`


## PowerShell execution policy note

If PowerShell blocks `Build-MSI.ps1` because it is not digitally signed, use the included CMD wrapper instead:

```cmd
Build-MSI.cmd
```

The wrapper runs PowerShell with a process-only bypass:

```cmd
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-MSI.ps1"
```

This does not permanently change your system execution policy.

Alternatively, run this directly from PowerShell:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build-MSI.ps1
```

Or unblock the script after extracting the ZIP:

```powershell
Unblock-File .\Build-MSI.ps1
.\Build-MSI.ps1
```
