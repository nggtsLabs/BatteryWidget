// Minimaler HID-Zugriff über die Win32-API (hid.dll / setupapi.dll), ohne externe Bibliotheken.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace BatteryWidget
{
    public sealed class HidDeviceInfo
    {
        public string Path;
        public ushort VendorId;
        public ushort ProductId;
        public ushort UsagePage;
        public ushort Usage;
        public int InputReportLength;
        public int OutputReportLength;

        public override string ToString()
        {
            return string.Format("{0:X4}:{1:X4} page={2:X4} usage={3:X4} in={4} out={5}",
                VendorId, ProductId, UsagePage, Usage, InputReportLength, OutputReportLength);
        }
    }

    public sealed class HidDevice : IDisposable
    {
        readonly SafeFileHandle handle;
        public readonly HidDeviceInfo Info;

        HidDevice(SafeFileHandle handle, HidDeviceInfo info)
        {
            this.handle = handle;
            Info = info;
        }

        public static List<HidDeviceInfo> Enumerate(ushort vendorId)
        {
            var result = new List<HidDeviceInfo>();
            Guid hidGuid;
            Native.HidD_GetHidGuid(out hidGuid);
            IntPtr set = Native.SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero,
                Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
            if (set == Native.INVALID_HANDLE_VALUE) return result;
            try
            {
                var ifData = new Native.SP_DEVICE_INTERFACE_DATA();
                ifData.cbSize = Marshal.SizeOf(ifData);
                for (uint i = 0; Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref ifData); i++)
                {
                    string path = GetInterfacePath(set, ref ifData);
                    if (path == null) continue;
                    var info = Query(path);
                    if (info != null && info.VendorId == vendorId) result.Add(info);
                }
            }
            finally
            {
                Native.SetupDiDestroyDeviceInfoList(set);
            }
            return result;
        }

        static string GetInterfacePath(IntPtr set, ref Native.SP_DEVICE_INTERFACE_DATA ifData)
        {
            int size;
            Native.SetupDiGetDeviceInterfaceDetail(set, ref ifData, IntPtr.Zero, 0, out size, IntPtr.Zero);
            if (size <= 0) return null;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                // cbSize der Detail-Struktur: 8 auf x64, 6 auf x86
                Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref ifData, buf, size, out size, IntPtr.Zero))
                    return null;
                return Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4));
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        static HidDeviceInfo Query(string path)
        {
            // Zugriff 0 reicht für Attribute/Caps, auch bei Geräten, die exklusiv belegt sind
            using (var h = Native.CreateFile(path, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) return null;
                var attr = new Native.HIDD_ATTRIBUTES();
                attr.Size = Marshal.SizeOf(attr);
                if (!Native.HidD_GetAttributes(h, ref attr)) return null;
                IntPtr pre;
                if (!Native.HidD_GetPreparsedData(h, out pre)) return null;
                try
                {
                    Native.HIDP_CAPS caps;
                    if (Native.HidP_GetCaps(pre, out caps) != Native.HIDP_STATUS_SUCCESS) return null;
                    return new HidDeviceInfo
                    {
                        Path = path,
                        VendorId = attr.VendorID,
                        ProductId = attr.ProductID,
                        UsagePage = caps.UsagePage,
                        Usage = caps.Usage,
                        InputReportLength = caps.InputReportByteLength,
                        OutputReportLength = caps.OutputReportByteLength,
                    };
                }
                finally
                {
                    Native.HidD_FreePreparsedData(pre);
                }
            }
        }

        public static HidDevice Open(HidDeviceInfo info)
        {
            var h = Native.CreateFile(info.Path, Native.GENERIC_READ | Native.GENERIC_WRITE,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING,
                Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            return new HidDevice(h, info);
        }

        /// <summary>Schreibt einen Report; data[0] ist die Report-ID. Wird auf OutputReportLength aufgefüllt.</summary>
        public void Write(byte[] data)
        {
            var buf = new byte[Math.Max(Info.OutputReportLength, data.Length)];
            Array.Copy(data, buf, data.Length);
            if (Transfer(buf, true, 1000) < 0) throw new TimeoutException("HID write timeout");
        }

        /// <summary>Liest einen Input-Report (inkl. Report-ID an Index 0) oder null bei Timeout.</summary>
        public byte[] Read(int timeoutMs)
        {
            var buf = new byte[Info.InputReportLength];
            int n = Transfer(buf, false, timeoutMs);
            if (n < 0) return null;
            if (n < buf.Length) Array.Resize(ref buf, n);
            return buf;
        }

        int Transfer(byte[] buf, bool write, int timeoutMs)
        {
            IntPtr mem = Marshal.AllocHGlobal(buf.Length);
            IntPtr ov = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeOverlapped)));
            using (var ev = new ManualResetEvent(false))
            {
                try
                {
                    if (write) Marshal.Copy(buf, 0, mem, buf.Length);
                    var o = new NativeOverlapped();
                    o.EventHandle = ev.SafeWaitHandle.DangerousGetHandle();
                    Marshal.StructureToPtr(o, ov, false);

                    int done;
                    bool ok = write
                        ? Native.WriteFile(handle, mem, buf.Length, out done, ov)
                        : Native.ReadFile(handle, mem, buf.Length, out done, ov);
                    if (!ok)
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (err != Native.ERROR_IO_PENDING) throw new Win32Exception(err);
                        if (!ev.WaitOne(timeoutMs))
                        {
                            Native.CancelIoEx(handle, ov);
                            Native.GetOverlappedResult(handle, ov, out done, true);
                            return -1;
                        }
                        if (!Native.GetOverlappedResult(handle, ov, out done, false))
                            throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                    if (!write) Marshal.Copy(mem, buf, 0, Math.Min(done, buf.Length));
                    return done;
                }
                finally
                {
                    Marshal.FreeHGlobal(mem);
                    Marshal.FreeHGlobal(ov);
                }
            }
        }

        public void Dispose()
        {
            handle.Dispose();
        }
    }

    static class Native
    {
        public const int DIGCF_PRESENT = 0x02, DIGCF_DEVICEINTERFACE = 0x10;
        public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
        public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        public const int ERROR_IO_PENDING = 997;
        public const int HIDP_STATUS_SUCCESS = 0x00110000;
        public static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("hid.dll")] public static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES attr);
        [DllImport("hid.dll")] public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
        [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwnd, int flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid guid, uint index,
            ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data,
            IntPtr detail, int detailSize, out int requiredSize, IntPtr devInfo);
        [DllImport("setupapi.dll")] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec,
            uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadFile(SafeFileHandle h, IntPtr buf, int len, out int read, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteFile(SafeFileHandle h, IntPtr buf, int len, out int written, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetOverlappedResult(SafeFileHandle h, IntPtr ov, out int transferred, bool wait);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CancelIoEx(SafeFileHandle h, IntPtr ov);
    }
}
