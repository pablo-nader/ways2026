using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Auditoria;
using Ways.Domain.Auditoria;
using Ways.Domain.Common;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;

namespace Ways.Application.Organizacion;

/// <summary>
/// ABM de sucursales (tenant → empresa → sucursal → punto de venta) y el movimiento de una caja entre sucursales de su
/// empresa. Las sucursales que nacen con un punto de venta las crea el hook de <c>WaysDbContext</c>; acá se crean,
/// renombran y dan de baja las que administra el operador.
///
/// <b>Locks</b> (<c>single-read-under-lock</c>): todo escritor de este archivo toma primero el advisory lock del TENANT
/// de las bajas (<see cref="TomarLockDeBajaAsync"/>), el mismo de la baja y el alta de un punto de venta, de la baja de
/// la empresa y del tenant; así una sucursal no desaparece entre que se la elige y que se escribe la caja que la
/// referencia. El movimiento toma además el lock de FILA del punto de venta (<c>FOR UPDATE</c>): los escritores que asientan
/// cantidades resuelven la sucursal de su caja con <c>FOR SHARE</c> sobre esa misma fila (<c>EscritorDeStock.ResolverSucursalAsync</c>),
/// de modo que un asiento en vuelo termina antes de que se mire el uso de la sucursal de origen, y el asiento que llega
/// después lee la sucursal nueva. No se bloquea la fila de la sucursal: ningún camino de escritura la modifica fuera del
/// lock del tenant. Lo que sigue sin cubrirse es una reversa de venta o compra (usa la sucursal guardada en el movimiento
/// original): solo puede apuntar a una sucursal que tuvo uso, y una sucursal con uso nunca se da de baja.
///
/// <b>Sin reintento</b> (<c>ef-retry-safe-writes</c>): cada escritura agrega una fila de auditoría sin clave de
/// idempotencia, así que corre en <see cref="EnUnaTransaccionDeBajaAsync{T}"/>.
///
/// <b>Backstops</b> (<c>db-error-backstops</c>): ninguna constraint nueva. <c>fk_puntos_venta_sucursal</c> (compuesta con
/// empresa y tenant) queda cubierta por la validación bajo lock y, si una escritura cruda la esquivara, por el backstop
/// genérico de FKs de <c>ManejadorDeErrores</c>. El nombre de la sucursal no es único, igual que el del punto de venta.
/// </summary>
public partial class ServicioDeOrganizacion
{
    private const int LargoMaximoNombreSucursal = 150;

    public static Expression<Func<Sucursal, SucursalListado>> ProyeccionDeSucursal(IWaysDbContext db) =>
        s => new SucursalListado(
            s.Id,
            s.IdTenant,
            s.IdEmpresa,
            s.Nombre,
            db.Empresas
                .Where(e => e.Id == s.IdEmpresa && e.DeletedAt == null)
                .Select(e => e.RazonSocial)
                .FirstOrDefault(),
            db.PuntosVenta.Count(p => p.IdSucursal == s.Id && p.DeletedAt == null));

    public async Task<IReadOnlyList<SucursalListado>> ListarSucursalesAsync(
        int? idEmpresa, CancellationToken ct = default) =>
        await db.Sucursales
            .Where(s => idEmpresa == null || s.IdEmpresa == idEmpresa)
            .OrderBy(s => s.Nombre)
            .ThenBy(s => s.Id)
            .Select(ProyeccionDeSucursal(db))
            .ToListAsync(ct);

    public async Task<SucursalListado> CrearSucursalAsync(SucursalAlta datos, CancellationToken ct = default)
    {
        var idEmpresa = datos.IdEmpresa
            ?? throw new ErrorDominio("empresa_requerida", "El campo empresa es obligatorio.", 400);

        var nombre = Normalizar(datos.Nombre, "nombre_sucursal", "nombre de la sucursal", LargoMaximoNombreSucursal);

        var idTenant = await db.Empresas
            .Where(e => e.Id == idEmpresa)
            .Select(e => (int?)e.IdTenant)
            .FirstOrDefaultAsync(ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe la empresa {idEmpresa}.");

        PoliticaDeRoles.ValidarAlcanceDeTenant(Actor, idTenant);

        return await EnUnaTransaccionDeBajaAsync(async () =>
        {
            await TomarLockDeBajaAsync(idTenant, ct);

            var empresa = await BuscarEmpresaAsync(idEmpresa, ct);

            var ahora = reloj.Ahora;
            var sucursal = new Sucursal
            {
                IdTenant = empresa.IdTenant,
                IdEmpresa = empresa.Id,
                Nombre = nombre,
                CreatedAt = ahora,
                UpdatedAt = ahora
            };
            db.Sucursales.Add(sucursal);
            await db.SaveChangesAsync(ct);

            var (valorAnterior, valorNuevo) = PayloadDeAuditoria.AltaDeSucursal(empresa.Id, nombre);
            auditoria.Registrar(new RegistroDeAuditoria(
                sucursal.IdTenant, idPuntoVenta: null, AccionAuditada.SucursalAlta, sucursal.Id,
                valorAnterior, valorNuevo));
            await db.SaveChangesAsync(ct);

            return await LeerSucursalAsync(sucursal.Id, ct);
        }, ct);
    }

    public async Task<SucursalListado> ActualizarSucursalAsync(
        int id, SucursalEdicion datos, CancellationToken ct = default)
    {
        // update-404-first: la sucursal inexistente, dada de baja o de otro tenant contesta 404 antes de validar el cuerpo.
        var idTenant = await IdTenantDeLaSucursalAsync(id, ct);
        var nombre = Normalizar(datos.Nombre, "nombre_sucursal", "nombre de la sucursal", LargoMaximoNombreSucursal);

        return await EnUnaTransaccionDeBajaAsync(async () =>
        {
            await TomarLockDeBajaAsync(idTenant, ct);

            var sucursal = await BuscarSucursalAsync(id, ct);

            var nombreAnterior = sucursal.Nombre;
            sucursal.Nombre = nombre;
            sucursal.UpdatedAt = reloj.Ahora;

            var (valorAnterior, valorNuevo) = PayloadDeAuditoria.RenombreDeSucursal(nombreAnterior, nombre);
            auditoria.Registrar(new RegistroDeAuditoria(
                sucursal.IdTenant, idPuntoVenta: null, AccionAuditada.SucursalEdicion, sucursal.Id,
                valorAnterior, valorNuevo));
            await db.SaveChangesAsync(ct);

            return await LeerSucursalAsync(sucursal.Id, ct);
        }, ct);
    }

    /// <summary>
    /// Baja lógica. Una sucursal con cajas vivas no se da de baja (<c>409 sucursal_con_puntos_venta</c>) y, desde la
    /// regla de bajas de todo el catálogo, tampoco una que ya tuvo uso: stock, lotes o movimientos
    /// (<c>409 sucursal_en_uso</c>, el mismo guard de <see cref="InspectorDeUso"/> que decide la baja de la última caja).
    /// </summary>
    public async Task EliminarSucursalAsync(int id, CancellationToken ct = default)
    {
        var idTenant = await IdTenantDeLaSucursalAsync(id, ct);

        await EnUnaTransaccionDeBajaAsync(async () =>
        {
            await TomarLockDeBajaAsync(idTenant, ct);

            var sucursal = await BuscarSucursalAsync(id, ct);

            if (await db.PuntosVenta.AnyAsync(p => p.IdSucursal == sucursal.Id, ct))
            {
                throw ErrorDominio.Conflicto(
                    "sucursal_con_puntos_venta",
                    "No se puede dar de baja la sucursal porque tiene puntos de venta: movelos a otra sucursal primero.");
            }

            await ExigirSinUsoDeSucursalAsync(sucursal, mensajeDeBloqueo: null, "sucursal_en_uso", ct);

            var momento = reloj.Ahora;
            Marcar(sucursal, momento);
            RegistrarBajaDeSucursal(sucursal, momento, porCascada: false);

            await db.SaveChangesAsync(ct);
            return true;
        }, ct);
    }

    /// <summary>
    /// Mueve un punto de venta a otra sucursal VIVA de su misma empresa (<c>POST /api/puntos-venta/{id}/sucursal</c>).
    /// El stock no viaja con la caja: pertenece a la sucursal, así que desde el movimiento la caja vende, compra y
    /// ajusta contra el stock de la sucursal nueva.
    ///
    /// Si la caja era la última viva de su sucursal, esa sucursal cae con el movimiento, salvo que tenga uso (stock,
    /// lotes o movimientos): entonces el movimiento se rechaza con <c>409 sucursal_en_uso</c>, la misma regla que la baja
    /// de la última caja. Mover la única caja de una sucursal con historia es una fusión (etapa posterior), no un movimiento.
    ///
    /// Mismo destino que el actual: no hace nada y no deja rastro. Una sucursal de otra empresa es
    /// <c>409 sucursal_de_otra_empresa</c>; una inexistente, dada de baja o de otro tenant es 404.
    /// </summary>
    public async Task<PuntoVentaListado> MoverPuntoVentaDeSucursalAsync(
        int id, PuntoVentaSucursalEdicion datos, CancellationToken ct = default)
    {
        // update-404-first: id de la caja inexistente o ajena → 404 antes de mirar el cuerpo.
        var idTenant = await db.PuntosVenta
            .Where(p => p.Id == id)
            .Select(p => (int?)p.IdTenant)
            .FirstOrDefaultAsync(ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el punto de venta {id}.");

        PoliticaDeRoles.ValidarAlcanceDeTenant(Actor, idTenant);

        var idSucursalNueva = datos.IdSucursal
            ?? throw new ErrorDominio("sucursal_requerida", "El campo sucursal es obligatorio.", 400);

        return await EnUnaTransaccionDeBajaAsync(async () =>
        {
            await TomarLockDeBajaAsync(idTenant, ct);
            await TomarLockDePuntoVentaAsync(id, ct);

            var puntoVenta = await BuscarPuntoVentaAsync(id, ct);

            if (puntoVenta.IdSucursal == idSucursalNueva)
            {
                return await LeerPuntoVentaAsync(id, ct);
            }

            var destino = await BuscarSucursalDeLaEmpresaAsync(idSucursalNueva, puntoVenta.IdEmpresa, ct);

            var idSucursalAnterior = puntoVenta.IdSucursal;
            var quedanCajas = await db.PuntosVenta
                .AnyAsync(p => p.IdSucursal == idSucursalAnterior && p.Id != puntoVenta.Id, ct);

            var origen = quedanCajas
                ? null
                : await db.Sucursales.FirstOrDefaultAsync(s => s.Id == idSucursalAnterior, ct);

            if (origen is not null)
            {
                await ExigirSinUsoDeSucursalAsync(
                    origen,
                    descripcion => "No se puede mover la caja: su sucursal quedaría sin cajas y todavía tiene " +
                                   $"{descripcion}. Para unir dos sucursales con stock hace falta una fusión.",
                    "sucursal_en_uso",
                    ct);
            }

            var momento = reloj.Ahora;
            puntoVenta.IdSucursal = destino.Id;
            puntoVenta.UpdatedAt = momento;

            var (valorAnterior, valorNuevo) = PayloadDeAuditoria.CambioDeSucursalDePuntoVenta(
                idSucursalAnterior, destino.Id, sucursalDadaDeBaja: origen is not null);
            auditoria.Registrar(new RegistroDeAuditoria(
                puntoVenta.IdTenant, puntoVenta.Id, AccionAuditada.PuntoVentaSucursal, puntoVenta.Id,
                valorAnterior, valorNuevo));

            if (origen is not null)
            {
                Marcar(origen, momento);
                RegistrarBajaDeSucursal(origen, momento, porCascada: true);
            }

            await db.SaveChangesAsync(ct);

            return await LeerPuntoVentaAsync(puntoVenta.Id, ct);
        }, ct);
    }

    private Task ExigirSinUsoDeSucursalAsync(
        Sucursal sucursal, Func<string, string>? mensajeDeBloqueo, string codigo, CancellationToken ct) =>
        ExigirSinUsoAsync(
            typeof(Sucursal),
            sucursal.CreatedAt,
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["Id"] = sucursal.Id,
                ["IdEmpresa"] = sucursal.IdEmpresa,
                ["IdTenant"] = sucursal.IdTenant
            },
            codigo,
            "la sucursal",
            ct,
            // Las cajas ya las respondió quien llama (vivas) o son las propias; acá solo el uso propio de la sucursal.
            soloRamas: rama => rama.Puente is null && rama.Tabla != "puntos_venta",
            mensajeDeBloqueo);

    private void RegistrarBajaDeSucursal(Sucursal sucursal, DateTimeOffset momento, bool porCascada)
    {
        var (valorAnterior, valorNuevo) = PayloadDeAuditoria.BajaDeOrganizacion(momento, porCascada);

        auditoria.Registrar(new RegistroDeAuditoria(
            sucursal.IdTenant, idPuntoVenta: null, AccionAuditada.SucursalBaja, sucursal.Id,
            valorAnterior, valorNuevo));
    }

    /// <summary>La sucursal viva con ese id que además es de la empresa dada. Se llama con el lock del tenant tomado:
    /// una baja concurrente no puede volverla inválida entre esta lectura y la escritura del punto de venta.</summary>
    private async Task<Sucursal> BuscarSucursalDeLaEmpresaAsync(int idSucursal, int idEmpresa, CancellationToken ct)
    {
        var sucursal = await BuscarSucursalAsync(idSucursal, ct);

        if (sucursal.IdEmpresa != idEmpresa)
        {
            throw ErrorDominio.Conflicto(
                "sucursal_de_otra_empresa", "La sucursal pertenece a otra empresa: elegí una de la empresa del punto de venta.");
        }

        return sucursal;
    }

    private async Task<Sucursal> BuscarSucursalAsync(int id, CancellationToken ct)
    {
        var sucursal = await db.Sucursales.FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe la sucursal {id}.");

        PoliticaDeRoles.ValidarAlcanceDeTenant(Actor, sucursal.IdTenant);

        return sucursal;
    }

    private async Task<int> IdTenantDeLaSucursalAsync(int id, CancellationToken ct)
    {
        var idTenant = await db.Sucursales
            .Where(s => s.Id == id)
            .Select(s => (int?)s.IdTenant)
            .FirstOrDefaultAsync(ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe la sucursal {id}.");

        PoliticaDeRoles.ValidarAlcanceDeTenant(Actor, idTenant);

        return idTenant;
    }

    private async Task<SucursalListado> LeerSucursalAsync(int id, CancellationToken ct) =>
        await db.Sucursales
            .Where(s => s.Id == id)
            .Select(ProyeccionDeSucursal(db))
            .FirstOrDefaultAsync(ct)
        ?? throw ErrorDominio.NoEncontrado($"No existe la sucursal {id}.");

    private async Task<PuntoVentaListado> LeerPuntoVentaAsync(int id, CancellationToken ct) =>
        await db.PuntosVenta
            .Where(p => p.Id == id)
            .Select(ProyeccionDePuntoVenta(db))
            .FirstOrDefaultAsync(ct)
        ?? throw ErrorDominio.NoEncontrado($"No existe el punto de venta {id}.");
}
