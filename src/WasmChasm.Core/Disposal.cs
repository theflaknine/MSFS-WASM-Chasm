using System.IO.Compression;

namespace WasmChasm.Core;

/// <summary>What happened to one folder during disposal.</summary>
public sealed record DisposalReport(bool LinkOnly, string? BackupPath);

public sealed class RecycleBinUnavailableException : IOException
{
    public RecycleBinUnavailableException(string message) : base(message) { }
}

/// <summary>Removes one already-guarded folder. Implementations: RecycleBin, ZipThenRecycle, Permanent.</summary>
public interface IDisposalStrategy
{
    DisposalMode Mode { get; }
    DisposalReport Dispose(ConsignTarget target);
}

/// <summary>Abstraction over the OS Recycle Bin so Core stays testable off Windows.</summary>
public interface IRecycleBin
{
    /// <summary>Returns null if the folder can go to the Recycle Bin, else a reason it can't.</summary>
    string? WhyCannotRecycle(string path, long sizeBytes);
    void Recycle(string path);
}

/// <summary>Shared helpers for every strategy.</summary>
public static class SafeDelete
{
    /// <summary>FR-13: if <paramref name="path"/> is a link, remove the link itself and return true.</summary>
    public static bool TryRemoveLinkOnly(string path)
    {
        if (!FileSystemWalk.IsReparsePoint(path)) return false;

        var dir = new DirectoryInfo(path);
        if (dir.Exists || dir.LinkTarget is not null)
        {
            try
            {
                // Non-recursive delete of a junction/symlink removes the link, never the target.
                dir.Delete(recursive: false);
                return true;
            }
            catch (IOException)
            {
                // Some platforms treat a directory symlink as a file.
                File.Delete(path);
                return true;
            }
        }

        File.Delete(path);
        return true;
    }

    /// <summary>Deletes one file. If it is a link, only the link goes.</summary>
    public static void DeleteFile(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists) return;
        if (file.IsReadOnly) file.IsReadOnly = false;
        file.Delete();
    }

    /// <summary>Recursive delete that never enters reparse points: links are removed, targets untouched.</summary>
    public static void DeleteTree(string path)
    {
        if (TryRemoveLinkOnly(path)) return;

        var root = new DirectoryInfo(path);
        if (!root.Exists) return;

        foreach (var child in root.EnumerateFileSystemInfos("*", new EnumerationOptions
                 {
                     RecurseSubdirectories = false,
                     AttributesToSkip = 0,
                     IgnoreInaccessible = false,
                 }).ToList())
        {
            if (FileSystemWalk.IsReparsePoint(child))
            {
                TryRemoveLinkOnly(child.FullName);
            }
            else if (child is DirectoryInfo d)
            {
                DeleteTree(d.FullName);
            }
            else
            {
                if ((child.Attributes & FileAttributes.ReadOnly) != 0)
                    child.Attributes &= ~FileAttributes.ReadOnly;
                child.Delete();
            }
        }

        if ((root.Attributes & FileAttributes.ReadOnly) != 0)
            root.Attributes &= ~FileAttributes.ReadOnly;
        root.Delete(recursive: false);
    }
}

public sealed class RecycleBinStrategy : IDisposalStrategy
{
    private readonly IRecycleBin _bin;
    public RecycleBinStrategy(IRecycleBin bin) => _bin = bin;
    public DisposalMode Mode => DisposalMode.RecycleBin;

    public DisposalReport Dispose(ConsignTarget target)
    {
        if (target.Files is not null)
        {
            foreach (var file in target.Files)
            {
                var whyNot = _bin.WhyCannotRecycle(file, target.SizeBytes);
                if (whyNot is not null) throw new RecycleBinUnavailableException(whyNot);
                _bin.Recycle(file);
            }

            return new DisposalReport(false, null);
        }

        if (SafeDelete.TryRemoveLinkOnly(target.Path)) return new DisposalReport(true, null);

        var why = _bin.WhyCannotRecycle(target.Path, target.SizeBytes);
        if (why is not null) throw new RecycleBinUnavailableException(why);
        _bin.Recycle(target.Path);
        return new DisposalReport(false, null);
    }
}

public sealed class PermanentStrategy : IDisposalStrategy
{
    public DisposalMode Mode => DisposalMode.Permanent;

    public DisposalReport Dispose(ConsignTarget target)
    {
        if (target.Files is not null)
        {
            foreach (var file in target.Files) SafeDelete.DeleteFile(file);
            return new DisposalReport(false, null);
        }

        if (SafeDelete.TryRemoveLinkOnly(target.Path)) return new DisposalReport(true, null);
        SafeDelete.DeleteTree(target.Path);
        return new DisposalReport(false, null);
    }
}

/// <summary>Zips a backup to a user folder, then hands off to an inner strategy.</summary>
public sealed class ZipThenStrategy : IDisposalStrategy
{
    private readonly string _backupFolder;
    private readonly IDisposalStrategy _then;
    private readonly Func<DateTime> _clock;

    public ZipThenStrategy(string backupFolder, IDisposalStrategy then, Func<DateTime>? clock = null)
    {
        if (!Path.IsPathFullyQualified(backupFolder))
            throw new ArgumentException("Backup folder must be a fully qualified path.", nameof(backupFolder));
        _backupFolder = backupFolder;
        _then = then;
        _clock = clock ?? (() => DateTime.Now);
    }

    public DisposalMode Mode => _then.Mode == DisposalMode.Permanent ? DisposalMode.ZipThenPermanent : DisposalMode.ZipThenRecycle;

    public DisposalReport Dispose(ConsignTarget target)
    {
        // A link has nothing of its own to back up; just remove the link.
        if (target.Files is null && SafeDelete.TryRemoveLinkOnly(target.Path)) return new DisposalReport(true, null);

        var zip = BackupZip.Create(target, _backupFolder, _clock());
        var inner = _then.Dispose(target);
        return inner with { BackupPath = zip };
    }
}

public static class BackupZip
{
    public static string Create(ConsignTarget target, string backupFolder, DateTime now)
    {
        Directory.CreateDirectory(backupFolder);
        var leaf = target.Files is not null ? "compiled" : Path.GetFileName(Path.TrimEndingDirectorySeparator(target.Path));
        var baseName = Sanitise($"{target.PackageId}_{target.SimVersionFolder}_{leaf}_{now:yyyyMMdd-HHmmss}");
        var zipPath = Path.Combine(backupFolder, baseName + ".zip");
        for (int n = 2; File.Exists(zipPath); n++)
            zipPath = Path.Combine(backupFolder, $"{baseName}_{n}.zip");

        try
        {
            using var stream = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
            if (target.Files is not null)
            {
                foreach (var file in target.Files)
                    archive.CreateEntryFromFile(file, leaf + "/" + Path.GetFileName(file), CompressionLevel.Optimal);
            }
            else
            {
                AddDirectory(archive, new DirectoryInfo(target.Path), leaf);
            }
        }
        catch
        {
            try { File.Delete(zipPath); } catch { /* best effort */ }
            throw;
        }

        return zipPath;
    }

    private static void AddDirectory(ZipArchive archive, DirectoryInfo dir, string prefix)
    {
        bool any = false;
        foreach (var child in dir.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }))
        {
            any = true;
            if (FileSystemWalk.IsReparsePoint(child)) continue; // never follow links into the backup
            var name = prefix + "/" + child.Name;
            if (child is DirectoryInfo d) AddDirectory(archive, d, name);
            else archive.CreateEntryFromFile(child.FullName, name, CompressionLevel.Optimal);
        }

        if (!any) archive.CreateEntry(prefix + "/");
    }

    private static string Sanitise(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }
}

public static class DisposalStrategies
{
    public static IDisposalStrategy Create(DisposalMode mode, IRecycleBin recycleBin, string? backupFolder) => mode switch
    {
        DisposalMode.RecycleBin => new RecycleBinStrategy(recycleBin),
        DisposalMode.Permanent => new PermanentStrategy(),
        DisposalMode.ZipThenRecycle => new ZipThenStrategy(RequireBackup(backupFolder), new RecycleBinStrategy(recycleBin)),
        DisposalMode.ZipThenPermanent => new ZipThenStrategy(RequireBackup(backupFolder), new PermanentStrategy()),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static string RequireBackup(string? folder) =>
        string.IsNullOrWhiteSpace(folder) ? throw new ArgumentException("A backup folder is required for zip backups.") : folder;
}
