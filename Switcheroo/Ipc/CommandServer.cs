using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace Switcheroo.Ipc
{
    /// <summary>
    /// 极简命名管道命令通道，让脚本（Quicker / AHK / HA / 命令行）能驱动 Switcheroo：
    /// <code>
    ///   switcheroo.exe --show "微信"     打开切换器并预填搜索词
    ///   switcheroo.exe --switch "微信"   直接切到第一个匹配窗口
    /// </code>
    /// 单实例：第二个进程只负责把命令发进管道然后退出。
    /// 管道名固定，不含用户目录，避免多用户互相干扰（本工具按用户运行）。
    /// </summary>
    public sealed class CommandServer : IDisposable
    {
        private const string PipeName = "switcheroo-ipc-v1";

        private readonly Action<string> _onCommand;
        private Thread _thread;
        private volatile bool _stopping;

        public CommandServer(Action<string> onCommand)
        {
            _onCommand = onCommand;
            _thread = new Thread(Loop) { IsBackground = true, Name = "switcheroo-ipc" };
            _thread.Start();
        }

        private void Loop()
        {
            while (!_stopping)
            {
                try
                {
                    using (var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.None))
                    {
                        server.WaitForConnection();
                        using (var reader = new StreamReader(server, Encoding.UTF8))
                        {
                            var line = reader.ReadLine();
                            if (!string.IsNullOrEmpty(line) && _onCommand != null)
                            {
                                try { _onCommand(line); } catch { }
                            }
                        }
                    }
                }
                catch
                {
                    Thread.Sleep(200);
                }
            }
        }

        /// <summary>客户端：把命令发给已运行的实例。返回 false 表示没有实例在跑。</summary>
        public static bool TrySend(string command, int timeoutMs = 500)
        {
            try
            {
                using (var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    client.Connect(timeoutMs);
                    using (var writer = new StreamWriter(client, new UTF8Encoding(false)))
                    {
                        writer.WriteLine(command);
                        writer.Flush();
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            _stopping = true;
        }
    }
}
