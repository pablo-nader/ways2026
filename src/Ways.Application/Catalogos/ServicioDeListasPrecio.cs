using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;

namespace Ways.Application.Catalogos;

/// <summary>
/// Escape hatch de ADR-11 (design decision 2, stage-3-articulos-y-precios Slice 4): mismo
/// criterio que <see cref="ServicioDeCategorias"/> — <c>listas_precio</c> reusa
/// <see cref="ServicioDeCatalogo{T,TListado,TAlta}"/> para list/get y para el resto de
/// alta/edición/baja, y agrega acá las cuatro protecciones propias de esta lista (spec:
/// listas-precio-minimal delta; state.yaml, obligación heredada de la Slice 3):
///
/// <list type="bullet">
/// <item>Una <c>derivada</c> exige <c>id_lista_base</c>/<c>porcentaje</c>, profundidad 1 (la
/// base no puede ser a su vez <c>derivada</c>) y <c>porcentaje &gt; -100</c> (obligación
/// heredada de <see cref="Domain.Precios.ResolvedorDePrecios.ResolverPrecioDerivado"/>, que
/// hasta ahora era la única guarda — esta escritura la vuelve inalcanzable en operación
/// normal, la de lectura queda como defensa en profundidad).</item>
/// <item>El cambio de <c>modo</c> (<c>fija</c> ↔ <c>derivada</c>) se bloquea si la lista ya
/// tiene historial de <c>precios</c>.</item>
/// <item>La desactivación (edición a <c>Activo: false</c> o baja lógica) se bloquea mientras
/// una <c>derivada</c> ACTIVA la referencia como <c>id_lista_base</c>.</item>
/// <item><c>EsDefault</c> tiene semántica de INTERCAMBIO, no de flag suelto: asignarlo a una
/// lista nueva desmarca automáticamente la que lo tenía en el mismo alcance
/// (<c>IdEmpresa</c>) — nunca se puede quedar en cero ni en dos (spec heredado de stage 2,
/// "One Default List Per Tenant", sin modificar por este delta). Esto es lo que vuelve
/// alcanzables por un cliente HTTP, por primera vez, los dos índices únicos parciales
/// <c>ux_listas_precio_default_compartido/empresa</c> (db-error-backstops) — antes solo los
/// escribía el seed de aprovisionamiento.</item>
/// </list>
///
/// Autorización: <c>Politicas.GestionDeCatalogo</c>, igual que el resto de los catálogos de
/// tenant (aplicada en <c>CatalogosEndpoints.MapearCatalogo</c>, no acá).
/// </summary>
public class ServicioDeListasPrecio(IWaysDbContext db, IRelojDelSistema reloj)
    : ServicioDeCatalogo<ListaPrecio, ListaPrecioListado, ListaPrecioAlta>(db, reloj)
{
    protected override DbSet<ListaPrecio> Conjunto => Db.ListasPrecio;

    protected override ListaPrecioListado Proyectar(ListaPrecio entidad) => new(
        entidad.Id, entidad.Nombre, entidad.Activo, entidad.IdEmpresa,
        entidad.EsDefault, entidad.Modo, entidad.IdListaBase, entidad.Porcentaje);

    protected override ListaPrecio Instanciar(string nombre, int? idEmpresa, bool activo, DateTimeOffset ahora) =>
        new() { Nombre = nombre, IdEmpresa = idEmpresa, Activo = activo, CreatedAt = ahora, UpdatedAt = ahora };

    protected override void AplicarPropios(ListaPrecio entidad, ListaPrecioAlta datos)
    {
        entidad.Modo = datos.Modo;
        entidad.IdListaBase = datos.IdListaBase;
        entidad.Porcentaje = datos.Porcentaje;
        entidad.EsDefault = datos.EsDefault;
    }

    public override async Task<ListaPrecioListado> CrearAsync(ListaPrecioAlta datos, CancellationToken ct = default)
    {
        await ValidarModoAsync(datos.Modo, datos.IdListaBase, datos.Porcentaje, idPropio: null, ct);
        ExigirDefaultConsistente(datos.EsDefault, datos.Activo);

        if (!datos.EsDefault)
        {
            return await base.CrearAsync(datos, ct);
        }

        // El INTERCAMBIO necesita transacción explícita (mismo patrón que
        // ServicioDeArticulos.CrearAsync/ServicioDePrecios.AbrirNuevoPrecioAsync): dos
        // SaveChangesAsync en el orden correcto (desmarcar la vieja ANTES de guardar la
        // nueva) tienen que ser atómicos entre sí — si el segundo falla (p.ej. nombre
        // duplicado), el tenant no puede quedar sin ninguna lista default.
        //
        // Sin reintento: `base.CrearAsync` hace Add de un Conjunto NUEVO en cada intento y no hay
        // clave de idempotencia — un reintento daría de alta una segunda lista.
        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(Db);
        return await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await Db.Database.BeginTransactionAsync(ct);

            await DesmarcarDefaultActualAsync(datos.IdEmpresa, excluirId: null, ct);
            var creada = await base.CrearAsync(datos, ct);

            await transaccion.CommitAsync(ct);
            return creada;
        });
    }

    public override async Task<ListaPrecioListado> ActualizarAsync(
        int id, ListaPrecioAlta datos, CancellationToken ct = default)
    {
        var actual = await BuscarAsync(id, ct);

        if (datos.Modo != actual.Modo)
        {
            await ExigirSinHistorialDePreciosAsync(id, ct);
        }

        await ValidarModoAsync(datos.Modo, datos.IdListaBase, datos.Porcentaje, idPropio: id, ct);
        ExigirDefaultConsistente(datos.EsDefault, datos.Activo);

        if (actual.Activo && !datos.Activo)
        {
            await ExigirSinDependientesActivosAsync(id, ct);
        }

        if (actual.EsDefault && datos.IdEmpresa != actual.IdEmpresa)
        {
            // Protección de la FUENTE (judgment-day ronda 1, stage-3 slice 4): si se
            // permitiera mover IdEmpresa de una fila que hoy es default, el alcance de
            // ORIGEN se quedaría sin ninguna lista default (el intercambio de abajo solo
            // desmarca en el alcance DESTINO). Se elige la opción simple del veredicto:
            // prohibir el cambio de alcance mientras EsDefault=true — primero hay que
            // desmarcarla (lo que ya exige asignar el default a otra lista del mismo
            // alcance, guarda de abajo) y recién en un PUT posterior mover IdEmpresa.
            throw ErrorDominio.Conflicto(
                "lista_default_requiere_reemplazo",
                "No se puede cambiar el alcance de una lista default; primero asigná el default a otra lista del "
                + "alcance de origen.");
        }

        if (actual.EsDefault && !datos.EsDefault)
        {
            // Spec heredado de stage 2 ("One Default List Per Tenant"): sin esta guarda, un
            // PUT que solo edita el nombre pero copia EsDefault=false de un formulario
            // desactualizado dejaría el tenant (o la empresa) sin ninguna lista default.
            throw ErrorDominio.Conflicto(
                "lista_default_requiere_reemplazo",
                "No se puede quitar el estado default sin asignarlo a otra lista en el mismo alcance primero.");
        }

        if (!actual.EsDefault && datos.EsDefault)
        {
            // El INTERCAMBIO se dispara al PROMOVER a default una fila que hoy no lo es —
            // esto cubre tanto la promoción simple (mismo alcance) como la promoción
            // combinada con un cambio de alcance (compartida -> empresa, empresa ->
            // compartida, empresa A -> empresa B), porque la guarda de arriba ya rechazó
            // cualquier cambio de alcance de una fila que HOY es default: si se llegó
            // hasta acá con datos.IdEmpresa != actual.IdEmpresa, es porque actual.EsDefault
            // era false. El alcance a desmarcar es siempre el DESTINO (datos.IdEmpresa,
            // el alcance donde la fila va a terminar), nunca el de origen.
            var estrategia = Db.Database.CreateExecutionStrategy();
            return await estrategia.ExecuteAsync(async () =>
            {
                await using var transaccion = await Db.Database.BeginTransactionAsync(ct);

                await DesmarcarDefaultActualAsync(datos.IdEmpresa, excluirId: id, ct);
                var actualizada = await base.ActualizarAsync(id, datos, ct);

                await transaccion.CommitAsync(ct);
                return actualizada;
            });
        }

        return await base.ActualizarAsync(id, datos, ct);
    }

    /// <summary>Baja lógica: además de la guarda heredada de dependientes activos (misma que
    /// la desactivación), una lista default nunca se puede eliminar — no hay ningún alta que
    /// la reemplace atómicamente en la misma operación (a diferencia de <see
    /// cref="ActualizarAsync"/>, que sí puede recibir el intercambio en el mismo request).
    /// Mismo criterio de "fila protegida" que <c>ReglaDeClientes.ValidarNoConsumidorFinal</c>,
    /// aplicado acá al estado <c>EsDefault</c> en vez de a una fila fija por convención
    /// (<c>numero = 1</c>) — cualquier lista puede llegar a ser la protegida, no solo
    /// "General".</summary>
    public override async Task EliminarAsync(int id, CancellationToken ct = default)
    {
        var actual = await BuscarAsync(id, ct);

        if (actual.EsDefault)
        {
            throw ErrorDominio.Conflicto(
                "lista_default_no_se_puede_eliminar",
                "No se puede eliminar la lista default; asigná el estado default a otra lista primero.");
        }

        await ExigirSinDependientesActivosAsync(id, ct);

        await base.EliminarAsync(id, ct);
    }

    /// <summary>Spec: "Derivada Mode Resolution And Validation" + orchestrator decision 2
    /// (profundidad 1, tasks.md) + state.yaml (obligación heredada de la Slice 3: rechazar
    /// <c>porcentaje &lt;= -100</c> acá para que la guarda de lectura de
    /// <c>ResolvedorDePrecios.ResolverPrecioDerivado</c> deje de ser alcanzable en operación
    /// normal).</summary>
    private async Task ValidarModoAsync(
        ModoLista modo, int? idListaBase, decimal? porcentaje, int? idPropio, CancellationToken ct)
    {
        if (modo == ModoLista.Fija)
        {
            if (idListaBase is not null || porcentaje is not null)
            {
                throw new ErrorDominio(
                    "lista_fija_no_admite_base",
                    "Una lista fija no puede tener id_lista_base ni porcentaje.",
                    400);
            }

            return;
        }

        if (idListaBase is null || porcentaje is null)
        {
            throw new ErrorDominio(
                "lista_derivada_requiere_base",
                "Una lista derivada requiere id_lista_base y porcentaje.",
                400);
        }

        if (idPropio is not null && idListaBase == idPropio)
        {
            throw new ErrorDominio("lista_base_invalida", "Una lista no puede ser su propia base.", 400);
        }

        ExigirPorcentajeValido(porcentaje.Value);

        // Tenant-scoped por el filtro global de EF (doc 09) — cubre a la vez "no existe" y
        // "es de otro tenant" (spec: "id_lista_base must reference an existing lista of the
        // tenant"), mismo criterio que ServicioDeArticulos.ExigirAreaValidaAsync.
        var listaBase = await Db.ListasPrecio.FirstOrDefaultAsync(l => l.Id == idListaBase, ct)
            ?? throw new ErrorDominio("referencia_invalida", $"No existe la lista base {idListaBase}.", 400);

        if (listaBase.Modo != ModoLista.Fija)
        {
            // Mismo código de dominio que la defensa en profundidad de lectura
            // (ServicioDePrecios.ResolverPrecioAsync) — profundidad 1, orchestrator decision 2.
            throw new ErrorDominio(
                "lista_base_invalida",
                "La lista base de una lista derivada no puede ser a su vez derivada.",
                400);
        }
    }

    /// <summary>Columna <c>numeric(5,2)</c> — mismo bound de precisión que
    /// <c>ServicioDeArticulos.ExigirDescuentoProveedorValido</c>, con el piso corrido a -100
    /// (exclusivo) en vez de 0: un <c>porcentaje</c> es un descuento (negativo) o un recargo
    /// (positivo) sobre la lista base, y -100 o menos da un precio derivado negativo o cero
    /// sin sentido de negocio (obligación heredada, ver el doc-comment de la clase).</summary>
    private static void ExigirPorcentajeValido(decimal valor)
    {
        if (valor <= -100m || valor >= 1000m)
        {
            throw new ErrorDominio(
                "porcentaje_invalido", "El campo porcentaje debe ser mayor a -100 y menor a 1000.", 400);
        }
    }

    private static void ExigirDefaultConsistente(bool esDefault, bool activo)
    {
        if (esDefault && !activo)
        {
            throw new ErrorDominio(
                "lista_default_debe_estar_activa", "Una lista default no puede quedar inactiva.", 400);
        }
    }

    /// <summary>Spec: "Blocked Mode Switch Once History Exists" — <c>db.Precios</c> ya
    /// filtra baja lógica vía el filtro global de EF, así que esto solo ve historial VIGENTE
    /// (no eliminado), igual criterio que el resto de los chequeos de "¿tiene historial?" del
    /// proyecto.</summary>
    private async Task ExigirSinHistorialDePreciosAsync(int id, CancellationToken ct)
    {
        if (await Db.Precios.AnyAsync(p => p.IdListaPrecio == id, ct))
        {
            throw ErrorDominio.Conflicto(
                "lista_modo_bloqueado_por_historial",
                "No se puede cambiar el modo de una lista que ya tiene precios registrados.");
        }
    }

    /// <summary>Spec: "Blocked Deactivation While Referenced As Base" — mirror de
    /// <c>ReglaDeClientes.ValidarNoConsumidorFinal</c> en forma (design.md: Protection
    /// Rules), aplicado a un predicado en vez de a un id fijo.</summary>
    private async Task ExigirSinDependientesActivosAsync(int id, CancellationToken ct)
    {
        if (await Db.ListasPrecio.AnyAsync(l => l.IdListaBase == id && l.Activo, ct))
        {
            throw ErrorDominio.Conflicto(
                "lista_referenciada_como_base",
                "No se puede desactivar una lista referenciada como base por una lista derivada activa.");
        }
    }

    /// <summary>El INTERCAMBIO en sí: desmarca la fila que hoy es default en el mismo alcance
    /// (<paramref name="idEmpresa"/> — <c>NULL</c> es el alcance compartido del tenant, un
    /// valor es el alcance propio de esa empresa, mismo criterio que
    /// <c>ux_listas_precio_default_compartido/empresa</c>) y guarda ANTES de que el llamador
    /// guarde la fila nueva — el orden es lo que hace que el índice único parcial nunca vea
    /// dos filas <c>es_default = true</c> a la vez dentro de la MISMA transacción (db-error-
    /// backstops: bajo dos ediciones concurrentes que compiten por el mismo intercambio, la
    /// fila compartida que este método toca serializa a los dos escritores por el lock de fila
    /// de Postgres — el segundo, al reanudar, ve la primera ya comiteada y su propio INSERT/
    /// UPDATE de la fila nueva choca contra el índice único con un 23505 genuino, traducido a
    /// 409 <c>default_duplicado</c> por <c>ManejadorDeErrores</c> — no hace falta ningún lock
    /// explícito adicional, a diferencia de <c>ServicioDePrecios</c>).</summary>
    private async Task DesmarcarDefaultActualAsync(int? idEmpresa, int? excluirId, CancellationToken ct)
    {
        var actual = await Db.ListasPrecio.FirstOrDefaultAsync(
            l => l.IdEmpresa == idEmpresa && l.EsDefault && l.Id != excluirId, ct);

        if (actual is null)
        {
            return;
        }

        actual.EsDefault = false;
        actual.UpdatedAt = Reloj.Ahora;

        await Db.SaveChangesAsync(ct);
    }
}
