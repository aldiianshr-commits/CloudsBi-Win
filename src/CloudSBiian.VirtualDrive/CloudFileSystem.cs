using System.Runtime.InteropServices;
using System.Security.AccessControl;
using CloudSBiian.Core.Logging;
using Fsp;
using FileInfo = Fsp.Interop.FileInfo;
using VolumeInfo = Fsp.Interop.VolumeInfo;

namespace CloudSBiian.VirtualDrive;

/// <summary>
/// WinFsp adapter: translates WinFsp callbacks into <see cref="DriveCore"/> operations.
/// Every callback is wrapped so that backend failures become NTSTATUS codes instead of exceptions.
/// </summary>
internal sealed class CloudFileSystem : FileSystemBase
{
    private const uint AttrDirectory = (uint)FileAttributes.Directory;
    private const uint AttrArchive = (uint)FileAttributes.Archive;
    private const uint AttrReadOnly = (uint)FileAttributes.ReadOnly;
    private const uint AttrOffline = (uint)FileAttributes.Offline;

    private static readonly byte[] DefaultSecurity = BuildSecurityDescriptor();

    private readonly DriveCore _core;
    private readonly IAppLog _log;
    private readonly string _label;

    public CloudFileSystem(DriveCore core, string volumeLabel, IAppLog log)
    {
        _core = core;
        _label = volumeLabel;
        _log = log;
    }

    private static byte[] BuildSecurityDescriptor()
    {
        // Owner/Group: Administrators; DACL: full access for SYSTEM, Administrators and the interactive user's session (Everyone).
        var sd = new RawSecurityDescriptor("O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;WD)");
        var bytes = new byte[sd.BinaryLength];
        sd.GetBinaryForm(bytes, 0);
        return bytes;
    }

    public override int Init(object host0)
    {
        var host = (FileSystemHost)host0;
        host.SectorSize = 4096;
        host.SectorsPerAllocationUnit = 1;
        host.MaxComponentLength = 255;
        host.FileInfoTimeout = 1000;
        host.CaseSensitiveSearch = false;
        host.CasePreservedNames = true;
        host.UnicodeOnDisk = true;
        host.PersistentAcls = false;
        host.PostCleanupWhenModifiedOnly = true;
        host.VolumeCreationTime = (ulong)DateTime.UtcNow.ToFileTimeUtc();
        host.VolumeSerialNumber = (uint)(host.VolumeCreationTime / (10000 * 1000));
        host.FileSystemName = "CloudSBiian";
        return NtStatus.Success;
    }

    public override int ExceptionHandler(Exception ex)
    {
        var status = NtStatus.FromException(ex);
        _log.Warn("winfsp", $"Operation failed: 0x{status:X8}", ex);
        return status;
    }

    public override int GetVolumeInfo(out VolumeInfo volumeInfo)
    {
        volumeInfo = default;
        var (total, free) = _core.VolumeSize();
        volumeInfo.TotalSize = total;
        volumeInfo.FreeSize = free;
        volumeInfo.SetVolumeLabel(_label);
        return NtStatus.Success;
    }

    public override int SetVolumeLabel(string volumeLabel, out VolumeInfo volumeInfo)
    {
        return GetVolumeInfo(out volumeInfo);
    }

    public override int GetSecurityByName(string fileName, out uint fileAttributes, ref byte[] securityDescriptor)
    {
        fileAttributes = 0;
        try
        {
            var info = _core.GetInfo(fileName);
            if (info == null)
            {
                return NtStatus.ObjectNameNotFound;
            }

            fileAttributes = Attributes(info.Value);
            if (securityDescriptor != null)
            {
                securityDescriptor = DefaultSecurity;
            }

            return NtStatus.Success;
        }
        catch (Exception ex)
        {
            return Fail("GetSecurityByName", fileName, ex);
        }
    }

    public override int Create(
        string fileName,
        uint createOptions,
        uint grantedAccess,
        uint fileAttributes,
        byte[] securityDescriptor,
        ulong allocationSize,
        out object fileNode,
        out object fileDesc,
        out FileInfo fileInfo,
        out string normalizedName)
    {
        fileNode = default!;
        fileDesc = default!;
        fileInfo = default;
        normalizedName = default!;
        try
        {
            var directory = (createOptions & NtStatus.FileDirectoryFile) != 0;
            var status = _core.Create(fileName, directory, out var node);
            _log.Debug("winfsp", $"Create {fileName} dir={directory} = 0x{status:X8}");
            if (status != NtStatus.Success || node == null)
            {
                return status;
            }

            fileNode = node;
            fileInfo = ToFileInfo(_core.GetInfo(node));
            return NtStatus.Success;
        }
        catch (Exception ex)
        {
            return Fail("Create", fileName, ex);
        }
    }

    public override int Open(
        string fileName,
        uint createOptions,
        uint grantedAccess,
        out object fileNode,
        out object fileDesc,
        out FileInfo fileInfo,
        out string normalizedName)
    {
        fileNode = default!;
        fileDesc = default!;
        fileInfo = default;
        normalizedName = default!;
        try
        {
            var status = _core.Open(fileName, out var node);
            _log.Debug("winfsp", $"Open {fileName} access=0x{grantedAccess:X} options=0x{createOptions:X} = 0x{status:X8}");
            if (status != NtStatus.Success || node == null)
            {
                return status;
            }

            fileNode = node;
            fileInfo = ToFileInfo(_core.GetInfo(node));
            return NtStatus.Success;
        }
        catch (Exception ex)
        {
            return Fail("Open", fileName, ex);
        }
    }

    public override int Overwrite(
        object fileNode,
        object fileDesc,
        uint fileAttributes,
        bool replaceFileAttributes,
        ulong allocationSize,
        out FileInfo fileInfo)
    {
        fileInfo = default;
        var node = (DriveNode)fileNode;
        try
        {
            var status = _core.Overwrite(node);
            fileInfo = ToFileInfo(_core.GetInfo(node));
            return status;
        }
        catch (Exception ex)
        {
            return Fail("Overwrite", node.CloudPath, ex);
        }
    }

    public override void Cleanup(object fileNode, object fileDesc, string fileName, uint flags)
    {
        var node = (DriveNode)fileNode;
        try
        {
            if ((flags & NtStatus.CleanupDelete) != 0)
            {
                _log.Debug("winfsp", $"Cleanup with delete: {fileName} (node path {node.CloudPath})");
            }

            _core.Cleanup(node, (flags & NtStatus.CleanupDelete) != 0);
        }
        catch (Exception ex)
        {
            Fail("Cleanup", node.CloudPath, ex);
        }
    }

    public override void Close(object fileNode, object fileDesc)
    {
        if (fileNode is DriveNode node)
        {
            try
            {
                _core.Close(node);
            }
            catch (Exception ex)
            {
                Fail("Close", node.CloudPath, ex);
            }
        }
    }

    public override int Read(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length, out uint bytesTransferred)
    {
        bytesTransferred = 0;
        var node = (DriveNode)fileNode;
        try
        {
            var temp = System.Buffers.ArrayPool<byte>.Shared.Rent((int)length);
            try
            {
                var status = _core.Read(node, (long)offset, temp.AsSpan(0, (int)length), out var read);
                if (status != NtStatus.Success)
                {
                    return status;
                }

                Marshal.Copy(temp, 0, buffer, read);
                bytesTransferred = (uint)read;
                return NtStatus.Success;
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(temp);
            }
        }
        catch (Exception ex)
        {
            return Fail("Read", node.CloudPath, ex);
        }
    }

    public override int Write(
        object fileNode,
        object fileDesc,
        IntPtr buffer,
        ulong offset,
        uint length,
        bool writeToEndOfFile,
        bool constrainedIo,
        out uint bytesTransferred,
        out FileInfo fileInfo)
    {
        bytesTransferred = 0;
        fileInfo = default;
        var node = (DriveNode)fileNode;
        try
        {
            var temp = System.Buffers.ArrayPool<byte>.Shared.Rent((int)length);
            try
            {
                Marshal.Copy(buffer, temp, 0, (int)length);
                var status = _core.Write(node, (long)offset, temp.AsSpan(0, (int)length), writeToEndOfFile, constrainedIo, out var written);
                bytesTransferred = (uint)written;
                fileInfo = ToFileInfo(_core.GetInfo(node));
                return status;
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(temp);
            }
        }
        catch (Exception ex)
        {
            return Fail("Write", node.CloudPath, ex);
        }
    }

    public override int Flush(object fileNode, object fileDesc, out FileInfo fileInfo)
    {
        fileInfo = default;
        if (fileNode is not DriveNode node)
        {
            // Volume flush.
            return NtStatus.Success;
        }

        try
        {
            var status = _core.Flush(node);
            fileInfo = ToFileInfo(_core.GetInfo(node));
            return status;
        }
        catch (Exception ex)
        {
            return Fail("Flush", node.CloudPath, ex);
        }
    }

    public override int GetFileInfo(object fileNode, object fileDesc, out FileInfo fileInfo)
    {
        fileInfo = default;
        var node = (DriveNode)fileNode;
        try
        {
            fileInfo = ToFileInfo(_core.GetInfo(node));
            return NtStatus.Success;
        }
        catch (Exception ex)
        {
            return Fail("GetFileInfo", node.CloudPath, ex);
        }
    }

    public override int SetBasicInfo(
        object fileNode,
        object fileDesc,
        uint fileAttributes,
        ulong creationTime,
        ulong lastAccessTime,
        ulong lastWriteTime,
        ulong changeTime,
        out FileInfo fileInfo)
    {
        // Cloud backends keep their own timestamps; accept the request so applications do not fail.
        fileInfo = default;
        var node = (DriveNode)fileNode;
        try
        {
            fileInfo = ToFileInfo(_core.GetInfo(node));
            return NtStatus.Success;
        }
        catch (Exception ex)
        {
            return Fail("SetBasicInfo", node.CloudPath, ex);
        }
    }

    public override int SetFileSize(object fileNode, object fileDesc, ulong newSize, bool setAllocationSize, out FileInfo fileInfo)
    {
        fileInfo = default;
        var node = (DriveNode)fileNode;
        try
        {
            var status = _core.SetFileSize(node, (long)newSize, setAllocationSize);
            fileInfo = ToFileInfo(_core.GetInfo(node));
            return status;
        }
        catch (Exception ex)
        {
            return Fail("SetFileSize", node.CloudPath, ex);
        }
    }

    public override int CanDelete(object fileNode, object fileDesc, string fileName)
    {
        var node = (DriveNode)fileNode;
        try
        {
            var status = _core.CanDelete(node);
            _log.Debug("winfsp", $"CanDelete {fileName} = 0x{status:X8}");
            return status;
        }
        catch (Exception ex)
        {
            return Fail("CanDelete", fileName, ex);
        }
    }

    public override int Rename(object fileNode, object fileDesc, string fileName, string newFileName, bool replaceIfExists)
    {
        var node = (DriveNode)fileNode;
        try
        {
            var status = _core.Rename(node, newFileName, replaceIfExists);
            _log.Debug("winfsp", $"Rename {fileName} -> {newFileName} (replace={replaceIfExists}) = 0x{status:X8}");
            return status;
        }
        catch (Exception ex)
        {
            return Fail("Rename", fileName, ex);
        }
    }

    public override int GetSecurity(object fileNode, object fileDesc, ref byte[] securityDescriptor)
    {
        securityDescriptor = DefaultSecurity;
        return NtStatus.Success;
    }

    public override bool ReadDirectoryEntry(
        object fileNode,
        object fileDesc,
        string pattern,
        string marker,
        ref object context,
        out string fileName,
        out FileInfo fileInfo)
    {
        fileName = default!;
        fileInfo = default;
        var node = (DriveNode)fileNode;
        List<(string Name, NodeInfo Info)> entries;
        try
        {
            lock (node.Lock)
            {
                if (node.DirectorySnapshot == null || (context == null && marker == null))
                {
                    var list = new List<(string, NodeInfo)>();
                    var isVolumeRoot = node.IsVirtualRoot || (_core.SingleAccount && node.IsAccountRoot);
                    if (!isVolumeRoot)
                    {
                        var self = _core.GetInfo(node);
                        list.Add((".", self));
                        list.Add(("..", self));
                    }

                    list.AddRange(_core.ListDirectory(node));
                    node.DirectorySnapshot = list;
                }

                entries = node.DirectorySnapshot;
            }
        }
        catch (Exception ex)
        {
            Fail("ReadDirectory", node.CloudPath, ex);
            return false;
        }

        int index;
        if (context == null)
        {
            index = 0;
            if (marker != null)
            {
                index = entries.FindIndex(e => string.Equals(e.Name, marker, StringComparison.OrdinalIgnoreCase));
                index = index < 0 ? entries.Count : index + 1;
            }
        }
        else
        {
            index = (int)context;
        }

        if (index >= entries.Count)
        {
            return false;
        }

        context = index + 1;
        fileName = entries[index].Name;
        fileInfo = ToFileInfo(entries[index].Info);
        return true;
    }

    private int Fail(string operation, string path, Exception ex)
    {
        var status = NtStatus.FromException(ex);
        var level = status is NtStatus.ObjectNameNotFound or NtStatus.ObjectPathNotFound ? LogLevel.Debug : LogLevel.Warning;
        _log.Write(level, "drive", $"{operation} {path} -> 0x{status:X8}: {ex.Message}");
        return status;
    }

    private static uint Attributes(NodeInfo info)
    {
        if (info.IsDirectory)
        {
            return AttrDirectory;
        }

        var attributes = AttrArchive;
        if (info.ReadOnly)
        {
            attributes |= AttrReadOnly;
        }

        return attributes;
    }

    private static FileInfo ToFileInfo(NodeInfo info)
    {
        var fi = default(FileInfo);
        fi.FileAttributes = Attributes(info);
        fi.FileSize = (ulong)Math.Max(0, info.Size);
        fi.AllocationSize = (fi.FileSize + 4095) / 4096 * 4096;
        fi.CreationTime = ToFileTime(info.Created);
        fi.LastWriteTime = ToFileTime(info.Modified);
        fi.LastAccessTime = fi.LastWriteTime;
        fi.ChangeTime = fi.LastWriteTime;
        return fi;
    }

    private static ulong ToFileTime(DateTimeOffset t)
    {
        try
        {
            return (ulong)Math.Max(0, t.UtcDateTime.ToFileTimeUtc());
        }
        catch (ArgumentOutOfRangeException)
        {
            return 0;
        }
    }
}
