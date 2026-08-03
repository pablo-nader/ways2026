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
    /// es "ahora", nunca provisto por el cliente. <c>vigenteDesde: null</c> le indica a
    /// <see cref="AbrirNuevoPrecioAsync"/> que resuelva "ahora" DESPUÉS de tomar el advisory
    /// lock (judgment-day, item 3) — capturarlo acá, antes de entrar a la transacción, es
    /// exactamente el bug que ese fix corrige: un llamador que espera el lock bajo contención
    /// terminaría con un <c>vigente_desde</c> más viejo que el de la fila que ya ganó la carrera
    /// y confirmó, disparando un <c>vigente_desde_invalido</c> espurio.</summary>
    public Task<PrecioVigente> EstablecerPrecioAsync(int idArticulo, AltaPrecio datos, CancellationToken ct = default) =>
        AbrirNuevoPrecioAsync(idArticulo, datos.IdListaPrecio, datos.Precio, vigenteDesde: null, datos.ConfirmarReemplazo, ct);

    /// <summary>Programa un precio a futuro (spec: Programmable Future Prices) —
    /// <see cref="ProgramarPrecio.VigenteDesde"/> tiene que ser una fecha futura antes de entrar
    /// a la transacción de <see cref="AbrirNuevoPrecioAsync"/>.</summary>
    public Task<PrecioVigente> ProgramarPrecioAsync(int idArticulo, ProgramarPrecio datos, CancellationToken ct = default)
    {
        ExigirVigenteDesdeFuturo(datos.VigenteDesde);
        return AbrirNuevoPrecioAsync(idArticulo, datos.IdListaPrecio, datos.Precio, datos.VigenteDesde, datos.ConfirmarReemplazo, ct);
    }

    /// <summary>
    /// Design decision 3/4 (revisado en judgment-day, items 1-3) — única fila de escritura de
    /// <c>precios</c>. Dentro de una única transacción: toma un <c>pg_advisory_xact_lock</c>
    /// determinístico sobre el par <c>(idArticulo, idListaPrecio)</c> de este tenant PRIMERO
    /// (<see cref="TomarLockDelParAsync"/>) — serializa CUALQUIER escritura concurrente sobre el
    /// mismo par, exista o no una fila abierta todavía (a diferencia del viejo <c>SELECT ... FOR
    /// UPDATE</c>, que solo podía lockear una fila YA EXISTENTE) — y recién ahí resuelve "ahora"
    /// y lee la fila actualmente abierta con un SELECT plano (<see
    /// cref="BuscarFilaAbiertaAsync"/>; seguro porque el lock ya garantiza que ningún otro
    /// escritor está tocando este par), decide si hace falta confirmación (fila pendiente —
    /// <c>vigente_desde &gt; ahora</c> — sin <paramref name="confirmarReemplazo"/>), la cierra
    /// (y, si era pendiente, re-cierra también su PREDECESOR — localizado con <see
    /// cref="BuscarPredecesorAsync"/> y re-cerrado con el mismo <see
    /// cref="CerrarFilaAsync"/> inline que usa el resto del método), e inserta la nueva fila
    /// abierta.
    ///
    /// Con el advisory lock, la carrera de <c>ux_precios_vigente</c> (dos primeros precios
    /// concurrentes para el mismo par) YA NO es alcanzable por este camino de servicio: el
    /// segundo llamador espera el lock, y al retomarlo lee el estado YA COMITEADO por el
    /// primero, así que hace un cierre-y-apertura legítimo en vez de chocar contra el índice
    /// único (task 3.11, test adaptado en judgment-day: ambas altas concurrentes terminan en
    /// 201). El backstop (<c>ux_precios_vigente</c>, <c>ManejadorDeErrores</c> → 409
    /// <c>precio_vigente_duplicado</c>) se mantiene igual como defensa de esquema — solo queda
    /// alcanzable por una escritura cruda/fuera de banda que bypasee este servicio (misma
    /// familia que <c>PK_articulos_empresas</c>, Slice 2 judgment-day ronda 2).
    /// </summary>
    public async Task<PrecioVigente> AbrirNuevoPrecioAsync(
        int idArticulo, int idListaPrecio, decimal precio, DateTimeOffset? vigenteDesde, bool confirmarReemplazo,
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

            await TomarLockDelParAsync(idTenant, idArticulo, idListaPrecio, ct);

            // "ahora" se captura DESPUÉS del lock (judgment-day, item 3) — nunca antes de entrar
            // a la transacción. vigenteDesde == null (caso inmediato, EstablecerPrecioAsync)
            // también se resuelve acá, con el mismo "ahora" post-lock.
            var ahora = reloj.Ahora;
            var vigenteDesdeEfectivo = vigenteDesde ?? ahora;

            var filaAbierta = await BuscarFilaAbiertaAsync(idArticulo, idListaPrecio, idTenant, ct);

            if (filaAbierta is { } fila)
            {
                // Reemplazar una fila pendiente con la MISMA fecha ("corregir el importe
                // manteniendo la fecha") es una operación legítima — exactamente por eso
                // BuscarPredecesorAsync tiene que ser determinístico y excluir filas muertas
                // (judgment-day ronda 3, item 1): un reemplazo mismo-fecha deja una fila muerta
                // (vigente_desde == vigente_hasta) que puede compartir límite con el predecesor
                // real si esa fila, a su vez, se vuelve a reemplazar.
                var esPendiente = fila.VigenteDesde > ahora;

                if (esPendiente && !confirmarReemplazo)
                {
                    throw ErrorDominio.Conflicto(
                        "precio_pendiente_existe",
                        "Ya existe un precio pendiente para este artículo en esta lista; confirmá el reemplazo.");
                }

                if (!esPendiente && vigenteDesdeEfectivo < fila.VigenteDesde)
                {
                    throw new ErrorDominio(
                        "vigente_desde_invalido",
                        "vigente_desde no puede ser anterior al del precio vigente actual.",
                        400);
                }

                // (judgment-day ronda 2, item 1) Chequeo SIMÉTRICO al de arriba, pero contra el
                // PREDECESOR en vez de contra la fila activa: si `fila` es pendiente, buscamos
                // ANTES de tocar nada quién es su predecesor (la fila cuyo vigente_hasta coincide
                // con el vigente_desde original de `fila`) y rechazamos si la fecha nueva cae en
                // o antes del inicio de ESE predecesor — mismo criterio de "no anterior al inicio
                // de la fila que se está por afectar" que el chequeo de la fila activa, aplicado
                // un nivel más atrás. Sin esto, la búsqueda del predecesor (BuscarPredecesorAsync
                // + CerrarFilaAsync) re-cerraba el
                // predecesor con un límite ANTERIOR a su propio inicio, invirtiendo su intervalo
                // (vigente_hasta < vigente_desde) — silencioso hasta la constraint de esquema
                // (ck_precios_ventana_valida), que acá se adelanta con un 400 claro y sin tocar
                // ninguna fila.
                FilaVigente? predecesor = esPendiente
                    ? await BuscarPredecesorAsync(idArticulo, idListaPrecio, idTenant, fila.Id, fila.VigenteDesde, ct)
                    : null;

                if (predecesor is { } pred && vigenteDesdeEfectivo <= pred.VigenteDesde)
                {
                    throw new ErrorDominio(
                        "vigente_desde_invalido",
                        "vigente_desde no puede ser anterior o igual al del precio predecesor.",
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
                var vigenteHastaDeLaFilaCerrada = esPendiente ? fila.VigenteDesde : vigenteDesdeEfectivo;

                await CerrarFilaAsync(fila.Id, vigenteHastaDeLaFilaCerrada, ahora, ct);

                if (predecesor is { } predecesorAReabrir)
                {
                    // (judgment-day, item 1) El PREDECESOR — la fila que se cerró originalmente
                    // al abrirse `fila` (su vigente_hasta == fila.VigenteDesde) — queda con un
                    // límite VIEJO si no se corrige acá. Sin esto: una fecha nueva ANTERIOR a la
                    // original produce SOLAPAMIENTO (dos filas satisfacen el predicado "vigente"
                    // en el rango entre ambas fechas — el historial miente); una fecha nueva
                    // POSTERIOR produce un HUECO (ningún precio vigente en ese rango). Se
                    // re-cierra al vigente_desde EFECTIVO de la fila nueva — mismo criterio que
                    // la fila ACTIVA usa arriba para su propio cierre. El chequeo simétrico de
                    // arriba ya garantizó que `vigenteDesdeEfectivo` es estrictamente posterior
                    // al inicio de este predecesor, así que el intervalo resultante nunca se
                    // invierte.
                    await CerrarFilaAsync(predecesorAReabrir.Id, vigenteDesdeEfectivo, ahora, ct);
                }
            }

            db.Precios.Add(new Precio
            {
                IdArticulo = idArticulo,
                IdListaPrecio = idListaPrecio,
                Monto = precio,
                VigenteDesde = vigenteDesdeEfectivo,
                VigenteHasta = null,
                CreatedAt = ahora,
                UpdatedAt = ahora
            });

            await db.SaveChangesAsync(ct);
            await transaccion.CommitAsync(ct);

            return new PrecioVigente(idArticulo, idListaPrecio, precio, vigenteDesdeEfectivo);
        });
    }

    /// <summary>Precio vigente de UN artículo en UNA lista a una fecha (spec: Current-Price
    /// Query Semantics By Date; Derived List Price Resolution At Read Time). <paramref
    /// name="fecha"/> por defecto es <c>reloj.Ahora</c>.
    ///
    /// <para>(judgment-day, item 5b) Divergencia DELIBERADA con <see cref="PreciosVigentesAsync"/>:
    /// acá NO se filtra por <c>lista.Activo</c> — una búsqueda puntual por id explícito puede
    /// resolver una lista inactiva (el llamador ya sabe qué lista quiere; una lista
    /// desactivada no deja de tener historial de precios válido). <see
    /// cref="PreciosVigentesAsync"/> sí filtra por <c>Activo</c> porque ahí el criterio es "qué
    /// listas mostrar por default", no "resolvé esta lista puntual". Documentado acá y en el
    /// otro método para que la próxima persona que lo lea no lo confunda con un bug.</para>
    /// </summary>
    public async Task<PrecioVigente> PrecioVigenteAsync(
        int idArticulo, int idListaPrecio, DateTimeOffset? fecha, CancellationToken ct = default)
    {
        await BuscarArticuloAsync(idArticulo, ct);
        var lista = await BuscarListaAsync(idListaPrecio, ct);

        return await ResolverPrecioAsync(idArticulo, lista, fecha ?? reloj.Ahora, ct);
    }

    /// <summary>Precio vigente de un artículo en TODAS las listas activas del tenant a una fecha
    /// — endpoint "single artículo across listas" (scope de esta slice).
    ///
    /// <para>(judgment-day, item 5b) Filtra por <c>Activo</c> a propósito, a diferencia de <see
    /// cref="PrecioVigenteAsync"/> (que resuelve cualquier lista por id explícito, activa o no)
    /// — ver el doc-comment de ese método para el criterio completo.</para>
    ///
    /// <para>(judgment-day, item 5c, INFO para la etapa POS) <c>N+1</c> deliberado: una consulta
    /// por lista dentro del <c>foreach</c>, sin batchear. Aceptable para este endpoint
    /// "single artículo" (pocas listas por tenant), pero el catálogo del POS (etapa 5) va a
    /// necesitar resolver precios de MUCHOS artículos a la vez — ahí sí va a hacer falta
    /// batchear esta resolución (probablemente una consulta por lista sobre TODOS los artículos
    /// del catálogo, no una por artículo). No se refactoriza acá porque está fuera del alcance
    /// de esta slice.</para>
    /// </summary>
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

        // (judgment-day, item 4) explícito en lugar de `lista.Porcentaje!.Value`: una lista
        // derivada sin porcentaje configurado es un invariante violado (ServicioDeListasPrecio,
        // Slice 4, lo exige al escribir) — mismo código de dominio que un precio derivado
        // negativo (ResolvedorDePrecios.ResolverPrecioDerivado), nunca un NRE crudo.
        var porcentaje = lista.Porcentaje
            ?? throw new ErrorDominio(
                "precio_derivado_invalido",
                $"La lista derivada {lista.Id} no tiene porcentaje configurado.",
                422);

        var monto = montoBase is { } b
            ? ResolvedorDePrecios.ResolverPrecioDerivado(b, porcentaje)
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

    /// <summary>Deriva la clave determinística de <c>pg_advisory_xact_lock(int, int)</c> para el
    /// par <c>(idArticulo, idListaPrecio)</c> de este tenant (judgment-day, item 2). Primer
    /// argumento: <c>idTenant</c> — no hace falta mezclarlo con nada más, cada tenant ocupa su
    /// propio subespacio de claves. Segundo argumento: combinación aritmética simple de
    /// <c>idArticulo</c>/<c>idListaPrecio</c> — DELIBERADAMENTE no <c>HashCode.Combine</c>, que
    /// incorpora una semilla aleatoria por proceso (dos instancias de la app, o la misma tras un
    /// reinicio, calcularían claves DISTINTAS para el MISMO par, y el lock dejaría de
    /// serializarlas entre sí — justo lo opuesto de lo que se busca). Una colisión de la clave 2
    /// entre dos pares DISTINTOS del mismo tenant es tolerable y no compromete la corrección:
    /// el peor caso es serializar de más (dos pares no relacionados se esperan entre sí sin
    /// necesidad) — nunca una lectura incorrecta, porque el estado real siempre se lee de la
    /// fila (<see cref="BuscarFilaAbiertaAsync"/>) DESPUÉS de tomar el lock, nunca del hash en
    /// sí.</summary>
    private static (int Clave1, int Clave2) ClaveDeLockDePar(int idTenant, int idArticulo, int idListaPrecio) =>
        (idTenant, unchecked((idArticulo * 397) ^ idListaPrecio));

    /// <summary><c>pg_advisory_xact_lock</c> con alcance de TRANSACCIÓN (se libera solo al
    /// COMMIT/ROLLBACK) tomado ANTES de leer nada de precios (judgment-day, item 2) — a diferencia del
    /// viejo <c>SELECT ... FOR UPDATE</c> sobre la fila mutable (que no existía para lockear
    /// cuando el par no tenía ninguna fila abierta todavía), esto serializa CUALQUIER escritura
    /// concurrente sobre el mismo par, exista o no una fila abierta: el segundo llamador espera
    /// acá hasta que el primero comitee o revierta, y recién ahí lee el estado ACTUAL — la
    /// semántica de "esperar y actuar sobre el estado actual" que el doc-comment de
    /// <see cref="AbrirNuevoPrecioAsync"/> promete.</summary>
    private async Task TomarLockDelParAsync(int idTenant, int idArticulo, int idListaPrecio, CancellationToken ct)
    {
        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var (clave1, clave2) = ClaveDeLockDePar(idTenant, idArticulo, idListaPrecio);

        await using var comando = conexion.CreateCommand();
        comando.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        comando.CommandText = "SELECT pg_advisory_xact_lock($1, $2)";

        AgregarParametro(comando, clave1);
        AgregarParametro(comando, clave2);

        await comando.ExecuteNonQueryAsync(ct);
    }

    /// <summary>SELECT plano (sin <c>FOR UPDATE</c>) vía ADO.NET crudo sobre la
    /// conexión/transacción activa del <see cref="IWaysDbContext"/> inyectado — mismo criterio
    /// de "nunca <c>FromSqlRaw&lt;T&gt;()</c>" que <c>AsignadorDeCodigoInternoArticulo</c>/
    /// <c>AsignadorDeNumeroCliente</c>. Seguro sin lock de fila propio porque
    /// <see cref="TomarLockDelParAsync"/> ya se tomó ANTES en la misma transacción — ningún otro
    /// escritor puede estar tocando este par en simultáneo. <c>id_tenant</c> se filtra
    /// explícitamente (defensa en profundidad) aunque RLS ya lo garantiza — mismo criterio
    /// dual-capa que el resto del código de escritura.</summary>
    private async Task<FilaVigente?> BuscarFilaAbiertaAsync(
        int idArticulo, int idListaPrecio, int idTenant, CancellationToken ct)
    {
        var conexion = await ObtenerConexionAbiertaAsync(ct);

        await using var comando = conexion.CreateCommand();
        comando.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        comando.CommandText =
            "SELECT id_precio, vigente_desde FROM precios " +
            "WHERE id_articulo = $1 AND id_lista_precio = $2 AND id_tenant = $3 " +
            "AND vigente_hasta IS NULL AND deleted_at IS NULL";

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

    /// <summary>(judgment-day, item 1; ronda 2, item 1) Localiza el PREDECESOR de una fila
    /// pendiente que está por reemplazarse — la fila cuyo <c>vigente_hasta</c> coincide EXACTO
    /// con <paramref name="limiteOriginal"/> (el <c>vigente_desde</c> original de la pendiente,
    /// antes del reemplazo). Devuelve <c>null</c> si no hay predecesor (la pendiente reemplazada
    /// era el primer precio del par) — nada que validar ni re-cerrar.
    ///
    /// Solo BUSCA: el caller (<see cref="AbrirNuevoPrecioAsync"/>) valida el límite nuevo contra
    /// <see cref="FilaVigente.VigenteDesde"/> ANTES de cerrar cualquier fila (ronda 2, item 1) —
    /// separar la búsqueda del cierre es lo que permite ese orden: sin esto, el cierre del
    /// predecesor ocurría a ciegas, sin chance de rechazar un límite inválido antes de escribir.
    ///
    /// <paramref name="idFilaPendienteCerrada"/> se EXCLUYE explícitamente de la búsqueda —
    /// cuando el reemplazo cierra la pendiente en su ventana muerta (<c>vigente_hasta ==
    /// vigente_desde == limiteOriginal</c>, ver el caller), esa MISMA fila también matchea
    /// <c>vigente_hasta = limiteOriginal</c>. Sin esta exclusión, la pendiente recién cerrada
    /// aparecería como su propio predecesor — el bug encontrado corriendo el caso "primer precio
    /// del par es directamente un programado, sin predecesor real".
    ///
    /// (judgment-day ronda 3, item 1) DOS defensas más, necesarias porque un reemplazo con la
    /// MISMA fecha ("corregir el importe manteniendo la fecha", ver el caller) deja una fila
    /// MUERTA (<c>vigente_desde == vigente_hasta</c>) que comparte el mismo límite que el
    /// predecesor REAL cuando ese reemplazo mismo-fecha, a su vez, se vuelve a reemplazar con una
    /// fecha nueva: <c>vigente_hasta = limiteOriginal</c> por sí solo es AMBIGUO entre la fila
    /// muerta y el predecesor real, y Postgres no garantiza cuál de las dos filas devuelve. Si
    /// devuelve la muerta, el cierre subsiguiente la REABRE (le pisa el <c>vigente_hasta</c>),
    /// resucitando un precio que el usuario ya había reemplazado — invisible en los tests que no
    /// prueban el camino "reemplazo mismo-fecha seguido de un reemplazo con fecha distinta".
    /// <c>vigente_desde &lt;&gt; vigente_hasta</c> excluye toda fila muerta (nunca es el
    /// predecesor real, que siempre tiene una ventana con contenido); <c>ORDER BY vigente_desde
    /// ASC LIMIT 1</c> hace el resultado determinístico incluso si llegara a haber más de una
    /// fila con contenido compartiendo el límite — el predecesor real siempre es el de menor
    /// <c>vigente_desde</c>.</summary>
    private async Task<FilaVigente?> BuscarPredecesorAsync(
        int idArticulo, int idListaPrecio, int idTenant, int idFilaPendienteCerrada, DateTimeOffset limiteOriginal,
        CancellationToken ct)
    {
        var conexion = await ObtenerConexionAbiertaAsync(ct);

        await using var comando = conexion.CreateCommand();
        comando.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        comando.CommandText =
            "SELECT id_precio, vigente_desde FROM precios " +
            "WHERE id_articulo = $1 AND id_lista_precio = $2 AND id_tenant = $3 " +
            "AND vigente_hasta = $4 AND id_precio != $5 AND deleted_at IS NULL " +
            "AND vigente_desde <> vigente_hasta " +
            "ORDER BY vigente_desde ASC LIMIT 1";

        AgregarParametro(comando, idArticulo);
        AgregarParametro(comando, idListaPrecio);
        AgregarParametro(comando, idTenant);
        AgregarParametro(comando, limiteOriginal);
        AgregarParametro(comando, idFilaPendienteCerrada);

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
