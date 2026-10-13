using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Bajas;
using Ways.Domain.Usuarios;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;

namespace Ways.Application.Catalogos;

/// <summary>
/// Además del catálogo genérico, administra la cuenta de tesorería, la comisión y las retenciones del medio. Las reglas
/// de valores y de comportamiento son puras (<see cref="ReglasDeCuentaDeMedioPago"/>); acá se agrega lo que necesita
/// la base: que la cuenta exista, sea de la empresa del medio y esté activa si es nueva. El alta y la edición corren sin
/// reintento y bajo <c>FOR KEY SHARE</c> sobre la fila viva de la cuenta, que choca contra el <c>FOR UPDATE</c> de su
/// baja: o la baja gana y la cuenta ya no existe para esta escritura, o espera y ve la referencia. La cuenta se lee una
/// sola vez, dentro de la transacción y después del lock, que se toma al final: después de las validaciones baratas del
/// flujo genérico (<see cref="AntesDeGuardarAsync"/>). El listado trae el nombre de la cuenta en la misma consulta.
///
/// La cuenta, la comisión y las retenciones son datos de tesorería que solo ve quien administra el catálogo (Admin);
/// para los demás roles —el POS y las pantallas de operación leen este listado— salen en null. El mecanismo es el de los
/// costos (<c>PuedeVerCostos</c>: el servicio pone null según el rol), pero los roles no son los mismos: acá solo el Admin
/// ve los campos, mientras que los costos también los ve el Supervisor.
/// </summary>
public class ServicioDeMediosPago(
    IWaysDbContext db, IRelojDelSistema reloj, GuardaDeReferencias guarda, IContextoDeUsuario contexto)
    : ServicioDeCatalogo<MedioPago, MedioPagoListado, MedioPagoAlta>(db, reloj, guarda)
{
    // Copia del parámetro en un campo (no una captura): evita CS9107.
    private readonly GuardaDeReferencias guardaDeCuentas = guarda;

    private bool PuedeVerLaCuenta => contexto.Rol is RolConocido.Admin;

    protected override DbSet<MedioPago> Conjunto => Db.MediosPago;

    protected override string CodigoEnUso => "medio_pago_en_uso";

    protected override string SujetoDeBaja => "el medio de pago";

    protected override MedioPagoListado Proyectar(MedioPago entidad) => new(
        entidad.Id, entidad.Nombre, entidad.Activo, entidad.IdEmpresa, entidad.Orden,
        entidad.Comportamiento, entidad.AdmiteVuelto, entidad.RequiereReferencia, entidad.RecargoPorcentaje,
        entidad.PorcentajeFacturacion, entidad.IdCuentaTesoreria, entidad.ComisionPorcentaje,
        entidad.RetencionIibbPorcentaje, entidad.RetencionIvaPorcentaje, entidad.RetencionGananciasPorcentaje);

    protected override MedioPago Instanciar(string nombre, int? idEmpresa, bool activo, DateTimeOffset ahora) =>
        new() { Nombre = nombre, IdEmpresa = idEmpresa, Activo = activo, CreatedAt = ahora, UpdatedAt = ahora };

    protected override void AplicarPropios(MedioPago entidad, MedioPagoAlta datos)
    {
        // Ausente conserva lo guardado: un cliente que no conoce el campo no puede pisarlo con 0.
        var porcentaje = datos.PorcentajeFacturacion ?? entidad.PorcentajeFacturacion;
        ExigirPorcentajeDeFacturacionValido(porcentaje);
        var cuenta = ResolverCuenta(entidad, datos);

        entidad.Orden = datos.Orden;
        entidad.Comportamiento = datos.Comportamiento;
        entidad.AdmiteVuelto = datos.AdmiteVuelto;
        entidad.RequiereReferencia = datos.RequiereReferencia;
        entidad.RecargoPorcentaje = datos.RecargoPorcentaje;
        entidad.PorcentajeFacturacion = porcentaje;
        entidad.IdCuentaTesoreria = cuenta.IdCuentaTesoreria;
        entidad.ComisionPorcentaje = cuenta.Comision;
        entidad.RetencionIibbPorcentaje = cuenta.RetencionIibb;
        entidad.RetencionIvaPorcentaje = cuenta.RetencionIva;
        entidad.RetencionGananciasPorcentaje = cuenta.RetencionGanancias;
    }

    /// <summary>Entre 0 y 100 con hasta 2 decimales, que es lo que guarda <c>numeric(5,2)</c>: más
    /// decimales se rechazan en vez de redondearse en silencio. El backstop de rango es
    /// <c>ck_medios_pago_porcentaje_facturacion</c>.</summary>
    private static void ExigirPorcentajeDeFacturacionValido(decimal porcentaje)
    {
        if (porcentaje < 0m || porcentaje > 100m || porcentaje != Math.Round(porcentaje, 2))
        {
            throw new ErrorDominio(
                "porcentaje_facturacion_invalido",
                "El porcentaje a facturar tiene que estar entre 0 y 100, con hasta 2 decimales.",
                400);
        }
    }

    private static CuentaDeMedioPago ResolverCuenta(MedioPago guardado, MedioPagoAlta datos) =>
        ReglasDeCuentaDeMedioPago.Resolver(
            datos.Comportamiento,
            new CuentaDeMedioPago(
                guardado.IdCuentaTesoreria, guardado.ComisionPorcentaje, guardado.RetencionIibbPorcentaje,
                guardado.RetencionIvaPorcentaje, guardado.RetencionGananciasPorcentaje),
            new CuentaEnviadaDeMedioPago(
                datos.IdCuentaTesoreria.Presente, datos.IdCuentaTesoreria.Valor, datos.ComisionPorcentaje,
                datos.RetencionIibbPorcentaje, datos.RetencionIvaPorcentaje, datos.RetencionGananciasPorcentaje));

    private sealed record FilaDeMedio(MedioPago Medio, string? NombreCuenta, bool CuentaVisible, bool CuentaActiva);


    /// <summary>Una sola consulta: la cuenta va por LEFT JOIN, así que una cuenta dada de baja deja el medio listado
    /// "sin cuenta" (id en null) en vez de ocultarlo.</summary>
    private IQueryable<FilaDeMedio> ConCuenta(IQueryable<MedioPago> medios) =>
        from m in medios
        join c in Db.CuentasTesoreria on m.IdCuentaTesoreria equals (int?)c.Id into cuentas
        from c in cuentas.DefaultIfEmpty()
        select new FilaDeMedio(m, c == null ? null : c.Nombre, c != null, c != null && c.Activo);

    /// <summary>Arma primero lo que ve el Admin y después, para el resto de los roles, lo oculta campo por campo.</summary>
    private MedioPagoListado Armar(FilaDeMedio fila)
    {
        var listado = Proyectar(fila.Medio);

        var delAdmin = fila.CuentaVisible
            ? listado with { NombreCuentaTesoreria = fila.NombreCuenta, CuentaTesoreriaInactiva = !fila.CuentaActiva }
            : listado with { IdCuentaTesoreria = null, CuentaTesoreriaInactiva = false };

        return PuedeVerLaCuenta
            ? delAdmin
            : delAdmin with
            {
                IdCuentaTesoreria = null,
                NombreCuentaTesoreria = null,
                CuentaTesoreriaInactiva = null,
                ComisionPorcentaje = null,
                RetencionIibbPorcentaje = null,
                RetencionIvaPorcentaje = null,
                RetencionGananciasPorcentaje = null
            };
    }

    public override async Task<IReadOnlyList<MedioPagoListado>> ListarAsync(
        bool incluirInactivos = false, CancellationToken ct = default)
    {
        var medios = Conjunto.AsQueryable();

        if (!incluirInactivos)
        {
            medios = medios.Where(m => m.Activo);
        }

        var filas = await ConCuenta(medios.OrderBy(m => m.Nombre)).ToListAsync(ct);
        return filas.Select(Armar).ToList();
    }

    public override async Task<MedioPagoListado> ObtenerAsync(int id, CancellationToken ct = default)
    {
        var fila = await ConCuenta(Conjunto.Where(m => m.Id == id)).FirstOrDefaultAsync(ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el recurso {id}.");

        return Armar(fila);
    }

    public override async Task<MedioPagoListado> CrearAsync(MedioPagoAlta datos, CancellationToken ct = default)
    {
        var id = await GuardarAsync(null, datos, ct);
        return await ObtenerAsync(id, ct);
    }

    public override async Task<MedioPagoListado> ActualizarAsync(
        int id, MedioPagoAlta datos, CancellationToken ct = default)
    {
        await GuardarAsync(id, datos, ct);
        return await ObtenerAsync(id, ct);
    }

    /// <summary>Sin reintento: el alta no tiene clave de idempotencia y el lock de la cuenta vive en la transacción. El
    /// orden de las validaciones es el del flujo genérico (404, nombre, disponibilidad, porcentaje de facturación y reglas
    /// puras de la cuenta); el lock de la cuenta llega al final, en <see cref="AntesDeGuardarAsync"/>.</summary>
    private async Task<int> GuardarAsync(int? id, MedioPagoAlta datos, CancellationToken ct)
    {
        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(Db);

        return await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await Db.Database.BeginTransactionAsync(ct);

            var guardado = id is { } existente
                ? await base.ActualizarAsync(existente, datos, ct)
                : await base.CrearAsync(datos, ct);

            await transaccion.CommitAsync(ct);
            return guardado.Id;
        });
    }

    /// <summary>Si ni la cuenta ni la empresa del medio cambian, no hay nada nuevo que validar ni que bloquear.
    /// "Nueva" es una cuenta distinta de la guardada: solo esa debe estar activa.</summary>
    protected override async Task AntesDeGuardarAsync(MedioPago entidad, CancellationToken ct)
    {
        if (entidad.IdCuentaTesoreria is not { } idCuenta)
        {
            return;
        }

        var entrada = Db.ChangeTracker.Entries<MedioPago>().FirstOrDefault(e => ReferenceEquals(e.Entity, entidad));
        var esNuevo = entrada is null || entrada.State is EntityState.Added or EntityState.Detached;
        var cuentaGuardada = esNuevo ? null : entrada!.Property(m => m.IdCuentaTesoreria).OriginalValue;
        var empresaGuardada = esNuevo ? null : entrada!.Property(m => m.IdEmpresa).OriginalValue;

        if (!esNuevo && idCuenta == cuentaGuardada && entidad.IdEmpresa == empresaGuardada)
        {
            return;
        }

        await ExigirCuentaAsignableAsync(idCuenta, entidad.IdEmpresa, asignacionNueva: idCuenta != cuentaGuardada, ct);
    }

    private async Task ExigirCuentaAsignableAsync(
        int idCuenta, int? idEmpresaDelMedio, bool asignacionNueva, CancellationToken ct)
    {
        if (!await guardaDeCuentas.BloquearSiEstaVivaAsync<CuentaTesoreria>(idCuenta, ct))
        {
            throw new ErrorDominio("referencia_invalida", $"No existe la cuenta de tesorería {idCuenta}.", 400);
        }

        var cuenta = await Db.CuentasTesoreria
            .Where(c => c.Id == idCuenta)
            .Select(c => new CuentaAsignable(c.IdEmpresa, c.Activo))
            .FirstAsync(ct);

        int? idUnicaEmpresa = null;
        if (idEmpresaDelMedio is null)
        {
            var empresas = await Db.Empresas.Select(e => e.Id).Take(2).ToListAsync(ct);
            idUnicaEmpresa = empresas.Count == 1 ? empresas[0] : null;
        }

        ReglasDeCuentaDeMedioPago.ExigirCuentaAsignable(idEmpresaDelMedio, cuenta, asignacionNueva, idUnicaEmpresa);
    }
}
