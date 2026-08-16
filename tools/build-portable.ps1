<#
.SYNOPSIS
    Dong goi ban portable de share: mot exe self-contained + config va ROI mac dinh.

.DESCRIPTION
    Che do mac dinh: publish self-contained single-file, gom kem config.json,
    car-template.png va bo ROI job cau ca trong packaging/defaults, roi nen ra dist/.

    Nguoi nhan giai nen la chay duoc ngay, khong can cai .NET va khong phai khoanh
    lai vung - AppPaths.MigrateFromExeFolder chep fishing.json + fishing/ tu thu muc
    exe sang %APPDATA%\GtaMiniGameBot o lan chay dau.

    Che do -SyncDefaults: di nguoc lai, chep ROI dang dung o %APPDATA% ve
    packaging/defaults de commit. Chay cai nay moi khi canh lai vung trong app,
    khong thi ban share se troi lech dan so voi ban dang dung.

.PARAMETER OutDir
    Noi dat file zip. Mac dinh la dist/ o goc repo.

.PARAMETER SyncDefaults
    Khong build. Chep ROI tu %APPDATA%\GtaMiniGameBot ve packaging/defaults.

.EXAMPLE
    .\tools\build-portable.ps1

.EXAMPLE
    .\tools\build-portable.ps1 -SyncDefaults
#>
[CmdletBinding()]
param(
    [string] $OutDir,
    [switch] $SyncDefaults
)

$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Packaging = Join-Path $RepoRoot 'packaging'
$DefaultsDir = Join-Path $Packaging 'defaults'
$UserDataDir = Join-Path $env:APPDATA 'GtaMiniGameBot'

# Thu muc chi dung khi setup (anh tinh full man hinh) hoac la rac debug -
# khong can cho bot chay, va shots/ nang ~27 MB nen tuyet doi khong gom vao.
$ExcludedDirs = @('shots', 'unknown')
$ExcludedDirPrefix = 'debug-'

function Test-ExcludedPath {
    param([string] $RelativeDir)

    if (-not $RelativeDir) { return $false }
    foreach ($seg in ($RelativeDir -split '\\')) {
        if ($ExcludedDirs -contains $seg) { return $true }
        if ($seg -like "$ExcludedDirPrefix*") { return $true }
    }
    return $false
}

# Chep ca cay thu muc, bo qua cac nhanh trong $ExcludedDirs. Tra ve so file da chep.
function Copy-TreeFiltered {
    param(
        [string] $From,
        [string] $To
    )

    $count = 0
    foreach ($file in Get-ChildItem -Path $From -Recurse -File) {
        $relativeDir = ''
        if ($file.DirectoryName.Length -gt $From.Length) {
            $relativeDir = $file.DirectoryName.Substring($From.Length).TrimStart('\')
        }
        if (Test-ExcludedPath $relativeDir) { continue }

        $targetDir = if ($relativeDir) { Join-Path $To $relativeDir } else { $To }
        if (-not (Test-Path $targetDir)) {
            New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
        }
        Copy-Item -Path $file.FullName -Destination (Join-Path $targetDir $file.Name) -Force
        $count++
    }
    return $count
}

# ---------------------------------------------------------------- sync defaults

if ($SyncDefaults) {
    $srcJson = Join-Path $UserDataDir 'fishing.json'
    $srcFishing = Join-Path $UserDataDir 'fishing'

    if (-not (Test-Path $srcJson)) {
        throw "Khong thay $srcJson. Mo app khoanh vung job cau ca truoc da."
    }

    New-Item -ItemType Directory -Force -Path $DefaultsDir | Out-Null
    Copy-Item -Path $srcJson -Destination (Join-Path $DefaultsDir 'fishing.json') -Force

    # Xoa truoc roi chep lai, de file da bo trong app cung bien mat khoi snapshot.
    $dstFishing = Join-Path $DefaultsDir 'fishing'
    if (Test-Path $dstFishing) { Remove-Item -Path $dstFishing -Recurse -Force }

    $copied = 0
    if (Test-Path $srcFishing) {
        $copied = Copy-TreeFiltered -From $srcFishing -To $dstFishing
    }

    Write-Host "Da dong bo ROI ve $DefaultsDir ($copied file, da bo shots/ va debug-*)."
    Write-Host "Xem lai bang 'git status' roi commit."
    return
}

# ---------------------------------------------------------------------- build

if (-not $OutDir) { $OutDir = Join-Path $RepoRoot 'dist' }

Push-Location $RepoRoot
try {
    $sha = (& git rev-parse --short HEAD).Trim()
    $branch = (& git rev-parse --abbrev-ref HEAD).Trim()
    $dirty = & git status --porcelain
}
finally {
    Pop-Location
}

if ($dirty) {
    Write-Warning "Working tree dang ban - ban zip se khong khop voi commit $sha."
}
Write-Host "Build tu nhanh $branch, commit $sha."

$stamp = Get-Date -Format 'yyyyMMdd'
$Staging = Join-Path $env:TEMP "gta-portable-$sha-$stamp"
if (Test-Path $Staging) { Remove-Item -Path $Staging -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Staging | Out-Null

$csproj = Join-Path $RepoRoot 'src\GtaMiniGameBot\GtaMiniGameBot.csproj'
Write-Host "Publish self-contained win-x64..."
& dotnet publish $csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $Staging
if ($LASTEXITCODE -ne 0) { throw "dotnet publish that bai (exit $LASTEXITCODE)." }

# Symbol debug, nguoi nhan khong can.
Get-ChildItem -Path $Staging -Filter '*.pdb' -File | Remove-Item -Force

# csproj include car-template.png kem Condition="Exists(...)", nen thieu file la
# publish *im lang* bo qua - ra ban exe khong do duoc "dang trong xe". Chan o day.
$carTemplate = Join-Path $Staging 'car-template.png'
if (-not (Test-Path $carTemplate)) {
    throw "Thieu car-template.png trong ban publish. Kiem tra src\GtaMiniGameBot\car-template.png."
}

Copy-Item -Path (Join-Path $RepoRoot 'app\config.json') -Destination $Staging -Force
Copy-Item -Path (Join-Path $Packaging 'HUONG-DAN.txt') -Destination $Staging -Force

$defaultsJson = Join-Path $DefaultsDir 'fishing.json'
if (-not (Test-Path $defaultsJson)) {
    throw "Thieu $defaultsJson. Chay '.\tools\build-portable.ps1 -SyncDefaults' truoc."
}
Copy-Item -Path $defaultsJson -Destination $Staging -Force

$defaultsFishing = Join-Path $DefaultsDir 'fishing'
$roiCount = 0
if (Test-Path $defaultsFishing) {
    $roiCount = Copy-TreeFiltered -From $defaultsFishing -To (Join-Path $Staging 'fishing')
}
Write-Host "Da gom $roiCount file ROI job cau ca."

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$zip = Join-Path $OutDir "GtaMiniGameBot-portable-$stamp-$sha.zip"
if (Test-Path $zip) { Remove-Item -Path $zip -Force }

Write-Host "Nen ra zip..."
Compress-Archive -Path (Join-Path $Staging '*') -DestinationPath $zip -CompressionLevel Optimal

Remove-Item -Path $Staging -Recurse -Force

$sizeMb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host ""
Write-Host "Xong: $zip ($sizeMb MB)"
