using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Bajas;
using Ways.Domain.Caja;
using Ways.Domain.Common;

namespace Ways.Application.Caja;

/// <summary>
/// ABM de las cuentas de tesorería de una empresa. La autorización (<c>GestionDeCatalogo</c>, tanto
/// para leer como para escribir) se aplica en la capa de API.
///
/// El pre-chequeo de nombre repetido es una cortesía de UX: el contrato real es el índice único
/// parcial <c>ux_cuentas_tesoreria_nombre_empresa</c>, que <c>ManejadorDeErrores</c> traduce al mismo 409.
/// </summary>
public class ServicioDeCuentasDeTesoreria(IWaysDbContext db, IRelojDelSistema reloj, GuardaDeReferencias guarda)
{
    public async Task<IReadOnlyList<CuentaTesoreriaListado>> ListarAsync(
        int? idEmpresa = null, CancellationToken ct = default)
    {
        var cuentas = db.CuentasTesoreria.AsQueryable();

        if (idEmpresa is { } filtro)
        {
            cuentas = cuentas.Where(c => c.IdEmpresa == filtro);
        }

        var filas = await Proyectar(cuentas.OrderBy(c => c.IdEmpresa).ThenBy(c => c.Nombre).ThenBy(c => c.Id))
            .ToListAsync(ct);

        return filas.Select(Armar).ToList();
    }

    public async Task<CuentaTesoreriaListado> ObtenerAsync(int id, CancellationToken ct = default)
    {
        var fila = await Proyectar(db.CuentasTesoreria.Where(c => c.Id == id)).FirstOrDefaultAsync(ct)
            ?? throw SinCuenta(id);

        return Armar(fila);
    }

    public async Task<CuentaTesoreriaListado> CrearAsync(AltaDeCuentaTesoreria datos, CancellationToken ct = default)
    {
        var nombre = ReglasDeCuentaTesoreria.NormalizarNombre(datos.Nombre);
        var idEmpresa = ReglasDeCuentaTesoreria.ExigirEmpresa(datos.IdEmpresa);
        var saldoInicial = ReglasDeCuentaTesoreria.ExigirSaldoInicialValido(datos.SaldoInicial);
        var activo = ReglasDeCuentaTesoreria.ExigirActivo(datos.Activo);

        // El filtro de tenant de EF esconde las empresas de otros tenants y las dadas de baja.
        if (!await db.Empresas.AnyAsync(e => e.Id == idEmpresa, ct))
        {
            throw new ErrorDominio("referencia_invalida", $"No existe la empresa {idEmpresa}.", 400);
        }

        await ExigirNombreLibreAsync(idEmpresa, nombre, excluirId: null, ct);

        var ahora = reloj.Ahora;
        var cuenta = new CuentaTesoreria
        {
            IdEmpresa = idEmpresa,
            Nombre = nombre,
            SaldoInicial = saldoInicial,
            Activo = activo,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };

        db.CuentasTesoreria.Add(cuenta);
        await db.SaveChangesAsync(ct);

        return await ObtenerAsync(cuenta.Id, ct);
    }

    /// <summary>
    /// Sin reintento: la cuenta se lee UNA sola vez, dentro de la transacción y después del
    /// <c>FOR UPDATE</c> de su fila. El primer movimiento de la cuenta toma <c>FOR KEY SHARE</c> sobre esa
    /// fila al validar su FK, así que o se comitea antes y esta consulta lo ve, o espera a que esta
    /// transacción termine: el saldo inicial no puede cambiar bajo una cadena ya iniciada. El orden es
    /// 404, validación del cuerpo, nombre repetido y regla del saldo inicial.
    /// </summary>
    public async Task<CuentaTesoreriaListado> ActualizarAsync(
        int id, EdicionDeCuentaTesoreria datos, CancellationToken ct = default)
    {
        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            await guarda.BloquearFilaAsync<CuentaTesoreria>(id, ct);

            var cuenta = await db.CuentasTesoreria.FirstOrDefaultAsync(c => c.Id == id, ct)
                ?? throw SinCuenta(id);

            var nombre = ReglasDeCuentaTesoreria.NormalizarNombre(datos.Nombre);
            var saldoInicial = ReglasDeCuentaTesoreria.ExigirSaldoInicialValido(datos.SaldoInicial);
            var activo = ReglasDeCuentaTesoreria.ExigirActivo(datos.Activo);

            await ExigirNombreLibreAsync(cuenta.IdEmpresa, nombre, excluirId: id, ct);

            if (saldoInicial != cuenta.SaldoInicial
                && await db.MovimientosCuenta.AnyAsync(m => m.IdCuentaTesoreria == id, ct))
            {
                throw ErrorDominio.Conflicto(
                    "saldo_inicial_con_movimientos",
                    "El saldo inicial no se puede modificar porque la cuenta ya tiene movimientos.");
            }

            cuenta.Nombre = nombre;
            cuenta.SaldoInicial = saldoInicial;
            cuenta.Activo = activo;
            cuenta.UpdatedAt = reloj.Ahora;

            await db.SaveChangesAsync(ct);
            await transaccion.CommitAsync(ct);
        });

        return await ObtenerAsync(id, ct);
    }

    /// <summary>Baja lógica, solo si ninguna fila referencia la cuenta (medios de pago o movimientos):
    /// el lock de la fila va antes de la lectura y de la consulta de referencias, sin reintento,
    /// como en la baja de personal.</summary>
    public async Task EliminarAsync(int id, CancellationToken ct = default)
    {
        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            await guarda.BloquearFilaAsync<CuentaTesoreria>(id, ct);

            var cuenta = await db.CuentasTesoreria.FirstOrDefaultAsync(c => c.Id == id, ct)
                ?? throw SinCuenta(id);

            await guarda.ExigirSinReferenciasAsync(
                cuenta, "cuenta_tesoreria_en_uso", "la cuenta de tesorería", etiquetasPropias: null, ct);

            var ahora = reloj.Ahora;
            cuenta.DeletedAt = ahora;
            cuenta.UpdatedAt = ahora;

            await db.SaveChangesAsync(ct);
            await transaccion.CommitAsync(ct);
        });
    }

    private static ErrorDominio SinCuenta(int id) =>
        ErrorDominio.NoEncontrado($"No existe la cuenta de tesorería {id}.");

    private async Task ExigirNombreLibreAsync(int idEmpresa, string nombre, int? excluirId, CancellationToken ct)
    {
        if (await db.CuentasTesoreria.AnyAsync(
                c => c.IdEmpresa == idEmpresa && c.Nombre == nombre && c.Id != excluirId, ct))
        {
            throw ErrorDominio.Conflicto(
                "cuenta_tesoreria_duplicada", $"Ya existe una cuenta de tesorería '{nombre}' en esta empresa.");
        }
    }

    private sealed record FilaDeCuenta(
        CuentaTesoreria Cuenta, string? NombreEmpresa, decimal? UltimoFinal, bool TieneMovimientos);

    /// <summary>Una sola consulta: el saldo actual y la existencia de movimientos salen de
    /// subconsultas correlacionadas por cuenta. La empresa va por LEFT JOIN: una empresa dada de
    /// baja deja la fila sin nombre de empresa en vez de ocultarla.</summary>
    private IQueryable<FilaDeCuenta> Proyectar(IQueryable<CuentaTesoreria> cuentas) =>
        from c in cuentas
        join e in db.Empresas on c.IdEmpresa equals e.Id into empresas
        from e in empresas.DefaultIfEmpty()
        select new FilaDeCuenta(
            c,
            e == null ? null : e.NombreFantasia ?? e.RazonSocial,
            db.MovimientosCuenta
                .Where(m => m.IdCuentaTesoreria == c.Id)
                .OrderByDescending(m => m.Id)
                .Select(m => (decimal?)m.Final)
                .FirstOrDefault(),
            db.MovimientosCuenta.Any(m => m.IdCuentaTesoreria == c.Id));

    private static CuentaTesoreriaListado Armar(FilaDeCuenta fila) =>
        new(
            fila.Cuenta.Id,
            fila.Cuenta.IdEmpresa,
            fila.NombreEmpresa,
            fila.Cuenta.Nombre,
            fila.Cuenta.SaldoInicial,
            fila.UltimoFinal ?? fila.Cuenta.SaldoInicial,
            fila.Cuenta.Activo,
            SaldoInicialEditable: !fila.TieneMovimientos);
}
