using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using ClickClean.Core;

namespace ClickClean.Security;

// 使用Windows SDK taskschd.h的固定IUnknown槽位，避免脚本/动态COM的VARIANT参数封送。
// 仅编译为x64；不接收路径、账户、任务XML或命令行参数。
public static unsafe class AuthorizationTaskInstaller
{
    private static readonly Guid serviceClass = new("0F87369F-A4E5-4CFC-BD3E-73E6154572DD");
    private static readonly Guid serviceInterface = new("2FABA4C7-4DA9-4013-9697-20CC3FD40F85");
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct NativeVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Value;
    }
    private static string Xml(string sid, string helper, string directory) =>
        "<?xml version=\"1.0\" encoding=\"UTF-16\"?><Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
        "<RegistrationInfo><Description>Click-Clean：安装时授权的固定整理组件；当前账户仅可读取和运行。</Description></RegistrationInfo><Triggers/>" +
        "<Principals><Principal id=\"Author\"><UserId>" + SecurityElement.Escape(sid) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>" +
        "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>" +
        "<AllowHardTerminate>false</AllowHardTerminate><AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled><Hidden>false</Hidden><ExecutionTimeLimit>PT0S</ExecutionTimeLimit></Settings>" +
        "<Actions Context=\"Author\"><Exec><Command>" + SecurityElement.Escape(helper) + "</Command><Arguments>" + SecurityElement.Escape(AuthorizedTaskPolicy.Arguments) + "</Arguments><WorkingDirectory>" +
        SecurityElement.Escape(directory) + "</WorkingDirectory></Exec></Actions></Task>";
    public static int SelfTest() => IntPtr.Size == 8 && Marshal.SizeOf<NativeVariant>() == 24 &&
        Xml("S-1-5-21-1-2-3-1001", @"C:\A&B\helper.exe", @"C:\A&B").Contains("C:\\A&amp;B") ? 0 : 1;
    public static void Configure(bool install)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (SelfTest() != 0 || !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) || !ProtectedInstallation.IsProtected(out _))
            throw new UnauthorizedAccessException("授权任务配置需要已提权的受保护系统组件。");
        var sid = identity.User!.Value;
        var initialized = CoInitializeEx(IntPtr.Zero, 0);
        if (initialized < 0 && initialized != unchecked((int)0x80010106)) Marshal.ThrowExceptionForHR(initialized);
        IntPtr service = IntPtr.Zero, folder = IntPtr.Zero, registered = IntPtr.Zero;
        IntPtr root = IntPtr.Zero, name = IntPtr.Zero, xml = IntPtr.Zero, user = IntPtr.Zero, security = IntPtr.Zero;
        try {
            var classId = serviceClass; var interfaceId = serviceInterface;
            var hr = CoCreateInstance(ref classId, IntPtr.Zero, 1, ref interfaceId, out service);
            Marshal.ThrowExceptionForHR(hr);
            // ITaskService: IUnknown(3) + IDispatch(4), GetFolder=7, Connect=10。
            var connect = (delegate* unmanaged[Stdcall]<IntPtr, NativeVariant, NativeVariant, NativeVariant, NativeVariant, int>)(*(void***)service)[10];
            Marshal.ThrowExceptionForHR(connect(service, default, default, default, default));
            root = Marshal.StringToBSTR("\\");
            var getFolder = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)(*(void***)service)[7];
            Marshal.ThrowExceptionForHR(getFolder(service, root, &folder));
            name = Marshal.StringToBSTR(AuthorizedTaskPolicy.NamePrefix + sid);
            if (!install) {
                var remove = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int>)(*(void***)folder)[15];
                hr = remove(folder, name, 0);
                if (hr != unchecked((int)0x80070002)) Marshal.ThrowExceptionForHR(hr);
                return;
            }
            xml = Marshal.StringToBSTR(Xml(sid, ProtectedInstallation.HelperPath, ProtectedInstallation.DirectoryPath));
            user = Marshal.StringToBSTR(sid);
            security = Marshal.StringToBSTR("O:BAG:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGX;;;" + sid + ")");
            // ITaskFolder.RegisterTask=16；22=创建/更新且不自动添加Principal完全控制ACE。
            var register = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int, NativeVariant, NativeVariant, int, NativeVariant, IntPtr*, int>)(*(void***)folder)[16];
            hr = register(folder, name, xml, 22, new() { Type = 8, Value = user }, default, 3, new() { Type = 8, Value = security }, &registered);
            Marshal.ThrowExceptionForHR(hr);
        }
        finally {
            Release(registered); Release(folder); Release(service);
            foreach (var value in new[] { root, name, xml, user, security }) if (value != IntPtr.Zero) Marshal.FreeBSTR(value);
            if (initialized >= 0) CoUninitialize();
        }
    }
    private static void Release(IntPtr value) { if (value != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(void***)value)[2])(value); }
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint concurrency);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid classId, IntPtr outer, uint context, ref Guid interfaceId, out IntPtr instance);
}
