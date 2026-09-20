# 教室上网管控系统 (Classroom NetGuard)

单间教室局域网白名单上网管控系统：教师端集中管控，学生端自动拦截。

- **教师端**（Windows WPF）：班级设备管理、网站白名单、下载策略、按设备解锁密码、实时日志
- **学生端**（Windows 服务 + 托盘）：连接教师端获取策略，本地代理 127.0.0.1:8888 实施白名单拦截（含 HTTPS 拦截页），无需浏览器扩展
- **部署形态**：教师机运行教师端；学生机一键安装（`setup.bat`），纯局域网运行，不上外网

> 发布包下载见 [GitHub Releases](https://github.com/mygelling/classroom-net-guard/releases)：
> - `TeacherConsole.zip` — 教师端发布包（解压直接运行）
> - `student.zip` — 学生端安装包内容（拷入学生机运行 `setup.bat`）

---

## 功能特性

| 模块 | 功能 |
|---|---|
| 教师端主界面 | 班级设备图标墙（按计算机名排序）、在线/离线/放行状态、双击卡片查看详情 |
| 管控模式 | 右上角全局切换「课堂管控 / 自由模式」；每台设备可单独切换 |
| 网站白名单 | 纯白名单制（默认全拦）；支持域名、通配域 `*.edu.cn`、IP、IP 段 `192.168.1.*`、CIDR `192.168.1.0/24`、`IP:端口` |
| 下载管控 | 代理层判定 http 下载（按类型/大小策略）；https 隧道内暂不识别 |
| 解锁密码 | 按设备独立设置；默认密码 = 计算机名转数字 × 当日日期转数字，取后 6 位；支持一键生成与 CSV 导出 |
| 网页解锁 | 拦截页输入密码临时放行 30 分钟；教师端随时「重新拦截」 |
| 切回管控自动生效 | 未加载完的页面直接显示"访问已被拦截"；已加载完的页面经受控 Edge（CDP）自动刷新重新判定 |
| 日志 | 教师端实时日志（上线/下线/解锁/策略；学生端不实时上报访问拦截日志，避免多机同时上报堵塞教师机） |
| 数据安全 | 纯局域网，不依赖任何外部服务；学生端无退出入口，重启自动生效 |

## 系统架构

```
┌─────────────────────────────┐         ┌──────────────────────────────┐
│        教师端 (WPF)          │         │        学生端 (Windows)       │
│  TCP 9999 策略服务器         │◄────────►│  服务 NetGuardStudentService │
│  UDP 9998 设备发现           │  hello/  │   ├─ 连接教师端获取策略      │
│  ├─ 白名单/下载/模式/密码    │  policy/ │   ├─ 本地代理 127.0.0.1:8888 │
│  └─ 实时日志                 │  log/ack │   │    ├─ HTTP 拦截页注入     │
└─────────────────────────────┘         │   │    └─ HTTPS MITM 拦截页   │
                                        │   ├─ Edge 调试端口 9222 (CDP) │
                                        │   └─ 本地策略 API 8890        │
                                        │  托盘 StudentTray            │
                                        │   └─ 设置系统代理(WinINET)    │
                                        └──────────────────────────────┘
```

- 通信协议：TCP 帧 = 4 字节大端长度前缀 + UTF-8 JSON（WireMessage）
- 策略权威文件：`C:\ProgramData\NetGuard\teacher-policy.json`
- HTTPS 拦截：本地生成 CA（`C:\ProgramData\NetGuard\cert\ca.pfx`），安装时导入学生机系统信任根

## 目录结构

```
上网控制/
├── ClassroomNetGuard/            # 主工程（解决方案：上网管控系统.sln）
│   ├── src/
│   │   ├── ClassroomNetGuard.Shared/   # 共享库：策略模型 / 协议 / 常量
│   │   ├── TeacherConsole/             # 教师端 WPF
│   │   ├── StudentService/             # 学生端服务（代理 / 策略 / 解锁 / CDP 刷新）
│   │   ├── StudentTray/                # 学生端托盘（系统代理）
│   │   └── StudentInstaller/           # 学生端安装器
│   ├── publish/                        # 本地发布输出（教师端/学生端）
│   └── scripts/                        # 端到端回归测试脚本 (PowerShell)
├── 学生端安装包/                       # 学生机安装介质（setup.bat + student.zip + 说明）
└── 上网管控原型/                       # 早期网页原型（可删除）
```

## 编译

要求：Windows + .NET 8 SDK。

```powershell
# 教师端（自包含 64 位）
dotnet publish .\ClassroomNetGuard\src\TeacherConsole\TeacherConsole.csproj -c Release -r win-x64 --self-contained true -o .\ClassroomNetGuard\publish\teacher

# 学生端（服务 + 托盘）
dotnet publish .\ClassroomNetGuard\src\StudentService\StudentService.csproj -c Release -r win-x64 --self-contained true -o .\ClassroomNetGuard\publish\student
dotnet publish .\ClassroomNetGuard\src\StudentTray\StudentTray.csproj    -c Release -r win-x64 --self-contained true -o .\ClassroomNetGuard\publish\student
```

发布后把 `publish\student` 全部内容打成 `student.zip` 放入 `学生端安装包\`，即构成安装介质。

## 部署

### 教师端
1. 解压 `TeacherConsole.zip`，运行 `TeacherConsole.exe`（无需安装 .NET 运行时）
2. 首次启动确认本机局域网 IP（学生端安装时填写）

### 学生端（每台学生机）
1. 拷贝「学生端安装包」文件夹到学生机，双击 `setup.bat`（需管理员权限）
2. 安装时输入教师端 IP（回车则自动搜索）
3. 安装程序自动完成：卸载旧版 → 解压安装 → 注册服务（开机自启）→ 导入 HTTPS 证书 → 创建受控 Edge 快捷方式 → 启动托盘设置系统代理
4. 学生统一通过桌面 **「Edge 学生浏览器」** 打开网页（带远程调试端口，支持切回管控自动刷新）

### 部署后检查
- 右下角出现盾牌托盘图标，双击显示"已连接教师端 · 课堂管控"
- 白名单外网站显示红色"访问已被拦截"页；白名单内正常
- 浏览器需在代理生效后重新打开

## 使用说明

### 教师端操作
- **切换管控**：右上角「课堂管控 / 自由模式」全局切换；设备卡片下方按钮单独切换
- **白名单**：右上角设置 → 网站白名单 → 保存即时下发（版本号 +1）
- **解锁密码**：设备卡片设置密码，或「默认密码」一键生成（计算机名×日期后 6 位）；右上角「导出密码」CSV
- **重新拦截**：学生解锁放行中，设备卡片出现红色「重新拦截」按钮

### 学生端拦截页解锁
拦截页输入教师下发的密码 → 「解锁上网」→ 临时放行 30 分钟 → 到期自动恢复管控；未设置密码的学生机无法自行解锁。

## 常见问题

| 现象 | 处理 |
|---|---|
| 双击 TeacherConsole.exe 无反应 | 检查是否为最新发布包；查看 Windows 事件日志 |
| 学生端托盘"服务未运行" | 重启服务：`sc start NetGuardStudentService`；重跑 setup.bat |
| https 显示"无法访问此页面" | 证书未信任：`certutil -addstore -f Root "C:\ProgramData\NetGuard\cert\ca.pfx"` 后重启浏览器 |
| 切回管控已打开页面不刷新 | 学生机必须使用桌面「Edge 学生浏览器」快捷方式打开网页 |
| 修改教师端 IP | 编辑 `C:\ProgramData\NetGuard\config.json` 的 `TeacherHost`，重启服务 |
| 内网 http 站点打不开/转圈 | 白名单需包含该 IP；使用最新安装包（代理已修复连接复用） |

## 技术要点

- **纯白名单制**：未收到策略默认全拦；`IsDomainAllowed` 支持域名/通配/IP/CIDR/IP 段/端口匹配
- **HTTP 拦截**：代理读响应头判定下载；管控恢复时对未开始响应的连接注入 403 拦截页
- **HTTPS 拦截**：CONNECT 隧道内 TLS 终结 + 本地 CA，返回拦截页
- **自动刷新**：受控 Edge 开启 `--remote-debugging-port=9222`，学生端经 CDP `Page.reload(ignoreCache:true)` 强制刷新已加载页面
- **解锁**：`UnlockState` 30 分钟计时；教师端下发任何新策略即撤销解锁
- **回归测试**：`ClassroomNetGuard\scripts\*.ps1`（假教师端 + 帧协议，覆盖拦截页注入、CDP 刷新、连接复用、放行日志等）

## 版本历史

- **v1.0.0**：完整系统首个发布版（教师端/学生端/安装包）
- 历史提交见 git log（初始 → 图标 → 内网 404 修复 → 放行日志 → 名称排序 → 全局切换 → 注入拦截页 → CDP 自动刷新）

## 许可

内部教学使用。教师端含管控逻辑，请勿在学生可访问位置公开分发源码。
