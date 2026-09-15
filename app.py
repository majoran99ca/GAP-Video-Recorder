"""A small multi-camera H.264 recorder for Windows."""

from __future__ import annotations

import sys
import threading
import time
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path

import cv2
from PySide6.QtCore import QByteArray, QMimeData, QPointF, QRectF, QSize, Qt, QThread, QTimer, QUrl, Signal
from PySide6.QtGui import QColor, QDrag, QImage, QKeyEvent, QMouseEvent, QPainter, QPen
from PySide6.QtMultimedia import (
    QMediaCaptureSession,
    QMediaDevices,
    QMediaFormat,
    QMediaRecorder,
    QVideoFrame,
    QVideoFrameInput,
)
from PySide6.QtWidgets import (
    QApplication,
    QComboBox,
    QHBoxLayout,
    QLabel,
    QListWidget,
    QListWidgetItem,
    QMainWindow,
    QMessageBox,
    QPushButton,
    QSizePolicy,
    QVBoxLayout,
    QWidget,
)


MIME_CAMERA = "application/x-gap-camera-id"
RESOLUTIONS = {"1080p": QSize(1920, 1080), "2K": QSize(2560, 1440), "4K": QSize(3840, 2160)}
FRAME_RATE = 30


@dataclass
class Tile:
    camera_id: bytes
    rect: QRectF  # Fractions of the recording canvas.


@dataclass
class CameraFeed:
    worker: CameraWorker
    image: QImage | None = None


class CameraWorker(QThread):
    """Read one DirectShow camera without blocking the interface."""

    frame_ready = Signal(QImage)
    failed = Signal(str)

    def __init__(self, index: int, name: str) -> None:
        super().__init__()
        self.index = index
        self.name = name
        self._stop = threading.Event()

    def stop(self) -> None:
        self._stop.set()

    def run(self) -> None:
        capture = cv2.VideoCapture(self.index, cv2.CAP_DSHOW)
        if not capture.isOpened():
            self.failed.emit(f"Could not open {self.name}. Check camera access and whether another app is using it.")
            return
        try:
            capture.set(cv2.CAP_PROP_FRAME_WIDTH, 1280)
            capture.set(cv2.CAP_PROP_FRAME_HEIGHT, 720)
            capture.set(cv2.CAP_PROP_FPS, FRAME_RATE)
            misses = 0
            while not self._stop.is_set():
                ok, data = capture.read()
                if not ok:
                    misses += 1
                    if misses >= 30:
                        self.failed.emit(f"{self.name} stopped sending video frames.")
                        break
                    self.msleep(20)
                    continue
                misses = 0
                image = QImage(data.data, data.shape[1], data.shape[0], data.strides[0], QImage.Format.Format_BGR888)
                self.frame_ready.emit(image.copy())
        finally:
            capture.release()


def video_file() -> Path:
    """Choose a unique MP4 name in the Windows Videos folder."""
    from PySide6.QtCore import QStandardPaths

    movies = QStandardPaths.writableLocation(QStandardPaths.StandardLocation.MoviesLocation)
    folder = Path(movies) if movies else Path.home() / "Videos"
    folder.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now().strftime("%Y-%m-%d_%H-%M-%S")
    candidate = folder / f"GAP_{stamp}.mp4"
    number = 2
    while candidate.exists():
        candidate = folder / f"GAP_{stamp}_{number}.mp4"
        number += 1
    return candidate


def crop_source(image: QImage, target: QRectF) -> QRectF:
    """Center-crop a camera image to fill its tile without stretching."""
    source_width = float(image.width())
    source_height = float(image.height())
    source_ratio = source_width / source_height
    target_ratio = target.width() / target.height()
    if source_ratio > target_ratio:
        width = source_height * target_ratio
        return QRectF((source_width - width) / 2, 0, width, source_height)
    height = source_width / target_ratio
    return QRectF(0, (source_height - height) / 2, source_width, height)


def draw_camera(painter: QPainter, target: QRectF, image: QImage | None) -> None:
    if image is None or image.isNull() or target.isEmpty():
        return
    painter.save()
    painter.setClipRect(target)
    painter.drawImage(target, image, crop_source(image, target))
    painter.restore()


def compose_frame(size: QSize, tiles: list[Tile], images: dict[bytes, QImage | None]) -> QImage:
    """Create exactly the video frame that will be encoded."""
    frame = QImage(size, QImage.Format.Format_RGB32)
    frame.fill(Qt.GlobalColor.black)
    painter = QPainter(frame)
    painter.setRenderHint(QPainter.RenderHint.SmoothPixmapTransform)
    for tile in tiles:
        box = QRectF(
            tile.rect.x() * size.width(),
            tile.rect.y() * size.height(),
            tile.rect.width() * size.width(),
            tile.rect.height() * size.height(),
        )
        draw_camera(painter, box, images.get(tile.camera_id))
    painter.end()
    return frame


class CameraList(QListWidget):
    def __init__(self) -> None:
        super().__init__()
        self.setDragEnabled(True)
        self.setSelectionMode(QListWidget.SelectionMode.SingleSelection)
        self.setStyleSheet(
            "QListWidget { background: #22262c; color: white; border: 1px solid #404751; "
            "border-radius: 8px; padding: 5px; } "
            "QListWidget::item { padding: 10px 6px; } "
            "QListWidget::item:selected { background: #34506c; border-radius: 5px; }"
        )

    def startDrag(self, supported_actions: Qt.DropAction) -> None:
        item = self.currentItem()
        if item is None:
            return
        camera_id = item.data(Qt.ItemDataRole.UserRole)
        mime = QMimeData()
        mime.setData(MIME_CAMERA, QByteArray(camera_id))
        drag = QDrag(self)
        drag.setMimeData(mime)
        drag.exec(Qt.DropAction.CopyAction)


class Canvas(QWidget):
    add_requested = Signal(bytes, QPointF)
    tile_removed = Signal(bytes)

    def __init__(self) -> None:
        super().__init__()
        self.tiles: list[Tile] = []
        self.images: dict[bytes, QImage | None] = {}
        self.names: dict[bytes, str] = {}
        self.output_size = RESOLUTIONS["1080p"]
        self.selected: bytes | None = None
        self.editable = True
        self.drag_mode: str | None = None
        self.drag_origin = QPointF()
        self.original_rect = QRectF()
        self.setAcceptDrops(True)
        self.setFocusPolicy(Qt.FocusPolicy.StrongFocus)
        self.setMinimumSize(480, 270)
        self.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Expanding)

    def record_rect(self) -> QRectF:
        margin = 16.0
        width = max(1.0, self.width() - 2 * margin)
        height = max(1.0, self.height() - 2 * margin)
        ratio = self.output_size.width() / self.output_size.height()
        if width / height > ratio:
            width = height * ratio
        else:
            height = width / ratio
        return QRectF((self.width() - width) / 2, (self.height() - height) / 2, width, height)

    def screen_box(self, tile: Tile) -> QRectF:
        canvas = self.record_rect()
        return QRectF(
            canvas.x() + tile.rect.x() * canvas.width(),
            canvas.y() + tile.rect.y() * canvas.height(),
            tile.rect.width() * canvas.width(),
            tile.rect.height() * canvas.height(),
        )

    def normalized_point(self, point: QPointF) -> QPointF:
        canvas = self.record_rect()
        return QPointF(
            (point.x() - canvas.x()) / canvas.width(),
            (point.y() - canvas.y()) / canvas.height(),
        )

    def tile_for(self, camera_id: bytes) -> Tile | None:
        return next((tile for tile in self.tiles if tile.camera_id == camera_id), None)

    def add_tile(self, camera_id: bytes, name: str, point: QPointF) -> None:
        existing = self.tile_for(camera_id)
        if existing is not None:
            self.selected = camera_id
            self.update()
            return
        width, height = 0.42, 0.42
        x = min(max(point.x() - width / 2, 0.0), 1.0 - width)
        y = min(max(point.y() - height / 2, 0.0), 1.0 - height)
        self.tiles.append(Tile(camera_id, QRectF(x, y, width, height)))
        self.names[camera_id] = name
        self.selected = camera_id
        self.setFocus()
        self.update()

    def remove_selected(self) -> None:
        if self.selected is None or not self.editable:
            return
        camera_id = self.selected
        self.tiles = [tile for tile in self.tiles if tile.camera_id != camera_id]
        self.images.pop(camera_id, None)
        self.names.pop(camera_id, None)
        self.selected = None
        self.tile_removed.emit(camera_id)
        self.update()

    def paintEvent(self, event) -> None:
        painter = QPainter(self)
        painter.fillRect(self.rect(), QColor("#171a1e"))
        area = self.record_rect()
        painter.fillRect(area, Qt.GlobalColor.black)
        painter.setRenderHint(QPainter.RenderHint.SmoothPixmapTransform)
        for tile in self.tiles:
            box = self.screen_box(tile)
            image = self.images.get(tile.camera_id)
            draw_camera(painter, box, image)
            if image is None or image.isNull():
                painter.setPen(QColor("#b9c0c9"))
                painter.drawText(box.adjusted(8, 8, -8, -8), Qt.AlignmentFlag.AlignCenter, self.names.get(tile.camera_id, "Camera"))
            painter.setPen(QPen(QColor("#58b8ff") if tile.camera_id == self.selected else QColor("#878f9a"), 2))
            painter.drawRect(box)
            if tile.camera_id == self.selected and self.editable:
                painter.fillRect(QRectF(box.right() - 12, box.bottom() - 12, 12, 12), QColor("#58b8ff"))
        if not self.tiles:
            painter.setPen(QColor("#929aa4"))
            painter.drawText(area, Qt.AlignmentFlag.AlignCenter, "Drag cameras here")
        painter.end()

    def dragEnterEvent(self, event) -> None:
        if self.editable and event.mimeData().hasFormat(MIME_CAMERA):
            event.acceptProposedAction()

    def dragMoveEvent(self, event) -> None:
        if self.editable and self.record_rect().contains(event.position()):
            event.acceptProposedAction()

    def dropEvent(self, event) -> None:
        if not self.editable or not self.record_rect().contains(event.position()):
            return
        camera_id = bytes(event.mimeData().data(MIME_CAMERA))
        self.add_requested.emit(camera_id, self.normalized_point(event.position()))
        event.acceptProposedAction()

    def mousePressEvent(self, event: QMouseEvent) -> None:
        if event.button() != Qt.MouseButton.LeftButton:
            return
        point = event.position()
        self.selected = None
        self.drag_mode = None
        for tile in reversed(self.tiles):
            box = self.screen_box(tile)
            if box.contains(point):
                self.selected = tile.camera_id
                if self.editable:
                    handle = QRectF(box.right() - 18, box.bottom() - 18, 18, 18)
                    self.drag_mode = "resize" if handle.contains(point) else "move"
                    self.drag_origin = self.normalized_point(point)
                    self.original_rect = QRectF(tile.rect)
                break
        self.setFocus()
        self.update()

    def mouseMoveEvent(self, event: QMouseEvent) -> None:
        if not self.editable or self.drag_mode is None or self.selected is None:
            return
        tile = self.tile_for(self.selected)
        if tile is None:
            return
        point = self.normalized_point(event.position())
        dx = point.x() - self.drag_origin.x()
        dy = point.y() - self.drag_origin.y()
        old = self.original_rect
        if self.drag_mode == "move":
            x = min(max(old.x() + dx, 0.0), 1.0 - old.width())
            y = min(max(old.y() + dy, 0.0), 1.0 - old.height())
            tile.rect = QRectF(x, y, old.width(), old.height())
        else:
            width = min(max(old.width() + dx, 0.05), 1.0 - old.x())
            height = min(max(old.height() + dy, 0.05), 1.0 - old.y())
            tile.rect = QRectF(old.x(), old.y(), width, height)
        self.update()

    def mouseReleaseEvent(self, event: QMouseEvent) -> None:
        self.drag_mode = None

    def keyPressEvent(self, event: QKeyEvent) -> None:
        if event.key() in (Qt.Key.Key_Delete, Qt.Key.Key_Backspace):
            self.remove_selected()
        else:
            super().keyPressEvent(event)


class MainWindow(QMainWindow):
    def __init__(self) -> None:
        super().__init__()
        self.setWindowTitle("GAP Video Recorder")
        self.resize(1280, 800)
        self.feeds: dict[bytes, CameraFeed] = {}
        self.devices: dict[bytes, tuple[int, str]] = {}
        self.media_devices = QMediaDevices(self)
        self.media_devices.videoInputsChanged.connect(self.refresh_cameras)
        self.record_session: QMediaCaptureSession | None = None
        self.recorder: QMediaRecorder | None = None
        self.video_input: QVideoFrameInput | None = None
        self.record_timer = QTimer(self)
        self.record_timer.setTimerType(Qt.TimerType.PreciseTimer)
        self.record_timer.timeout.connect(self.send_frame)
        self.record_started_ns = 0
        self.record_path: Path | None = None
        self.finishing = False
        self.close_when_finished = False

        root = QWidget()
        self.setCentralWidget(root)
        outer = QVBoxLayout(root)
        outer.setContentsMargins(16, 16, 16, 12)
        outer.setSpacing(12)
        content = QHBoxLayout()
        content.setSpacing(16)
        outer.addLayout(content, 1)

        sidebar = QVBoxLayout()
        sidebar.setSpacing(8)
        camera_title = QLabel("CAMERAS")
        camera_title.setStyleSheet("font-weight: 700; color: #d8dce2;")
        sidebar.addWidget(camera_title)
        self.camera_list = CameraList()
        self.camera_list.itemDoubleClicked.connect(self.add_from_list)
        sidebar.addWidget(self.camera_list, 1)
        guide = QLabel("Drag a camera onto the canvas.\nDrag its corner to resize.\nDelete removes a selected camera.")
        guide.setWordWrap(True)
        guide.setStyleSheet("color: #a7aeb7; font-size: 12px;")
        sidebar.addWidget(guide)
        content.addLayout(sidebar, 0)
        content.setStretch(0, 1)

        self.canvas = Canvas()
        self.canvas.add_requested.connect(self.add_camera_at)
        self.canvas.tile_removed.connect(self.stop_feed)
        content.addWidget(self.canvas, 1)
        content.setStretch(1, 4)

        controls = QHBoxLayout()
        controls.addWidget(QLabel("Recording size"))
        self.size_box = QComboBox()
        for label, size in RESOLUTIONS.items():
            self.size_box.addItem(f"{label}  ·  {size.width()} × {size.height()}", label)
        self.size_box.currentIndexChanged.connect(self.change_size)
        controls.addWidget(self.size_box)
        controls.addStretch(1)
        self.record_button = QPushButton("●  Record")
        self.record_button.clicked.connect(self.start_recording)
        self.record_button.setStyleSheet("QPushButton { background: #ba3541; color: white; font-weight: 700; padding: 9px 20px; border-radius: 7px; } QPushButton:disabled { background: #5b3338; color: #aaa; }")
        controls.addWidget(self.record_button)
        self.stop_button = QPushButton("■  Stop")
        self.stop_button.clicked.connect(self.stop_recording)
        self.stop_button.setEnabled(False)
        self.stop_button.setStyleSheet("QPushButton { background: #3e454d; color: white; font-weight: 700; padding: 9px 20px; border-radius: 7px; } QPushButton:disabled { color: #777; }")
        controls.addWidget(self.stop_button)
        outer.addLayout(controls)
        self.statusBar().showMessage("Ready · H.264 MP4 · no audio · saves to Videos")
        self.statusBar().setStyleSheet("QStatusBar { background: #242930; color: #c8d0d8; border-top: 1px solid #3a424b; }")
        root.setStyleSheet("background: #171a1e; color: white; font-family: Segoe UI;")
        self.refresh_cameras()

    def refresh_cameras(self) -> None:
        devices = QMediaDevices.videoInputs()
        self.devices = {bytes(device.id()): (index, device.description()) for index, device in enumerate(devices)}
        self.camera_list.clear()
        for camera_id, (_index, name) in self.devices.items():
            item = QListWidgetItem(name)
            item.setData(Qt.ItemDataRole.UserRole, camera_id)
            self.camera_list.addItem(item)
        if not devices:
            self.statusBar().showMessage("No cameras found. Connect a camera and check Windows camera permissions.")

    def add_from_list(self, item: QListWidgetItem) -> None:
        self.add_camera_at(item.data(Qt.ItemDataRole.UserRole), QPointF(0.5, 0.5))

    def add_camera_at(self, camera_id: bytes, point: QPointF) -> None:
        if not self.canvas.editable:
            return
        device_info = self.devices.get(camera_id)
        if device_info is None:
            self.statusBar().showMessage("That camera is no longer connected.")
            return
        index, name = device_info
        if camera_id not in self.feeds:
            worker = CameraWorker(index, name)
            feed = CameraFeed(worker)
            self.feeds[camera_id] = feed
            worker.frame_ready.connect(lambda image, key=camera_id: self.receive_frame(key, image))
            worker.failed.connect(self.statusBar().showMessage)
            worker.start()
        self.canvas.add_tile(camera_id, name, point)

    def receive_frame(self, camera_id: bytes, image: QImage) -> None:
        feed = self.feeds.get(camera_id)
        if feed is None or image.isNull():
            return
        feed.image = image
        self.canvas.images[camera_id] = image
        self.canvas.update()

    def stop_feed(self, camera_id: bytes) -> None:
        feed = self.feeds.pop(camera_id, None)
        if feed is not None:
            feed.worker.stop()
            feed.worker.wait(2000)

    def change_size(self) -> None:
        label = self.size_box.currentData()
        if label in RESOLUTIONS:
            self.canvas.output_size = RESOLUTIONS[label]
            self.canvas.update()

    def start_recording(self) -> None:
        if not self.canvas.tiles or self.recorder is not None:
            self.statusBar().showMessage("Add at least one camera to the canvas before recording.")
            return
        format_check = QMediaFormat()
        supported = format_check.supportedVideoCodecs(QMediaFormat.ConversionMode.Encode)
        if QMediaFormat.VideoCodec.H264 not in supported:
            QMessageBox.critical(self, "H.264 unavailable", "This Windows installation has no H.264 encoder available to Qt Multimedia.")
            return
        try:
            self.record_path = video_file()
        except OSError as exc:
            QMessageBox.critical(self, "Cannot save video", str(exc))
            return
        self.record_session = QMediaCaptureSession(self)
        self.recorder = QMediaRecorder(self)
        self.video_input = QVideoFrameInput(self)
        self.record_session.setVideoFrameInput(self.video_input)
        self.record_session.setRecorder(self.recorder)
        media_format = QMediaFormat()
        media_format.setFileFormat(QMediaFormat.FileFormat.MPEG4)
        media_format.setVideoCodec(QMediaFormat.VideoCodec.H264)
        self.recorder.setMediaFormat(media_format)
        self.recorder.setVideoResolution(self.canvas.output_size)
        self.recorder.setVideoFrameRate(FRAME_RATE)
        bitrate = {"1080p": 10_000_000, "2K": 16_000_000, "4K": 28_000_000}[self.size_box.currentData()]
        self.recorder.setVideoBitRate(bitrate)
        self.recorder.setOutputLocation(QUrl.fromLocalFile(str(self.record_path)))
        self.recorder.recorderStateChanged.connect(self.record_state_changed)
        self.recorder.errorOccurred.connect(self.record_error)
        self.canvas.editable = False
        self.camera_list.setEnabled(False)
        self.size_box.setEnabled(False)
        self.record_button.setEnabled(False)
        self.statusBar().showMessage("Starting recording…")
        self.recorder.record()

    def record_state_changed(self, state: QMediaRecorder.RecorderState) -> None:
        if state == QMediaRecorder.RecorderState.RecordingState:
            self.record_started_ns = time.monotonic_ns()
            self.record_timer.start(1000 // FRAME_RATE)
            self.stop_button.setEnabled(True)
            self.statusBar().showMessage(f"Recording to {self.record_path}")
        elif state == QMediaRecorder.RecorderState.StoppedState and self.recorder is not None:
            path = self.record_path
            self.record_timer.stop()
            self.cleanup_recorder()
            self.statusBar().showMessage(f"Saved {path}" if path and path.exists() else "Recording stopped.")
            if self.close_when_finished:
                self.close()

    def send_frame(self) -> None:
        if self.video_input is None or self.recorder is None:
            return
        images = {camera_id: feed.image for camera_id, feed in self.feeds.items()}
        image = compose_frame(self.canvas.output_size, self.canvas.tiles, images)
        frame = QVideoFrame(image)
        start_us = (time.monotonic_ns() - self.record_started_ns) // 1000
        frame.setStartTime(start_us)
        frame.setEndTime(start_us + 1_000_000 // FRAME_RATE)
        self.video_input.sendVideoFrame(frame)  # A busy encoder drops this frame; preview stays live.

    def stop_recording(self) -> None:
        if self.recorder is None or self.finishing:
            return
        self.finishing = True
        self.stop_button.setEnabled(False)
        self.record_timer.stop()
        self.statusBar().showMessage("Finishing MP4…")
        self.recorder.stop()

    def record_error(self, error: QMediaRecorder.Error, message: str) -> None:
        self.record_timer.stop()
        self.cleanup_recorder()
        QMessageBox.critical(self, "Recording failed", message or str(error))

    def cleanup_recorder(self) -> None:
        self.record_timer.stop()
        self.finishing = False
        self.canvas.editable = True
        self.camera_list.setEnabled(True)
        self.size_box.setEnabled(True)
        self.record_button.setEnabled(True)
        self.stop_button.setEnabled(False)
        if self.record_session is not None:
            self.record_session.setRecorder(None)
            self.record_session.setVideoFrameInput(None)
        if self.recorder is not None:
            self.recorder.deleteLater()
        if self.video_input is not None:
            self.video_input.deleteLater()
        if self.record_session is not None:
            self.record_session.deleteLater()
        self.recorder = None
        self.video_input = None
        self.record_session = None

    def closeEvent(self, event) -> None:
        if self.recorder is not None:
            self.close_when_finished = True
            self.stop_recording()
            event.ignore()
            return
        for camera_id in list(self.feeds):
            self.stop_feed(camera_id)
        event.accept()


def main() -> int:
    app = QApplication(sys.argv)
    app.setApplicationName("GAP Video Recorder")
    window = MainWindow()
    window.show()
    return app.exec()


if __name__ == "__main__":
    raise SystemExit(main())
