# AI-ACE · 可离线运行的 AI 桌面工作台

一个完全跑在自己机器上的 AI 助手：原生 Windows 桌面端 + Docker 本地服务栈，断网也能用，对话与文件不出盘。

- **桌面端**：C# WinForms 原生窗口（不是套壳网页），双击一个 exe 完成安装，装完直接进聊天界面
- **模型**：本地 Qwen3-4B（llama.cpp + CUDA），另带视觉模型与文档解析
- **能力**：多轮对话、知识库问答（RAG）、文档解析与生成、文件读写、代码执行、任务式自主执行（会记经验、可并行）
- **数据**：默认存放在 `D:\AI-ACE-data`，五个子目录统一由一个 `.env` 管；托盘「数据位置…」可整库搬家
- **安全**：所有服务只监听 `127.0.0.1`，附 `安全/联网隔离.ps1` 可拦截容器出网并自证

## 本仓库包含什么

本仓库是**桌面端 + 部署工具链**，约 1.4 MB：

| 目录 / 文件 | 说明 |
|---|---|
| `AI-ACE.exe` | 安装向导 + 启动器（同一份程序，按 `system\install-state.json` 判断角色） |
| `desktop\src\` | 桌面端全部 C# 源码（10 个 .cs），`build.bat` 用系统自带 csc 编译，无需装 SDK |
| `desktop\安全\` | 联网隔离脚本（iptables 链 AIACE_EGRESS + 自证探针） |
| `desktop\数据迁移.ps1` | 把整个数据目录搬到另一个盘 |
| `compose\docker-compose.yml` | 13 个服务的编排（qdrant / 文本 / 视觉 / 解析 / RAG / 文件 / 智能体 / 文档生成 / WebUI / 面板 / 网关） |
| `services\` | 各服务源码（Python） |
| `docs\`、`部署到另一台机器.md`、`看这里.txt` | 完整部署与排障文档（含 25 个章节） |

**不包含**（体积原因）：`models\`（Qwen3-4B 等约 5.3 GB）与 `docker-images\`（9 个镜像 tar 约 5.5 GB）。这两个目录构成完整离线包的主体，需单独获取；缺它们时向导会停在「缺镜像」这一步并列出缺哪些。

## 快速开始

前置：Windows 10/11 x64、Docker Desktop（WSL2 后端）、NVIDIA 显卡与驱动、内存 ≥16 GB、磁盘 ≥20 GB 空闲；**安装目录不能在 C 盘**（向导会直接拒绝，C 盘只留给系统）。

1. 拿到完整离线包（本仓库 + models + docker-images）后，把整个文件夹拷到目标机，**放在非 C 盘**
2. 双击包根 `AI-ACE.exe`
3. 之后不用点任何东西：向导自动完成 5 步 —— 环境检查 → 定目录（默认 `D:\AI-ACE`）→ 逐个 `docker load` 镜像 → 保留旧数据 → `compose up` 并等 3000 端口健康
4. 装完自动进入桌面端主界面，不会打开浏览器

装好后再双击同一个 `AI-ACE.exe` 就是启动器：起服务、等就绪、开窗口。

## 数据与隐私

- 对话记录、上传文件、知识库、数据库、输出产物全部在 `D:\AI-ACE-data`（可改），一个都不落在 C 盘
- 端口只绑 `127.0.0.1`，同一局域网的其他机器也访问不到
- 需要更硬的隔离时跑 `desktop\安全\联网隔离.ps1 -Apply`：在 docker-desktop 的 WSL 里建链，放行内网、丢弃其余出网；Docker 重启后规则会消失，重跑一次即可
- 发布包里不含任何人的对话数据库：`open-webui\webui.db` 被移除，首次启动由使用者自己注册管理员

## 目录结构（运行时）

```
AI-ACE\
├─ AI-ACE.exe            安装向导 / 启动器
├─ compose\              编排 + .env（AIACE_DATA=数据根）
├─ services\             各服务源码
├─ models\               Qwen3-4B 等模型（不随本仓库）
├─ docker-images\        镜像 tar（不随本仓库）
├─ system\               install-state.json / install.log
└─ desktop\              桌面端源码与工具
D:\AI-ACE-data\           open-webui documents output knowledge database
```

## 常见问题

**卡在「载入镜像」很久？** 9 个 tar 共 5.5 GB，机械盘或 U 盘上十几分钟正常，窗口别关，进度条会走。

**提示 Docker 没起来？** 向导会自己尝试拉起 Docker Desktop 并最多等 180 秒；仍失败就手动打开 Docker Desktop，等鲸鱼图标稳定后点「重试部署」。

**想换数据盘？** 托盘 →「数据位置…」，它会停服务、搬数据、改 `.env`、再起服务；也可以跑 `desktop\数据迁移.ps1 -From D:\AI-ACE-data -To E:\AI-ACE-data`。

**装完打不开聊天？** 看 `AI-ACE\system\install.log` 与 `%APPDATA%\Docker\log.txt`；桌面端托盘「查看日志」也能直接打开。

## 第三方组件

本项目把若干开源组件打包成离线镜像分发，版权归各原作者，详见 [THIRD-PARTY.md](THIRD-PARTY.md)。模型权重（Qwen3-4B）遵循其原始许可，镜像内的 Open WebUI、Qdrant、llama.cpp 等同样遵循各自许可。

## 说明

代码与文档供学习交流使用；未附开源许可证，转载或商用请先联系作者。
