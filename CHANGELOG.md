# 更新日志

本项目按语义化版本管理公开版本。尚未创建对应 Git 标签的版本标记为“待发布”。

## [1.1.0] - 待发布

### 新增

- 增加纯预览调轴计划、唯一目标命名和多语言字幕防覆盖。
- 增加独立 staging、manifest、逐项结构化结果和有限任务日志。
- 增加低质量拒绝、队列/并发上限、单项超时、取消和过期任务清理。
- 增加显式 commit、默认冲突拒绝、覆盖前备份、哈希校验、原子落盘和 rollback。
- 增加 `video_global`、`subtitle_reference`、实验性 `video_split` 和 `no_sync` 安全任务模式。
- 增加真实 FFmpeg + FFsubsync 端到端 CI，覆盖三种调轴模式及提交/回滚链路。

### 修复与加固

- 修复改名后继续使用旧字幕路径的工作流问题，统一为先调轴到 staging、再提交。
- 修复同集多语言字幕目标名称冲突。
- 修复 FFsubsync 临时输出文件丢失真实字幕扩展名的问题。
- 加固媒体、上传和工作目录边界，并拒绝符号链接逃逸。
- 子进程改用结构化参数、并行读取输出，并在取消或超时时终止进程树。

### 兼容性

- 保留旧版调轴创建路由，并提供 `/api/sync/tasks` 兼容路由。
- 扫描、上传、匹配和原有改名能力继续保留。
- 新调轴任务不会自动写入正式媒体目录，调用方需要显式 commit。

### 已知限制

- `video_split` 仍为实验性功能；响应中的 `offsetSeconds` 是各条偏移的中位数，最终结果以 staging 文件为准。
- 本版本代码和镜像已通过自动化验证，但没有执行家庭 NAS 或真实媒体库生产部署。

## [1.0.0]

- 初始 ASP.NET Core WebUI 与 Docker 版本。
- 提供字幕扫描、上传、匹配、批量改名和基础 FFsubsync 调轴。

[1.1.0]: https://github.com/BMnandu/SubRenamerWeb/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/BMnandu/SubRenamerWeb/tree/v1.0.0
