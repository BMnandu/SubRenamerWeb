#!/usr/bin/env python3
"""ffsubsync wrapper:作为库调用 ffsubsync,通过 progress_handler 回调
输出 JSON 进度行到 stdout,供 C# SubSyncService 解析。

用法: python3 sync_wrapper.py <video> <subtitle> <output>

输出格式(每行一个 JSON):
  {"type":"start","video":"...","subtitle":"...","output":"..."}
  {"type":"progress","fraction":0.35}
  ...
  {"type":"done","output":"..."}
  或 {"type":"error","message":"..."}
"""
import sys
import json
import ffsubsync
from ffsubsync.ffsubsync import make_parser


def emit(obj):
    print(json.dumps(obj, ensure_ascii=False), flush=True)


def on_progress(info):
    # info.fraction 是 0.0-1.0 的比率(total 未知时为 None)
    emit({"type": "progress", "fraction": info.fraction})


def main():
    if len(sys.argv) < 4:
        emit({"type": "error", "message": "用法: sync_wrapper.py <video> <subtitle> <output>"})
        sys.exit(1)

    video, subtitle, output = sys.argv[1], sys.argv[2], sys.argv[3]
    try:
        emit({"type": "start", "video": video, "subtitle": subtitle, "output": output})
        args = make_parser().parse_args([video, "-i", subtitle, "-o", output])
        ffsubsync.run(args, progress_handler=on_progress)
        emit({"type": "done", "output": output})
    except Exception as e:
        emit({"type": "error", "message": str(e)})
        sys.exit(1)


if __name__ == "__main__":
    main()
