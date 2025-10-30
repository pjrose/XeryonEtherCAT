using System;
using System.Runtime.InteropServices;

namespace XeryonEtherCAT.App.Services;

public sealed class LogConsoleService : IDisposable
{
    private bool _consoleAllocated;

    public bool IsConsoleAttached => _consoleAllocated;

    public void EnsureConsole()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (_consoleAllocated)
        {
            return;
        }

        if (AttachConsole(uint.MaxValue))
        {
            _consoleAllocated = true;
            return;
        }

        if (AllocConsole())
        {
            _consoleAllocated = true;
        }
    }

    public void ReleaseConsole()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (!_consoleAllocated)
        {
            return;
        }

        FreeConsole();
        _consoleAllocated = false;
    }

    public void Dispose()
    {
        ReleaseConsole();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);
}
