using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using StreamerApp.Hubs;
using StreamerApp.Services;

namespace StreamerApp.Server;

public class EmbeddedWebServer
{
    private static EmbeddedWebServer? _instance;
    public static EmbeddedWebServer Instance => _instance ??= new EmbeddedWebServer();

    private IHost? _webHost;
    private IHubContext<StreamHub>? _hubContext;
    private Action<string, SIPSorcery.Net.RTCIceCandidateInit>? _iceCandidateHandler;

    public bool IsRunning { get; private set; }

    public async Task StartAsync(int port = 5000)
    {
        if (IsRunning) return;

        var builder = WebApplication.CreateBuilder();

        // Bind Kestrel to all interfaces
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Any, port);
        });

        // Add SignalR and CORS
        builder.Services.AddSignalR();
        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                policy.AllowAnyHeader()
                      .AllowAnyMethod()
                      .SetIsOriginAllowed(_ => true)
                      .AllowCredentials();
            });
        });

        var app = builder.Build();

        app.UseCors();

        // Serve static files from wwwroot
        var wwwrootDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot");
        if (!Directory.Exists(wwwrootDir))
        {
            Directory.CreateDirectory(wwwrootDir);
        }

        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(wwwrootDir),
            RequestPath = ""
        });

        // Map root to index.html
        app.MapGet("/", () => Results.File(Path.Combine(wwwrootDir, "index.html"), "text/html; charset=utf-8"));

        // Map SignalR Hub
        app.MapHub<StreamHub>("/streamhub");

        // Simple health/info endpoint
        app.MapGet("/api/status", () => Results.Json(new
        {
            isBroadcasting = BroadcastStateService.Instance.IsBroadcasting,
            isPaused = BroadcastStateService.Instance.IsPaused,
            viewers = BroadcastStateService.Instance.ViewersCount,
            fps = BroadcastStateService.Instance.CurrentFps,
            bitrateKbps = BroadcastStateService.Instance.OutgoingBitrateKbps,
            activePeers = WebRtcStreamingManager.Instance.ActiveConnectionsCount,
            roomCode = BroadcastStateService.Instance.Settings.RoomCode,
            activeBackend = BroadcastStateService.Instance.ActiveBackendName,
            captureEngine = BroadcastStateService.Instance.Settings.CaptureEngine
        }));

        static bool IsLocalRequest(HttpContext ctx)
        {
            var ip = ctx.Connection.RemoteIpAddress;
            return ip != null && (IPAddress.IsLoopback(ip) || ip.ToString() == "127.0.0.1" || ip.ToString() == "::1");
        }

        app.MapPost("/api/broadcast/start", async (HttpContext ctx) =>
        {
            if (!IsLocalRequest(ctx)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            await BroadcastStateService.Instance.RequestStartBroadcastAsync();
            return Results.Json(new { success = true, isBroadcasting = BroadcastStateService.Instance.IsBroadcasting });
        });

        app.MapPost("/api/broadcast/stop", async (HttpContext ctx) =>
        {
            if (!IsLocalRequest(ctx)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            await BroadcastStateService.Instance.RequestStopBroadcastAsync();
            return Results.Json(new { success = true, isBroadcasting = BroadcastStateService.Instance.IsBroadcasting });
        });

        app.MapPost("/api/broadcast/pause", async (HttpContext ctx) =>
        {
            if (!IsLocalRequest(ctx)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            bool newPaused = !BroadcastStateService.Instance.IsPaused;
            await BroadcastStateService.Instance.RequestTogglePauseAsync(newPaused);
            return Results.Json(new { success = true, isPaused = BroadcastStateService.Instance.IsPaused });
        });

        _webHost = app;
        await _webHost.StartAsync();

        _hubContext = app.Services.GetRequiredService<IHubContext<StreamHub>>();

        // Hook up WebRTC ICE candidate forwarding to clients (with handler cleanup support)
        if (_iceCandidateHandler != null)
        {
            WebRtcStreamingManager.Instance.OnIceCandidateGenerated -= _iceCandidateHandler;
        }

        _iceCandidateHandler = (connId, candidate) =>
        {
            _hubContext?.Clients.Client(connId).SendAsync("ReceiveIceCandidate", new
            {
                candidate = candidate.candidate,
                sdpMid = candidate.sdpMid,
                sdpMLineIndex = candidate.sdpMLineIndex
            });
        };
        WebRtcStreamingManager.Instance.OnIceCandidateGenerated += _iceCandidateHandler;

        IsRunning = true;
    }

    public async Task StopAsync()
    {
        if (!IsRunning || _webHost == null) return;

        if (_iceCandidateHandler != null)
        {
            WebRtcStreamingManager.Instance.OnIceCandidateGenerated -= _iceCandidateHandler;
            _iceCandidateHandler = null;
        }

        try
        {
            await _webHost.StopAsync(TimeSpan.FromSeconds(2));
            _webHost.Dispose();
        }
        catch { }
        finally
        {
            _webHost = null;
            _hubContext = null;
            IsRunning = false;
        }
    }

    public async Task NotifyStreamStarted()
    {
        if (_hubContext != null)
        {
            await _hubContext.Clients.All.SendAsync("StreamStarted");
        }
    }

    public async Task NotifyStreamStopped()
    {
        if (_hubContext != null)
        {
            await _hubContext.Clients.All.SendAsync("StreamStopped");
        }
    }

    public async Task NotifyStreamPaused(bool isPaused)
    {
        if (_hubContext != null)
        {
            await _hubContext.Clients.All.SendAsync("StreamPaused", isPaused);
        }
    }

    public async Task BroadcastLiveFrameAsync(byte[] jpegBytes)
    {
        if (_hubContext != null && jpegBytes != null && jpegBytes.Length > 0)
        {
            try
            {
                string base64 = Convert.ToBase64String(jpegBytes);
                await _hubContext.Clients.All.SendAsync("LiveFrame", base64);
            }
            catch { }
        }
    }

    public async Task SendHostChatMessage(string text)
    {
        if (_hubContext != null)
        {
            await _hubContext.Clients.All.SendAsync("ReceiveChatMessage", new
            {
                sender = "Ведущий",
                text = text,
                timestamp = DateTime.Now.ToString("HH:mm:ss"),
                isHost = true
            });
        }
    }

    public async Task KickViewerAsync(string connectionId)
    {
        if (_hubContext != null)
        {
            await _hubContext.Clients.Client(connectionId).SendAsync("ViewerKicked");
        }
        WebRtcStreamingManager.Instance.ClosePeer(connectionId);
        BroadcastStateService.Instance.RemoveViewer(connectionId);
    }

    public static string GetLocalIpAddress()
    {
        try
        {
            var interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                            n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                .ToList();

            // First priority: physical network adapters, excluding VPN/TUN/virtual adapters
            foreach (var net in interfaces)
            {
                string desc = (net.Name + " " + net.Description).ToLowerInvariant();
                if (desc.Contains("tun") || desc.Contains("tap") || desc.Contains("vpn") ||
                    desc.Contains("singbox") || desc.Contains("virtual") || desc.Contains("docker") ||
                    desc.Contains("wsl") || desc.Contains("hyper-v") || desc.Contains("pseudo"))
                {
                    continue;
                }

                var props = net.GetIPProperties();
                foreach (var addr in props.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        var ip = addr.Address.ToString();
                        if (!ip.StartsWith("169.254") && !ip.StartsWith("127."))
                        {
                            return ip;
                        }
                    }
                }
            }

            // Fallback: any IPv4 address that is not link-local
            foreach (var net in interfaces)
            {
                var props = net.GetIPProperties();
                foreach (var addr in props.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        var ip = addr.Address.ToString();
                        if (!ip.StartsWith("169.254") && !ip.StartsWith("127."))
                        {
                            return ip;
                        }
                    }
                }
            }
        }
        catch { }

        return "127.0.0.1";
    }
}
