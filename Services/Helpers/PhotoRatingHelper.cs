using System.Runtime.InteropServices;

namespace VRCNext.Services.Helpers;

// Reads and writes the native OS file rating. need to add linux support later.
public static class PhotoRatingHelper
{
#if WINDOWS
    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VarType;
        [FieldOffset(8)] public uint   UIntValue;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetPropertyStoreFromParsingName(
        string path, IntPtr bindCtx, int flags, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    private const ushort VT_UI4 = 19;
    private static Guid _propertyStoreIid = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static PropertyKey _ratingKey = new() { FormatId = new Guid("64440492-4C8B-11D1-8B70-080036B11A03"), PropertyId = 9 };

    private static int ReadRatingValue(string path)
    {
        IPropertyStore? store = null;
        try
        {
            if (SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 0, ref _propertyStoreIid, out store) != 0 || store == null)
                return 0;
            var key = _ratingKey;
            if (store.GetValue(ref key, out var value) != 0) return 0;
            try { return value.VarType == VT_UI4 ? (int)value.UIntValue : 0; }
            finally { PropVariantClear(ref value); }
        }
        catch { return 0; }
        finally { if (store != null) Marshal.ReleaseComObject(store); }
    }

    private static uint StarsToRatingValue(int stars) => stars switch
    {
        1 => 1,
        2 => 25,
        3 => 50,
        4 => 75,
        5 => 99,
        _ => 0,
    };

    private static int RatingValueToStars(int value) => value switch
    {
        <= 0  => 0,
        <= 12 => 1,
        <= 37 => 2,
        <= 62 => 3,
        <= 87 => 4,
        _     => 5,
    };

    public static Task<int> GetRatingAsync(string path) =>
        Task.FromResult(RatingValueToStars(ReadRatingValue(path)));

    public static async Task<bool> SetRatingAsync(string path, int stars)
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            var props = new Dictionary<string, object> { ["System.Rating"] = StarsToRatingValue(stars) };
            await file.Properties.SavePropertiesAsync(props);
            return true;
        }
        catch { return false; }
    }
#else
    public static Task<int> GetRatingAsync(string path) => Task.FromResult(0);
    public static Task<bool> SetRatingAsync(string path, int stars) => Task.FromResult(false);
#endif
}
