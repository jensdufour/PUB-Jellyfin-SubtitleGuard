import argparse
import csv
import http.server
import json
import pathlib
import runpy
import subprocess
import tempfile
import threading
import time


FIXTURE = runpy.run_path(str(pathlib.Path(__file__).with_name("streaming-burn-in.py")))
FRAME_BYTES = FIXTURE["FRAME_BYTES"] * 3
SEGMENT_SECONDS = 3
FPS = 10
ERRORS = ("premature", "input/output error", "error during demuxing", "invalid data", "[error]", "[fatal]")
CUES = (
    "Dialogue: 0,0:00:00.50,0:00:01.50,Default,,0,0,0,,EARLY CUE\n",
    "Dialogue: 1,0:00:01.25,0:00:07.75,Default,,0,0,0,,{\\move(60,40,260,40)\\fad(300,300)}MOVING, STYLED CUE\n",
    "Dialogue: 0,0:00:02.50,0:00:04.50,Default,,0,0,0,,BOUNDARY CUE\n",
    "Dialogue: 0,0:00:07.00,0:00:08.50,Default,,0,0,0,,LATE CUE\n",
)


def run(ffmpeg, arguments):
    result = subprocess.run(
        [ffmpeg, "-hide_banner", "-loglevel", "warning", "-nostdin", "-y", *arguments],
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=30,
    )
    diagnostics = result.stderr.decode(errors="replace").lower()
    if result.returncode or any(message in diagnostics for message in ERRORS):
        raise RuntimeError(f"FFmpeg failed ({result.returncode}): {diagnostics[-2000:]}")
    return result.stdout


def cue_time(value):
    hours, minutes, seconds = value.split(":")
    return int(hours) * 3600 + int(minutes) * 60 + float(seconds)


def render_window(ffmpeg, media, subtitles, start, end):
    subtitle_path = str(subtitles).replace("\\", "/").replace(":", "\\:").replace("'", "'\\''")
    return run(ffmpeg, [
        "-copyts", "-i", str(media), "-map", "0:v:0", "-an",
        "-vf", f"subtitles=filename='{subtitle_path}',trim=start={start}:end={end},setpts=PTS-STARTPTS",
        "-threads", "1", "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1",
    ])


def make_fixture(ffmpeg, root):
    subtitles = root / "source.ass"
    subtitles.write_text(FIXTURE["HEADER"] + "".join(CUES), encoding="utf-8")
    media = root / "source.mkv"
    run(ffmpeg, [
        "-f", "lavfi", "-i", "testsrc2=s=320x180:r=10:d=12",
        "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=8000:duration=12",
        "-i", str(subtitles), "-map", "0:v", "-map", "1:a", "-map", "2:s",
        "-c:v", "ffv1", "-g", "30", "-threads", "1", "-c:a", "pcm_s16le",
        "-c:s", "ass", "-t", "12", "-f", "matroska", "-live", "1", str(media),
    ])
    return media, subtitles


def prepare(ffmpeg, source, root, on_window=None):
    root.mkdir()
    command = [
        ffmpeg, "-hide_banner", "-loglevel", "warning", "-nostdin", "-y",
        "-analyzeduration", "1000000", "-probesize", "512000", "-copyts", "-i", str(source),
        "-map", "0:v:0", "-map", "0:a:0", "-map", "0:s:0", "-c", "copy",
        "-f", "segment", "-segment_format", "matroska", "-segment_time", str(SEGMENT_SECONDS),
        "-reset_timestamps", "0", "-segment_list_type", "csv", "-segment_list", "pipe:1",
        str(root / "source-%03d.mkv"),
    ]
    windows = []
    active_cues = []
    started = time.monotonic()
    with tempfile.TemporaryFile() as errors:
        with subprocess.Popen(command, stdout=subprocess.PIPE, stderr=errors, text=True) as process:
            deadline = threading.Timer(30, process.kill)
            deadline.start()
            try:
                assert process.stdout is not None
                for row in csv.reader(process.stdout):
                    name, start_text, end_text = row
                    start, end = float(start_text), float(end_text)
                    if abs(end - start - SEGMENT_SECONDS) > 0.01:
                        raise RuntimeError(f"Incomplete window {start}-{end}; nothing published for it")
                    media = root / pathlib.Path(name).name
                    extracted = run(ffmpeg, [
                        "-copyts", "-i", str(media), "-map", "0:s:0", "-c:s", "copy", "-f", "ass", "pipe:1",
                    ]).decode("utf-8")
                    header = []
                    for line in extracted.splitlines(keepends=True):
                        if line.startswith("Dialogue:"):
                            fields = line.split(",", 9)
                            if len(fields) != 10:
                                raise RuntimeError("Invalid generated ASS event")
                            active_cues.append((cue_time(fields[1]), cue_time(fields[2]), line))
                        else:
                            header.append(line)
                    active_cues = [cue for cue in active_cues if cue[1] > start]
                    pending = root / f"window-{len(windows):03d}.pending.ass"
                    pending.write_text("".join(header) + "".join(cue[2] for cue in active_cues if cue[0] < end), encoding="utf-8")
                    frames = render_window(ffmpeg, media, pending, start, end)
                    if len(frames) != SEGMENT_SECONDS * FPS * FRAME_BYTES:
                        raise RuntimeError(f"Incomplete decoded video window {start}-{end}")
                    complete = pending.with_name(pending.name.replace(".pending", ".complete"))
                    pending.replace(complete)
                    windows.append({"media": media, "subtitles": complete, "start": start, "end": end, "frames": frames})
                    if on_window is not None:
                        on_window(windows[-1])
                exit_code = process.wait(timeout=5)
            finally:
                deadline.cancel()
                if process.poll() is None:
                    process.kill()
                    process.wait(timeout=5)
        errors.seek(0)
        diagnostics = errors.read().decode(errors="replace").lower()
    if exit_code or any(message in diagnostics for message in ERRORS):
        raise RuntimeError(f"Source acquisition failed ({exit_code}); incomplete stream not reusable")
    return windows, round(time.monotonic() - started, 3)


def encode_hls(ffmpeg, source, root, ready, reference, qsv):
    root.mkdir()
    playlist = root / "index.m3u8"
    first_ready = []
    started = time.monotonic()
    command = [ffmpeg, "-hide_banner", "-loglevel", "warning", "-nostdin", "-y"]
    if qsv:
        command += [
            "-init_hw_device", "vaapi=va:/dev/dri/renderD128,driver=iHD",
            "-init_hw_device", "qsv=qs@va", "-filter_hw_device", "qs",
        ]
    command += [
        "-f", "rawvideo", "-pixel_format", "rgb24", "-video_size", "320x180",
        "-framerate", str(FPS), "-i", "pipe:0", "-analyzeduration", "1000000",
        "-probesize", "512000", "-i", str(source), "-map", "0:v:0", "-map", "1:a:0",
    ]
    if qsv:
        command += ["-vf", "format=nv12,hwupload=extra_hw_frames=64", "-c:v", "h264_qsv", "-global_quality", "18"]
    else:
        command += ["-c:v", "libx264", "-preset", "ultrafast", "-crf", "18", "-pix_fmt", "yuv420p"]
    command += [
        "-threads", "1", "-g", str(SEGMENT_SECONDS * FPS), "-bf", "0", "-sc_threshold", "0",
        "-c:a", "aac", "-b:a", "128k", "-ac", "2", "-ar", "48000", "-f", "hls",
        "-hls_time", str(SEGMENT_SECONDS), "-hls_list_size", "0", "-hls_playlist_type", "event",
        "-hls_flags", "independent_segments+temp_file", "-hls_segment_filename", str(root / "burned-%03d.ts"),
        "-progress", "pipe:1", "-stats_period", "0.1", str(playlist),
    ]
    with tempfile.TemporaryFile() as errors:
        with subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=errors) as process:
            deadline = threading.Timer(40, process.kill)
            deadline.start()

            def observe_progress():
                assert process.stdout is not None
                for line in process.stdout:
                    if line.startswith(b"out_time_us=") and not first_ready and playlist.exists():
                        if ".ts" in playlist.read_text(encoding="utf-8"):
                            first_ready.append(round(time.monotonic() - started, 3))
                            ready.set()

            observer = threading.Thread(target=observe_progress, daemon=True)
            observer.start()

            def feed_window(window):
                assert process.stdin is not None
                process.stdin.write(window["frames"])
                process.stdin.flush()

            try:
                prepare(ffmpeg, source, root / "prepared", feed_window)
                assert process.stdin is not None
                process.stdin.close()
                exit_code = process.wait(timeout=30)
            finally:
                deadline.cancel()
                if process.poll() is None:
                    process.kill()
                    process.wait(timeout=5)
                observer.join(timeout=5)
        errors.seek(0)
        diagnostics = errors.read().decode(errors="replace").lower()
    if exit_code or any(message in diagnostics for message in ERRORS):
        raise RuntimeError(f"HLS encode failed ({exit_code}): {diagnostics[-2000:]}")
    assert first_ready, "No completed HLS segment observed"
    manifest = playlist.read_text(encoding="utf-8")
    assert "#EXT-X-ENDLIST" in manifest and manifest.count("#EXTINF:") == 4, manifest
    decoded = run(ffmpeg, ["-i", str(playlist), "-map", "0:v:0", "-an", "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1"])
    assert len(decoded) == len(reference), "HLS dropped or duplicated video frames"
    sample_offsets = range(0, len(reference), FPS * FRAME_BYTES)
    differences = [
        sum(abs(actual - expected) for actual, expected in zip(
            decoded[offset:offset + FRAME_BYTES], reference[offset:offset + FRAME_BYTES]
        )) / FRAME_BYTES for offset in sample_offsets
    ]
    assert max(differences) < 8, differences
    probe = json.loads(subprocess.check_output([
        str(pathlib.Path(ffmpeg).with_name("ffprobe")), "-v", "error", "-show_packets",
        "-show_entries", "packet=stream_index,pts_time,duration_time", "-of", "json", str(playlist),
    ], timeout=15))
    gaps = {}
    for stream_index, name in ((0, "video"), (1, "audio")):
        packets = [packet for packet in probe["packets"] if packet["stream_index"] == stream_index]
        assert len(packets) > 1, name
        deltas = [
            float(following["pts_time"]) - float(previous["pts_time"]) - float(previous["duration_time"])
            for previous, following in zip(packets, packets[1:])
        ]
        gaps[name] = round(max(abs(delta) for delta in deltas), 6)
        assert gaps[name] < 0.001, (name, gaps[name])
    return {
        "encoder": "h264_qsv" if qsv else "libx264", "segments": 4,
        "first_playable_segment_seconds": first_ready[0], "decoded_frames": len(decoded) // FRAME_BYTES,
        "max_sample_mean_pixel_error": round(max(differences), 3),
        "maximum_packet_timeline_gap_seconds": gaps,
        "audio_source_reads": "Separate HTTP reader in one continuous AAC encode; no per-window encoder reset.",
    }


def check_network(ffmpeg, media, root, reference, qsv):
    body = media.read_bytes()
    release_tail = threading.Event()
    release_hls_tail = threading.Event()
    hls_tail_checks = []
    state = {"first_before_tail": False, "first_seconds": None, "requests": 0, "truncated_requests": 0}

    class MediaSource(http.server.BaseHTTPRequestHandler):
        def do_GET(self):
            if self.path not in ("/delayed.mkv", "/interrupted.mkv", "/hls.mkv"):
                self.send_error(404)
                return
            if self.headers.get("Range", "bytes=0-") != "bytes=0-":
                self.send_error(416)
                return
            state["requests"] += 1
            self.send_response(200)
            self.send_header("Content-Type", "video/x-matroska")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            try:
                boundary = len(body) * 3 // 4
                self.wfile.write(body[:boundary])
                self.wfile.flush()
                if self.path == "/delayed.mkv":
                    state["first_before_tail"] = release_tail.wait(timeout=5)
                    self.wfile.write(body[boundary:])
                    self.wfile.flush()
                elif self.path == "/hls.mkv":
                    hls_tail_checks.append(release_hls_tail.wait(timeout=10))
                    self.wfile.write(body[boundary:])
                    self.wfile.flush()
                else:
                    state["truncated_requests"] += 1
            except (BrokenPipeError, ConnectionResetError):
                pass
            self.close_connection = True

        def log_message(self, format, *args):
            pass

    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), MediaSource)
    worker = threading.Thread(target=server.serve_forever, daemon=True)
    worker.start()
    try:
        origin = f"http://127.0.0.1:{server.server_port}"
        started = time.monotonic()

        def first_window(window):
            if state["first_seconds"] is None:
                state["first_seconds"] = round(time.monotonic() - started, 3)
                release_tail.set()

        streamed, _ = prepare(ffmpeg, origin + "/delayed.mkv", root / "streamed", first_window)
        assert state["first_before_tail"], "First window still waits for the source tail"
        assert b"".join(window["frames"] for window in streamed) == reference, "Streamed frames differ"

        published = []
        interrupted_root = root / "interrupted"
        try:
            prepare(ffmpeg, origin + "/interrupted.mkv", interrupted_root, published.append)
        except RuntimeError:
            pass
        else:
            raise AssertionError("Interrupted source was accepted as complete")
        assert state["truncated_requests"] > 0
        assert 0 < len(published) < 4, "Fault did not exercise a partial acquisition"
        assert len(list(interrupted_root.glob("*.complete.ass"))) == len(published)
        prefix = b"".join(window["frames"] for window in published)
        assert prefix == reference[:len(prefix)], "Completed prefix changed on interruption"

        recovered, _ = prepare(ffmpeg, origin + "/delayed.mkv", root / "retry-generation")
        assert b"".join(window["frames"] for window in recovered) == reference

        def cancel_after_first(window):
            raise InterruptedError("Synthetic client cancellation")

        try:
            prepare(ffmpeg, origin + "/delayed.mkv", root / "cancelled", cancel_after_first)
        except InterruptedError:
            pass
        else:
            raise AssertionError("Cancellation was ignored")

        hls = encode_hls(ffmpeg, origin + "/hls.mkv", root / "hls", release_hls_tail, reference, qsv)
        assert len(hls_tail_checks) >= 2 and all(hls_tail_checks), "HLS waited for complete source input"
        hls["first_segment_before_source_tail"] = True

        return {
            "first_window_seconds": state["first_seconds"],
            "first_window_before_tail": state["first_before_tail"],
            "source_bytes": len(body), "held_back_bytes": len(body) - len(body) * 3 // 4,
            "pixel_exact_against_continuous": True,
            "interrupted_completed_prefix_windows": len(published),
            "incomplete_window_published": False,
            "separate_retry_generation_exact": True, "cancellation_stops_acquisition": True,
            "seamless_live_retry_tested": False,
            "hls": hls,
        }
    finally:
        release_tail.set()
        release_hls_tail.set()
        server.shutdown()
        server.server_close()
        worker.join(timeout=5)


def check(ffmpeg, qsv):
    with tempfile.TemporaryDirectory(prefix="subtitleguard-segments-") as directory:
        root = pathlib.Path(directory)
        media, subtitles = make_fixture(ffmpeg, root)
        reference = render_window(ffmpeg, media, subtitles, 0, 12)
        assert len(reference) == 12 * FPS * FRAME_BYTES
        windows, seconds = prepare(ffmpeg, media, root / "windows")
        assert len(windows) == 4, len(windows)
        assert b"".join(window["frames"] for window in windows) == reference, "Segmented captions differ from continuous reference"
        for index in (2, 1, 0, 3):
            window = windows[index]
            sought = render_window(ffmpeg, window["media"], window["subtitles"], window["start"], window["end"])
            assert sought == window["frames"], f"Seek mismatch in window {index}"
        assert "BOUNDARY CUE" in windows[1]["subtitles"].read_text(encoding="utf-8")
        assert "Dialogue:" not in windows[3]["subtitles"].read_text(encoding="utf-8")
        network = check_network(ffmpeg, media, root, reference, qsv)
        return {
            "windows": len(windows), "frames": len(reference) // FRAME_BYTES, "seconds": seconds,
            "pixel_exact_against_continuous": True, "cross_boundary_cue": True,
            "sparse_window": True, "prepared_window_seeks": True,
            "network": network,
            "network_streaming_proven": True,
            "scope": "Synthetic windows and loopback HTTP only; no production plugin, API, provider or native cache changes.",
        }


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Isolated segment-scoped ASS preparation and burn-in proof.")
    parser.add_argument("--ffmpeg", default="/usr/lib/jellyfin-ffmpeg/ffmpeg")
    parser.add_argument("--qsv", action="store_true")
    arguments = parser.parse_args()
    print(json.dumps(check(arguments.ffmpeg, arguments.qsv), indent=2))