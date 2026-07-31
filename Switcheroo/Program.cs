/*
 * Switcheroo - The incremental-search task switcher for Windows.
 * https://github.com/coezbek/switcheroo
 * Copyright 2009, 2010 James Sulak
 * Copyright 2014 Regin Larsen
 * 
 * Switcheroo is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * Switcheroo is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 * 
 * You should have received a copy of the GNU General Public License
 * along with Switcheroo.  If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Xml;
using Switcheroo.Properties;

namespace Switcheroo
{
    internal class Program
    {
        private const string mutex_id = "DBDE24E4-91F6-11DF-B495-C536DFD72085-switcheroo";

#if CONSOLE_DEBUG
        // P/Invoke declarations for console handling
        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(uint dwProcessId);

        private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate HandlerRoutine, bool Add);

        private delegate bool ConsoleCtrlDelegate(CtrlTypes CtrlType);

        private enum CtrlTypes
        {
            CTRL_C_EVENT = 0,
            CTRL_BREAK_EVENT = 1,
            CTRL_CLOSE_EVENT = 2,
            CTRL_LOGOFF_EVENT = 5,
            CTRL_SHUTDOWN_EVENT = 6
        }

        private static CancellationTokenSource _cts;
#endif

        public const string AppId = "github.com.coezbek.switcheroo";

        [DllImport("shell32.dll", SetLastError = true)]
        private static extern void SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string AppID);

        [STAThread]
        private static void Main()
        {
#if CONSOLE_DEBUG
            // Attach to the parent console (your WSL terminal)
            AttachConsole(ATTACH_PARENT_PROCESS);
            _cts = new CancellationTokenSource();
            
            // Set up the low-level handler
            SetConsoleCtrlHandler(type => {
                Console.WriteLine("Ctrl+C detected. Shutting down Switcheroo...");
                if (type == CtrlTypes.CTRL_C_EVENT)
                {
                    Console.WriteLine("Ctrl+C detected. Shutting down Switcheroo...");
                    _cts.Cancel();
                    // Return true to indicate we've handled the event
                    return true;
                }
                return false;
            }, true);
#endif
            RunAsAdministratorIfConfigured();

            using (var mutex = new Mutex(false, mutex_id))
            {
                var hasHandle = false;
                try
                {
                    try
                    {
                        hasHandle = mutex.WaitOne(5000, false);
                        if (hasHandle == false) return; //another instance exist
                    }
                    catch (AbandonedMutexException)
                    {
                        // Log the fact the mutex was abandoned in another process, it will still get aquired
                    }

                    // 所有构建统一使用自定义配置 provider（PortableSettingsProvider）：
                    // 安装版存 %AppData%\Switcheroo\settings.xml、便携版存 exe 同目录——
                    // 固定路径不随版本号变化，彻底解决"每次升级配置丢失"问题
                    // （.NET 默认 LocalFileSettingsProvider 按版本号分目录，旧方案仅 FirstRun 迁移一次）。
                    MakePortable(Settings.Default);

                    MigrateUserSettings();

                    // Set the AppUserModelID for the process for proper taskbar grouping and toast notifications
                    SetCurrentProcessExplicitAppUserModelID(AppId);

                    var app = new App();
                    var mainWindow = new MainWindow();

#if CONSOLE_DEBUG
                    // When cancellation is requested, shut down the WPF application
                    _cts.Token.Register(() =>
                    {
                        // We need to dispatch this to the UI thread
                        app.Dispatcher.Invoke(app.Shutdown);
                    });
#endif

                    Console.WriteLine("Switcheroo started...");

                    // This starts the WPF message loop and blocks until the app exits
                    app.Run(mainWindow);
                }
                finally
                {
                    if (hasHandle)
                        mutex.ReleaseMutex();
                }
            }
        }

        private static void RunAsAdministratorIfConfigured()
        {
            if (RunAsAdminRequested() && !IsRunAsAdmin())
            {
                ProcessStartInfo proc = new ProcessStartInfo
                {
                    UseShellExecute = true,
                    WorkingDirectory = Environment.CurrentDirectory,
                    FileName = Assembly.GetEntryAssembly().CodeBase,
                    Verb = "runas"
                };

                Process.Start(proc);
                Environment.Exit(0);
            }
        }

        private static bool RunAsAdminRequested()
        {
            return Settings.Default.RunAsAdmin;
        }

        private static string CurrentSettingsFilePath;

        private static void MakePortable(ApplicationSettingsBase settings)
        {
            var portableSettingsProvider = new PortableSettingsProvider();
            CurrentSettingsFilePath = portableSettingsProvider.SettingsFilePath;
            settings.Providers.Add(portableSettingsProvider);
            foreach (SettingsProperty prop in settings.Properties)
            {
                prop.Provider = portableSettingsProvider;
            }
            settings.Reload();
        }

        /// <summary>
        /// 首次运行（或首次用新配置机制运行）时的配置迁移：
        /// 从旧版 .NET 默认位置（%LocalAppData%\Switcheroo\Switcheroo.exe_Url_*\*\user.config，取最新）
        /// 把用户设置合并到新固定位置配置（安装版 %AppData%\Switcheroo\settings.xml / 便携版 exe 同目录）。
        /// 旧方案按版本号分目录 + FirstRun 只 Upgrade 一次，跳版本/混用便携与安装版都会丢配置——
        /// 这里一次性兜底迁移，之后配置固定在单一路径，永不随版本号丢失。
        /// </summary>
        private static void MigrateUserSettings()
        {
            if (!Settings.Default.FirstRun) return;

            // 新位置已有配置文件（例如用户手动拷贝过）则跳过迁移
            if (File.Exists(CurrentSettingsFilePath))
            {
                Settings.Default.FirstRun = false;
                Settings.Default.Save();
                return;
            }

            string legacyConfig = FindLatestLegacyUserConfig();
            if (legacyConfig != null)
            {
                try
                {
                    MigrateLegacySettings(legacyConfig, CurrentSettingsFilePath);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] Failed migrating settings from '{legacyConfig}': {ex.Message}");
                }
            }

            Settings.Default.FirstRun = false;
            Settings.Default.Save();
        }

        private static string FindLatestLegacyUserConfig()
        {
            string baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Switcheroo");
            if (!Directory.Exists(baseDir)) return null;

            string latest = null;
            DateTime latestTime = DateTime.MinValue;
            try
            {
                foreach (var f in Directory.GetFiles(baseDir, "user.config", SearchOption.AllDirectories))
                {
                    var t = File.GetLastWriteTimeUtc(f);
                    if (t > latestTime)
                    {
                        latestTime = t;
                        latest = f;
                    }
                }
            }
            catch
            {
            }
            return latest;
        }

        /// <summary>
        /// 把旧 user.config 的 &lt;userSettings&gt; 下所有 setting（value 的 InnerText）合并进新配置。
        /// 与 PortableSettingsProvider 存储等价：新配置 setting 节点的 InnerText = 序列化值。
        /// FirstRun 由本方法调用方统一管理，跳过。
        /// </summary>
        private static void MigrateLegacySettings(string legacyConfigPath, string newConfigPath)
        {
            var oldDoc = new XmlDocument();
            oldDoc.Load(legacyConfigPath);

            var newDoc = new XmlDocument();
            if (File.Exists(newConfigPath))
            {
                newDoc.Load(newConfigPath);
            }
            else
            {
                newDoc.AppendChild(newDoc.CreateXmlDeclaration("1.0", "utf-8", string.Empty));
                newDoc.AppendChild(newDoc.CreateElement("settings"));
            }

            XmlNode localSettings = newDoc.DocumentElement.SelectSingleNode("localSettings");
            if (localSettings == null)
            {
                localSettings = newDoc.CreateElement("localSettings");
                newDoc.DocumentElement.AppendChild(localSettings);
            }

            string machineName = Environment.MachineName.ToLowerInvariant();
            XmlNode machineNode = localSettings.SelectSingleNode(machineName);
            if (machineNode == null)
            {
                machineNode = newDoc.CreateElement(machineName);
                localSettings.AppendChild(machineNode);
            }

            int migrated = 0;
            foreach (XmlNode setting in oldDoc.SelectNodes("//userSettings/*/setting"))
            {
                var nameAttr = setting.Attributes["name"];
                if (nameAttr == null) continue;
                string name = nameAttr.Value;
                if (name == "FirstRun") continue; // 由调用方管理

                var valueNode = setting.SelectSingleNode("value");
                string value = valueNode != null ? valueNode.InnerText : string.Empty;

                var existing = machineNode.SelectSingleNode(string.Format("setting[@name='{0}']", name));
                if (existing != null)
                {
                    existing.InnerText = value;
                }
                else
                {
                    var el = newDoc.CreateElement("setting");
                    var attr = newDoc.CreateAttribute("name");
                    attr.Value = name;
                    el.Attributes.Append(attr);
                    el.InnerText = value;
                    machineNode.AppendChild(el);
                }
                migrated++;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(newConfigPath));
            newDoc.Save(newConfigPath);
            Console.WriteLine($"[Settings] Migrated {migrated} settings from legacy config '{legacyConfigPath}'");
        }

        private static bool IsRunAsAdmin()
        {
            WindowsIdentity id = WindowsIdentity.GetCurrent();
            WindowsPrincipal principal = new WindowsPrincipal(id);

            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
}