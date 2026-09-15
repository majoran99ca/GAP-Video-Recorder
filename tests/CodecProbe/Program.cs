using OpenCvSharp;

var output = Environment.GetEnvironmentVariable("GAP_CODEC_OUTPUT") ?? Path.Combine(Path.GetTempPath(), $"gap-h264-{Guid.NewGuid():N}.mp4");
var quality = int.TryParse(Environment.GetEnvironmentVariable("GAP_CODEC_QUALITY"), out var requestedQuality) ? requestedQuality : 60;
using (var writer = new VideoWriter(output, FourCC.FromFourChars('a', 'v', 'c', '1'), 30, new Size(640, 360)))
{
    if (!writer.IsOpened())
        throw new InvalidOperationException("Windows H.264 encoder did not open through OpenCV.");
    var applied = writer.Set(VideoWriterProperties.Quality, quality);
    using var frame = new Mat(360, 640, MatType.CV_8UC3);
    for (var i = 0; i < 90; i++)
    {
        Cv2.Randu(frame, Scalar.Black, Scalar.White);
        writer.Write(frame);
    }
    Console.WriteLine($"Quality requested={quality}, accepted={applied}");
}
var info = new FileInfo(output);
if (!info.Exists || info.Length < 1000)
    throw new InvalidOperationException("H.264 encoder wrote no usable MP4.");
Console.WriteLine($"H.264 probe passed: {info.Length} bytes");
if (Environment.GetEnvironmentVariable("GAP_CODEC_OUTPUT") is null)
    File.Delete(output);
