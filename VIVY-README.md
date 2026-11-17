# MewUI（Vivy fork · 维护说明）


`src/Vivy` 的 UI 自管理链路使用 MewUI（Win32 平台 + Direct2D 后端）作为窗口、消息泵、
控件树、渲染节拍的宿主框架。这里是 **aprillz/MewUI 的 fork**，Vivy 的改动以 `vivy` 分支
叠在上游之上——上游仍在活跃更新，改动要能随时 rebase 回上游。

## 仓库形态

| 项 | 值 |
|---|---|
| 上游 | <https://github.com/aprillz/MewUI>（`upstream`） |
| Vivy fork | <https://github.com/vvyoko/MewUI>（`origin`） |
| **Vivy 分支** | `vivy`（`origin/vivy`，所有本地补丁都在这里） |
| `main` | 只跟踪 `upstream/main`，永远不提交本地改动 |
| 基线 commit | `553f28d96df9bb1df7bc69bba2990dade71b760e` |
| 上游版本（`build/MewUI.Common.props`） | `0.22.1` |

- 本目录被 Vivy 仓库的 `.gitignore` 忽略：它有自己的 `.git`，但**不是 submodule**，
  不会把两套历史缠在一起。`src/Vivy/Vivy.csproj` 直接 `ProjectReference` 这里的三个工程，
  所以路径必须保持 `Libraries/MewUI/`。
- 上游全部工程都在（GDI / MewVG / Browser / X11 / MacOS / Headless / Preview 等），
  Vivy 只引用 `MewUI`、`MewUI.Platform.Win32`、`MewUI.Backend.Direct2D` 三个，其余不参与编译。
- 上游是 MIT，fork 与再分发没问题；`LICENSE` 保持原样。

## 换一台机器：还原

```powershell
cd Vivy/Libraries
git clone --branch vivy https://github.com/vvyoko/MewUI.git MewUI
cd MewUI
git remote add upstream https://github.com/aprillz/MewUI.git
```

`vivy` 上的提交就是全部补丁，代码逐字一致，没有需要手工重放的东西。
补丁的**意图**与「上游若已自行实现就删掉这段」的判断看 `docs/internal/mewui.md` 的表格。

## 拉上游并同步补丁（升级流程）

```powershell
cd Libraries/MewUI
git fetch upstream
git switch main && git merge --ff-only upstream/main   # main 永远与上游一致
git switch vivy && git rebase upstream/main            # 冲突只可能在补丁文件里
git push --force-with-lease origin vivy                # rebase 改写了历史，只能强推自己的分支
cd ../..; dotnet test src/Vivy.Tests/Vivy.Tests.csproj
dotnet build src/Vivy/Vivy.csproj -c Release
```

- 撞冲突时按「上游的新形态 + Vivy 需要的接缝」重写，别退回旧代码；补丁意图看
  `docs/internal/mewui.md` 的对应行。
- rebase 完成后回填上表的基线 commit / 版本号，并在 `docs/internal/mewui.md` 追加一条升级记录。
- `dotnet test` + Release 构建（以及发版前的 AOT publish）是升级的验收条件：
  MewUI 的窗口 / 渲染 / 输入是宿主整条链路的底座。
- `--force-with-lease` 只对 `origin/vivy` 用：那是 Vivy 自己的分支，改写历史是常态。

## vivy 分支上的提交

| 提交 | 内容 |
|---|---|
| `Vivy 补丁：窗口延迟显示、窗口内弹层、NC 缩放带、侧键` | `src/MewUI` 下 4 个文件，逐条对应 `docs/internal/mewui.md` 的表格 |
| `Vivy 工程接入：屏蔽仓库根 props + 本说明` | `Directory.Build.props`、本文件 |

补丁的人类可读清单**只有一份**：`docs/internal/mewui.md`。这里只记「补丁在哪、怎么维护」，
不重复写改了什么。

## Directory.Build.props

只做一件事：屏蔽 Vivy 仓库根的 `Directory.Build.props`。根 props 会把 `TargetFramework`
默认成 `net11.0-windows`，而 MewUI 各工程用 `MewUI.Common.props` 声明
`TargetFrameworks=net8.0;net10.0`（复数），两者打架会让还原直接失败。
MSBuild 只取最近的一份 `Directory.Build.props`，本文件存在即根部不再生效——
所以它刻意不设 `TargetFramework` / `TargetFrameworks`，交给各工程自己声明。
