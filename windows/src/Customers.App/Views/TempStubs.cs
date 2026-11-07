using Microsoft.UI.Xaml.Controls;
namespace Customers.Ui;
public sealed class HomeView : UserControl { public HomeView(AppModel m, ShellView s) { } }
public sealed class SourcesView : UserControl { public SourcesView(AppModel m, ShellView s) { } }
public sealed class RunsView : UserControl { public RunsView(AppModel m, ShellView s) { } }
public sealed class SettingsView : UserControl { public SettingsView(AppModel m, SessionModel se, ShellView s) { } public void Select(SettingsSection x) { } }
public sealed class SmokeTest { public SmokeTest(string d) { } public Task RunAsync() => Task.CompletedTask; }
