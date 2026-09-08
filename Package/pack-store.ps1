# Requires PowerShell 5.1+. Builds a Microsoft Store MSIX (win-x64) for No Click Switch.
# Usage:
#   .\Package\pack-store.ps1
#   .\Package\pack-store.ps1 -Sideload
# After reserving the app name in Partner Center, paste Package identity into identity.json.

[CmdletBinding()]
param(
    [switch]$Sideload,
    [string]$Name,
    [string]$Publisher,
    [string]$PublisherDisplayName
)

$ErrorActionPreference = "Stop"

$PackageRoot = $PSScriptRoot
$RepoRoot = Split-Path $PackageRoot -Parent
$Csproj = Join-Path $RepoRoot "NoClickSwitch.csproj"
$IdentityPath = Join-Path $PackageRoot "identity.json"
$ManifestTemplate = Join-Path $PackageRoot "AppxManifest.xml"
$IconSource = Join-Path $RepoRoot "Assets\app-icon-512.png"
$AssetsDir = Join-Path $PackageRoot "Assets"
$LayoutDir = Join-Path $PackageRoot "layout"
$OutDir = Join-Path $PackageRoot "out"
$ToolsDir = Join-Path $PackageRoot ".tools"

function Get-FourPartVersion([string]$version) {
    $parts = @($version.Split("."))
    while ($parts.Count -lt 4) {
        $parts += "0"
    }
    return ($parts[0..3] -join ".")
}

function Get-ProjectVersion {
    $raw = Get-Content $Csproj -Raw
    if ($raw -notmatch "<Version>([^<]+)</Version>") {
        throw "Could not read <Version> from $Csproj"
    }
    return Get-FourPartVersion $Matches[1].Trim()
}

function Get-Identity {
    if (-not (Test-Path $IdentityPath)) {
        throw "Missing $IdentityPath. Copy values from Partner Center after you reserve the app name."
    }
    $json = Get-Content $IdentityPath -Raw | ConvertFrom-Json
    $identity = [pscustomobject]@{
        Name                  = if ($Name) { $Name } else { [string]$json.Name }
        Publisher             = if ($Publisher) { $Publisher } else { [string]$json.Publisher }
        PublisherDisplayName  = if ($PublisherDisplayName) { $PublisherDisplayName } else { [string]$json.PublisherDisplayName }
    }
    if ([string]::IsNullOrWhiteSpace($identity.Name) -or
        [string]::IsNullOrWhiteSpace($identity.Publisher) -or
        [string]::IsNullOrWhiteSpace($identity.PublisherDisplayName)) {
        throw "identity.json must set Name, Publisher, and PublisherDisplayName."
    }
    return $identity
}

function Find-SdkTool([string]$fileName) {
    $kits = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    if (Test-Path $kits) {
        $hit = Get-ChildItem $kits -Recurse -Filter $fileName -ErrorAction SilentlyContinue |
            Where-Object { $_.Directory.Name -eq "x64" } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }

    $nuget = Join-Path $env:USERPROFILE ".nuget\packages\microsoft.windows.sdk.buildtools"
    if (Test-Path $nuget) {
        $hit = Get-ChildItem $nuget -Recurse -Filter $fileName -ErrorAction SilentlyContinue |
            Where-Object { $_.Directory.Name -eq "x64" } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }

    return $null
}

function Restore-SdkBuildTools {
    Write-Host "Restoring Microsoft.Windows.SDK.BuildTools (MakeAppx)..."
    New-Item -ItemType Directory -Force -Path $ToolsDir | Out-Null
    $probe = Join-Path $ToolsDir "sdk-buildtools"
    New-Item -ItemType Directory -Force -Path $probe | Out-Null
    $csproj = Join-Path $probe "SdkBuildTools.csproj"
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>disable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Windows.SDK.BuildTools" Version="10.0.26100.4654" />
  </ItemGroup>
</Project>
"@ | Set-Content -Path $csproj -Encoding UTF8
    & dotnet restore $csproj | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore of Microsoft.Windows.SDK.BuildTools failed."
    }
}

function Get-MakeAppx {
    $tool = Find-SdkTool "makeappx.exe"
    if ($tool) { return [string]$tool }
    Restore-SdkBuildTools | Out-Null
    $tool = Find-SdkTool "makeappx.exe"
    if (-not $tool) {
        throw "makeappx.exe not found. Install the Windows 10/11 SDK or restore Microsoft.Windows.SDK.BuildTools."
    }
    return [string]$tool
}

function Save-Png([System.Drawing.Bitmap]$bitmap, [string]$path) {
    $dir = Split-Path $path -Parent
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
}

function New-SquareLogo([System.Drawing.Image]$icon, [int]$size, [string]$path) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.DrawImage($icon, 0, 0, $size, $size)
        Save-Png $bmp $path
    }
    finally {
        $g.Dispose()
        $bmp.Dispose()
    }
}

function New-BrandedLogo([System.Drawing.Image]$icon, [int]$width, [int]$height, [string]$title, [string]$path) {
    $bg = [System.Drawing.Color]::FromArgb(255, 26, 31, 42)
    $fg = [System.Drawing.Color]::FromArgb(255, 244, 247, 251)
    $muted = [System.Drawing.Color]::FromArgb(255, 154, 168, 189)
    $bmp = New-Object System.Drawing.Bitmap $width, $height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $fgBrush = New-Object System.Drawing.SolidBrush $fg
    $mutedBrush = New-Object System.Drawing.SolidBrush $muted
    $fontSize = [Math]::Max(12, [int]($height * 0.22))
    $font = New-Object -TypeName System.Drawing.Font -ArgumentList @("Segoe UI", $fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $subSize = [Math]::Max(10, [int]($height * 0.12))
    $subFont = New-Object -TypeName System.Drawing.Font -ArgumentList @("Segoe UI", $subSize, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    try {
        $g.Clear($bg)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
        $iconSize = [Math]::Min($height - 24, [int]($width * 0.32))
        $ix = 16
        $iy = [int](($height - $iconSize) / 2)
        $g.DrawImage($icon, $ix, $iy, $iconSize, $iconSize)
        $textX = $ix + $iconSize + 16
        $textY = [int](($height / 2) - $fontSize)
        $g.DrawString($title, $font, $fgBrush, $textX, $textY)
        $g.DrawString("NCS", $subFont, $mutedBrush, $textX, $textY + $fontSize + 4)
        Save-Png $bmp $path
    }
    finally {
        $subFont.Dispose()
        $font.Dispose()
        $mutedBrush.Dispose()
        $fgBrush.Dispose()
        $g.Dispose()
        $bmp.Dispose()
    }
}

function Write-StoreAssets {
    if (-not (Test-Path $IconSource)) {
        throw "Missing icon $IconSource"
    }
    Add-Type -AssemblyName System.Drawing
    New-Item -ItemType Directory -Force -Path $AssetsDir | Out-Null
    $icon = [System.Drawing.Image]::FromFile($IconSource)
    try {
        New-SquareLogo $icon 50 (Join-Path $AssetsDir "StoreLogo.png")
        New-SquareLogo $icon 100 (Join-Path $AssetsDir "StoreLogo.scale-200.png")
        New-SquareLogo $icon 44 (Join-Path $AssetsDir "Square44x44Logo.png")
        New-SquareLogo $icon 88 (Join-Path $AssetsDir "Square44x44Logo.scale-200.png")
        New-SquareLogo $icon 71 (Join-Path $AssetsDir "Square71x71Logo.png")
        New-SquareLogo $icon 150 (Join-Path $AssetsDir "Square150x150Logo.png")
        New-SquareLogo $icon 300 (Join-Path $AssetsDir "Square150x150Logo.scale-200.png")
        New-SquareLogo $icon 310 (Join-Path $AssetsDir "Square310x310Logo.png")
        New-BrandedLogo $icon 310 150 "No Click Switch" (Join-Path $AssetsDir "Wide310x150Logo.png")
        New-BrandedLogo $icon 620 300 "No Click Switch" (Join-Path $AssetsDir "Wide310x150Logo.scale-200.png")
        New-BrandedLogo $icon 620 300 "No Click Switch" (Join-Path $AssetsDir "SplashScreen.png")
        New-BrandedLogo $icon 1240 600 "No Click Switch" (Join-Path $AssetsDir "SplashScreen.scale-200.png")
    }
    finally {
        $icon.Dispose()
    }
}

function Publish-App([string]$destination) {
    if (Test-Path $destination) {
        Remove-Item $destination -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    Write-Host "Publishing win-x64 self-contained build..."
    & dotnet publish $Csproj -c Release -r win-x64 --self-contained true `
        -p:DebugType=None -p:DebugSymbols=false `
        -o $destination | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed."
    }

    Get-ChildItem $destination -Recurse -Include *.pdb, *.sys, createdump.exe -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue
}

function Write-LayoutManifest([string]$layout, $identity, [string]$version) {
    $xml = Get-Content $ManifestTemplate -Raw
    $xml = $xml.Replace("__IDENTITY_NAME__", $identity.Name)
    $xml = $xml.Replace("__PUBLISHER__", $identity.Publisher)
    $xml = $xml.Replace("__PUBLISHER_DISPLAY_NAME__", $identity.PublisherDisplayName)
    $xml = $xml.Replace("__VERSION__", $version)
    $utf8NoBom = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllText((Join-Path $layout "AppxManifest.xml"), $xml, $utf8NoBom)

    $assetsDest = Join-Path $layout "Assets"
    if (Test-Path $assetsDest) {
        Remove-Item $assetsDest -Recurse -Force
    }
    Copy-Item $AssetsDir $assetsDest -Recurse
}

function New-TestCertificate([string]$subject) {
    $existing = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $subject -and $_.FriendlyName -eq "No Click Switch MSIX test" } |
        Select-Object -First 1
    if ($existing) { return $existing }

    Write-Host "Creating self-signed test certificate $subject ..."
    return New-SelfSignedCertificate `
        -Type Custom `
        -Subject $subject `
        -KeyUsage DigitalSignature `
        -FriendlyName "No Click Switch MSIX test" `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
}

function Sign-Msix([string]$msixPath, [string]$publisher) {
    $signtool = Find-SdkTool "signtool.exe"
    if (-not $signtool) {
        Restore-SdkBuildTools
        $signtool = Find-SdkTool "signtool.exe"
    }
    if (-not $signtool) {
        throw "signtool.exe not found; cannot sign for sideload."
    }

    $cert = New-TestCertificate $publisher
    Write-Host "Signing $($msixPath) with $($cert.Thumbprint) ..."
    & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint $msixPath
    if ($LASTEXITCODE -ne 0) {
        throw "signtool failed."
    }

    $cer = Join-Path $OutDir "NoClickSwitch-test.cer"
    $bytes = $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)
    [System.IO.File]::WriteAllBytes($cer, $bytes)
    Write-Host "Test certificate exported to $cer"
    Write-Host "For sideload, install the .cer into Trusted People (Current User or Local Machine), then:"
    Write-Host "  Add-AppxPackage `"$msixPath`""
}

$identity = Get-Identity
$version = Get-ProjectVersion
Write-Host "Package $($identity.Name) $version"

Write-StoreAssets
Publish-App $LayoutDir
Write-LayoutManifest $LayoutDir $identity $version

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$msix = Join-Path $OutDir "NoClickSwitch-$version-x64.msix"
$makeappx = Get-MakeAppx
Write-Host "Packing $msix ..."
& $makeappx pack /d $LayoutDir /p $msix /o
if ($LASTEXITCODE -ne 0) {
    throw "makeappx pack failed."
}

if ($Sideload) {
    Sign-Msix $msix $identity.Publisher
}

Write-Host ""
Write-Host "MSIX: $msix"
Write-Host "Upload this file in Partner Center. Microsoft re-signs Store submissions. No CA cert required."
Write-Host "Before the first submission, replace Package\identity.json with the Package/Identity values from Partner Center."
