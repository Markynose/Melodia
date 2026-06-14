using System;
using System.ComponentModel;

namespace Melodia.Models;

public class LyricLine : INotifyPropertyChanged
{
    public TimeSpan Time { get; init; }
    public string Text { get; init; } = string.Empty;

    private bool _isCurrent;
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value) return;
            _isCurrent = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
