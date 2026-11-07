using Microsoft.UI.Dispatching;

namespace Customers.Ui;

/// <summary>Переход в поток интерфейса для моделей из Customers.Presentation.</summary>
internal sealed class DispatcherUi(DispatcherQueue queue) : IUiDispatcher
{
    public void Post(Action action)
    {
        if (queue.HasThreadAccess)
        {
            action();
        }
        else
        {
            queue.TryEnqueue(() => action());
        }
    }
}
