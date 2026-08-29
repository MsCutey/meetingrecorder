namespace MeetingRecorder.Core.Recording;

internal static class BlackFrameAsset
{
    public static string EnsureCreated(string assetsDirectory)
    {
        Directory.CreateDirectory(assetsDirectory);
        var path = Path.Combine(assetsDirectory, "audio-only-black-frame.bmp");
        if (File.Exists(path)) return path;

        const int width = 64;
        const int height = 64;
        const int bytesPerPixel = 3;
        var pixelBytes = width * height * bytesPerPixel;
        var data = new byte[54 + pixelBytes];
        data[0] = (byte)'B';
        data[1] = (byte)'M';
        BitConverter.GetBytes(data.Length).CopyTo(data, 2);
        BitConverter.GetBytes(54).CopyTo(data, 10);
        BitConverter.GetBytes(40).CopyTo(data, 14);
        BitConverter.GetBytes(width).CopyTo(data, 18);
        BitConverter.GetBytes(height).CopyTo(data, 22);
        BitConverter.GetBytes((short)1).CopyTo(data, 26);
        BitConverter.GetBytes((short)24).CopyTo(data, 28);
        BitConverter.GetBytes(pixelBytes).CopyTo(data, 34);

        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, data);
        File.Move(temporary, path, true);
        return path;
    }
}
