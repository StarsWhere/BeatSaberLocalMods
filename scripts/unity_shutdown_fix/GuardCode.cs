using System;
using System.Collections.Generic;

namespace UnityShutdownFix
{
    internal static class GuardCode
    {
        internal const int ContextRva = 0xB1D772;
        internal const int PatchRva = 0xB1D781;
        internal const int ContinueRva = 0xB1D788;
        internal const int MissingWindowRva = 0xB1D78E;
        private static readonly byte[] context =
        {
            0x48,0x8B,0x0D,0x37,0x01,0x72,0x01, 0x45,0x33,0xC9, 0x45,0x33,0xC0, 0x33,0xD2,
            0x48,0x8B,0x89,0xE0,0x00,0x00,0x00, 0xFF,0x15,0x6A,0xA2,0x12,0x01,
            0x33,0xED, 0x48,0x39,0x6E,0x18
        };
        internal static int ContextLength { get { return context.Length; } }
        internal static byte[] OriginalInstruction { get { return new byte[] {0x48,0x8B,0x89,0xE0,0,0,0}; } }
        internal static bool Matches(byte[] value)
        {
            if (value == null || value.Length != context.Length) return false;
            for (int i = 0; i < value.Length; i++) if (value[i] != context[i]) return false;
            return true;
        }
        internal static byte[] BuildIsland(IntPtr normal, IntPtr missing)
        {
            // No managed callbacks can survive this late RuntimeCleanup call.
            // Preserve flags and stack on both paths, and every register except
            // the RCX window-handle load that the original instruction owns.
            List<byte> bytes = new List<byte> { 0x9C, 0x48,0x85,0xC9, 0x74,0x16, 0x9D };
            bytes.AddRange(OriginalInstruction);
            AbsoluteJump(bytes, normal);
            bytes.Add(0x9D);
            AbsoluteJump(bytes, missing);
            return bytes.ToArray();
        }
        private static void AbsoluteJump(List<byte> bytes, IntPtr target)
        {
            bytes.AddRange(new byte[] {0xFF,0x25,0,0,0,0});
            bytes.AddRange(BitConverter.GetBytes(target.ToInt64()));
        }
        internal static byte[] BuildEntry(IntPtr entry, IntPtr island)
        {
            long displacement = island.ToInt64() - (entry.ToInt64() + 5L);
            if (displacement < int.MinValue || displacement > int.MaxValue)
                throw new ArgumentOutOfRangeException("island", "Native island is outside the relative jump range.");
            byte[] result = new byte[] { 0xE9,0,0,0,0,0x90,0x90 };
            Array.Copy(BitConverter.GetBytes((int)displacement), 0, result, 1, 4);
            return result;
        }
    }
}
