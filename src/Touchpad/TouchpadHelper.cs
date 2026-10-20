using System.Runtime.InteropServices;
using ThreeFingerDragLite.Native;
using static ThreeFingerDragLite.Native.NativeMethods;

namespace ThreeFingerDragLite.Touchpad;

/// <summary>
/// Registers the precision touchpad as a raw input source and parses WM_INPUT HID reports
/// into contact lists. Ported from ThreeFingerDragOnWindows (MIT), which is based on
/// emoacht/RawInput.Touchpad, with one important fix: preparsed data is cached per device
/// handle and validated against the touchpad usage (0x0D/0x0005). On multi-TLC devices
/// (e.g. Synaptics SYNA2BA6), querying RIDI_PREPARSEDDATA from the WM_INPUT device handle
/// can return another collection's descriptors, which makes every HidP_GetUsageValue call
/// fail silently (observed: caps queries succeed, zero contacts extracted).
/// </summary>
public static class TouchpadHelper {
    public const ushort UsagePageTouchpad = 0x000D;
    public const ushort UsageTouchpad = 0x0005;

    private static IntPtr _preparsedCache = IntPtr.Zero;
    private static IntPtr _preparsedCacheDevice = IntPtr.Zero;
    private static bool _fallbackLogged;

    /// <summary>Call on WM_INPUT_DEVICE_CHANGE removal so a re-attached device never matches a stale cached handle.</summary>
    public static void InvalidatePreparsedCache() {
        if (_preparsedCache != IntPtr.Zero) Marshal.FreeHGlobal(_preparsedCache);
        _preparsedCache = IntPtr.Zero;
        _preparsedCacheDevice = IntPtr.Zero;
    }

    public static bool Exists() {
        // Enumerate via a native buffer: marshaling a struct[] for this API proved unreliable,
        // while the raw IntPtr form returns real device handles.
        uint count = 0;
        uint entrySize = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        if (GetRawInputDeviceList(IntPtr.Zero, ref count, entrySize) != 0) return false;
        if (count == 0) return false;

        IntPtr list = Marshal.AllocHGlobal((int)(count * entrySize));
        try {
            if (GetRawInputDeviceList(list, ref count, entrySize) != count) return false;

            for (uint i = 0; i < count; i++) {
                IntPtr entry = new IntPtr(list.ToInt64() + i * entrySize);
                IntPtr hDevice = Marshal.ReadIntPtr(entry);
                uint dwType = (uint)Marshal.ReadInt32(entry, 8);
                if (dwType == RIM_TYPEHID && IsTouchpadDevice(hDevice)) return true;
            }
            return false;
        } finally {
            Marshal.FreeHGlobal(list);
        }
    }

    private static bool IsTouchpadDevice(IntPtr hDevice) {
        uint infoSize = 0;
        if (GetRawInputDeviceInfo(hDevice, RIDI_DEVICEINFO, IntPtr.Zero, ref infoSize) == unchecked((uint)-1)) return false;

        // RID_DEVICE_INFO layout: cbSize(4), dwType(4), then the hid member:
        // vendorId(4), productId(4), versionNumber(4), usagePage(2), usage(2).
        // The API requires cbSize to be set by the caller before the query, and the
        // buffer must be deterministic (AllocHGlobal memory is not).
        var buffer = new byte[Math.Max(infoSize, 24)];
        BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), infoSize);

        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try {
            if (GetRawInputDeviceInfo(hDevice, RIDI_DEVICEINFO, pinned.AddrOfPinnedObject(), ref infoSize) == unchecked((uint)-1))
                return false;
            ushort usagePage = (ushort)BitConverter.ToUInt16(buffer, 20);
            ushort usage = (ushort)BitConverter.ToUInt16(buffer, 22);
            return usagePage == UsagePageTouchpad && usage == UsageTouchpad;
        } finally {
            pinned.Free();
        }
    }

    /// <summary>Registers raw input for all precision touchpads; messages arrive even when the window is in the background.</summary>
    public static bool RegisterInput(IntPtr hwndTarget) {
        var device = new RAWINPUTDEVICE {
            usUsagePage = UsagePageTouchpad,
            usUsage = UsageTouchpad,
            dwFlags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY,
            hwndTarget = hwndTarget
        };
        return RegisterRawInputDevices(new[] { device }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    /// <summary>Returns cached preparsed data describing the precision touchpad collection (never freed; process-lifetime cache).</summary>
    private static IntPtr GetPreparsedData(IntPtr hDevice) {
        if (_preparsedCache != IntPtr.Zero && _preparsedCacheDevice == hDevice)
            return _preparsedCache;

        if (hDevice != IntPtr.Zero) {
            uint size = 0;
            if (GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, IntPtr.Zero, ref size) == 0 && size > 0) {
                IntPtr buffer = Marshal.AllocHGlobal((int)size);
                if (GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, buffer, ref size) != unchecked((uint)-1) &&
                    HidP_GetCaps(buffer, out var caps) == HIDP_STATUS_SUCCESS &&
                    caps.UsagePage == UsagePageTouchpad && caps.Usage == UsageTouchpad) {
                    _preparsedCache = buffer;
                    _preparsedCacheDevice = hDevice;
                    return buffer;
                }
                Marshal.FreeHGlobal(buffer);
            }
        }

        return GetPreparsedFromEnumeratedTouchpad(hDevice);
    }

    private static IntPtr GetPreparsedFromEnumeratedTouchpad(IntPtr hDevice) {
        if (!_fallbackLogged) {
            _fallbackLogged = true;
            Logger.Log($"preparsed fallback: WM_INPUT device 0x{hDevice:X} did not describe the touchpad collection; using the enumerated touchpad");
        }

        uint count = 0;
        uint entrySize = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        if (GetRawInputDeviceList(IntPtr.Zero, ref count, entrySize) != 0 || count == 0)
            return IntPtr.Zero;

        IntPtr list = Marshal.AllocHGlobal((int)(count * entrySize));
        try {
            if (GetRawInputDeviceList(list, ref count, entrySize) != count) return IntPtr.Zero;

            for (uint i = 0; i < count; i++) {
                IntPtr entry = new IntPtr(list.ToInt64() + i * entrySize);
                IntPtr candidate = Marshal.ReadIntPtr(entry);
                if ((uint)Marshal.ReadInt32(entry, 8) != RIM_TYPEHID) continue;
                if (!IsTouchpadDevice(candidate)) continue;

                uint size = 0;
                if (GetRawInputDeviceInfo(candidate, RIDI_PREPARSEDDATA, IntPtr.Zero, ref size) != 0 || size == 0) continue;
                IntPtr buffer = Marshal.AllocHGlobal((int)size);
                if (GetRawInputDeviceInfo(candidate, RIDI_PREPARSEDDATA, buffer, ref size) == unchecked((uint)-1)) {
                    Marshal.FreeHGlobal(buffer);
                    continue;
                }
                _preparsedCache = buffer;
                _preparsedCacheDevice = candidate;
                return buffer;
            }
            return IntPtr.Zero;
        } finally {
            Marshal.FreeHGlobal(list);
        }
    }

    /// <summary>Parses a WM_INPUT lParam into (device handle, contacts, declared contact count).</summary>
    public static (IntPtr device, List<TouchpadContact>? contacts, uint declaredCount) ParseInput(IntPtr lParam) {
        uint rawInputSize = 0;
        uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();

        if (GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref rawInputSize, headerSize) != 0)
            return (IntPtr.Zero, null, 0);

        IntPtr rawInputPointer = Marshal.AllocHGlobal((int)rawInputSize);
        try {
            if (GetRawInputData(lParam, RID_INPUT, rawInputPointer, ref rawInputSize, headerSize) != rawInputSize)
                return (IntPtr.Zero, null, 0);

            var rawInput = Marshal.PtrToStructure<RAWINPUT>(rawInputPointer);
            IntPtr currentDevice = rawInput.Header.hDevice;

            int hidDataLength = (int)(rawInput.Hid.dwSizeHid * rawInput.Hid.dwCount);
            if (hidDataLength <= 0) return (currentDevice, null, 0);

            // The raw HID bytes live at (rawInputSize - hidDataLength) within the RAWINPUT buffer.
            var rawHidRawData = new byte[hidDataLength];
            long dataOffset = rawInputSize - (uint)hidDataLength;
            Marshal.Copy(new IntPtr(rawInputPointer.ToInt64() + (long)dataOffset), rawHidRawData, 0, hidDataLength);

            IntPtr preparsedPointer = GetPreparsedData(currentDevice);
            if (preparsedPointer == IntPtr.Zero) return (currentDevice, null, 0);

            if (HidP_GetCaps(preparsedPointer, out var caps) != HIDP_STATUS_SUCCESS)
                return (currentDevice, null, 0);

            ushort valueCapsLength = caps.NumberInputValueCaps;
            var valueCaps = new HIDP_VALUE_CAPS[valueCapsLength];
            if (HidP_GetValueCaps(HidP_Input, valueCaps, ref valueCapsLength, preparsedPointer) != HIDP_STATUS_SUCCESS)
                return (currentDevice, null, 0);

            uint scanTime = 0;
            uint contactCount = 99;

            var creators = new List<TouchpadContactCreator>();
            var contacts = new List<TouchpadContact>();

            IntPtr hidDataPointer = Marshal.AllocHGlobal(hidDataLength);
            try {
                Marshal.Copy(rawHidRawData, 0, hidDataPointer, hidDataLength);

                foreach (var valueCap in valueCaps.OrderBy(x => x.LinkCollection)) {
                    for (int contactIndex = 0; contactIndex < rawInput.Hid.dwCount; contactIndex++) {
                        IntPtr reportPointer = new IntPtr(hidDataPointer.ToInt64()
                            + (long)rawInput.Hid.dwSizeHid * contactIndex);

                        if (HidP_GetUsageValue(HidP_Input, valueCap.UsagePage, valueCap.LinkCollection,
                                valueCap.Usage, out var value, preparsedPointer, reportPointer,
                                (uint)hidDataLength) != HIDP_STATUS_SUCCESS)
                            continue;

                        switch (valueCap.LinkCollection) {
                            case 0:
                                switch (valueCap.UsagePage, valueCap.Usage) {
                                    case (0x0D, 0x56): scanTime = value; break;           // Scan Time
                                    case (0x0D, 0x54): contactCount = value; break;       // Contact Count
                                }
                                break;

                            default:
                                while (creators.Count <= contactIndex) creators.Add(new TouchpadContactCreator());
                                switch (valueCap.UsagePage, valueCap.Usage) {
                                    case (0x0D, 0x51): creators[contactIndex].ContactId = (int)value; break; // Contact ID
                                    case (0x01, 0x30): creators[contactIndex].X = (int)value; break;         // X
                                    case (0x01, 0x31): creators[contactIndex].Y = (int)value; break;         // Y
                                }
                                break;
                        }
                    }

                    foreach (var creator in creators) {
                        if ((contactCount == 0 || contacts.Count < contactCount) && creator.TryCreate(out var contact)) {
                            contacts.Add(contact);
                            creator.Clear();
                        }
                    }
                    if (contactCount != 0 && contacts.Count >= contactCount) break;
                }
            } finally {
                Marshal.FreeHGlobal(hidDataPointer);
            }

            return (currentDevice, contacts, contactCount);
        } finally {
            Marshal.FreeHGlobal(rawInputPointer);
        }
    }
}
