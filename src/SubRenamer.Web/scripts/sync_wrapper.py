#!/usr/bin/env python3
"""ffsubsync wrapper:作为库调用 ffsubsync,通过 progress_handler 回调
输出 JSON 进度行到 stdout,供 C# SubSyncService 解析。

用法: python3 sync_wrapper.py <reference> <subtitle> <output> [ffsubsync 参数]

输出格式(每行一个 JSON):
  {"type":"start","video":"...","subtitle":"...","output":"..."}
  {"type":"progress","fraction":0.35}
  ...
  {"type":"result","successful":true,"offset_seconds":1.2,"framerate_scale_factor":1.0}
  或 {"type":"error","message":"..."}
"""
import sys
import json
import logging
import ffsubsync
from ffsubsync.ffsubsync import make_parser


def emit(obj):
    print(json.dumps(obj, ensure_ascii=False), flush=True)


def on_progress(info):
    # info.fraction 是 0.0-1.0 的比率(total 未知时为 None)
    emit({"type": "progress", "fraction": info.fraction})


class QualityReasonHandler(logging.Handler):
    def __init__(self):
        super().__init__(level=logging.WARNING)
        self.reasons = []

    def emit(self, record):
        message = record.getMessage()
        prefix = "low-quality alignment ("
        if not message.startswith(prefix) or ");" not in message:
            return
        details = message[len(prefix) :].split(");", 1)[0]
        self.reasons.extend(reason.strip() for reason in details.split(";") if reason.strip())


def main():
    if len(sys.argv) < 4:
        emit({"type": "error", "message": "用法: sync_wrapper.py <reference> <subtitle> <output> [参数]"})
        sys.exit(1)

    reference, subtitle, output = sys.argv[1], sys.argv[2], sys.argv[3]
    quality_handler = QualityReasonHandler()
    ffsubsync_logger = logging.getLogger("ffsubsync.ffsubsync")
    ffsubsync_logger.addHandler(quality_handler)
    try:
        emit({"type": "start"})
        args = make_parser().parse_args(
            [reference, "-i", subtitle, "-o", output, *sys.argv[4:]]
        )
        result = ffsubsync.run(args, progress_handler=on_progress)
        emit(
            {
                "type": "result",
                "successful": bool(result.get("sync_was_successful")),
                "offset_seconds": result.get("offset_seconds"),
                "framerate_scale_factor": result.get("framerate_scale_factor"),
                "quality_reasons": quality_handler.reasons,
            }
        )
        sys.exit(int(result.get("retval", 0)))
    except Exception as e:
        emit({"type": "error", "message": str(e)})
        sys.exit(1)
    finally:
        ffsubsync_logger.removeHandler(quality_handler)


if __name__ == "__main__":
    main()
