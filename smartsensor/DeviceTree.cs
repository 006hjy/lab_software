using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace smartsensor
{
    /// <summary>一个 PnP 设备节点（通过 SetupAPI 枚举）。</summary>
    public sealed class DeviceNode
    {
        public string InstanceId = "";
        public string Name = "";
        public string Class = "";
        public string ParentInstanceId = "";

        public DeviceNode Parent;
        public readonly List<DeviceNode> Children = new List<DeviceNode>();

        public override string ToString()
        {
            return string.IsNullOrEmpty(Name) ? InstanceId : Name + "  [" + InstanceId + "]";
        }
    }

    /// <summary>
    /// 使用 SetupAPI 枚举当前存在的所有 PnP 设备，并读取每个设备的父设备
    /// （DEVPKEY_Device_Parent），用于构建设备树。
    /// </summary>
    public static class DeviceTree
    {
        private const uint DIGCF_PRESENT = 0x00000002;
        private const uint DIGCF_ALLCLASSES = 0x00000004;
        private const uint ERROR_INSUFFICIENT_BUFFER = 122;

        private const uint SPDRP_DEVICEDESC = 0x00000000;
        private const uint SPDRP_CLASS = 0x00000007;
        private const uint SPDRP_FRIENDLYNAME = 0x0000000C;

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA
        {
            public uint cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr SetupDiGetClassDevs(
            IntPtr classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(
            IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool SetupDiGetDeviceInstanceId(
            IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
            StringBuilder deviceInstanceId, uint deviceInstanceIdSize, out uint requiredSize);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool SetupDiGetDeviceRegistryProperty(
            IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, uint property,
            out uint propertyRegDataType, StringBuilder propertyBuffer,
            uint propertyBufferSize, out uint requiredSize);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_IDW(uint dnDevInst, StringBuilder buffer,
            uint bufferLen, uint ulFlags);

        /// <summary>枚举所有当前存在的设备，并解析每个设备的父设备 ID。</summary>
        public static List<DeviceNode> EnumeratePresent()
        {
            IntPtr set = SetupDiGetClassDevs(
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
            if (set == IntPtr.Zero || set == new IntPtr(-1))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var nodes = new List<DeviceNode>();
            try
            {
                var info = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA)) };
                uint index = 0;
                while (SetupDiEnumDeviceInfo(set, index, ref info))
                {
                    index++;
                    nodes.Add(new DeviceNode
                    {
                        InstanceId = GetInstanceId(set, ref info),
                        Name = GetRegistryProperty(set, ref info, SPDRP_FRIENDLYNAME)
                            ?? GetRegistryProperty(set, ref info, SPDRP_DEVICEDESC)
                            ?? "",
                        Class = GetRegistryProperty(set, ref info, SPDRP_CLASS) ?? "",
                        ParentInstanceId = GetParentInstanceId(set, ref info) ?? ""
                    });
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }
            return nodes;
        }

        /// <summary>根据 ParentInstanceId 建立父子引用，供整树遍历使用。</summary>
        public static void BuildTree(List<DeviceNode> nodes)
        {
            var byId = new Dictionary<string, DeviceNode>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in nodes)
            {
                byId[n.InstanceId] = n;
            }
            foreach (var n in nodes)
            {
                if (!string.IsNullOrEmpty(n.ParentInstanceId)
                    && byId.TryGetValue(n.ParentInstanceId, out var parent))
                {
                    n.Parent = parent;
                    parent.Children.Add(n);
                }
            }
        }

        private static string GetInstanceId(IntPtr set, ref SP_DEVINFO_DATA info)
        {
            uint required = 0;
            SetupDiGetDeviceInstanceId(set, ref info, null, 0, out required);
            var sb = new StringBuilder((int)required);
            return SetupDiGetDeviceInstanceId(set, ref info, sb, required, out required)
                ? sb.ToString()
                : "";
        }

        private static string GetRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA info, uint property)
        {
            uint regType, required = 0;
            if (!SetupDiGetDeviceRegistryProperty(set, ref info, property, out regType, null, 0, out required))
            {
                if (Marshal.GetLastWin32Error() != ERROR_INSUFFICIENT_BUFFER)
                {
                    return null;
                }
            }
            var sb = new StringBuilder((int)required);
            return SetupDiGetDeviceRegistryProperty(set, ref info, property, out regType, sb, required, out required)
                ? sb.ToString()
                : null;
        }

        private static string GetParentInstanceId(IntPtr set, ref SP_DEVINFO_DATA info)
        {
            // 用 cfgmgr32 的 CM_Get_Parent 获取父设备句柄，再取其实例 ID。
            uint parentDevInst;
            if (CM_Get_Parent(out parentDevInst, info.DevInst, 0) != 0)
            {
                return null;
            }

            var sb = new StringBuilder(512);
            if (CM_Get_Device_IDW(parentDevInst, sb, (uint)sb.Capacity, 0) != 0)
            {
                return null;
            }
            return sb.ToString();
        }
    }
}
