using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Bajas;
using Ways.Application.Stock;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Ofertas;
using Ways.Domain.Precios;
using Ways.Domain.Proveedores;
using Ways.Domain.Usuarios;
using static Ways.Application.Busqueda.BusquedaSinAcentos;

namespace Ways.Application.Articulos;

/// <summary>
/// ABM de artículos + códigos de barra (design decision 1: entidad/servicio dedicados, no
/// <c>ServicioDeCatalogo&lt;T&gt;</c>). Autorización: <c>Politicas.GestionDeCatalogo</c>
/// aplicada en la capa de API — mismo criterio que
/// <see cref="Clientes.ServicioDeClientes"/>/<see cref="Proveedores.ServicioDeProveedores"/>.
///
/// Un <c>ServicioDeCodigosBarra</c> separado no existe a propósito (task 2.3, "implementer's
/// call, document whichever is chosen"): los códigos de barra son una sub-colección del
/// artículo sin autorización/ciclo de vida propio — separarlos en otra clase solo agregaría un
/// segundo servicio inyectado en <c>ArticulosEndpoints</c> sin ningún beneficio de cohesión.
///
/// El alta abre transacción (mismo patrón que <see cref="Clientes.ServicioDeClientes.CrearAsync"/>)
/// porque puede necesitar <see cref="AsignadorDeCodigoInternoArticulo"/> — a diferencia de
/// <see cref="Proveedores.ServicioDeProveedores"/>, que nunca la necesita.
///
/// fix/articulos-lock-referencias: la edición TAMBIÉN abre transacción desde este fix (antes no
/// la necesitaba: el <c>codigo_interno</c> no es editable, ver <see cref="EdicionArticulo"/>) —
/// <see cref="ActualizarAsync"/> pasó a compartir el mismo "transaction-blocked-provider caveat"
/// que <see cref="CrearAsync"/>: <c>ServicioDeArticulosTests</c> ya no cubre su round-trip
/// persistido completo contra el proveedor InMemory (movido a <c>ArticulosEndpointsTests</c>,
/// Postgres real, mismo criterio que <c>ServicioDeOfertasTests</c>/<c>OfertasEndpointsTests</c>).
///
/// Los 5 chequeos de referencia de catálogo (área/categoría/marca/grupo/proveedor habitual)
/// corren DENTRO de esa transacción, bajo <see cref="GuardaDeReferencias.BloquearSiEstaVivaAsync{T}"/>
/// en vez del pre-chequeo <c>AnyAsync</c> filtrado de antes: cierra, del lado de este escritor, el
/// residual documentado en <see cref="GuardaDeReferencias"/> (una baja de catálogo que gana la
/// carrera contra un alta/edición de artículo en vuelo ya no puede comitear una referencia
/// colgante). <see cref="ExigirAlicuotaIvaValidaAsync"/> queda afuera de este cambio (catálogo
/// global <c>[global]</c>, sin baja lógica de tenant que este fix necesite cerrar).
///
/// stage-12-lotes-vencimientos, Slice 4 (design: Reconciliation triggers): un flip de
/// <see cref="Articulo.ControlaLote"/> <c>false → true</c> en <see cref="ActualizarAsync"/>
/// dispara <see cref="ServicioDeLotes.ReconciliarAsync"/> — primera escritura de dominio de
/// stock que hace un ABM de catálogo (flagged for the owner, no bloqueante, design: Open
/// Questions). Sigue corriendo DESPUÉS del commit de la transacción de edición, nunca adentro
/// (contrato de fallo parcial documentado en <see cref="ActualizarAsync"/>).
/// </summary>
public class ServicioDeArticulos(
    IWaysDbContext db, IRelojDelSistema reloj, IContextoDeUsuario contexto, ServicioDeLotes servicioDeLotes,
    GuardaDeReferencias guarda)
{
    /// <summary>stage-18-etiquetas-y-consulta, Slice 2 (task 2.4; design.md:60, decisión 9): el
    /// tope de paginado/selección — YA existía como el literal <c>200</c> del clamp de abajo, la
    /// promoción a constante pública es lo nuevo. <see cref="Etiquetas.ServicioDeEtiquetas"/> lo
    /// referencia en vez de duplicar el número: un mutante del valor rompe el clamp de esta clase
    /// Y el <c>truncado</c> de <c>ServicioDeEtiquetas</c> A LA VEZ (mutation target 18) — la
    /// coupling es el punto, no un accidente.</summary>
    public const int TamanioMaximoDePagina = 200;

    /// <param name="idProveedor">Solo tiene efecto junto con <paramref name="busqueda"/>: suma a los
    /// artículos que tengan, para ese proveedor, un código vivo igual (sin distinguir mayúsculas) al
    /// término buscado, los lista primero y expone ese código en
    /// <see cref="ArticuloListado.CodigoProveedor"/>. Un proveedor dado de baja no coincide por código.</param>
    public async Task<PaginaDe<ArticuloListado>> ListarAsync(
        string? busqueda = null,
        int? idEmpresa = null,
        bool incluirEliminados = false,
        int pagina = 1,
        int tamanio = 25,
        int? idArea = null,
        int? idCategoria = null,
        int? idMarca = null,
        int? idProveedor = null,
        CancellationToken ct = default)
    {
        pagina = Math.Max(pagina, 1);
        tamanio = Math.Clamp(tamanio, 1, TamanioMaximoDePagina);

        var query = db.Articulos.AsQueryable();

        if (incluirEliminados)
        {
            // Solo la baja lógica: ignorar todos los filtros de un tirón también saltearía
            // el de tenant (ADR-6) — mismo criterio que ServicioDeClientes.ListarAsync.
            query = query.IgnoreQueryFilters(["BajaLogica"]);
        }

        if (idEmpresa is { } idEmp)
        {
            query = query.DisponibleEnEmpresa(db, idEmp);
        }

        var termino = busqueda?.Trim();
        var buscaPorCodigoDeProveedor = idProveedor is not null && !string.IsNullOrEmpty(termino);
        var idProveedorBuscado = idProveedor ?? 0;

        if (!string.IsNullOrEmpty(termino))
        {
            // Sin mayúsculas ni acentos (BusquedaSinAcentos). El
            // término también busca por codigo_interno y por cualquiera de los codigos_barra
            // del artículo (subquery correlacionada, mismo shape que el EXISTS de
            // DisponibleEnEmpresa). El código de proveedor es la excepción: igualdad exacta
            // (citext), solo contra el proveedor pedido y solo si ese proveedor sigue visible.
            var patron = PatronDeContiene(termino);
            query = query.Where(a =>
                Coincide(a.Nombre, patron) ||
                Coincide(a.CodigoInterno, patron) ||
                db.CodigosBarra.Any(c => c.IdArticulo == a.Id && Coincide(c.Codigo, patron)) ||
                (buscaPorCodigoDeProveedor &&
                    db.CodigosProveedor.Any(c =>
                        c.IdArticulo == a.Id && c.IdProveedor == idProveedorBuscado && c.Codigo == termino &&
                        db.Proveedores.Any(p => p.Id == c.IdProveedor))));
        }

        // stage-18-etiquetas-y-consulta, Slice 2 (task 2.5; design.md:219-224): tres filtros
        // ADITIVOS, cada uno con su propia guarda `if (… is { } x)` — cada guarda es un conjunct
        // independiente del AND (Reconciliación 4 de tasks.md), no una rama compartida; borrar
        // cualquiera de las tres NO afecta a las otras dos ni al camino sin filtros (mutation
        // target 26/27).
        if (idArea is { } idAreaValor)
        {
            query = query.Where(a => a.IdArea == idAreaValor);
        }

        if (idMarca is { } idMarcaValor)
        {
            query = query.Where(a => a.IdMarca == idMarcaValor);
        }

        if (idCategoria is { } idCategoriaValor)
        {
            // Una sola proyección id→id_padre de TODO el tenant (mismo criterio que
            // ServicioDeOfertas.ResolverAsync, design.md:32-34, decisión 8) — la expansión de
            // descendientes corre en memoria, sin una consulta jerárquica por artículo.
            var padrePorCategoria = await db.Categorias
                .Select(c => new { c.Id, c.IdCategoriaPadre })
                .ToDictionaryAsync(c => c.Id, c => c.IdCategoriaPadre, ct);

            var descendientes = CadenaDeCategorias.ConstruirDescendientes(idCategoriaValor, padrePorCategoria);

            query = query.Where(a => a.IdCategoria != null && descendientes.Contains(a.IdCategoria.Value));
        }

        var total = await query.CountAsync(ct);

        var items = await query
            .Select(a => new
            {
                Articulo = a,
                CodigoProveedor = db.CodigosProveedor
                    .Where(c =>
                        buscaPorCodigoDeProveedor && c.IdArticulo == a.Id && c.IdProveedor == idProveedorBuscado &&
                        c.Codigo == termino && db.Proveedores.Any(p => p.Id == c.IdProveedor))
                    .Select(c => c.Codigo)
                    .FirstOrDefault()
            })
            .OrderBy(x => x.CodigoProveedor == null ? 1 : 0)
            .ThenBy(x => x.Articulo.Nombre)
            .Skip((pagina - 1) * tamanio)
            .Take(tamanio)
            .Select(x => new ArticuloListado(
                x.Articulo.Id, x.Articulo.CodigoInterno, x.Articulo.Nombre, x.Articulo.Descripcion,
                x.Articulo.IdArea, x.Articulo.IdCategoria, x.Articulo.IdMarca, x.Articulo.IdGrupo,
                x.Articulo.IdProveedorHabitual, x.Articulo.IdAlicuotaIva, x.Articulo.UnidadVenta,
                x.Articulo.UnidadesPorBulto, x.Articulo.EsProducto, x.Articulo.CostoLista,
                x.Articulo.DescuentoProveedor, x.Articulo.CostoNominal, x.Articulo.DisponibleParaTodas,
                Array.Empty<int>(), x.Articulo.Activo, x.Articulo.ControlaLote, x.CodigoProveedor))
            .ToListAsync(ct);

        if (!PuedeVerCostos)
        {
            items = items.ConvertAll(SinCostos);
        }

        return new PaginaDe<ArticuloListado>(items, total, pagina, tamanio);
    }

    public async Task<ArticuloListado> ObtenerAsync(int id, CancellationToken ct = default)
    {
        var articulo = await BuscarAsync(id, ct);

        // judgment-day ronda 1 (item 2): el detalle expone el subset actual — un cliente HTTP
        // necesita esto para armar un PUT de no-op sin perder las filas de articulos_empresas.
        IReadOnlyList<int> idsEmpresas = articulo.DisponibleParaTodas
            ? Array.Empty<int>()
            : await db.ArticulosEmpresas
                .Where(ae => ae.IdArticulo == articulo.Id)
                .Select(ae => ae.IdEmpresa)
                .ToListAsync(ct);

        var detalle = Proyectar(articulo, idsEmpresas);
        return PuedeVerCostos ? detalle : SinCostos(detalle);
    }

    /// <summary>Los costos son del back-office (admin y supervisor): el vendedor lee este mismo
    /// endpoint desde el POS y nunca los recibe.</summary>
    private bool PuedeVerCostos => contexto.Rol is RolConocido.Admin or RolConocido.Supervisor;

    private static ArticuloListado SinCostos(ArticuloListado a) =>
        a with { CostoLista = null, DescuentoProveedor = null, CostoNominal = null };

    /// <summary>Asigna <c>codigo_interno</c> de forma atómica (design decision 6) cuando se
    /// omite, dentro de la misma transacción que el INSERT — igual criterio que
    /// <see cref="Clientes.ServicioDeClientes.CrearAsync"/>. Cuando se provee, se valida único
    /// por tenant antes de abrir la transacción (pre-chequeo best-effort, db-error-backstops:
    /// el backstop real es <c>ux_articulos_codigo_interno</c>).</summary>
    public async Task<ArticuloListado> CrearAsync(AltaArticulo datos, CancellationToken ct = default)
    {
        var nombre = NormalizarRequerido(datos.Nombre, "nombre", 150);
        var descripcion = NormalizarOpcional(datos.Descripcion, "descripcion", null);
        var codigoInterno = NormalizarCodigoInternoOpcional(datos.CodigoInterno);
        var codigoProveedor = ReglaDeCodigoProveedor.NormalizarOpcional(datos.CodigoProveedor, "codigo_proveedor");
        ReglaDeCodigoProveedor.ExigirProveedor(codigoProveedor, datos.IdProveedorHabitual);

        ExigirIdRequerido(datos.IdArea, "id_area");
        ExigirIdRequerido(datos.IdAlicuotaIva, "id_alicuota_iva");
        ExigirUnidadesPorBultoValida(datos.UnidadesPorBulto);
        ExigirCostoValido(datos.CostoLista, "costo_lista");
        ExigirCostoValido(datos.CostoNominal, "costo_nominal");
        ExigirDescuentoProveedorValido(datos.DescuentoProveedor);

        await ExigirAlicuotaIvaValidaAsync(datos.IdAlicuotaIva, ct);

        // judgment-day ronda 1 (item 3): .Distinct() ANTES de validar/insertar — un duplicado
        // en el payload no debe inflar el conteo de "subset presente" ni generar dos INSERT
        // que choquen contra la PK compuesta (defensa en profundidad, ver el mapeo de
        // PK_articulos_empresas en ManejadorDeErrores).
        var idsEmpresas = datos.IdsEmpresas?.Distinct().ToList();

        // El artículo todavía no existe: crear directamente con disponible_para_todas=false
        // sin subconjunto cae bajo la misma regla que restringirlo después de creado (spec:
        // "Restricting availability requires at least one subset row") — la regla valida el
        // ESTADO RESULTANTE, no una transición (ver ReglaDeArticulos).
        ReglaDeArticulos.ValidarRestriccionDeDisponibilidad(datos.DisponibleParaTodas, idsEmpresas?.Count ?? 0);

        if (!datos.DisponibleParaTodas)
        {
            await ExigirEmpresasValidasAsync(idsEmpresas!, ct);
        }

        if (codigoInterno is not null)
        {
            await ExigirCodigoInternoDisponibleAsync(codigoInterno, ct);
        }

        var idTenant = ExigirTenantDeLaSesion();

        // Sin reintento: el INSERT no es idempotente y no hay clave de idempotencia — el
        // codigo_interno autogenerado se resuelve DENTRO de la transacción, así que un reintento
        // tomaría uno nuevo y daría de alta un segundo artículo.
        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        return await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            // fix/articulos-lock-referencias: primeras sentencias del lambda — bajo el lock de
            // GuardaDeReferencias.BloquearSiEstaVivaAsync, no el pre-chequeo AnyAsync filtrado de
            // antes (ver el doc-comment de la clase). Mismo orden área → categoría → marca →
            // grupo → proveedor que tenían los pre-chequeos, mismo código/mensaje de error.
            await ExigirAreaValidaAsync(datos.IdArea, ct);
            await ExigirCategoriaValidaAsync(datos.IdCategoria, ct);
            await ExigirMarcaValidaAsync(datos.IdMarca, ct);
            await ExigirGrupoValidoAsync(datos.IdGrupo, ct);
            await ExigirProveedorHabitualValidoAsync(datos.IdProveedorHabitual, ct);

            var codigoFinal = codigoInterno;
            if (codigoFinal is null)
            {
                await AsignadorDeCodigoInternoArticulo.AsegurarContadorAsync(db, idTenant, ct);
                var numero = await AsignadorDeCodigoInternoArticulo.AsignarSiguienteAsync(db, idTenant, ct);
                codigoFinal = numero.ToString(CultureInfo.InvariantCulture);
            }

            var ahora = reloj.Ahora;
            var articulo = new Articulo
            {
                CodigoInterno = codigoFinal,
                Nombre = nombre,
                Descripcion = descripcion,
                IdArea = datos.IdArea,
                IdCategoria = datos.IdCategoria,
                IdMarca = datos.IdMarca,
                IdGrupo = datos.IdGrupo,
                IdProveedorHabitual = datos.IdProveedorHabitual,
                IdAlicuotaIva = datos.IdAlicuotaIva,
                UnidadVenta = datos.UnidadVenta,
                UnidadesPorBulto = datos.UnidadesPorBulto,
                EsProducto = datos.EsProducto,
                CostoLista = datos.CostoLista,
                DescuentoProveedor = datos.DescuentoProveedor,
                CostoNominal = datos.CostoNominal,
                DisponibleParaTodas = datos.DisponibleParaTodas,
                Activo = datos.Activo,
                ControlaLote = datos.ControlaLote,
                CreatedAt = ahora,
                UpdatedAt = ahora
            };

            db.Articulos.Add(articulo);
            await db.SaveChangesAsync(ct);

            if (!datos.DisponibleParaTodas && idsEmpresas is { Count: > 0 })
            {
                AgregarFilasDeSubset(articulo.Id, idTenant, idsEmpresas);
                await db.SaveChangesAsync(ct);
            }

            // El proveedor habitual ya quedó bloqueado vivo arriba (ExigirProveedorHabitualValidoAsync):
            // el código se atribuye a él, en la misma transacción que el artículo.
            if (codigoProveedor is not null)
            {
                await ExigirCodigoProveedorDisponibleAsync(datos.IdProveedorHabitual!.Value, codigoProveedor, ct);
                db.CodigosProveedor.Add(NuevoCodigoProveedor(
                    articulo.Id, datos.IdProveedorHabitual.Value, codigoProveedor));
                await db.SaveChangesAsync(ct);
            }

            await transaccion.CommitAsync(ct);

            // Sin reconciliación acá (a diferencia de ActualizarAsync): un artículo recién creado
            // no puede tener stock preexistente, así que cualquier corrida sería un no-op —
            // design decisión 13, el residuo de un par sin fila de stock siempre es cero.
            return Proyectar(articulo, datos.DisponibleParaTodas ? Array.Empty<int>() : (IReadOnlyList<int>?)idsEmpresas ?? Array.Empty<int>());
        });
    }

    public async Task<ArticuloListado> ActualizarAsync(int id, EdicionArticulo datos, CancellationToken ct = default)
    {
        // 404 antes de CUALQUIER validación de payload, porque esa es la precedencia que había:
        // hasta este fix la lectura del artículo era la PRIMERA sentencia del método, así que un id
        // inexistente rendía 404 aunque el body fuera inválido. Tiene que ser la primera sentencia
        // y no solo "antes de abrir la transacción" — con los validadores de payload delante, un id
        // inexistente con body inválido pasa a 400 y el contrato cambia en silencio (judgment-day
        // encontró exactamente eso acá, y antes el mismo error en ServicioDeOfertas.ActualizarAsync).
        //
        // Va por EXISTS y NO por BuscarAsync: una lectura trackeada acá metería la entidad en el
        // identity map y la de adentro del lock resolvería contra ella — justo el snapshot pre-lock
        // que este fix elimina. Best-effort: la AUTORIDAD es la lectura de adentro del lock, mismo
        // idioma que ServicioDeOfertas.ActualizarAsync, donde este chequeo también es la primera
        // sentencia.
        //
        // ServicioDeOrganizacion.ActualizarModoPuntoVentaAsync comparte el idioma pero NO la
        // posición, y no es un descuido: ahí el guard de `modo_requerido` precede al 404 porque ese
        // fue siempre su contrato —es anterior al fix de la lectura bajo el lock y lo fija
        // OrganizacionTests.OmitirElCampoModoEnJsonCrudoAlCambiarElModoDevuelve400YNoCambiaNada—,
        // así que ahí mover el EXISTS más arriba cambiaría un comportamiento probado. Acá al revés:
        // la lectura ERA la primera sentencia, así que el EXISTS tiene que ocupar ese lugar para no
        // cambiar nada.
        if (!await db.Articulos.AnyAsync(a => a.Id == id, ct))
        {
            throw ErrorDominio.NoEncontrado($"No existe el artículo {id}.");
        }

        var nombre = NormalizarRequerido(datos.Nombre, "nombre", 150);
        var descripcion = NormalizarOpcional(datos.Descripcion, "descripcion", null);

        ExigirIdRequerido(datos.IdArea, "id_area");
        ExigirIdRequerido(datos.IdAlicuotaIva, "id_alicuota_iva");
        ExigirUnidadesPorBultoValida(datos.UnidadesPorBulto);
        ExigirCostoValido(datos.CostoLista, "costo_lista");
        ExigirCostoValido(datos.CostoNominal, "costo_nominal");
        ExigirDescuentoProveedorValido(datos.DescuentoProveedor);

        await ExigirAlicuotaIvaValidaAsync(datos.IdAlicuotaIva, ct);

        // judgment-day ronda 1 (item 3): mismo dedup que CrearAsync, antes de validar/insertar.
        var idsEmpresas = datos.IdsEmpresas?.Distinct().ToList();

        // judgment-day ronda 1 (root cause de los dos CRITICAL): la regla valida el ESTADO
        // RESULTANTE, no la transición — dispara igual si el artículo YA estaba restringido y
        // se vuelve a guardar sin ninguna fila de subset (false -> false), no solo en el pasaje
        // true -> false. Antes de este fix, un PUT así esquivaba el guard y reventaba con NRE
        // en ExigirEmpresasValidasAsync al iterar datos.IdsEmpresas nulo.
        ReglaDeArticulos.ValidarRestriccionDeDisponibilidad(datos.DisponibleParaTodas, idsEmpresas?.Count ?? 0);

        if (!datos.DisponibleParaTodas)
        {
            await ExigirEmpresasValidasAsync(idsEmpresas!, ct);
        }

        var idTenant = ExigirTenantDeLaSesion();

        // fix/articulos-lock-referencias (ef-retry-safe-writes, forma (b)): de acá hasta el
        // SaveChangesAsync corre sin reintento, dentro de una transacción explícita — antes esta
        // escritura no abría ninguna (el codigo_interno no es editable), pero ahora necesita
        // envolver los 5 chequeos de referencia LOCKEADOS (GuardaDeReferencias.BloquearSiEstaVivaAsync,
        // ver el doc-comment de la clase) en la misma transacción que el UPDATE que los usa. Sin
        // reintento por el mismo motivo que ServicioDeCatalogo.EliminarAsync: un commit ambiguo
        // reintentado releería el artículo con datos potencialmente ya actualizados por el propio
        // intento anterior, mismo riesgo que duplicar filas de ArticulosEmpresas.
        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        // Asignadas ADENTRO del lambda y leídas después: sirve porque la estrategia es la SIN
        // reintento, así que el lambda corre exactamente una vez y cualquier fallo se propaga en
        // vez de dejarlas a medio asignar. Con una estrategia reintentable esto sería una trampa.
        Articulo? articulo = null;
        var controlaLoteAnterior = false;

        await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            // EL LOCK QUE FALTABA, y es el PRIMER statement de la transacción: FOR UPDATE sobre la
            // fila del propio artículo. Los 5 BloquearSiEstaVivaAsync de abajo son FOR KEY SHARE
            // sobre los catálogos REFERENCIADOS, nunca sobre articulos.
            //
            // Lo que este lock agrega NO es la serialización: dos ediciones del mismo artículo ya
            // se serializaban entre sí, porque el UPDATE de SaveChangesAsync toma su propio lock de
            // fila y espera. Lo que agrega es serializar ANTES DE LEER. Sin él, el perdedor leía su
            // foto primero, esperaba recién al escribir, y terminaba escribiendo valores derivados
            // de un estado ya pisado: un campo cuyo valor pedido coincidía con esa foto quedaba
            // FUERA del UPDATE (EF no detecta cambio contra su valor ORIGINAL) y se perdía en
            // silencio con un 200. Verificado con el mutante: borrando esta línea la edición SIGUE
            // observándose bloqueada —el lock implícito del UPDATE alcanza para eso— y lo que muere
            // es la aserción sobre la fila final. Ver la skill single-read-under-lock, regla 4.
            //
            // ORDEN, y por qué no abre un ciclo: artículo (FOR UPDATE) y DESPUÉS los catálogos
            // (FOR KEY SHARE). La baja de un catálogo toma el orden inverso —FOR UPDATE sobre el
            // catálogo y después LEE articulos— pero lo lee SIN lock (InspectorDeUso es
            // read-only), así que nunca espera por esta fila y no hay ciclo. Ningún otro escritor
            // del repo tomaba un lock explícito sobre articulos antes de este fix.
            await guarda.BloquearFilaAsync<Articulo>(id, ct);

            // La ÚNICA lectura del artículo, nacida bajo el lock. Una segunda no arreglaría nada:
            // con la entidad ya trackeada, la relectura resuelve contra el identity map y devuelve
            // la MISMA instancia vieja.
            articulo = await BuscarAsync(id, ct);

            // Task 4.2 (design: Reconciliation triggers): capturado ANTES de sobrescribir el campo
            // — es el único momento en que "antes" y "después" conviven en memoria. Ahora sale de
            // la lectura bajo el lock: leído de la foto pre-lock, una edición concurrente que
            // flipeara controla_lote entre esa foto y el commit hacía que esta detección de
            // transición viera el "antes" equivocado y saltara (o disparara de más) la
            // reconciliación de lotes.
            //
            // Honestidad sobre la cobertura (judgment-day, los dos jueces): esta mitad NO tiene test
            // de carrera propio. Sale de la MISMA lectura única que sí está probada por la carrera
            // de `activo`, así que el mecanismo está cubierto; lo que no está afirmado por ningún
            // test es la consecuencia observable —que la reconciliación de lotes dispare o no—,
            // porque el andamiaje de lotes vive en ReconciliacionTests y el de carreras en
            // ArticulosReferenciasVivasTests, sin colocar. Es deuda declarada, no cobertura supuesta.
            controlaLoteAnterior = articulo.ControlaLote;

            await ExigirAreaValidaAsync(datos.IdArea, ct);
            await ExigirCategoriaValidaAsync(datos.IdCategoria, ct);
            await ExigirMarcaValidaAsync(datos.IdMarca, ct);
            await ExigirGrupoValidoAsync(datos.IdGrupo, ct);
            await ExigirProveedorHabitualValidoAsync(datos.IdProveedorHabitual, ct);

            articulo.Nombre = nombre;
            articulo.Descripcion = descripcion;
            articulo.IdArea = datos.IdArea;
            articulo.IdCategoria = datos.IdCategoria;
            articulo.IdMarca = datos.IdMarca;
            articulo.IdGrupo = datos.IdGrupo;
            articulo.IdProveedorHabitual = datos.IdProveedorHabitual;
            articulo.IdAlicuotaIva = datos.IdAlicuotaIva;
            articulo.UnidadVenta = datos.UnidadVenta;
            articulo.UnidadesPorBulto = datos.UnidadesPorBulto;
            articulo.EsProducto = datos.EsProducto;
            articulo.CostoLista = datos.CostoLista;
            articulo.DescuentoProveedor = datos.DescuentoProveedor;
            articulo.CostoNominal = datos.CostoNominal;
            articulo.DisponibleParaTodas = datos.DisponibleParaTodas;
            articulo.Activo = datos.Activo;
            articulo.ControlaLote = datos.ControlaLote;
            articulo.UpdatedAt = reloj.Ahora;

            // Reemplaza el subconjunto entero (INSERT/DELETE físico, sin historial que preservar —
            // ArticuloEmpresa es PK-only, task 1.4): más simple que calcular un delta, y el
            // volumen esperado por artículo es bajo (subconjunto de empresas de UN tenant).
            var filasActuales = await db.ArticulosEmpresas.Where(ae => ae.IdArticulo == id).ToListAsync(ct);
            db.ArticulosEmpresas.RemoveRange(filasActuales);

            if (!datos.DisponibleParaTodas && idsEmpresas is { Count: > 0 })
            {
                AgregarFilasDeSubset(id, idTenant, idsEmpresas);
            }

            await db.SaveChangesAsync(ct);
            await transaccion.CommitAsync(ct);
        });

        // Task 4.2 (design: Reconciliation triggers — "articulos.controla_lote flipped false →
        // true"): alcance = ese artículo, todas las PV (ReconciliarAsync ya filtra a las de
        // empresas con lotes_habilitado efectivo). Un flip a false no reconcilia nada (spec: "the
        // false → true transition... a flip to false reconciles nothing").
        //
        // Contrato de fallo parcial (diseño aceptado, sin transacción ambiente entre el commit de
        // arriba y este ReconciliarAsync): el flip de controla_lote ya quedó COMMITEADO antes de
        // este punto. Si ReconciliarAsync falla a mitad de camino (algunos pares (artículo, PV) ya
        // reconciliados, otros no), el request devuelve un 500 genérico y el flip queda commiteado
        // con reconciliación incompleta. No hay rollback del flip ni señal adicional que distinga
        // "reconciliación completa" de "parcial" — la recuperación es el self-healing por
        // construcción de ReconciliarParAsync (residuo recomputado desde el estado actual en la
        // próxima corrida) o un POST manual a /api/stock/lotes/reconciliacion sobre el mismo
        // alcance.
        if (!controlaLoteAnterior && datos.ControlaLote)
        {
            await servicioDeLotes.ReconciliarAsync(articulo!.Id, idPuntoVenta: null, ct);
        }

        return Proyectar(articulo!, datos.DisponibleParaTodas ? Array.Empty<int>() : (IReadOnlyList<int>?)idsEmpresas ?? Array.Empty<int>());
    }

    /// <summary>Baja lógica: escribe <c>deleted_at</c>, no borra la fila. Los
    /// <see cref="CodigoBarra"/>/<see cref="ArticuloEmpresa"/> asociados quedan como están —
    /// sin cascada, mismo criterio que <see cref="Proveedores.ServicioDeProveedores.EliminarAsync"/>
    /// (sin guard de fila protegida a diferencia de clientes: artículos no tiene un equivalente
    /// al Consumidor Final).
    ///
    /// La paridad con esa baja de proveedores era declarativa y ahora es real: allá el lock de fila
    /// se toma ANTES de cargar la entidad y acá no se tomaba ninguno, así que dos bajas concurrentes
    /// del mismo artículo leían las dos la fila viva y la segunda re-estampaba un <c>deleted_at</c>
    /// NUEVO sobre el de la primera en vez de rendir 404. Mismo shape sin reintento + lock antes de
    /// la carga que <c>ServicioDeCatalogo{T,TListado,TAlta}.EliminarAsync</c>, y el mismo lock de
    /// fila que <see cref="ActualizarAsync"/>, así que editar y dar de baja el mismo artículo
    /// también se serializan entre sí.</summary>
    public async Task EliminarAsync(int id, CancellationToken ct = default)
    {
        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            await guarda.BloquearFilaAsync<Articulo>(id, ct);

            var articulo = await BuscarAsync(id, ct);

            var ahora = reloj.Ahora;
            articulo.DeletedAt = ahora;
            articulo.UpdatedAt = ahora;

            await db.SaveChangesAsync(ct);
            await transaccion.CommitAsync(ct);
        });
    }

    /// <summary>db-error-backstops: pre-chequeo best-effort — el backstop real sigue siendo
    /// <c>ux_codigos_barra_codigo_tenant</c> (23505 → 409 <c>codigo_barra_duplicado</c>,
    /// <c>ManejadorDeErrores</c>). <see cref="CodigoBarra"/> hereda de
    /// <see cref="Ways.Domain.Common.EntidadTenant"/>: <c>IdTenant</c> se auto-estampa en
    /// <c>SaveChangesAsync</c>, sin necesitar el estampado manual que sí requiere
    /// <see cref="ArticuloEmpresa"/> (task 1.4).</summary>
    public async Task<CodigoBarraListado> AgregarCodigoBarraAsync(
        int idArticulo, AltaCodigoBarra datos, CancellationToken ct = default)
    {
        var articulo = await BuscarAsync(idArticulo, ct);

        var codigo = NormalizarRequerido(datos.Codigo, "codigo", 50);

        await ExigirCodigoBarraDisponibleAsync(codigo, ct);

        var ahora = reloj.Ahora;
        var codigoBarra = new CodigoBarra
        {
            IdArticulo = articulo.Id,
            Codigo = codigo,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };

        db.CodigosBarra.Add(codigoBarra);
        await db.SaveChangesAsync(ct);

        return new CodigoBarraListado(codigoBarra.Id, codigoBarra.IdArticulo, codigoBarra.Codigo, codigoBarra.Activo);
    }

    /// <summary>Lista los códigos de barra activos del artículo (spec: Barcode Add/Remove
    /// Management) — el filtro global <c>BajaLogica</c> ya deja afuera los dados de baja, sin
    /// necesitar un <c>Where</c> explícito por <c>Activo</c>/<c>DeletedAt</c>. ADR-8: mismo 404
    /// uniforme que <see cref="AgregarCodigoBarraAsync"/>/<see cref="EliminarCodigoBarraAsync"/>
    /// si el artículo no existe o es de otro tenant.</summary>
    public async Task<IReadOnlyList<CodigoBarraListado>> ListarCodigosBarraAsync(
        int idArticulo, CancellationToken ct = default)
    {
        await BuscarAsync(idArticulo, ct);

        return await db.CodigosBarra
            .Where(c => c.IdArticulo == idArticulo)
            .OrderBy(c => c.Id)
            .Select(c => new CodigoBarraListado(c.Id, c.IdArticulo, c.Codigo, c.Activo))
            .ToListAsync(ct);
    }

    /// <summary>Baja lógica del código de barras — deja el código reutilizable después
    /// (índice parcial <c>WHERE deleted_at IS NULL</c>), mismo criterio que la baja de
    /// <c>cuit</c> en <see cref="Proveedores.ServicioDeProveedores"/>.</summary>
    public async Task EliminarCodigoBarraAsync(int idArticulo, int idCodigoBarra, CancellationToken ct = default)
    {
        // ADR-8: mismo 404 si el artículo no existe o es de otro tenant, antes de buscar el
        // código de barras.
        await BuscarAsync(idArticulo, ct);

        var codigoBarra = await db.CodigosBarra
            .FirstOrDefaultAsync(c => c.Id == idCodigoBarra && c.IdArticulo == idArticulo, ct)
            ?? throw ErrorDominio.NoEncontrado(
                $"No existe el código de barras {idCodigoBarra} del artículo {idArticulo}.");

        var ahora = reloj.Ahora;
        codigoBarra.DeletedAt = ahora;
        codigoBarra.UpdatedAt = ahora;

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Asocia al artículo el código con que un proveedor lo imprime en su factura.
    /// Idempotente: repetir el mismo (proveedor, código) sobre el MISMO artículo devuelve la fila
    /// existente sin crear otra; sobre OTRO artículo es 409 <c>codigo_proveedor_duplicado</c>.
    /// Artículo y proveedor se bloquean vivos dentro de la transacción (FOR KEY SHARE), como en
    /// <see cref="CrearAsync"/>, y un 404 cubre inexistente, de otro tenant o dado de baja.
    /// El pre-chequeo de duplicado es solo UX: el contrato real es
    /// <c>ux_codigos_proveedor_proveedor_codigo</c> (23505 → 409 en <c>ManejadorDeErrores</c>), de modo
    /// que dos asociaciones simultáneas del mismo código pueden resolverse con 409 también para el
    /// mismo artículo.</summary>
    public async Task<ResultadoDeCodigoProveedor> AgregarCodigoProveedorAsync(
        int idArticulo, AltaCodigoProveedor datos, CancellationToken ct = default)
    {
        ExigirIdRequerido(datos.IdProveedor, "id_proveedor");
        var codigo = ReglaDeCodigoProveedor.NormalizarRequerido(datos.Codigo, "codigo");

        // Sin reintento: el INSERT no es idempotente a nivel de estrategia (ef-retry-safe-writes).
        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        return await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            if (!await guarda.BloquearSiEstaVivaAsync<Articulo>(idArticulo, ct))
            {
                throw ErrorDominio.NoEncontrado($"No existe el artículo {idArticulo}.");
            }

            if (!await guarda.BloquearSiEstaVivaAsync<Proveedor>(datos.IdProveedor, ct))
            {
                throw ErrorDominio.NoEncontrado($"No existe el proveedor {datos.IdProveedor}.");
            }

            var existente = await db.CodigosProveedor
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.IdProveedor == datos.IdProveedor && c.Codigo == codigo, ct);

            if (existente is not null)
            {
                if (existente.IdArticulo != idArticulo)
                {
                    throw ErrorDominio.Conflicto(
                        "codigo_proveedor_duplicado",
                        $"El código {codigo} ya está asignado a otro artículo para el proveedor {datos.IdProveedor}.");
                }

                return new ResultadoDeCodigoProveedor(Proyectar(existente), Creado: false);
            }

            var nuevo = NuevoCodigoProveedor(idArticulo, datos.IdProveedor, codigo);
            db.CodigosProveedor.Add(nuevo);
            await db.SaveChangesAsync(ct);
            await transaccion.CommitAsync(ct);

            return new ResultadoDeCodigoProveedor(Proyectar(nuevo), Creado: true);
        });
    }

    /// <summary>Spec: Margin-Based Price Suggestion — resuelve <c>margenGrupo</c>/
    /// <c>margenProveedor</c> desde las referencias del artículo y delega el cálculo puro en
    /// <see cref="SugeridorDePrecio"/>. Nunca persiste un <c>precios</c> row (design decision
    /// 8: "called but never auto-applied").</summary>
    public async Task<SugerenciaDePrecio> SugerirPrecioAsync(int idArticulo, CancellationToken ct = default)
    {
        var articulo = await BuscarAsync(idArticulo, ct);

        var margenGrupo = articulo.IdGrupo is { } idGrupo
            ? await db.Grupos.Where(g => g.Id == idGrupo).Select(g => g.Margen).FirstOrDefaultAsync(ct)
            : null;

        var margenProveedor = articulo.IdProveedorHabitual is { } idProveedor
            ? await db.Proveedores.Where(p => p.Id == idProveedor).Select(p => p.Margen).FirstOrDefaultAsync(ct)
            : null;

        var precioSugerido = SugeridorDePrecio.Sugerir(
            articulo.CostoNominal, articulo.CostoLista, articulo.DescuentoProveedor, margenGrupo, margenProveedor);

        return new SugerenciaDePrecio(precioSugerido);
    }

    /// <summary><see cref="ArticuloEmpresa.IdTenant"/> NO se auto-estampa (task 1.4: no hereda
    /// <see cref="Ways.Domain.Common.EntidadTenant"/>) — este es el punto de escritura real que el
    /// comentario de <c>WaysDbContext.AplicarFiltroDeTenantEnArticuloEmpresa</c> dejaba
    /// pendiente para esta slice: hay que asignarlo a mano, o el RLS <c>WITH CHECK</c> rechaza
    /// el INSERT con SQLSTATE 42501.</summary>
    private void AgregarFilasDeSubset(int idArticulo, int idTenant, IReadOnlyList<int> idsEmpresas)
    {
        foreach (var idEmpresa in idsEmpresas)
        {
            db.ArticulosEmpresas.Add(new ArticuloEmpresa
            {
                IdArticulo = idArticulo,
                IdEmpresa = idEmpresa,
                IdTenant = idTenant
            });
        }
    }

    private async Task<Articulo> BuscarAsync(int id, CancellationToken ct) =>
        await db.Articulos.FirstOrDefaultAsync(a => a.Id == id, ct)
            // El filtro de EF (+ RLS por debajo) ya deja invisible la fila de otro tenant —
            // esto solo cubre "no existe en absoluto" (ADR-8: mismo 404 en los dos casos).
            ?? throw ErrorDominio.NoEncontrado($"No existe el artículo {id}.");

    private int ExigirTenantDeLaSesion() =>
        contexto.IdTenant
            // GestionDeCatalogo (capa de API) ya exige admin de tenant — un actor de
            // plataforma nunca llega hasta acá. Defensa en profundidad, no un camino
            // alcanzable en operación normal.
            ?? throw new InvalidOperationException(
                "ServicioDeArticulos requiere un actor de tenant; GestionDeCatalogo es admin-only.");

    private static void ExigirIdRequerido(int valor, string campo)
    {
        if (valor <= 0)
        {
            throw new ErrorDominio($"{campo}_requerido", $"El campo {campo} es obligatorio.", 400);
        }
    }

    /// <summary>Columna <c>numeric(10,2)</c> (migración <c>ArticulosYPreciosEtapa3</c>) — sin
    /// este chequeo, Postgres respondería con 22003 y, para un valor que además viene mal
    /// formado de negocio (negativo), terminaría en un 500 sin el backstop genérico del item
    /// 22003 llegando a cubrirlo del todo (ver <c>ManejadorDeErrores</c>). Mismo criterio de
    /// clase que <c>ServicioDeProveedores.ExigirMargenValido</c>.</summary>
    private static void ExigirUnidadesPorBultoValida(decimal? valor)
    {
        if (valor is not { } v)
        {
            return;
        }

        if (v < 0 || v >= 100_000_000m)
        {
            throw new ErrorDominio(
                "unidades_por_bulto_invalido",
                "El campo unidades_por_bulto debe estar entre 0 y 99999999.99.",
                400);
        }
    }

    /// <summary>Aplica a <c>costo_lista</c>/<c>costo_nominal</c>, ambas <c>numeric(14,2)</c> —
    /// mismo bound que <c>ServicioDeClientes.ExigirLimiteCreditoValido</c> (misma precisión de
    /// columna).</summary>
    private static void ExigirCostoValido(decimal? valor, string campo)
    {
        if (valor is not { } v)
        {
            return;
        }

        if (v < 0 || v >= 1_000_000_000_000m)
        {
            throw new ErrorDominio(
                $"{campo}_invalido", $"El campo {campo} debe estar entre 0 y 999999999999.99.", 400);
        }
    }

    /// <summary>Columna <c>numeric(5,2)</c> — mismo bound que
    /// <c>ServicioDeProveedores.ExigirMargenValido</c> (misma precisión de columna, misma
    /// familia semántica de "porcentaje").</summary>
    private static void ExigirDescuentoProveedorValido(decimal? valor)
    {
        if (valor is not { } v)
        {
            return;
        }

        if (v < 0 || v >= 1000m)
        {
            throw new ErrorDominio(
                "descuento_proveedor_invalido", "El campo descuento_proveedor debe estar entre 0 y 999.99.", 400);
        }
    }

    /// <summary>fix/articulos-lock-referencias: reemplaza el pre-chequeo <c>AnyAsync</c>
    /// filtrado (sin lock) por <see cref="GuardaDeReferencias.BloquearSiEstaVivaAsync{T}"/>,
    /// dentro de la transacción de escritura — cierra, del lado de este escritor, el residual
    /// documentado en <see cref="GuardaDeReferencias"/> (ver su doc-comment para el porqué del
    /// <c>FOR KEY SHARE</c>). El backstop real sigue siendo <c>fk_articulos_area</c> (compuesta,
    /// 23503 → 400 <c>referencia_invalida</c>, genérico desde la Slice 1).</summary>
    private async Task ExigirAreaValidaAsync(int id, CancellationToken ct)
    {
        if (!await guarda.BloquearSiEstaVivaAsync<Area>(id, ct))
        {
            throw new ErrorDominio("referencia_invalida", $"No existe el área {id}.", 400);
        }
    }

    /// <summary>Mismo criterio que <see cref="ExigirAreaValidaAsync"/>, para
    /// <c>fk_articulos_categoria</c> — <c>IdCategoria</c> es nullable (spec: Articulo Schema At
    /// Rest).</summary>
    private async Task ExigirCategoriaValidaAsync(int? id, CancellationToken ct)
    {
        if (id is null)
        {
            return;
        }

        if (!await guarda.BloquearSiEstaVivaAsync<Categoria>(id.Value, ct))
        {
            throw new ErrorDominio("referencia_invalida", $"No existe la categoría {id}.", 400);
        }
    }

    /// <summary>Mismo criterio que <see cref="ExigirAreaValidaAsync"/>, para
    /// <c>fk_articulos_marca</c>.</summary>
    private async Task ExigirMarcaValidaAsync(int? id, CancellationToken ct)
    {
        if (id is null)
        {
            return;
        }

        if (!await guarda.BloquearSiEstaVivaAsync<Marca>(id.Value, ct))
        {
            throw new ErrorDominio("referencia_invalida", $"No existe la marca {id}.", 400);
        }
    }

    /// <summary>Mismo criterio que <see cref="ExigirAreaValidaAsync"/>, para
    /// <c>fk_articulos_grupo</c>.</summary>
    private async Task ExigirGrupoValidoAsync(int? id, CancellationToken ct)
    {
        if (id is null)
        {
            return;
        }

        if (!await guarda.BloquearSiEstaVivaAsync<Grupo>(id.Value, ct))
        {
            throw new ErrorDominio("referencia_invalida", $"No existe el grupo {id}.", 400);
        }
    }

    /// <summary>Mismo criterio que <see cref="ExigirAreaValidaAsync"/>, para
    /// <c>fk_articulos_proveedor_habitual</c>.</summary>
    private async Task ExigirProveedorHabitualValidoAsync(int? id, CancellationToken ct)
    {
        if (id is null)
        {
            return;
        }

        if (!await guarda.BloquearSiEstaVivaAsync<Proveedor>(id.Value, ct))
        {
            throw new ErrorDominio("referencia_invalida", $"No existe el proveedor {id}.", 400);
        }
    }

    /// <summary><c>alicuotas_iva</c> es <c>[global]</c> (no
    /// <see cref="Ways.Domain.Common.EntidadTenant"/>, ADR-11 gate #4): sin alcance de tenant
    /// que filtrar, mismo criterio que
    /// <c>ServicioDeClientes.ExigirCondicionFiscalValidaAsync</c>.</summary>
    private async Task ExigirAlicuotaIvaValidaAsync(int id, CancellationToken ct)
    {
        if (!await db.AlicuotasIva.AnyAsync(a => a.Id == id, ct))
        {
            throw new ErrorDominio("referencia_invalida", $"No existe la alícuota de IVA {id}.", 400);
        }
    }

    /// <summary>Spec: Cross-tenant empresa reference is blocked — pre-chequeo tenant-scoped
    /// (no <c>IgnoreQueryFilters</c>, per db-error-backstops) antes de escribir cualquier fila
    /// de <see cref="ArticuloEmpresa"/>: el filtro de EF ya deja afuera una empresa de otro
    /// tenant de <c>db.Empresas</c>, así que este chequeo cubre "no existe" y "es de otro
    /// tenant" con el mismo 400.</summary>
    private async Task ExigirEmpresasValidasAsync(IReadOnlyList<int> idsEmpresas, CancellationToken ct)
    {
        foreach (var idEmpresa in idsEmpresas)
        {
            if (!await db.Empresas.AnyAsync(e => e.Id == idEmpresa, ct))
            {
                throw new ErrorDominio("referencia_invalida", $"No existe la empresa {idEmpresa}.", 400);
            }
        }
    }

    /// <summary>db-error-backstops: pre-chequeo best-effort — el backstop real sigue siendo
    /// <c>ux_articulos_codigo_interno</c> (23505 → 409 <c>codigo_interno_duplicado</c>,
    /// <c>ManejadorDeErrores</c>). No reemplaza la constraint: dos altas concurrentes con el
    /// mismo <c>codigo_interno</c> pueden pasar las dos este chequeo y competir recién en el
    /// <c>SaveChangesAsync</c> de la que pierde (spec: "Concurrent autogeneration yields no
    /// gaps or duplicates" cubre el camino autogenerado — acá es el camino de un valor
    /// provisto por el cliente HTTP, sin ningún lock de fila que serialice la carrera por
    /// construcción, task 2.8). Sin parámetro <c>excluirId</c> (judgment-day ronda 1, item 4a,
    /// dead code removido): <c>codigo_interno</c> es inmutable (ver <see cref="EdicionArticulo"/>),
    /// así que el único llamador es <see cref="CrearAsync"/>, que nunca necesita excluir su
    /// propio id porque todavía no existe.</summary>
    private async Task ExigirCodigoInternoDisponibleAsync(string codigoInterno, CancellationToken ct)
    {
        var tomado = await db.Articulos.AnyAsync(a => a.CodigoInterno == codigoInterno, ct);

        if (tomado)
        {
            throw ErrorDominio.Conflicto(
                "codigo_interno_duplicado", $"Ya existe un artículo con el código interno {codigoInterno} en este tenant.");
        }
    }

    /// <summary>Mismo criterio que <see cref="ExigirCodigoInternoDisponibleAsync"/>, para
    /// <c>ux_codigos_barra_codigo_tenant</c> (23505 → 409 <c>codigo_barra_duplicado</c>, task
    /// 2.9) — sin <c>excluirId</c>: a diferencia del <c>codigo_interno</c> de un artículo (que
    /// se puede volver a guardar con el mismo valor en una edición), agregar un código de
    /// barras siempre es un alta nueva, nunca una edición de una fila existente.</summary>
    private async Task ExigirCodigoBarraDisponibleAsync(string codigo, CancellationToken ct)
    {
        var tomado = await db.CodigosBarra.AnyAsync(c => c.Codigo == codigo, ct);

        if (tomado)
        {
            throw ErrorDominio.Conflicto("codigo_barra_duplicado", $"Ya existe el código de barras {codigo} en este tenant.");
        }
    }

    /// <summary>Pre-chequeo best-effort del alta de artículo (db-error-backstops): el backstop real
    /// es <c>ux_codigos_proveedor_proveedor_codigo</c>.</summary>
    private async Task ExigirCodigoProveedorDisponibleAsync(int idProveedor, string codigo, CancellationToken ct)
    {
        if (await db.CodigosProveedor.AnyAsync(c => c.IdProveedor == idProveedor && c.Codigo == codigo, ct))
        {
            throw ErrorDominio.Conflicto(
                "codigo_proveedor_duplicado",
                $"El código {codigo} ya está asignado a otro artículo para el proveedor {idProveedor}.");
        }
    }

    private CodigoProveedor NuevoCodigoProveedor(int idArticulo, int idProveedor, string codigo)
    {
        var ahora = reloj.Ahora;
        return new CodigoProveedor
        {
            IdArticulo = idArticulo,
            IdProveedor = idProveedor,
            Codigo = codigo,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
    }

    private static CodigoProveedorListado Proyectar(CodigoProveedor c) =>
        new(c.Id, c.IdArticulo, c.IdProveedor, c.Codigo);

    private static string? NormalizarCodigoInternoOpcional(string? valor)
    {
        var limpio = valor?.Trim();

        if (string.IsNullOrEmpty(limpio))
        {
            return null;
        }

        if (limpio.Length > 30)
        {
            throw new ErrorDominio(
                "codigo_interno_muy_largo", "El campo codigo_interno no puede superar los 30 caracteres.", 400);
        }

        return limpio;
    }

    private static string? NormalizarOpcional(string? valor, string campo, int? largoMaximo)
    {
        var limpio = valor?.Trim();

        if (string.IsNullOrEmpty(limpio))
        {
            return null;
        }

        if (largoMaximo is { } maximo && limpio.Length > maximo)
        {
            throw new ErrorDominio(
                $"{campo}_muy_largo", $"El campo {campo} no puede superar los {maximo} caracteres.", 400);
        }

        return limpio;
    }

    private static string NormalizarRequerido(string? valor, string campo, int largoMaximo)
    {
        var limpio = valor?.Trim() ?? string.Empty;

        if (limpio.Length == 0)
        {
            throw new ErrorDominio($"{campo}_requerido", $"El campo {campo} es obligatorio.", 400);
        }

        if (limpio.Length > largoMaximo)
        {
            throw new ErrorDominio(
                $"{campo}_muy_largo", $"El campo {campo} no puede superar los {largoMaximo} caracteres.", 400);
        }

        return limpio;
    }

    private static ArticuloListado Proyectar(Articulo a, IReadOnlyList<int> idsEmpresas) => new(
        a.Id, a.CodigoInterno, a.Nombre, a.Descripcion, a.IdArea, a.IdCategoria, a.IdMarca, a.IdGrupo,
        a.IdProveedorHabitual, a.IdAlicuotaIva, a.UnidadVenta, a.UnidadesPorBulto, a.EsProducto,
        a.CostoLista, a.DescuentoProveedor, a.CostoNominal, a.DisponibleParaTodas, idsEmpresas, a.Activo,
        a.ControlaLote);
}
