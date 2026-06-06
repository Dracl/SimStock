using Another_Mirai_Native.Abstractions.Attributes;
using Another_Mirai_Native.Abstractions.Context;
using Another_Mirai_Native.Abstractions.Handlers;

namespace SimStock;

[Menu("管理面板")]
public class MenuEntry : IMenuHandler
{
    private AdminWindow? _window;

    public void OnMenu(MenuContext e)
    {
        if (_window == null)
        {
            _window = new AdminWindow();
            _window.Closing += (s, ev) => { ev.Cancel = true; _window.Hide(); };
        }
        _window.Show();
    }
}