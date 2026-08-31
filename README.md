# ReFS 块克隆工具（ReFsBlockCloneGUI）

Windows 上对 ReFS 卷内文件做**块级克隆**（Block Clone）的轻量工具，提供 **GUI** 与**无头 CLI** 两种使用方式。核心思想源自 Sergey Gruzdov（egel@egel.su）的 PowerShell 脚本，本项目以原生 .NET Framework（x64）重写，并修正了非对齐文件大小、同卷校验、失败零残留等问题。

## 原理

ReFS 的块克隆通过 `FSCTL_DUPLICATE_EXTENTS_TO_FILE` 实现：只复制文件的**物理块引用**（refcount），不读写任何数据字节，因此是纯元数据操作：

- 近零 NAND 写入，对 SSD 友好
- 近零额外空间占用（克隆与源共享同一批物理簇）
- 秒级完成（实测 8.2GB 文件约 0.6 秒）

克隆与源共享物理簇；此后任一方的修改走写时复制（COW），互不影响；删除源后克隆依然完整（引用计数语义）。

## 功能

- GUI（WinForms）与无头 CLI 两种模式
- 非簇对齐文件（稠密或稀疏）也能得到字节级一致的克隆：向上取整克隆后收缩回源精确大小
- 稀疏文件克隆后保持稀疏
- 强制源与目标位于**同一 ReFS 卷**，跨卷明确报错
- 失败零残留：目标创建后最先设置删除待决标记，任何失败自动清理半成品
- 不覆盖已有文件；GUI 单实例
- 仅 64 位（`DUPLICATE_EXTENTS_DATA.FileHandle` 为固定 8 字节 HANDLE）

## 实现

```
src/
  ReFsBlockCloner.cs   核心引擎：封装全部 Win32 调用（唯一逻辑来源，GUI/CLI/测试共享）
  MainForm.cs          WinForms 界面
  Program.cs           入口：单实例互斥体 + GUI / 无头 CLI 分支
test/
  ReFsCloneTest.cs     引擎验证程序（稠密/稀疏/零字节/跨卷用例）
reference/
  refsblockclone.fixed.ps1  原始 PowerShell 参考实现
```

引擎主流程：

1. 打开源文件（GENERIC_READ），校验所在卷支持块引用（`FILE_SUPPORTS_BLOCK_REFCOUNTING`）
2. 以 `CREATE_NEW` 创建目标，标记 sparse，先设置删除待决标记
3. 校验目标卷支持块克隆且卷序列号与源一致
4. 读取源完整性信息得到簇大小，把克隆区间向上取整到簇边界
5. 复制源完整性设置，按 100MB 分块执行 `FSCTL_DUPLICATE_EXTENTS_TO_FILE`
6. 把目标 EOF 收缩回源精确大小，清除删除待决标记

## 构建与发布

目标 .NET Framework 4.8（Windows 自带），无需安装 .NET SDK。本地构建：

```powershell
msbuild ReFsBlockCloneGUI.sln /p:Configuration=Release /p:Platform=x64 /p:PreferredToolArchitecture=x64 /m
```

产物：`x64\Release\ReFsBlockCloneGUI.exe`（及 `x64\Release\ReFsCloneTest.exe`）。

GitHub Actions 已配置在 `.github/workflows/build-release.yml`：手动触发（`workflow_dispatch`）并填写 Release tag 后，自动用 MSBuild 编译 Release x64 并发布到 GitHub Release。

## 使用

**GUI**：双击 `ReFsBlockCloneGUI.exe`，选择源文件与目标路径后点击“开始克隆”。

**CLI**：

```bat
ReFsBlockCloneGUI.exe <源文件> <目标文件>
```

无界面，运行后在当前目录写 `refsclone_headless.log`（UTF-8 带 BOM）；退出码 `0`=成功，`1`=失败。

## 注意事项

- 仅支持 64 位 Windows 10/11；目标需为支持块引用的 ReFS 版本
- 源与目标必须在同一 ReFS 卷（可不同目录）
- 目标已存在会报错，不覆盖
