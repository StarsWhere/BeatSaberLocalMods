using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace UnityShutdownFix
{
    internal static class NativeGuard
    {
        internal const string ExpectedHash = "826f0d671c0d3d7c852c32051a6ebd6a3d715d27e5323a5e34dbb33eef407fdd";
        private static bool installed;
        private static IntPtr retainedIsland;
        internal static bool Installed { get { return installed; } }
        internal static string FailureReason { get; private set; }
        internal static IntPtr Island { get { return retainedIsland; } }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);
        internal static bool Install(string gameRoot, out string detail)
        {
            if (installed) { detail = "Native shutdown guard is already installed."; return true; }
            IntPtr island = IntPtr.Zero, entry = IntPtr.Zero;
            uint oldProtection = 0;
            bool pageChanged = false, entryWritten = false, rollbackSafe = true;
            try
            {
                if (IntPtr.Size != 8) throw new NotSupportedException("This guard requires a Windows x64 process.");
                if (File.ReadAllText(Path.Combine(gameRoot, "BeatSaberVersion.txt")).Trim() != "1.45.2_28848")
                    throw new NotSupportedException("The game build is not Beat Saber 1.45.2_28848.");
                string path = Path.Combine(gameRoot, "UnityPlayer.dll");
                using (SHA256 hash = SHA256.Create())
                using (FileStream file = File.OpenRead(path))
                    if (BitConverter.ToString(hash.ComputeHash(file)).Replace("-", "").ToLowerInvariant() != ExpectedHash)
                        throw new NotSupportedException("UnityPlayer.dll SHA256 does not match the verified build.");
                if (FileVersionInfo.GetVersionInfo(path).ProductVersion != "6000.3.19f1 (7689f4515d75)")
                    throw new NotSupportedException("The Unity player product version does not match the verified build.");
                IntPtr module = GetModuleHandle("UnityPlayer.dll");
                if (module == IntPtr.Zero) throw new InvalidOperationException("UnityPlayer.dll is not loaded.");
                int pe = Marshal.ReadInt32(module, 0x3C);
                if (Marshal.ReadInt16(module) != 0x5A4D || Marshal.ReadInt32(module, pe) != 0x4550 ||
                    Marshal.ReadInt32(module, pe + 8) != 0x6A3B523A || Marshal.ReadInt32(module, pe + 24 + 56) != 0x2436000)
                    throw new NotSupportedException("The loaded Unity player image does not match its verified PE headers.");
                byte[] context = new byte[GuardCode.ContextLength];
                Marshal.Copy(IntPtr.Add(module, GuardCode.ContextRva), context, 0, context.Length);
                if (!GuardCode.Matches(context)) throw new NotSupportedException("The loaded shutdown instructions differ from the verified context.");
                entry = IntPtr.Add(module, GuardCode.PatchRva);
                island = NativeMemory.AllocateNear(entry, 4096);
                byte[] code = GuardCode.BuildIsland(IntPtr.Add(module, GuardCode.ContinueRva), IntPtr.Add(module, GuardCode.MissingWindowRva));
                Marshal.Copy(code, 0, island, code.Length);
                NativeMemory.Protect(island, 4096, NativeMemory.ExecuteRead);
                NativeMemory.Flush(island, code.Length);
                byte[] patch = GuardCode.BuildEntry(entry, island);
                // The target is a shutdown-only path; installation happens once
                // on the game thread at startup, before this cleanup can run.
                oldProtection = NativeMemory.Protect(entry, patch.Length, NativeMemory.ExecuteReadWrite);
                pageChanged = true;
                Marshal.Copy(patch, 0, entry, patch.Length);
                entryWritten = true;
                NativeMemory.Flush(entry, patch.Length);
                NativeMemory.Protect(entry, patch.Length, oldProtection);
                pageChanged = false;
                installed = true;
                FailureReason = string.Empty;
                retainedIsland = island;
                // Deliberately process-owned: Unity calls RuntimeCleanup after
                // managed shutdown. OnExit must neither restore nor free this
                // entirely native island. The OS reclaims it at process exit.
                detail = "Installed the verified native accessibility shutdown null guard.";
                return true;
            }
            catch (Exception error)
            {
                string recovery = string.Empty;
                try
                {
                    if (entryWritten)
                    {
                        byte[] original = GuardCode.OriginalInstruction;
                        Marshal.Copy(original, 0, entry, original.Length);
                        NativeMemory.Flush(entry, original.Length);
                    }
                    if (pageChanged) NativeMemory.Protect(entry, 7, oldProtection);
                }
                catch (Exception rollback)
                {
                    rollbackSafe = false;
                    retainedIsland = island;
                    recovery = " Rollback could not be confirmed; native island retained to avoid a dangling jump. " + rollback.Message;
                }
                if (rollbackSafe && island != IntPtr.Zero)
                {
                    try { NativeMemory.Free(island); }
                    catch (Exception release) { recovery += " Unused native island release failed: " + release.Message; }
                }
                detail = "Native shutdown guard was not installed: " + error.Message + recovery;
                FailureReason = detail;
                return false;
            }
        }
    }
}
