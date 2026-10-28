using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityShutdownFix;

internal static class GuardTests
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void Probe(IntPtr input, IntPtr output);
    private static readonly long[] sentinels =
    {
        0x11111111,0x88888888,0x99999999,0x10101010,0x11111112,
        0x22222222,0x33333333,0x44444444,0x55555555,0x66666666,0x77777777,0x12121212,0x13131313
    };
    private static int Main()
    {
        if (IntPtr.Size != 8) throw new InvalidOperationException("Native guard tests require x64.");
        IntPtr memory = NativeMemory.Allocate(4096), data = NativeMemory.Allocate(4096), near = IntPtr.Zero;
        try
        {
            near = NativeMemory.AllocateNear(memory, 4096);
            GuardCode.BuildEntry(memory, near);
            byte[] entry = GuardCode.BuildEntry(IntPtr.Add(memory, 0x800), IntPtr.Add(memory, 0x600));
            Check(entry.Length == 7 && entry[0] == 0xE9 && entry[5] == 0x90 && entry[6] == 0x90, "relative entry shape");
            bool rejected = false;
            try { GuardCode.BuildEntry(new IntPtr(0x100000000L), new IntPtr(0x200000000L)); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected, "entry rejects an unreachable island");
            Check(!GuardCode.Matches(new byte[GuardCode.ContextLength]), "unverified native context is refused");
            foreach (uint flags in new uint[] { 0x202,0x247,0x282 })
            {
                byte[] wrapper = Wrapper(IntPtr.Add(memory, 0x800), flags);
                byte[] valid = Continuation(1), missing = Continuation(0);
                byte[] island = GuardCode.BuildIsland(IntPtr.Add(memory, 0x200), IntPtr.Add(memory, 0x400));
                NativeMemory.Protect(memory, 4096, NativeMemory.ReadWrite);
                Write(memory, wrapper);
                Write(IntPtr.Add(memory, 0x200), valid);
                Write(IntPtr.Add(memory, 0x400), missing);
                Write(IntPtr.Add(memory, 0x600), island);
                Write(IntPtr.Add(memory, 0x800), entry);
                NativeMemory.Protect(memory, 4096, NativeMemory.ExecuteRead);
                NativeMemory.Flush(memory, 4096);
                Probe run = (Probe)Marshal.GetDelegateForFunctionPointer(memory, typeof(Probe));
                long window = 0x123456789ABCDEFL;
                Marshal.WriteInt64(data, 0xE0, window);
                IntPtr output = IntPtr.Add(data, 0x200);
                foreach (bool present in new bool[] { false, true })
                {
                    run(present ? data : IntPtr.Zero, output);
                    Check(Marshal.ReadInt64(output, 16) == (present ? window : 0L), "original RCX window load / null continuation");
                    Check(Marshal.ReadInt32(output, 88) == (present ? 1 : 0), "selected native continuation");
                    Check(Marshal.ReadInt64(output, 0) == Marshal.ReadInt64(output, 24), "stack pointer preserved");
                    Check(Marshal.ReadInt64(output, 8) == Marshal.ReadInt64(output, 32), "complete flags preserved");
                    Check((Marshal.ReadInt64(output, 8) & 0x8D5) == (flags & 0x8D5), "flag canary established");
                    Check(Marshal.ReadInt64(output, 80) == output.ToInt64(), "RDX preserved");
                    int[] offsets = {40,48,56,64,72,96,104,112,120,128,136,144,152};
                    for (int i = 0; i < offsets.Length; i++)
                        Check(Marshal.ReadInt64(output, offsets[i]) == sentinels[i], "general register " + i + " preserved");
                }
                GC.KeepAlive(run);
            }
            Console.WriteLine("Unity shutdown native guard: PASS (null/non-null, flags, stack, all other general registers, relative island allocation).");
            return 0;
        }
        finally
        {
            NativeMemory.Free(near);
            NativeMemory.Free(data);
            NativeMemory.Free(memory);
        }
    }
    private static void Write(IntPtr address, byte[] value) { Marshal.Copy(value, 0, address, value.Length); }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Imm64(List<byte> code, byte prefix, byte instruction, long value)
    {
        code.Add(prefix); code.Add(instruction); code.AddRange(BitConverter.GetBytes(value));
    }
    private static void Jump(List<byte> code, IntPtr destination)
    {
        code.AddRange(new byte[] {0xFF,0x25,0,0,0,0}); code.AddRange(BitConverter.GetBytes(destination.ToInt64()));
    }
    private static byte[] Wrapper(IntPtr entry, uint flags)
    {
        List<byte> c = new List<byte> {0x53,0x55,0x56,0x57,0x41,0x54,0x41,0x55,0x41,0x56,0x41,0x57};
        Imm64(c,0x48,0xB8,sentinels[0]);
        Imm64(c,0x49,0xB8,sentinels[1]); Imm64(c,0x49,0xB9,sentinels[2]);
        Imm64(c,0x49,0xBA,sentinels[3]); Imm64(c,0x49,0xBB,sentinels[4]);
        Imm64(c,0x48,0xBB,sentinels[5]); Imm64(c,0x48,0xBD,sentinels[6]);
        Imm64(c,0x48,0xBE,sentinels[7]); Imm64(c,0x48,0xBF,sentinels[8]);
        Imm64(c,0x49,0xBC,sentinels[9]); Imm64(c,0x49,0xBD,sentinels[10]);
        Imm64(c,0x49,0xBE,sentinels[11]); Imm64(c,0x49,0xBF,sentinels[12]);
        c.AddRange(new byte[] {0x48,0x89,0x22,0x68}); c.AddRange(BitConverter.GetBytes(flags));
        c.AddRange(new byte[] {0x9D,0x9C,0x8F,0x42,0x08});
        Jump(c,entry);
        return c.ToArray();
    }
    private static byte[] Continuation(uint branch)
    {
        List<byte> c = new List<byte>
        {
            0x48,0x89,0x4A,0x10, 0x48,0x89,0x62,0x18, 0x9C,0x8F,0x42,0x20,
            0x48,0x89,0x42,0x28, 0x4C,0x89,0x42,0x30, 0x4C,0x89,0x4A,0x38,
            0x4C,0x89,0x52,0x40, 0x4C,0x89,0x5A,0x48, 0x48,0x89,0x52,0x50,
            0xC7,0x42,0x58
        };
        c.AddRange(BitConverter.GetBytes(branch));
        byte[] prefixes = {0x48,0x48,0x48,0x48,0x4C,0x4C,0x4C,0x4C};
        byte[] registers = {0x9A,0xAA,0xB2,0xBA,0xA2,0xAA,0xB2,0xBA};
        for (int i = 0; i < 8; i++)
        {
            c.Add(prefixes[i]); c.Add(0x89); c.Add(registers[i]); c.AddRange(BitConverter.GetBytes(96 + i * 8));
        }
        c.AddRange(new byte[] {0x41,0x5F,0x41,0x5E,0x41,0x5D,0x41,0x5C,0x5F,0x5E,0x5D,0x5B,0xC3});
        return c.ToArray();
    }
}
