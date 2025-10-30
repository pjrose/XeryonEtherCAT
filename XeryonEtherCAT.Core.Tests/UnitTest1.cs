using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestPlatform.TestExecutor;
using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using XeryonEtherCAT.Core.Internal.Soem;
using XeryonEtherCAT.Core.Models;
using XeryonEtherCAT.Core.Services;
using Xunit;

namespace XeryonEtherCAT.Core.Tests;

public sealed class SoemShimImportTests
{
    [Fact]
    public void AllNativeMethodsAreBoundToSoemShimLibrary()
    {
        var methods = typeof(SoemShim).GetMethods(BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotEmpty(methods);

        foreach (var method in methods)
        {
            var import = method.GetCustomAttribute<DllImportAttribute>();
            Assert.NotNull(import);
            Assert.Equal("soemshim", import!.Value);
            Assert.Equal(CallingConvention.Cdecl, import.CallingConvention);
        }
    }

    [Fact]
    public void MissingNativeLibrarySurfacesAsDllNotFound()
    {
        var ex = Assert.Throws<DllNotFoundException>(() => new SoemClient(NullLogger<SoemClient>.Instance));
        Assert.Contains("soemshim", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class SoemShimLayoutTests
{
    [Fact]
    public void DriveRxPdoMatchesNativeSize()
    {
        var size = Marshal.SizeOf<SoemShim.DriveRxPDO>();
        Assert.Equal(45, size);
    }

    [Fact]
    public void DriveTxPdoMatchesNativeSize()
    {
        var size = Marshal.SizeOf<SoemShim.DriveTxPDO>();
        Assert.Equal(27, size);
    }
}

public sealed class PendingCommandEncodingTests
{
    private static readonly Type PendingCommandType = typeof(EthercatDriveService).GetNestedType("PendingCommand", BindingFlags.NonPublic)!
        ?? throw new InvalidOperationException("PendingCommand type not found.");

    private static readonly Type CommandCompletionType = typeof(EthercatDriveService).GetNestedType("CommandCompletion", BindingFlags.NonPublic)!
        ?? throw new InvalidOperationException("CommandCompletion type not found.");

    [Fact]
    public void ApplyPopulatesCommandFrame()
    {
        var completion = Enum.Parse(CommandCompletionType, "AckOnly");
        var createMotion = PendingCommandType.GetMethod("CreateMotion", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("CreateMotion factory not found.");
        var command = createMotion.Invoke(null, new object[]
        {
            0,
            "TEST",
            123,
            456,
            (ushort)789,
            (ushort)321,
            TimeSpan.FromSeconds(10),
            completion!,
            true
        }) ?? throw new InvalidOperationException("Factory returned null.");

        var start = PendingCommandType.GetMethod("Start", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Start method not found.");
        start.Invoke(command, Array.Empty<object>());

        var apply = PendingCommandType.GetMethod("Apply", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Apply method not found.");

        var parameters = new object[] { new SoemShim.DriveRxPDO { Command = new byte[32] } };
        apply.Invoke(command, parameters);
        var pdo = (SoemShim.DriveRxPDO)parameters[0];

        Assert.Equal(1, pdo.Execute); // requires ack, not yet acked
        Assert.Equal(123, pdo.Parameter);
        Assert.Equal(456, pdo.Velocity);
        Assert.Equal((ushort)789, pdo.Acceleration);
        Assert.Equal((ushort)321, pdo.Deceleration);
        Assert.Equal("TEST", GetAsciiString(pdo.Command));

        var markAcked = PendingCommandType.GetMethod("MarkAcked", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException("MarkAcked method not found.");
        markAcked.Invoke(command, Array.Empty<object>());

        parameters[0] = pdo;
        apply.Invoke(command, parameters);
        pdo = (SoemShim.DriveRxPDO)parameters[0];
        Assert.Equal(0, pdo.Execute); // command acknowledged clears execute bit
    }

    private static string GetAsciiString(byte[] buffer)
    {
        var terminator = Array.IndexOf(buffer, (byte)0);
        var length = terminator >= 0 ? terminator : buffer.Length;
        return System.Text.Encoding.ASCII.GetString(buffer, 0, length);
    }
}

public sealed class DriveStateFormatterTests
{
    [Fact]
    public void ToBitMaskCombinesActiveFlags()
    {
        var tx = new SoemShim.DriveTxPDO
        {
            AmplifiersEnabled = 1,
            EndStop = 1,
            ExecuteAck = 1,
            ErrorLimit = 1
        };

        var mask = DriveStateFormatter.ToBitMask(tx);
        Assert.Equal((1u << 0) | (1u << 1) | (1u << 19) | (1u << 16), mask);
    }
}
