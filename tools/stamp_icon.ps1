param([string]$exe, [string]$ico)

Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class IconUpdater {
    [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    public static extern IntPtr BeginUpdateResource(string pFileName, bool bDeleteExistingResources);
    [DllImport("kernel32.dll", SetLastError=true)]
    public static extern bool UpdateResource(IntPtr hUpdate, uint lpType, uint lpName, ushort wLanguage, byte[] lpData, uint cbData);
    [DllImport("kernel32.dll", SetLastError=true)]
    public static extern bool EndUpdateResource(IntPtr hUpdate, bool fDiscard);

    public static void Embed(string exePath, string icoPath) {
        byte[] icoData = File.ReadAllBytes(icoPath);
        int imgCount = BitConverter.ToUInt16(icoData, 4);

        var grp = new List<byte>();
        grp.AddRange(new byte[]{0, 0, 1, 0}); // reserved=0, type=1 (icon)
        grp.AddRange(BitConverter.GetBytes((ushort)imgCount));

        var frames = new List<byte[]>();
        for (int i = 0; i < imgCount; i++) {
            int off = 6 + i * 16;
            byte w  = icoData[off],     h  = icoData[off+1];
            byte cc = icoData[off+2],   res = icoData[off+3];
            ushort planes = BitConverter.ToUInt16(icoData, off+4);
            ushort bpp    = BitConverter.ToUInt16(icoData, off+6);
            int size    = (int)BitConverter.ToUInt32(icoData, off+8);
            int dataOff = (int)BitConverter.ToUInt32(icoData, off+12);

            byte[] frame = new byte[size];
            Array.Copy(icoData, dataOff, frame, 0, size);
            frames.Add(frame);

            // GRPICONDIRENTRY
            grp.Add(w); grp.Add(h); grp.Add(cc); grp.Add(res);
            grp.AddRange(BitConverter.GetBytes(planes));
            grp.AddRange(BitConverter.GetBytes(bpp));
            grp.AddRange(BitConverter.GetBytes((uint)size));
            grp.AddRange(BitConverter.GetBytes((ushort)(i + 1))); // nId
        }

        IntPtr hRes = BeginUpdateResource(exePath, false);
        if (hRes == IntPtr.Zero) throw new Exception("BeginUpdateResource failed: " + Marshal.GetLastWin32Error());
        for (int i = 0; i < frames.Count; i++)
            UpdateResource(hRes, 3, (uint)(i + 1), 0, frames[i], (uint)frames[i].Length); // RT_ICON
        UpdateResource(hRes, 14, 1, 0, grp.ToArray(), (uint)grp.Count);                    // RT_GROUP_ICON
        if (!EndUpdateResource(hRes, false)) throw new Exception("EndUpdateResource failed: " + Marshal.GetLastWin32Error());
    }
}
'@

try {
    [IconUpdater]::Embed($exe, $ico)
    Write-Output "Icon stamped into $(Split-Path $exe -Leaf)"
} catch {
    Write-Warning "stamp_icon: $_"
}
