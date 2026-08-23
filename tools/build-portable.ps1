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

# Nhung gi ban portable phai mang theo, khop voi AppPaths.MigrateFromExeFolder.
# Doi o day thi phai doi ca ben kia, khong thi file duoc nen vao zip ma app khong
# chep sang %APPDATA% - dung cai bay da gap: items/ nam trong zip nhung bi bo qua.
$PortableFiles = @('fishing.json', 'config.json', 'hotkeys.json', 'miner.json', 'wood.json', 'electric.json')
$PortableDirs = @('fishing', 'items', 'wood', 'electric')

# Loc thu chi dung tren MAY NAY ra khoi snapshot dem di share.
#
#  - ItemCachePath la duong dan cache game cua rieng may goc; may khac tro sai cho.
#  - FishSlots GIU NGUYEN: DescribeTrunkGaps can o chua ca de bat do cop.
function Remove-MachineSpecific {
    param([string] $JsonPath)

    if (-not (Test-Path $JsonPath)) { return @() }

    $text = [System.IO.File]::ReadAllText($JsonPath)
    $gone = @()

    $next = [regex]::Replace($text, '[ \t]*"ItemCachePath"\s*:\s*"[^"]*"\s*,\s*\r?\n', '')
    if ($next -ne $text) { $gone += 'ItemCachePath'; $text = $next }

    if ($gone.Count -gt 0) {
        [System.IO.File]::WriteAllText($JsonPath, $text, (New-Object System.Text.UTF8Encoding($false)))
    }
    return $gone
}

# Ban dang chay bat do cop thi zip phai mang FishSlots. Rong = nguoi nhan thay "thieu o chua ca".
function Test-TrunkDumpReady {
    param([string] $JsonPath)

    $cfg = Get-Content -LiteralPath $JsonPath -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($p in $cfg.Profiles.PSObject.Properties) {
        $prof = $p.Value
        $slots = @($prof.FishSlots)
        if ($prof.TrunkDumpEnabled -and $slots.Count -lt 1) {
            throw "TrunkDumpEnabled nhung FishSlots rong ($($p.Name)). Khong ship ban thieu do cop."
        }
    }
}

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
        # Snapshot nay di vao git roi ra ban share, nen khong mang theo file tam.
        if ($file.Name -like '*.bak*' -or $file.Name -like '*.tmp') { continue }

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

    if (-not (Test-Path $srcJson)) {
        throw "Khong thay $srcJson. Mo app khoanh vung job cau ca truoc da."
    }

    New-Item -ItemType Directory -Force -Path $DefaultsDir | Out-Null

    # Config tung job. Thieu file nao thi bo qua - khong phai ai cung dung du cac job.
    foreach ($name in $PortableFiles) {
        $src = Join-Path $UserDataDir $name
        if (Test-Path $src) { Copy-Item -Path $src -Destination (Join-Path $DefaultsDir $name) -Force }
    }

    # Xoa truoc roi chep lai, de file da bo trong app cung bien mat khoi snapshot.
    $copied = 0
    foreach ($dir in $PortableDirs) {
        $dst = Join-Path $DefaultsDir $dir
        if (Test-Path $dst) { Remove-Item -Path $dst -Recurse -Force }

        $src = Join-Path $UserDataDir $dir
        if (Test-Path $src) { $copied += Copy-TreeFiltered -From $src -To $dst }
    }

    $stripped = Remove-MachineSpecific (Join-Path $DefaultsDir 'fishing.json')

    Write-Host "Da dong bo ROI ve $DefaultsDir ($copied file, da bo shots/ va debug-*)."
    if ($stripped) { Write-Host "Da luoc khoi snapshot: $($stripped -join ', ')." }
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

Copy-Item -Path (Join-Path $Packaging 'HUONG-DAN.txt') -Destination $Staging -Force

$defaultsJson = Join-Path $DefaultsDir 'fishing.json'
if (-not (Test-Path $defaultsJson)) {
    throw "Thieu $defaultsJson. Chay '.\tools\build-portable.ps1 -SyncDefaults' truoc."
}

foreach ($name in $PortableFiles) {
    $src = Join-Path $DefaultsDir $name
    if (-not (Test-Path $src) -and $name -eq 'config.json') {
        $src = Join-Path $RepoRoot 'app\config.json'   # ban cu giu config.json trong app/
    }
    if (Test-Path $src) { Copy-Item -Path $src -Destination $Staging -Force }
    else { Write-Warning "khong co $name - ban portable se chay voi mac dinh cho phan do." }
}

Test-TrunkDumpReady (Join-Path $Staging 'fishing.json')

$roiCount = 0
foreach ($dir in $PortableDirs) {
    $src = Join-Path $DefaultsDir $dir
    if (Test-Path $src) { $roiCount += Copy-TreeFiltered -From $src -To (Join-Path $Staging $dir) }
}
Write-Host "Da gom $roiCount file du lieu (ROI + bo icon vat pham)."

# Thieu bo icon la bot tut ve che do o khai bao. Chan o day chu khong de nguoi nhan
# phat hien bang cach thay ca khong duoc keo.
$iconCount = 0
$stagedItems = Join-Path $Staging 'items'
if (Test-Path $stagedItems) { $iconCount = (Get-ChildItem $stagedItems -File).Count }
if ($iconCount -lt 1) {
    throw "Thieu bo icon vat pham trong ban dong goi. Chay '-SyncDefaults' sau khi da trich icon trong app."
}
Write-Host "Bo icon vat pham: $iconCount file."

# ROI tho moc di theo $PortableDirs nhu moi job khac, khong can khoi rieng. Thieu thi
# ban share van chay, chi la nguoi nhan phai tu khoanh - job tu ha xuong go E mu.

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$zip = Join-Path $OutDir "GtaMiniGameBot-portable-$stamp-$sha.zip"
if (Test-Path $zip) { Remove-Item -Path $zip -Force }

Write-Host "Nen ra zip..."
Compress-Archive -Path (Join-Path $Staging '*') -DestinationPath $zip -CompressionLevel Optimal

Remove-Item -Path $Staging -Recurse -Force

$sizeMb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host ""
Write-Host "Xong: $zip ($sizeMb MB)"
