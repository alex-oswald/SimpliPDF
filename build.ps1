<#
.SYNOPSIS
    Build and publish SimpliPDF for one or more architectures.

.PARAMETER Architectures
    Target architectures. Defaults to arm64.

.PARAMETER Configuration
    Build configuration. Defaults to Debug.

.PARAMETER Publish
    If set, publishes a self-contained app instead of just building.

.PARAMETER Msix
    If set, builds a signed MSIX package using the dev certificate.

.EXAMPLE
    .\build.ps1                              # Build arm64 Debug
    .\build.ps1 -Configuration Release       # Build arm64 Release
    .\build.ps1 -Architectures x64,arm64 -Msix  # Build signed MSIX for both
#>
param(
    [ValidateSet("x64", "x86", "arm64")]
    [string[]]$Architectures = @("arm64"),

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [switch]$Publish,
    [switch]$Msix
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$certPath = Join-Path $root "SimpliPDF_Dev.pfx"

foreach ($arch in $Architectures) {
    Write-Host "`n=== $Configuration | $arch ===" -ForegroundColor Cyan

    if ($Msix) {
        dotnet publish "$root\SimpliPDF\SimpliPDF.csproj" `
            -c $Configuration `
            -p:Platform=$arch `
            -p:WindowsPackageType=MSIX `
            -p:GenerateAppxPackageOnBuild=true `
            -p:AppxPackageSigningEnabled=true `
            -p:PackageCertificateKeyFile="$certPath" `
            -p:PackageCertificatePassword=""
    } elseif ($Publish) {
        dotnet publish "$root\SimpliPDF\SimpliPDF.csproj" `
            -c $Configuration `
            -p:Platform=$arch `
            -p:PublishReadyToRun=true `
            --self-contained
    } else {
        dotnet build "$root\SimpliPDF\SimpliPDF.csproj" `
            -c $Configuration `
            -p:Platform=$arch
    }

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Build failed for $arch"
        exit $LASTEXITCODE
    }

    Write-Host "OK  $Configuration | $arch" -ForegroundColor Green
}
