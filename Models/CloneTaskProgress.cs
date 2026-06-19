using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Replicore.Models;

public class CloneTaskProgress : INotifyPropertyChanged
{
    private string _targetDiskName = "";
    private int _percent = 0;
    private string _speed = "0.0 MB/s";
    private string _eta = "estimating...";
    private string _status = "Pending";
    private string _statusColor = "#71717A"; // Muted gray

    public string TargetDiskName
    {
        get => _targetDiskName;
        set { _targetDiskName = value; OnPropertyChanged(); }
    }

    public int Percent
    {
        get => _percent;
        set { _percent = value; OnPropertyChanged(); }
    }

    public string Speed
    {
        get => _speed;
        set { _speed = value; OnPropertyChanged(); }
    }

    public string Eta
    {
        get => _eta;
        set { _eta = value; OnPropertyChanged(); }
    }

    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); }
    }

    public string StatusColor
    {
        get => _statusColor;
        set { _statusColor = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string name = null!) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
