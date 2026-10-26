using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.JSInterop;
using SimpleCatalog.Models;

namespace SimpleCatalog.Services;

public class AppState(IJSRuntime js)
{
    private const string ProductsKey = "simple-catalog-products";
    private const string UsersKey = "simple-catalog-users";
    private const string SessionKey = "simple-catalog-session";
    private bool initialized;
    private List<UserAccount> users = [];

    public event Action? Changed;
    public List<Product> Products { get; private set; } = [];
    public UserAccount? CurrentUser { get; private set; }

    public async Task InitializeAsync()
    {
        if (initialized) return;

        Products = await ReadAsync<List<Product>>(ProductsKey) ?? SeedProducts();
        users = await ReadAsync<List<UserAccount>>(UsersKey) ??
        [
            new UserAccount
            {
                Name = "Администратор",
                Email = "admin@catalog.ru",
                PasswordHash = Hash("admin123"),
                IsAdmin = true
            }
        ];

        await SaveAsync(ProductsKey, Products);
        await SaveAsync(UsersKey, users);

        var sessionEmail = await js.InvokeAsync<string?>("localStorage.getItem", SessionKey);
        CurrentUser = users.FirstOrDefault(user => user.Email.Equals(sessionEmail, StringComparison.OrdinalIgnoreCase));
        initialized = true;
        Changed?.Invoke();
    }

    public async Task<(bool Success, string Message)> RegisterAsync(string name, string email, string password)
    {
        await InitializeAsync();
        name = name.Trim();
        email = email.Trim().ToLowerInvariant();

        if (name.Length < 2) return (false, "Введите имя не короче двух символов.");
        if (!email.Contains('@')) return (false, "Введите корректную почту.");
        if (password.Length < 6) return (false, "Пароль должен содержать минимум 6 символов.");
        if (users.Any(user => user.Email.Equals(email, StringComparison.OrdinalIgnoreCase)))
            return (false, "Пользователь с такой почтой уже зарегистрирован.");

        var account = new UserAccount { Name = name, Email = email, PasswordHash = Hash(password) };
        users.Add(account);
        await SaveAsync(UsersKey, users);
        await SetSessionAsync(account);
        return (true, "Регистрация завершена.");
    }

    public async Task<(bool Success, string Message)> LoginAsync(string email, string password)
    {
        await InitializeAsync();
        var account = users.FirstOrDefault(user =>
            user.Email.Equals(email.Trim(), StringComparison.OrdinalIgnoreCase) &&
            user.PasswordHash == Hash(password));

        if (account is null) return (false, "Неверная почта или пароль.");
        await SetSessionAsync(account);
        return (true, "Вход выполнен.");
    }

    public async Task LogoutAsync()
    {
        CurrentUser = null;
        await js.InvokeVoidAsync("localStorage.removeItem", SessionKey);
        Changed?.Invoke();
    }

    public async Task AddProductAsync(Product product)
    {
        await InitializeAsync();
        product.Id = Products.Count == 0 ? 1 : Products.Max(item => item.Id) + 1;
        Products.Add(product);
        await SaveAsync(ProductsKey, Products);
        Changed?.Invoke();
    }

    public async Task DeleteProductAsync(int id)
    {
        await InitializeAsync();
        Products.RemoveAll(item => item.Id == id);
        await SaveAsync(ProductsKey, Products);
        Changed?.Invoke();
    }

    private async Task SetSessionAsync(UserAccount account)
    {
        CurrentUser = account;
        await js.InvokeVoidAsync("localStorage.setItem", SessionKey, account.Email);
        Changed?.Invoke();
    }

    private async Task<T?> ReadAsync<T>(string key)
    {
        var json = await js.InvokeAsync<string?>("localStorage.getItem", key);
        return string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json);
    }

    private Task SaveAsync<T>(string key, T value) =>
        js.InvokeVoidAsync("localStorage.setItem", key, JsonSerializer.Serialize(value)).AsTask();

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static List<Product> SeedProducts() =>
    [
        new() { Id = 1, Name = "Настольная лампа", Category = "Для дома", Price = 2490, Emoji = "💡", Description = "Мягкий свет и три уровня яркости.", IsNew = true },
        new() { Id = 2, Name = "Керамическая кружка", Category = "Посуда", Price = 790, Emoji = "☕", Description = "Удобная кружка объёмом 350 мл." },
        new() { Id = 3, Name = "Ежедневник", Category = "Канцелярия", Price = 1190, Emoji = "📓", Description = "Недатированный ежедневник на 160 страниц." },
        new() { Id = 4, Name = "Термобутылка", Category = "Посуда", Price = 1790, Emoji = "🧴", Description = "Сохраняет температуру напитка до 8 часов.", IsNew = true },
        new() { Id = 5, Name = "Органайзер", Category = "Для дома", Price = 990, Emoji = "🗄️", Description = "Компактное хранение мелочей на рабочем столе." },
        new() { Id = 6, Name = "Беспроводная мышь", Category = "Техника", Price = 2290, Emoji = "🖱️", Description = "Тихие клавиши и до 12 месяцев работы." }
    ];
}
