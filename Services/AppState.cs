using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.JSInterop;
using SimpleCatalog.Models;

namespace SimpleCatalog.Services;

public class AppState(IJSRuntime js)
{
    // Имена ключей, под которыми данные сохраняются в localStorage браузера.
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
        // Повторная инициализация не нужна при переходах между страницами.
        if (initialized) return;

        // При первом запуске создаём стартовый каталог, при следующих — читаем сохранённый.
        Products = await ReadAsync<List<Product>>(ProductsKey) ?? SeedProducts();
        NormalizeProductImages();
        users = await ReadAsync<List<UserAccount>>(UsersKey) ??
        [
            // Учётная запись администратора доступна сразу в демонстрационной версии.
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

        // В сессии хранится только почта текущего пользователя.
        var sessionEmail = await js.InvokeAsync<string?>("localStorage.getItem", SessionKey);
        CurrentUser = users.FirstOrDefault(user => user.Email.Equals(sessionEmail, StringComparison.OrdinalIgnoreCase));
        initialized = true;
        Changed?.Invoke();
    }

    public async Task<(bool Success, string Message)> RegisterAsync(string name, string email, string password)
    {
        await InitializeAsync();

        // Убираем случайные пробелы и приводим почту к единому виду.
        name = name.Trim();
        email = email.Trim().ToLowerInvariant();

        if (name.Length < 2) return (false, "Введите имя не короче двух символов.");
        if (!email.Contains('@')) return (false, "Введите корректную почту.");
        if (password.Length < 6) return (false, "Пароль должен содержать минимум 6 символов.");
        if (users.Any(user => user.Email.Equals(email, StringComparison.OrdinalIgnoreCase)))
            return (false, "Пользователь с такой почтой уже зарегистрирован.");

        // Пароль сохраняется как SHA-256-хеш, а не как открытый текст.
        var account = new UserAccount { Name = name, Email = email, PasswordHash = Hash(password) };
        users.Add(account);
        await SaveAsync(UsersKey, users);
        await SetSessionAsync(account);
        return (true, "Регистрация завершена.");
    }

    public async Task<(bool Success, string Message)> LoginAsync(string email, string password)
    {
        await InitializeAsync();

        // Пользователь считается найденным, если совпали почта и хеш пароля.
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

        // Новый идентификатор берётся после максимального существующего ID.
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
        // localStorage хранит строки, поэтому объект преобразуется из JSON.
        var json = await js.InvokeAsync<string?>("localStorage.getItem", key);
        return string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json);
    }

    // Любое изменение сразу записывается обратно в браузер.
    private Task SaveAsync<T>(string key, T value) =>
        js.InvokeVoidAsync("localStorage.setItem", key, JsonSerializer.Serialize(value)).AsTask();

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    // Начальные товары используются, пока администратор не изменил каталог.
    private static List<Product> SeedProducts() =>
    [
        new() { Id = 1, Name = "Настольная лампа", Category = "Для дома", Price = 2490, ImageUrl = "images/lamp.jpg", ImageAlt = "Настольная лампа на рабочем столе", Description = "Мягкий свет и три уровня яркости.", IsNew = true },
        new() { Id = 2, Name = "Керамическая кружка", Category = "Посуда", Price = 790, ImageUrl = "images/mug.jpg", ImageAlt = "Керамическая кружка на светлом столе", Description = "Удобная кружка объёмом 350 мл." },
        new() { Id = 3, Name = "Ежедневник", Category = "Канцелярия", Price = 1190, ImageUrl = "images/notebook.jpg", ImageAlt = "Открытый ежедневник рядом с кружкой", Description = "Недатированный ежедневник на 160 страниц." },
        new() { Id = 4, Name = "Термобутылка", Category = "Посуда", Price = 1790, ImageUrl = "images/bottle.jpg", ImageAlt = "Белая термобутылка на рабочем столе", Description = "Сохраняет температуру напитка до 8 часов.", IsNew = true },
        new() { Id = 5, Name = "Органайзер", Category = "Для дома", Price = 990, ImageUrl = "images/organizer.jpg", ImageAlt = "Организованный рабочий стол с канцелярией", Description = "Компактное хранение мелочей на рабочем столе." },
        new() { Id = 6, Name = "Беспроводная мышь", Category = "Техника", Price = 2290, ImageUrl = "images/mouse.jpg", ImageAlt = "Беспроводная мышь рядом с ноутбуком", Description = "Тихие клавиши и до 12 месяцев работы." }
    ];

    private void NormalizeProductImages()
    {
        // Этот словарь обновляет старые записи, которые были сохранены ещё с эмодзи.
        var images = new Dictionary<string, (string Url, string Alt)>(StringComparer.OrdinalIgnoreCase)
        {
            ["Настольная лампа"] = ("images/lamp.jpg", "Настольная лампа на рабочем столе"),
            ["Керамическая кружка"] = ("images/mug.jpg", "Керамическая кружка на светлом столе"),
            ["Ежедневник"] = ("images/notebook.jpg", "Открытый ежедневник рядом с кружкой"),
            ["Термобутылка"] = ("images/bottle.jpg", "Белая термобутылка на рабочем столе"),
            ["Органайзер"] = ("images/organizer.jpg", "Рабочий стол с канцелярией"),
            ["Беспроводная мышь"] = ("images/mouse.jpg", "Беспроводная мышь рядом с ноутбуком")
        };

        foreach (var product in Products)
        {
            // Для стандартных товаров всегда используем подходящие локальные фотографии.
            if (images.TryGetValue(product.Name, out var image))
            {
                product.ImageUrl = image.Url;
                product.ImageAlt = image.Alt;
            }
            else if (string.IsNullOrWhiteSpace(product.ImageUrl))
            {
                // Пользовательскому товару без фото назначается нейтральная заглушка.
                product.ImageUrl = "images/hero-box.jpg";
                product.ImageAlt = "Фото товара";
            }
        }
    }
}
