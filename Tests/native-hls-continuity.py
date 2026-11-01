"""Synthetic native-argument HLS regression; no server, HTTP, or plugin writes.

Run with --ffmpeg /path/to/ffmpeg; --qsv additionally checks Intel hardware.
The bad playlist clock must fail the SAME video oracle as the shared clock.
This exercises FFmpeg arguments, not Jellyfin routing or the C# cache. ASS
windows are explicitly selected from one global fixture, not extracted cache
results. Fractional rates exercise the unbounded fallback, never windowed HLS.
"""

import argparse
from collections import Counter
from fractions import Fraction
import json
from pathlib import Path
import struct
import subprocess
import tempfile


WIDTH, HEIGHT, FPS, SAMPLE_RATE = 160, 90, 30, 48000
PACKET_SAMPLES = 1024
ASS_HEADER = """[Script Info]
ScriptType: v4.00+
PlayResX: 160
PlayResY: 90
WrapStyle: 0
[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Default,DejaVu Sans,14,&H00FFFFFF,&H0000FFFF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,1,0,2,3,3,5,1
[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
"""
EVENTS = (
    (29, 31, "Dialogue: 0,0:00:29.00,0:00:31.00,Default,,0,0,0,,CROSS ONE"),
    (29, 31, "Dialogue: 1,0:00:29.00,0:00:31.00,Default,,0,0,0,,{\\pos(80,40)}LAYER, COMMA"),
    (29, 31, "Dialogue: 1,0:00:29.00,0:00:31.00,Default,,0,0,0,,{\\pos(80,40)}LAYER, COMMA"),
    (59, 61, "Dialogue: 0,0:00:59.00,0:01:01.00,Default,,0,0,0,,CROSS TWO"),
)


def require(condition, message):
    if not condition:
        raise AssertionError(message)


class Regression:
    def __init__(self, ffmpeg, root, container="fmp4", mux="native", sample_rate=48000, source_container="mp4", bframes=3, native_overlay=False):
        self.ffmpeg = str(ffmpeg)
        self.ffprobe = str(Path(ffmpeg).with_name("ffprobe"))
        self.root = root
        self.container = container
        self.mux = mux
        self.sample_rate = sample_rate
        self.source_container = source_container
        self.bframes = bframes
        self.native_overlay = native_overlay
        self.passed = 0

    def run(self, arguments, binary=None):
        command = [binary or self.ffmpeg]
        if binary is None:
            command += ["-hide_banner", "-loglevel", "error", "-nostdin", "-y"]
        result = subprocess.run(command + list(map(str, arguments)), capture_output=True, timeout=90)
        require(result.returncode == 0, "Command failed: " + result.stderr.decode(errors="replace")[-3000:])
        return result.stdout

    def check(self, condition, message):
        require(condition, message)
        self.passed += 1
        print("PASS " + message, flush=True)

    def probe(self, path, selector, kind):
        entries = "frame=pts,nb_samples,pict_type" if kind == "frames" else "packet=pts,dts,duration,flags,data_hash"
        data = json.loads(self.run([
            "-v", "error", "-select_streams", selector, "-show_streams", "-show_" + kind,
            *(["-show_data_hash", "sha256"] if kind == "packets" else []),
            "-show_entries", "stream=time_base:" + entries,
            "-of", "json", path,
        ], self.ffprobe))
        time_base = Fraction(data["streams"][0]["time_base"])
        return [(Fraction(entry["pts"]) * time_base,
                 Fraction(entry.get("duration", entry.get("nb_samples", 0))) *
                 (time_base if kind == "packets" else Fraction(1, self.sample_rate)), entry)
                for entry in data[kind] if "pts" in entry]

    def fixture(self, name, rate="30", duration="91.3"):
        path = self.root / (name + "." + self.source_container)
        barcode = "geq=lum='if(lt(Y,16),16+219*mod(floor(N/pow(2,floor(X/12))),2),lum(X,Y))':cb='cb(X,Y)':cr='cr(X,Y)'"
        self.run([
            "-f", "lavfi", "-i", f"testsrc2=s={WIDTH}x{HEIGHT}:r={rate}:d={duration}",
            "-f", "lavfi", "-i", f"sine=frequency=997:sample_rate={self.sample_rate}:duration={duration}",
            "-map", "0:v", "-map", "1:a", "-vf", barcode,
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "10", "-g", "251", "-bf", "3",
            "-force_key_frames", "0,7.1,19.3,34.7,52.2,73.1,89.1",
            "-af", "asetpts=PTS+1024/SR/TB", "-c:a", "aac", "-b:a", "128k", "-output_ts_offset", "7", path,
        ])
        return path

    def ass(self, name, start=0, end=100):
        path = self.root / (name + ".ass")
        selected = [line for cue_start, cue_end, line in EVENTS if cue_start < end and cue_end > start]
        path.write_text(ASS_HEADER + "".join(line + "\n" for line in selected), encoding="utf-8")
        text = path.read_text(encoding="utf-8")
        require(text.startswith(ASS_HEADER), "ASS headers changed")
        require(Counter(text.splitlines()[len(ASS_HEADER.splitlines()):]) == Counter(selected), "ASS event multiset changed")
        return path

    def hls(self, source, name, start, end, ass, encoder, audio="copy", rate=FPS):
        directory = self.root / name
        directory.mkdir()
        hardware = (["-init_hw_device", "vaapi=va:/dev/dri/renderD128,driver=iHD",
                     "-init_hw_device", "qsv=qs@va", "-filter_hw_device", "qs"] if encoder == "qsv" else [])
        video_filter = f"subtitles={ass}"
        video_filter += ",format=nv12,hwupload=extra_hw_frames=64" if encoder == "qsv" else ",format=yuv420p"
        filter_args = ["-vf", video_filter]
        if self.native_overlay:
            require(encoder == "qsv", "Native overlay requires --qsv")
            hardware += ["-hwaccel", "vaapi", "-hwaccel_output_format", "vaapi", "-noautorotate"]
            filter_args = ["-filter_complex",
                f"alphasrc=s={WIDTH}x{HEIGHT}:r=30:start='{start}',format=bgra,"
                f"subtitles=f='{ass}':charenc=utf-8:alpha=1:sub2video=1,"
                "hwupload=derive_device=qsv:extra_hw_frames=64[sub];"
                "[0:v:0]setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709,"
                "scale_vaapi=format=nv12:extra_hw_frames=24,hwmap=derive_device=qsv,format=qsv[main];"
                f"[main][sub]overlay_qsv=eof_action=pass:repeatlast=0:w={WIDTH}:h={HEIGHT}[video]"]
        codec = (["-c:v", "h264_qsv", "-preset", "veryfast", "-b:v", "1000k"] if encoder == "qsv"
                 else ["-c:v", "libx264", "-preset", "veryfast", "-crf", "18"])
        gop = str(round(float(rate) * 6))
        audio_args = ["-codec:a:0", "copy"] if audio == "copy" else ["-codec:a:0", "aac", "-b:a", "128k", "-ar", "48000"]
        if audio == "copy" and start > 0:
            audio_args += ["-bsf:a", f"noise=drop='lt(pts*tb\\,{float(start):.3f})'"]
        arguments = hardware + ["-ss", str(start), "-i", str(source), "-map_metadata", "-1",
            "-map_chapters", "-1", "-threads", "2", "-map", "[video]" if self.native_overlay else "0:v:0", "-map", "0:a:0", "-sn",
            *filter_args] + codec + ["-g", gop, "-keyint_min", gop, "-bf", str(self.bframes)] + audio_args + [
            "-copyts", "-avoid_negative_ts", "disabled", "-start_at_zero",
            "-max_muxing_queue_size", "2048", "-f", "hls", "-max_delay", "5000000",
            "-hls_time", "6", "-hls_segment_type", self.container, "-start_number", str(int(float(start) // 6)),
            *(["-hls_fmp4_init_filename", "init.mp4"] if self.container == "fmp4" else []),
            "-hls_segment_filename", str(directory / ("%d.mp4" if self.container == "fmp4" else "%d.ts")),
            "-hls_playlist_type", "vod", "-hls_list_size", "0"]
        if end is not None:
            arguments += ["-to", str(end)]
        if self.mux == "child-copyts":
            arguments += ["-hls_segment_options", "mpegts_copyts=1"]
        elif self.mux == "child-timestamps":
            arguments += ["-hls_segment_options", "mpegts_copyts=1:avoid_negative_ts=disabled"]
        elif self.mux == "outer-copyts":
            arguments += ["-mpegts_copyts", "1"]
        elif self.mux == "zero-delay":
            arguments += ["-muxdelay", "0"]
        playlist = directory / "index.m3u8"
        self.run(arguments + [playlist])
        return playlist

    def identities(self, path):
        raw = self.run(["-i", path, "-map", "0:v:0", "-an", "-sn", "-vf",
                        "crop=156:16:0:0,scale=13:1:flags=neighbor", "-pix_fmt", "gray",
                        "-fps_mode", "passthrough", "-f", "rawvideo", "pipe:1"])
        require(len(raw) % 13 == 0, "Incomplete barcode frame")
        return [sum((value > 128) << bit for bit, value in enumerate(raw[offset:offset + 13]))
                for offset in range(0, len(raw), 13)]

    def segments(self, playlist):
        lines = playlist.read_text().splitlines()
        durations = [Fraction(line.split(":", 1)[1].rstrip(",")) for line in lines if line.startswith("#EXTINF:")]
        names = [line for line in lines if line and not line.startswith("#")]
        require(len(durations) == len(names), "Playlist duration/file mismatch")
        return [(playlist.parent / name, duration) for name, duration in zip(names, durations)]

    def video_oracle(self, playlists, expected_ids, expected_pts, full=True):
        identities = [identity for playlist in playlists for identity in self.identities(playlist)]
        timestamps = [pts for playlist in playlists for pts, _, _ in self.probe(playlist, "v:0", "frames")]
        require(identities == expected_ids, f"Source frame IDs differ: {len(identities)}/{len(expected_ids)} frames")
        require(timestamps == expected_pts, "Decoded PTS differ from continuous reference")
        if full:
            for playlist in playlists:
                segments = self.segments(playlist)
                require(len(segments) == 5, f"Expected five segments, got {len(segments)}")
                for segment, duration in segments:
                    frames = self.probe_segment(segment, "v:0", "frames")
                    require(len(frames) == 180 and duration == 6, f"Non-final segment has {len(frames)} frames / {duration}s")
        return len(identities)

    def probe_segment(self, segment, selector, kind):
        if segment.suffix == ".ts":
            return self.probe(segment, selector, kind)
        joined = segment.parent / "probe.mp4"
        joined.write_bytes((segment.parent / "init.mp4").read_bytes() + segment.read_bytes())
        return self.probe(joined, selector, kind)

    def audio_oracle(self, playlists, reference, mode):
        baseline = self.probe(reference, "a:0", "packets")
        packets = [packet for playlist in playlists for packet in self.probe(playlist, "a:0", "packets")]
        quantum = Fraction(PACKET_SAMPLES, SAMPLE_RATE)
        require(baseline and packets, "Missing AAC packets")
        for playlist in playlists:
            for segment, _ in self.segments(playlist):
                require(self.probe_segment(segment, "v:0", "frames"), "Empty video/audio-only extra segment")
        ordered = sorted((pts, pts + duration) for pts, duration, _ in packets)
        covered = ordered[0][1]
        largest_overlap = Fraction(0)
        for start, end in ordered[1:]:
            require(start <= covered + Fraction(1, SAMPLE_RATE), f"AAC coverage hole of {float(start - covered):.6f}s")
            largest_overlap = max(largest_overlap, covered - start)
            covered = max(covered, end)
        require(abs(ordered[0][0] - baseline[0][0]) <= quantum, "AAC initial coverage differs by more than one packet")
        require(abs(covered - (baseline[-1][0] + baseline[-1][1])) <= quantum, "AAC end coverage differs by more than one packet")
        require(largest_overlap <= 2 * quantum, "AAC overlap exceeds priming/boundary quantization")
        maximum_deviation = Fraction(0)
        preroll = []
        for playlist in playlists:
            video_pts = [pts for pts, _, _ in self.probe(playlist, "v:0", "frames")]
            job_packets = self.probe(playlist, "a:0", "packets")
            preroll.append(sum(pts + duration <= video_pts[0] for pts, duration, _ in job_packets))
            require(job_packets[-1][0] + job_packets[-1][1] <= video_pts[-1] + Fraction(1, FPS) + quantum,
                    "AAC tail exceeds one packet beyond owned video range")
        if mode == "copy":
            by_payload = {}
            expected = Counter((pts, duration, entry["data_hash"]) for pts, duration, entry in baseline)
            for pts, duration, entry in baseline:
                by_payload.setdefault(entry["data_hash"], []).append((pts, duration))
            actual = Counter()
            for pts, duration, entry in packets:
                digest = entry["data_hash"]
                require(digest in by_payload, "Copied AAC payload absent from continuous reference")
                reference_pts, reference_duration = min(by_payload[digest], key=lambda packet: abs(pts - packet[0]))
                deviation = abs(pts - reference_pts)
                require(deviation <= Fraction(1, 1000), "Copied AAC PTS exceeds fMP4 millisecond edit-list quantization")
                require(duration == reference_duration, "Copied AAC packet duration changed")
                maximum_deviation = max(maximum_deviation, deviation)
                actual[(reference_pts, reference_duration, digest)] += 1
            require(set(actual) == set(expected), "Copied AAC reference packet coverage has missing/extra packets")
            require(all(count <= expected[packet] + 1 for packet, count in actual.items()), "Copied AAC repeated beyond adjacent-job preroll")
        else:
            for playlist in playlists:
                frames = self.probe(playlist, "a:0", "frames")
                require(all((pts * SAMPLE_RATE).denominator == 1 for pts, _, _ in frames), "AAC decoded PTS not sample-aligned")
                pcm = self.run(["-i", playlist, "-map", "0:a:0", "-vn", "-ac", "1", "-ar", "48000",
                                "-c:a", "pcm_s16le", "-f", "s16le", "pipe:1"])
                samples = struct.unpack("<" + "h" * (len(pcm) // 2), pcm)
                require(len(samples) == sum(int(entry["nb_samples"]) for _, _, entry in frames), "PCM/sample coverage differs")
                video_pts = [pts for pts, _, _ in self.probe(playlist, "v:0", "frames")]
                owned_start, owned_end = video_pts[0], video_pts[-1] + Fraction(1, FPS)
                for boundary in (30, 60, 90):
                    first_sample = int(frames[0][0] * SAMPLE_RATE)
                    offset = boundary * SAMPLE_RATE - first_sample
                    for begin in (offset - 480, offset):
                        sample_start = Fraction(first_sample + begin, SAMPLE_RATE)
                        sample_end = sample_start + Fraction(480, SAMPLE_RATE)
                        if owned_start <= sample_start and sample_end <= owned_end:
                            require(0 <= begin and begin + 480 <= len(samples), "Decoded audio misses owned boundary samples")
                            energy = sum(value * value for value in samples[begin:begin + 480]) / 480
                            require(energy > 10000, "Artificial blank AAC gap at job boundary")
        self.check(True, f"{mode} AAC reference coverage/sample boundaries ({len(packets)} packets, "
                   f"overlap {float(largest_overlap):.6f}s, copy PTS delta {maximum_deviation}s, "
                   f"native seek preroll {preroll}; 1024-sample priming/boundary quantization)")

    def cue_pixels(self, source, ass, windows):
        def render(start, end, subtitle):
            return self.run(["-ss", str(start), "-i", source, "-copyts", "-start_at_zero",
                "-avoid_negative_ts", "disabled", "-map", "0:v:0", "-an", "-sn", "-to", str(end),
                "-vf", f"subtitles={subtitle},crop=160:70:0:20", "-fps_mode", "passthrough",
                "-pix_fmt", "gray", "-f", "rawvideo", "pipe:1"])
        continuous = render(0, 90, ass)
        independent = b"".join(render(start, start + 30, window) for start, window in zip((0, 30, 60), windows))
        self.check(continuous == independent and len(continuous) == 2700 * 160 * 70,
                   "absolute ASS windows retain headers/duplicate event multiset and all 2700 rendered cue frames")
        empty = self.ass("empty", 91, 92)
        absent = render(0, 90, empty)
        size = 160 * 70
        for start, end in ((29, 31), (59, 61)):
            self.check(all(continuous[index * size:(index + 1) * size] != absent[index * size:(index + 1) * size]
                           for index in range(start * FPS, end * FPS)), f"crossing cue visible on every frame {start}-{end}s")

    def scenario(self, source, encoder):
        ass = self.ass("global")
        reference = self.hls(source, encoder + "-reference", 0, 90, ass, encoder)
        expected_ids = self.identities(reference)
        expected_pts = [pts for pts, _, _ in self.probe(reference, "v:0", "frames")]
        self.check(expected_ids == list(range(2700)) and expected_pts == [Fraction(index, FPS) for index in range(2700)],
                   encoder + " continuous reference: 2700 exact source IDs/PTS")
        bad = [self.hls(source, encoder + "-bad" + str(index), start, end,
                        self.ass("bad-window" + str(index), start, end), encoder)
               for index, (start, end) in enumerate(((0, 30.695), (36.834, 67.529), (73.668, 104.363)))]
        try:
            self.video_oracle(bad, expected_ids, expected_pts)
        except AssertionError as failure:
            print(f"BASELINE FAIL (expected) {encoder}: {failure}; playlist=6.139/cap=30.695/next=36.834", flush=True)
        else:
            raise AssertionError("Bad clock unexpectedly passed the seamless oracle")
        tails = [len(self.probe_segment(self.segments(playlist)[-1][0], "v:0", "frames")) for playlist in bad[:2]]
        self.check(tails == [21, 20], f"{encoder} original short tails reproduced exactly: {tails}")
        windows = [self.ass("window" + str(start), start, start + 30) for start in (0, 30, 60)]
        fixed = [self.hls(source, encoder + "-fixed" + str(start), start, start + 30, window, encoder)
                 for start, window in zip((0, 30, 60), windows)]
        count = self.video_oracle(fixed, expected_ids, expected_pts)
        self.check(count == 2700, f"FIX PASS {encoder}: three jobs / 15 full 180-frame segments / exact IDs and decoded PTS")
        self.audio_oracle(fixed, reference, "copy")
        transcode_reference = self.hls(source, encoder + "-aac-reference", 0, 90, ass, encoder, "aac")
        transcode = [self.hls(source, encoder + "-aac" + str(start), start, start + 30, window, encoder, "aac")
                     for start, window in zip((0, 30, 60), windows)]
        self.video_oracle(transcode, expected_ids, expected_pts)
        self.audio_oracle(transcode, transcode_reference, "aac")
        full = self.hls(source, encoder + "-final-reference", 0, None, ass, encoder)
        final = self.hls(source, encoder + "-final", 90, 91.3, self.ass("final-window", 90, 91.3), encoder)
        full_ids = self.identities(full)
        full_pts = [pts for pts, _, _ in self.probe(full, "v:0", "frames")]
        self.video_oracle(fixed + [final], full_ids, full_pts, full=False)
        final_segments = self.segments(final)
        self.check(len(final_segments) == 1 and len(self.probe_segment(final_segments[0][0], "v:0", "frames")) == 39
                   and final_segments[0][1] == Fraction(13, 10), encoder + " legitimate final 1.3s/39-frame partial only at EOF")
        for direction, start in (("forward", 60), ("backward", 30)):
            seek = self.hls(source, encoder + "-seek-" + direction, start, start + 30,
                            windows[start // 30], encoder)
            self.video_oracle([seek], expected_ids[start * FPS:(start + 30) * FPS],
                              expected_pts[start * FPS:(start + 30) * FPS])
            self.check(True, f"{encoder} prepared {direction} seek {start}-{start + 30}s exact 900 IDs/PTS and five full segments")
        if encoder == "software":
            self.cue_pixels(source, ass, windows)

    def fractional(self, rate):
        source = self.fixture("fractional-" + rate.replace("/", "-"), rate, "13")
        ass = self.ass("fractional-empty", 91, 92)
        native = self.hls(source, "fractional-hls-" + rate.replace("/", "-"), 0, None, ass,
                          "software", rate=Fraction(rate))
        self.check(self.identities(native) == self.identities(source),
                   f"{rate} unbounded native fallback preserves every source frame (no window or -to)")
        timestamps = [pts for pts, _, _ in self.probe(native, "v:0", "frames")]
        source_pts = [pts for pts, _, _ in self.probe(source, "v:0", "frames")]
        self.check(timestamps == [pts - source_pts[0] for pts in source_pts], f"{rate} unbounded fallback exact rational decoded PTS")

    def ts_regression(self, source, encoder):
        ass = self.ass("ts-global")
        reference = self.hls(source, encoder + "-ts-reference", 0, 90, ass, encoder)
        expected_ids = self.identities(reference)
        expected_pts = [pts for pts, _, _ in self.probe(reference, "v:0", "frames")]
        origin = expected_pts[0]
        self.check(expected_ids == list(range(2700)) and
                   expected_pts == [origin + Fraction(index, FPS) for index in range(2700)],
                   f"{encoder} {self.container}/{self.mux}: continuous 2700 IDs, constant native origin {origin}")
        jobs = [self.hls(source, encoder + "-ts-job" + str(start), start, start + 30,
                         self.ass("ts-window" + str(start), start, start + 30), encoder)
                for start in (0, 30, 60)]
        self.video_oracle(jobs, expected_ids, expected_pts)
        baseline = self.probe(reference, "v:0", "packets")
        packets = [packet for job in jobs for packet in self.probe(job, "v:0", "packets")]
        self.check([(pts, duration) for pts, duration, _ in packets] ==
                   [(pts, duration) for pts, duration, _ in baseline],
                   f"{encoder} exact video packet PTS/durations and decoded PTS across both boundaries")
        audio = [packet for job in jobs for packet in self.probe(job, "a:0", "packets")]
        expected_audio = self.probe(reference, "a:0", "packets")
        quantum = Fraction(1024, self.sample_rate)
        ordered = sorted((pts, pts + duration) for pts, duration, _ in audio)
        covered = ordered[0][1]
        overlap = Fraction(0)
        for start, end in ordered[1:]:
            require(start <= covered + Fraction(1, self.sample_rate),
                    f"AAC coverage hole {float(start - covered):.9f}s at {float(start):.9f}s after {float(covered):.9f}s")
            overlap = max(overlap, covered - start)
            covered = max(covered, end)
        require(overlap <= quantum + Fraction(1, 90000), f"AAC multiframe overlap {float(overlap):.6f}s > {float(quantum):.6f}s")
        self.check([(duration, entry["data_hash"]) for _, duration, entry in audio] ==
                   [(duration, entry["data_hash"]) for _, duration, entry in expected_audio],
                   f"copied AAC exact ordered packet payload/duration coverage; overlap {float(overlap):.6f}s")
        self.check(all(abs(actual[0] - expected[0]) <= Fraction(1, self.sample_rate)
                       for actual, expected in zip(audio, expected_audio)),
                   "copied AAC PTS differs by at most one audio sample of TS timebase quantization")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ffmpeg", type=Path, default=Path("/usr/lib/jellyfin-ffmpeg/ffmpeg"))
    parser.add_argument("--qsv", action="store_true", help="Also run native Intel QSV; software always runs")
    parser.add_argument("--container", choices=("fmp4", "mpegts"), default="fmp4")
    parser.add_argument("--ts-regression", action="store_true")
    parser.add_argument("--mux", choices=("native", "child-copyts", "child-timestamps", "outer-copyts", "zero-delay"), default="native")
    parser.add_argument("--sample-rate", type=int, choices=(44100, 48000), default=48000)
    parser.add_argument("--source-container", choices=("mp4", "mkv"), default="mp4")
    parser.add_argument("--bframes", type=int, choices=(2, 3), default=3)
    parser.add_argument("--native-overlay", action="store_true")
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="native-hls-continuity-") as directory:
        regression = Regression(args.ffmpeg, Path(directory), args.container, args.mux,
                                args.sample_rate, args.source_container, args.bframes, args.native_overlay)
        version = regression.run(["-version"]).decode().splitlines()[0]
        print(version, flush=True)
        source = regression.fixture("source")
        frames = regression.probe(source, "v:0", "frames")
        regression.check(frames[0][0] == 7 and any(entry["pict_type"] == "B" for _, _, entry in frames),
                         "fixture has nonzero seven-second origin and reordered B frames")
        if args.ts_regression:
            regression.ts_regression(source, "qsv" if args.qsv else "software")
            return
        regression.scenario(source, "software")
        if args.qsv:
            regression.scenario(source, "qsv")
        for rate in ("24000/1001", "30000/1001"):
            regression.fractional(rate)
        print(f"PASS {regression.passed} checks; expected baseline FAIL and shared-clock FIX PASS. "
              "Synthetic native arguments only; C# CanPrepare/cache, native job routing, provider and client acceptance not exercised.", flush=True)


if __name__ == "__main__":
    main()