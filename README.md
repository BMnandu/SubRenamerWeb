# SubRenamer.Web

基于 [SubRenamer](https://github.com/qwqcode/SubRenamer) 核心算法的 Docker + WebUI 改造版。

字幕批量匹配、改名与自动调轴工具。复用原项目 `SubRenamer.Core` 匹配算法（零修改），提供浏览器 Web 界面和 Docker 部署支持。

## 特性

- **混合文件模式**：视频从挂载目录扫描，字幕通过浏览器批量上传
- **同文件夹模式**：直接匹配并改名挂载目录中的字幕，可选 `SubBackup` 备份
- **自动匹配**：调用 `SubRenamer.Core` diff 算法关联视频与字幕
- **改名预览与微调**：执行前预览目标文件名，可手动调整匹配关系
- **多语言字幕**：支持一个视频匹配多个字幕，自动识别 `chs`、`cht`、`zh`、`ja`、`en` 等语言标记
- **安全调轴任务**：通过 FFsubsync + FFmpeg 在独立 staging 中执行，支持逐项状态、质量门禁、超时和取消
- **多架构镜像**：GitHub Actions 自动发布 `linux/amd64` 和 `linux/arm64` 镜像

## 使用 Docker Hub 镜像

镜像地址：[`beiming712/subrenamerweb`](https://hub.docker.com/r/beiming712/subrenamerweb)

Docker 会根据设备自动选择 AMD64 或 ARM64 版本：

```bash
docker run -d \
  --name subrenamer \
  -p 38080:8080 \
  -v /path/to/media:/media \
  --tmpfs /uploads:mode=1777 \
  --tmpfs /work:mode=1777 \
  --restart unless-stopped \
  beiming712/subrenamerweb:latest
```

浏览器访问 `http://宿主机IP:38080`。

> 媒体目录需要可写权限，因为旧改名接口和显式 `commit` 会写入视频所在目录。调轴执行阶段只写 `/work` staging。

## Docker Compose

修改 `docker-compose.yml` 中的媒体目录、UID 和 GID：

```yaml
volumes:
  - /path/to/media:/media
user: "1000:10"
```

使用已发布镜像启动：

```bash
docker compose pull
docker compose up -d
```

从源码重新构建：

```bash
docker compose up -d --build
```

默认访问地址为 `http://localhost:38080`。

## 使用流程

1. **扫描**：输入媒体库子目录（留空扫描根目录），列出视频和已挂载字幕
2. **上传**：将字幕批量拖入上传区；如果字幕已与视频放在同一目录，可跳过此步
3. **匹配**：点击“一键匹配”，自动关联视频与字幕并显示改名预览
4. **微调**：在结果表格中手动修正匹配关系，必要时填写附加语言后缀
5. **改名**：执行字幕改名；上传字幕会复制到视频目录，挂载字幕会在原目录改名
6. **调轴**：选择全局、分段或 `no_sync` 模式，在独立 staging 中执行并查看逐项结果
7. **提交或回滚**：通过质量门禁后显式提交；需要覆盖时勾选“允许覆盖”，系统会先备份旧字幕

### 调轴说明

调轴功能调用固定版本的 FFsubsync 分析视频音轨，并通过 FFmpeg 辅助处理：

- 任务在后台异步执行，浏览器每秒查询一次状态
- 每个任务使用 `/work/<taskId>/`，包含 `manifest.json`、`output/`、`logs/` 和 `backup/`
- 输出先写临时文件，校验非空后原子改名为 staging 候选文件
- 默认启用低质量拒绝，返回偏移秒数、帧率比例、质量状态与逐项错误
- 支持 `video_global`、实验性 `video_split`、`subtitle_reference` 和 `no_sync`
- 支持队列上限、并发上限、单项超时、取消和过期 staging 清理
- 单个项目失败后继续处理下一项，不会删除上传字幕或修改正式字幕
- `commit` 默认拒绝已存在目标；显式允许覆盖时，旧文件先备份到任务 `backup/`
- `rollback` 会校验当前目标哈希，只处理本任务提交且未被外部修改的文件
- staging 和备份默认保留，使重复 `commit` / `rollback` 返回明确的幂等结果
- Docker 镜像已内置 Python 3、FFsubsync 和 FFmpeg，无需额外安装

调轴依赖视频中存在可分析的音轨。处理大文件时需要一定 CPU、内存和临时存储空间，ARM 设备耗时通常更长。

任务成功后状态为 `awaiting_commit`，表示候选结果仍位于 staging。全部候选提交后状态为 `completed`；回滚后重新回到 `awaiting_commit`。PR 合并或镜像发布不代表 NAS 已部署。

## 文件权限

`docker-compose.yml` 默认使用 `1000:10` 运行容器。请按宿主机媒体目录的所有者修改：

```bash
id
```

如果容器可以扫描但无法改名或调轴，通常是 `/media` 没有写权限。不要为了省事给媒体库设置 `777`，优先让容器 UID/GID 与目录所有者一致。

## API 文档

启动后访问 `http://宿主机IP:38080/api/docs` 查看 Swagger。

主要接口：

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | `/api/files/scan?dir=` | 扫描媒体目录 |
| POST | `/api/subtitles/upload` | 批量上传字幕 |
| POST | `/api/match` | 匹配视频与字幕 |
| POST | `/api/rename` | 执行改名 |
| POST | `/api/sync`、`/api/sync/tasks` | 创建 staging 调轴任务，返回 `taskId` |
| GET | `/api/sync/{taskId}/status`、`/api/sync/tasks/{taskId}` | 查询总体状态、逐项指标和有限日志 |
| POST | `/api/sync/{taskId}/cancel`、`/api/sync/tasks/{taskId}/cancel` | 取消排队中或执行中的任务 |
| POST | `/api/sync/{taskId}/commit`、`/api/sync/tasks/{taskId}/commit` | 提交通过质量门禁的候选结果；默认不覆盖 |
| POST | `/api/sync/{taskId}/rollback`、`/api/sync/tasks/{taskId}/rollback` | 回滚本任务已提交且未被外部修改的文件 |
| POST | `/api/sync/plans` | 创建纯预览调轴计划，返回唯一目标名称与逐项校验结果 |

`/api/sync/plans` 不会写入媒体目录。它支持 `subtitle_reference`、`video_global`、`video_split` 和 `no_sync` 模式；未识别语言使用稳定的 `und` 后缀，同一视频的多个字幕会生成互不冲突的候选文件名。

## 本地开发

基础功能需要 .NET 8 SDK：

```bash
dotnet restore SubRenamer.Web.sln
dotnet build SubRenamer.Web.sln -c Release --no-restore
dotnet test SubRenamer.Web.sln -c Release --no-build --filter "Category!=EndToEnd"
dotnet run --project src/SubRenamer.Web/SubRenamer.Web.csproj
```

开发工作请从独立分支发起，并通过 Pull Request 和 CI 合并。分支、提交、测试及发布门禁参见 [参与开发](CONTRIBUTING.md) 和 [开发与交付流程](docs/开发与交付流程.md)。

如果需要在非 Docker 环境测试调轴，还需安装：

- Python 3
- FFmpeg
- `ffsubsync` Python 包

真实调轴端到端测试会调用锁定版本的 FFsubsync，并由 FFmpeg 动态生成测试视频，覆盖：

- `video_global`：校正整体晚 2 秒的字幕，并验证 staging、commit 与 rollback；
- `subtitle_reference`：按参考字幕时间轴校正，同时确认输入字幕与参考字幕均未修改；
- 实验性 `video_split`：把前后两组分别晚 2 秒和 4 秒的字幕校正到同一视频时间轴。

```bash
python3 -m venv .venv-e2e
.venv-e2e/bin/python -m pip install "ffsubsync==0.5.1"

RUN_REAL_SYNC_E2E=1 \
PYTHON_EXECUTABLE="$PWD/.venv-e2e/bin/python" \
FFMPEG_EXECUTABLE=ffmpeg \
dotnet test tests/SubRenamer.Web.Tests/SubRenamer.Web.Tests.csproj \
  -c Release \
  --filter "Category=EndToEnd"
```

未设置 `RUN_REAL_SYNC_E2E=1` 时，普通单元测试不会要求开发机安装 FFmpeg 或 FFsubsync；GitHub Actions 会在独立的 `Real FFmpeg + FFsubsync E2E` 作业中强制执行该测试。

`video_split` 的 `offsetSeconds` 是 FFsubsync 对各字幕偏移量计算的中位数，仅用于汇总展示；分段模式不会对所有字幕应用同一个偏移，最终 staging 文件才是逐条校正结果的权威来源。

本地运行时可通过环境变量指定目录：

```bash
MEDIA_DIR=/path/to/media \
UPLOAD_DIR=/tmp/subrenamer-uploads \
WORK_DIR=/tmp/subrenamer-work \
dotnet run --project src/SubRenamer.Web/SubRenamer.Web.csproj
```

调轴运行参数：

| 环境变量 | 默认值 | 说明 |
|---|---:|---|
| `WORK_DIR` | `/work` | staging、manifest、日志和备份根目录 |
| `MAX_CONCURRENT_SYNCS` | `1` | 同时执行的任务数 |
| `MAX_QUEUE_SIZE` | `20` | 等待队列最多容纳的任务数 |
| `SYNC_TIMEOUT_SECONDS` | `900` | 单个字幕的默认超时 |
| `TASK_RETENTION_HOURS` | `24` | 已完成或遗留 staging 的保留时间 |
| `MAX_TASK_LOG_ENTRIES` | `200` | 每个任务最多保留的内存日志条数 |
| `PYTHON_EXECUTABLE` | `python3` | Python 可执行文件 |

Docker 构建参数 `FFSUBSYNC_VERSION` 默认锁定为 `0.5.1`。

## 项目结构

```text
SubRenamer.Web/
├── .github/workflows/         # AMD64/ARM64 镜像发布流水线
├── docs/                      # 开发、API 与迁移文档
├── CHANGELOG.md               # 版本变更与已知限制
├── src/
│   ├── SubRenamer.Core/       # 原项目核心匹配算法（零修改）
│   └── SubRenamer.Web/        # ASP.NET Core Web API + 单页前端
│       ├── Controllers/       # 扫描、上传、匹配、改名、调轴接口
│       ├── Models/            # DTO 与任务状态模型
│       ├── Services/          # 文件、改名和调轴服务
│       ├── scripts/           # FFsubsync Python 包装程序
│       └── wwwroot/           # WebUI
├── tests/                     # 单元、集成与端到端测试
├── SubRenamer.Web.sln         # 本地开发与 CI 统一入口
├── Dockerfile
├── docker-compose.yml
└── README.md
```

## 镜像发布

推送到 `main` 分支后，GitHub Actions 自动构建多架构镜像并发布：

- `beiming712/subrenamerweb:latest`
- `beiming712/subrenamerweb:main`

推送版本标签（例如 `v1.1.0`）时还会发布：

- `beiming712/subrenamerweb:1.1.0`
- `beiming712/subrenamerweb:1.1`

创建版本标签前应完成 [v1.1.0 发布检查清单](docs/v1.1.0-发布检查清单.md)，并遵循 [版本与标签规范](docs/版本与标签规范.md)。版本变化参见 [更新日志](CHANGELOG.md)。镜像发布不代表已经部署到 NAS。

## 后续计划

- [x] FFsubsync + FFmpeg 自动调轴（异步任务 + 进度轮询）
- [x] 多语言字幕一对多匹配与语言标记识别
- [x] Docker Hub AMD64/ARM64 多架构镜像发布
- [x] staging、质量门禁、显式提交与安全回滚
- [x] 三种调轴模式的真实 FFmpeg + FFsubsync 端到端验证
- [ ] 手动匹配规则编辑器（正则）
- [ ] 跨层级目录递归搜索

## 开源协议

本项目基于 GPL-2.0 授权的 `SubRenamer.Core`，整体遵循仓库中的 [GPL-2.0 License](LICENSE)。原项目地址：[qwqcode/SubRenamer](https://github.com/qwqcode/SubRenamer)。
