# ReFS 块克隆工具（ReFsBlockCloneGUI）

Windows 上对 **同一 ReFS 卷内** 的文件做块级克隆（Block Clone）的轻量工具，提供 **GUI** 与**无头 CLI** 两种用法。核心为原生 .NET Framework 4.8（x64）实现，理论依据以微软官方文献为准（见文末与 [docs/DESIGN.md](docs/DESIGN.md)）。

## 原理

`FSCTL_DUPLICATE_EXTENTS_TO_FILE` 让 ReFS 以**元数据操作**完成"复制"：只改写目标文件的 VCN→LCN 映射并增加物理簇引用计数，不读写数据字节；此后任何一方写入共享簇时，ReFS 通过 **allocate-on-write（写时复制）** 分配新簇，保证文件间隔离。因此：

- 不复制数据字节（仍有少量元数据写入），对 SSD 写入放大友好；
- 克隆与源共享物理簇，容量占用近零；
- 删除源文件后克隆依然完整（引用计数语义）；
- 大文件通常秒级完成（CI 在真实 ReFS 卷上有实测日志）。

注意：块克隆**不是快照**。克隆期间若源文件被并发写入，结果不保证一致；请尽量对静止文件操作。

## 微软文档核定的硬约束

| 约束 | 说明 |
|---|---|
| 同一 ReFS 卷 | 源与目标必须在同一卷；卷须以 Windows Server 2016+ 格式化 |
| 簇对齐 | 源/目标偏移与长度必须是簇大小（4KB 或 64KB）的整数倍 |
| 单次 < 4GB | 每次 FSCTL 的克隆区间长度必须小于 4GB（本工具按 100MB 分块） |
| 目标不越界 | 目标区间不得超出目标 EOF；扩展目标须先 `SetEndOfFile` |
| 完整性一致 | 源与目标的 Integrity Streams 设置必须相同（目标会继承父目录设置，需显式镜像） |
| 稀疏规则 | 源稀疏 ⇒ 目标必须稀疏；本工具同时保证源稠密 ⇒ 目标稠密（属性保真） |
| 引用上限 | 同一物理区域最多 8175 个文件区域引用；超限返回 `ERROR_BLOCK_TOO_MANY_REFERENCES`（347） |
| 同文件不重叠 | 同文件内源/目标区间不得重叠 |
| 机会锁 | 块克隆会打断共享机会锁（Level 2 Oplock） |

另：自 Windows 11 24H2 / Windows Server 2025 起，系统受支持的复制操作已**原生**使用块克隆；本工具的价值在于显式、可控、可验证的克隆（含 CLI 与测试）。

## 实现要点

```
src/
  ReFsBlockCloner.cs   核心引擎：全部 Win32 调用与唯一克隆逻辑（GUI/CLI/测试共享）
  MainForm.cs          WinForms 界面（卷信息、进度、取消、拖放、日志）
  Program.cs           入口：单实例互斥体 + GUI / 无头 CLI 分支
test/
  ReFsCloneTest.cs     引擎验证矩阵（真实 ReFS 卷，4K 与 64K 簇）
  GuiSmokeTest.cs      GUI 无头冒烟测试：真实克隆各状态渲染为 PNG 截图
scripts/
  New-ReFsTestVolumes.ps1  CI 用：VHDX 上制备 4K/64K 簇 ReFS 测试卷
reference/
  refsblockclone.fixed.ps1  历史参考脚本（保留溯源；其中个别注释有误，见 DESIGN.md）
```

引擎主流程：

1. 打开源（`GENERIC_READ`），校验卷具备 `FILE_SUPPORTS_BLOCK_REFCOUNTING`；
2. 读取源大小、稀疏属性、完整性信息（含簇大小）；
3. `CREATE_NEW` 创建目标（绝不覆盖），**立即**设置删除待决标记作为失败清理手段；
4. 校验目标卷能力与卷序列号同源；镜像源的稀疏属性与完整性设置；
5. `SetEndOfFile` 到源精确大小；对**簇对齐前缀**按 **1GB** 分块执行块克隆（分块须为簇整数倍且 <4GB）；
6. 尾部不足一簇的部分用普通读写复制（稀疏源的空洞尾部保持空洞）；
7. 镜像源的时间戳/属性与 owner/group/DACL（best-effort，失败仅警告）；
8. 成功后清除删除待决标记；任何失败路径关闭句柄即自动删除半成品。

关于"删除待决"（delete-pending）的准确语义：它是**进程级**清理辅助——句柄关闭时文件系统删除文件，可覆盖异常、取消、进程被杀等路径；它**不是**块克隆的前提条件，也**不保证**操作系统崩溃/断电后的清理（该标记不跨崩溃持久化）。单次 FSCTL 本身是原子的，但多分块序列整体不是事务，故应用层清理仍然必要。创建与标记之间的极小窗口另有 `DeleteFileW` 兜底。

## 构建与 CI

目标 .NET Framework 4.8（Windows 自带），无需安装 .NET SDK：

```powershell
msbuild ReFsBlockCloneGUI.sln /p:Configuration=Release /p:Platform=x64 /p:PreferredToolArchitecture=x64 /m
```

产物：`src\x64\Release\ReFsBlockCloneGUI.exe`、`test\x64\Release\ReFsCloneTest.exe`、`test\x64\Release\GuiSmokeTest.exe`。

**所有编译、运行、调试、测试都在 GitHub Actions 的 Windows runner 上完成**（`.github/workflows/ci.yml`，push / PR / 手动触发）：

1. MSBuild 编译 Release x64；
2. 在动态 VHDX 上制备两个真实 ReFS 卷（R: 4KB 簇、S: 64KB 簇）；
3. 引擎矩阵：非对齐稠密/稀疏、稀疏尾洞、零字节、亚簇小文件、跨卷（ReFS→NTFS、ReFS→ReFS）拒绝、完整性开启/目录继承、取消清理、8175 引用上限、8.2GB 全尺度（磁盘空间足够时）、元数据/ACL 保真；
4. 分块/写入量基准：100/1024/3072MB 分块克隆 2GB 源，以动态 VHDX 新增长度代理宿主写入；另测 integrity-on 源；
5. 无头 CLI 成功/失败路径与哈希校验；
6. GUI 冒烟测试：真实克隆的初始/填充/进度/成功/取消/错误/目标已存在/最小尺寸状态截图（PNG 产物）；
7. 上传日志、截图、基准与二进制产物。

发布：手动触发 `Build and Release` 并填写 tag（发布前跑 quick 引擎验证）。

## 使用

**GUI**：双击 `ReFsBlockCloneGUI.exe`，选择源文件（支持拖放），目标路径自动建议（可改），点击"开始克隆"；可中途取消；卷信息面板实时显示文件系统、簇大小、完整性与同卷校验。

**CLI**：

```bat
ReFsBlockCloneGUI.exe <源文件> <目标文件>
```

无界面；在当前目录追加写 `refsclone_headless.log`（UTF-8 带 BOM）；退出码 `0`=成功，`1`=失败。

## 注意事项

- 仅 64 位 Windows（ReFS 块克隆仅存在于 x64 Windows）；
- 块克隆要求源与目标同一 ReFS 卷（可不同目录）；目标已存在会报错，不覆盖；
- 稀疏源保真空洞；元数据（时间戳/属性/ACL）best-effort 镜像；
- 块克隆同区域引用上限 8175（第 8176 次返回 347）；目标应避免作为后续重修改的工作副本；
- 错误信息附 Win32 错误码与针对 ReFS 的中文提示（如 347 引用上限、50 约束不满足等）。

## 文献

- [Block Cloning（Win32）](https://learn.microsoft.com/en-us/windows/win32/fileio/block-cloning)
- [Block cloning on ReFS](https://learn.microsoft.com/en-us/windows-server/storage/refs/block-cloning)
- [FSCTL_DUPLICATE_EXTENTS_TO_FILE](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_duplicate_extents_to_file)
- [[MS-FSA] 2.1.5.10.4 FSCTL_DUPLICATE_EXTENTS_TO_FILE](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-fsa/4623bf0a-ab5f-4ab6-9c03-b8372c7aa06b)
- [[MS-FSCC] 2.3.7 / 2.3.9](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-fscc/78e67b4b-84a4-4730-a34f-dcdcb2224e49)
- [ReFS integrity streams](https://learn.microsoft.com/en-us/windows-server/storage/refs/integrity-streams)
- [FILE_DISPOSITION_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_disposition_info)
- [FSCTL_QUERY_ALLOCATED_RANGES](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_query_allocated_ranges)
- [FSCTL_SET_ZERO_DATA](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_set_zero_data)
- [FILE_BASIC_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_basic_info)
- [GetSecurityInfo / SetSecurityInfo](https://learn.microsoft.com/en-us/windows/win32/api/aclapi/nf-aclapi-getsecurityinfo)