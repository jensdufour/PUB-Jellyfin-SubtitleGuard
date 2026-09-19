import argparse
import http.server
import json
import pathlib
import subprocess
import tempfile
import threading
import time


FRAME_BYTES = 320 * 180
HEADER = """[Script Info]
ScriptType: v4.00+
PlayResX: 320
PlayResY: 180
[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Default,DejaVu Sans,24,&H00FFFFFF,&H00FFFFFF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,1,0,2,10,10,10,1
[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
"""
EARLY = "Dialogue: 0,0:00:00.50,0:00:01.50,Default,,0,0,0,,EARLY CUE\n"
LATE = "Dialogue: 0,0:00:04.00,0:00:05.00,Default,,0,0,0,,LATE CUE\n"


def render(ffmpeg, source, *, first_frame=None, paced=False, sub2video=False):
    escaped = source.replace("\\", "/").replace(":", "\\:").replace("'", "'\\''")
    command = [ffmpeg, "-hide_banner", "-loglevel", "warning", "-nostdin"]
    if paced:
        command.append("-re")
    command += [
        "-f", "lavfi", "-i", "color=c=black:s=320x180:r=10:d=6",
    ]
    if sub2video:
        command += [
            "-filter_complex",
            f"alphasrc=s=320x180:r=10:start=0,format=bgra,subtitles=filename='{escaped}':alpha=1:sub2video=1[sub];"
            "[0:v][sub]overlay=eof_action=pass:repeatlast=0:shortest=1",
        ]
    else:
        command += ["-vf", f"subtitles=filename='{escaped}'"]
    command += ["-threads", "1", "-pix_fmt", "gray", "-f", "rawvideo", "pipe:1"]
    started = time.monotonic()
    with tempfile.TemporaryFile() as errors:
        with subprocess.Popen(command, stdout=subprocess.PIPE, stderr=errors) as process:
            deadline = threading.Timer(20, process.kill)
            deadline.start()
            try:
                assert process.stdout is not None
                output = process.stdout.read(FRAME_BYTES)
                first_seconds = None
                if len(output) == FRAME_BYTES:
                    first_seconds = round(time.monotonic() - started, 3)
                    if first_frame is not None:
                        first_frame()
                output += process.stdout.read()
                exit_code = process.wait(timeout=5)
            finally:
                deadline.cancel()
                if process.poll() is None:
                    process.kill()
                    process.wait(timeout=5)
        errors.seek(0)
        diagnostics = errors.read().decode(errors="replace")
    frames = [output[offset:offset + FRAME_BYTES] for offset in range(0, len(output), FRAME_BYTES)]
    return {
        "exit_code": exit_code,
        "frames": len(frames),
        "first_frame_seconds": first_seconds,
        "early_caption": len(frames) > 10 and max(frames[10]) > 128,
        "late_caption": len(frames) > 45 and max(frames[45]) > 128,
        "error_diagnostic": any(message in diagnostics.lower() for message in (
            "premature", "input/output error", "invalid data", "error", "failed"
        )),
    }


def check(ffmpeg, sub2video):
    first_frame = threading.Event()
    state = {"released_before_eof": False}
    requests = {"/delayed.ass": 0, "/interrupted.ass": 0}
    prefix = (HEADER + EARLY).encode()
    suffix = LATE.encode()

    class SubtitleSource(http.server.BaseHTTPRequestHandler):
        def do_GET(self):
            if self.path not in ("/delayed.ass", "/interrupted.ass"):
                self.send_error(404)
                return
            requests[self.path] += 1
            first_request = requests[self.path] == 1
            self.send_response(200)
            self.send_header("Content-Type", "text/x-ssa")
            self.send_header("Content-Length", str(len(prefix) + len(suffix)))
            self.end_headers()
            self.wfile.write(prefix)
            self.wfile.flush()
            if self.path == "/delayed.ass":
                rendered = first_frame.wait(timeout=3)
                if first_request:
                    state["released_before_eof"] = rendered
                self.wfile.write(suffix)
                self.wfile.flush()
            self.close_connection = True

        def log_message(self, format, *args):
            pass

    with tempfile.TemporaryDirectory(prefix="subtitleguard-streaming-") as directory:
        root = pathlib.Path(directory)
        complete = root / "complete.ass"
        complete.write_text(HEADER + EARLY + LATE, encoding="utf-8")
        baseline = render(ffmpeg, str(complete), sub2video=sub2video)
        assert baseline["exit_code"] == 0 and baseline["frames"] == 60, baseline
        assert baseline["early_caption"] and baseline["late_caption"], baseline

        server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), SubtitleSource)
        worker = threading.Thread(target=server.serve_forever, daemon=True)
        worker.start()
        try:
            origin = f"http://127.0.0.1:{server.server_port}"
            delayed = render(ffmpeg, origin + "/delayed.ass", first_frame=first_frame.set, sub2video=sub2video)
            interrupted = render(ffmpeg, origin + "/interrupted.ass", sub2video=sub2video)
        finally:
            server.shutdown()
            server.server_close()
            worker.join(timeout=5)
        assert all(count >= 1 for count in requests.values()), requests
        assert delayed["exit_code"] == 0 and delayed["early_caption"] and delayed["late_caption"], delayed

        growing = root / "growing.ass"
        growing.write_text(HEADER + EARLY, encoding="utf-8")

        def append_late_cue():
            with growing.open("a", encoding="utf-8") as output:
                output.write(LATE)

        appended = render(ffmpeg, str(growing), first_frame=append_late_cue, paced=True, sub2video=sub2video)
        assert appended["exit_code"] == 0 and appended["frames"] == 60 and appended["early_caption"], appended
        assert growing.read_text(encoding="utf-8").endswith(LATE)
        return {
            "version": subprocess.check_output([ffmpeg, "-version"], text=True).splitlines()[0],
            "renderer": "sub2video-overlay" if sub2video else "subtitles",
            "complete_file_control": baseline,
            "http_request_counts": requests,
            "delayed_http": {**delayed, "first_frame_before_subtitle_eof": state["released_before_eof"]},
            "interrupted_http": interrupted,
            "growing_file": appended,
            "streaming_proven": bool(state["released_before_eof"] and delayed["late_caption"]),
            "growing_file_proven": bool(appended["late_caption"]),
            "scope": "Synthetic loopback ASS and video only; no Jellyfin or provider requests.",
        }


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Test native ASS burn-in startup and late-cue behavior, without changing Jellyfin.")
    parser.add_argument("--ffmpeg", default="/usr/lib/jellyfin-ffmpeg/ffmpeg")
    parser.add_argument("--sub2video", action="store_true")
    arguments = parser.parse_args()
    print(json.dumps(check(arguments.ffmpeg, arguments.sub2video), indent=2))