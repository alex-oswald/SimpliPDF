<#
.SYNOPSIS
    Build and publish SimplePDF for one or more architectures.

.PARAMETER Architectures
    Target architectures. Defaults to x64.

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.PARAMETER Publish
    If set, publishes a self-contained app instead of just building.

.EXAMPLE
    .\build.ps1                              # Build x64 Debug
    .\build.ps1 -Configuration Release       # Build x64 Release
    .\build.ps1 -Architectures x64,arm64 -Publish  # Publish x64 and ARM64
#>
param(
    [ValidateSet("x64", "x86", "arm64")]
    [string[]]$Architectures = @("arm64"),

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [switch]$Publish
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

foreach ($arch in $Architectures) {
    Write-Host "`n=== $Configuration | $arch ===" -ForegroundColor Cyan

    if ($Publish) {
        dotnet publish "$root\SimplePDF\SimplePDF.csproj" `
            -c $Configuration `
            -p:Platform=$arch `
            -p:PublishReadyToRun=true `
            --self-contained
    } else {
        dotnet build "$root\SimplePDF\SimplePDF.csproj" `
            -c $Configuration `
            -p:Platform=$arch
    }

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Build failed for $arch"
        exit $LASTEXITCODE
    }

    Write-Host "OK  $Configuration | $arch" -ForegroundColor Green
}
