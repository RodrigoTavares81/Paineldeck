using System.Runtime.InteropServices;

namespace PainelDeck;

/// <summary>Monitor físico ligado numa tela: nome amigável, EDID (fabricante+produto) e caminho do dispositivo.</summary>
internal sealed record MonitorInfo(string GdiName, string Name, string Edid, string Path);

/// <summary>
/// Liga cada tela do Windows (\\.\DISPLAYn, que muda quando você pluga/despluga) ao monitor físico,
/// via QueryDisplayConfig + DisplayConfigGetDeviceInfo. Assim o painel sabe que é o Arzopa de verdade.
/// </summary>
internal static class DisplayConfig
{
    private const uint QDC_ONLY_ACTIVE_PATHS = 2;
    private const uint GET_SOURCE_NAME = 1, GET_TARGET_NAME = 2;

    [StructLayout(LayoutKind.Sequential)] private struct LUID { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_SOURCE { public LUID adapterId; public uint id, modeInfoIdx, statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_TARGET
    {
        public LUID adapterId; public uint id, modeInfoIdx, outputTechnology, rotation, scaling;
        public uint refreshNum, refreshDen, scanLineOrdering; public int targetAvailable; public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_INFO { public PATH_SOURCE source; public PATH_TARGET target; public uint flags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MODE_INFO { public uint infoType, id; public LUID adapterId; public ulong u0, u1, u2, u3, u4, u5; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HEADER { public uint type, size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SOURCE_NAME
    {
        public HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TARGET_NAME
    {
        public HEADER header;
        public uint flags, outputTechnology;
        public ushort edidManufactureId, edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PATH_INFO[] paths, ref uint numModes, [Out] MODE_INFO[] modes, IntPtr topologyId);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref SOURCE_NAME info);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref TARGET_NAME info);

    /// <summary>Monitores ativos agora. Se a API falhar, devolve lista vazia (o painel trata como "monitor não achado").</summary>
    public static List<MonitorInfo> Active()
    {
        var list = new List<MonitorInfo>();
        for (int attempt = 0; attempt < 3; attempt++) // o tamanho pode mudar entre as duas chamadas se o monitor acabou de plugar
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var np, out var nm) != 0) return list;
            var paths = new PATH_INFO[np];
            var modes = new MODE_INFO[nm];
            int err = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero);
            if (err == 122) continue; // ERROR_INSUFFICIENT_BUFFER
            if (err != 0) return list;
            for (int i = 0; i < np; i++)
            {
                var p = paths[i];
                var src = new SOURCE_NAME { header = { type = GET_SOURCE_NAME, size = (uint)Marshal.SizeOf<SOURCE_NAME>(), adapterId = p.source.adapterId, id = p.source.id } };
                var tgt = new TARGET_NAME { header = { type = GET_TARGET_NAME, size = (uint)Marshal.SizeOf<TARGET_NAME>(), adapterId = p.target.adapterId, id = p.target.id } };
                if (DisplayConfigGetDeviceInfo(ref src) != 0 || DisplayConfigGetDeviceInfo(ref tgt) != 0) continue;
                list.Add(new MonitorInfo(src.viewGdiDeviceName, tgt.monitorFriendlyDeviceName ?? "",
                    EdidId(tgt.edidManufactureId, tgt.edidProductCodeId), tgt.monitorDevicePath ?? ""));
            }
            return list;
        }
        return list;
    }

    /// <summary>EDID: fabricante = 3 letras de 5 bits (big-endian), produto em hex. Ex.: GWD + 1161 = "GWD1161".</summary>
    public static string EdidId(ushort manufacturer, ushort product)
    {
        int m = (ushort)((manufacturer >> 8) | (manufacturer << 8));
        char L(int shift) => (char)('A' - 1 + ((m >> shift) & 0x1F));
        return $"{L(10)}{L(5)}{L(0)}{product:X4}";
    }
}
