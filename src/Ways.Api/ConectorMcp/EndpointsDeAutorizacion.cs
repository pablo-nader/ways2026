using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using Ways.Api.Seguridad;
using Ways.Application.Abstracciones;
using Ways.Application.Usuarios;
using Ways.Domain.Common;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Ways.Api.ConectorMcp;

/// <summary>
/// Endpoints en modo passthrough de OpenIddict: OpenIddict ya validó la solicitud (cliente,
/// redirect_uri, PKCE, alcances, recursos) antes de que corran estos handlers.
/// </summary>
public static class EndpointsDeAutorizacion
{
    private const string CampoAccion = "accion";
    private const string CampoMail = "mail";

    // Se llama "password" a propósito: OpenIddict reemplaza ese parámetro por [redacted] al
    // registrar la solicitud en sus logs; con cualquier otro nombre quedaría en texto plano.
    private const string CampoPassword = "password";

    /// <summary>El mismo texto que devuelve <see cref="ServicioDeAutenticacion.IniciarSesionAsync"/>
    /// ante una contraseña incorrecta. Un mail fuera de la lista y un usuario de plataforma reciben la
    /// página con este texto y el mismo estado (200) que una contraseña mal escrita. El tiempo de
    /// respuesta sí difiere para el mail fuera de la lista, que se rechaza antes de consultar la base.</summary>
    public const string MensajeDeCredencialesInvalidas = "Mail o contraseña incorrectos.";

    public const string DescripcionDeFormularioIlegible = "El formulario de la solicitud no se puede leer.";

    /// <summary>Corre en la extracción de OpenIddict de <c>/connect/authorize</c> y <c>/connect/token</c>,
    /// antes de que OpenIddict lea el formulario, y solo para lo que leería él: un POST cuyo Content-Type
    /// empieza con <c>application/x-www-form-urlencoded</c>. Rechaza como <c>invalid_request</c>, en lugar
    /// del 500 que daría la excepción al salir de OpenIddict, un Content-Type con ese prefijo que el
    /// framework no reconoce como formulario (por ejemplo <c>application/x-www-form-urlencoded-x</c>, o con
    /// un parámetro entre comillas sin cerrar) y un formulario que el framework no puede leer (por ejemplo,
    /// una clave más larga que el límite de <c>FormOptions</c>).</summary>
    public static async ValueTask RechazarFormularioIlegibleAsync(OpenIddictServerEvents.BaseValidatingContext contexto)
    {
        // En este servidor toda solicitud de OpenIddict llega por ASP.NET Core.
        var request = contexto.Transaction.GetHttpRequest()!;
        if (!HttpMethods.IsPost(request.Method) ||
            request.ContentType?.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) is not true)
        {
            return;
        }

        // OpenIddict solo mira el prefijo; con un Content-Type que no es un formulario, ReadFormAsync tira
        // InvalidOperationException.
        if (!request.HasFormContentType)
        {
            contexto.Reject(Errors.InvalidRequest, DescripcionDeFormularioIlegible);
            return;
        }

        try
        {
            await request.ReadFormAsync(request.HttpContext.RequestAborted);
        }
        catch (InvalidDataException)
        {
            contexto.Reject(Errors.InvalidRequest, DescripcionDeFormularioIlegible);
        }
    }

    public static IEndpointRouteBuilder MapearEndpointsDeAutorizacion(this IEndpointRouteBuilder app)
    {
        app.MapMethods(ConstantesDeMcp.RutaDeAutorizacion, [HttpMethods.Get, HttpMethods.Post], AutorizarAsync)
            .AllowAnonymous()
            .ExcludeFromDescription();

        app.MapPost(ConstantesDeMcp.RutaDeToken, CanjearAsync)
            .AllowAnonymous()
            .ExcludeFromDescription();

        return app;
    }

    private static async Task<IResult> AutorizarAsync(
        HttpContext http,
        IOpenIddictApplicationManager aplicaciones,
        ServicioDeAutenticacion autenticacion,
        TenantActualDeSesion tenantActual,
        IAntiforgery antiforgery,
        IOptionsMonitor<OpcionesDeMcp> opciones,
        UrlsDelConector urls,
        CancellationToken ct)
    {
        // En modo passthrough OpenIddict ya extrajo y validó la solicitud, cliente incluido, y el
        // cliente sembrado (RegistroDelConectorMcp) siempre tiene nombre.
        var solicitud = http.GetOpenIddictServerRequest()!;
        var cliente = (await aplicaciones.FindByClientIdAsync(solicitud.ClientId!, ct))!;
        var nombreDelCliente = (await aplicaciones.GetDisplayNameAsync(cliente, ct))!;

        // En un POST OpenIddict ya leyó el formulario (solo acepta application/x-www-form-urlencoded); un
        // GET trae la solicitud en la query y su cuerpo no se lee.
        var formulario = HttpMethods.IsPost(http.Request.Method) ? await http.Request.ReadFormAsync(ct) : null;
        var accion = formulario?[CampoAccion].ToString();

        var pedidos = solicitud.GetResources();
        string[] recursos = pedidos.IsDefaultOrEmpty ? [urls.Recurso(http)] : [.. pedidos];

        // El alcance del conector se concede siempre: es el único que tiene este servidor y, si el
        // cliente no lo pide, /mcp rechazaría el token aunque el usuario haya aprobado.
        string[] alcances = [.. solicitud.GetScopes().Union([ConstantesDeMcp.AlcanceMcp], StringComparer.Ordinal)];

        var pagina = new PaginaDeConsentimiento(
            nombreDelCliente,
            alcances,
            recursos,
            http.Request.PathBase + ConstantesDeMcp.RutaDeAutorizacion,
            formulario is not null ? formulario : http.Request.Query,
            [CampoAccion, CampoMail, CampoPassword]);

        if (string.IsNullOrEmpty(accion))
        {
            return pagina.ComoResultado(http, antiforgery, error: null, mail: null);
        }

        if (!await antiforgery.IsRequestValidAsync(http))
        {
            DiagnosticoDelConector.RegistrarMotivo(http, "aprobación rechazada: token antiforgery inválido o ausente");
            return pagina.ComoResultado(
                http, antiforgery, "La página venció. Vuelva a intentarlo.", mail: null, StatusCodes.Status400BadRequest);
        }

        if (accion == "denegar")
        {
            DiagnosticoDelConector.RegistrarMotivo(http, "el usuario denegó el acceso");
            return Results.Forbid(authenticationSchemes: [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
        }

        if (accion != "aprobar")
        {
            DiagnosticoDelConector.RegistrarMotivo(http, "acción de formulario desconocida");
            return pagina.ComoResultado(http, antiforgery, error: null, mail: null);
        }

        var mail = formulario![CampoMail].ToString();

        // Antes de llegar al servicio de autenticación: un mail fuera de la lista no suma intentos
        // fallidos ni puede bloquear la cuenta.
        if (!new ListaDeMailsHabilitados(opciones.CurrentValue.MailsHabilitados).Incluye(mail))
        {
            DiagnosticoDelConector.RegistrarMotivo(http, "aprobación rechazada: mail fuera de Mcp:MailsHabilitados");
            return pagina.ComoResultado(http, antiforgery, MensajeDeCredencialesInvalidas, mail);
        }

        // Mismo contrato que POST /api/auth/login: modo Login antes de tocar `usuarios`.
        tenantActual.Establecer(ModoDeAcceso.Login, idTenant: null);

        UsuarioAutenticado usuario;
        try
        {
            usuario = await autenticacion.IniciarSesionAsync(
                new SolicitudDeLogin(mail, formulario[CampoPassword].ToString()), ct);
        }
        catch (ErrorDominio error)
        {
            DiagnosticoDelConector.RegistrarMotivo(http, $"aprobación rechazada: {error.Codigo}");
            return pagina.ComoResultado(http, antiforgery, error.Message, mail);
        }

        // Un usuario de plataforma (sin tenant) nunca autoriza el conector, aunque esté en la lista.
        if (usuario.IdTenant is null)
        {
            DiagnosticoDelConector.RegistrarMotivo(http, "aprobación rechazada: usuario de plataforma");
            return pagina.ComoResultado(http, antiforgery, MensajeDeCredencialesInvalidas, mail);
        }

        var identidad = IdentidadDelConector.ParaOpenIddict(usuario, alcances, recursos);

        return Results.SignIn(
            new ClaimsPrincipal(identidad),
            properties: null,
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static async Task<IResult> CanjearAsync(
        HttpContext http, WaysDbContext db, IOptionsMonitor<OpcionesDeMcp> opciones, CancellationToken ct)
    {
        // En modo passthrough OpenIddict ya rechazó los tipos de concesión no habilitados y validó el
        // código o el refresh token, así que el principal siempre está. El parámetro resource del
        // canje no cambia la audiencia: el token sale con los recursos que quedaron al autorizar.
        var principal = (await http.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)).Principal!;

        var deWays = IdentidadDelConector.ParaWays(principal, IdentidadDelConector.TipoDeAutenticacion);
        if (!await ValidadorDeSesion.EsVigenteAsync(deWays, http, db))
        {
            return Rechazar(Errors.InvalidGrant, "La cuenta ya no está habilitada.");
        }

        // La lista se vuelve a consultar en cada canje de código y en cada refresh. El chequeo de
        // usuario de plataforma no se repite: sin tenant nunca se obtiene un código (AutorizarAsync).
        var idUsuario = int.Parse(principal.GetClaim(Claims.Subject)!, CultureInfo.InvariantCulture);
        var mail = await db.Usuarios
            .AsNoTracking()
            .Where(u => u.Id == idUsuario)
            .Select(u => u.Mail)
            .FirstAsync(ct);

        if (!new ListaDeMailsHabilitados(opciones.CurrentValue.MailsHabilitados).Incluye(mail))
        {
            return Rechazar(Errors.InvalidGrant, "La cuenta no está habilitada para el conector.");
        }

        principal.SetDestinations(static _ => [Destinations.AccessToken]);

        return Results.SignIn(principal, properties: null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static IResult Rechazar(string error, string descripcion) =>
        Results.Forbid(
            new AuthenticationProperties(new Dictionary<string, string?>
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
                [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = descripcion
            }),
            [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
}
