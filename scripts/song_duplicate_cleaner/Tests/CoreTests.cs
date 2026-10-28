using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using SongDuplicateCleaner;

internal static class CoreTests
{
    private static string testRoot;
    private static int passed;

    private static int Main(string[] args)
    {
        if (args.Length != 1) { Console.Error.WriteLine("Expected the temporary test directory."); return 2; }
        string basePath = Path.GetFullPath(args[0]).TrimEnd(Path.DirectorySeparatorChar);
        if (!basePath.EndsWith(Path.Combine(".tmp", "song-duplicate-cleaner", "tests"), StringComparison.OrdinalIgnoreCase))
        { Console.Error.WriteLine("Tests must run under .tmp/song-duplicate-cleaner/tests."); return 2; }
        testRoot = Path.Combine(basePath, "cases-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        try
        {
            Run("identical files, canonical keeper, immutable plan and idempotence", ExactDuplicates);
            Run("same SongCore hash with different assets", DifferentAssets);
            Run("deterministic fallback and independent SongCore hashes", DeterministicFallback);
            Run("root, outside and nested paths rejected", PathBoundaries);
            Run("directory junctions rejected at every boundary", ReparsePoints);
            Run("candidate content and structure changes rejected", ChangedCandidates);
            Run("keeper mutation or removal preserves candidates", ChangedKeepers);
            Run("locked candidate failure continues other candidates", PartialFailure);
            Run("read-only deletion failure is reported truthfully", ReadOnlyFailure);
            Run("scan and delete cancellation", PreCancelled);
            Run("cancellation reports actual partial results", PartialCancellation);
            Console.WriteLine("PASS: " + passed + " filesystem regression tests.");
            RemoveTree(testRoot);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine("Failed fixture retained at: " + testRoot);
            return 1;
        }
    }

    private static void Run(string name, Action test)
    {
        test();
        passed++;
        Console.WriteLine("PASS " + name);
    }

    private static string Library(string name)
    {
        string root = Path.Combine(testRoot, name, "CustomLevels");
        Directory.CreateDirectory(root);
        return root;
    }

    private static SongEntry Song(string root, string name, string hash, bool canonical, string audio)
    {
        string path = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(path, "assets"));
        File.WriteAllText(Path.Combine(path, "Info.dat"), "{\"song\":\"test\"}");
        File.WriteAllText(Path.Combine(path, "Expert.dat"), "{\"notes\":[]}");
        File.WriteAllText(Path.Combine(path, "assets", "audio.ogg"), audio);
        return new SongEntry(path, "custom_level_" + hash + (canonical ? "" : "_" + name), hash, "Test " + hash);
    }

    private static ScanResult Scan(string root, params SongEntry[] entries)
    {
        ScanResult result = DuplicateLibrary.Scan(root, entries, CancellationToken.None);
        Assert(result.Errors.Count == 0, String.Join(Environment.NewLine, result.Errors));
        return result;
    }

    private static void ExactDuplicates()
    {
        using (MemoryStream empty = new MemoryStream())
            Assert(NativeSha256.Compute(empty, CancellationToken.None) == "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855", "Native SHA256 must match the empty input test vector.");
        using (MemoryStream abc = new MemoryStream(new byte[] { 97, 98, 99 }))
            Assert(NativeSha256.Compute(abc, CancellationToken.None) == "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", "Native SHA256 must match the abc test vector.");
        byte[] multipleBlocks = new byte[131075];
        new Random(415).NextBytes(multipleBlocks);
        string expected;
        using (System.Security.Cryptography.SHA256 managed = System.Security.Cryptography.SHA256.Create())
            expected = BitConverter.ToString(managed.ComputeHash(multipleBlocks)).Replace("-", "");
        using (MemoryStream data = new MemoryStream(multipleBlocks))
            Assert(NativeSha256.Compute(data, CancellationToken.None) == expected, "Native SHA256 must match the reference across multiple blocks and a final short block.");
        string root = Library("exact");
        SongEntry a = Song(root, "a-copy", "A", false, "audio"), z = Song(root, "z-original", "A", true, "audio");
        SongEntry b = Song(root, "b-copy", "A", false, "audio");
        List<SongEntry> entries = new List<SongEntry> { a, z, b, a };
        ScanResult scan = DuplicateLibrary.Scan(root, entries, CancellationToken.None);
        entries.Clear();
        Assert(scan.Errors.Count == 0 && scan.Groups.Count == 1, "Expected one content group.");
        DuplicateGroup group = scan.Groups[0];
        Assert(group.Keep.Path == z.Path, "Canonical level ID must win over alphabetical directory order.");
        Assert(group.Remove.Count == 2 && group.Remove[0].Path == a.Path && group.Remove[1].Path == b.Path, "Candidates must be unique and ordered.");
        long size = Directory.GetFiles(a.Path, "*", SearchOption.AllDirectories).Sum(value => new FileInfo(value).Length);
        Assert(group.ReclaimBytes == 2 * size, "Reclaimable bytes must reflect all candidate files.");
        bool readOnly = false;
        try { ((IList<SongEntry>)group.Remove).Clear(); } catch (NotSupportedException) { readOnly = true; }
        Assert(readOnly, "The plan must be immutable.");
        DeleteResult deleted = DuplicateLibrary.Delete(root, scan.Groups, CancellationToken.None);
        Assert(deleted.DeletedPaths.Count == 2 && deleted.Errors.Count == 0 && !deleted.Cancelled, "Both candidates must be deleted.");
        Assert(Directory.Exists(z.Path) && File.Exists(Path.Combine(z.Path, "assets", "audio.ogg")), "The complete keeper must survive.");
        DeleteResult again = DuplicateLibrary.Delete(root, scan.Groups, CancellationToken.None);
        Assert(again.DeletedPaths.Count == 0 && again.Errors.Count == 0, "Repeated execution must be idempotent.");
        RemoveTree(z.Path);
        again = DuplicateLibrary.Delete(root, scan.Groups, CancellationToken.None);
        Assert(again.DeletedPaths.Count == 0 && again.Errors.Count == 0, "A fully completed plan must remain idempotent if the keeper is later removed.");
    }

    private static void DifferentAssets()
    {
        string root = Library("different-assets");
        SongEntry a = Song(root, "a", "A", true, "first"), b = Song(root, "b", "A", false, "second");
        ScanResult scan = Scan(root, a, b);
        Assert(scan.Groups.Count == 0 && scan.DifferentContentGroups == 1, "Same SongCore hash with different audio must remain intact.");
        SongEntry c = Song(root, "c", "A", false, "first"), d = Song(root, "d", "A", false, "second");
        scan = Scan(root, a, b, c, d);
        Assert(scan.Groups.Count == 2 && scan.DifferentContentGroups == 1, "Only identical content subgroups may be consolidated.");
        DeleteResult deleted = DuplicateLibrary.Delete(root, scan.Groups, CancellationToken.None);
        Assert(deleted.Errors.Count == 0 && deleted.DeletedPaths.Count == 2 && Directory.Exists(a.Path) && Directory.Exists(b.Path), "Each distinct audio version must retain a copy.");
    }

    private static void DeterministicFallback()
    {
        string root = Library("fallback");
        SongEntry z = Song(root, "z", "A", false, "same"), a = Song(root, "a", "A", false, "same"), other = Song(root, "other", "B", true, "same");
        ScanResult scan = Scan(root, z, other, a);
        Assert(scan.Groups.Count == 1 && scan.Groups[0].Keep.Path == a.Path, "Fallback must use stable path order without combining different SongCore hashes.");
    }

    private static void PathBoundaries()
    {
        string root = Library("boundaries");
        SongEntry keep = Song(root, "keep", "A", true, "same"), copy = Song(root, "copy", "A", false, "same");
        SongEntry outside = Song(Path.GetDirectoryName(root), "outside", "A", false, "same");
        SongEntry nested = Song(keep.Path, "nested", "B", false, "same");
        SongEntry rootItself = new SongEntry(root, "custom_level_A", "A", "root");
        ScanResult scan = DuplicateLibrary.Scan(root, new[] { rootItself, nested, outside, keep, copy }, CancellationToken.None);
        Assert(scan.Errors.Count == 3, "Root, outside and nested input paths must be rejected.");
        Assert(Directory.Exists(outside.Path), "Outside content must be untouched.");
        // The nested data makes keep different from copy, which must also prevent consolidation.
        Assert(scan.Groups.Count == 0, "Full directory content must participate in identity.");
        string secondRoot = Library("other-root");
        SongEntry x = Song(secondRoot, "x", "C", true, "same"), y = Song(secondRoot, "y", "C", false, "same");
        ScanResult second = Scan(secondRoot, x, y);
        DeleteResult wrongRoot = DuplicateLibrary.Delete(root, second.Groups, CancellationToken.None);
        Assert(wrongRoot.Errors.Count == 1 && Directory.Exists(y.Path), "A plan is bound to its original root.");
    }

    private static void ReparsePoints()
    {
        string root = Library("links");
        SongEntry keep = Song(root, "keep", "A", true, "same"), copy = Song(root, "copy", "A", false, "same");
        string outside = Path.Combine(testRoot, "links", "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "precious.dat"), "do not touch");
        string top = Path.Combine(root, "linked");
        CreateJunction(top, outside);
        string nested = Path.Combine(copy.Path, "linked-assets");
        CreateJunction(nested, outside);
        string rootAlias = Path.Combine(testRoot, "links", "root-alias");
        CreateJunction(rootAlias, root);
        try
        {
            ScanResult scan = DuplicateLibrary.Scan(root, new[] { keep, copy, new SongEntry(top, "custom_level_A_link", "A", "link") }, CancellationToken.None);
            Assert(scan.Errors.Count == 2 && scan.Groups.Count == 0, "Top-level and nested junctions must both be rejected.");
            scan = DuplicateLibrary.Scan(rootAlias, new SongEntry[0], CancellationToken.None);
            Assert(scan.Errors.Count == 1, "A junction library root must be rejected.");
            string throughParent = Path.Combine(rootAlias, "keep");
            scan = DuplicateLibrary.Scan(throughParent, new SongEntry[0], CancellationToken.None);
            Assert(scan.Errors.Count == 1, "Junction ancestors must be rejected.");
            Assert(File.ReadAllText(Path.Combine(outside, "precious.dat")) == "do not touch", "Junction targets must be untouched.");
        }
        finally
        {
            Directory.Delete(top);
            Directory.Delete(nested);
            Directory.Delete(rootAlias);
        }
    }

    private static void ChangedCandidates()
    {
        string root = Library("changed-candidate");
        SongEntry keep = Song(root, "keep", "A", true, "same"), copy = Song(root, "copy", "A", false, "same");
        ScanResult scan = Scan(root, keep, copy);
        File.WriteAllText(Path.Combine(copy.Path, "assets", "audio.ogg"), "edit");
        DeleteResult deleted = DuplicateLibrary.Delete(root, scan.Groups, CancellationToken.None);
        Assert(deleted.Errors.Count == 1 && deleted.DeletedPaths.Count == 0 && Directory.Exists(copy.Path), "Same-length file mutation must invalidate the snapshot.");
        File.WriteAllText(Path.Combine(copy.Path, "assets", "audio.ogg"), "same");
        File.WriteAllText(Path.Combine(copy.Path, "new-file.bin"), "new");
        deleted = DuplicateLibrary.Delete(root, scan.Groups, CancellationToken.None);
        Assert(deleted.Errors.Count == 1 && Directory.Exists(copy.Path), "New files must not be deleted by an old plan.");
    }

    private static void ChangedKeepers()
    {
        string root = Library("changed-keeper");
        SongEntry keep = Song(root, "keep", "A", true, "same"), copy = Song(root, "copy", "A", false, "same");
        ScanResult scan = Scan(root, keep, copy);
        File.WriteAllText(Path.Combine(keep.Path, "assets", "audio.ogg"), "edit");
        DeleteResult deleted = DuplicateLibrary.Delete(root, scan.Groups, CancellationToken.None);
        Assert(deleted.Errors.Count == 1 && Directory.Exists(copy.Path), "Changed keeper must block its candidates.");
        RemoveTree(keep.Path);
        deleted = DuplicateLibrary.Delete(root, scan.Groups, CancellationToken.None);
        Assert(deleted.Errors.Count == 1 && deleted.DeletedPaths.Count == 0 && Directory.Exists(copy.Path), "Missing keeper must preserve the last complete copy.");
    }

    private static void PartialFailure()
    {
        string root = Library("partial-failure");
        SongEntry keep = Song(root, "z-keep", "A", true, "same"), locked = Song(root, "a-locked", "A", false, "same"), good = Song(root, "b-good", "A", false, "same");
        ScanResult scan = Scan(root, keep, locked, good);
        using (FileStream held = new FileStream(Path.Combine(locked.Path, "Info.dat"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            DeleteResult deleted = DuplicateLibrary.Delete(root, scan.Groups, CancellationToken.None);
            Assert(deleted.Errors.Count == 1 && deleted.DeletedPaths.Count == 1 && deleted.DeletedPaths[0] == good.Path,
                "One locked candidate must not prevent independent candidates from completing.");
            Assert(Directory.Exists(locked.Path) && Directory.Exists(keep.Path), "Failed candidate and keeper must survive.");
        }
        DeleteResult retried = DuplicateLibrary.Delete(root, scan.Groups, CancellationToken.None);
        Assert(retried.Errors.Count == 0 && retried.DeletedPaths.Count == 1 && retried.DeletedPaths[0] == locked.Path, "An unchanged failed candidate must be retryable.");
    }

    private static void ReadOnlyFailure()
    {
        string root = Library("read-only");
        SongEntry keep = Song(root, "z-keep", "A", true, "same"), bad = Song(root, "a-readonly", "A", false, "same"), good = Song(root, "b-good", "A", false, "same");
        ScanResult scan = Scan(root, keep, bad, good);
        string audio = Path.Combine(bad.Path, "assets", "audio.ogg");
        File.SetAttributes(audio, File.GetAttributes(audio) | FileAttributes.ReadOnly);
        try
        {
            DeleteResult deleted = DuplicateLibrary.Delete(root, scan.Groups, CancellationToken.None);
            Assert(deleted.Errors.Count == 1 && deleted.DeletedPaths.Count == 1 && deleted.DeletedPaths[0] == good.Path, "A partial filesystem failure must be reported while other candidates complete.");
            Assert(Directory.Exists(bad.Path) && File.Exists(Path.Combine(keep.Path, "Info.dat")) && File.Exists(Path.Combine(keep.Path, "assets", "audio.ogg")), "The retained copy must remain complete after deletion failure.");
        }
        finally { if (File.Exists(audio)) File.SetAttributes(audio, FileAttributes.Normal); }
    }

    private static void PreCancelled()
    {
        string root = Library("cancelled");
        SongEntry keep = Song(root, "keep", "A", true, "same"), copy = Song(root, "copy", "A", false, "same");
        ScanResult scan = Scan(root, keep, copy);
        using (CancellationTokenSource cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            bool thrown = false;
            try { DuplicateLibrary.Scan(root, new[] { keep, copy }, cancellation.Token); } catch (OperationCanceledException) { thrown = true; }
            Assert(thrown, "Cancelled scans must throw rather than present incomplete results as success.");
            DeleteResult deleted = DuplicateLibrary.Delete(root, scan.Groups, cancellation.Token);
            Assert(deleted.Cancelled && deleted.DeletedPaths.Count == 0 && deleted.Errors.Count == 0 && Directory.Exists(copy.Path), "Pre-cancelled deletion must preserve all candidates.");
        }
    }

    private static void PartialCancellation()
    {
        string root = Library("partial-cancel");
        SongEntry keep = Song(root, "z-keep", "A", true, "same"), first = Song(root, "a-copy", "A", false, "same"), second = Song(root, "b-copy", "A", false, "same");
        byte[] block = new byte[65536];
        new Random(103).NextBytes(block);
        foreach (SongEntry entry in new[] { keep, first, second })
            using (FileStream file = new FileStream(Path.Combine(entry.Path, "large.bin"), FileMode.Create, FileAccess.Write))
                for (int index = 0; index < 512; index++) file.Write(block, 0, block.Length);
        ScanResult scan = Scan(root, keep, first, second);
        using (CancellationTokenSource cancellation = new CancellationTokenSource())
        {
            bool finished = false;
            Thread observer = new Thread(() =>
            {
                while (!Volatile.Read(ref finished))
                {
                    if (!Directory.Exists(first.Path)) { cancellation.Cancel(); break; }
                    Thread.Sleep(1);
                }
            });
            observer.IsBackground = true;
            observer.Start();
            DeleteResult deleted;
            try { deleted = DuplicateLibrary.Delete(root, scan.Groups, cancellation.Token); }
            finally { Volatile.Write(ref finished, true); observer.Join(); }
            Assert(deleted.Cancelled && deleted.DeletedPaths.Count == 1 && deleted.DeletedPaths[0] == first.Path,
                "Cancellation must retain and report the already completed removal.");
            Assert(Directory.Exists(second.Path) && Directory.Exists(keep.Path), "Unstarted candidate and keeper must survive cancellation.");
        }
    }

    private static void CreateJunction(string link, string target)
    {
        ProcessStartInfo start = new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using (Process process = Process.Start(start))
        {
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert(process.ExitCode == 0, "Could not create test junction: " + output);
        }
    }

    private static void RemoveTree(string path)
    {
        string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (!String.Equals(full, testRoot, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(testRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing fixture cleanup outside the resolved test root: " + full);
        Directory.Delete(full, true);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
