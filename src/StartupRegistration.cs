using Microsoft.Win32;

namespace ThreeFingerDragLite;

/// <summary>当前用户的登录启动项；以注册表为准，不在配置文件中重复保存。</summary>
public sealed class StartupRegistration(string registryPath = @"Software\Microsoft\Windows\CurrentVersion\Run") {
    private const string ValueName = "ThreeFingerDragLite";

    private static string Command {
        get {
            string executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法获取程序路径");
            string command = $"\"{executable}\"";
            if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase)) {
                string assembly = typeof(Program).Assembly.Location;
                if (string.IsNullOrEmpty(assembly)) throw new InvalidOperationException("无法获取程序程序集路径");
                command += $" \"{assembly}\"";
            }
            return command;
        }
    }

    public bool IsEnabled {
        get {
            if (!OperatingSystem.IsWindows()) return false;
            using var key = Registry.CurrentUser.OpenSubKey(registryPath);
            return string.Equals(key?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void SetEnabled(bool enabled) {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (enabled) {
            using var key = Registry.CurrentUser.CreateSubKey(registryPath);
            key.SetValue(ValueName, Command, RegistryValueKind.String);
        } else {
            using var key = Registry.CurrentUser.OpenSubKey(registryPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
