# 设计核定与修复记录（DESIGN）

本文记录接管本项目后对原有理论的逐条核定、修复决策与实测证据。
核定基准为微软一手文献；所有实测均在 GitHub Actions 的 Windows runner
（Windows Server 2025，`windows-latest`）上、于动态 VHDX 制备的**真实 ReFS 卷**
（R: 4KB 簇、S: 64KB 簇）完成，见 `.github/workflows/ci.yml` 与每次运行的
`engine-test.log` / `gui-test.log` / `gui-shots/*.png` 产物。

## 0. 背景

项目初版由大模型生成：README 与参考脚本含有多处与微软文档不一致的表述，
且旧 CI **只编译、从不运行测试**，关键"修复"从未在真实 ReFS 上验证。
本次接管以文献核定 + Actions 实测闭环重做引擎、GUI、测试与 CI。

## 1. 文献

| 文献 | 用途 |
|---|---|
| [Block Cloning (Win32)](https://learn.microsoft.com/en-us/windows/win32/fileio/block-cloning) | 约束总表、簇大小与 COW 粒度、24H2 原生复制 |
| [Block cloning on ReFS](https://learn.microsoft.com/en-us/windows-server/storage/refs/block-cloning) | 8175 引用上限、SetEndOfFile、integrity/稀疏规则、oplock |
| [FSCTL_DUPLICATE_EXTENTS_TO_FILE](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_duplicate_extents_to_file) | IOCTL 用法与支持矩阵 |
| [[MS-FSA] 2.1.5.10.4](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-fsa/4623bf0a-ab5f-4ab6-9c03-b8372c7aa06b) | 对象存储伪代码：对齐、Source.Size 校验、稀疏规则、extent/refcount 算法 |
| [[MS-FSCC] 2.3.7 / 2.3.9](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-fscc/78e67b4b-84a4-4730-a34f-dcdcb2224e49) | 报文语义与错误表（"source range beyond allocation size"） |
| [ReFS integrity streams](https://learn.microsoft.com/en-us/windows-server/storage/refs/integrity-streams) | 完整性可按文件/目录/卷随时开关、目录继承 |
| [FILE_DISPOSITION_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_disposition_info) | delete-pending 语义 |
| [NTSTATUS→Win32 映射（OSR/WDK）](https://www.osr.com/wp-content/uploads/NTtoDos.pdf) | STATUS_BLOCK_TOO_MANY_REFERENCES → 347 |

## 2. 逐条核定

### 2.1 "失败零残留：目标创建后最先设置删除待决标记，任何失败自动清理半成品"

**判定：机制真实有效，但表述过度，且参考脚本对其作用的解释是错的。**

- 参考脚本注释称 delete-pending "lets ReFS clone into a freshly-sized output"
  （让 ReFS 能克隆进刚设置大小的目标）——**错误**。删除待决与克隆能力无关，
  纯粹是清理手段；去掉它克隆照样成功（实测矩阵全部用例不依赖它）。
- 它**不是**微软在文件系统层对"失败克隆"的自动回滚：单次 FSCTL 是原子的
  （元数据操作要么整体生效要么不生效），但**多分块序列整体不是事务**——
  第 5 块失败时前 4 块已生效，文件系统不会回滚。因此应用层清理仍然必要，
  "微软已经妥善实现所以这是废物"的猜测不成立。
- delete-pending 的真实语义：最后一个句柄关闭时删除文件。覆盖异常、取消、
  进程被杀等**进程级**失败；**不覆盖 OS 崩溃/断电**（该状态不跨崩溃持久化）。
  "任何失败"是过度承诺。
- 原实现还有两个小窗口/疏漏：`CREATE_NEW` 与设置标记之间先做了被忽略返回值的
  `FSCTL_SET_SPARSE`；若标记设置本身失败则无兜底。

**处理**：保留该机制（它是 `FILE_FLAG_DELETE_ON_CLOSE` 的"可清除"变体，
成功后需保留文件，故不能用不可清除的 DELETE_ON_CLOSE）；标记紧跟创建；
`SET_SPARSE` 检查返回值；标记未armed 的窗口通过 `catch` 中 `DeleteFileW` 兜底；
README 改写为准确语义。

### 2.2 非对齐文件"向上取整克隆后收缩回源精确大小"

**判定：在当前 ReFS 上碰巧可行，但依据不足、语义偏险；改为更稳的等价设计。**

- [MS-FSA] 伪代码：`Source.Size < SourceFileOffset + ByteCount ⇒ STATUS_NOT_SUPPORTED`
  （按**逻辑 EOF** 校验）；而 [MS-FSCC] 错误表写的是 "beyond the source file's
  **allocation size**"。两者口径不一致。
- **实测（CI 诊断用例 [12]）**：在 Server 2025 / 4KB 簇 ReFS 上，对非对齐源直接
  发 round-up 的原始 FSCTL **成功（Win32 0）**——本地实现走 allocation-size 口径。
  即旧策略在当前 ReFS 上并未坏，但它是依赖未明文化容忍度的做法。
- 新设计：**簇对齐前缀**走 FSCTL，**尾部不足一簇**走普通读写；目标 EOF 直接设为
  源精确大小（前缀与尾部都在 EOF 内），**无需收缩步骤**。该设计在两种口径下
  都成立，对稀疏空洞尾部也自然正确（空洞不写、保持稀疏）。
- 实测：[1][3][6][7][8] 全部 PASS（稠密/稀疏/尾洞/亚簇/64K 簇，SHA256 一致）。

### 2.3 "稀疏文件克隆后保持稀疏"（原实现无条件 `FSCTL_SET_SPARSE`）

**判定：半对半错。** 微软只要求"源稀疏 ⇒ 目标稀疏"；原实现把**所有**目标
（含稠密源的克隆）标记稀疏，改变文件属性，破坏属性保真。
**处理**：镜像源稀疏属性。实测 [1] "dense source => dst NOT sparse" PASS，
[3][6][8] 稀疏保持 PASS。

### 2.4 完整性 streams 复制

**判定：方向正确，补强两点。** 目标新文件会**继承父目录**的完整性设置，可能与源
不一致而导致 FSCTL 拒绝；必须在克隆前显式镜像源的算法与 flags，且宜在目标仍为空
时设置。实测三方向（源显式开、源目录继承开→目标普通目录、源关→目标目录开）
均 PASS，目标设置与源一致。

### 2.5 "仅 64 位（FileHandle 为固定 8 字节 HANDLE）"

**判定：理由表述错误。** HANDLE 是**指针宽度**而非"固定 8 字节"；x64-only 的
真正原因是 ReFS 块克隆只存在于 x64 Windows。结构体改用 `IntPtr`  marshaling。

### 2.6 "近零 NAND 写入 / 纯元数据操作"

**判定：基本正确但需限定。** 不复制数据字节；引用计数、extent 列表等**元数据
写入仍然存在**（integrity 开启时还有校验和元数据）。README 已限定表述。

### 2.7 缺失的硬约束文档

补齐：单次 <4GB（当前 1GB 分块，早期为 100MB）、8175 引用上限（347 友好提示）、簇对齐、
目标先 SetEndOfFile、同文件不重叠、oplock 打断、24H2/Server 2025 原生复制背景。

### 2.8 "实测 8.2GB 约 0.6 秒"

**判定：未经本项目验证的宣传数字。** CI 实测（Server 2025 runner、VHDX 虚拟磁盘、
83 次 100MB FSCTL）：**8.231GB+12345 用时 4.936s**，内容抽样一致。性能与硬件/
虚拟化层强相关，README 不再写死数字，指向 CI 日志。

## 3. 实测事实摘录

**CI run 1（commit ddd0a14）**

- 引擎矩阵 40 PASS / 1 FAIL；唯一 FAIL 为测试断言写反（继承完整性方向），
  引擎行为正确；run 2 修正断言并补反向用例。
- round-up 原始 FSCTL：Win32 0（成功）→ 记录 MS-FSA 与本地实现口径差异。
- 引用上限：连续 **8175** 次克隆成功（1.8s），第 8176 次返回 **347**，无残留。
- 跨卷拒绝：ReFS→NTFS 与 ReFS→ReFS 均干净失败、无孤儿文件。
- 取消：`OperationCanceledException` + 半成品自动删除。
- 64KB 簇卷：稠密/稀疏非对齐均字节级一致。
- 8.231GB+12345 全尺度克隆 **4.936s**（VHDX 虚拟磁盘），内容抽样一致。

**CI run 4（commit 129f937，全绿）**

- 引擎矩阵 **45 PASS / 0 FAIL**（含三方向完整性、尾有数据稀疏、引用上限 2.4s）。
- headless CLI：4MB+123 成功 **0.096s**、哈希一致；跨卷退出码 1、无孤儿。
- GUI 冒烟 **23 PASS / 0 FAIL**，8 张状态截图（初始/填充/进度/成功/取消/
  错误/目标已存在/最小尺寸）。
- 迭代中修掉的两个 CI/测试坑：PowerShell `&` 不等待 GUI 子系统进程
  （`$LASTEXITCODE` 为空）→ 改 `Start-Process -Wait -PassThru`；
  `Form.DrawToBitmap` 输出含非客户区标题栏，bitmap 必须按**窗口尺寸**创建，
  否则底部状态栏被裁掉（run 3 截图"状态栏空白"的真因）。

## 4. 设计决策

### 4.1 引擎流程（src/ReFsBlockCloner.cs）

打开源 → 卷能力/序列号 → 大小/稀疏/完整性 → `CREATE_NEW` 目标 → **立即**
delete-pending → 目标卷校验 → 镜像稀疏 → 镜像完整性 →
`SetEndOfFile(源大小)` → 对齐前缀分块 FSCTL（**默认 1GB**，可取消、报进度）→
尾部普通读写（稀疏空洞跳过）→ 清除 delete-pending → 备用数据流（ADS）普通 I/O 复制 →
镜像时间戳/可设置属性/owner/group/DACL。
失败路径：句柄关闭触发删除；未armed 窗口或 ADS 阶段失败时 `DeleteFileW` 兜底。
错误统一附 Win32 码 + 系统文本 + ReFS 中文提示（5/19/32/33/50/87/112/347）。
路径规范化支持引号、正斜杠与 `\\?\` 长路径。

### 4.2 GUI（src/MainForm.cs）

分组式布局：源/目标（浏览+拖放+目标自动建议）、卷信息面板（文件系统、簇大小、
完整性、稀疏、同卷校验，300ms 防抖异步探测）、操作行（开始/取消/百分比）、
等宽日志、Panel+Label 状态栏（可被 DrawToBitmap 完整渲染）。
DPI PerMonitorV2 + longPathAware manifest。
**可测试性**：`UiHooks.ShowMessage` 对话框钩子；`MainForm.ProgressOverride`
（worker 线程同步回调，可确定性暂停克隆以截取进度态）；`InternalsVisibleTo`。

### 4.3 测试与 CI

- `ReFsCloneTest`：矩阵（见 README），含 round-up 诊断、引用上限、稀疏符号链接、目标已存在、只读属性与 ADS 实测。
- `GuiSmokeTest`：驱动真实 MainForm 走真实克隆管线，`DrawToBitmap` 渲染
  初始/填充/进度/成功/取消/错误/目标已存在/最小尺寸 8 态 PNG，另经 PrintWindow
  抓取原生打开/另存为对话框 2 张（共 10 张，session 0 可用，不依赖交互桌面），
  并断言文件存在性、SHA256、对话框记录、状态文本。
- `ci.yml`：push/PR/dispatch → MSBuild → 双 ReFS VHD（4K/64K）→ 引擎矩阵
  （C 盘剩余 >12GB 时 full，含 8.2GB 与引用上限）→ headless CLI → GUI 截图 →
  上传产物。`build-release.yml` 移除"删除全部运行记录"步骤，发布前跑 quick 验证。

## 5. 已知限制

- 块克隆不是快照：克隆期间源被并发写入不保证一致。
- delete-pending 不跨 OS 崩溃持久化；崩溃后可能留半成品（NTFS/ReFS 皆然）。
- ADS 只复制数据，不单独镜像命名流的时间戳/ACL（安全描述符随主文件统一设置）；
  且 ADS 复制在清除 delete-pending 后进行，进程恰在此窗口崩溃可能留下主数据完整、
  命名流不完整的目标文件。
- 同卷判定用卷序列号代理（理论上可碰撞）；FSCTL 自身亦会拒绝跨卷。
- GUI 截图为整窗渲染（含标题栏与状态栏），用于布局与状态验证；
  原生打开/另存为对话框另经 PrintWindow 抓取并纳入产物。

## 6. 候选后续（状态见 §7）

- `FSCTL_DUPLICATE_EXTENTS_TO_FILE_EX` + `SOURCE_ATOMIC`：**评估后不采用**（§7.4）。
- 稀疏源按 `FSCTL_QUERY_ALLOCATED_RANGES` 只克隆已分配区间：**实测后否决**（§7.2，QAR 首簇报告不可靠）。
- CLI 进度输出与 `--verify` 哈希自校验开关：未实施。

## 7. 第二轮审计：稀疏 / 速度 / 写入量 / 使用适配（CI run 15，commit 5a9d32f，全绿）

引擎 **69 PASS / 0 FAIL**、GUI **27 PASS / 0 FAIL**（8 态截图）。

### 7.1 稀疏文件兼容性（问题①）

- 块克隆路径：源稀疏 ⇒ 目标 `FSCTL_SET_SPARSE` 镜像；对齐前缀经 refcount 共享，
  空洞随映射天然保留；尾部不足一簇若为空洞则不写。4K 与 64K 簇卷空洞图均一致。
- 曾实现的物理复制路径（4K 零 run 打洞）已按用户要求整体移除；QAR 与
  `FSCTL_SET_ZERO_DATA` 的实测结论保留在审计记录中，不再参与产品路径。
- **实测新事实**：现代 ReFS（Server 2025）**支持** `FSCTL_QUERY_ALLOCATED_RANGES`
  （Win32 旧支持矩阵列 ReFS=No 已过时），也支持 `FSCTL_SET_ZERO_DATA`。
  但 ReFS 的 QAR 对稀疏文件**首簇报告有偏差**（把已分配的首簇报成空洞），
  故**不能**用 QAR 驱动复制（会漏数据）；本工具仅用它做空洞图观测，
  复制一律线性扫描保证内容正确。跨文件系统空洞校验以字节扫描为 ground truth。

### 7.2 速度（问题③）与基准

CI 基准（2GB 随机源，R: 4K ReFS VHDX，宿主写入以动态 VHDX 新增长度代理）：

| 分块 | 耗时 | VHDX 新增块 |
|---|---|---|
| 100MB | 17ms | 0 |
| 1024MB | 16ms | 0 |
| 3072MB | 16ms | 0 |
| 1024MB（源 integrity-on，1GB） | 20ms | 0 |

- 块克隆只改 refcount/元数据、不搬数据字节，故 2GB 克隆 ~16ms、宿主**无新增数据块**。
- 温缓存 VHDX 下分块大小对耗时影响可忽略；**默认 1GB** 的价值在于减少 FSCTL 调用次数
  （2GB 仅 2 次 vs 100MB 的 20 次），降低冷缓存/真实盘上的逐次校验与日志提交开销，
  且远小于 4GB 单次上限。分块须为簇整数倍、<4GB，引擎显式校验。
- 度量口径说明：VHDX 增长是**块粒度**（测新增块分配，非总字节）；要精确总写入需
  ETW/SMART。数据字节零搬运是块克隆的结构性优势，与分块无关。
- 取消修正：1GB 分块下小文件只跑一次循环，进度回调触发的取消落在循环后；
  已在循环后/尾部前补 `ThrowIfCancellationRequested`，保证取消必清理半成品。

### 7.3 磁盘写入量（问题④）

已落地的降写入手段：
1. 块克隆：数据字节 0 写入（仅元数据/refcount）。
2. 稀疏镜像：空洞随共享簇保留，不产生数据写入。
3. 完整性镜像：目标继承源 integrity 设置；integrity-on 源克隆**不重算校验和**
   （校验和随共享簇走，基准 VHDX 新增 0）。
4. 尾部仅复制不足一簇的零头；元数据（时间戳/属性/ACL）仅描述符写入。
块模式写入量 ≈ 元数据；远低于整文件重写。

### 7.4 EX + SOURCE_ATOMIC 不采用理由

`FSCTL_DUPLICATE_EXTENTS_TO_FILE_EX`（0x00098348）+ `SOURCE_ATOMIC`(0x1) 提供
"单次复制从源视角原子"。不采用：① 其保证与既有 delete-pending 清理 + 失败回滚重叠；
② 源侧序列化带来额外开销；③ [MS-FSCC] 2.3.9.1 wire 布局为 StructureSize 在前共 44B，
与本地 winioctl 结构 padding 存在差异、未获实测确认，引入不确定风险。收益/风险不划算。

### 7.5 适用性与限制（问题⑤）

| 场景 | 结论 |
|---|---|
| 只读/少改、同基多克隆（模板、备份） | 适用：瞬时、共享簇、写入极低 |
| 克隆后大量修改 | 不适用工作副本：修改触发 allocate-on-write 放大与 refcount 递减，建议直接复制使用 |
| 跨卷 / 非 ReFS 目标 | 不支持：块克隆仅限同 ReFS 卷 |
| 大稀疏文件 | 支持：空洞随 refcount 共享，属性镜像 |
| 需 integrity streams | 支持：校验和随共享簇复用，不重算 |
| 同区域 >8175 份克隆 | 受限：第 8176 次返回 347，应换基线或减少同基克隆 |

GUI 仅保留块克隆路径；元数据保真：时间戳/属性经 `FileBasicInfo` 镜像；owner/group/DACL 经
`GetSecurityInfo`/`SetSecurityInfo`，清除 INHERITED_ACE 并以 PROTECTED_DACL 落地；
测试按"每 trustee 权利并集"比较有效 ACL。均为 best-effort，失败仅警告不致命。

### 7.6 本轮修掉的实现缺陷

- 目标句柄缺 `WRITE_DAC|WRITE_OWNER` → 镜像 ACL 返回 Win32 5；已补。
- 继承 ACE 直落导致跨目录 ACL 漂移 → 清 INHERITED + PROTECTED_DACL。
- 物理复制模式（含 4K 零 run 打洞、QAR 驱动复制）按要求整体移除。
- 1GB 分块下取消检查缺失 → 循环后补检查。

## 8. 第三轮一致性审计（本次）

对引擎、GUI、CLI、测试、CI 与文档做全量对照，修复以下核心/一致性问题：

- **稀疏/完整性文件的元数据镜像会整段失败**：`FILE_BASIC_INFO.FileAttributes` 若含
  `FILE_ATTRIBUTE_SPARSE_FILE`（0x200）或 `FILE_ATTRIBUTE_INTEGRITY_STREAM`（0x8000），
  `SetFileInformationByHandle(FileBasicInfo)` 不接受这些由 FSCTL 管理的属性，导致同一次调用里的
  时间戳/可设置属性也一起未落地。现按可设置位掩码（READONLY/HIDDEN/SYSTEM/ARCHIVE/NORMAL/
  TEMPORARY/OFFLINE/NOT_CONTENT_INDEXED）镜像，稀疏与完整性仍由 FSCTL 负责。
- **ADS 被静默丢弃**：`FSCTL_DUPLICATE_EXTENTS_TO_FILE` 只作用于默认数据流。现用
  `FindFirstStreamW/FindNextStreamW` 枚举、在主数据克隆成功并清除 delete-pending 后，以普通 I/O
  复制每个命名流（ReFS 单流 ≤128KB）；失败仍删除整个目标，不留半成品。
- **稀疏判断看路径不看句柄**：符号链接指向稀疏文件时，`GetFileAttributesW(link)` 不带
  `SPARSE_FILE`，目标未标稀疏会令 FSCTL 拒绝。现从已打开句柄的 `FileBasicInfo` 取属性；
  `TryProbe` 同样以句柄属性判定稀疏/目录，并新增 `PathExists/IsDirectory`。
- **目标已存在/目录时 UI 仍显示“同卷”**：卷探测新增路径存在性与目录标记，GUI 分别显示
  “文件已存在”“路径是目录”，并在校验失败时给出状态栏原因；源不存在显示“不存在”，
  源为空显示“待选择”，初始不再显示 0%。
- **空路径/目录源/目录目标错误信息不明确**：引擎入口显式拒绝并给出中文原因；尾部读取遇
  EOF 报“源文件被截断”而非 Win32 0。
- **CI 基准失败不使作业失败**：bench 两次调用均检查 `$LASTEXITCODE`；GUI 对话框抓取改为断言。
- **文档口径**：README 的“100MB 分块”改为 1GB（当前默认）；DESIGN 移除已删除的进度条描述，
  并更新截图/对话框说明。
