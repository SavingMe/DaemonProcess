# ProcessDaemon 使用说明

> **文档版本**：v1.0  
> **最后更新**：2026-04-22  
> **目标读者**：运维人员、系统管理员  
> **状态**：已发布

---

## 一、简介

ProcessDaemon 是一个轻量级的 .NET 进程守护服务。它可以管理多个 .NET 应用程序的启动、停止和监控，当子服务崩溃时自动重启，并提供 Web 控制台进行可视化管理。

### 核心能力

- ✅ 通过浏览器管理多个 .NET 服务的启停
- ✅ 崩溃后自动重启，频繁崩溃触发熔断保护
- ✅ 实时查看 CPU、内存使用情况
- ✅ 实时查看进程控制台日志输出
- ✅ 所有配置保存在本地 JSON 文件中，无需数据库

---

## 二、环境准备

### 2.1 系统要求

| 组件 | 要求 |
|------|------|
| 操作系统 | Windows 10+ / CentOS 7+ / Ubuntu 18.04+ |
| .NET Runtime | 7.0 及以上 |
| 浏览器 | Chrome / Edge / Firefox（最新两个大版本） |
| 内存 | ≥ 512MB（ProcessDaemon 自身约占 50-80MB） |

### 2.2 安装 .NET Runtime

**Windows：**
```powershell
# 通过 winget 安装
winget install Microsoft.DotNet.Runtime.7

# 或访问 https://dotnet.microsoft.com/download 下载安装包
```

**Linux (Ubuntu/Debian)：**
```bash
sudo apt-get update
sudo apt-get install -y dotnet-runtime-7.0
```

**Linux (CentOS/RHEL)：**
```bash
sudo yum install dotnet-runtime-7.0
```

验证安装：
```bash
dotnet --version
```

---

## 三、部署

### 3.1 文件准备

将发布后的文件部署到目标服务器。典型的文件结构：

```
/opt/processdaemon/               # 部署目录（示例）
├── ProcessDaemon.dll             # 主程序
├── ProcessDaemon.deps.json
├── ProcessDaemon.runtimeconfig.json
├── appsettings.json              # 应用配置
├── processes.json                # 进程配置（首次运行自动生成）
├── wwwroot/
│   └── index.html                # Web 控制台
└── manage_service.sh             # 服务管理脚本（仅 Linux）
```

### 3.2 Windows 部署

**直接运行：**
```powershell
cd C:\path\to\processdaemon
dotnet ProcessDaemon.dll
```

**作为 Windows 服务运行：**
```powershell
# 使用 sc.exe 创建服务
sc.exe create ProcessDaemon binPath= "dotnet C:\path\to\ProcessDaemon.dll" start= auto

# 启动服务
sc.exe start ProcessDaemon

# 停止服务
sc.exe stop ProcessDaemon

# 删除服务
sc.exe delete ProcessDaemon
```

### 3.3 Linux 部署（systemd）

使用自带脚本一键部署：

```bash
# 1. 赋予执行权限
chmod +x manage_service.sh

# 2. 以 root 权限运行
sudo ./manage_service.sh
```

脚本会以交互方式引导你完成配置：

```
=== 📝 填写服务配置信息 ===
1. 请输入服务名称: processdaemon
2. 请输入 dotnet 可执行文件的绝对路径 [默认: /usr/bin/dotnet]:
3. 请输入程序的工作目录: /opt/processdaemon
4. 请输入 DLL 文件的绝对路径: /opt/processdaemon/ProcessDaemon.dll
```

完成后服务将自动启动并设置为开机自启。

**手动管理服务：**
```bash
# 查看状态
sudo systemctl status processdaemon

# 启动
sudo systemctl start processdaemon

# 停止
sudo systemctl stop processdaemon

# 重启
sudo systemctl restart processdaemon

# 查看日志
sudo journalctl -fu processdaemon
```

**卸载服务：**
```bash
sudo ./manage_service.sh
# 选择 2) 一键卸载并清理服务
```

### 3.4 配置文件

**appsettings.json：**

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*"
}
```

**关键配置说明：**

| 配置项 | 说明 |
|--------|------|
| `Logging:LogLevel:Default` | 日志级别，可选 `Trace`/`Debug`/`Information`/`Warning`/`Error` |
| `AllowedHosts` | 允许的主机头，`*` 表示允许所有 |

> **注意**：进程配置不建议在 `appsettings.json` 中维护。请通过 Web 控制台管理进程配置，系统会自动保存到 `processes.json`。

**修改监听端口：**

方式一 — 通过命令行参数：
```bash
dotnet ProcessDaemon.dll --urls "http://0.0.0.0:8080"
```

方式二 — 通过环境变量：
```bash
export ASPNETCORE_URLS="http://0.0.0.0:8080"
dotnet ProcessDaemon.dll
```

---

## 四、使用指南

### 4.1 访问控制台

启动 ProcessDaemon 后，在浏览器中打开：

```
http://<服务器IP>:<端口>
```

例如：`http://localhost:5000`

### 4.2 控制台界面概览

控制台界面分为以下区域：

```
┌─────────────────────────────────────────────────┐
│  ProcessDaemon 控制台              [● 已连接]    │  ← 标题栏 + 连接状态
├─────────────────────────────────────────────────┤
│  进程监控                         [新增进程]     │  ← 操作区
├──────┬─────┬──────┬──────┬──────┬───────────────┤
│ 进程 │ PID │ CPU  │ 内存 │ 状态 │    操作       │  ← 表头
├──────┼─────┼──────┼──────┼──────┼───────────────┤
│ 服务A│12345│ 2.5% │128MB │ 运行中│终端 停止 编辑 删除│
│ 服务B│  -  │  -   │  -   │ 已停止│终端 启动 编辑 删除│
└──────┴─────┴──────┴──────┴──────┴───────────────┘
```

**连接状态指示器（右上角）：**

| 状态 | 颜色 | 含义 |
|------|------|------|
| 已连接 | 🟢 绿色 | 与服务端的 WebSocket 连接正常 |
| 连接中 | 🟡 黄色 | 正在建立连接 |
| 重连中 | 🟡 黄色 | 连接断开，正在自动重连 |
| 已断开 | 🔴 红色 | 连接已断开，需刷新页面 |
| 连接失败 | 🔴 红色 | 无法连接到服务端 |

---

### 4.3 新增进程

1. 点击右上角的 **「新增进程」** 按钮
2. 在弹窗中填写以下信息：

| 字段 | 说明 | 示例 |
|------|------|------|
| ID | 进程唯一标识，英文和数字组合 | `collector-01` |
| 名称 | 进程显示名称 | `频谱数据采集服务` |
| DLL 路径 | .NET 应用的 DLL 文件路径 | `Collector.dll` 或 `/opt/apps/Collector.dll` |
| 启动参数 | 传给应用的命令行参数（可选） | `--port 5001 --env production` |
| 启动延迟 | 启动后等待就绪的时间（毫秒） | `5000` |

3. 点击 **「保存配置」**
4. 新进程出现在列表中，状态为"已停止"

> **提示**：保存配置后进程不会自动启动，需要手动点击"启动"按钮。

> **DLL 路径说明**：
> - 相对路径：相对于 ProcessDaemon 的工作目录
> - 绝对路径：`/opt/apps/Collector.dll` 或 `C:\Apps\Collector.dll`

---

### 4.4 启动 / 停止进程

**启动进程：**
1. 在列表中找到目标进程
2. 点击 **「启动」** 按钮
3. 状态变为"运行中"，PID 列显示进程 ID

**停止进程：**
1. 点击 **「停止」** 按钮
2. 系统会终止整个进程树
3. 状态变为"已停止"

---

### 4.5 查看实时日志

1. 在列表中点击目标进程的 **「终端」** 按钮
2. 弹出终端窗口，显示进程的实时日志输出
3. 日志自动滚动到底部
4. 首次打开会加载最近 100 条历史日志
5. 关闭窗口或按 `Esc` 键退出

日志格式：
```
[14:30:25] 正在监听 http://0.0.0.0:5001
[14:30:26] 数据采集模块已初始化
[14:30:27] [ERROR] 连接数据库失败: Connection refused
```

- 普通日志：标准输出（stdout）
- `[ERROR]` 前缀：标准错误（stderr）

---

### 4.6 编辑进程配置

1. 点击 **「编辑」** 按钮
2. 弹窗中 ID 字段为灰色不可修改
3. 修改其他字段后点击 **「保存修改」**
4. 如果进程正在运行，会提示"重启后生效"

> **注意**：修改配置不会自动重启进程。需要手动停止再启动，新配置才会生效。

---

### 4.7 删除进程

1. 点击 **「删除」** 按钮
2. 确认对话框中点击"确定"
3. 如果进程正在运行，系统会先停止再删除

> **警告**：删除操作不可撤销，配置将从 `processes.json` 中永久移除。

---

### 4.8 进程状态说明

| 状态 | 颜色 | 含义 | 操作按钮 |
|------|------|------|----------|
| 运行中 | 🟢 绿色 | 进程正常运行 | 可停止 |
| 已停止 | ⚪ 灰色 | 进程被手动停止 | 可启动 |
| 自动拉起中 | 🟡 黄色 | 进程崩溃后正在自动重启 | 可停止 |
| 已熔断 | 🟣 紫色 | 进程频繁崩溃，已停止自动重启 | 可手动启动 |

**熔断机制说明：**
- 当进程在 60 秒内崩溃 5 次或以上时触发熔断
- 熔断后系统不再自动重启该进程
- 需要人工排查问题后，手动点击"启动"来解除熔断

---

## 五、配置文件手动编辑

`processes.json` 是进程配置的持久化文件，支持手动编辑。

**文件位置**：与 `ProcessDaemon.dll` 同目录

**格式示例**：
```json
[
  {
    "id": "data-collector-01",
    "name": "频谱数据采集服务",
    "dllPath": "Collector.dll",
    "arguments": "--port 5001",
    "startupDelayMs": 3000
  },
  {
    "id": "signal-analyzer-01",
    "name": "IQ 信号离线分析服务",
    "dllPath": "Analyzer.dll",
    "arguments": "--port 5002",
    "startupDelayMs": 5000
  }
]
```

**字段说明**：

| 字段 | 类型 | 必填 | 说明 |
|------|------|:----:|------|
| `id` | 字符串 | ✅ | 唯一标识，不可重复 |
| `name` | 字符串 | ✅ | 显示名称 |
| `dllPath` | 字符串 | ✅ | DLL 文件路径 |
| `arguments` | 字符串 | ❌ | 启动参数 |
| `startupDelayMs` | 整数 | ✅ | 启动延迟（毫秒），≥0 |

> **注意**：手动编辑 `processes.json` 后需要重启 ProcessDaemon 服务才能生效。

---

## 六、启动顺序与依赖管理

ProcessDaemon 启动时，会按照 `processes.json` 中配置的顺序**依次启动**每个进程。

**启动流程**：
```
启动进程 A → 等待 A 的 StartupDelayMs → 启动进程 B → 等待 B 的 StartupDelayMs → ...
```

**如何利用启动顺序管理依赖**：

假设"分析服务"依赖于"采集服务"，则在 `processes.json` 中将采集服务放在前面：

```json
[
  {
    "id": "collector",
    "name": "数据采集服务",
    "dllPath": "Collector.dll",
    "startupDelayMs": 5000    // 启动后等待 5 秒再启动下一个
  },
  {
    "id": "analyzer",
    "name": "数据分析服务",
    "dllPath": "Analyzer.dll",
    "startupDelayMs": 3000
  }
]
```

**StartupDelayMs 设置建议**：

| 服务类型 | 建议延迟 |
|----------|----------|
| 无外部依赖的工具类服务 | 1000 - 2000ms |
| Web API 服务 | 3000 - 5000ms |
| 需要初始化数据库/缓存的服务 | 5000 - 10000ms |
| 最后启动的服务 | 可设为 0 |

---

## 七、运维监控

### 7.1 查看 ProcessDaemon 自身日志

**Linux（systemd）：**
```bash
# 实时跟踪日志
sudo journalctl -fu processdaemon

# 查看最近 100 行
sudo journalctl -u processdaemon -n 100

# 查看今天的日志
sudo journalctl -u processdaemon --since today
```

**Windows：**
```powershell
# 控制台模式直接看输出
dotnet ProcessDaemon.dll
```

### 7.2 常用日志关键字

| 日志关键字 | 级别 | 含义 |
|-----------|------|------|
| `已启动 {Name}，PID: {Pid}` | Information | 进程启动成功 |
| `启动后立即退出，ExitCode=` | Error | 进程秒退（DLL 不存在等） |
| `检测到 {Name} 已停止，准备自动重启` | Warning | 检测到崩溃，准备重启 |
| `[熔断] {Name} 崩溃过于频繁` | Error | 触发熔断保护 |
| `[熔断] {Name} 启动后反复秒退` | Error | 秒退导致熔断 |
| `已手动停止 {Name}` | Information | 用户手动停止 |
| `启动 {Name} 失败` | Error | 进程启动异常 |
| `等待 {Name} 就绪` | Information | 启动延迟等待中 |

---

## 八、常见问题排查

### Q1：进程显示"运行中"但实际上服务不可用

**可能原因**：进程在运行但应用层面发生了错误（端口冲突、配置错误等）。

**排查方法**：
1. 点击"终端"查看实时日志
2. 检查日志中是否有 `[ERROR]` 前缀的错误信息
3. 确认 DLL 路径和参数是否正确

---

### Q2：进程状态一直显示"已熔断"

**原因**：进程在 60 秒内崩溃了 5 次或以上。

**排查步骤**：
1. 点击"终端"查看崩溃前的日志
2. 检查 DLL 文件是否存在
3. 检查启动参数是否正确
4. 检查依赖服务是否已启动
5. 修复问题后，点击"启动"按钮手动恢复

---

### Q3：点击"启动"后状态仍然是"已停止"

**原因**：进程启动后立即退出（秒退），通常是 DLL 路径错误。

**排查方法**：
1. 查看 ProcessDaemon 的控制台日志，搜索"启动后立即退出"
2. 确认 DLL 文件路径正确且文件存在
3. 尝试手动运行：`dotnet <DLL路径> <参数>` 查看具体错误

---

### Q4：Web 控制台无法访问

**排查步骤**：
1. 确认 ProcessDaemon 服务正在运行
2. 确认访问地址和端口正确
3. 检查防火墙是否允许对应端口
4. 查看 ProcessDaemon 日志是否有启动错误

**Linux 防火墙开放端口**：
```bash
sudo firewall-cmd --zone=public --add-port=5000/tcp --permanent
sudo firewall-cmd --reload
```

---

### Q5：连接状态显示"已断开"或"连接失败"

**原因**：WebSocket 连接断开。

**解决方法**：
1. 系统会自动尝试重连
2. 如果持续失败，按 `F5` 刷新页面
3. 检查网络连接和服务端是否正常

---

### Q6：进程的 CPU / 内存显示为 `-`

**原因**：进程未运行时不采集指标。

**说明**：只有状态为"运行中"的进程才会显示 CPU 和内存数据，更新频率为每 3 秒。

---

### Q7：修改了配置但不生效

**原因**：正在运行的进程不会自动应用新配置。

**解决方法**：
1. 点击"停止"停止进程
2. 点击"启动"重新启动
3. 新配置将在启动时生效

---

## 九、安全注意事项

> ⚠️ **重要提醒**：当前版本没有内置认证机制。

**生产环境部署建议**：

1. **使用反向代理**：通过 Nginx/Caddy 添加 Basic Auth 或 IP 白名单

   ```nginx
   # Nginx 示例 - IP 白名单
   server {
       listen 80;
       server_name daemon.example.com;

       allow 10.0.0.0/8;
       allow 192.168.0.0/16;
       deny all;

       location / {
           proxy_pass http://localhost:5000;
           proxy_http_version 1.1;
           proxy_set_header Upgrade $http_upgrade;
           proxy_set_header Connection "upgrade";
           proxy_set_header Host $host;
       }
   }
   ```

2. **仅绑定内网地址**：
   ```bash
   dotnet ProcessDaemon.dll --urls "http://10.0.1.100:5000"
   ```

3. **使用防火墙限制访问**：仅允许管理员 IP 访问控制台端口
