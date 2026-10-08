// SPDX-License-Identifier: GPL-2.0-or-later
// Lighting transaction adapted from OpenRGB's KeychronKeyboardController.
// Copyright (c) 2022 Morgan Guimard (OpenRGB protocol implementation).
// This Windows utility and native HID integration: 2026.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Win32.SafeHandles;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
[assembly: System.Reflection.AssemblyVersion("0.1.1.0")]
[assembly: System.Reflection.AssemblyFileVersion("0.1.1.0")]

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct InterfaceData
    { public int Size; public Guid ClassGuid; public int Flags; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] internal struct Attributes
    { public int Size; public ushort Vendor, Product, Version; }
    [StructLayout(LayoutKind.Sequential)] internal struct Caps
    {
        public ushort Usage, UsagePage, InputLength, OutputLength, FeatureLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst=17)] public ushort[] Reserved;
        public ushort LinkNodes, InputButtons, InputValues, InputIndices;
        public ushort OutputButtons, OutputValues, OutputIndices;
        public ushort FeatureButtons, FeatureValues, FeatureIndices;
    }
    [DllImport("hid.dll")] internal static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    internal static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string enumerator, IntPtr hwnd, uint flags);
    [DllImport("setupapi.dll", SetLastError=true)]
    internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr info, IntPtr device, ref Guid guid, uint index, ref InterfaceData data);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    internal static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr info, ref InterfaceData data, IntPtr detail, uint size, out uint needed, IntPtr device);
    [DllImport("setupapi.dll")] internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr info);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("hid.dll", SetLastError=true)] internal static extern bool HidD_GetAttributes(SafeFileHandle handle, ref Attributes data);
    [DllImport("hid.dll", SetLastError=true)] internal static extern bool HidD_GetProductString(SafeFileHandle handle, byte[] data, int length);
    [DllImport("hid.dll", SetLastError=true)] internal static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
    [DllImport("hid.dll")] internal static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] internal static extern int HidP_GetCaps(IntPtr data, out Caps caps);
    [DllImport("hid.dll", SetLastError=true)] internal static extern bool HidD_GetFeature(SafeFileHandle handle, byte[] data, int length);
    [DllImport("hid.dll", SetLastError=true)] internal static extern bool HidD_SetFeature(SafeFileHandle handle, byte[] data, int length);

    internal static List<string> Paths()
    {
        Guid guid; HidD_GetHidGuid(out guid);
        IntPtr list=SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x12);
        if(list == new IntPtr(-1)) throw new Win32Exception();
        List<string> result=new List<string>();
        try
        {
            for(uint i=0; ; i++)
            {
                InterfaceData data=new InterfaceData(); data.Size=Marshal.SizeOf(typeof(InterfaceData));
                if(!SetupDiEnumDeviceInterfaces(list, IntPtr.Zero, ref guid, i, ref data))
                { if(Marshal.GetLastWin32Error()==259) break; throw new Win32Exception(); }
                uint needed;
                SetupDiGetDeviceInterfaceDetail(list, ref data, IntPtr.Zero, 0, out needed, IntPtr.Zero);
                IntPtr detail=Marshal.AllocHGlobal((int)needed);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size==8 ? 8 : 6);
                    if(!SetupDiGetDeviceInterfaceDetail(list, ref data, detail, needed, out needed, IntPtr.Zero)) throw new Win32Exception();
                    result.Add(Marshal.PtrToStringUni(IntPtr.Add(detail, 4)));
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(list); }
        return result;
    }
}

internal sealed class Keyboard : IDisposable
{
    internal string Path, Product;
    internal Native.Caps Capabilities;
    internal SafeFileHandle Handle;
    internal static Keyboard Open(string path)
    {
        SafeFileHandle handle=Native.CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if(handle.IsInvalid) { handle.Dispose(); return null; }
        try
        {
            Native.Attributes attr=new Native.Attributes(); attr.Size=Marshal.SizeOf(typeof(Native.Attributes));
            if(!Native.HidD_GetAttributes(handle, ref attr) || attr.Vendor!=0x05AC || attr.Product!=0x024F) { handle.Dispose(); return null; }
            byte[] name=new byte[512];
            if(!Native.HidD_GetProductString(handle, name, name.Length)) throw new Win32Exception();
            string product=Encoding.Unicode.GetString(name).TrimEnd('\0');
            IntPtr prep;
            if(!Native.HidD_GetPreparsedData(handle, out prep)) throw new Win32Exception();
            Native.Caps caps;
            try { if(Native.HidP_GetCaps(prep, out caps)!=0x00110000) throw new Exception("Could not read the HID descriptor."); }
            finally { Native.HidD_FreePreparsedData(prep); }
            return new Keyboard { Path=path, Product=product, Capabilities=caps, Handle=handle };
        }
        catch { handle.Dispose(); throw; }
    }
    internal static Keyboard Find()
    {
        List<Keyboard> found=new List<Keyboard>();
        try
        {
            foreach(string path in Native.Paths())
            {
                if(path.IndexOf("vid_05ac&pid_024f", StringComparison.OrdinalIgnoreCase)<0) continue;
                Keyboard k=Open(path);
                if(k==null) continue;
                if(k.Product.Equals("Keychron K8", StringComparison.OrdinalIgnoreCase)
                    && path.IndexOf("&mi_00", StringComparison.OrdinalIgnoreCase)>=0
                    && k.Capabilities.UsagePage==1 && k.Capabilities.Usage==6 && k.Capabilities.FeatureLength==65)
                    found.Add(k);
                else k.Dispose();
            }
            if(found.Count!=1) throw new Exception(found.Count==0
                ? "No Keychron K8 with a compatible USB interface was found. Switch the keyboard to Cable mode."
                : "Multiple Keychron K8 keyboards are connected. Leave only one connected.");
            return found[0];
        }
        catch { foreach(Keyboard k in found) k.Dispose(); throw; }
    }
    internal void Send(byte[] data)
    {
        if(data.Length!=64) throw new ArgumentException("Invalid packet size");
        byte[] report=new byte[65]; Array.Copy(data, 0, report, 1, 64);
        if(!Native.HidD_SetFeature(Handle, report, report.Length)) throw new Win32Exception(Marshal.GetLastWin32Error(), "The keyboard rejected the USB command");
        Thread.Sleep(10);
    }
    internal byte[] Receive()
    {
        byte[] report=new byte[65];
        if(!Native.HidD_GetFeature(Handle, report, report.Length)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the keyboard response");
        Thread.Sleep(10);
        return report;
    }
    internal byte[] Command(byte command, bool receive, byte count)
    {
        byte[] data=new byte[64]; data[0]=4; data[1]=command; data[8]=count;
        Send(data);
        if(!receive) return null;
        byte[] reply=Receive();
        if(reply[1]!=4 || reply[2]!=command)
            throw new Exception("The keyboard returned an unexpected response. Applying settings was stopped.");
        return reply;
    }
    public void Dispose() { if(Handle!=null) Handle.Dispose(); }
}



