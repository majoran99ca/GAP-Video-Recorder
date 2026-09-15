"""Optional full-window smoke test using two synthetic camera images."""

import tempfile
from pathlib import Path

from PySide6.QtCore import QTimer, QRectF, Qt
from PySide6.QtGui import QImage
from PySide6.QtWidgets import QApplication

import sys
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import app as recorder_app  # noqa: E402


def main() -> int:
    application = QApplication([])
    with tempfile.TemporaryDirectory() as folder:
        output = Path(folder) / "window.mp4"
        recorder_app.video_file = lambda: output
        window = recorder_app.MainWindow()
        window.canvas.tiles = [
            recorder_app.Tile(b"a", QRectF(0, 0, 0.5, 1)),
            recorder_app.Tile(b"b", QRectF(0.5, 0, 0.5, 1)),
        ]
        red = QImage(320, 180, QImage.Format.Format_RGB32)
        red.fill(Qt.GlobalColor.red)
        blue = QImage(320, 180, QImage.Format.Format_RGB32)
        blue.fill(Qt.GlobalColor.blue)
        fake_worker = type("Worker", (), {"stop": lambda self: None, "wait": lambda self, ms: None})()
        window.feeds = {
            b"a": type("Feed", (), {"image": red, "worker": fake_worker})(),
            b"b": type("Feed", (), {"image": blue, "worker": fake_worker})(),
        }
        error_messages = []
        window.show()
        window.start_recording()
        QTimer.singleShot(1100, window.stop_recording)
        QTimer.singleShot(12000, application.quit)

        def finished(state) -> None:
            if state == recorder_app.QMediaRecorder.RecorderState.StoppedState:
                QTimer.singleShot(100, application.quit)

        if window.recorder is not None:
            window.recorder.recorderStateChanged.connect(finished)
            window.recorder.errorOccurred.connect(lambda error, message: (error_messages.append(message), application.quit()))
        application.exec()
        assert not error_messages, error_messages
        assert window.recorder is None, "Recorder did not finalize"
        assert output.exists() and output.stat().st_size > 1000
        window.close()
        print(f"Window smoke test passed: {output.stat().st_size} bytes")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
