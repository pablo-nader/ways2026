using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Precios;

namespace Ways.Application.Precios;

/// <summary>
/// Motor de historial de precios (design decisions 3/4, tasks 3.2/3.3): el único punto de
/// escritura de <c>precios</c> es <see cref="AbrirNuevoPrecioAsync"/> — cierra la fila
/// actualmente abierta (si hay una) e inserta una nueva, siempre en la MISMA transacción, nunca
/// hay un <c>Update</c> sobre <see cref="Precio.Monto"/> de una fila existente. La lectura
/// (<see cref="PrecioVigenteAsync"/>) resuelve <c>fija</c> por consulta filtrada por fecha y
/// <c>derivada</c> en el momento, sin persistir nunca una fila para una lista derivada (spec:
/// Derived List Price Resolution At Read Time).
///
/// Autorización: <c>Politicas.GestionDeCatalogo</c> aplicada en la capa de API, mismo criterio
/// que <see cref="Articulos.ServicioDeArticulos"/>.
/// </summary>
public class ServicioDePrecios(IWaysDbContext db, IRelojDelSistema reloj, IContextoDeUsuario contexto)
{
    /// <summary>Tolerancia de desfasaje de reloj entre cliente y servidor para "vigente_desde no
    /// puede estar en el pasado" (spec: Programmable Future Prices — el spec no fija un número;
    /// 30 segundos es una decisión de esta capa de servicio, documentada acá porque no hay una
    /// cifra más autoritativa que citar) — sin esto, un cliente que arma "ahora + 1 segundo" y
    /// tarda en llegar a la red rechazaría de forma espuria.</summary>
    private static readonly TimeSpan ToleranciaReloj = TimeSpan.FromSeconds(30);

    /// <summary>Establece el precio vigente AHORA (spec: Price History Never Overwrites,
    /// "Changing a price closes the old row and opens a new one") — <c>vigente_desde</c> siempre
    /// es <c>reloj.Ahora</c>, nunca provisto por el cliente.</summary>
    public Task<PrecioVigente> EstablecerPrecioAsync(int idArticulo, AltaPrecio datos, CancellationToken ct = default) =>
        AbrirNuevoPrecioAsync(idArticulo, datos.IdListaPrecio, datos.Precio, reloj.Ahora, datos.ConfirmarReemplazo, ct);

    /// <summary>Programa un precio a futuro (spec: Programmable Future Prices) —
    /// <see cref="ProgramarPrecio.VigenteDesde"/> tiene que ser una fecha futura antes de entrar
    /// a la transacción de <see cref="AbrirNuevoPrecioAsync"/>.</summary>
    public Task<PrecioVigente> ProgramarPrecioAsync(int idArticulo, ProgramarPrecio datos, CancellationToken ct = default)
    {
        ExigirVigenteDesdeFuturo(datos.VigenteDesde);
        return AbrirNuevoPrecioAsync(idArticulo, datos.IdListaPrecio, datos.Precio, datos.VigenteDesde, datos.ConfirmarReemplazo, ct);
    }

    /// <summary>
    /// Design decision 3/4 — única fila de escritura de <c>precios</c>. Dentro de una única
    /// transacción: bloquea (<c>SELECT ... FOR UPDATE</c>, vía ADO.NET crudo — EF Core/Npgsql no
    /// tiene un equivalente mapeado) la fila actualmente abierta del par
    /// <c>(idArticulo, idListaPrecio)</c> si existe, decide si hace falta confirmación (fila
    /// pendiente — <c>vigente_desde &gt; ahora</c> — sin <paramref name="confirmarReemplazo"/>),
    /// la cierra, e inserta la nueva fila abierta.
    ///
    /// Cuando NO hay fila abierta (primer precio del par) no hay nada que bloquear — dos altas
    /// concurrentes compiten recién en el <c>INSERT</c>, contra <c>ux_precios_vigente</c>
    /// (backstop real, <c>ManejadorDeErrores</c> → 409 <c>precio_vigente_duplicado</c>, task
    /// 3.11). Esa es la carrera GENUINA de este backstop — a diferencia de
    /// <c>ux_articulos_codigo_interno</c>'s camino autogenerado, acá no hay ningún contador ni
    /// lock de fila que la evite por construcción.
    /// </summary>
    public async Task<PrecioVigente> AbrirNuevoPrecioAsync(
        int idArticulo, int idListaPrecio, decimal precio, DateTimeOffset vigenteDesde, bool confirmarReemplazo,
        CancellationToken ct = default)
    {
        await BuscarArticuloAsync(idArticulo, ct);
        await BuscarListaFijaAsync(idListaPrecio, ct);
        ExigirPrecioValido(precio);

        var idTenant = ExigirTenantDeLaSesion();

        var estrategia = db.Database.CreateExecutionStrategy();

        return await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            var filaAbierta = await BloquearFilaVigenteAsync(idArticulo, idListaPrecio, idTenant, ct);
            var ahora = reloj.Ahora;

            if (filaAbierta is { } fila)
            {
                var esPendiente = fila.VigenteDesde > ahora;

                if (esPendiente && !confirmarReemplazo)
                {
                    throw ErrorDominio.Conflicto(
                        "precio_pendiente_existe",
                        "Ya existe un precio pendiente para este artículo en esta lista; confirmá el reemplazo.");
                }

                if (!esPendiente && vigenteDesde < fila.VigenteDesde)
                {
                    throw new ErrorDominio(
                        "vigente_desde_invalido",
                        "vigente_desde no puede ser anterior al del precio vigente actual.",
                        400);
                }

                // Reemplazo de una fila PENDIENTE: se cierra en su PROPIO vigente_desde (ventana
                // vacía, vigente_hasta == vigente_desde), no en el vigente_desde de la fila
                // nueva. Si se cerrara ahí, un reemplazo con una fecha nueva POSTERIOR a la
                // original dejaría al precio reemplazado brevemente "vigente" entre su fecha
                // original y la fecha nueva — exactamente lo que "reemplazado" dice que NO tiene
                // que pasar (spec: "the $150 pending row is REPLACED by the $160 one", no
                // "activo hasta que el nuevo empiece"). Para la fila ACTIVA (no pendiente) el
                // criterio es el opuesto y correcto: se cierra en el vigente_desde de la fila
                // nueva, porque esa fila SÍ estuvo vigente hasta ese momento (spec: "the $100
                // row's vigente_hasta is set to the new row's vigente_desde").
                var vigenteHastaDeLaFilaCerrada = esPendiente ? fila.VigenteDesde : vigenteDesde;

                await CerrarFilaAsync(fila.Id, vigenteHastaDeLaFilaCerrada, ahora, ct);
            }

            db.Precios.Add(new Precio
            {
                IdArticulo = idArticulo,
                IdListaPrecio = idListaPrecio,
                Monto = precio,
                VigenteDesde = vigenteDesde,
                VigenteHasta = null,
                CreatedAt = ahora,
                UpdatedAt = ahora
            });

            await db.SaveChangesAsync(ct);
            await transaccion.CommitAsync(ct);

            return new PrecioVigente(idArticulo, idListaPrecio, precio, vigenteDesde);
        });
    }

    /// <summary>Precio vigente de UN artículo en UNA lista a una fecha (spec: Current-Price
    /// Query Semantics By Date; Derived List Price Resolution At Read Time). <paramref
    /// name="fecha"/> por defecto es <c>reloj.Ahora</c>.</summary>
    public async Task<PrecioVigente> PrecioVigenteAsync(
        int idArticulo, int idListaPrecio, DateTimeOffset? fecha, CancellationToken ct = default)
    {
        await BuscarArticuloAsync(idArticulo, ct);
        var lista = await BuscarListaAsync(idListaPrecio, ct);

        return await ResolverPrecioAsync(idArticulo, lista, fecha ?? reloj.Ahora, ct);
    }

    /// <summary>Precio vigente de un artículo en TODAS las listas activas del tenant a una fecha
    /// — endpoint "single artículo across listas" (scope de esta slice).</summary>
    public async Task<IReadOnlyList<PrecioVigente>> PreciosVigentesAsync(
        int idArticulo, DateTimeOffset? fecha, CancellationToken ct = default)
    {
        await BuscarArticuloAsync(idArticulo, ct);

        var fechaConsulta = fecha ?? reloj.Ahora;
        var listas = await db.ListasPrecio.Where(l => l.Activo).ToListAsync(ct);

        var resultado = new List<PrecioVigente>(listas.Count);
        foreach (var lista in listas)
        {
            resultado.Add(await ResolverPrecioAsync(idArticulo, lista, fechaConsulta, ct));
        }

        return resultado;
    }

    /// <summary>Historial completo (spec: Price History Never Overwrites, "Historical prices
    /// remain queryable") — solo tiene sentido para una lista <c>fija</c>: una <c>derivada</c>
    /// nunca tiene filas propias.</summary>
    public async Task<IReadOnlyList<HistorialDePrecio>> HistorialDePrecioAsync(
        int idArticulo, int idListaPrecio, CancellationToken ct = default)
    {
        await BuscarArticuloAsync(idArticulo, ct);
        await BuscarListaFijaAsync(idListaPrecio, ct);

        return await db.Precios
            .Where(p => p.IdArticulo == idArticulo && p.IdListaPrecio == idListaPrecio)
            .OrderByDescending(p => p.VigenteDesde)
            .Select(p => new HistorialDePrecio(p.Id, p.Monto, p.VigenteDesde, p.VigenteHasta))
            .ToListAsync(ct);
    }

    /// <summary>Resuelve <paramref name="lista"/>: <c>fija</c> ⇒ consulta directa por fecha;
    /// <c>derivada</c> ⇒ resuelve la base (guarda de profundidad 1, orchestrator decision 2 —
    /// la escritura la bloquea <c>ServicioDeListasPrecio</c> en la Slice 4; acá es defensa en
    /// profundidad en LECTURA, por si una fila inconsistente llega a existir) y aplica
    /// <see cref="ResolvedorDePrecios.ResolverPrecioDerivado"/>.</summary>
    private async Task<PrecioVigente> ResolverPrecioAsync(
        int idArticulo, ListaPrecio lista, DateTimeOffset fecha, CancellationToken ct)
    {
        if (lista.Modo == ModoLista.Fija)
        {
            var montoFijo = await ObtenerPrecioFijaAsync(idArticulo, lista.Id, fecha, ct);
            return new PrecioVigente(idArticulo, lista.Id, montoFijo, fecha);
        }

        var idListaBase = lista.IdListaBase
            ?? throw new InvalidOperationException(
                $"La lista {lista.Id} es derivada sin id_lista_base — invariante de ServicioDeListasPrecio (Slice 4) violado.");

        var listaBase = await db.ListasPrecio.FirstOrDefaultAsync(l => l.Id == idListaBase, ct)
            ?? throw new ErrorDominio("referencia_invalida", $"No existe la lista base {idListaBase}.", 400);

        if (listaBase.Modo != ModoLista.Fija)
        {
            throw new ErrorDominio(
                "lista_base_invalida",
                "La lista base de una lista derivada no puede ser a su vez derivada.",
                400);
        }

        var montoBase = await ObtenerPrecioFijaAsync(idArticulo, listaBase.Id, fecha, ct);

        var monto = montoBase is { } b
            ? ResolvedorDePrecios.ResolverPrecioDerivado(b, lista.Porcentaje!.Value)
            : (decimal?)null;

        return new PrecioVigente(idArticulo, lista.Id, monto, fecha);
    }

    private async Task<decimal?> ObtenerPrecioFijaAsync(
        int idArticulo, int idListaPrecio, DateTimeOffset fecha, CancellationToken ct) =>
        await db.Precios
            .Where(p =>
                p.IdArticulo == idArticulo && p.IdListaPrecio == idListaPrecio &&
                p.VigenteDesde <= fecha && (p.VigenteHasta == null || p.VigenteHasta > fecha))
            .OrderByDescending(p => p.VigenteDesde)
            .Select(p => (decimal?)p.Monto)
            .FirstOrDefaultAsync(ct);

    private async Task<Articulo> BuscarArticuloAsync(int id, CancellationToken ct) =>
        await db.Articulos.FirstOrDefaultAsync(a => a.Id == id, ct)
            // El filtro de EF (+ RLS por debajo) ya deja invisible la fila de otro tenant — esto
            // solo cubre "no existe en absoluto" (ADR-8: mismo 404 en los dos casos).
            ?? throw ErrorDominio.NoEncontrado($"No existe el artículo {id}.");

    private async Task<ListaPrecio> BuscarListaAsync(int id, CancellationToken ct) =>
        await db.ListasPrecio.FirstOrDefaultAsync(l => l.Id == id, ct)
            ?? throw new ErrorDominio("referencia_invalida", $"No existe la lista de precios {id}.", 400);

    /// <summary>Spec: "lista must be fija to store rows (derivada rejected with clear 400)" —
    /// pre-chequeo antes de cualquier escritura en <c>precios</c>.</summary>
    private async Task<ListaPrecio> BuscarListaFijaAsync(int id, CancellationToken ct)
    {
        var lista = await BuscarListaAsync(id, ct);

        if (lista.Modo != ModoLista.Fija)
        {
            throw new ErrorDominio(
                "lista_no_es_fija",
                "Solo se pueden registrar precios propios en listas de modo fija; una derivada se resuelve en lectura.",
                400);
        }

        return lista;
    }

    private int ExigirTenantDeLaSesion() =>
        contexto.IdTenant
            // GestionDeCatalogo (capa de API) ya exige admin de tenant — un actor de plataforma
            // nunca llega hasta acá. Defensa en profundidad, no un camino alcanzable en
            // operación normal.
            ?? throw new InvalidOperationException(
                "ServicioDePrecios requiere un actor de tenant; GestionDeCatalogo es admin-only.");

    /// <summary>Columna <c>numeric(14,2)</c> (<c>PrecioConfiguration</c>) — mismo bound que
    /// <c>ServicioDeArticulos.ExigirCostoValido</c> (misma precisión de columna).</summary>
    private static void ExigirPrecioValido(decimal precio)
    {
        if (precio < 0 || precio >= 1_000_000_000_000m)
        {
            throw new ErrorDominio("precio_invalido", "El campo precio debe estar entre 0 y 999999999999.99.", 400);
        }
    }

    private void ExigirVigenteDesdeFuturo(DateTimeOffset vigenteDesde)
    {
        if (vigenteDesde < reloj.Ahora - ToleranciaReloj)
        {
            throw new ErrorDominio(
                "vigente_desde_en_el_pasado",
                "vigente_desde no puede estar en el pasado (tolerancia de desfasaje de reloj de "
                    + $"{ToleranciaReloj.TotalSeconds:0} segundos).",
                400);
        }
    }

    /// <summary><c>SELECT ... FOR UPDATE</c> vía ADO.NET crudo sobre la conexión/transacción
    /// activa del <see cref="IWaysDbContext"/> inyectado — mismo criterio de "nunca
    /// <c>FromSqlRaw&lt;T&gt;()</c>"
    /// que <c>AsignadorDeCodigoInternoArticulo</c>/<c>AsignadorDeNumeroCliente</c>, pero acá el
    /// motivo es distinto: EF Core/Npgsql no tiene una API mapeada para <c>FOR UPDATE</c> sobre
    /// una entidad, así que la única forma de tomar el lock de fila es SQL crudo. <c>id_tenant</c>
    /// se filtra explícitamente (defensa en profundidad) aunque RLS ya lo garantiza — mismo
    /// criterio dual-capa que el resto del código de escritura.</summary>
    private async Task<FilaVigente?> BloquearFilaVigenteAsync(
        int idArticulo, int idListaPrecio, int idTenant, CancellationToken ct)
    {
        var conexion = await ObtenerConexionAbiertaAsync(ct);

        await using var comando = conexion.CreateCommand();
        comando.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        comando.CommandText =
            "SELECT id_precio, vigente_desde FROM precios " +
            "WHERE id_articulo = $1 AND id_lista_precio = $2 AND id_tenant = $3 " +
            "AND vigente_hasta IS NULL AND deleted_at IS NULL " +
            "FOR UPDATE";

        AgregarParametro(comando, idArticulo);
        AgregarParametro(comando, idListaPrecio);
        AgregarParametro(comando, idTenant);

        await using var lector = await comando.ExecuteReaderAsync(ct);

        if (!await lector.ReadAsync(ct))
        {
            return null;
        }

        return new FilaVigente(lector.GetInt32(0), lector.GetFieldValue<DateTimeOffset>(1));
    }

    private async Task CerrarFilaAsync(int idPrecio, DateTimeOffset vigenteHasta, DateTimeOffset ahora, CancellationToken ct)
    {
        var conexion = await ObtenerConexionAbiertaAsync(ct);

        await using var comando = conexion.CreateCommand();
        comando.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        comando.CommandText = "UPDATE precios SET vigente_hasta = $1, updated_at = $2 WHERE id_precio = $3";

        AgregarParametro(comando, vigenteHasta);
        AgregarParametro(comando, ahora);
        AgregarParametro(comando, idPrecio);

        await comando.ExecuteNonQueryAsync(ct);
    }

    private async Task<DbConnection> ObtenerConexionAbiertaAsync(CancellationToken ct)
    {
        var conexion = db.Database.GetDbConnection();

        if (conexion.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        return conexion;
    }

    private static void AgregarParametro(DbCommand comando, object valor)
    {
        var parametro = comando.CreateParameter();
        parametro.Value = valor;
        comando.Parameters.Add(parametro);
    }

    private readonly record struct FilaVigente(int Id, DateTimeOffset VigenteDesde);
}
