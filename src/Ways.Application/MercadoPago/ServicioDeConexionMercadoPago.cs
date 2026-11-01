using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Ways.Application.Abstracciones;
using Ways.Domain.Common;
using Ways.Domain.MercadoPago;

namespace Ways.Application.MercadoPago;

/// <summary>
/// Conexión de una empresa con Mercado Pago por OAuth. Todo lo usa un Admin autenticado (la política se aplica en la capa de API).
/// El cierre del circuito (<see cref="CompletarEnSesionAsync"/>) exige que el <c>state</c> lo haya armado la misma sesión (empresa y
/// usuario, que ata el tenant) que ahora lo completa: el callback anónimo de Mercado Pago no canjea nada.
///
/// Los dos índices únicos parciales (una activa por empresa, una activa por cuenta de Mercado Pago en todo el sistema) son el
/// contrato; los chequeos previos son cortesía de UX y evitan quemar el código de autorización cuando se sabe de antemano que
/// la conexión no va a entrar. La traducción de un 23505 que gane la carrera la hace la capa de API.
///
/// Sin estrategia de reintento explícita: el alta es un único <c>SaveChangesAsync</c> y la desconexión un único
/// <c>UPDATE</c> guardado por estado, sin entidades rastreadas que un reintento pueda arrastrar.
/// </summary>
public class ServicioDeConexionMercadoPago(
    IWaysDbContext db,
    [FromKeyedServices(ClavesDeContexto.Plataforma)] IWaysDbContext dbPlataforma,
    IRelojDelSistema reloj,
    IContextoDeUsuario contexto,
    IOptions<OpcionesDeMercadoPago> opciones,
    IProtectorDeEstadoOAuth protector,
    IClienteOAuthMercadoPago cliente,
    IAlmacenDeTokensMercadoPago almacen)
{
    public static readonly TimeSpan VigenciaDelEstado = TimeSpan.FromMinutes(10);

    private const string SqlIdReservado =
        "SELECT nextval(pg_get_serial_sequence('conexiones_mercadopago', 'id_conexion_mercadopago'))::int AS \"Value\"";

    /// <summary>Orden: 404 de la empresa, configuración, cuenta de tesorería y conexión previa.</summary>
    public async Task<InicioDeConexion> IniciarAsync(
        int idEmpresa, SolicitudDeInicioDeConexion solicitud, string origen, CancellationToken ct = default)
    {
        await ExigirEmpresaAsync(idEmpresa, ct);
        ExigirConfigurado();

        await ExigirCuentaDeLaEmpresaAsync(idEmpresa, solicitud.IdCuentaTesoreria, ct);

        if (await db.ConexionesMercadoPago.AnyAsync(
                c => c.IdEmpresa == idEmpresa && c.Estado == EstadoConexionMercadoPago.Activa, ct))
        {
            throw YaConectada();
        }

        var idTenant = contexto.IdTenant
            ?? throw new InvalidOperationException("Un usuario de plataforma no puede conectar Mercado Pago.");

        var verificador = Pkce.GenerarVerificador();
        var estado = protector.Proteger(new EstadoOAuth(
            idTenant, idEmpresa, solicitud.IdCuentaTesoreria, contexto.UsuarioId, verificador, origen));

        var config = opciones.Value;
        var consulta = string.Join(
            "&",
            $"client_id={Uri.EscapeDataString(config.ClientId!)}",
            "response_type=code",
            "platform_id=mp",
            $"state={Uri.EscapeDataString(estado)}",
            $"redirect_uri={Uri.EscapeDataString(config.RedirectUri!)}",
            $"code_challenge={Pkce.DesafioS256(verificador)}",
            "code_challenge_method=S256");

        var separador = config.UrlAutorizacion.Contains('?') ? "&" : "?";
        return new InicioDeConexion($"{config.UrlAutorizacion}{separador}{consulta}");
    }

    public const int LargoMaximoDelCodigo = 512;
    public const int LargoMaximoDelEstado = 2048;

    public EstadoOAuth? LeerEstado(string? estado) =>
        string.IsNullOrWhiteSpace(estado) ? null : protector.Desproteger(estado);

    /// <summary>
    /// Cierre del circuito dentro de la sesión que lo inició. Orden: 404 de la empresa, validación del cuerpo, 400
    /// <c>mercadopago_estado_invalido</c> (alterado o vencido), 403 <c>mercadopago_estado_ajeno</c> si la empresa o el
    /// usuario del <c>state</c> no son los de la ruta y la sesión, y 503 si el entorno no está configurado. Recién entonces se
    /// canjea el código, así que un enlace reenviado a otra persona nunca llega a Mercado Pago.
    /// </summary>
    public async Task<ResultadoDeConexion> CompletarEnSesionAsync(
        int idEmpresa, SolicitudDeCompletarConexion solicitud, CancellationToken ct = default)
    {
        await ExigirEmpresaAsync(idEmpresa, ct);

        if (string.IsNullOrWhiteSpace(solicitud.Code) || solicitud.Code.Length > LargoMaximoDelCodigo)
        {
            throw new ErrorDominio("solicitud_invalida", "Indicá el código de autorización (code).", 400);
        }

        if (string.IsNullOrWhiteSpace(solicitud.State) || solicitud.State.Length > LargoMaximoDelEstado)
        {
            throw new ErrorDominio("solicitud_invalida", "Indicá el estado de la conexión (state).", 400);
        }

        var estado = LeerEstado(solicitud.State)
            ?? throw new ErrorDominio(
                "mercadopago_estado_invalido",
                "El enlace de conexión con Mercado Pago no es válido o venció. Iniciá la conexión de nuevo.",
                400);

        // La empresa de la ruta ya se resolvió dentro del tenant de la sesión, así que igualarla con la del state ata también el tenant.
        if (estado.IdEmpresa != idEmpresa || estado.IdUsuario != contexto.UsuarioId)
        {
            throw new ErrorDominio(
                "mercadopago_estado_ajeno",
                "Esta conexión con Mercado Pago la inició otra sesión o otra empresa. Iniciala de nuevo desde acá.",
                403);
        }

        ExigirConfigurado();

        return await CompletarAsync(estado, solicitud.Code, null, ct);
    }

    /// <summary>
    /// Cierra el circuito con el tenant del <paramref name="estado"/> ya establecido por la capa de API. Todo fallo
    /// recuperable se devuelve como resultado para que el navegador vuelva a la pantalla con el motivo; el 23505 de una
    /// carrera sube como excepción para que la API lo clasifique con el resto de los backstops.
    /// </summary>
    public async Task<ResultadoDeConexion> CompletarAsync(
        EstadoOAuth estado, string? codigo, string? errorDeMercadoPago, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(errorDeMercadoPago))
        {
            return ResultadoDeConexion.Cancelado;
        }

        if (string.IsNullOrWhiteSpace(codigo) || !opciones.Value.EstaConfigurado || !almacen.PuedeCifrar)
        {
            return ResultadoDeConexion.Error;
        }

        if (!await db.Empresas.AnyAsync(e => e.Id == estado.IdEmpresa, ct))
        {
            return ResultadoDeConexion.Error;
        }

        var cuentaValida = await db.CuentasTesoreria.AnyAsync(
            c => c.Id == estado.IdCuentaTesoreria && c.IdEmpresa == estado.IdEmpresa && c.Activo, ct);
        if (!cuentaValida)
        {
            return ResultadoDeConexion.Error;
        }

        if (await db.ConexionesMercadoPago.AnyAsync(
                c => c.IdEmpresa == estado.IdEmpresa && c.Estado == EstadoConexionMercadoPago.Activa, ct))
        {
            return ResultadoDeConexion.YaConectada;
        }

        TokensDeMercadoPago tokens;
        try
        {
            tokens = await cliente.CanjearCodigoAsync(codigo, estado.CodeVerifier, ct);
        }
        catch (ErrorDominio)
        {
            return ResultadoDeConexion.Error;
        }

        // El tenant del contexto de la request no ve las conexiones de otros tenants: la cuenta de Mercado Pago ya
        // usada en otra empresa solo se ve con el contexto de plataforma.
        if (await dbPlataforma.ConexionesMercadoPago.AnyAsync(
                c => c.IdUsuarioMercadoPago == tokens.IdUsuario && c.Estado == EstadoConexionMercadoPago.Activa, ct))
        {
            return ResultadoDeConexion.CuentaEnUso;
        }

        // El AAD incluye el id de la conexión, así que se reserva antes de cifrar.
        var idConexion = await db.Database.SqlQueryRaw<int>(SqlIdReservado).SingleAsync(ct);

        var cifrados = almacen.Cifrar(estado.IdTenant, estado.IdEmpresa, idConexion, tokens.AccessToken, tokens.RefreshToken);
        var ahora = reloj.Ahora;

        db.ConexionesMercadoPago.Add(new ConexionMercadoPago
        {
            Id = idConexion,
            IdTenant = estado.IdTenant,
            IdEmpresa = estado.IdEmpresa,
            IdCuentaTesoreria = estado.IdCuentaTesoreria,
            IdUsuarioMercadoPago = tokens.IdUsuario,
            AccessTokenCifrado = cifrados.AccessTokenCifrado,
            AccessTokenNonce = cifrados.AccessTokenNonce,
            AccessTokenTag = cifrados.AccessTokenTag,
            RefreshTokenCifrado = cifrados.RefreshTokenCifrado,
            RefreshTokenNonce = cifrados.RefreshTokenNonce,
            RefreshTokenTag = cifrados.RefreshTokenTag,
            IdClaveMaestra = cifrados.IdClaveMaestra,
            AccessTokenExpiraEn = ahora.AddSeconds(tokens.ExpiraEnSegundos),
            Estado = EstadoConexionMercadoPago.Activa,
            IdUsuarioConecto = estado.IdUsuario,
            FechaConexion = ahora,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });

        await db.SaveChangesAsync(ct);

        return ResultadoDeConexion.Conectado;
    }

    /// <summary>
    /// La conexión activa si la hay. Sin activa, si la última conexión de la empresa quedó en
    /// <c>requiere_reconexion</c> responde <c>conectada: false</c> con ese <c>estado</c>, para que la pantalla ofrezca reconectar;
    /// una desconexión posterior (o ninguna conexión) responde el estado vacío.
    /// </summary>
    public async Task<EstadoDeConexionMercadoPago> ObtenerAsync(int idEmpresa, CancellationToken ct = default)
    {
        await ExigirEmpresaAsync(idEmpresa, ct);

        // La activa si existe; si no, la más reciente de cualquier estado (la desconectada o la que requiere reconexión).
        var fila = await (
            from c in db.ConexionesMercadoPago
            where c.IdEmpresa == idEmpresa
            join t in db.CuentasTesoreria on c.IdCuentaTesoreria equals t.Id into cuentas
            from t in cuentas.DefaultIfEmpty()
            orderby c.Estado == EstadoConexionMercadoPago.Activa descending, c.Id descending
            select new
            {
                c.Estado,
                c.IdCuentaTesoreria,
                NombreCuenta = t == null ? null : t.Nombre,
                c.IdUsuarioMercadoPago,
                c.FechaConexion,
                c.AccessTokenExpiraEn
            }).FirstOrDefaultAsync(ct);

        return fila?.Estado switch
        {
            EstadoConexionMercadoPago.Activa => new EstadoDeConexionMercadoPago(
                true, "activa", fila.IdCuentaTesoreria, fila.NombreCuenta, fila.IdUsuarioMercadoPago, fila.FechaConexion,
                fila.AccessTokenExpiraEn),
            EstadoConexionMercadoPago.RequiereReconexion => new EstadoDeConexionMercadoPago(
                false, "requiere_reconexion", fila.IdCuentaTesoreria, fila.NombreCuenta, fila.IdUsuarioMercadoPago,
                fila.FechaConexion, null),
            _ => new EstadoDeConexionMercadoPago(false, null, null, null, null, null, null)
        };
    }

    /// <summary>
    /// Un solo <c>UPDATE</c> guardado por estado: pasa a desconectada y borra los blobs en el mismo statement, así que no
    /// hay lectura previa que pueda quedar vieja. No se llama a Mercado Pago para revocar el token: la API de OAuth no
    /// documenta una revocación, la conexión se invalida del lado de Ways.
    /// </summary>
    public async Task DesconectarAsync(int idEmpresa, CancellationToken ct = default)
    {
        await ExigirEmpresaAsync(idEmpresa, ct);

        var ahora = reloj.Ahora;
        var filas = await db.ConexionesMercadoPago
            .Where(c => c.IdEmpresa == idEmpresa && c.Estado == EstadoConexionMercadoPago.Activa)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(c => c.Estado, EstadoConexionMercadoPago.Desconectada)
                    .SetProperty(c => c.FechaDesconexion, ahora)
                    .SetProperty(c => c.UpdatedAt, ahora)
                    .SetProperty(c => c.AccessTokenCifrado, (byte[]?)null)
                    .SetProperty(c => c.AccessTokenNonce, (byte[]?)null)
                    .SetProperty(c => c.AccessTokenTag, (byte[]?)null)
                    .SetProperty(c => c.RefreshTokenCifrado, (byte[]?)null)
                    .SetProperty(c => c.RefreshTokenNonce, (byte[]?)null)
                    .SetProperty(c => c.RefreshTokenTag, (byte[]?)null),
                ct);

        if (filas == 0)
        {
            throw ErrorDominio.NoEncontrado($"La empresa {idEmpresa} no tiene una conexión activa con Mercado Pago.");
        }
    }

    /// <summary>
    /// Mercado Pago respondió 401 a un token que se creía vigente: un solo <c>UPDATE</c> pasa la conexión a
    /// <c>requiere_reconexion</c> y borra los seis campos de cifrado. Va guardado por estado Y por el vencimiento del token usado, así
    /// que una renovación que haya commiteado entre la lectura del token y este 401 (otro vencimiento) deja la fila intacta.
    /// </summary>
    public async Task<bool> MarcarRequiereReconexionPorRechazoAsync(
        int idConexion, DateTimeOffset accessTokenExpiraEn, CancellationToken ct = default)
    {
        var ahora = reloj.Ahora;
        var filas = await db.ConexionesMercadoPago
            .Where(c => c.Id == idConexion
                && c.Estado == EstadoConexionMercadoPago.Activa
                && c.AccessTokenExpiraEn == accessTokenExpiraEn)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(c => c.Estado, EstadoConexionMercadoPago.RequiereReconexion)
                    .SetProperty(c => c.FechaRequiereReconexion, ahora)
                    .SetProperty(c => c.UpdatedAt, ahora)
                    .SetProperty(c => c.AccessTokenCifrado, (byte[]?)null)
                    .SetProperty(c => c.AccessTokenNonce, (byte[]?)null)
                    .SetProperty(c => c.AccessTokenTag, (byte[]?)null)
                    .SetProperty(c => c.RefreshTokenCifrado, (byte[]?)null)
                    .SetProperty(c => c.RefreshTokenNonce, (byte[]?)null)
                    .SetProperty(c => c.RefreshTokenTag, (byte[]?)null),
                ct);

        return filas > 0;
    }

    private async Task ExigirEmpresaAsync(int idEmpresa, CancellationToken ct)
    {
        // El filtro de tenant de EF esconde las empresas de otros tenants y las dadas de baja.
        if (!await db.Empresas.AnyAsync(e => e.Id == idEmpresa, ct))
        {
            throw ErrorDominio.NoEncontrado($"No existe la empresa {idEmpresa}.");
        }
    }

    private void ExigirConfigurado()
    {
        if (!opciones.Value.EstaConfigurado || !almacen.PuedeCifrar)
        {
            throw new ErrorDominio(
                "mercadopago_no_configurado",
                "La conexión con Mercado Pago no está configurada en este entorno.",
                503);
        }
    }

    private async Task ExigirCuentaDeLaEmpresaAsync(int idEmpresa, int idCuenta, CancellationToken ct)
    {
        var cuenta = await db.CuentasTesoreria
            .Where(c => c.Id == idCuenta)
            .Select(c => new { c.IdEmpresa, c.Activo })
            .FirstOrDefaultAsync(ct)
            ?? throw new ErrorDominio("referencia_invalida", $"No existe la cuenta de tesorería {idCuenta}.", 400);

        if (cuenta.IdEmpresa != idEmpresa)
        {
            throw ErrorDominio.Conflicto(
                "cuenta_tesoreria_de_otra_empresa", "La cuenta de tesorería pertenece a otra empresa.");
        }

        if (!cuenta.Activo)
        {
            throw ErrorDominio.Conflicto("cuenta_tesoreria_inactiva", "La cuenta de tesorería está inactiva.");
        }
    }

    private static ErrorDominio YaConectada() =>
        ErrorDominio.Conflicto("mercadopago_ya_conectada", "La empresa ya tiene Mercado Pago conectado.");
}
