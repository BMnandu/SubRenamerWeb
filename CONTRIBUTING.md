# 参与开发

本项目采用“小分支、小提交、Pull Request 验证”的开发方式。`main` 只保存经过 CI 验证、可构建的代码；生产部署不属于普通 PR 的默认动作。

完整流程参见 [`docs/开发与交付流程.md`](docs/开发与交付流程.md)。

## 快速开始

```bash
git switch main
git pull --ff-only
git switch -c feat/<主题>

dotnet restore SubRenamer.Web.sln
dotnet build SubRenamer.Web.sln -c Release --no-restore
dotnet test SubRenamer.Web.sln -c Release --no-build
```

提交前请确认：

- 改动只覆盖当前任务范围；
- 新行为有测试，修复先补失败复现；
- README、API 与迁移说明同步更新；
- 不提交密钥、Cookie、真实媒体数据、构建产物或临时文件；
- 不从开发流程直接操作 NAS 或生产媒体目录。

提交信息使用简体中文，建议采用以下前缀：

```text
feat: 增加调轴计划接口
fix: 修复媒体目录边界判断
test: 补充低质量拒绝测试
docs: 更新迁移说明
refactor: 拆分安全路径服务
ci: 增加 Pull Request 构建校验
chore: 更新开发工具配置
```
