using System;

namespace StreamerApp.Models;

public class StreamSettings
{
    public int SelectedMonitorIndex { get; set; } = 0;
    public string Resolution { get; set; } = "1920x1080"; // "Original", "1920x1080", "1280x720"
    public int TargetFps { get; set; } = 30; // 15, 30, 60
    public int BitrateKbps { get; set; } = 2500; // 500 - 8000
    public bool IsAudioEnabled { get; set; } = true;
    public string CaptureEngine { get; set; } = "DXGI"; // "DXGI", "GDI"
    public bool ShowCursor { get; set; } = true;
    public int Port { get; set; } = 5000;
    public string RoomCode { get; set; } = GenerateRoomCode();

    public static string GenerateRoomCode()
    {
        int part1 = Random.Shared.Next(100, 999);
        int part2 = Random.Shared.Next(100, 999);
        return $"{part1}-{part2}";
    }

    public (int width, int height) GetTargetDimensions(int sourceWidth, int sourceHeight)
    {
        return Resolution switch
        {
            "1280x720" => (1280, 720),
            "1920x1080" => (1920, 1080),
            _ => (sourceWidth, sourceHeight)
        };
    }
}
