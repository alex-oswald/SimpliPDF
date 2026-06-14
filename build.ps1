<#
.SYNOPSIS
    Build and publish SimpliPDF for one or more architectures.

.PARAMETER Architectures
    Target architectures. Defaults to arm64 for a plain build, or x64 + arm64
    when publishing (-Publish) or packaging (-Msix).

.PARAMETER Configuration
    Build configuration. Defaults to Debug. Use Release for distribution.

.PARAMETER Publish
    If set, publishes a self-contained, unpackaged app (bundles the .NET runtime
    and Windows App SDK so it runs on any machine with no install).

    For Release, x64 and ARM64 are published with Native AOT (compiled to native
    code, no JIT); x86 falls back to a self-contained JIT build because Native AOT
    does not support x86. Debug always publishes JIT. Building the AOT targets
    requires the Visual Studio "Desktop development with C++" workload (link.exe);
    ARM64 additionally needs the C++ ARM64 build tools.

.PARAMETER Msix
    If set, builds a signed MSIX package using the dev certificate.

.EXAMPLE
    .\build.ps1                                      # Build arm64 Debug
    .\build.ps1 -Configuration Release               # Build arm64 Release
    .\build.ps1 -Publish -Configuration Release      # Self-contained x64 + arm64
    .\build.ps1 -Architectures x64,arm64 -Configuration Release -Msix  # Signed MSIX for both
#>
param(
    [ValidateSet("x64", "x86", "arm64")]
    [string[]]$Architectures,

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [switch]$Publish,
    [switch]$Msix
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$project = Join-Path $root "SimpliPDF\SimpliPDF.csproj"
$certPath = Join-Path $root "SimpliPDF_Dev.pfx"
$tfm = "net10.0-windows10.0.26100.0"

# Default to both shipping architectures when producing artifacts (publish / MSIX),
# or a single quick build for the local inner loop.
if (-not $Architectures) {
    $Architectures = if ($Publish -or $Msix) { @("x64", "arm64") } else { @("arm64") }
}

foreach ($arch in $Architectures) {
    Write-Host "`n=== $Configuration | $arch ===" -ForegroundColor Cyan

    if ($Msix) {
        dotnet publish $project `
            -c $Configuration `
            -p:Platform=$arch `
            -p:WindowsPackageType=MSIX `
            -p:GenerateAppxPackageOnBuild=true `
            -p:AppxPackageSigningEnabled=true `
            -p:PackageCertificateKeyFile="$certPath" `
            -p:PackageCertificatePassword=""
    } elseif ($Publish) {
        # Self-contained, unpackaged native build via the per-arch publish profile.
        # Bundles the .NET runtime and the Windows App SDK so it runs with no install.
        # For Release, the win-x64 / win-arm64 profiles compile with Native AOT (see the
        # SimpliPdfUseAot logic in SimpliPDF.csproj); win-x86 stays a self-contained JIT build.
        dotnet publish $project `
            -c $Configuration `
            -p:Platform=$arch `
            -p:PublishProfile=win-$arch
    } else {
        dotnet build $project `
            -c $Configuration `
            -p:Platform=$arch
    }

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Build failed for $arch"
        exit $LASTEXITCODE
    }

    if ($Publish) {
        $outDir = Join-Path $root "SimpliPDF\bin\$Configuration\$tfm\win-$arch\publish"
        $mode = if ($Configuration -ne "Debug" -and ($arch -eq "x64" -or $arch -eq "arm64")) { "Native AOT" } else { "JIT (self-contained)" }
        Write-Host "OK  $Configuration | $arch | $mode  ->  $outDir" -ForegroundColor Green
    }
    else {
        Write-Host "OK  $Configuration | $arch" -ForegroundColor Green
    }
}
