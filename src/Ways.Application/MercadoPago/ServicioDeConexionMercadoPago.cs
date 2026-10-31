using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Ways.Application.Abstracciones;
using Ways.Domain.Common;
using Ways.Domain.MercadoPago;

namespace Ways.Application.MercadoPago;

/// <summary>
/// Conexión de una empresa con Mercado Pago por OAuth. Inicio, lectura y desconexión los usa un Admin (la política se
/// aplica en la capa de API); el cierre del circuito (<see cref="CompletarAsync"/>) lo dispara el navegador al volver de
/// Mercado Pago, sin sesión, y confía solo en lo que dice el <c>state</c> protegido.
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

    public EstadoOAuth? LeerEstado(string? estado) =>
        string.IsNullOrWhiteSpace(estado) ? null : protector.Desproteger(estado);

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

    public async Task<EstadoDeConexionMercadoPago> ObtenerAsync(int idEmpresa, CancellationToken ct = default)
    {
        await ExigirEmpresaAsync(idEmpresa, ct);

        var fila = await (
            from c in db.ConexionesMercadoPago
            where c.IdEmpresa == idEmpresa && c.Estado == EstadoConexionMercadoPago.Activa
            join t in db.CuentasTesoreria on c.IdCuentaTesoreria equals t.Id into cuentas
            from t in cuentas.DefaultIfEmpty()
            select new
            {
                c.IdCuentaTesoreria,
                NombreCuenta = t == null ? null : t.Nombre,
                c.IdUsuarioMercadoPago,
                c.FechaConexion,
                c.AccessTokenExpiraEn
            }).FirstOrDefaultAsync(ct);

        return fila is null
            ? new EstadoDeConexionMercadoPago(false, null, null, null, null, null, null)
            : new EstadoDeConexionMercadoPago(
                true,
                "activa",
                fila.IdCuentaTesoreria,
                fila.NombreCuenta,
                fila.IdUsuarioMercadoPago,
                fila.FechaConexion,
                fila.AccessTokenExpiraEn);
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
