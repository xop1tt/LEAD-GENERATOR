using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Customers.Ui;

/// <summary>Вход и регистрация: облачная учётная запись (почта и пароль) или локальная (имя и пароль, всё хранится только на компьютере).</summary>
public sealed partial class AuthView : UserControl
{
    private readonly SessionModel _session;
    private bool _localRegister;
    private Action? _one;
    private Action? _two;
    private Action? _three;

    public AuthView(SessionModel session)
    {
        _session = session;
        InitializeComponent();
        _localRegister = !session.HasUsers;
        _session.PropertyChanged += OnSessionChanged;
        Unloaded += (_, _) => _session.PropertyChanged -= OnSessionChanged;
        PasswordRules.Text = "Не короче 8 символов. Пароль хранится только в виде необратимого хеша.";
        OfflineNote.Text = session.CloudAvailable
            ? "Без интернета можно работать с локальной учётной записью. Облачный вход требует сети только при первом входе."
            : "Облачная синхронизация в этой сборке не настроена: доступны локальные учётные записи.";
        Render();
        Loaded += (_, _) => FocusFirst();
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SessionModel.Stage) or nameof(SessionModel.PendingEmail))
        {
            Render();
        }
        else
        {
            RenderStatus();
        }
    }

    private void SetStage(AuthStage stage)
    {
        _session.Stage = stage;
        Render();
    }

    private void Render()
    {
        var stage = _session.Stage;
        bool user = false, email = false, code = false, password = false, confirm = false, remember = false;
        string title, primary, hint = "";
        (string Text, Action Do)? one = null, two = null, three = null;
        switch (stage)
        {
            case AuthStage.SignIn:
                title = "Вход";
                email = password = true;
                primary = "Войти";
                one = ("Забыли пароль?", () => SetStage(AuthStage.Forgot));
                two = ("Создать аккаунт", () => SetStage(AuthStage.SignUp));
                three = ("Работать без облака (локальная учётная запись)", () => SetStage(AuthStage.Local));
                break;
            case AuthStage.SignUp:
                title = "Регистрация";
                email = password = confirm = true;
                primary = "Создать аккаунт";
                hint = "Мы отправим на почту код подтверждения. Тот же аккаунт работает и в версии для macOS.";
                one = ("Уже есть аккаунт — войти", () => SetStage(AuthStage.SignIn));
                break;
            case AuthStage.Confirm:
                title = "Подтвердите почту";
                code = true;
                primary = "Подтвердить";
                hint = $"Введите код из письма, отправленного на {_session.PendingEmail}.";
                one = ("Отправить код ещё раз", () => _ = _session.ResendConfirmationAsync(_session.PendingEmail));
                two = ("Назад ко входу", () => SetStage(AuthStage.SignIn));
                break;
            case AuthStage.Forgot:
                title = "Восстановление пароля";
                email = true;
                primary = "Отправить код";
                hint = "Пришлём на почту код для смены пароля.";
                one = ("У меня уже есть код", () => SetStage(AuthStage.Reset));
                two = ("Назад ко входу", () => SetStage(AuthStage.SignIn));
                break;
            case AuthStage.Reset:
                title = "Новый пароль";
                email = code = password = confirm = true;
                primary = "Сменить пароль";
                one = ("Назад ко входу", () => SetStage(AuthStage.SignIn));
                break;
            case AuthStage.NewPassword:
                title = "Задайте новый пароль";
                password = confirm = true;
                primary = "Сохранить пароль";
                break;
            default:
                title = _localRegister ? "Новая локальная учётная запись" : "Вход без облака";
                user = password = remember = true;
                confirm = _localRegister;
                primary = _localRegister ? "Создать" : "Войти";
                hint = "Данные хранятся только на этом компьютере в зашифрованной базе.";
                if (_session.HasUsers)
                {
                    one = (_localRegister ? "У меня уже есть учётная запись" : "Создать новую локальную учётную запись", () =>
                    {
                        _localRegister = !_localRegister;
                        Render();
                    });
                }

                if (_session.CloudAvailable)
                {
                    two = ("Войти через облако", () => SetStage(AuthStage.SignIn));
                }

                break;
        }

        StageTitle.Text = title;
        StageHint.Text = hint;
        StageHint.Visibility = hint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UsernameBox.Visibility = Fmt.Vis(user);
        EmailBox.Visibility = Fmt.Vis(email);
        CodeBox.Visibility = Fmt.Vis(code);
        PasswordBox.Visibility = Fmt.Vis(password);
        ConfirmBox.Visibility = Fmt.Vis(confirm);
        PasswordRules.Visibility = Fmt.Vis(confirm);
        RememberBox.Visibility = Fmt.Vis(remember);
        PasswordBox.Header = stage == AuthStage.Reset || stage == AuthStage.NewPassword ? "Новый пароль" : "Пароль";
        if (stage is AuthStage.Confirm or AuthStage.Reset && _session.PendingEmail.Length > 0 && EmailBox.Text.Length == 0)
        {
            EmailBox.Text = _session.PendingEmail;
        }

        PrimaryButton.Content = primary;
        SetLink(LinkOne, one, a => _one = a);
        SetLink(LinkTwo, two, a => _two = a);
        SetLink(LinkThree, three, a => _three = a);
        PasswordBox.Password = "";
        ConfirmBox.Password = "";
        CodeBox.Text = "";
        RenderStatus();
        FocusFirst();
    }

    private static void SetLink(HyperlinkButton button, (string Text, Action Do)? link, Action<Action?> store)
    {
        button.Visibility = Fmt.Vis(link is not null);
        button.Content = link?.Text;
        store(link?.Do);
    }

    private void RenderStatus()
    {
        ErrorBar.IsOpen = !string.IsNullOrEmpty(_session.Error);
        ErrorBar.Message = _session.Error ?? "";
        InfoBarMessage.IsOpen = !string.IsNullOrEmpty(_session.Info);
        InfoBarMessage.Message = _session.Info ?? "";
        BusyBar.Visibility = Fmt.Vis(_session.Busy);
        PrimaryButton.IsEnabled = !_session.Busy;
    }

    private void FocusFirst()
    {
        Control? target = null;
        foreach (var c in new Control[] { UsernameBox, EmailBox, CodeBox, PasswordBox })
        {
            if (c.Visibility == Visibility.Visible)
            {
                target = c;
                break;
            }
        }

        target?.Focus(FocusState.Programmatic);
    }

    private void OnKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && !_session.Busy)
        {
            e.Handled = true;
            OnPrimaryClick(sender, e);
        }
    }

    private void OnLinkOneClick(object sender, RoutedEventArgs e) => _one?.Invoke();

    private void OnLinkTwoClick(object sender, RoutedEventArgs e) => _two?.Invoke();

    private void OnLinkThreeClick(object sender, RoutedEventArgs e) => _three?.Invoke();

    private async void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        if (_session.Busy)
        {
            return;
        }

        _session.Error = null;
        var email = EmailBox.Text.Trim();
        var password = PasswordBox.Password;
        var confirm = ConfirmBox.Password;
        switch (_session.Stage)
        {
            case AuthStage.SignIn:
                await _session.CloudSignInAsync(email, password);
                break;
            case AuthStage.SignUp:
                await _session.CloudSignUpAsync(email, password, confirm);
                break;
            case AuthStage.Confirm:
                await _session.ConfirmSignUpAsync(_session.PendingEmail, CodeBox.Text.Trim());
                break;
            case AuthStage.Forgot:
                await _session.RequestResetAsync(email);
                break;
            case AuthStage.Reset:
                await _session.FinishResetAsync(email, CodeBox.Text.Trim(), password, confirm);
                break;
            case AuthStage.NewPassword:
                await _session.SetPasswordAfterRecoveryAsync(password, confirm);
                break;
            default:
                if (_localRegister)
                {
                    await _session.RegisterAsync(UsernameBox.Text, password, confirm, RememberBox.IsChecked == true);
                }
                else
                {
                    await _session.LoginAsync(UsernameBox.Text, password, RememberBox.IsChecked == true);
                }

                break;
        }
    }
}
