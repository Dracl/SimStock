using Another_Mirai_Native.Abstractions.Attributes;
using Another_Mirai_Native.Abstractions.Context;
using Another_Mirai_Native.Abstractions.Handlers;
using System.Windows.Threading;

namespace SimStock;

[Menu("管理面板")]
public class MenuEntry : IMenuHandler
{
    private AdminWindow? _window;
    private Thread? _uiThread;

    public void OnMenu(MenuContext e)
    {
        if (_window == null)
        {
            // WPF 窗口必须在独立 STA 线程上运行，拥有自己的 Dispatcher 消息泵。
            // AMN2 宿主进程不是 WPF 应用，没有 WPF 消息循环，直接 Show() 会导致
            // 窗口虽然显示但无法接收键盘输入。
            using var ready = new ManualResetEventSlim(false);
            _uiThread = new Thread(() =>
            {
                _window = new AdminWindow();
                _window.Closing += (s, ev) => { ev.Cancel = true; _window.Hide(); };
                ready.Set();
                Dispatcher.Run();
            })
            {
                IsBackground = true,
            };
            _uiThread.SetApartmentState(ApartmentState.STA);
            _uiThread.Start();
            ready.Wait();
        }

        _window!.Dispatcher.Invoke(() => _window.Show());
    }
}