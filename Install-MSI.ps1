param(
    [string]$MsiPath = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($MsiPath)) {
    $artifactDir = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) "artifacts"
    $msi = Get-ChildItem -Path $artifactDir -Filter "LLM-Memory-Widget-Setup-*.msi" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if ($null -eq $msi) {
        throw "No MSI found. Run .\Build-MSI.ps1 first, or pass -MsiPath."
    }

    $MsiPath = $msi.FullName
}

Start-Process msiexec.exe -ArgumentList "/i `"$MsiPath`"" -Wait -Verb RunAs
