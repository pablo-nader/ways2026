using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ways.Api.Endpoints;
using Ways.Api.Seguridad;
using Ways.Application;
using Ways.Application.Abstracciones;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

// El content root se fija al directorio del ensamblado en vez de heredarlo del working
// directory. Algunos paneles arrancan el contenedor con otro cwd, y ahí wwwroot deja de
// resolverse y el front no se sirve.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

// --- Capas ---
builder.Services.AgregarApplication();
builder.Services.AgregarInfrastructure(builder.Configuration);

// --- Contexto del usuario autenticado ---
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IContextoDeUsuario, ContextoDeUsuarioHttp>();

// --- Detrás de un proxy inverso (EasyPanel, Traefik, nginx) ---
// El proxy termina el TLS y habla HTTP con el contenedor. Sin esto la app cree que
// la conexión es insegura y nunca marca la cookie de sesión como Secure.
// Se vacían las redes y proxies conocidos porque en Docker la IP del proxy es dinámica;
// es seguro mientras el contenedor solo sea alcanzable a través del proxy.
builder.Services.Configure<ForwardedHeadersOptions>(opciones =>
{
    opciones.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    opciones.KnownIPNetworks.Clear();
    opciones.KnownProxies.Clear();
});

// --- Autenticación por cookie ---
// La sesión vive mientras haya actividad: expiración deslizante de 1 hora.
// Cada request dentro de la ventana renueva la cookie; una hora de inactividad la vence.
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(opciones =>
    {
        opciones.Cookie.Name = "ways.sesion";
        opciones.Cookie.HttpOnly = true;
        opciones.Cookie.SameSite = SameSiteMode.Lax;
        // SameAsRequest para que también funcione detrás de un proxy que no termina TLS.
        opciones.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        opciones.Cookie.IsEssential = true;

        opciones.ExpireTimeSpan = TimeSpan.FromHours(1);
        opciones.SlidingExpiration = true;

        // Es una API: nunca redirige a una página de login, devuelve el código y listo.
        opciones.Events.OnRedirectToLogin = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        opciones.Events.OnRedirectToAccessDenied = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };

        // Una cuenta bloqueada, inactiva o dada de baja pierde la sesión en la request
        // siguiente, sin esperar a que venza la cookie.
        opciones.Events.OnValidatePrincipal = async ctx =>
        {
            var claim = ctx.Principal?.FindFirst(ClaimTypes.NameIdentifier);
            if (claim is null || !int.TryParse(claim.Value, out var usuarioId))
            {
                ctx.RejectPrincipal();
                return;
            }

            var db = ctx.HttpContext.RequestServices.GetRequiredService<WaysDbContext>();

            // El modo/tenant de la sesión se resuelve ANTES de tocar `usuarios` a propósito
            // (slice 2): el filtro de tenant de `Usuario` (ADR-1) falla cerrado en modo
            // `Ninguno`, así que revisar la cuenta propia con el contexto todavía sin
            // resolver la dejaría siempre invisible, para cualquier cuenta de tenant.
            // Resolver el modo primero, a partir de los claims ya decodificados de la
            // cookie, evita el problema de origen y de paso deja el chequeo de vigencia
            // scopeado por tenant como una capa más (ADR-8).
            if (!await ResolverModoDeLaSesionAsync(ctx, db))
            {
                return;
            }

            var vigente = await db.Usuarios
                .AsNoTracking()
                .AnyAsync(u => u.Id == usuarioId && u.Estado == EstadoUsuario.Activo);

            if (!vigente)
            {
                ctx.RejectPrincipal();
                await ctx.HttpContext.SignOutAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme);
            }
        };
    });

// No hay zonas públicas: todo pide sesión salvo lo marcado con AllowAnonymous.
builder.Services
    .AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
    .AgregarPoliticasWays();

// Los enums viajan por JSON como texto ("Activo"), no como el ordinal.
// Un número obliga al front a conocer el orden de declaración del enum de C#,
// que es exactamente el problema de los `tipo` numéricos del sistema viejo.
builder.Services.ConfigureHttpJsonOptions(opciones =>
{
    opciones.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ManejadorDeErrores>();
builder.Services.AddOpenApi();

var app = builder.Build();

// Tiene que ir primero: todo lo que sigue depende de saber el esquema real.
app.UseForwardedHeaders();

app.UseExceptionHandler();

// --- Migraciones y semilla ---
await using (var alcance = app.Services.CreateAsyncScope())
{
    var inicializador = alcance.ServiceProvider.GetRequiredService<InicializadorDeBaseDeDatos>();
    var semilla = alcance.ServiceProvider.GetRequiredService<IOptions<SemillaRoot>>().Value;

    await inicializador.EjecutarAsync(semilla);
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.MapearSalud();
app.MapearAuth();
app.MapearUsuarios();
app.MapearCatalogos();
app.MapearParametros();
app.MapearAprovisionamiento();
app.MapearOrganizacion();
app.MapearClientes();
app.MapearProveedores();
app.MapearArticulos();

// Cualquier ruta que no sea /api la resuelve el router de React.
// Una /api/... inexistente tiene que dar 404, no devolver el index.html.
app.MapFallback(async contexto =>
{
    if (contexto.Request.Path.StartsWithSegments("/api"))
    {
        contexto.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    var entorno = contexto.RequestServices.GetRequiredService<IWebHostEnvironment>();
    var raiz = string.IsNullOrEmpty(entorno.WebRootPath)
        ? Path.Combine(AppContext.BaseDirectory, "wwwroot")
        : entorno.WebRootPath;
    var indice = Path.Combine(raiz, "index.html");

    if (!File.Exists(indice))
    {
        contexto.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    contexto.Response.ContentType = "text/html; charset=utf-8";
    await contexto.Response.SendFileAsync(indice);
}).AllowAnonymous();

app.Run();

// --- Resolución del modo/tenant de la sesión (ADR-2) ---
// Vive acá y no en un método de instancia porque necesita los mismos servicios de
// request que OnValidatePrincipal ya tiene resueltos (db, HttpContext). Corre ANTES que
// cualquier lectura de `usuarios` — ver el comentario en OnValidatePrincipal.
//
// Devuelve `false` cuando ya rechazó la sesión (tenant inexistente/suspendido/de baja): el
// llamador no debe seguir revisando la cuenta.
static async Task<bool> ResolverModoDeLaSesionAsync(CookieValidatePrincipalContext ctx, WaysDbContext db)
{
    var tenantActual = ctx.HttpContext.RequestServices.GetRequiredService<TenantActualDeSesion>();

    var esRoot =
        int.TryParse(ctx.Principal?.FindFirstValue(ClaimsWays.RolId), out var rolId)
        && (RolConocido)rolId == RolConocido.Root;

    if (esRoot)
    {
        tenantActual.Establecer(ModoDeAcceso.Plataforma, idTenant: null);
        return true;
    }

    // El claim ways:id_tenant está ausente para staff de plataforma (ya cubierto arriba,
    // esRoot) y para cualquier cuenta creada antes del backfill de la migración 2
    // (gate #2 pendiente). Sin claim el contexto queda "Ninguno": no ve nada scopeado.
    if (!int.TryParse(ctx.Principal?.FindFirstValue(ClaimsWays.IdTenant), out var idTenant))
    {
        tenantActual.Establecer(ModoDeAcceso.Ninguno, idTenant: null);
        return true;
    }

    tenantActual.Establecer(ModoDeAcceso.Tenant, idTenant);

    // IgnoreQueryFilters(["BajaLogica"]) para distinguir "el tenant no existe" (bug) de
    // "está dado de baja" (estado de negocio) — las dos rechazan la sesión igual, pero
    // sin ignorar la baja lógica un tenant borrado devolvería null y se confundiría con
    // el default(EstadoTenant) = Activo si se seleccionara solo el campo.
    var tenant = await db.Tenants
        .AsNoTracking()
        .IgnoreQueryFilters(["BajaLogica"])
        .FirstOrDefaultAsync(t => t.Id == idTenant);

    if (tenant is null || tenant.Estado != EstadoTenant.Activo || tenant.DeletedAt is not null)
    {
        ctx.RejectPrincipal();
        await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return false;
    }

    return true;
}

/// <summary>Hace público el <c>Program</c> implícito de top-level statements para que
/// <c>WebApplicationFactory&lt;Program&gt;</c> lo vea desde <c>Ways.IntegrationTests</c>.</summary>
public partial class Program;
