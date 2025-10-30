using XeryonEtherCAT.Core.Internal.Soem;
using XeryonEtherCAT.Core.Models;

namespace XeryonEtherCAT.App.ViewModels;

public sealed class DriveStatusViewModel : ViewModelBase
{
    private int _position;
    private SoemShim.DriveTxPDO _status;
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

    public SoemShim.DriveTxPDO Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                StatusText = DriveStateFormatter.Describe(value);
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public void Update(in SoemShim.DriveTxPDO status)
    {
        Position = status.ActualPosition;
        Status = status;
    }
}
