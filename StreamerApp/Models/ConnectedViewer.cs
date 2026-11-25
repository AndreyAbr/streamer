using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace StreamerApp.Models;

public class ConnectedViewer : INotifyPropertyChanged
{
    private int _pingMs;
    private bool _isHandRaised;

    private string _nickname = "Гость";
    private string _ipAddress = "127.0.0.1";

    public string ConnectionId { get; set; } = string.Empty;

    public string Nickname
    {
        get => _nickname;
        set
        {
            if (_nickname != value)
            {
                _nickname = value;
                OnPropertyChanged();
            }
        }
    }

    public string IpAddress
    {
        get => _ipAddress;
        set
        {
            if (_ipAddress != value)
            {
                _ipAddress = value;
                OnPropertyChanged();
            }
        }
    }

    public DateTime ConnectedAt { get; set; } = DateTime.Now;
    public DateTime LastHeartbeat { get; set; } = DateTime.Now;

    public int PingMs
    {
        get => _pingMs;
        set
        {
            if (_pingMs != value)
            {
                _pingMs = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsHandRaised
    {
        get => _isHandRaised;
        set
        {
            if (_isHandRaised != value)
            {
                _isHandRaised = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusDisplay));
            }
        }
    }

    public string StatusDisplay => IsHandRaised ? "Поднята рука" : "Зритель";

    public string ConnectedTimeDisplay => ConnectedAt.ToString("HH:mm:ss");

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public class ChatMessage
{
    public string Sender { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public bool IsHost { get; set; }
    public string TimeDisplay => Timestamp.ToString("HH:mm:ss");
}

public record ViewerDto(string ConnectionId, string Nickname, bool IsHandRaised);

public record ChatMessageDto(string Sender, string Text, string Timestamp, bool IsHost);

