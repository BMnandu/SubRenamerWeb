## 变更摘要

- （请填写）

## 变更类型

- [ ] 修复
- [ ] 功能
- [ ] 重构
- [ ] 测试或文档
- [ ] 构建、CI 或依赖

## 验证

- [ ] `dotnet restore SubRenamer.Web.sln`
- [ ] `dotnet build SubRenamer.Web.sln -c Release --no-restore`
- [ ] `dotnet test SubRenamer.Web.sln -c Release --no-build --filter "Category!=EndToEnd"`
- [ ] 涉及调轴时已执行 `Category=EndToEnd` 真实 FFmpeg + FFsubsync 测试
- [ ] 涉及容器时已完成 Docker 构建

实际执行结果：

```text

```

## 安全与兼容性

- [ ] 未提交凭据、真实媒体路径或敏感日志
- [ ] 文件写入、删除和覆盖行为已经检查
- [ ] API 或配置变化已有兼容/迁移说明
- [ ] 本 PR 未执行生产部署

## 关联规划

- 项目规划或 Issue：
- 已知限制与后续事项：
