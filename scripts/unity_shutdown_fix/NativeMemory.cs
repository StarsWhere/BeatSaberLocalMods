using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace UnityShutdownFix
{
    internal static class NativeMemory
    {
        internal const uint ReadWrite = 0x04, ExecuteRead = 0x20, ExecuteReadWrite = 0x40;
        [StructLayout(LayoutKind.Sequential)]
        private struct Region
        {
            internal IntPtr Base, AllocationBase;
            internal uint AllocationProtect;
            internal ushort PartitionId;
            internal UIntPtr Size;
            internal uint State, Protect, Type;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct SystemInfo
        {
            internal uint Architecture, PageSize;
            internal IntPtr Minimum, Maximum;
            internal UIntPtr ProcessorMask;
            internal uint ProcessorCount, ProcessorType, Granularity;
            internal ushort Level, Revision;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocation, uint protect);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern UIntPtr VirtualQuery(IntPtr address, out Region region, UIntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint protect, out uint oldProtect);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint kind);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")]
        private static extern void GetSystemInfo(out SystemInfo info);
        internal static IntPtr Allocate(int size)
        {
            IntPtr result = VirtualAlloc(IntPtr.Zero, new UIntPtr((uint)size), 0x3000, ReadWrite);
            if (result == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualAlloc failed.");
            return result;
        }
        internal static IntPtr AllocateNear(IntPtr target, int size)
        {
            SystemInfo system;
            GetSystemInfo(out system);
            long granularity = system.Granularity;
            long lower = Math.Max(system.Minimum.ToInt64(), target.ToInt64() + 5L + int.MinValue);
            long upper = Math.Min(system.Maximum.ToInt64() - size + 1L, target.ToInt64() + 5L + int.MaxValue);
            long cursor = Align(lower, granularity);
            for (int regions = 0; regions < 65536 && cursor <= upper; regions++)
            {
                Region region;
                if (VirtualQuery(new IntPtr(cursor), out region, new UIntPtr((uint)Marshal.SizeOf(typeof(Region)))) == UIntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualQuery failed while locating a native island.");
                long end = checked(region.Base.ToInt64() + (long)region.Size.ToUInt64());
                long candidate = Align(Math.Max(cursor, region.Base.ToInt64()), granularity);
                if (region.State == 0x10000 && candidate <= upper && candidate <= end - size)
                {
                    IntPtr result = VirtualAlloc(new IntPtr(candidate), new UIntPtr((uint)size), 0x3000, ReadWrite);
                    if (result != IntPtr.Zero) return result;
                }
                if (end <= cursor) throw new InvalidOperationException("VirtualQuery returned a non-advancing memory region.");
                cursor = Align(end, granularity);
            }
            throw new InvalidOperationException("No free native island was found within the relative jump range.");
        }
        private static long Align(long value, long alignment)
        {
            return checked((value + alignment - 1L) / alignment * alignment);
        }
        internal static uint Protect(IntPtr address, int size, uint protection)
        {
            uint old;
            if (!VirtualProtect(address, new UIntPtr((uint)size), protection, out old))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualProtect failed.");
            return old;
        }
        internal static void Flush(IntPtr address, int size)
        {
            if (!FlushInstructionCache(GetCurrentProcess(), address, new UIntPtr((uint)size)))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "FlushInstructionCache failed.");
        }
        internal static void Free(IntPtr address)
        {
            if (address != IntPtr.Zero && !VirtualFree(address, UIntPtr.Zero, 0x8000))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualFree failed.");
        }
    }
}
