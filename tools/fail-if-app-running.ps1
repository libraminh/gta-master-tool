$ErrorActionPreference = 'Stop'

$exe = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\app\GtaMiniGameBot.exe'))
if (-not (Test-Path $exe)) { exit 0 }

$running = Get-Process -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and ([System.IO.Path]::GetFullPath($_.Path) -eq $exe) }

if (-not $running) {
    try {
        $fs = [System.IO.File]::Open(
            $exe,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::ReadWrite,
            [System.IO.FileShare]::None)
        $fs.Dispose()
        exit 0
    }
    catch {
        $running = $true
    }
}

if ($running) {
    Write-Host 'Đang chạy app\GtaMiniGameBot.exe. Tắt app rồi bảo build lại.'
    exit 1
}