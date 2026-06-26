param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock]$Command,

        [Parameter(Mandatory = $true)]
        [string]$ErrorMessage
    )

    & $Command

    if ($LASTEXITCODE -ne 0) {
        throw $ErrorMessage
    }
}

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$appProject = Join-Path $repoRoot "LlmMemoryWidget.csproj"
$installerProject = Join-Path $repoRoot "Installer\LlmMemoryWidget.Installer.wixproj"
$publishDir = Join-Path $repoRoot "publish\$Runtime"
$artifactDir = Join-Path $repoRoot "artifacts"

New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null

Write-Host "Publishing LLM Memory Widget..."
Invoke-Checked {
    dotnet publish $appProject `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:PublishReadyToRun=false `
        -p:Version=$Version `
        -p:FileVersion="$Version.0" `
        -p:AssemblyVersion="$Version.0" `
        -o $publishDir
} "dotnet publish failed."

$publishedExe = Join-Path $publishDir "LlmMemoryWidget.exe"
if (!(Test-Path $publishedExe)) {
    throw "Expected published exe was not found: $publishedExe"
}

Write-Host "Building x64 MSI..."
Invoke-Checked {
    dotnet build $installerProject `
        -c $Configuration `
        -p:PublishDir="$publishDir" `
        -p:Version=$Version `
        -p:InstallerPlatform=x64 `
        -p:Platform=x64
} "dotnet build for the WiX installer failed."

$msi = Get-ChildItem -Path (Join-Path $repoRoot "Installer\bin\$Configuration") -Filter "*.msi" -Recurse |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if ($null -eq $msi) {
    throw "MSI build completed, but no MSI was found under Installer\bin\$Configuration."
}

$finalMsi = Join-Path $artifactDir "LLM-Memory-Widget-Setup-$Version.msi"
Copy-Item $msi.FullName $finalMsi -Force

Write-Host ""
Write-Host "MSI created:"
Write-Host $finalMsi
Write-Host ""
Write-Host "Install with:"
Write-Host "msiexec /i `"$finalMsi`""
Write-Host ""
Write-Host "Silent install:"
Write-Host "msiexec /i `"$finalMsi`" /qn"
