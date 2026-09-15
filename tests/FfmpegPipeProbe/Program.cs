using System.Runtime.InteropServices;
using System.IO;
using GapVideoRecorder;
using OpenCvSharp;

var outputPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg-pipe-probe.mp4");
using (var encoder = new FfmpegEncoder(outputPath, 320, 180, 30, 24))
using (var frame = new Mat(180, 320, MatType.CV_8UC3))
{
    for (var i = 0; i < 60; i++)
    {
        Cv2.Randu(frame, Scalar.Black, Scalar.White);
        var bytes = new byte[checked((int)(frame.Total() * frame.ElemSize()))];
        Marshal.Copy(frame.Data, bytes, 0, bytes.Length);
        encoder.Write(bytes);
    }
    encoder.Finish();
}
var result = new FileInfo(outputPath);
if (!result.Exists || result.Length < 1000)
    throw new InvalidOperationException("FFmpeg pipe encoder wrote no usable MP4.");
Console.WriteLine($"FFmpeg pipe probe passed: {result.FullName} ({result.Length} bytes)");
