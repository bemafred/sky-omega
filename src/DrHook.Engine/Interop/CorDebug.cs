// Source-generated COM interfaces for the CONSUME direction (we call ICorDebug).
// Validated by PoC probes 02/03/05/06 (findings 03/05). [PreserveSig] int on every
// method for explicit HRESULT control; type mapping per finding 08 (LONG/ULONG/DWORD ->
// int/uint, locked 32-bit by the PAL; pointers -> nint). Methods are declared in exact
// IDL order so the generated RCW dispatches to the correct native V-table slot; uncalled
// methods are slot-fillers that keep later methods at their correct slots.

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace SkyOmega.DrHook.Engine.Interop;

/// <summary>ICorDebug — the root debugger interface (cordebug.idl). Declared through slot 8
/// (DebugActiveProcess); slots 6-7 are uncalled fillers preserving slot ordering.</summary>
[GeneratedComInterface]
[Guid("3d6f5f61-7538-11d3-8d5b-00104b35e7ef")]
internal partial interface ICorDebug
{
    [PreserveSig] int Initialize();                                   // slot 3
    [PreserveSig] int Terminate();                                    // slot 4
    [PreserveSig] int SetManagedHandler(nint pCallback);              // slot 5
    [PreserveSig] int SetUnmanagedHandler(nint pCallback);            // slot 6 (filler, uncalled)
    [PreserveSig] int CreateProcess(nint lpApplicationName, nint lpCommandLine, // slot 7 (filler, uncalled)
        nint lpProcessAttributes, nint lpThreadAttributes, int bInheritHandles,
        uint dwCreationFlags, nint lpEnvironment, nint lpCurrentDirectory,
        nint lpStartupInfo, nint lpProcessInformation, int debuggingFlags, out nint ppProcess);
    [PreserveSig] int DebugActiveProcess(uint id, int win32Attach, out nint ppProcess); // slot 8
}

/// <summary>ICorDebugController — base of processes and app domains (cordebug.idl). Declared
/// through slot 9 (Detach); we use Continue (slot 4) and Detach (slot 9). pProcess from
/// DebugActiveProcess QIs to this (ICorDebugProcess : ICorDebugController).</summary>
[GeneratedComInterface]
[Guid("3d6f5f62-7538-11d3-8d5b-00104b35e7ef")]
internal partial interface ICorDebugController
{
    [PreserveSig] int Stop(uint dwTimeoutIgnored);                            // slot 3 (filler)
    [PreserveSig] int Continue(int fIsOutOfBand);                             // slot 4
    [PreserveSig] int IsRunning(out int pbRunning);                          // slot 5 (filler)
    [PreserveSig] int HasQueuedCallbacks(nint pThread, out int pbQueued);    // slot 6 (filler)
    [PreserveSig] int EnumerateThreads(out nint ppThreads);                  // slot 7 (filler)
    [PreserveSig] int SetAllThreadsDebugState(int state, nint pExceptThisThread); // slot 8 (filler)
    [PreserveSig] int Detach();                                             // slot 9
}
