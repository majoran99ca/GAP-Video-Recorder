"""Optional Windows smoke test for the actual H.264 recording pipeline."""

import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
from pathlib import Path

from PySide6.QtCore import QTimer, QUrl, QSize, Qt
from PySide6.QtGui import QImage
from PySide6.QtMultimedia import QMediaCaptureSession, QMediaFormat, QMediaRecorder, QVideoFrame, QVideoFrameInput
from PySide6.QtWidgets import QApplication

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from app import Tile, compose_frame  # noqa: E402
from PySide6.QtCore import QRectF  # noqa: E402


def main() -> int:
    app = QApplication([])
    image_a = QImage(64, 64, QImage.Format.Format_RGB32)
    image_a.fill(Qt.GlobalColor.red)
    image_b = QImage(64, 64, QImage.Format.Format_RGB32)
    image_b.fill(Qt.GlobalColor.blue)
    tiles = [Tile(b"a", QRectF(0, 0, 0.5, 1)), Tile(b"b", QRectF(0.5, 0, 0.5, 1))]
    frame = compose_frame(QSize(320, 180), tiles, {b"a": image_a, b"b": image_b})
    assert frame.pixelColor(20, 20).red() > 200
    assert frame.pixelColor(300, 20).blue() > 200

    with tempfile.TemporaryDirectory() as folder:
        output = Path(folder) / "smoke.mp4"
        session = QMediaCaptureSession()
        recorder = QMediaRecorder()
        source = QVideoFrameInput()
        session.setVideoFrameInput(source)
        session.setRecorder(recorder)
        media_format = QMediaFormat()
        media_format.setFileFormat(QMediaFormat.FileFormat.MPEG4)
        media_format.setVideoCodec(QMediaFormat.VideoCodec.H264)
        recorder.setMediaFormat(media_format)
        recorder.setVideoResolution(320, 180)
        recorder.setVideoFrameRate(30)
        recorder.setOutputLocation(QUrl.fromLocalFile(str(output)))
        ticker = QTimer()
        ticker.setInterval(33)
        started = time.monotonic_ns()
        sent = 0
        errors = []

        def send() -> None:
            nonlocal sent
            video_frame = QVideoFrame(frame)
            start_us = (time.monotonic_ns() - started) // 1000
            video_frame.setStartTime(start_us)
            video_frame.setEndTime(start_us + 33_333)
            sent += bool(source.sendVideoFrame(video_frame))

        def state_changed(state) -> None:
            nonlocal started
            if state == QMediaRecorder.RecorderState.RecordingState:
                started = time.monotonic_ns()
                ticker.start()
                QTimer.singleShot(1200, recorder.stop)
            elif state == QMediaRecorder.RecorderState.StoppedState:
                ticker.stop()
                app.quit()

        ticker.timeout.connect(send)
        recorder.recorderStateChanged.connect(state_changed)
        recorder.errorOccurred.connect(lambda error, message: (errors.append(message), app.quit()))
        recorder.record()
        QTimer.singleShot(10000, app.quit)
        app.exec()
        assert not errors, errors
        assert sent > 0, "Encoder accepted no video frames"
        assert output.exists() and output.stat().st_size > 1000, "No MP4 was written"
        ffprobe = os.environ.get("FFPROBE_PATH") or shutil.which("ffprobe")
        if ffprobe:
            result = subprocess.run(
                [ffprobe, "-v", "error", "-show_entries", "stream=codec_type,codec_name", "-of", "json", str(output)],
                check=True, capture_output=True, text=True,
            )
            streams = json.loads(result.stdout)["streams"]
            assert streams == [{"codec_name": "h264", "codec_type": "video"}], streams
        print(f"H.264 smoke test passed: {sent} frames, {output.stat().st_size} bytes")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
