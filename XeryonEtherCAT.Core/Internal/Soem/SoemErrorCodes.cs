namespace XeryonEtherCAT.Core.Internal.Soem;

/// <summary>
/// Error codes returned by the soem_shim native library.
/// These match the definitions in soem_shim.h
/// </summary>
internal static class SoemErrorCodes
{
    /// <summary>
    /// Invalid arguments passed to native function.
    /// </summary>
    public const int SOEM_ERR_BAD_ARGS = -13;

    /// <summary>
    /// Failed to send process data (ecx_send_processdata failed).
    /// </summary>
    public const int SOEM_ERR_SEND_FAIL = -11;

    /// <summary>
    /// Failed to receive process data (ecx_receive_processdata failed).
    /// </summary>
    public const int SOEM_ERR_RECV_FAIL = -12;

    /// <summary>
    /// Working counter below expected value (communication issue with one or more slaves).
    /// </summary>
    public const int SOEM_ERR_WKC_LOW = -10;

    /// <summary>
    /// Checks if the error code indicates a fatal communication error.
    /// </summary>
    public static bool IsFatalError(int errorCode)
    {
        return errorCode switch
        {
            SOEM_ERR_BAD_ARGS => true,
            SOEM_ERR_SEND_FAIL => true,
            SOEM_ERR_RECV_FAIL => true,
            _ => false
        };
    }

    /// <summary>
    /// Checks if the error code indicates a recoverable condition.
    /// </summary>
    public static bool IsRecoverableError(int errorCode)
    {
        return errorCode == SOEM_ERR_WKC_LOW;
    }

    /// <summary>
    /// Gets a human-readable description of the error code.
    /// </summary>
    public static string GetErrorDescription(int errorCode)
    {
        return errorCode switch
        {
            SOEM_ERR_BAD_ARGS => "Invalid arguments passed to SOEM function",
            SOEM_ERR_SEND_FAIL => "Failed to send EtherCAT process data",
            SOEM_ERR_RECV_FAIL => "Failed to receive EtherCAT process data",
            SOEM_ERR_WKC_LOW => "Working counter below expected (slave communication issue)",
            _ when errorCode < 0 => $"Unknown SOEM error code: {errorCode}",
            _ => "Success"
        };
    }
}