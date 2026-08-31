param(
    [ValidateNotNullOrEmpty()]
    $InFile = "V:\VDISK\tidygpuwin.vhdx",
    [ValidateNotNullOrEmpty()]
    $OutFile = "V:\VDISK\refsblockclone.renameme"
)

# ReFS Block Clone (fixed build, 64-bit only)
# Based on the original by Sergey Gruzdov (egel@egel.su), with fixes for:
#   1) source files whose logical size is NOT a multiple of the volume cluster size
#      (dense OR sparse) -- final chunk is rounded up to the cluster boundary and the
#      output EOF is then shrunk back to the source size for a byte-identical clone;
#   2) destination volume validation (must support block cloning AND be the same volume);
#   3) immediate Win32 error capture at every API failure + non-zero process exit code;
#   4) 64-bit-only core: DUPLICATE_EXTENTS_DATA.FileHandle is a fixed 8-byte field
#      (HANDLE is 8 bytes on x64) -- no 32-bit support, best 64-bit performance;
#      correct elapsed-time display.
# Block cloning stays a pure metadata operation (no data read/write), which is what
# makes it SSD-friendly: near-zero NAND writes, near-zero space use, instant copies.

$FILE_SUPPORTS_BLOCK_REFCOUNTING = 0x08000000
$GENERIC_READ = 0x80000000L
$GENERIC_WRITE = 0x40000000L
$DELETE = 0x00010000L
$FILE_SHARE_READ = 0x00000001
$OPEN_EXISTING = 3
$CREATE_NEW = 1
$INVALID_HANDLE_VALUE = -1
$SIZEOF_FSCTL_GET_INTEGRITY_INFORMATION_BUFFER = 16
$SIZEOF_FSCTL_SET_INTEGRITY_INFORMATION_BUFFER = 8
$SIZEOF_FILE_END_OF_FILE_INFO = 8
$SIZEOF_FILE_DISPOSITION_INFO = 1
$FSCTL_GET_INTEGRITY_INFORMATION = 0x9027C
$FSCTL_SET_INTEGRITY_INFORMATION = 0x9C280
$FSCTL_DUPLICATE_EXTENTS_TO_FILE = 0x98344
$FSCTL_SET_SPARSE = 0x000900C4
$FileEndOfFileInfo = 6
$FileDispositionInfo = 4

$StructsDefinition = @'
using System;
using System.Runtime.InteropServices;

namespace CloneStructs
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct FSCTL_GET_INTEGRITY_INFORMATION_BUFFER
    {
        public ushort ChecksumAlgorithm;
        public ushort Reserved;
        public uint Flags;
        public uint ChecksumChunkSizeInBytes;
        public uint ClusterSizeInBytes;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct FSCTL_SET_INTEGRITY_INFORMATION_BUFFER
    {
        public ushort ChecksumAlgorithm;
        public ushort Reserved;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct FILE_DISPOSITION_INFO
    {
        public bool DeleteFile;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct FILE_END_OF_FILE_INFO
    {
        public ulong EndOfFile;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct DUPLICATE_EXTENTS_DATA
    {
        public long FileHandle;   // 64-bit only: HANDLE is 8 bytes on x64 (no 32-bit support)
        public ulong SourceFileOffset;
        public ulong TargetFileOffset;
        public ulong ByteCount;
    }
}
'@

$MethodDefinitions = @'
[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
public static extern IntPtr CreateFileW(
    string lpFileName,
    uint dwDesiredAccess,
    uint dwShareMode,
    IntPtr lpSecurityAttributes,
    uint dwCreationDisposition,
    uint dwFlagsAndAttributes,
    IntPtr hTemplateFile
);

[DllImport("kernel32.dll")]
public static extern bool CloseHandle(IntPtr hObject);

[DllImport("kernel32.dll")]
public static extern uint GetLastError();

[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
public static extern bool GetVolumeInformationByHandleW(
    IntPtr hFile,
    IntPtr lpVolumeNameBuffer,
    uint nVolumeNameSize,
    out uint lpVolumeSerialNumber,
    IntPtr lpMaximumComponentLength,
    out ulong lpFileSystemFlags,
    IntPtr lpFileSystemNameBuffer,
    uint nFileSystemNameSize
);

[DllImport("kernel32.dll")]
public static extern bool GetFileSizeEx(IntPtr hFile, out ulong lpFileSize);

[DllImport("kernel32.dll")]
public static extern bool DeviceIoControl(
    IntPtr hDevice,
    uint dwIoControlCode,
    IntPtr lpInBuffer,
    uint nInBufferSize,
    IntPtr lpOutBuffer,
    uint nOutBufferSize,
    out uint lpBytesReturned,
    IntPtr lpOverlapped
);

[DllImport("kernel32.dll")]
public static extern bool SetFileInformationByHandle(
    IntPtr hFile,
    int FileInformationClass,
    IntPtr lpFileInformation,
    uint dwBufferSize
);
'@

Write-Host "Clone file using ReFS Block Clone. Written by Sergey Gruzdov (egel@egel.su)"
Write-Host "Cloning '$InFile' to '$OutFile'"

$startTime = Get-Date
$hInFile = $INVALID_HANDLE_VALUE
$hOutFile = $INVALID_HANDLE_VALUE
$dwRet = 0
$lastWin32Error = 0
$failed = $false

try
{
    $Methods = Add-Type -MemberDefinition $MethodDefinitions -Name 'Methods' -Namespace 'Win32' -PassThru
    Add-Type -TypeDefinition $StructsDefinition

    # ---- open source (GENERIC_READ, share-read only) ----
    $hInFile = $Methods::CreateFileW($InFile, $GENERIC_READ, $FILE_SHARE_READ, [IntPtr]::Zero, $OPEN_EXISTING, 0, [IntPtr]::Zero)
    if ($hInFile -eq $INVALID_HANDLE_VALUE)
    {
        $lastWin32Error = $Methods::GetLastError()
        throw ("Unable to open file '{0}' (Win32 error 0x{1:X})" -f $InFile, $lastWin32Error)
    }

    # ---- source volume capability ----
    $srcVolumeFlags = [uint64]0
    $srcSerial = [uint32]0
    if (!($Methods::GetVolumeInformationByHandleW($hInFile, [IntPtr]::Zero, 0, [ref]$srcSerial, [IntPtr]::Zero, [ref]$srcVolumeFlags, [IntPtr]::Zero, 0)))
    {
        $lastWin32Error = $Methods::GetLastError()
        throw ("Unable to get volume information for source file (Win32 error 0x{0:X})" -f $lastWin32Error)
    }
    if (!($srcVolumeFlags -band $FILE_SUPPORTS_BLOCK_REFCOUNTING))
    {
        throw "Source volume does not support block cloning (not ReFS / older ReFS)!"
    }

    # ---- source logical size ----
    $SourceFileSize = [uint64]0
    if (!$($Methods::GetFileSizeEx($hInFile, [ref]$SourceFileSize)))
    {
        $lastWin32Error = $Methods::GetLastError()
        throw ("Unable to get size of source file '{0}' (Win32 error 0x{1:X})" -f $InFile, $lastWin32Error)
    }

    # ---- create output (CREATE_NEW) ----
    $hOutFile = $Methods::CreateFileW($OutFile, ($GENERIC_READ -bor $GENERIC_WRITE -bor $DELETE), 0, [IntPtr]::Zero, $CREATE_NEW, 0, [IntPtr]::Zero)
    if ($hOutFile -eq $INVALID_HANDLE_VALUE)
    {
        $lastWin32Error = $Methods::GetLastError()
        throw ("Unable to create output file '{0}' (Win32 error 0x{1:X})" -f $OutFile, $lastWin32Error)
    }

    # ---- mark output sparse (REQUIRED when source is sparse; harmless otherwise) ----
    $null = $Methods::DeviceIoControl($hOutFile, $FSCTL_SET_SPARSE, [IntPtr]::Zero, 0, [IntPtr]::Zero, 0, [ref]$dwRet, [IntPtr]::Zero)

    # ---- delete-pending trick: lets ReFS clone into a freshly-sized output ----
    # (set FIRST so that ANY later failure auto-deletes the partial output on close)
    $disposeInfo = New-Object CloneStructs.FILE_DISPOSITION_INFO
    $disposeInfo.DeleteFile = $true
    $ptrInfo = [System.Runtime.InteropServices.Marshal]::AllocHGlobal($SIZEOF_FILE_DISPOSITION_INFO)
    [System.Runtime.InteropServices.Marshal]::StructureToPtr($disposeInfo, $ptrInfo, $false)
    $result = $Methods::SetFileInformationByHandle($hOutFile, $FileDispositionInfo, $ptrInfo, $SIZEOF_FILE_DISPOSITION_INFO)
    [System.Runtime.InteropServices.Marshal]::FreeHGlobal($ptrInfo)
    if (!$result)
    {
        $lastWin32Error = $Methods::GetLastError()
        throw ("Unable to set file disposition (Win32 error 0x{0:X})" -f $lastWin32Error)
    }

    # ---- NEW: destination must support block cloning AND be the same volume ----
    $dstVolumeFlags = [uint64]0
    $dstSerial = [uint32]0
    if (!($Methods::GetVolumeInformationByHandleW($hOutFile, [IntPtr]::Zero, 0, [ref]$dstSerial, [IntPtr]::Zero, [ref]$dstVolumeFlags, [IntPtr]::Zero, 0)))
    {
        $lastWin32Error = $Methods::GetLastError()
        throw ("Unable to get volume information for output file (Win32 error 0x{0:X})" -f $lastWin32Error)
    }
    if (!($dstVolumeFlags -band $FILE_SUPPORTS_BLOCK_REFCOUNTING))
    {
        throw "Destination volume does not support block cloning! Output must be on the same ReFS volume as the source."
    }
    if ($srcSerial -ne $dstSerial)
    {
        throw "Source and destination must reside on the same ReFS volume (volume serial mismatch)!"
    }

    # ---- source integrity + cluster size ----
    $sourceFileIntegrity = New-Object CloneStructs.FSCTL_GET_INTEGRITY_INFORMATION_BUFFER
    $type = $sourceFileIntegrity.GetType()
    $ptrInfo = [System.Runtime.InteropServices.Marshal]::AllocHGlobal($SIZEOF_FSCTL_GET_INTEGRITY_INFORMATION_BUFFER)
    if (!($Methods::DeviceIoControl($hInFile, $FSCTL_GET_INTEGRITY_INFORMATION, [IntPtr]::Zero, 0, $ptrInfo, $SIZEOF_FSCTL_GET_INTEGRITY_INFORMATION_BUFFER, [ref]$dwRet, [IntPtr]::Zero)))
    {
        $lastWin32Error = $Methods::GetLastError()
        [System.Runtime.InteropServices.Marshal]::FreeHGlobal($ptrInfo)
        throw ("Unable to get integrity of input file (Win32 error 0x{0:X})" -f $lastWin32Error)
    }
    $sourceFileIntegrity = [System.Runtime.InteropServices.Marshal]::PtrToStructure($ptrInfo, [System.Type]$type)
    [System.Runtime.InteropServices.Marshal]::FreeHGlobal($ptrInfo)

    $ClusterSize = [uint64]$sourceFileIntegrity.ClusterSizeInBytes
    if ($ClusterSize -eq 0) { $ClusterSize = 4096 }

    # ---- NEW: round clone extent UP to a cluster boundary (fixes non-aligned file sizes, dense or sparse) ----
    $AlignedSize = [uint64]([Math]::Ceiling([double]$SourceFileSize / [double]$ClusterSize) * [double]$ClusterSize)

    # ---- output EOF must be large enough to hold the aligned clone ----
    $endOfOutFileInfo = New-Object CloneStructs.FILE_END_OF_FILE_INFO
    $endOfOutFileInfo.EndOfFile = $AlignedSize
    $ptrInfo = [System.Runtime.InteropServices.Marshal]::AllocHGlobal($SIZEOF_FILE_END_OF_FILE_INFO)
    [System.Runtime.InteropServices.Marshal]::StructureToPtr($endOfOutFileInfo, $ptrInfo, $false)
    $result = $Methods::SetFileInformationByHandle($hOutFile, $FileEndOfFileInfo, $ptrInfo, $SIZEOF_FILE_END_OF_FILE_INFO)
    [System.Runtime.InteropServices.Marshal]::FreeHGlobal($ptrInfo)
    if (!$result)
    {
        $lastWin32Error = $Methods::GetLastError()
        throw ("Unable to set end of output file (Win32 error 0x{0:X})" -f $lastWin32Error)
    }

    # ---- copy integrity setting from source to output (must match) ----
    $tragetFileIntegrity = New-Object CloneStructs.FSCTL_SET_INTEGRITY_INFORMATION_BUFFER
    $tragetFileIntegrity.ChecksumAlgorithm = $sourceFileIntegrity.ChecksumAlgorithm
    $tragetFileIntegrity.Reserved = $sourceFileIntegrity.Reserved
    $tragetFileIntegrity.Flags = $sourceFileIntegrity.Flags
    $ptrInfo = [System.Runtime.InteropServices.Marshal]::AllocHGlobal($SIZEOF_FSCTL_SET_INTEGRITY_INFORMATION_BUFFER)
    [System.Runtime.InteropServices.Marshal]::StructureToPtr($tragetFileIntegrity, $ptrInfo, $true)
    $result = $Methods::DeviceIoControl($hOutFile, $FSCTL_SET_INTEGRITY_INFORMATION, $ptrInfo, $SIZEOF_FSCTL_SET_INTEGRITY_INFORMATION_BUFFER, [IntPtr]::Zero, 0, [ref]$dwRet, [IntPtr]::Zero)
    [System.Runtime.InteropServices.Marshal]::FreeHGlobal($ptrInfo)
    if (!$result)
    {
        $lastWin32Error = $Methods::GetLastError()
        throw ("Unable to set integrity of output file (Win32 error 0x{0:X})" -f $lastWin32Error)
    }

    # ---- clone loop over the aligned range (all offsets/byte counts are cluster multiples) ----
    $ByteCount = 100Mb
    $dupExtent = New-Object CloneStructs.DUPLICATE_EXTENTS_DATA
    $SIZEOF_DUP = [System.Runtime.InteropServices.Marshal]::SizeOf($dupExtent)
    $ptrInfo = [System.Runtime.InteropServices.Marshal]::AllocHGlobal($SIZEOF_DUP)
    $dupExtent.FileHandle = $hInFile
    $dupExtent.ByteCount = $ByteCount
    $FileOffset = [uint64]0

    while ($FileOffset -lt $AlignedSize)
    {
        $dupExtent.SourceFileOffset = $FileOffset
        $dupExtent.TargetFileOffset = $FileOffset
        $dupExtent.ByteCount = $ByteCount
        if (($FileOffset + $ByteCount) -gt $AlignedSize)
        {
            $dupExtent.ByteCount = $AlignedSize - $FileOffset
        }

        [System.Runtime.InteropServices.Marshal]::StructureToPtr($dupExtent, $ptrInfo, $false)

        $status = $Methods::DeviceIoControl($hOutFile, $FSCTL_DUPLICATE_EXTENTS_TO_FILE, $ptrInfo, $SIZEOF_DUP, [IntPtr]::Zero, 0, [ref]$dwRet, [IntPtr]::Zero)
        if (!$status)
        {
            $lastWin32Error = $Methods::GetLastError()
            throw ("DeviceIoControl failed at offset: {0} (Win32 error 0x{1:X})" -f $FileOffset, $lastWin32Error)
        }

        $FileOffset += $dupExtent.ByteCount
    }
    [System.Runtime.InteropServices.Marshal]::FreeHGlobal($ptrInfo)

    # ---- NEW: shrink output back to the source's exact logical size (byte-identical clone) ----
    if ($AlignedSize -gt $SourceFileSize)
    {
        $endOfOutFileInfo.EndOfFile = $SourceFileSize
        $ptrInfo = [System.Runtime.InteropServices.Marshal]::AllocHGlobal($SIZEOF_FILE_END_OF_FILE_INFO)
        [System.Runtime.InteropServices.Marshal]::StructureToPtr($endOfOutFileInfo, $ptrInfo, $false)
        $result = $Methods::SetFileInformationByHandle($hOutFile, $FileEndOfFileInfo, $ptrInfo, $SIZEOF_FILE_END_OF_FILE_INFO)
        [System.Runtime.InteropServices.Marshal]::FreeHGlobal($ptrInfo)
        if (!$result)
        {
            $lastWin32Error = $Methods::GetLastError()
            throw ("Unable to trim output file size (Win32 error 0x{0:X})" -f $lastWin32Error)
        }
    }

    # ---- clear delete-pending so the output file survives ----
    $disposeInfo.DeleteFile = $false
    $ptrInfo = [System.Runtime.InteropServices.Marshal]::AllocHGlobal($SIZEOF_FILE_DISPOSITION_INFO)
    [System.Runtime.InteropServices.Marshal]::StructureToPtr($disposeInfo, $ptrInfo, $false)
    $result = $Methods::SetFileInformationByHandle($hOutFile, $FileDispositionInfo, $ptrInfo, $SIZEOF_FILE_DISPOSITION_INFO)
    [System.Runtime.InteropServices.Marshal]::FreeHGlobal($ptrInfo)
    if (!$result)
    {
        $lastWin32Error = $Methods::GetLastError()
        throw ("Unable to clear file disposition (Win32 error 0x{0:X})" -f $lastWin32Error)
    }

    $elapsed = (Get-Date) - $startTime
    Write-Host ("Completed in {0:N3} second(s)" -f $elapsed.TotalSeconds)
}
catch
{
    Write-Host ("ERROR: {0}" -f $_.Exception.Message)
    $failed = $true
}
finally
{
    if ($hInFile -ne $INVALID_HANDLE_VALUE)
    {
        [void]$Methods::CloseHandle($hInFile)
    }
    if ($hOutFile -ne $INVALID_HANDLE_VALUE)
    {
        [void]$Methods::CloseHandle($hOutFile)
    }
}
if ($failed) { exit 1 }
