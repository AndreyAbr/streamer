using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using StreamerApp.Models;
using StreamerApp.Server;
using StreamerApp.Services;

namespace StreamerApp;

public partial class MainWindow : Window
{
    private readonly ScreenCaptureService _screenCapture = new();
    private readonly AudioCaptureService _audioCapture = new();
    private readonly DispatcherTimer _uiTimer = new();
    private string _localIp = "127.0.0.1";
    private int _port = 5000;
    private DateTime _broadcastStartTime;
    private int _unreadChatCount = 0;

    public MainWindow()
    {
        InitializeComponent();

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // 1. Determine local IP and Port
        _localIp = EmbeddedWebServer.GetLocalIpAddress();
        _port = BroadcastStateService.Instance.Settings.Port;
        ServerUrlText.Text = $"http://{_localIp}:{_port}";
        StatusNetworkText.Text = $"LAN {_localIp} : {_port}";

        // 2. Bind Room Code
        TxtRoomCode.Text = BroadcastStateService.Instance.Settings.RoomCode;

        // 3. Bind DataGrid and Chat
        DgViewers.ItemsSource = BroadcastStateService.Instance.ConnectedViewers;
        LstChat.ItemsSource = BroadcastStateService.Instance.ChatMessages;

        // Auto-scroll chat & unread badge tracking
        BroadcastStateService.Instance.ChatMessages.CollectionChanged += (s, ev) =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (PanelChatView.Visibility == Visibility.Visible)
                {
                    if (LstChat.Items.Count > 0)
                    {
                        LstChat.ScrollIntoView(LstChat.Items[LstChat.Items.Count - 1]);
                    }
                }
                else
                {
                    _unreadChatCount++;
                    TxtChatUnreadCount.Text = _unreadChatCount.ToString();
                    BadgeChatUnread.Visibility = Visibility.Visible;
                }
            });
        };

        // 4. Populate Monitors
        var monitors = ScreenCaptureService.GetMonitors();
        CmbMonitors.ItemsSource = monitors;
        if (monitors.Count > 0)
        {
            CmbMonitors.SelectedIndex = 0;
        }

        // 5. Wire up capture events
        _screenCapture.OnFrameCaptured += (width, height, buffer) =>
        {
            int fps = BroadcastStateService.Instance.Settings.TargetFps;
            WebRtcStreamingManager.Instance.SendVideoFrame(width, height, buffer, fps);
        };

        _screenCapture.OnFpsUpdated += (fps) =>
        {
            BroadcastStateService.Instance.CurrentFps = fps;
        };

        _screenCapture.OnJpegFrameCaptured += async (jpegBytes) =>
        {
            await EmbeddedWebServer.Instance.BroadcastLiveFrameAsync(jpegBytes);
        };

        _audioCapture.OnAudioSample += (audioSample) =>
        {
            if (BroadcastStateService.Instance.Settings.IsAudioEnabled)
            {
                WebRtcStreamingManager.Instance.SendAudioSample(audioSample);
            }
        };

        // 6. Start Embedded Web Server
        try
        {
            await EmbeddedWebServer.Instance.StartAsync(_port);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось запустить веб-сервер на порту {_port}: {ex.Message}", "Ошибка запуска сервера", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        // 7. Start UI Stats Timer
        _uiTimer.Interval = TimeSpan.FromSeconds(1);
        _uiTimer.Tick += UiTimer_Tick;
        _uiTimer.Start();

        // 8. Hook external broadcast triggers
        BroadcastStateService.Instance.OnStartBroadcastRequested += async () =>
        {
            await Dispatcher.InvokeAsync(StartBroadcastAsync);
        };
        BroadcastStateService.Instance.OnStopBroadcastRequested += async () =>
        {
            await Dispatcher.InvokeAsync(StopBroadcastAsync);
        };
        BroadcastStateService.Instance.OnPauseBroadcastRequested += async (isPaused) =>
        {
            await Dispatcher.InvokeAsync(() => UpdatePauseUI(isPaused));
            await EmbeddedWebServer.Instance.NotifyStreamPaused(isPaused);
        };
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _uiTimer.Stop();
        _screenCapture.Dispose();
        _audioCapture.Dispose();
        WebRtcStreamingManager.Instance.Dispose();
        _ = EmbeddedWebServer.Instance.StopAsync();
    }

    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        var state = BroadcastStateService.Instance;

        // Update Stats Bar
        StatusFps.Text = _screenCapture.CurrentFps.ToString();
        StatusBackend.Text = state.ActiveBackendName;
        StatusBitrate.Text = $"{state.OutgoingBitrateKbps} Кбит/с";
        StatusPeers.Text = WebRtcStreamingManager.Instance.ActiveConnectionsCount.ToString();

        // Update Header Counters
        int viewers = state.ConnectedViewers.Count;
        int hands = state.ConnectedViewers.Count(v => v.IsHandRaised);

        TxtViewersCount.Text = viewers.ToString();
        TxtHandsCount.Text = hands.ToString();

        // Update Tab badges
        TxtBadgeViewers.Text = viewers.ToString();
        TxtViewersDetailCount.Text = $"Всего: {viewers}";
        BadgeHandsIndicator.Visibility = hands > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Update Broadcast Timer
        if (state.IsBroadcasting)
        {
            var elapsed = DateTime.UtcNow - _broadcastStartTime;
            TxtBroadcastDuration.Text = elapsed.ToString(@"hh\:mm\:ss");
        }
        else
        {
            TxtBroadcastDuration.Text = "00:00:00";
        }
    }

    private async void BtnStartStopBroadcast_Click(object sender, RoutedEventArgs e)
    {
        if (!BroadcastStateService.Instance.IsBroadcasting)
        {
            await StartBroadcastAsync();
        }
        else
        {
            await StopBroadcastAsync();
        }
    }

    private async void BtnPauseBroadcast_Click(object sender, RoutedEventArgs e)
    {
        var state = BroadcastStateService.Instance;
        if (!state.IsBroadcasting) return;

        bool newPaused = !state.IsPaused;
        state.IsPaused = newPaused;
        await EmbeddedWebServer.Instance.NotifyStreamPaused(newPaused);
        UpdatePauseUI(newPaused);
    }

    private void UpdatePauseUI(bool isPaused)
    {
        if (isPaused)
        {
            BtnPauseBroadcast.Content = "Возобновить";
            BtnPauseBroadcast.Style = (Style)FindResource("SuccessButton");
            StatusBadgeDot.Fill = (SolidColorBrush)FindResource("WarningAmber");
            StatusBadgeText.Text = "Трансляция на паузе";
            StatusBadgeText.Foreground = (SolidColorBrush)FindResource("WarningAmber");
        }
        else
        {
            BtnPauseBroadcast.Content = "Пауза";
            BtnPauseBroadcast.Style = (Style)FindResource("GrayButton");
            StatusBadgeDot.Fill = (SolidColorBrush)FindResource("SuccessGreen");
            StatusBadgeText.Text = "Трансляция активна";
            StatusBadgeText.Foreground = (SolidColorBrush)FindResource("SuccessGreen");
        }
    }

    public async Task StartBroadcastAsync()
    {
        var state = BroadcastStateService.Instance;
        if (state.IsBroadcasting) return;

        // Start Broadcast
        ApplyCurrentSettings();

        _screenCapture.Start();

        if (state.Settings.IsAudioEnabled)
        {
            _audioCapture.IsEnabled = true;
            _audioCapture.Start();
        }

        _broadcastStartTime = DateTime.UtcNow;
        state.IsBroadcasting = true;
        state.IsPaused = false;
        await EmbeddedWebServer.Instance.NotifyStreamStarted();

        // Update UI State
        BtnStartStopBroadcast.Content = "Остановить трансляцию";
        BtnStartStopBroadcast.Style = (Style)FindResource("DangerButton");

        BtnPauseBroadcast.IsEnabled = true;
        BtnPauseBroadcast.Content = "Пауза";
        BtnPauseBroadcast.Style = (Style)FindResource("GrayButton");

        StatusBadgeDot.Fill = (SolidColorBrush)FindResource("SuccessGreen");
        StatusBadgeText.Text = "Трансляция активна";
        StatusBadgeText.Foreground = (SolidColorBrush)FindResource("SuccessGreen");
    }

    public async Task StopBroadcastAsync()
    {
        var state = BroadcastStateService.Instance;
        if (!state.IsBroadcasting) return;

        // Stop Broadcast
        _screenCapture.Stop();
        _audioCapture.Stop();
        WebRtcStreamingManager.Instance.CloseAllPeers();

        state.IsBroadcasting = false;
        state.IsPaused = false;
        await EmbeddedWebServer.Instance.NotifyStreamStopped();

        // Update UI State
        BtnStartStopBroadcast.Content = "Начать трансляцию";
        BtnStartStopBroadcast.Style = (Style)FindResource("SuccessButton");

        BtnPauseBroadcast.IsEnabled = false;
        BtnPauseBroadcast.Content = "Пауза";
        BtnPauseBroadcast.Style = (Style)FindResource("GrayButton");

        StatusBadgeDot.Fill = (SolidColorBrush)FindResource("TextMuted");
        StatusBadgeText.Text = "Трансляция остановлена";
        StatusBadgeText.Foreground = (SolidColorBrush)FindResource("TextSecondary");

        TxtBroadcastDuration.Text = "00:00:00";
    }

    private void ApplyCurrentSettings()
    {
        var settings = BroadcastStateService.Instance.Settings;

        settings.SelectedMonitorIndex = CmbMonitors.SelectedIndex >= 0 ? CmbMonitors.SelectedIndex : 0;

        if (CmbResolution.SelectedItem is ComboBoxItem resItem && resItem.Tag is string resStr)
        {
            settings.Resolution = resStr;
        }

        if (CmbFps.SelectedItem is ComboBoxItem fpsItem && int.TryParse(fpsItem.Tag?.ToString(), out int fpsVal))
        {
            settings.TargetFps = fpsVal;
        }

        settings.BitrateKbps = (int)SliderBitrate.Value;
        bool audioWasEnabled = settings.IsAudioEnabled;
        settings.IsAudioEnabled = ChkAudioEnabled.IsChecked == true;

        if (CmbCaptureEngine.SelectedItem is ComboBoxItem engItem && engItem.Tag is string engStr)
        {
            settings.CaptureEngine = engStr;
        }

        settings.ShowCursor = ChkShowCursor.IsChecked == true;

        _screenCapture.Configure(settings.SelectedMonitorIndex, settings.Resolution, settings.TargetFps, settings.CaptureEngine, settings.ShowCursor);
        _audioCapture.IsEnabled = settings.IsAudioEnabled;

        // Reactive audio start / stop if broadcast is already running
        if (BroadcastStateService.Instance.IsBroadcasting)
        {
            if (settings.IsAudioEnabled && !audioWasEnabled)
            {
                _audioCapture.Start();
            }
            else if (!settings.IsAudioEnabled && audioWasEnabled)
            {
                _audioCapture.Stop();
            }
        }
    }

    private void BtnGenCode_Click(object sender, RoutedEventArgs e)
    {
        string newCode = StreamSettings.GenerateRoomCode();
        TxtRoomCode.Text = newCode;
        BroadcastStateService.Instance.Settings.RoomCode = newCode;
    }

    private void TxtRoomCode_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (TxtRoomCode != null)
        {
            BroadcastStateService.Instance.Settings.RoomCode = TxtRoomCode.Text.Trim();
        }
    }

    private async void BtnCopyLink_Click(object sender, RoutedEventArgs e)
    {
        string url = $"http://{_localIp}:{_port}";
        try { Clipboard.SetText(url); } catch { }
        BtnCopyLink.Content = "Скопировано!";
        await Task.Delay(2000);
        BtnCopyLink.Content = "Скопировать";
    }

    private async void BtnCopyCodeLink_Click(object sender, RoutedEventArgs e)
    {
        string code = TxtRoomCode.Text.Trim();
        string url = $"http://{_localIp}:{_port}/?code={code}";
        try { Clipboard.SetText(url); } catch { }
        BtnCopyCodeLink.Content = "Скопировано!";
        await Task.Delay(2000);
        BtnCopyCodeLink.Content = "Копировать ссылку с кодом";
    }

    private async void BtnKickViewer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string connectionId)
        {
            var viewer = BroadcastStateService.Instance.GetViewer(connectionId);
            string name = viewer?.Nickname ?? "зрителя";

            var result = MessageBox.Show($"Вы действительно хотите отключить {name}?", "Отключение зрителя", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                await EmbeddedWebServer.Instance.KickViewerAsync(connectionId);
            }
        }
    }

    private async void BtnSendHostMessage_Click(object sender, RoutedEventArgs e)
    {
        await SendHostMessage();
    }

    private async void TxtHostMessage_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await SendHostMessage();
        }
    }

    private async Task SendHostMessage()
    {
        string text = TxtHostMessage.Text.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;

        BroadcastStateService.Instance.AddChatMessage("Ведущий", text, isHost: true);
        await EmbeddedWebServer.Instance.SendHostChatMessage(text);

        TxtHostMessage.Text = "";
        if (LstChat.Items.Count > 0)
        {
            LstChat.ScrollIntoView(LstChat.Items[LstChat.Items.Count - 1]);
        }
    }

    private void CmbMonitors_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) ApplyCurrentSettings();
    }

    private void CmbResolution_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) ApplyCurrentSettings();
    }

    private void CmbFps_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) ApplyCurrentSettings();
    }

    private void SliderBitrate_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtBitrateValue != null)
        {
            TxtBitrateValue.Text = $"{(int)SliderBitrate.Value} Кбит/с";
        }
        if (IsLoaded) ApplyCurrentSettings();
    }

    private void ChkAudioEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) ApplyCurrentSettings();
    }

    private void CmbCaptureEngine_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) ApplyCurrentSettings();
    }

    private void ChkShowCursor_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) ApplyCurrentSettings();
    }

    private void BtnTabChat_Click(object sender, RoutedEventArgs e)
    {
        PanelChatView.Visibility = Visibility.Visible;
        PanelViewersView.Visibility = Visibility.Collapsed;

        BtnTabChat.Background = (SolidColorBrush)FindResource("AccentBlue");
        BtnTabChat.Foreground = Brushes.White;

        BtnTabViewers.Background = Brushes.Transparent;
        BtnTabViewers.Foreground = (SolidColorBrush)FindResource("TextSecondary");

        _unreadChatCount = 0;
        BadgeChatUnread.Visibility = Visibility.Collapsed;

        if (LstChat.Items.Count > 0)
        {
            LstChat.ScrollIntoView(LstChat.Items[LstChat.Items.Count - 1]);
        }
    }

    private void BtnTabViewers_Click(object sender, RoutedEventArgs e)
    {
        PanelChatView.Visibility = Visibility.Collapsed;
        PanelViewersView.Visibility = Visibility.Visible;

        BtnTabViewers.Background = (SolidColorBrush)FindResource("AccentBlue");
        BtnTabViewers.Foreground = Brushes.White;

        BtnTabChat.Background = Brushes.Transparent;
        BtnTabChat.Foreground = (SolidColorBrush)FindResource("TextSecondary");
    }

    private void BtnToggleSettings_Click(object sender, RoutedEventArgs e)
    {
        if (PanelSettingsContent.Visibility == Visibility.Visible)
        {
            PanelSettingsContent.Visibility = Visibility.Collapsed;
            TxtSettingsChevron.Text = "▼";
        }
        else
        {
            PanelSettingsContent.Visibility = Visibility.Visible;
            TxtSettingsChevron.Text = "▲";
        }
    }
}