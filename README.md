# SubRenamer.Web

基于 [SubRenamer](https://github.com/qwqcode/SubRenamer) 核心算法的 Docker + WebUI 改造版。

字幕批量改名工具,复用原项目 `SubRenamer.Core` 匹配算法(零修改),提供浏览器 Web 界面,容器化部署。

## 特性

- **混合模式**:视频从挂载目录读取,字幕通过浏览器上传
- **同文件夹改名**:挂载目录里的字幕可直接改名(支持 SubBackup 备份)
- **一键匹配**:调用 SubRenamer.Core diff 算法自动关联视频与字幕
- **结果微调**:匹配后可在前端手动调整对应关系
- **Docker 部署**:开箱即用,挂载媒体库即用

## 快速开始

```bash
# 1. 准备媒体目录(放视频文件)
mkdir -p media && cp -r /path/to/your/videos media/

# 2. 构建并启动
docker compose up -d --build

# 3. 打开浏览器
open http://localhost:8080
```

也可用 `docker run`:
```bash
docker build -t subrenamer-web .
docker run -d -p 8080:8080 -v /path/to/media:/media subrenamer-web
```

## 使用流程

1. **扫描** — 输入子目录(留空扫全部)→ 点击扫描,列出视频与挂载字幕
2. **上传** — 拖拽字幕文件到上传区(支持批量)
3. **匹配** — 点击"一键匹配",自动关联视频与字幕
4. **微调** — 在结果表格里下拉调整错误对应
5. **改名** — 勾选备份、填语言后缀(可选)→ 执行改名

字幕会重命名后写入视频所在目录。挂载目录内的字幕走直接改名,上传的字幕走拷贝。

## 文件权限

容器默认以 UID 1000 运行。若写入媒体库时权限不足,在 `docker-compose.yml` 取消 `user` 注释,改成你的 UID:GID(终端 `id` 查看)。

## API 文档

启动后访问 `http://localhost:8080/api/docs`(Swagger)。

主要接口:

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | `/api/files/scan?dir=` | 扫描挂载目录 |
| POST | `/api/subtitles/upload` | 上传字幕(批量) |
| POST | `/api/match` | 匹配视频与字幕 |
| POST | `/api/rename` | 执行改名 |
| POST | `/api/sync` | 调轴(占位,后续接入) |

## 本地开发

需 .NET 8 SDK:
```bash
cd src
dotnet sln add SubRenamer.Core/SubRenamer.Core.csproj
dotnet sln add SubRenamer.Web/SubRenamer.Web.csproj
# 或直接运行 Web 项目
dotnet run --project SubRenamer.Web/SubRenamer.Web.csproj
```

## 项目结构

```
SubRenamer.Web/
├── src/
│   ├── SubRenamer.Core/      # 复用原项目核心匹配算法(零修改)
│   └── SubRenamer.Web/       # Web API + 静态前端
│       ├── Models/           # DTO
│       ├── Services/         # 扫描/上传/改名服务
│       ├── Controllers/      # API 控制器
│       └── wwwroot/          # 单页前端
├── Dockerfile
├── docker-compose.yml
└── README.md
```

## 后续计划

- [ ] 接入 FFsubsync + FFmpeg 实现自动调轴(异步任务 + 进度轮询)
- [ ] 手动匹配规则编辑器(正则)
- [ ] 多语言字幕一对多匹配增强

## 开源协议

核心算法 `SubRenamer.Core` 遵循原项目 [GPL-2.0](https://github.com/qwqcode/SubRenamer/blob/main/LICENSE) 协议。