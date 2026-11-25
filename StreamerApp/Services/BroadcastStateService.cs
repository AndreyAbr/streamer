using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using StreamerApp.Models;

namespace StreamerApp.Services;

public class BroadcastStateService
{
    private static BroadcastStateService? _instance;
    public static BroadcastStateService Instance => _instance ??= new BroadcastStateService();

    public StreamSettings Settings { get; set; } = new();

    public bool IsBroadcasting { get; set; } = false;
    public bool IsPaused { get; set; } = false;

    public ObservableCollection<ConnectedViewer> ConnectedViewers { get; } = new();
    public ObservableCollection<ChatMessage> ChatMessages { get; } = new();

    private readonly ConcurrentDictionary<string, ConnectedViewer> _viewersMap = new();

    public event Action? OnStateChanged;
    public event Action<ConnectedViewer>? OnViewerJoined;
    public event Action<string>? OnViewerLeft;
    public event Action<ChatMessage>? OnChatMessageAdded;
    public event Action<string, bool>? OnHandRaisedChanged;
    public event Func<Task>? OnStartBroadcastRequested;
    public event Func<Task>? OnStopBroadcastRequested;
    public event Func<bool, Task>? OnPauseBroadcastRequested;

    public async Task RequestStartBroadcastAsync()
    {
        if (OnStartBroadcastRequested != null)
        {
            await OnStartBroadcastRequested.Invoke();
        }
    }

    public async Task RequestStopBroadcastAsync()
    {
        if (OnStopBroadcastRequested != null)
        {
            await OnStopBroadcastRequested.Invoke();
        }
    }

    public async Task RequestTogglePauseAsync(bool isPaused)
    {
        IsPaused = isPaused;
        if (OnPauseBroadcastRequested != null)
        {
            await OnPauseBroadcastRequested.Invoke(isPaused);
        }
    }

    public int CurrentFps { get; set; } = 0;
    public int OutgoingBitrateKbps { get; set; } = 0;
    public string ActiveBackendName { get; set; } = "DXGI (GPU)";

    public event Action<string>? OnBackendChanged;

    public void SetActiveBackend(string backend)
    {
        if (ActiveBackendName != backend)
        {
            ActiveBackendName = backend;
            OnBackendChanged?.Invoke(backend);
            OnStateChanged?.Invoke();
        }
    }

    public int ViewersCount => _viewersMap.Count;

    public void AddOrUpdateViewer(string connectionId, string nickname, string ip)
    {
        var viewer = _viewersMap.GetOrAdd(connectionId, id =>
        {
            var v = new ConnectedViewer
            {
                ConnectionId = id,
                Nickname = string.IsNullOrWhiteSpace(nickname) ? "Зритель" : nickname.Trim(),
                IpAddress = ip,
                ConnectedAt = DateTime.Now
            };

            RunOnUI(() => ConnectedViewers.Add(v));
            OnViewerJoined?.Invoke(v);
            return v;
        });

        viewer.Nickname = string.IsNullOrWhiteSpace(nickname) ? viewer.Nickname : nickname.Trim();
        viewer.LastHeartbeat = DateTime.Now;
        OnStateChanged?.Invoke();
    }

    public void RemoveViewer(string connectionId)
    {
        if (_viewersMap.TryRemove(connectionId, out var viewer))
        {
            RunOnUI(() => ConnectedViewers.Remove(viewer));
            OnViewerLeft?.Invoke(connectionId);
            OnStateChanged?.Invoke();
        }
    }

    public void UpdatePing(string connectionId, int pingMs)
    {
        if (_viewersMap.TryGetValue(connectionId, out var viewer))
        {
            viewer.PingMs = pingMs;
            viewer.LastHeartbeat = DateTime.Now;
        }
    }

    public void SetHandRaised(string connectionId, bool isRaised)
    {
        if (_viewersMap.TryGetValue(connectionId, out var viewer))
        {
            viewer.IsHandRaised = isRaised;
            OnHandRaisedChanged?.Invoke(connectionId, isRaised);
        }
    }

    public void AddChatMessage(string sender, string text, bool isHost = false)
    {
        var msg = new ChatMessage
        {
            Sender = sender,
            Text = text,
            Timestamp = DateTime.Now,
            IsHost = isHost
        };

        RunOnUI(() =>
        {
            ChatMessages.Add(msg);
            if (ChatMessages.Count > 200)
            {
                ChatMessages.RemoveAt(0);
            }
        });

        OnChatMessageAdded?.Invoke(msg);
    }

    public ConnectedViewer? GetViewer(string connectionId)
    {
        _viewersMap.TryGetValue(connectionId, out var v);
        return v;
    }

    public List<ViewerDto> GetAllViewersDto()
    {
        return _viewersMap.Values
            .Select(v => new ViewerDto(v.ConnectionId, v.Nickname, v.IsHandRaised))
            .ToList();
    }

    public List<ChatMessageDto> GetRecentChatMessagesDto(int count = 50)
    {
        lock (ChatMessages)
        {
            return ChatMessages
                .TakeLast(count)
                .Select(m => new ChatMessageDto(m.Sender, m.Text, m.TimeDisplay, m.IsHost))
                .ToList();
        }
    }

    private static void RunOnUI(Action action)
    {
        if (Application.Current != null && !Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.BeginInvoke(action);
        }
        else
        {
            action();
        }
    }
}
