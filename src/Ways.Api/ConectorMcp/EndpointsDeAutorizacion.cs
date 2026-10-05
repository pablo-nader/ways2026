using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
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
    /// ante una contraseña incorrecta: un mail fuera de la lista o un usuario de plataforma no se
    /// distinguen de una contraseña mal escrita.</summary>
    public const string MensajeDeCredencialesInvalidas = "Mail o contraseña incorrectos.";

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

        var formulario = http.Request.HasFormContentType ? await http.Request.ReadFormAsync(ct) : null;
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
