using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using TelegramContacts.Services;

namespace TelegramContacts.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly TelegramService _tg = new();
    private readonly AppSettings _settings = TelegramService.LoadSettings();

    public MainViewModel()
    {
        _apiId = _settings.ApiId == 0 ? "" : _settings.ApiId.ToString();
        _apiHash = _settings.ApiHash;
        _phone = _settings.Phone;

        ContactsView = CollectionViewSource.GetDefaultView(Contacts);
        ContactsView.Filter = o => o is ContactInfo c && Matches(c, _search);

        LoginCommand = new AsyncCommand(LoginAsync, () => !IsBusy);
        RefreshCommand = new AsyncCommand(RefreshAsync, () => IsLoggedIn && !IsBusy);
        AddCommand = new AsyncCommand(AddAsync, () => IsLoggedIn && !IsBusy);
        SaveCommand = new AsyncCommand(SaveAsync, () => IsLoggedIn && !IsBusy && Selected != null);
        DeleteCommand = new AsyncCommand(DeleteAsync, () => IsLoggedIn && !IsBusy && Selected != null);
        NewCommand = new AsyncCommand(() => { Selected = null; ClearForm(); return Task.CompletedTask; });
        LogoutCommand = new AsyncCommand(LogoutAsync, () => IsLoggedIn && !IsBusy);
    }

    // ------------------------------------------------------------ login
    private string _apiId, _apiHash, _phone, _loginValue = "";
    public string ApiId { get => _apiId; set => Set(ref _apiId, value); }
    public string ApiHash { get => _apiHash; set => Set(ref _apiHash, value); }
    public string Phone { get => _phone; set => Set(ref _phone, value); }
    /// <summary>The code, 2FA password or name, depending on <see cref="Step"/>.</summary>
    public string LoginValue { get => _loginValue; set => Set(ref _loginValue, value); }

    private LoginStep? _step;
    public LoginStep? Step
    {
        get => _step;
        private set
        {
            if (!Set(ref _step, value)) return;
            Raise(nameof(StepPrompt)); Raise(nameof(IsAskingValue)); Raise(nameof(LoginButtonText));
            Raise(nameof(IsPasswordStep)); Raise(nameof(IsTextStep));
        }
    }
    public bool IsAskingValue => _step is LoginStep.Code or LoginStep.Password or LoginStep.Name;
    public bool IsPasswordStep => _step == LoginStep.Password;
    public bool IsTextStep => _step is LoginStep.Code or LoginStep.Name;
    public string StepPrompt => _step switch
    {
        LoginStep.Code => "أدخل الكود الذي وصلك في تلجرام",
        LoginStep.Password => "أدخل كلمة مرور التحقق بخطوتين",
        LoginStep.Name => "رقم جديد: أدخل اسمك لإنشاء الحساب",
        _ => "",
    };
    public string LoginButtonText => IsAskingValue ? "تأكيد" : "إرسال الكود";

    private bool _isLoggedIn;
    public bool IsLoggedIn
    {
        get => _isLoggedIn;
        private set { if (Set(ref _isLoggedIn, value)) Raise(nameof(IsLoggedOut)); }
    }
    public bool IsLoggedOut => !_isLoggedIn;

    private string _meText = "";
    public string MeText { get => _meText; private set => Set(ref _meText, value); }

    // ------------------------------------------------------------ contacts
    public ObservableCollection<ContactInfo> Contacts { get; } = [];
    public ICollectionView ContactsView { get; }

    private string _search = "";
    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value)) ContactsView.Refresh(); }
    }

    private ContactInfo? _selected;
    public ContactInfo? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            Raise(nameof(FormTitle)); Raise(nameof(IsEditing));
            if (value != null)
            {
                FirstName = value.FirstName; LastName = value.LastName; FormPhone = value.Phone;
            }
        }
    }
    public bool IsEditing => _selected != null;
    public string FormTitle => _selected == null ? "إضافة جهة اتصال" : "تعديل جهة الاتصال";

    private string _firstName = "", _lastName = "", _formPhone = "";
    public string FirstName { get => _firstName; set => Set(ref _firstName, value); }
    public string LastName { get => _lastName; set => Set(ref _lastName, value); }
    public string FormPhone { get => _formPhone; set => Set(ref _formPhone, value); }

    public int Count => Contacts.Count;

    // ------------------------------------------------------------ status
    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    private string _status = "";
    public string Status { get => _status; private set => Set(ref _status, value); }

    public AsyncCommand LoginCommand { get; }
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand AddCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand DeleteCommand { get; }
    public AsyncCommand NewCommand { get; }
    public AsyncCommand LogoutCommand { get; }

    /// <summary>Called once the window is shown: an existing session logs in without asking anything.</summary>
    public async Task StartAsync()
    {
        if (TelegramService.HasSession && _settings.ApiId != 0 && _settings.Phone.Length > 0)
            await Run("جارٍ استعادة الجلسة…", async () => await AfterStep(await _tg.LoginAsync(_settings, _settings.Phone)));
    }

    private Task LoginAsync() => Run("جارٍ تسجيل الدخول…", async () =>
    {
        if (!IsAskingValue)
        {
            if (!int.TryParse(ApiId.Trim(), out var id) || ApiHash.Trim().Length == 0)
                throw new InvalidOperationException("أدخل api_id و api_hash من my.telegram.org");
            if (Phone.Trim().Length < 6) throw new InvalidOperationException("أدخل رقم الهاتف مع رمز الدولة، مثل +9665xxxxxxx");
            _settings.ApiId = id; _settings.ApiHash = ApiHash.Trim(); _settings.Phone = Phone.Trim();
            TelegramService.SaveSettings(_settings);
            await AfterStep(await _tg.LoginAsync(_settings, _settings.Phone));
        }
        else
        {
            if (LoginValue.Trim().Length == 0) throw new InvalidOperationException(StepPrompt);
            var value = LoginValue.Trim();
            LoginValue = "";
            await AfterStep(await _tg.LoginAsync(_settings, value));
        }
    });

    private async Task AfterStep(LoginStep step)
    {
        if (step != LoginStep.Done) { Step = step; return; }
        Step = null;
        IsLoggedIn = true;
        var me = _tg.Me!;
        MeText = $"{me.first_name} {me.last_name}".Trim() + (me.MainUsername is { } u ? $"  @{u}" : "");
        await LoadContacts();
    }

    private Task RefreshAsync() => Run("جارٍ تحميل جهات الاتصال…", LoadContacts);

    private async Task LoadContacts()
    {
        var list = await _tg.GetContactsAsync();
        Contacts.Clear();
        foreach (var c in list) Contacts.Add(c);
        Raise(nameof(Count));
        Status = $"تم تحميل {list.Count} جهة اتصال";
    }

    private Task AddAsync() => Run("جارٍ الإضافة…", async () =>
    {
        var phone = new string(FormPhone.Where(ch => char.IsDigit(ch) || ch == '+').ToArray());
        if (FirstName.Trim().Length == 0) throw new InvalidOperationException("الاسم الأول مطلوب");
        if (phone.Length < 6) throw new InvalidOperationException("أدخل رقم هاتف صحيح مع رمز الدولة");
        var added = await _tg.AddAsync(FirstName.Trim(), LastName.Trim(), phone);
        if (added == null) { Status = "هذا الرقم ليس لديه حساب تلجرام"; return; }
        Replace(added);
        Selected = added;
        Status = $"تمت إضافة {added.DisplayName}";
    });

    private Task SaveAsync() => Run("جارٍ حفظ التعديل…", async () =>
    {
        if (Selected is not { } c) return;
        if (FirstName.Trim().Length == 0) throw new InvalidOperationException("الاسم الأول مطلوب");
        var edited = await _tg.EditAsync(c.Id, FirstName.Trim(), LastName.Trim());
        Replace(edited);
        Selected = edited;
        Status = $"تم حفظ {edited.DisplayName}";
    });

    private Task DeleteAsync() => Run("جارٍ الحذف…", async () =>
    {
        if (Selected is not { } c) return;
        var ok = MessageBox.Show($"حذف «{c.DisplayName}» من جهات اتصال تلجرام؟", "تأكيد الحذف",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No,
            MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
        if (ok != MessageBoxResult.Yes) return;
        await _tg.DeleteAsync([c.Id]);
        Contacts.Remove(c);
        Raise(nameof(Count));
        Selected = null;
        ClearForm();
        Status = $"تم حذف {c.DisplayName}";
    });

    private Task LogoutAsync() => Run("جارٍ تسجيل الخروج…", async () =>
    {
        await _tg.LogoutAsync();
        IsLoggedIn = false;
        Step = null;
        Contacts.Clear();
        Raise(nameof(Count));
        Selected = null;
        ClearForm();
        MeText = "";
        Status = "تم تسجيل الخروج";
    });

    private void Replace(ContactInfo c)
    {
        var i = Contacts.ToList().FindIndex(x => x.Id == c.Id);
        if (i >= 0) Contacts[i] = c; else Contacts.Insert(0, c);
        Raise(nameof(Count));
    }

    private void ClearForm() { FirstName = ""; LastName = ""; FormPhone = ""; }

    private static bool Matches(ContactInfo c, string q)
    {
        if (string.IsNullOrWhiteSpace(q)) return true;
        q = q.Trim();
        return c.DisplayName.Contains(q, StringComparison.CurrentCultureIgnoreCase)
            || c.Phone.Contains(q.TrimStart('+'), StringComparison.Ordinal)
            || c.Username.Contains(q.TrimStart('@'), StringComparison.OrdinalIgnoreCase);
    }

    private async Task Run(string busyText, Func<Task> work)
    {
        IsBusy = true;
        Status = busyText;
        try { await work(); }
        catch (TL.RpcException ex) { Status = "خطأ من تلجرام: " + Translate(ex.Message); }
        catch (Exception ex) { Status = ex.Message; }
        finally { IsBusy = false; }
    }

    private static string Translate(string code) => code switch
    {
        "PHONE_CODE_INVALID" => "الكود غير صحيح",
        "PHONE_CODE_EXPIRED" => "انتهت صلاحية الكود، أعد الإرسال",
        "PHONE_NUMBER_INVALID" => "رقم الهاتف غير صحيح",
        "PASSWORD_HASH_INVALID" => "كلمة المرور غير صحيحة",
        "API_ID_INVALID" => "api_id أو api_hash غير صحيح",
        _ when code.StartsWith("FLOOD", StringComparison.Ordinal) => "طلبات كثيرة، انتظر قليلاً ثم أعد المحاولة",
        "CONTACT_ID_INVALID" => "جهة الاتصال غير صالحة",
        _ => code,
    };
}
