<#
.SYNOPSIS
    Build, publish, and package SimpliPDF for one or more architectures.

.PARAMETER Architectures
    Target architectures. Defaults to arm64 for a plain build, or x64 + arm64
    when publishing (-Publish) or packaging (-Msi).

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

.PARAMETER Msi
    If set, builds a versioned MSI installer (WiX v6) per architecture from a
    self-contained publish, mirroring the Release workflow. Outputs
    dist\SimpliPDF-<Version>-<arch>.msi. Requires x64/arm64 (Native AOT, and so
    the MSI, does not support x86). WiX v6 (`wix` global tool) is installed on
    demand if missing.

.PARAMETER Version
    Version stamped into the published binaries and the MSI (e.g. 1.2.3). A
    leading 'v' is stripped. Defaults to 0.0.0.

.EXAMPLE
    .\build.ps1                                              # Build arm64 Debug
    .\build.ps1 -Configuration Release                      # Build arm64 Release
    .\build.ps1 -Publish -Configuration Release             # Self-contained x64 + arm64
    .\build.ps1 -Msi -Configuration Release -Version 1.2.3  # Versioned MSIs for x64 + arm64
#>
param(
    [ValidateSet("x64", "x86", "arm64")]
    [string[]]$Architectures,

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [switch]$Publish,
    [switch]$Msi,

    [string]$Version = "0.0.0"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$project = Join-Path $root "SimpliPDF\SimpliPDF.csproj"
$tfm = "net10.0-windows10.0.26100.0"

# Normalize the version: strip a leading 'v' and derive a numeric major.minor.patch
# for the MSI ProductVersion / AssemblyVersion / FileVersion (which must be numeric).
$releaseVersion = $Version -replace '^v', ''
$msiVersion = ($releaseVersion -split '[-+]')[0]
$verParts = @($msiVersion -split '\.')
while ($verParts.Count -lt 3) { $verParts += '0' }
$msiVersion = ($verParts[0..2] -join '.')
if ($Msi -and $msiVersion -notmatch '^\d+\.\d+\.\d+$') {
    Write-Error "Could not derive a numeric version from '$Version'. Use -Version MAJOR.MINOR.PATCH (e.g. 1.2.3)."
    exit 1
}

# Default to both shipping architectures when producing artifacts (publish / MSI),
# or a single quick build for the local inner loop.
if (-not $Architectures) {
    $Architectures = if ($Publish -or $Msi) { @("x64", "arm64") } else { @("arm64") }
}

# WiX is only needed for -Msi. Install the tool + pinned Util extension on demand.
if ($Msi) {
    $wixVersion = "6.0.2"
    if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
        Write-Host "Installing WiX $wixVersion (dotnet global tool)..." -ForegroundColor Cyan
        dotnet tool install --global wix --version $wixVersion
        if ($LASTEXITCODE -ne 0) { Write-Error "Failed to install WiX"; exit $LASTEXITCODE }
        $env:PATH = "$env:USERPROFILE\.dotnet\tools;$env:PATH"
    }
    # Pin the util extension to the same WiX v6 version; 7.x is incompatible with
    # WiX v6 and wildcard version refs are not accepted.
    wix extension add --global WixToolset.Util.wixext/$wixVersion | Out-Null
    $distDir = Join-Path $root "dist"
    New-Item -ItemType Directory -Force -Path $distDir | Out-Null
}

foreach ($arch in $Architectures) {
    Write-Host "`n=== $Configuration | $arch ===" -ForegroundColor Cyan

    if ($Msi) {
        if ($arch -eq "x86") {
            Write-Error "MSI packaging targets x64 and arm64 only (Native AOT does not support x86)."
            exit 1
        }

        $pubDir = Join-Path $root "SimpliPDF\bin\$Configuration\$tfm\win-$arch\publish"

        # Self-contained publish (Native AOT on x64/arm64 in Release) via the per-arch
        # profile, version-stamped just like the Release workflow.
        dotnet publish $project `
            -c $Configuration `
            -p:Platform=$arch `
            -p:PublishProfile=win-$arch `
            -p:Version=$releaseVersion `
            -p:AssemblyVersion=$msiVersion `
            -p:FileVersion=$msiVersion `
            -p:InformationalVersion=$releaseVersion

        if ($LASTEXITCODE -ne 0) { Write-Error "Publish failed for $arch"; exit $LASTEXITCODE }

        $utilCa = if ($arch -eq "arm64") { "Wix4UtilCA_A64" } else { "Wix4UtilCA_X64" }
        $msiPath = Join-Path $distDir "SimpliPDF-$releaseVersion-$arch.msi"

        wix build (Join-Path $root "installer\SimpliPDF.wxs") `
            -arch $arch `
            -ext WixToolset.Util.wixext `
            -d PublishDir="$pubDir" `
            -d ProductVersion=$msiVersion `
            -d UtilCA=$utilCa `
            -o "$msiPath"

        if ($LASTEXITCODE -ne 0) { Write-Error "MSI build failed for $arch"; exit $LASTEXITCODE }

        Write-Host "OK  $Configuration | $arch | MSI  ->  $msiPath" -ForegroundColor Green
        continue
    }

    if ($Publish) {
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
