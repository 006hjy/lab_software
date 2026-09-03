using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace smartsensor
{
    /// <summary>一个 WCH 串口（对应采集板上的 1 个通道）。</summary>
    public sealed class SmartsensorPort
    {
        /// <summary>通道字母：A~H。</summary>
        public string Channel = "";

        /// <summary>COM 端口名，如 "COM54"。</summary>
        public string PortName = "";

        /// <summary>设备实例 ID。</summary>
        public string InstanceId = "";

        public override string ToString()
        {
            return $"Ch {Channel} = {PortName}";
        }
    }

    /// <summary>一组 8 个串口（对应一个 WCH 8 路串口芯片）。</summary>
    public sealed class SmartsensorGroup
    {
        /// <summary>组号（1 = 串口1~8，2 = 串口9~16；按 USB 端口顺序）。</summary>
        public int GroupIndex = 0;

        /// <summary>所属 WCH 8 路串口芯片设备节点。</summary>
        public DeviceNode Hub;

        /// <summary>按通道 A~H 排序的 8 个串口。</summary>
        public List<SmartsensorPort> Ports = new List<SmartsensorPort>();
    }

    /// <summary>一块 smartsensor 采集板（一个 USB hub 芯片 + 2 组串口）。</summary>
    public sealed class SmartsensorBoard
    {
        /// <summary>板号（按 USB hub 芯片所在端口顺序）。</summary>
        public int BoardIndex = 0;

        /// <summary>本板所属 USB hub 芯片设备节点（VID_1A86&amp;PID_8091）。</summary>
        public DeviceNode UsbHub;

        /// <summary>本板的两组串口（组1 = 串口1~8，组2 = 串口9~16）。</summary>
        public List<SmartsensorGroup> Groups = new List<SmartsensorGroup>();
    }

    /// <summary>
    /// smartsensor 采集板串口发现：
    ///
    /// 硬件结构：电脑 USB → USB hub 芯片（VID_1A86&PID_8091）→ 2 个
    /// WCH 8 路串口芯片（VID_1A86&PID_55D9）→ 16 路 UART。
    /// 每个 8 路芯片在 Windows 上表现为 1 个 "WCH USB=>Multi-SERIAL"
    /// 复合设备，其下挂 8 个 "WCH USB-SERIAL Ch A~H" 串口设备。
    ///
    /// 发现方式：枚举全部 PnP 设备，用 DEVPKEY_Device_Parent 读取父子关系，
    /// 把 16 个串口按父设备（WCH 8 路芯片）分成 2 组。
    /// </summary>
    public static class SmartsensorDiscovery
    {
        /// <summary>WCH 8 路串口芯片（"WCH USB=>Multi-SERIAL"）实例 ID 前缀。</summary>
        public const string WchHubPrefix = @"USB\VID_1A86&PID_55D9\";

        /// <summary>USB hub 芯片（"通用 USB 集线器"）实例 ID 前缀。</summary>
        public const string WchUsbHubPrefix = @"USB\VID_1A86&PID_8091\";

        /// <summary>WCH 串口设备（"WCH USB-SERIAL Ch X"）实例 ID 前缀。</summary>
        public const string WchPortPrefix = @"USB2MCOM\";

        private static readonly Regex ComIndexRegex =
            new Regex(@"USB55D9_COM_(\d+)", RegexOptions.IgnoreCase);

        /// <summary>
        /// 发现所有 smartsensor 采集板，按"板 → 组"返回。
        /// 每块板由一个 USB hub 芯片（VID_1A86&amp;PID_8091）唯一标识，
        /// 其下的两个 WCH 8 路串口芯片（VID_1A86&amp;PID_55D9）即两组串口。
        /// </summary>
        public static List<SmartsensorBoard> DiscoverBoards()
        {
            var nodes = DeviceTree.EnumeratePresent();
            DeviceTree.BuildTree(nodes);

            // 1) 找到所有 USB hub 芯片（每块板一个，实例 ID 唯一）。
            var usbHubs = nodes
                .Where(n => n.InstanceId.StartsWith(WchUsbHubPrefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => HubPortNumber(n.InstanceId))
                .ToList();

            var boards = new List<SmartsensorBoard>();
            int boardIndex = 0;
            foreach (var usbHub in usbHubs)
            {
                // 2) 该 USB hub 芯片下的 WCH 8 路串口芯片 = 本板的组。
                var chips = nodes
                    .Where(n => n.InstanceId.StartsWith(WchHubPrefix, StringComparison.OrdinalIgnoreCase)
                                && string.Equals(n.ParentInstanceId, usbHub.InstanceId,
                                    StringComparison.OrdinalIgnoreCase))
                    .OrderBy(n => HubPortNumber(n.InstanceId))
                    .ToList();
                if (chips.Count == 0)
                {
                    continue; // 该 hub 不是 smartsensor 采集板的（没有挂 WCH 串口芯片）
                }

                boardIndex++;
                var board = new SmartsensorBoard
                {
                    BoardIndex = boardIndex,
                    UsbHub = usbHub,
                    Groups = new List<SmartsensorGroup>()
                };

                int groupIndex = 0;
                foreach (var chip in chips)
                {
                    groupIndex++;
                    board.Groups.Add(new SmartsensorGroup
                    {
                        GroupIndex = groupIndex,
                        Hub = chip,
                        Ports = nodes
                            .Where(n => n.InstanceId.StartsWith(WchPortPrefix, StringComparison.OrdinalIgnoreCase)
                                        && string.Equals(n.ParentInstanceId, chip.InstanceId,
                                            StringComparison.OrdinalIgnoreCase))
                            .OrderBy(n => ComChannelIndex(n.InstanceId))
                            .Select(n => new SmartsensorPort
                            {
                                Channel = ComChannelLetter(n.InstanceId),
                                PortName = ReadComPortName(n.InstanceId),
                                InstanceId = n.InstanceId
                            })
                            .ToList()
                    });
                }

                boards.Add(board);
            }
            return boards;
        }

        /// <summary>
        /// 从实例 ID 尾部解析该 WCH 芯片挂在 USB hub 上的端口号
        /// （如 "...&0&1" → 1，"...&0&2" → 2），用于确定组顺序。
        /// </summary>
        private static int HubPortNumber(string instanceId)
        {
            int idx = instanceId.LastIndexOf('&');
            int port;
            return idx >= 0
                   && int.TryParse(instanceId.Substring(idx + 1),
                       NumberStyles.Integer, CultureInfo.InvariantCulture, out port)
                ? port
                : int.MaxValue;
        }

        /// <summary>解析串口通道序号（USB55D9_COM_00..07 → 0..7）。</summary>
        private static int ComChannelIndex(string instanceId)
        {
            var m = ComIndexRegex.Match(instanceId);
            int index;
            return m.Success
                   && int.TryParse(m.Groups[1].Value,
                       NumberStyles.Integer, CultureInfo.InvariantCulture, out index)
                ? index
                : int.MaxValue;
        }

        /// <summary>通道序号 → 字母（0 → 'A'，7 → 'H'）。</summary>
        private static string ComChannelLetter(string instanceId)
        {
            int index = ComChannelIndex(instanceId);
            return index >= 0 && index < 8
                ? ((char)('A' + index)).ToString()
                : "?";
        }

        /// <summary>从注册表读取串口设备对应的 COM 端口名（Device Parameters\PortName）。</summary>
        private static string ReadComPortName(string instanceId)
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Enum\" + instanceId + @"\Device Parameters"))
                {
                    return key?.GetValue("PortName") as string ?? "";
                }
            }
            catch
            {
                return "";
            }
        }
    }
}
