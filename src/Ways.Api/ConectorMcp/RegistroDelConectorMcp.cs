using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Authentication;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Ways.Api.ConectorMcp;

/// <summary>
/// Conector MCP (experimental): servidor MCP remoto (<c>/mcp</c>) protegido por OAuth, con
/// OpenIddict como servidor de autorización dentro del mismo proceso. <c>Program.cs</c> llama a
/// estos tres métodos siempre; con <c>Mcp:Habilitado</c> apagado ninguno registra, mapea ni loguea
/// nada.
/// </summary>
public static class RegistroDelConectorMcp
{
    private const string CategoriaDeLog = "Ways.Api.ConectorMcp";

    private const string Instrucciones =
        "Servidor MCP de Ways, un sistema de gestión comercial y punto de venta. Es experimental: por " +
        "ahora solo expone la herramienta quien_soy, que informa con qué usuario y tenant de Ways " +
        "quedó autorizada la conexión.";

    private static readonly string[] EsquemasDelConector =
    [
        OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
        OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme,
        McpAuthenticationDefaults.AuthenticationScheme,
        ConstantesDeMcp.EsquemaDeToken
    ];

    public static EstadoDelConectorMcp AgregarConectorMcp(this WebApplicationBuilder builder)
    {
        // TryParse y no GetValue<bool>: un valor mal escrito no puede impedir el arranque.
        if (!bool.TryParse(builder.Configuration[$"{OpcionesDeMcp.Seccion}:{nameof(OpcionesDeMcp.Habilitado)}"], out var habilitado) ||
            !habilitado)
        {
            return EstadoDelConectorMcp.Apagado();
        }

        var seccion = builder.Configuration.GetSection(OpcionesDeMcp.Seccion);
        OpcionesDeMcp opciones;
        try
        {
            // Nunca devuelve null acá: la sección tiene al menos Habilitado.
            opciones = seccion.Get<OpcionesDeMcp>()!;
        }
        catch (InvalidOperationException error)
        {
            return EstadoDelConectorMcp.Deshabilitado($"la sección Mcp tiene un valor inválido ({error.Message}).");
        }

        var validacion = ValidacionDelConector.Validar(opciones, builder.Environment.IsDevelopment());
        if (validacion.Error is { } motivo)
        {
            return EstadoDelConectorMcp.Deshabilitado(motivo);
        }

        var urlPublica = validacion.UrlPublica;
        var recursos = UrlsDelConector.RecursosRegistrables(builder.Configuration, urlPublica);

        builder.Services.Configure<OpcionesDeMcp>(seccion);
        builder.Services.AddSingleton(new UrlsDelConector(urlPublica, recursos));

        // OpenIddict registra cada solicitud completa en nivel Information y no oculta el
        // code_verifier; se baja a Warning salvo que la configuración pida otro nivel.
        if (string.IsNullOrEmpty(builder.Configuration["Logging:LogLevel:OpenIddict"]))
        {
            builder.Logging.AddFilter("OpenIddict", LogLevel.Warning);
        }

        // Nombre único por host: el almacén InMemory de EF se comparte entre todos los hosts del
        // mismo proceso que usan el mismo nombre.
        var baseEnMemoria = $"ways-conector-mcp-{Guid.NewGuid():N}";
        builder.Services.AddDbContext<ContextoOAuthDelConector>(opcionesDeEf =>
        {
            opcionesDeEf.UseInMemoryDatabase(baseEnMemoria);
            opcionesDeEf.UseOpenIddict();
        });

        builder.Services.AddOpenIddict()
            .AddCore(core => core
                .UseEntityFrameworkCore()
                .UseDbContext<ContextoOAuthDelConector>()
                // InMemory no soporta las operaciones masivas de EF (actualizar filas sin cargarlas):
                // sin esto OpenIddict registra un warning y la revocación de la cadena de tokens no ocurre.
                .DisableBulkOperations())
            .AddServer(servidor =>
            {
                servidor
                    .SetAuthorizationEndpointUris(ConstantesDeMcp.RutaDeAutorizacion.TrimStart('/'))
                    .SetTokenEndpointUris(ConstantesDeMcp.RutaDeToken.TrimStart('/'));

                servidor.AllowAuthorizationCodeFlow().AllowRefreshTokenFlow();
                servidor.RequireProofKeyForCodeExchange();
                servidor.RegisterScopes(ConstantesDeMcp.AlcanceMcp);
                servidor.RegisterResources([.. recursos]);
                servidor.SetAccessTokenLifetime(TimeSpan.FromMinutes(opciones.MinutosDeAccessToken));

                if (opciones.SegundosDeToleranciaDeReusoDeRefresh is { } segundos)
                {
                    servidor.SetRefreshTokenReuseLeeway(TimeSpan.FromSeconds(segundos));
                }

                servidor.AddEphemeralEncryptionKey().AddEphemeralSigningKey();

                if (urlPublica is not null)
                {
                    servidor.SetIssuer(urlPublica);
                }

                // Solo S256, y "none" en la metadata porque el cliente de Claude es público (sin secreto).
                servidor.Configure(opcionesDelServidor =>
                {
                    opcionesDelServidor.CodeChallengeMethods.Remove(CodeChallengeMethods.Plain);
                    opcionesDelServidor.ClientAuthenticationMethods.Add(ClientAuthenticationMethods.None);
                });

                var aspNetCore = servidor.UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableTokenEndpointPassthrough();

                if (builder.Environment.IsDevelopment())
                {
                    aspNetCore.DisableTransportSecurityRequirement();
                }
            })
            .AddValidation(validacionDeTokens =>
            {
                validacionDeTokens.UseLocalServer();

                // Sin estas dos validaciones un access token sigue sirviendo hasta vencer aunque
                // OpenIddict haya revocado su entrada (por ejemplo, toda la cadena al detectar el
                // reuso de un refresh token) o su autorización. EnableTokenEntryValidation tiene que
                // ir después de UseLocalServer, que la fija según UseReferenceAccessTokens.
                validacionDeTokens.EnableTokenEntryValidation();
                validacionDeTokens.EnableAuthorizationEntryValidation();

                validacionDeTokens.UseAspNetCore();
            });

        builder.Services
            .AddAuthentication()
            .AddMcp(mcp =>
            {
                mcp.ForwardAuthenticate = ConstantesDeMcp.EsquemaDeToken;

                // El esquema MCP responde tanto la URL con la ruta del recurso como la raíz de
                // /.well-known/oauth-protected-resource, y las dos devuelven este documento: Claude
                // consulta primero la URL con la ruta y después la raíz.
                mcp.Events.OnResourceMetadataRequest = contexto =>
                {
                    var urls = contexto.HttpContext.RequestServices.GetRequiredService<UrlsDelConector>();
                    contexto.ResourceMetadata = new ProtectedResourceMetadata
                    {
                        Resource = urls.Recurso(contexto.HttpContext),
                        AuthorizationServers = [urls.Emisor(contexto.HttpContext)],
                        ScopesSupported = [ConstantesDeMcp.AlcanceMcp],
                        ResourceName = "Ways"
                    };
                    return Task.CompletedTask;
                };
            })
            .AddScheme<AuthenticationSchemeOptions, ManejadorDeTokenMcp>(ConstantesDeMcp.EsquemaDeToken, _ => { });

        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(ConstantesDeMcp.PoliticaDeAcceso, politica => politica
                .AddAuthenticationSchemes(McpAuthenticationDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser());

        // Solo registra los servicios: ningún endpoint de la app pasa a exigir antiforgery. La
        // página de consentimiento lo valida a mano (EndpointsDeAutorizacion).
        builder.Services.AddAntiforgery(antiforgery =>
        {
            antiforgery.Cookie.Name = "ways.mcp.antiforgery";
            antiforgery.Cookie.Path = ConstantesDeMcp.RutaDeAutorizacion;
            antiforgery.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;

            // La página manda su propio X-Frame-Options (DENY) en lugar del SAMEORIGIN de antiforgery.
            antiforgery.SuppressXFrameOptionsHeader = true;
        });

        builder.Services
            .AddMcpServer(servidorMcp =>
            {
                servidorMcp.ServerInfo = new() { Name = "ways", Title = "Ways", Version = "0.1.0-experimental" };
                servidorMcp.ServerInstructions = Instrucciones;
            })
            .WithHttpTransport(transporte => transporte.SessionMode = HttpServerSessionMode.Stateless)
            .WithTools<HerramientasDeIdentidad>();

        return EstadoDelConectorMcp.Registrado();
    }

    public static WebApplication UsarConectorMcp(this WebApplication app, EstadoDelConectorMcp estado)
    {
        if (estado.Motivo is { } motivo)
        {
            Logueador(app).LogError("El conector MCP quedó deshabilitado: {Motivo}", motivo);
        }

        if (!estado.Activo)
        {
            return app;
        }

        var urlPublica = app.Services.GetRequiredService<UrlsDelConector>().UrlPublica;
        var logueador = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger($"{CategoriaDeLog}.Diagnostico");

        app.Use(async (http, siguiente) =>
        {
            if (!estado.Activo || !DiagnosticoDelConector.EsRutaDelConector(http.Request.Path))
            {
                await siguiente(http);
                return;
            }

            // OpenIddict y el esquema MCP derivan sus URLs (endpoints, metadata, WWW-Authenticate)
            // de la request: con URL pública se fijan esquema y host para que coincidan con ella
            // aunque el proxy reescriba el Host o no mande X-Forwarded-Proto.
            if (urlPublica is not null)
            {
                http.Request.Scheme = urlPublica.Scheme;
                http.Request.Host = UrlsDelConector.HostFijado(urlPublica);
            }

            await DiagnosticoDelConector.RegistrarAsync(http, siguiente, logueador);
        });

        return app;
    }

    public static async Task MapearConectorMcpAsync(this WebApplication app, EstadoDelConectorMcp estado)
    {
        if (!estado.Activo)
        {
            return;
        }

        try
        {
            await InicializarAsync(app);
            MapearEndpoints(app);
        }
        catch (Exception error)
        {
            // Los manejadores de OpenIddict y del esquema MCP atienden todas las requests de la app:
            // se quitan sus esquemas para que el resto siga funcionando como con el flag apagado.
            var esquemas = app.Services.GetRequiredService<IAuthenticationSchemeProvider>();
            foreach (var esquema in EsquemasDelConector)
            {
                esquemas.RemoveScheme(esquema);
            }

            estado.Desactivar(error.Message);
            Logueador(app).LogError(error, "El conector MCP no pudo inicializarse y quedó deshabilitado.");
        }
    }

    private static async Task InicializarAsync(WebApplication app)
    {
        // Valida ahora las opciones de OpenIddict: si fallaran recién en la primera request, la
        // excepción saldría en cada request de la app.
        _ = app.Services.GetRequiredService<IOptionsMonitor<OpenIddictServerOptions>>().CurrentValue;
        _ = app.Services.GetRequiredService<IOptionsMonitor<OpenIddictValidationOptions>>().CurrentValue;

        await using var alcance = app.Services.CreateAsyncScope();
        var servicios = alcance.ServiceProvider;
        var opciones = servicios.GetRequiredService<IOptions<OpcionesDeMcp>>().Value;
        var urls = servicios.GetRequiredService<UrlsDelConector>();
        var aplicaciones = servicios.GetRequiredService<IOpenIddictApplicationManager>();

        // El almacén en memoria es nuevo en cada arranque (nombre único por host): el cliente todavía
        // no existe.
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = opciones.IdDeCliente,
            ClientType = ClientTypes.Public,
            ConsentType = ConsentTypes.Explicit,
            DisplayName = "Claude"
        };

        foreach (var uri in ConstantesDeMcp.UrisDeRedireccionDeClaude)
        {
            descriptor.RedirectUris.Add(new Uri(uri));
        }

        descriptor.Permissions.UnionWith(
        [
            Permissions.Endpoints.Authorization,
            Permissions.Endpoints.Token,
            Permissions.GrantTypes.AuthorizationCode,
            Permissions.GrantTypes.RefreshToken,
            Permissions.ResponseTypes.Code,
            Permissions.Prefixes.Scope + ConstantesDeMcp.AlcanceMcp,
            .. urls.RecursosRegistrados.Select(recurso => Permissions.Prefixes.Resource + recurso)
        ]);

        await aplicaciones.CreateAsync(descriptor);

        Logueador(app).LogInformation(
            "Conector MCP (experimental) habilitado: cliente {Cliente}, recursos registrados {Recursos}, " +
            "URL pública {UrlPublica}, mails habilitados {CantidadDeMails}.",
            opciones.IdDeCliente,
            string.Join(", ", urls.RecursosRegistrados),
            urls.UrlPublica?.ToString() ?? "(derivada de cada request)",
            new ListaDeMailsHabilitados(opciones.MailsHabilitados).Cantidad);
    }

    private static void MapearEndpoints(WebApplication app)
    {
        // Sin la política de ruteo por Content-Type (ver Program.cs) el POST llega al SDK con cualquier
        // tipo de contenido, y el SDK lee el cuerpo como JSON con una excepción que saldría como 500. Se
        // rechaza antes, como lo hace el binding de los demás endpoints: 415 con solicitud_invalida.
        app.MapMcp(ConstantesDeMcp.RutaMcp)
            .RequireAuthorization(ConstantesDeMcp.PoliticaDeAcceso)
            .AddEndpointFilter((contexto, siguiente) => contexto.HttpContext.Request.HasJsonContentType()
                ? siguiente(contexto)
                : throw new BadHttpRequestException(
                    "El cuerpo de /mcp no es JSON.", StatusCodes.Status415UnsupportedMediaType));

        // En modo sin estado el SDK solo mapea POST: sin esto, GET y DELETE caerían en el
        // fallback de la SPA.
        app.MapMethods(ConstantesDeMcp.RutaMcp, [HttpMethods.Get, HttpMethods.Delete], (HttpContext http) =>
            {
                http.Response.Headers.Allow = HttpMethods.Post;
                return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
            })
            .RequireAuthorization(ConstantesDeMcp.PoliticaDeAcceso)
            .ExcludeFromDescription();

        app.MapearEndpointsDeAutorizacion();

        // Los documentos conocidos los responden OpenIddict y el esquema MCP antes de llegar a los
        // endpoints; cualquier otro /.well-known/* da 404 en lugar de caer en el fallback de la SPA.
        app.MapGet("/.well-known/{**resto}", () => Results.NotFound())
            .AllowAnonymous()
            .ExcludeFromDescription();
    }

    private static ILogger Logueador(WebApplication app) =>
        app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(CategoriaDeLog);
}
