using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Application.Auditoria;
using Ways.Domain.Auditoria;
using Ways.Domain.Common;
using Ways.Domain.MercadoPago;
using Ways.Domain.Organizacion;

namespace Ways.Application.MercadoPago;

/// <summary>
/// Registra una sucursal como local (<i>store</i>) de la cuenta de Mercado Pago conectada a su empresa y guarda el vínculo
/// (<c>locales_mercadopago</c>). Todo lo usa un Admin (la política se aplica en la capa de API). Orden de los errores: 404 de la
/// sucursal, 400 de la dirección incompleta, 409 de la conexión ausente o pendiente de reconexión.
///
/// <b>Lock</b> (<c>single-read-under-lock</c>): un <c>pg_advisory_xact_lock</c> por sucursal serializa dos clics y se mantiene
/// mientras se llama a Mercado Pago, así que el segundo espera el resultado del primero y encuentra el vínculo ya guardado. La
/// sucursal y el vínculo se leen UNA vez, después del lock. El lock no cubre la baja ni la edición de la sucursal (esas toman el
/// lock del tenant): una edición concurrente deja el vínculo desactualizado porque la fecha de sincronización es la de la lectura
/// de la sucursal, no la del fin de la llamada.
///
/// <b>Sin reintento</b> (<c>ef-retry-safe-writes</c>, forma (b)): la escritura agrega el vínculo y una fila de auditoría después de
/// una llamada externa que no se puede repetir a ciegas. Si Mercado Pago creó el local y el guardado falla, el próximo intento lo
/// encuentra por <c>external_id</c> y lo adopta en vez de duplicarlo.
///
/// Un 401 de Mercado Pago deja la conexión en <c>requiere_reconexion</c> dentro de esta misma transacción, así que se confirma
/// igual antes de relanzar el error: de lo contrario el rollback desharía esa marca.
///
/// Los campos de Mercado Pago (<c>id</c>, <c>external_id</c>, la forma de la búsqueda) vienen de la referencia de la API y no se
/// ejercitaron en vivo; los DTO ignoran lo que no usan.
/// </summary>
public class ServicioDeLocalesMercadoPago(
    IWaysDbContext db,
    IClienteApiMercadoPago api,
    IRelojDelSistema reloj,
    ServicioDeAuditoria auditoria)
{
    /// <summary>Segunda clave del <c>pg_advisory_xact_lock(int, int)</c>; la primera es el id de la sucursal.</summary>
    public const int ClaveDeLockDeLocal = 0x4D504C43;

    public async Task<EstadoDeLocalMercadoPago> ObtenerAsync(int idSucursal, CancellationToken ct = default)
    {
        var sucursal = await LeerSucursalAsync(idSucursal, ct);
        var faltantes = LocalParaMercadoPago.CamposFaltantes(sucursal.Nombre, sucursal.Direccion);

        var conexion = await LeerConexionAsync(sucursal.IdEmpresa, ct);
        if (conexion is not { Estado: EstadoConexionMercadoPago.Activa })
        {
            return new EstadoDeLocalMercadoPago(EstadosDeLocalMercadoPago.SinConexion, null, null, null, null, faltantes);
        }

        var vinculo = await db.LocalesMercadoPago
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.IdSucursal == idSucursal && l.IdUsuarioMercadoPago == conexion.IdUsuarioMercadoPago, ct);
        if (vinculo is null)
        {
            return new EstadoDeLocalMercadoPago(EstadosDeLocalMercadoPago.NoRegistrada, null, null, null, null, faltantes);
        }

        var estado = sucursal.UpdatedAt > vinculo.FechaSincronizacion
            ? EstadosDeLocalMercadoPago.Desactualizada
            : EstadosDeLocalMercadoPago.Registrada;

        return new EstadoDeLocalMercadoPago(
            estado, vinculo.IdStoreMercadoPago, vinculo.ExternalId, vinculo.FechaRegistro, vinculo.FechaSincronizacion, faltantes);
    }

    /// <summary>
    /// Crea el local en Mercado Pago, adopta el que ya existe con el mismo <c>external_id</c> o actualiza el vínculo con los datos
    /// actuales de la sucursal, según lo que encuentre bajo el lock. Devuelve el estado resultante.
    /// </summary>
    public async Task<EstadoDeLocalMercadoPago> RegistrarAsync(int idSucursal, CancellationToken ct = default)
    {
        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        return await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);
            await TomarLockDeLaSucursalAsync(idSucursal, ct);

            ErrorDominio? reconexion = null;
            EstadoDeLocalMercadoPago? resultado = null;
            try
            {
                resultado = await RegistrarBajoLockAsync(idSucursal, ct);
            }
            catch (ErrorDominio error) when (error.Codigo == CodigosDeErrorDeMercadoPago.RequiereReconexion)
            {
                reconexion = error;
            }

            await transaccion.CommitAsync(ct);

            return reconexion is null ? resultado! : throw reconexion;
        });
    }

    private async Task<EstadoDeLocalMercadoPago> RegistrarBajoLockAsync(int idSucursal, CancellationToken ct)
    {
        var sucursal = await LeerSucursalAsync(idSucursal, ct);
        var local = LocalParaMercadoPago.Armar(sucursal.Nombre, sucursal.Direccion);

        var conexion = await LeerConexionAsync(sucursal.IdEmpresa, ct);
        var idUsuario = conexion switch
        {
            { Estado: EstadoConexionMercadoPago.Activa } => conexion.IdUsuarioMercadoPago,
            { Estado: EstadoConexionMercadoPago.RequiereReconexion } => throw new ErrorDominio(
                CodigosDeErrorDeMercadoPago.RequiereReconexion,
                "La conexión con Mercado Pago de esta empresa dejó de ser válida: hay que reconectarla.",
                409),
            _ => throw new ErrorDominio(
                CodigosDeErrorDeMercadoPago.NoConectada, "La empresa no tiene Mercado Pago conectado.", 409)
        };

        var vinculo = await db.LocalesMercadoPago
            .FirstOrDefaultAsync(l => l.IdSucursal == idSucursal && l.IdUsuarioMercadoPago == idUsuario, ct);

        // La fecha de la lectura de la sucursal, no la del fin de la llamada: una edición hecha mientras Mercado Pago responde
        // queda posterior a ella y el vínculo figura desactualizado.
        var sincronizadoEn = reloj.Ahora;
        var externalId = LocalParaMercadoPago.ExternalIdDe(idSucursal);
        var cuerpo = Armar(local, externalId);
        var ruta = $"/users/{idUsuario}/stores";

        var estadoDeLaSucursal = sucursal.UpdatedAt.UtcTicks;
        string operacion;
        long idStore;
        if (vinculo is not null)
        {
            idStore = vinculo.IdStoreMercadoPago;
            operacion = "actualizacion";
            try
            {
                await ActualizarAsync(sucursal.IdEmpresa, ruta, idStore, cuerpo, idSucursal, estadoDeLaSucursal, ct);
            }
            catch (ErrorDeApiMercadoPago error) when (error.EstadoMercadoPago == 404)
            {
                // El local se borró en Mercado Pago: se lo busca por external_id y, si tampoco está, se lo crea de nuevo.
                (idStore, operacion) = await AdoptarOCrearAsync(
                    sucursal.IdEmpresa, ruta, externalId, cuerpo, idSucursal, estadoDeLaSucursal, ct);
            }
        }
        else
        {
            (idStore, operacion) = await AdoptarOCrearAsync(
                sucursal.IdEmpresa, ruta, externalId, cuerpo, idSucursal, estadoDeLaSucursal, ct);
        }

        var ahora = reloj.Ahora;
        if (vinculo is null)
        {
            vinculo = new LocalMercadoPago
            {
                IdTenant = sucursal.IdTenant,
                IdEmpresa = sucursal.IdEmpresa,
                IdSucursal = idSucursal,
                IdUsuarioMercadoPago = idUsuario,
                IdStoreMercadoPago = idStore,
                ExternalId = externalId,
                FechaRegistro = ahora,
                FechaSincronizacion = sincronizadoEn,
                CreatedAt = ahora,
                UpdatedAt = ahora
            };
            db.LocalesMercadoPago.Add(vinculo);
        }
        else
        {
            vinculo.IdStoreMercadoPago = idStore;
            vinculo.FechaSincronizacion = sincronizadoEn;
            vinculo.UpdatedAt = ahora;
        }

        var (anterior, nuevo) = PayloadDeAuditoria.LocalDeMercadoPago(operacion, idUsuario, idStore, externalId, sincronizadoEn);
        auditoria.Registrar(new RegistroDeAuditoria(
            sucursal.IdTenant, idPuntoVenta: null, AccionAuditada.SucursalMercadoPago, idSucursal, anterior, nuevo));
        await db.SaveChangesAsync(ct);

        return new EstadoDeLocalMercadoPago(
            EstadosDeLocalMercadoPago.Registrada, idStore, externalId, vinculo.FechaRegistro, sincronizadoEn, []);
    }

    private static SolicitudDeLocalMercadoPago Armar(LocalParaMercadoPago local, string externalId) => new(
        local.Nombre,
        externalId,
        new UbicacionDeLocalMercadoPago(
            local.Altura, local.Calle, local.Ciudad, local.Provincia, local.Latitud, local.Longitud, local.Referencia));

    private async Task<(long IdStore, string Operacion)> AdoptarOCrearAsync(
        int idEmpresa, string ruta, string externalId, SolicitudDeLocalMercadoPago cuerpo, int idSucursal, long estadoDeLaSucursal,
        CancellationToken ct)
    {
        if (await BuscarPorExternalIdAsync(idEmpresa, ruta, externalId, ct) is { } existente)
        {
            await ActualizarAsync(idEmpresa, ruta, existente, cuerpo, idSucursal, estadoDeLaSucursal, ct);
            return (existente, "adopcion");
        }

        return (await CrearAsync(idEmpresa, ruta, cuerpo, idSucursal, ct), "alta");
    }

    private static string HuellaDe(SolicitudDeLocalMercadoPago cuerpo) => HuellaDeTexto(JsonSerializer.Serialize(cuerpo));

    private static string HuellaDeTexto(string texto) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(texto)))[..16];

    /// <summary>La clave cambia con el contenido: Mercado Pago contesta 409 si la misma clave llega con otro cuerpo dentro de las 24 h,
    /// así que un alta con datos editados no puede reusar la clave del intento anterior.</summary>
    private async Task<long> CrearAsync(
        int idEmpresa, string ruta, SolicitudDeLocalMercadoPago cuerpo, int idSucursal, CancellationToken ct)
    {
        var creado = await api.EnviarAsync<LocalDeMercadoPago>(
            idEmpresa, HttpMethod.Post, ruta, cuerpo, $"ways-local-{idSucursal}-alta-{HuellaDe(cuerpo)}", ct);

        if (creado.Id <= 0)
        {
            throw new ErrorDominio(
                CodigosDeErrorDeMercadoPago.RespuestaInvalida, "Mercado Pago respondió con un local sin identificador.", 502);
        }

        return creado.Id;
    }

    /// <summary>La clave lleva el local de destino, el contenido y el estado de la sucursal (su <c>updated_at</c>): reintentar el mismo
    /// intento repite la clave, volver a un contenido anterior (A, B, A) después de una edición no recibe la respuesta guardada del
    /// primero, y la adopción que sigue a un 404 no reusa la clave del PUT que falló contra el local borrado.</summary>
    private async Task ActualizarAsync(
        int idEmpresa, string ruta, long idStore, SolicitudDeLocalMercadoPago cuerpo, int idSucursal, long estadoDeLaSucursal,
        CancellationToken ct) =>
        await api.EnviarAsync<LocalDeMercadoPago>(
            idEmpresa, HttpMethod.Put, $"{ruta}/{idStore}", cuerpo,
            $"ways-local-{idSucursal}-act-{HuellaDeTexto($"{idStore}|{HuellaDe(cuerpo)}|{estadoDeLaSucursal}")}", ct);

    /// <summary>El local que un intento anterior dejó creado en Mercado Pago sin llegar a guardar el vínculo, si hay uno con el
    /// mismo <c>external_id</c>.</summary>
    private async Task<long?> BuscarPorExternalIdAsync(int idEmpresa, string ruta, string externalId, CancellationToken ct)
    {
        var busqueda = await api.EnviarAsync<BusquedaDeLocalesMercadoPago>(
            idEmpresa, HttpMethod.Get, $"{ruta}/search?external_id={Uri.EscapeDataString(externalId)}", ct: ct);

        var coincidencia = busqueda.Results?.FirstOrDefault(
            r => r.Id > 0 && string.Equals(r.ExternalId, externalId, StringComparison.OrdinalIgnoreCase));

        return coincidencia?.Id;
    }

    private async Task<SucursalLeida> LeerSucursalAsync(int idSucursal, CancellationToken ct) =>
        await db.Sucursales
            .AsNoTracking()
            .Where(s => s.Id == idSucursal)
            .Select(s => new SucursalLeida(
                s.IdTenant, s.IdEmpresa, s.Nombre, s.UpdatedAt,
                s.Calle, s.Altura, s.Ciudad, s.Provincia, s.CodigoPostal, s.Latitud, s.Longitud, s.Referencia))
            .FirstOrDefaultAsync(ct)
        ?? throw ErrorDominio.NoEncontrado($"No existe la sucursal {idSucursal}.");

    /// <summary>La conexión activa de la empresa; sin activa, la más reciente (para distinguir "nunca conectó" de "hay que reconectar").</summary>
    private Task<ConexionMercadoPago?> LeerConexionAsync(int idEmpresa, CancellationToken ct) =>
        db.ConexionesMercadoPago
            .AsNoTracking()
            .Where(c => c.IdEmpresa == idEmpresa)
            .OrderByDescending(c => c.Estado == EstadoConexionMercadoPago.Activa)
            .ThenByDescending(c => c.Id)
            .FirstOrDefaultAsync(ct);

    private async Task TomarLockDeLaSucursalAsync(int idSucursal, CancellationToken ct)
    {
        var transaccion = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException(
                "El lock del local de Mercado Pago es de alcance de transacción: tomarlo fuera de una no protege nada.");

        var conexion = db.Database.GetDbConnection();
        if (conexion.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion.GetDbTransaction();
        comando.CommandText = "SELECT pg_advisory_xact_lock($1, $2)";
        ParametrosDeComando.Agregar(comando, idSucursal);
        ParametrosDeComando.Agregar(comando, ClaveDeLockDeLocal);
        await comando.ExecuteNonQueryAsync(ct);
    }

    private sealed record SucursalLeida(
        int IdTenant,
        int IdEmpresa,
        string Nombre,
        DateTimeOffset UpdatedAt,
        string? Calle,
        string? Altura,
        string? Ciudad,
        string? Provincia,
        string? CodigoPostal,
        decimal? Latitud,
        decimal? Longitud,
        string? Referencia)
    {
        public DireccionDeSucursal Direccion =>
            new(Calle, Altura, Ciudad, Provincia, CodigoPostal, Latitud, Longitud, Referencia);
    }
}
