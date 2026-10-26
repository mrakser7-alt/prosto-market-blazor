using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.JSInterop;
using SimpleCatalog.Models;

namespace SimpleCatalog.Services;

public class SupabaseService(HttpClient http, IJSRuntime js, SupabaseOptions options)
{
    private const string AccessTokenKey = "simple-catalog-access-token";
    private const string RefreshTokenKey = "simple-catalog-refresh-token";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private string? accessToken;
    private string? refreshToken;

    public bool IsConfigured => options.IsConfigured;

    public async Task<List<Product>> GetProductsAsync()
    {
        using var request = CreateRequest(HttpMethod.Get,
            "rest/v1/products?select=id,name,category,price,description,image_url,image_alt,is_new&order=id.asc");
        using var response = await http.SendAsync(request);
        await EnsureSuccessAsync(response);

        var rows = await response.Content.ReadFromJsonAsync<List<ProductRow>>(JsonOptions) ?? [];
        return rows.Select(row => row.ToProduct()).ToList();
    }

    public async Task<AuthResult> RegisterAsync(string name, string email, string password)
    {
        var payload = new { email, password, data = new { name } };
        using var response = await SendAuthAsync("auth/v1/signup", payload);

        if (!response.IsSuccessStatusCode)
            return AuthResult.Fail(await ReadErrorAsync(response));

        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions);
        if (auth?.User is null)
            return AuthResult.Fail("Не удалось создать пользователя.");

        if (string.IsNullOrWhiteSpace(auth.AccessToken))
            return AuthResult.Fail("Проверьте почту и подтвердите регистрацию, затем выполните вход.");

        await StoreSessionAsync(auth);
        var account = await GetProfileAsync(auth.User.Id);
        return account is null ? AuthResult.Fail("Профиль пользователя не найден.") : AuthResult.Ok(account);
    }

    public async Task<AuthResult> LoginAsync(string email, string password)
    {
        var payload = new { email, password };
        using var response = await SendAuthAsync("auth/v1/token?grant_type=password", payload);

        if (!response.IsSuccessStatusCode)
            return AuthResult.Fail(await ReadErrorAsync(response));

        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions);
        if (auth?.User is null || string.IsNullOrWhiteSpace(auth.AccessToken))
            return AuthResult.Fail("Supabase не вернул активную сессию.");

        await StoreSessionAsync(auth);
        var account = await GetProfileAsync(auth.User.Id);
        return account is null ? AuthResult.Fail("Профиль пользователя не найден.") : AuthResult.Ok(account);
    }

    public async Task<UserAccount?> RestoreSessionAsync()
    {
        accessToken = await js.InvokeAsync<string?>("localStorage.getItem", AccessTokenKey);
        refreshToken = await js.InvokeAsync<string?>("localStorage.getItem", RefreshTokenKey);

        if (string.IsNullOrWhiteSpace(refreshToken)) return null;

        var payload = new { refresh_token = refreshToken };
        using var response = await SendAuthAsync("auth/v1/token?grant_type=refresh_token", payload);
        if (!response.IsSuccessStatusCode)
        {
            await ClearSessionAsync();
            return null;
        }

        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions);
        if (auth?.User is null || string.IsNullOrWhiteSpace(auth.AccessToken))
        {
            await ClearSessionAsync();
            return null;
        }

        await StoreSessionAsync(auth);
        return await GetProfileAsync(auth.User.Id);
    }

    public async Task LogoutAsync()
    {
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            using var request = CreateRequest(HttpMethod.Post, "auth/v1/logout", includeUserToken: true);
            using var _ = await http.SendAsync(request);
        }

        await ClearSessionAsync();
    }

    public async Task AddProductAsync(Product product)
    {
        var row = ProductRow.FromProduct(product);
        using var request = CreateRequest(HttpMethod.Post, "rest/v1/products", includeUserToken: true);
        request.Headers.TryAddWithoutValidation("Prefer", "return=minimal");
        request.Content = JsonContent.Create(row, options: JsonOptions);

        using var response = await http.SendAsync(request);
        await EnsureSuccessAsync(response);
    }

    public async Task DeleteProductAsync(int id)
    {
        using var request = CreateRequest(HttpMethod.Delete, $"rest/v1/products?id=eq.{id}", includeUserToken: true);
        using var response = await http.SendAsync(request);
        await EnsureSuccessAsync(response);
    }

    private async Task<UserAccount?> GetProfileAsync(Guid userId)
    {
        using var request = CreateRequest(HttpMethod.Get,
            $"rest/v1/profiles?id=eq.{userId:D}&select=id,name,email,role", includeUserToken: true);
        using var response = await http.SendAsync(request);
        await EnsureSuccessAsync(response);

        var profiles = await response.Content.ReadFromJsonAsync<List<ProfileRow>>(JsonOptions) ?? [];
        var profile = profiles.FirstOrDefault();
        return profile is null
            ? null
            : new UserAccount
            {
                Id = profile.Id,
                Name = profile.Name,
                Email = profile.Email,
                IsAdmin = profile.Role.Equals("admin", StringComparison.OrdinalIgnoreCase)
            };
    }

    private async Task<HttpResponseMessage> SendAuthAsync(string path, object payload)
    {
        var request = CreateRequest(HttpMethod.Post, path);
        request.Content = JsonContent.Create(payload, options: JsonOptions);
        return await http.SendAsync(request);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, bool includeUserToken = false)
    {
        var baseUrl = options.Url.TrimEnd('/');
        var request = new HttpRequestMessage(method, $"{baseUrl}/{path}");
        request.Headers.TryAddWithoutValidation("apikey", options.PublishableKey);

        var bearer = includeUserToken ? accessToken : null;
        if (string.IsNullOrWhiteSpace(bearer) && options.PublishableKey.StartsWith("eyJ", StringComparison.Ordinal))
            bearer = options.PublishableKey;

        if (!string.IsNullOrWhiteSpace(bearer))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        return request;
    }

    private async Task StoreSessionAsync(AuthResponse auth)
    {
        accessToken = auth.AccessToken;
        refreshToken = auth.RefreshToken;
        await js.InvokeVoidAsync("localStorage.setItem", AccessTokenKey, accessToken);
        await js.InvokeVoidAsync("localStorage.setItem", RefreshTokenKey, refreshToken);
    }

    private async Task ClearSessionAsync()
    {
        accessToken = null;
        refreshToken = null;
        await js.InvokeVoidAsync("localStorage.removeItem", AccessTokenKey);
        await js.InvokeVoidAsync("localStorage.removeItem", RefreshTokenKey);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        throw new InvalidOperationException(await ReadErrorAsync(response));
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(JsonOptions);
            return error?.Message ?? error?.ErrorDescription ?? error?.Hint ?? $"Ошибка Supabase: {(int)response.StatusCode}.";
        }
        catch
        {
            return response.StatusCode == HttpStatusCode.Unauthorized
                ? "Сессия истекла. Выполните вход ещё раз."
                : $"Ошибка Supabase: {(int)response.StatusCode}.";
        }
    }

    public sealed record AuthResult(bool Success, UserAccount? User, string Message)
    {
        public static AuthResult Ok(UserAccount user) => new(true, user, "Готово.");
        public static AuthResult Fail(string message) => new(false, null, message);
    }

    private sealed class AuthResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("user")] public AuthUser? User { get; set; }
    }

    private sealed class AuthUser
    {
        [JsonPropertyName("id")] public Guid Id { get; set; }
    }

    private sealed class ErrorResponse
    {
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("error_description")] public string? ErrorDescription { get; set; }
        [JsonPropertyName("hint")] public string? Hint { get; set; }
    }

    private sealed class ProfileRow
    {
        [JsonPropertyName("id")] public Guid Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("email")] public string Email { get; set; } = string.Empty;
        [JsonPropertyName("role")] public string Role { get; set; } = "user";
    }

    private sealed class ProductRow
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("category")] public string Category { get; set; } = string.Empty;
        [JsonPropertyName("price")] public decimal Price { get; set; }
        [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;
        [JsonPropertyName("image_url")] public string ImageUrl { get; set; } = string.Empty;
        [JsonPropertyName("image_alt")] public string ImageAlt { get; set; } = string.Empty;
        [JsonPropertyName("is_new")] public bool IsNew { get; set; }

        public Product ToProduct() => new()
        {
            Id = Id,
            Name = Name,
            Category = Category,
            Price = Price,
            Description = Description,
            ImageUrl = ImageUrl,
            ImageAlt = ImageAlt,
            IsNew = IsNew
        };

        public static ProductRow FromProduct(Product product) => new()
        {
            Name = product.Name,
            Category = product.Category,
            Price = product.Price,
            Description = product.Description,
            ImageUrl = product.ImageUrl,
            ImageAlt = product.ImageAlt,
            IsNew = product.IsNew
        };
    }
}
