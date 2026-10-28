using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace SongDuplicateCleaner
{
    public sealed class SongEntry
    {
        public string Path { get; private set; }
        public string LevelId { get; private set; }
        public string Hash { get; private set; }
        public string Title { get; private set; }

        public SongEntry(string path, string levelId, string hash, string title)
        {
            Path = path;
            LevelId = levelId;
            Hash = hash;
            Title = title;
        }
    }

    public sealed class DuplicateGroup
    {
        public string Hash { get; private set; }
        public string Title { get; private set; }
        public SongEntry Keep { get; private set; }
        public IReadOnlyList<SongEntry> Remove { get; private set; }
        public long ReclaimBytes { get; private set; }
        internal readonly string Root;
        internal readonly ContentSnapshot KeepSnapshot;
        internal readonly ReadOnlyCollection<ContentSnapshot> RemoveSnapshots;

        internal DuplicateGroup(string root, SongEntry keep, ContentSnapshot keepSnapshot,
            IList<SongEntry> remove, IList<ContentSnapshot> removeSnapshots)
        {
            Root = root;
            Hash = keep.Hash;
            Title = keep.Title;
            Keep = keep;
            KeepSnapshot = keepSnapshot;
            Remove = new ReadOnlyCollection<SongEntry>(remove.ToArray());
            RemoveSnapshots = new ReadOnlyCollection<ContentSnapshot>(removeSnapshots.ToArray());
            ReclaimBytes = removeSnapshots.Sum(value => value.TotalBytes);
        }
    }

    public sealed class ScanResult
    {
        public IReadOnlyList<DuplicateGroup> Groups { get; private set; }
        public IReadOnlyList<string> Errors { get; private set; }
        public int DifferentContentGroups { get; private set; }
        internal ScanResult(IList<DuplicateGroup> groups, IList<string> errors, int differentContentGroups)
        {
            Groups = new ReadOnlyCollection<DuplicateGroup>(groups.ToArray());
            Errors = new ReadOnlyCollection<string>(errors.ToArray());
            DifferentContentGroups = differentContentGroups;
        }
    }

    public sealed class DeleteResult
    {
        public IReadOnlyList<string> DeletedPaths { get; private set; }
        public IReadOnlyList<string> Errors { get; private set; }
        public bool Cancelled { get; private set; }
        internal DeleteResult(IList<string> deletedPaths, IList<string> errors, bool cancelled)
        {
            DeletedPaths = new ReadOnlyCollection<string>(deletedPaths.ToArray());
            Errors = new ReadOnlyCollection<string>(errors.ToArray());
            Cancelled = cancelled;
        }
    }

    internal sealed class FileSnapshot
    {
        internal string RelativePath;
        internal long Length;
        internal string Digest;
    }

    internal sealed class ContentSnapshot
    {
        internal string Path;
        internal string[] Directories;
        internal FileSnapshot[] Files;
        internal long TotalBytes;

        internal bool SameContent(ContentSnapshot other)
        {
            if (Directories.Length != other.Directories.Length || Files.Length != other.Files.Length)
                return false;
            for (int index = 0; index < Directories.Length; index++)
                if (!String.Equals(Directories[index], other.Directories[index], StringComparison.Ordinal))
                    return false;
            for (int index = 0; index < Files.Length; index++)
            {
                FileSnapshot left = Files[index], right = other.Files[index];
                if (!String.Equals(left.RelativePath, right.RelativePath, StringComparison.Ordinal) ||
                    left.Length != right.Length || !String.Equals(left.Digest, right.Digest, StringComparison.Ordinal))
                    return false;
            }
            return true;
        }
    }

    // Every deletion operates on the same handles that were hashed. A path replacement
    // between validation and deletion therefore cannot redirect deletion to new content.
    internal sealed class LockedNode : IDisposable
    {
        internal string Path;
        internal bool Directory;
        internal SafeFileHandle Handle;
        internal FileStream Stream;
        internal bool Closed;
        public void Dispose()
        {
            if (Closed) return;
            Closed = true;
            // Mono's SafeFileHandle FileStream constructor does not consistently take
            // ownership of the supplied handle. Explicitly release both owners before
            // deleting the parent directory; disposing a SafeHandle twice is harmless.
            try { if (Stream != null) Stream.Dispose(); }
            finally { if (Handle != null) Handle.Dispose(); }
        }
    }

    internal sealed class LockedContent : IDisposable
    {
        internal readonly List<LockedNode> Nodes = new List<LockedNode>();
        internal ContentSnapshot Snapshot;

        public void Dispose()
        {
            for (int index = Nodes.Count - 1; index >= 0; index--) Nodes[index].Dispose();
        }

        internal void Delete()
        {
            // Cancellation is observed before this operation, never halfway through a
            // directory. A failure can leave a partially removed candidate and is reported.
            foreach (LockedNode node in Nodes.Where(value => !value.Directory)) DeleteNode(node);
            foreach (LockedNode node in Nodes.Where(value => value.Directory).Reverse()) DeleteNode(node);
        }

        private static void DeleteNode(LockedNode node)
        {
            Native.FileDispositionInfo disposition = new Native.FileDispositionInfo { DeleteFile = true };
            if (!Native.SetFileInformationByHandle(node.Handle, 4, ref disposition,
                (uint)Marshal.SizeOf(typeof(Native.FileDispositionInfo))))
                throw new IOException("Could not delete '" + node.Path + "': " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
            node.Dispose();
        }
    }

    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct ByHandleFileInformation
        {
            internal uint FileAttributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            internal uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct FileDispositionInfo
        {
            [MarshalAs(UnmanagedType.Bool)] internal bool DeleteFile;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(string name, uint access, FileShare share,
            IntPtr security, uint creationDisposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation information);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass,
            ref FileDispositionInfo information, uint bufferSize);
    }

    internal static class NativeSha256
    {
        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int BCryptOpenAlgorithmProvider(out IntPtr algorithm, string algorithmId,
            string implementation, uint flags);

        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int BCryptGetProperty(IntPtr handle, string property, [Out] byte[] output,
            uint outputLength, out uint resultLength, uint flags);

        [DllImport("bcrypt.dll", ExactSpelling = true)]
        private static extern int BCryptCreateHash(IntPtr algorithm, out IntPtr hash, IntPtr hashObject,
            uint hashObjectLength, IntPtr secret, uint secretLength, uint flags);

        [DllImport("bcrypt.dll", ExactSpelling = true)]
        private static extern int BCryptHashData(IntPtr hash, [In] byte[] input, uint inputLength, uint flags);

        [DllImport("bcrypt.dll", ExactSpelling = true)]
        private static extern int BCryptFinishHash(IntPtr hash, [Out] byte[] output, uint outputLength, uint flags);

        [DllImport("bcrypt.dll", ExactSpelling = true)]
        private static extern int BCryptDestroyHash(IntPtr hash);

        [DllImport("bcrypt.dll", ExactSpelling = true)]
        private static extern int BCryptCloseAlgorithmProvider(IntPtr algorithm, uint flags);

        internal static string Compute(Stream stream, CancellationToken token)
        {
            IntPtr algorithm = IntPtr.Zero, hash = IntPtr.Zero, hashObject = IntPtr.Zero;
            try
            {
                token.ThrowIfCancellationRequested();
                Check(BCryptOpenAlgorithmProvider(out algorithm, "SHA256", null, 0), "BCryptOpenAlgorithmProvider");
                byte[] property = new byte[4];
                uint returned;
                Check(BCryptGetProperty(algorithm, "ObjectLength", property, 4, out returned, 0), "BCryptGetProperty");
                uint objectLength = BitConverter.ToUInt32(property, 0);
                if (returned != 4 || objectLength == 0 || objectLength > 1048576)
                    throw new IOException("BCrypt returned an invalid SHA256 object length.");
                // The hash object must stay at a stable address across BCryptHashData
                // calls. Unmanaged storage avoids retaining a pinned managed array.
                hashObject = Marshal.AllocHGlobal((int)objectLength);
                Check(BCryptCreateHash(algorithm, out hash, hashObject, objectLength, IntPtr.Zero, 0, 0), "BCryptCreateHash");
                byte[] buffer = new byte[65536];
                int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    Check(BCryptHashData(hash, buffer, (uint)count, 0), "BCryptHashData");
                }
                token.ThrowIfCancellationRequested();
                byte[] digest = new byte[32];
                Check(BCryptFinishHash(hash, digest, (uint)digest.Length, 0), "BCryptFinishHash");
                return BitConverter.ToString(digest).Replace("-", "");
            }
            finally
            {
                // Perform every release even if a preceding native cleanup fails.
                int destroyStatus = hash == IntPtr.Zero ? 0 : BCryptDestroyHash(hash);
                if (hashObject != IntPtr.Zero) Marshal.FreeHGlobal(hashObject);
                int closeStatus = algorithm == IntPtr.Zero ? 0 : BCryptCloseAlgorithmProvider(algorithm, 0);
                Check(destroyStatus, "BCryptDestroyHash");
                Check(closeStatus, "BCryptCloseAlgorithmProvider");
            }
        }

        private static void Check(int status, string operation)
        {
            if (status != 0) throw new IOException(operation + " failed (NTSTATUS 0x" + status.ToString("X8") + ").");
        }
    }

    public static class DuplicateLibrary
    {
        private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

        public static ScanResult Scan(string root, IEnumerable<SongEntry> entries, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            List<string> errors = new List<string>();
            List<DuplicateGroup> result = new List<DuplicateGroup>();
            int differentContentGroups = 0;
            List<LockedNode> rootGuards = new List<LockedNode>();
            try
            {
                root = NormalizeRoot(root);
                LockRoot(root, rootGuards, token);
                if (entries == null) throw new ArgumentNullException("entries");
                Dictionary<string, List<SongEntry>> byHash = new Dictionary<string, List<SongEntry>>(StringComparer.OrdinalIgnoreCase);
                HashSet<string> seen = new HashSet<string>(PathComparer);
                foreach (SongEntry entry in entries.ToArray())
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if (entry == null) throw new IOException("Song entry is null.");
                        string path = ChildPath(root, entry.Path);
                        if (String.IsNullOrWhiteSpace(entry.Hash)) throw new IOException("SongCore hash is missing.");
                        CheckAttributes(path, true);
                        if (!seen.Add(path)) continue;
                        SongEntry copy = new SongEntry(path, entry.LevelId, entry.Hash, entry.Title);
                        List<SongEntry> group;
                        if (!byHash.TryGetValue(copy.Hash, out group)) byHash.Add(copy.Hash, group = new List<SongEntry>());
                        group.Add(copy);
                    }
                    catch (Exception error)
                    {
                        if (!IsFileError(error)) throw;
                        errors.Add((entry == null ? "<entry>" : entry.Path) + ": " + error.Message);
                    }
                }
                foreach (List<SongEntry> hashGroup in byHash.Values.OrderBy(value => value[0].Hash, StringComparer.OrdinalIgnoreCase))
                {
                    token.ThrowIfCancellationRequested();
                    if (hashGroup.Count < 2) continue;
                    List<List<KeyValuePair<SongEntry, ContentSnapshot>>> contentGroups = new List<List<KeyValuePair<SongEntry, ContentSnapshot>>>();
                    foreach (SongEntry entry in hashGroup.OrderBy(value => value.Path, PathComparer).ThenBy(value => value.Path, StringComparer.Ordinal))
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            ContentSnapshot snapshot;
                            using (LockedContent content = Capture(entry.Path, false, token)) snapshot = content.Snapshot;
                            List<KeyValuePair<SongEntry, ContentSnapshot>> contentGroup = contentGroups.FirstOrDefault(value => value[0].Value.SameContent(snapshot));
                            if (contentGroup == null) contentGroups.Add(contentGroup = new List<KeyValuePair<SongEntry, ContentSnapshot>>());
                            contentGroup.Add(new KeyValuePair<SongEntry, ContentSnapshot>(entry, snapshot));
                        }
                        catch (Exception error)
                        {
                            if (!IsFileError(error)) throw;
                            errors.Add(entry.Path + ": " + error.Message);
                        }
                    }
                    if (contentGroups.Count > 1) differentContentGroups++;
                    foreach (List<KeyValuePair<SongEntry, ContentSnapshot>> contentGroup in contentGroups)
                    {
                        if (contentGroup.Count < 2) continue;
                        var ordered = contentGroup.OrderByDescending(value => String.Equals(value.Key.LevelId,
                            "custom_level_" + value.Key.Hash, StringComparison.OrdinalIgnoreCase))
                            .ThenBy(value => value.Key.Path, PathComparer).ThenBy(value => value.Key.Path, StringComparer.Ordinal).ToArray();
                        result.Add(new DuplicateGroup(root, ordered[0].Key, ordered[0].Value,
                            ordered.Skip(1).Select(value => value.Key).ToArray(), ordered.Skip(1).Select(value => value.Value).ToArray()));
                    }
                }
            }
            catch (Exception error)
            {
                if (!IsFileError(error)) throw;
                errors.Add((root ?? "<root>") + ": " + error.Message);
            }
            finally { DisposeGuards(rootGuards); }
            token.ThrowIfCancellationRequested();
            return new ScanResult(result, errors, differentContentGroups);
        }

        public static DeleteResult Delete(string root, IEnumerable<DuplicateGroup> groups, CancellationToken token)
        {
            List<string> deleted = new List<string>();
            List<string> errors = new List<string>();
            List<LockedNode> rootGuards = new List<LockedNode>();
            try
            {
                token.ThrowIfCancellationRequested();
                root = NormalizeRoot(root);
                LockRoot(root, rootGuards, token);
                if (groups == null) throw new ArgumentNullException("groups");
                foreach (DuplicateGroup group in groups.ToArray())
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if (group == null || !PathComparer.Equals(root, group.Root)) throw new IOException("This plan belongs to a different library root.");
                        string keepPath = ChildPath(root, group.Keep.Path);
                        List<int> pending = new List<int>();
                        for (int index = 0; index < group.Remove.Count; index++)
                        {
                            string path = ChildPath(root, group.Remove[index].Path);
                            if (PathComparer.Equals(keepPath, path)) throw new IOException("The keeper cannot also be a removal candidate.");
                            if (Exists(path)) pending.Add(index);
                        }
                        if (pending.Count == 0) continue;
                        using (LockedContent keeper = Capture(keepPath, false, token))
                        {
                            if (!keeper.Snapshot.SameContent(group.KeepSnapshot)) throw new IOException("The retained copy changed since the scan; scan again.");
                            foreach (int index in pending)
                            {
                                token.ThrowIfCancellationRequested();
                                string path = group.Remove[index].Path;
                                try
                                {
                                    if (!Exists(path)) continue;
                                    using (LockedContent candidate = Capture(path, true, token))
                                    {
                                        if (!candidate.Snapshot.SameContent(group.RemoveSnapshots[index]) ||
                                            !candidate.Snapshot.SameContent(keeper.Snapshot))
                                            throw new IOException("The removal candidate changed since the scan; scan again.");
                                        token.ThrowIfCancellationRequested();
                                        candidate.Delete();
                                    }
                                    if (Exists(path)) throw new IOException("The directory still exists after removal.");
                                    deleted.Add(path);
                                }
                                catch (Exception error)
                                {
                                    if (!IsFileError(error)) throw;
                                    errors.Add(path + ": " + error.Message);
                                }
                            }
                        }
                    }
                    catch (Exception error)
                    {
                        if (!IsFileError(error)) throw;
                        errors.Add((group == null ? "<group>" : group.Keep.Path) + ": " + error.Message);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (!IsFileError(error)) throw;
                errors.Add((root ?? "<root>") + ": " + error.Message);
            }
            finally { DisposeGuards(rootGuards); }
            return new DeleteResult(deleted, errors, token.IsCancellationRequested);
        }

        private static string NormalizeRoot(string root)
        {
            if (String.IsNullOrWhiteSpace(root)) throw new IOException("Library root is missing.");
            return TrimSeparators(System.IO.Path.GetFullPath(root));
        }

        private static string TrimSeparators(string path)
        {
            string volume = System.IO.Path.GetPathRoot(path);
            return path.Length == volume.Length ? path : path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        }

        private static string ChildPath(string root, string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new IOException("Song directory is missing.");
            string full = TrimSeparators(System.IO.Path.GetFullPath(path));
            string parent = System.IO.Path.GetDirectoryName(full);
            if (parent == null || !PathComparer.Equals(TrimSeparators(parent), root))
                throw new IOException("Only direct child directories of the custom-song root may be processed.");
            return full;
        }

        private static void LockRoot(string root, IList<LockedNode> guards, CancellationToken token)
        {
            List<string> parents = new List<string>();
            for (DirectoryInfo directory = new DirectoryInfo(root); directory != null; directory = directory.Parent) parents.Add(directory.FullName);
            parents.Reverse();
            foreach (string path in parents)
            {
                token.ThrowIfCancellationRequested();
                guards.Add(Open(path, true, false));
            }
        }

        private static void DisposeGuards(IList<LockedNode> guards)
        {
            for (int index = guards.Count - 1; index >= 0; index--) guards[index].Dispose();
        }

        private static bool Exists(string path)
        {
            try { File.GetAttributes(path); return true; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }

        private static void CheckAttributes(string path, bool directory)
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse points are not allowed.");
            if (((attributes & FileAttributes.Directory) != 0) != directory) throw new IOException("Unexpected file or directory type.");
        }

        private static LockedNode Open(string path, bool directory, bool delete)
        {
            uint access = directory ? 0x80U : 0x80000000U;
            if (delete) access |= 0x00010000U;
            SafeFileHandle handle = Native.CreateFile(path, access,
                directory ? FileShare.Read | FileShare.Write : FileShare.Read,
                IntPtr.Zero, 3, 0x00200000U | (directory ? 0x02000000U : 0U), IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new IOException("Cannot lock '" + path + "': " + new Win32Exception(error).Message);
            }
            try
            {
                Native.ByHandleFileInformation info;
                if (!Native.GetFileInformationByHandle(handle, out info)) throw new IOException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
                if ((info.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse points are not allowed: " + path);
                if (((info.FileAttributes & (uint)FileAttributes.Directory) != 0) != directory) throw new IOException("Unexpected file or directory type: " + path);
                LockedNode node = new LockedNode { Path = path, Directory = directory, Handle = handle };
                if (!directory) node.Stream = new FileStream(handle, FileAccess.Read);
                return node;
            }
            catch { handle.Dispose(); throw; }
        }

        private static LockedContent Capture(string path, bool delete, CancellationToken token)
        {
            LockedContent result = new LockedContent();
            try
            {
                List<string> directories = new List<string>();
                List<FileSnapshot> files = new List<FileSnapshot>();
                CaptureDirectory(path, path, delete, result, directories, files, token);
                files.Sort((left, right) => StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath));
                directories.Sort(StringComparer.Ordinal);
                result.Snapshot = new ContentSnapshot { Path = path, Directories = directories.ToArray(), Files = files.ToArray(), TotalBytes = files.Sum(value => value.Length) };
                return result;
            }
            catch { result.Dispose(); throw; }
        }

        private static void CaptureDirectory(string root, string path, bool delete, LockedContent content,
            IList<string> directories, IList<FileSnapshot> files, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            content.Nodes.Add(Open(path, true, delete));
            if (!PathComparer.Equals(root, path)) directories.Add(path.Substring(root.Length + 1));
            foreach (string child in Directory.GetDirectories(path).OrderBy(value => value, StringComparer.Ordinal))
                CaptureDirectory(root, child, delete, content, directories, files, token);
            foreach (string file in Directory.GetFiles(path).OrderBy(value => value, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                LockedNode node = Open(file, false, delete);
                content.Nodes.Add(node);
                string digest = NativeSha256.Compute(node.Stream, token);
                files.Add(new FileSnapshot { RelativePath = file.Substring(root.Length + 1), Length = node.Stream.Length, Digest = digest });
            }
        }

        private static bool IsFileError(Exception error)
        {
            return error is IOException || error is UnauthorizedAccessException || error is ArgumentException ||
                error is NotSupportedException || error is System.Security.SecurityException;
        }
    }
}
