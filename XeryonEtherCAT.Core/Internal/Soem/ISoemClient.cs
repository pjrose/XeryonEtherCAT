using System;
using System.Text;

namespace XeryonEtherCAT.Core.Internal.Soem;

public interface ISoemClient : IDisposable
{
    IntPtr Initialize(string iface);

    void Shutdown(IntPtr handle);

    int GetSlaveCount(IntPtr handle);

    int GetExpectedRxBytes();

    int GetExpectedTxBytes();

    int WriteRxPdo(IntPtr handle, int slaveIndex, ref SoemShim.DriveRxPDO pdo);

    int ReadTxPdo(IntPtr handle, int slaveIndex, out SoemShim.DriveTxPDO pdo);

    int ExchangeProcessData(IntPtr handle, int timeoutUs);

    int GetHealth(IntPtr handle, out SoemShim.SoemHealth health);

    int TryRecover(IntPtr handle, int timeoutMs);

    string DrainErrorList(IntPtr handle, StringBuilder? buffer = null);
}
