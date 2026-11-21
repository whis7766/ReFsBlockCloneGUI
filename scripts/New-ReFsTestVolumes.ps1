# Creates two real ReFS volumes on dynamic VHDXs for CI testing:
#   R: 40 GB max, 4 KB clusters (ReFS default)
#   S:  4 GB max, 64 KB clusters (alternative ReFS cluster size)
# Works with or without the Hyper-V PowerShell module (diskpart fallback).
param(
    [string]$VhdDir = "C:\refs_vhd",
    [string]$Drive1 = "R",
    [string]$Drive2 = "S"
)
$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $VhdDir | Out-Null

function New-RefsVolume {
    param([string]$Name, [int]$MaxGB, [string]$Letter, [int]$AllocKB)
    $file = Join-Path $VhdDir "$Name.vhdx"
    if (Test-Path $file) { Remove-Item $file -Force }
    $before = @(Get-Disk | Select-Object -ExpandProperty Number)
    if (Get-Command New-VHD -ErrorAction SilentlyContinue) {
        New-VHD -Path $file -SizeBytes ([int64]$MaxGB * 1GB) -Dynamic | Out-Null
        Mount-VHD -Path $file | Out-Null
    }
    else {
        $dp = @"
create vdisk file="$file" maximum=$($MaxGB * 1024) type=expandable
select vdisk file="$file"
attach vdisk
"@
        $dp | diskpart | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "diskpart failed for $file" }
    }
    $disk = Get-Disk | Where-Object { $before -notcontains $_.Number } | Select-Object -First 1
    if (-not $disk) { throw "attached virtual disk not found for $file" }
    Initialize-Disk -Number $disk.Number -PartitionStyle GPT | Out-Null
    $part = New-Partition -DiskNumber $disk.Number -UseMaximumSize -DriveLetter $Letter
    Format-Volume -Partition $part -FileSystem ReFS -AllocationUnitSize ($AllocKB * 1KB) `
        -NewFileSystemLabel "ReFsTest$Letter" -Confirm:$false | Out-Null
    Write-Host "Volume ${Letter}: ReFS with $AllocKB KB clusters ready ($file)"
}

New-RefsVolume -Name "refs4k"  -MaxGB 40 -Letter $Drive1 -AllocKB 4
New-RefsVolume -Name "refs64k" -MaxGB 4  -Letter $Drive2 -AllocKB 64

Write-Host "`n--- fsutil refsinfo ---"
fsutil fsinfo refsinfo "${Drive1}:"
fsutil fsinfo refsinfo "${Drive2}:"
Get-Volume | Where-Object { $_.DriveLetter -in @($Drive1, $Drive2) } |
    Format-Table DriveLetter, FileSystemType, AllocationUnitSize, Size, SizeRemaining -AutoSize