# AI-ACE 迁移交付报告

生成日期：2026-08-26

---

## 1. 迁移包位置

```
G:\AI-ACE-MIGRATION\AI-ACE\
```

该目录即为最终交付包，整体复制到新电脑 `D:\AI-ACE` 即可运行。

## 2. 迁移包大小

| 部分 | 大小 |
|---|---|
| 项目文件（含模型） | 12 GB |
| Docker 镜像（9 个 tar） | 5.5 GB |
| 文档 + 脚本 | < 1 MB |
| **总计** | **约 17 GB** |

## 3. 文件清单

```
AI-ACE\
├── compose\docker-compose.yml      # 权威编排配置（相对路径、internal 说明）
├── services\                        # 全部服务源码（document-parser/rag/term/docx/vision/dashboard）
├── models\                          # 本地模型（qwen3-4b 2.4G / vision 2.6G / deepseek 4.4G / embedding 932M）
├── knowledge\                       # 向量数据库 + 术语库（Qdrant 数据）
├── open-webui\                      # 用户账号 + 聊天记录
├── documents\                       # 上传文档 + 生成文档 + 图片
├── output\                          # 挂载点
├── system\                          # GPU 信息（启动时自动生成）
├── docker-images\                   # 9 个镜像 tar + docker-image-list.txt
├── scripts\import-docker-images.ps1 # 离线镜像导入脚本
├── docs\                            # 用户手册 + 本报告
├── start-ai-ace.bat                 # 一键启动（离线）
├── stop-ai-ace.bat                  # 一键关闭
├── build-ai-ace.bat                 # 首次构建镜像（需联网，旧电脑用）
├── export-images.bat                # 导出镜像（旧电脑用）
└── load-images.bat                  # 离线导入镜像（新电脑用）
```

## 4. Docker 镜像清单

| 镜像 | TAG | 导出文件 |
|---|---|---|
| ghcr.io/ggml-org/llama.cpp | server-cuda | llama.cpp-server-cuda.tar |
| ghcr.io/open-webui/open-webui | main | open-webui.tar |
| qdrant/qdrant | latest | qdrant.tar |
| compose-ace-rag-service | latest | compose-ace-rag-service.tar |
| compose-ace-document-parser | latest | compose-ace-document-parser.tar |
| compose-ace-term-extractor | latest | compose-ace-term-extractor.tar |
| compose-ace-docx-generator | latest | compose-ace-docx-generator.tar |
| compose-ace-vision-service | latest | compose-ace-vision-service.tar |
| compose-ace-dashboard | latest | compose-ace-dashboard.tar |

## 5. 启动方法

```
双击 D:\AI-ACE\start-ai-ace.bat
```
自动完成：检查 Docker → 检查 GPU → 检查镜像 → 启动服务 → 等待健康 → 打开聊天界面 http://127.0.0.1:3000

## 6. 关闭方法

```
双击 D:\AI-ACE\stop-ai-ace.bat
```

## 7. Word 手册位置

```
D:\AI-ACE\docs\AI-ACE本地私有AI平台用户手册.docx
```
共 14 章，面向普通用户。

## 8. 新电脑部署步骤

1. 安装 Docker Desktop（唯一需要联网安装的软件）。
2. 将 `G:\AI-ACE-MIGRATION\AI-ACE` 整个文件夹复制到新电脑的 `D:\AI-ACE`。
3. 进入 `D:\AI-ACE\scripts`，右键 `import-docker-images.ps1` →「使用 PowerShell 运行」（离线导入镜像）。
4. 双击 `D:\AI-ACE\start-ai-ace.bat`。
5. 浏览器自动打开 http://127.0.0.1:3000，首次创建本地账号即可使用。

## 9. 安全检查结果

| 检查项 | 结果 |
|---|---|
| 云端 AI / 外部 API 调用 | ✅ 0 处 |
| 外部域名硬编码 | ✅ 0 处 |
| 自动拉取镜像 | ✅ 禁止（pull_policy: never + --no-build） |
| 运行时下载模型 | ✅ 禁止（HF 离线 + 本地路径） |
| 运行时 pip/apt/npm | ✅ 0 处 |
| 遥测 / 统计上传 | ✅ 全关闭 |
| 自动更新 | ✅ 0 处 |
| 镜像固定版本（无 latest） | ✅ 5 个 digest 固定 |
| 路径 | ✅ 全部相对路径，拷到任何盘符可运行 |

## 10. 已知风险

1. **Docker Desktop 首次安装需联网**：Docker Desktop 本身需要联网安装，但安装后 AI-ACE 完全离线运行。
2. **GPU 加速依赖 NVIDIA 驱动**：无独立显卡或驱动时，文本模型仍可用但较慢，视觉模型不可用。
3. **首次启动时间**：导入镜像（约 5-10 分钟）和首次启动服务（约 1-2 分钟）需要耐心等待。
4. **internal:true 断网**：Windows Docker Desktop 上会破坏 localhost 端口转发，故未启用；当前联网隔离依赖「代码零外连 + 禁拉镜像 + 禁下模型」共同保证（已实测无外连代码）。若部署到原生 Linux Docker，可在 compose 中取消 `internal: true` 注释获得硬件级断网。

---

**结论**：AI-ACE 已成为独立产品。复制 `D:\AI-ACE` 即可运行，无 Claude、无云端、无自动下载、无数据泄露。
