**简体中文** | [English](README.en.md)

# GE2RB 存档管理器

《GOD EATER 2 Rage Burst》存档管理 / 跨平台互转工具（Windows，.NET Framework 4.8，WinForms）。

## 功能

### 存档识别与信息

拖入包含 `SECURE.BIN` 的存档文件夹后自动识别平台并解密，显示：

| 项 | 说明 |
| --- | --- |
| 存档路径 | 拖入的文件夹路径 |
| 平台 | PS亚洲版 / PS欧美版 / Steam版 |
| 名字 | 存档中的玩家名 |
| 代号 | 存档中的玩家代号 |

平台由存档内的标记自动判定，无需手动选择。

### 平台互转

界面上是两个并排的转换目标按钮，按当前来源平台自动指向另外两个平台：

- PS亚洲版 ↔ Steam版：直接转换
- PS欧美版 ↔ Steam版：直接转换
- PS亚洲版 ↔ PS欧美版：先转 Steam版，再转目标版本（两步中转）

### Steam 用户列表

读取 `<Steam>\config\loginusers.vdf`，把 `userdata` 下的 SteamID 目录名显示为 `昵称（登录账户名）`。

### 槽位管理

每个账户 3 个槽位，每行提供：

- **槽位按钮**：显示该槽位存档的玩家名
- **备份**：把该槽位复制到 exe 同目录的 `backups\`
- **删除**：清空该槽位下所有文件（二次确认）
- **替换**：用拖入的存档替换该槽位；来源非 Steam版 会先转换，替换完成后询问是否修复 vdf

### 其他

- **修复 vdf**：修复 Steam 云存档状态，解决卡片加载错误（可能要求先关闭 Steam）
- **打开备份**：打开备份目录
- **Steam 路径检测**：优先读注册表（`InstallPath` / `SteamPath` / `SteamExe`）；检测不到或目录无效时，弹出资源管理器风格的文件夹选择窗口让用户手动指定
- **兜底重选**：未选定有效 Steam 路径时，点击「备份 / 删除 / 替换 / steam 用户列表」会弹窗提示并重新触发路径选择

## 环境

- Windows
- 运行时：.NET Framework 4.8
- 构建：.NET SDK（可构建 `net48`）或 Visual Studio 2022

## 构建

```bash
dotnet build -c Release
```

产物：`GE2RBSaveManager/bin/Release/net48/GE2RBSaveManager.exe`

## 目录结构

```
.
├── README.md                     # 本文档（简体中文）
├── README.en.md                  # English
├── .gitignore
└── GE2RBSaveManager/
    ├── GE2RBSaveManager.csproj
    ├── Program.cs                # 全部逻辑（GUI + 转换核心）
    ├── ps_asia_steam_main.txt    # 嵌入资源
    ├── ps_asia_steam_card.txt    # 嵌入资源
    ├── ps_us_steam_main.txt      # 嵌入资源
    └── ps_us_steam_card.txt      # 嵌入资源
```

## 嵌入资源

以下 4 个 `*.txt` 通过 `<EmbeddedResource>` 打进 exe（单文件自包含分发），是平台互转所用的补丁数据（结构性的插入 / 删除补丁）：

| 文件 | 用途 |
| --- | --- |
| `ps_asia_steam_main.txt` | PS亚洲版 → Steam版（主存档） |
| `ps_asia_steam_card.txt` | PS亚洲版 → Steam版（卡片存档） |
| `ps_us_steam_main.txt` | PS欧美版 → Steam版（主存档） |
| `ps_us_steam_card.txt` | PS欧美版 → Steam版（卡片存档） |

## 免责声明

本工具仅用于个人存档管理与文件格式研究。操作前请自行备份存档，使用风险自负。
