using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Web;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.MercadoPago;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.MercadoPago;

namespace Ways.IntegrationTests;

/// <summary>
/// Arma el host de pruebas de la conexión con Mercado Pago: configuración y claves maestras en memoria, el endpoint
/// de tokens reemplazado por un manejador falso y un tenant provisionado con su Admin, su Supervisor, su Vendedor y una
/// cuenta de tesorería.
/// </summary>
public sealed class ApoyoDeMercadoPago(WaysApiFixture fixture)
{
    public const string ClientId = "5551234567";
    public const string ClientSecret = "secreto-de-la-aplicacion-mp-de-prueba";
    public const string RedirectUri = "https://ways.test/api/integraciones/mercadopago/callback";
    public const string UrlAutorizacion = "https://auth.mp.test/authorization";
    public const string RutaCallback = "/api/integraciones/mercadopago/callback";
    public const string AccessToken = "APP_USR-7654321-103112-acceso-secreto-de-prueba";
    public const string RefreshToken = "TG-abcdef0123456789-refresco-secreto-de-prueba";

    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordOtroRol = "otro-rol-password-larga";

    private static long _siguienteIdUsuario = 700_000_000;

    /// <summary>Cada manejador usa su propia cuenta de Mercado Pago: el índice único es global y la base se comparte en la clase.</summary>
    public static long NuevoIdUsuario() => Interlocked.Increment(ref _siguienteIdUsuario);

    public static readonly byte[] ClaveMaestra = Convert.FromBase64String("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=");

    public static IConfiguration ConfiguracionDeClaves() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ways:MercadoPago:ClaveMaestraActual"] = "t1",
            ["Ways:MercadoPago:ClavesMaestras:t1"] = Convert.ToBase64String(ClaveMaestra)
        }).Build();

    /// <summary>Endpoint de tokens de Mercado Pago falso: registra cada pedido y responde lo que diga <see cref="Responder"/>.</summary>
    public sealed class ManejadorDeMercadoPago : HttpMessageHandler
    {
        private readonly object _candado = new();
        private readonly List<string> _cuerpos = [];

        public long IdUsuario { get; } = NuevoIdUsuario();

        public ManejadorDeMercadoPago() => Responder = _ => Exitoso(AccessToken, RefreshToken, IdUsuario);

        public Func<string, HttpResponseMessage> Responder { get; set; }

        /// <summary>Se ejecuta antes de responder; permite sincronizar dos pedidos concurrentes.</summary>
        public Func<Task>? AntesDeResponder { get; set; }

        public IReadOnlyList<string> Cuerpos
        {
            get
            {
                lock (_candado)
                {
                    return [.. _cuerpos];
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage pedido, CancellationToken ct)
        {
            var cuerpo = pedido.Content is null ? string.Empty : await pedido.Content.ReadAsStringAsync(ct);
            lock (_candado)
            {
                _cuerpos.Add($"{pedido.Method} {pedido.RequestUri} {cuerpo}");
            }

            if (AntesDeResponder is { } antes)
            {
                await antes();
            }

            return Responder(cuerpo);
        }
    }

    public static HttpResponseMessage Exitoso(string acceso, string renovacion, long idUsuario) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    access_token = acceso,
                    refresh_token = renovacion,
                    expires_in = 15_552_000,
                    user_id = idUsuario,
                    token_type = "Bearer"
                }),
                Encoding.UTF8,
                "application/json")
        };

    public sealed class RelojControlable(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; set; } = ahora;
    }

    public sealed record Contexto(
        WebApplicationFactory<Program> Host,
        int IdTenant,
        int IdEmpresa,
        int IdUsuarioAdmin,
        int IdCuenta,
        HttpClient Admin,
        HttpClient Supervisor,
        HttpClient Vendedor,
        ManejadorDeMercadoPago MercadoPago)
    {
        public string Ruta => $"/api/empresas/{IdEmpresa}/mercadopago/conexion";
    }

    public WebApplicationFactory<Program> CrearHost(
        ManejadorDeMercadoPago mercadoPago, bool configurado = true, IRelojDelSistema? reloj = null) =>
        fixture.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            if (configurado)
            {
                services.Configure<OpcionesDeMercadoPago>(o =>
                {
                    o.ClientId = ClientId;
                    o.ClientSecret = ClientSecret;
                    o.RedirectUri = RedirectUri;
                    o.UrlAutorizacion = UrlAutorizacion;
                });
                services.AddSingleton<IAlmacenDeTokensMercadoPago>(new CifradoDeTokensMercadoPago(ConfiguracionDeClaves()));
            }

            if (reloj is not null)
            {
                services.AddSingleton<IRelojDelSistema>(reloj);
            }

            services.AddHttpClient<IClienteOAuthMercadoPago, ClienteOAuthMercadoPago>(http => http.BaseAddress = new Uri("https://api.mp.test/"))
                .ConfigurePrimaryHttpMessageHandler(() => mercadoPago);
        }));

    public async Task<Contexto> PrepararAsync(
        string nombre, bool configurado = true, IRelojDelSistema? reloj = null, ManejadorDeMercadoPago? mercadoPago = null)
    {
        mercadoPago ??= new ManejadorDeMercadoPago();
        var host = CrearHost(mercadoPago, configurado, reloj);

        using var root = host.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var unico = $"{nombre}-{Guid.NewGuid().ToString("N")[..6]}".ToLowerInvariant();
        var mailAdmin = $"{unico}@ways.test";
        var alta = await root.PostAsJsonAsync(
            "/api/plataforma/tenants",
            new SolicitudDeAprovisionamiento(unico, $"{unico} SA", "Local 1", mailAdmin, ModoPuntoVenta.Web));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var resultado = (await alta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = host.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        var supervisor = await CrearYLoguearAsync(host, admin, unico, "supervisor", RolConocido.Supervisor);
        var vendedor = await CrearYLoguearAsync(host, admin, unico, "vendedor", RolConocido.Vendedor);

        var idCuenta = await CrearCuentaAsync(admin, resultado.IdEmpresa, "Mercado Pago");

        return new Contexto(
            host, resultado.IdTenant, resultado.IdEmpresa, resultado.IdUsuarioAdmin, idCuenta, admin, supervisor, vendedor, mercadoPago);
    }

    private static async Task<HttpClient> CrearYLoguearAsync(
        WebApplicationFactory<Program> host, HttpClient admin, string unico, string sufijo, RolConocido rol)
    {
        var corto = Guid.NewGuid().ToString("N")[..8];
        var mail = $"{unico}-{sufijo}@ways.test";
        var alta = await admin.PostAsJsonAsync(
            "/api/usuarios", new CrearUsuario($"{sufijo}-{corto}", mail, (int)rol, PasswordOtroRol));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        var cliente = host.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, PasswordOtroRol));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    public static async Task<int> CrearCuentaAsync(HttpClient admin, int idEmpresa, string nombre, bool activo = true)
    {
        var respuesta = await admin.PostAsJsonAsync(
            "/api/cuentas-tesoreria", new AltaDeCuentaTesoreria(idEmpresa, nombre, 0m, activo));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        return (await respuesta.Content.ReadFromJsonAsync<CuentaTesoreriaListado>())!.Id;
    }

    /// <summary>Segunda empresa del mismo tenant, con su cuenta de tesorería.</summary>
    public async Task<(int IdEmpresa, int IdCuenta)> CrearOtraEmpresaConCuentaAsync(Contexto ctx, string nombre)
    {
        int idEmpresa;
        await using (var conexion = await fixture.AbrirConexionCrudaAsync("plataforma", null))
        {
            await using var comando = conexion.CreateCommand();
            comando.CommandText =
                "INSERT INTO empresas (id_tenant, razon_social, created_at, updated_at) VALUES ($1, $2, now(), now()) " +
                "RETURNING id_empresa";
            comando.Parameters.Add(new NpgsqlParameter { Value = ctx.IdTenant });
            comando.Parameters.Add(new NpgsqlParameter { Value = nombre });
            idEmpresa = (int)(await comando.ExecuteScalarAsync())!;
        }

        return (idEmpresa, await CrearCuentaAsync(ctx.Admin, idEmpresa, "Mercado Pago"));
    }

    public static HttpClient ClienteAnonimoSinRedirecciones(WebApplicationFactory<Program> host) =>
        host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    public static async Task<string?> CodigoAsync(HttpResponseMessage respuesta)
    {
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        return problema.GetProperty("codigo").GetString();
    }

    /// <summary>Inicia la conexión como Admin y devuelve la URL de autorización junto con su <c>state</c>.</summary>
    public static async Task<(string Url, string Estado)> IniciarAsync(Contexto ctx, int? idCuenta = null)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            $"{ctx.Ruta}/inicio", new SolicitudDeInicioDeConexion(idCuenta ?? ctx.IdCuenta));
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var url = (await respuesta.Content.ReadFromJsonAsync<InicioDeConexion>())!.UrlAutorizacion;
        return (url, HttpUtility.ParseQueryString(new Uri(url).Query)["state"]!);
    }

    public static Task<HttpResponseMessage> LlamarAlCallbackAsync(
        HttpClient anonimo, string estado, string? codigo = "TG-codigo-de-autorizacion", string? error = null)
    {
        var consulta = $"state={Uri.EscapeDataString(estado)}";
        if (codigo is not null)
        {
            consulta += $"&code={Uri.EscapeDataString(codigo)}";
        }

        if (error is not null)
        {
            consulta += $"&error={Uri.EscapeDataString(error)}";
        }

        return anonimo.GetAsync($"{RutaCallback}?{consulta}");
    }

    public static string Ubicacion(HttpResponseMessage respuesta)
    {
        Assert.Equal(HttpStatusCode.Redirect, respuesta.StatusCode);
        return respuesta.Headers.Location!.ToString();
    }

    public sealed record FilaDeConexion(
        int Id,
        int IdTenant,
        int IdEmpresa,
        int IdCuenta,
        long IdUsuarioMercadoPago,
        string Estado,
        byte[]? AccessCifrado,
        byte[]? AccessNonce,
        byte[]? AccessTag,
        byte[]? RefreshCifrado,
        byte[]? RefreshNonce,
        byte[]? RefreshTag,
        string IdClaveMaestra,
        DateTimeOffset AccessExpiraEn,
        int IdUsuarioConecto,
        DateTimeOffset FechaConexion,
        DateTimeOffset? FechaDesconexion,
        DateTimeOffset? FechaUltimaRenovacion,
        DateTimeOffset? FechaRequiereReconexion);

    public async Task<List<FilaDeConexion>> FilasAsync(int? idEmpresa = null)
    {
        var filas = new List<FilaDeConexion>();
        await using var conexion = await fixture.AbrirConexionCrudaAsync("plataforma", null);
        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "SELECT id_conexion_mercadopago, id_tenant, id_empresa, id_cuenta_tesoreria, id_usuario_mercadopago, estado, " +
            "access_token_cifrado, access_token_nonce, access_token_tag, refresh_token_cifrado, refresh_token_nonce, " +
            "refresh_token_tag, id_clave_maestra, access_token_expira_en, id_usuario_conecto, fecha_conexion, fecha_desconexion, " +
            "fecha_ultima_renovacion, fecha_requiere_reconexion " +
            "FROM conexiones_mercadopago WHERE ($1::int IS NULL OR id_empresa = $1) ORDER BY id_conexion_mercadopago";
        comando.Parameters.Add(new NpgsqlParameter<int?> { TypedValue = idEmpresa });

        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync())
        {
            filas.Add(new FilaDeConexion(
                lector.GetInt32(0), lector.GetInt32(1), lector.GetInt32(2), lector.GetInt32(3), lector.GetInt64(4),
                lector.GetString(5),
                lector.IsDBNull(6) ? null : (byte[])lector[6],
                lector.IsDBNull(7) ? null : (byte[])lector[7],
                lector.IsDBNull(8) ? null : (byte[])lector[8],
                lector.IsDBNull(9) ? null : (byte[])lector[9],
                lector.IsDBNull(10) ? null : (byte[])lector[10],
                lector.IsDBNull(11) ? null : (byte[])lector[11],
                lector.GetString(12),
                lector.GetFieldValue<DateTimeOffset>(13),
                lector.GetInt32(14),
                lector.GetFieldValue<DateTimeOffset>(15),
                lector.IsDBNull(16) ? null : lector.GetFieldValue<DateTimeOffset>(16),
                lector.IsDBNull(17) ? null : lector.GetFieldValue<DateTimeOffset>(17),
                lector.IsDBNull(18) ? null : lector.GetFieldValue<DateTimeOffset>(18)));
        }

        return filas;
    }

    /// <summary>Inserta a mano una conexión (por defecto, activa y con material de cifrado válido) bajo el modo plataforma.
    /// Cualquier columna se puede pisar para provocar una violación de constraint.</summary>
    public async Task<int> InsertarAsync(
        int idTenant,
        int idEmpresa,
        int idCuenta,
        long idUsuarioMercadoPago,
        int idUsuarioConecto,
        string estado = "activa",
        byte[]? accessCifrado = null,
        byte[]? accessNonce = null,
        byte[]? accessTag = null,
        byte[]? refreshCifrado = null,
        byte[]? refreshNonce = null,
        byte[]? refreshTag = null,
        DateTimeOffset? fechaDesconexion = null,
        bool sinMaterial = false,
        DateTimeOffset? fechaRequiereReconexion = null)
    {
        await using var conexion = await fixture.AbrirConexionCrudaAsync("plataforma", null);
        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "INSERT INTO conexiones_mercadopago (id_tenant, id_empresa, id_cuenta_tesoreria, id_usuario_mercadopago, " +
            "access_token_cifrado, access_token_nonce, access_token_tag, refresh_token_cifrado, refresh_token_nonce, " +
            "refresh_token_tag, id_clave_maestra, access_token_expira_en, estado, id_usuario_conecto, fecha_conexion, " +
            "fecha_desconexion, fecha_requiere_reconexion, created_at, updated_at) " +
            "VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, 't1', now(), $11, $12, now(), $13, $14, now(), now()) " +
            "RETURNING id_conexion_mercadopago";

        byte[]? Material(byte[]? valor, int largo) => sinMaterial ? null : valor ?? new byte[largo];

        comando.Parameters.Add(new NpgsqlParameter { Value = idTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = idEmpresa });
        comando.Parameters.Add(new NpgsqlParameter { Value = idCuenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = idUsuarioMercadoPago });
        comando.Parameters.Add(new NpgsqlParameter<byte[]?> { TypedValue = Material(accessCifrado, 24) });
        comando.Parameters.Add(new NpgsqlParameter<byte[]?> { TypedValue = Material(accessNonce, 12) });
        comando.Parameters.Add(new NpgsqlParameter<byte[]?> { TypedValue = Material(accessTag, 16) });
        comando.Parameters.Add(new NpgsqlParameter<byte[]?> { TypedValue = Material(refreshCifrado, 24) });
        comando.Parameters.Add(new NpgsqlParameter<byte[]?> { TypedValue = Material(refreshNonce, 12) });
        comando.Parameters.Add(new NpgsqlParameter<byte[]?> { TypedValue = Material(refreshTag, 16) });
        comando.Parameters.Add(new NpgsqlParameter { Value = estado });
        comando.Parameters.Add(new NpgsqlParameter { Value = idUsuarioConecto });
        comando.Parameters.Add(new NpgsqlParameter<DateTimeOffset?> { TypedValue = fechaDesconexion });
        comando.Parameters.Add(new NpgsqlParameter<DateTimeOffset?> { TypedValue = fechaRequiereReconexion });

        return (int)(await comando.ExecuteScalarAsync())!;
    }
}
