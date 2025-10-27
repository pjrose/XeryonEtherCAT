using System.Collections.Generic;
using XeryonEtherCAT.Core.Models;

namespace XeryonEtherCAT.App.ViewModels;

public sealed class DriveStatusViewModel : ViewModelBase
{
    private int _position;
    private DriveStatus _status;
    private string _statusText = string.Empty;

    public DriveStatusViewModel(int slave)
    {
        Slave = slave;
    }

    public int Slave { get; }

    public int Position
    {
        get => _position;
        private set => SetProperty(ref _position, value);
    }

    public DriveStatus Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                StatusText = DescribeStatus(value);
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public void Update(int position, DriveStatus status)
    {
        Position = position;
        Status = status;
    }

    private static string DescribeStatus(DriveStatus status)
    {
        if (status == DriveStatus.None)
        {
            return "Idle";
        }

        var parts = new List<string>();
        if (status.HasFlag(DriveStatus.AmplifiersEnabled)) parts.Add("Enabled");
        if (status.HasFlag(DriveStatus.MotorOn)) parts.Add("MotorOn");
        if (status.HasFlag(DriveStatus.ClosedLoop)) parts.Add("ClosedLoop");
        if (status.HasFlag(DriveStatus.PositionReached)) parts.Add("InPos");
        if (status.HasFlag(DriveStatus.Scanning)) parts.Add("Jogging");
        if (status.HasFlag(DriveStatus.ExecuteAck)) parts.Add("Ack");
        if (status.HasFlag(DriveStatus.ErrorLimit)) parts.Add("FollowErr");
        if (status.HasFlag(DriveStatus.SafetyTimeout)) parts.Add("Timeout");
        if (status.HasFlag(DriveStatus.PositionFail)) parts.Add("PositionFail");
        if (status.HasFlag(DriveStatus.EmergencyStop)) parts.Add("E-Stop");
        if (status.HasFlag(DriveStatus.ForceZero)) parts.Add("ForceZero");

        return parts.Count == 0 ? status.ToString() : string.Join(", ", parts);
    }
}
