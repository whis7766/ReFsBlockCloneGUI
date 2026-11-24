using System.Reflection;
using System.Runtime.InteropServices;
#if DEBUG
using System.Runtime.CompilerServices;
#endif

[assembly: AssemblyTitle("ReFS Block Clone GUI")]
[assembly: AssemblyDescription("ReFS 块克隆工具：元数据级复制、共享存储空间、SSD 友好")]
[assembly: AssemblyProduct("ReFsBlockCloneGUI")]
[assembly: AssemblyVersion("2.1.1.0")]
[assembly: AssemblyFileVersion("2.1.1.0")]
[assembly: ComVisible(false)]
#if DEBUG
[assembly: InternalsVisibleTo("GuiSmokeTest")]
#endif