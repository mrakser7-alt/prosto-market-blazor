using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SimpleCatalog;
using SimpleCatalog.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Корневые компоненты подключаются к элементам из wwwroot/index.html.
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// HttpClient нужен Blazor для загрузки файлов, а AppState хранит состояние каталога и пользователя.
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<AppState>();

await builder.Build().RunAsync();
