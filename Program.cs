using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using QuestPDF.Infrastructure;
using RedAJP.Clases;
using RedAJP.Services;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// ============================================================
// 1. CONFIGURACIÓN BASE
// ============================================================
if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddUserSecrets<Program>();
}
builder.Configuration.AddEnvironmentVariables();

// ============================================================
// 2. RECONSTRUCCIÓN DE LLAVE Y RUTAS
// ============================================================
static string GetInternalResourcePrefix() => "2026_02_16@AJP_";

string publicPart = builder.Configuration["BinaryResource.Compiler"] ?? string.Empty;
string masterKey = GetInternalResourcePrefix() + publicPart;

string envSuffix = builder.Configuration["APP_ENVIRONMENT"]?.ToLower() ?? "dev";
string encFileName = $"appsettings.{envSuffix}.data";
string encFilePath = Path.Combine(builder.Environment.ContentRootPath, encFileName);

// ============================================================
// 3. DESENCRIPTACIÓN (MODO RESILIENTE / FALLO SILENCIOSO)
// ============================================================
// No matamos la app si falta el archivo o la llave
if (File.Exists(encFilePath) && !string.IsNullOrEmpty(publicPart))
{
    try
    {
        using var sha256 = SHA256.Create();
        byte[] keyBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(masterKey));

        // FileShare.Read es vital en IIS/SmarterASP para evitar bloqueos de archivo
        using var fs = new FileStream(encFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        byte[] iv = new byte[16];
        int read = fs.Read(iv, 0, iv.Length);

        if (read == 16)
        {
            using var aes = Aes.Create();
            aes.Key = keyBytes;
            aes.IV = iv;

            using var decryptor = aes.CreateDecryptor();
            using var cryptoStream = new CryptoStream(fs, decryptor, CryptoStreamMode.Read);
            using var sr = new StreamReader(cryptoStream);

            string decryptedJson = sr.ReadToEnd();

            // Inyectamos los secretos en memoria
            builder.Configuration.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(decryptedJson)));

            // Log interno (se verá si activas stdout en web.config)
            Console.WriteLine($"--> [BÚNKER] Carga exitosa: {envSuffix}");
        }
    }
    catch (Exception ex)
    {
        // FALLO SILENCIOSO: Solo registramos el error, pero no lanzamos excepción
        Console.WriteLine($"--> [BÚNKER ERROR]: {ex.Message}");
    }
}

// ============================================================
// 4. SERVICIOS
// ============================================================

// 1. CONFIGURACIÓN DE DATA PROTECTION (Evita el Error 400)
// 1. Configuramos la persistencia básica (Funciona en Windows y Linux)
var dataProtection = builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "App_Data", "Keys")))
    .SetApplicationName("RedAJP_Produccion");

// 2. Aplicamos la encriptación nativa SOLO si el servidor es Windows (SmarterASP)
if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    dataProtection.ProtectKeysWithDpapi(true);
}
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Login/Index";
        options.ExpireTimeSpan = TimeSpan.FromMinutes(20);
        options.SlidingExpiration = true;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.Name = "__Host-RedAJP-Secure";
    });

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddControllersWithViews();
builder.Services.AddSignalR();
builder.Services.AddScoped<ISolicitudesService, SolicitudesService>();
QuestPDF.Settings.License = LicenseType.Community;

var app = builder.Build();

// ============================================================
// 5. PIPELINE (MIDDLEWARES)
// ============================================================
app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.Use(async (context, next) =>
{
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Append("X-Frame-Options", "SAMEORIGIN");
    context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
    context.Response.Headers.Remove("X-Powered-By");
    await next();
});

app.UseHttpsRedirection();
app.UseStaticFiles();

// EL PORTERO: Filtro Geográfico
// Si este middleware usa la base de datos y la conexión falló, 
// aquí es donde verás el error al navegar.
app.UseMiddleware<FiltroGeograficoMiddleware>();

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Eventos}/{action=Index}/{id?}");

app.MapHub<RedAJP.Hubs.EventosHub>("/eventosHub");
app.Run();