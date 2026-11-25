using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using SIPSorcery.Net;
using StreamerApp.Models;
using StreamerApp.Services;

namespace StreamerApp.Hubs;

public class StreamHub : Hub
{
    public record JoinResult(
        bool Success,
        string? Error,
        bool IsBroadcasting,
        string? StreamTitle,
        bool IsPaused = false,
        List<ViewerDto>? Viewers = null,
        List<ChatMessageDto>? ChatHistory = null
    );

    public record IceCandidateDto(string Candidate, string? SdpMid, ushort SdpMLineIndex);

    public async Task<JoinResult> JoinRoom(string code, string nickname)
    {
        var state = BroadcastStateService.Instance;
        var cleanCode = code?.Trim().Replace(" ", "").Replace("-", "");
        var targetCode = state.Settings.RoomCode?.Trim().Replace(" ", "").Replace("-", "");

        if (!string.Equals(cleanCode, targetCode, StringComparison.OrdinalIgnoreCase))
        {
            return new JoinResult(false, "Неверный код подключения", false, null, false);
        }

        var httpContext = Context.GetHttpContext();
        var ip = httpContext?.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
        if (ip == "::1") ip = "127.0.0.1";

        state.AddOrUpdateViewer(Context.ConnectionId, nickname, ip);
        Context.Items["IsAuthenticated"] = true;

        // Notify other viewers about new participant
        await Clients.Others.SendAsync("ViewerJoined", Context.ConnectionId, nickname);

        var currentViewers = state.GetAllViewersDto();
        var recentChat = state.GetRecentChatMessagesDto(50);

        return new JoinResult(
            true,
            null,
            state.IsBroadcasting,
            "Трансляция экрана",
            state.IsPaused,
            currentViewers,
            recentChat
        );
    }

    public async Task<string> SendOffer(string offerSdp)
    {
        if (!IsClientAuthenticated())
        {
            throw new HubException("Необходима авторизация в комнате");
        }

        var httpContext = Context.GetHttpContext();
        var clientIp = httpContext?.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
        if (clientIp == "::1") clientIp = "127.0.0.1";

        // Only replace .local if clientIp is a standard IPv4 address
        if (!string.IsNullOrEmpty(clientIp) && !clientIp.Contains(':') && offerSdp != null && offerSdp.Contains(".local"))
        {
            offerSdp = System.Text.RegularExpressions.Regex.Replace(offerSdp, @"[a-zA-Z0-9\-]+\.local", clientIp);
        }

        var manager = WebRtcStreamingManager.Instance;
        var answerSdp = await manager.HandleOfferAsync(Context.ConnectionId, offerSdp ?? string.Empty);
        return answerSdp;
    }

    public void SendIceCandidate(IceCandidateDto candidate)
    {
        if (!IsClientAuthenticated()) return;
        if (candidate == null || string.IsNullOrEmpty(candidate.Candidate)) return;

        string candStr = candidate.Candidate;

        var httpContext = Context.GetHttpContext();
        var clientIp = httpContext?.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
        if (clientIp == "::1") clientIp = "127.0.0.1";

        if (candStr.Contains(".local") && !string.IsNullOrEmpty(clientIp) && !clientIp.Contains(':'))
        {
            candStr = System.Text.RegularExpressions.Regex.Replace(candStr, @"[a-zA-Z0-9\-]+\.local", clientIp);
        }

        var init = new RTCIceCandidateInit
        {
            candidate = candStr,
            sdpMid = candidate.SdpMid,
            sdpMLineIndex = candidate.SdpMLineIndex
        };

        WebRtcStreamingManager.Instance.HandleIceCandidate(Context.ConnectionId, init);
    }

    public long Ping(long clientTimestamp)
    {
        if (!IsClientAuthenticated()) return 0;

        long serverNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        int rtt = (int)Math.Max(1, serverNow - clientTimestamp);
        BroadcastStateService.Instance.UpdatePing(Context.ConnectionId, rtt);
        return serverNow;
    }

    public async Task SendChatMessage(string text)
    {
        if (!IsClientAuthenticated()) return;
        if (string.IsNullOrWhiteSpace(text)) return;

        var viewer = BroadcastStateService.Instance.GetViewer(Context.ConnectionId);
        string sender = viewer?.Nickname ?? "Зритель";

        BroadcastStateService.Instance.AddChatMessage(sender, text.Trim(), false);

        await Clients.All.SendAsync("ReceiveChatMessage", new
        {
            sender = sender,
            text = text.Trim(),
            timestamp = DateTime.Now.ToString("HH:mm:ss"),
            isHost = false
        });
    }

    public async Task RaiseHand(bool isRaised)
    {
        if (!IsClientAuthenticated()) return;

        var viewer = BroadcastStateService.Instance.GetViewer(Context.ConnectionId);
        string sender = viewer?.Nickname ?? "Зритель";

        BroadcastStateService.Instance.SetHandRaised(Context.ConnectionId, isRaised);

        await Clients.All.SendAsync("UserHandRaised", Context.ConnectionId, sender, isRaised);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        WebRtcStreamingManager.Instance.ClosePeer(Context.ConnectionId);
        BroadcastStateService.Instance.RemoveViewer(Context.ConnectionId);
        await Clients.Others.SendAsync("ViewerLeft", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    private bool IsClientAuthenticated()
    {
        if (Context.Items.TryGetValue("IsAuthenticated", out var auth) && auth is true)
        {
            return true;
        }

        return BroadcastStateService.Instance.GetViewer(Context.ConnectionId) != null;
    }
}
