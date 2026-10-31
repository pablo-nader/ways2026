using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Application.Bajas;
using Ways.Application.Familias;
using Ways.Application.Parametros;
using Ways.Application.Precios;
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
///
/// Familias de artículos (doc 10 §3): <see cref="ActualizarAsync"/> respeta el protocolo de locks de las
/// familias — lock de membresía como primera sentencia (exclusivo solo con el alcance <c>SoloEste</c>),
/// después las filas de <c>articulos</c> y, al final, los chequeos de catálogo — y aplica los dieciséis campos
/// compartidos a TODOS los miembros vivos solo cuando el pedido cambia alguno y el cliente eligió el alcance
/// <c>Familia</c>; los campos propios son siempre del artículo editado. <see cref="CrearAsync"/> con
/// <see cref="AltaArticulo.IdFamilia"/> es un cambio de pertenencia: toma el lock de membresía exclusivo,
/// exige que los dieciséis campos compartidos del pedido sean idénticos a los del miembro vivo de menor id y copia
/// su estado de precios con <see cref="ServicioDePrecios"/>, que sigue siendo el único escritor de
/// <c>precios</c>.
/// </summary>
public class ServicioDeArticulos(
    IWaysDbContext db, IRelojDelSistema reloj, IContextoDeUsuario contexto, ServicioDeLotes servicioDeLotes,
    GuardaDeReferencias guarda, ServicioDePrecios servicioDePrecios, ServicioDeParametros servicioDeParametros)
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
    /// <param name="soloVendibles"><c>true</c> deja afuera los artículos que no están disponibles a la venta
    /// (<see cref="Articulo.DisponibleVenta"/>): es el filtro de la búsqueda que usa la caja. Los listados de compras y
    /// del back-office no lo mandan y los ven a todos.</param>
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
        bool soloVendibles = false,
        CancellationToken ct = default)
    {
        pagina = Math.Max(pagina, 1);
        tamanio = Math.Clamp(tamanio, 1, TamanioMaximoDePagina);

        var query = db.Articulos.AsQueryable();

        if (soloVendibles)
        {
            query = query.Where(a => a.DisponibleVenta);
        }

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
                    .FirstOrDefault(),
                TieneImagen = db.ImagenesArticulo.Any(i => i.IdArticulo == a.Id)
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
                x.Articulo.CostoListaPor, x.Articulo.CostoListaIncluyeIva, x.Articulo.DescuentoProveedor, x.Articulo.CostoNominal, x.Articulo.DisponibleParaTodas,
                Array.Empty<int>(), x.Articulo.Activo, x.Articulo.ControlaLote,
                x.Articulo.AcumulaEnVenta, x.Articulo.IdFamilia, x.CodigoProveedor,
                x.Articulo.DisponibleOnline, x.Articulo.DisponibleVenta, x.TieneImagen, x.Articulo.TeclaAtajo,
                x.Articulo.ImpuestoInterno))
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

        var detalle = Proyectar(articulo, idsEmpresas) with
        {
            TieneImagen = await db.ImagenesArticulo.AnyAsync(i => i.IdArticulo == id, ct)
        };
        return PuedeVerCostos ? detalle : SinCostos(detalle);
    }

    /// <summary>Los costos son del back-office (admin y supervisor): el vendedor lee este mismo
    /// endpoint desde el POS y nunca los recibe.</summary>
    private bool PuedeVerCostos => contexto.Rol is RolConocido.Admin or RolConocido.Supervisor;

    private static ArticuloListado SinCostos(ArticuloListado a) =>
        a with
        {
            CostoLista = null,
            CostoListaPor = null,
            CostoListaIncluyeIva = null,
            DescuentoProveedor = null,
            CostoNominal = null,
            ImpuestoInterno = null
        };

    /// <summary>Asigna <c>codigo_interno</c> de forma atómica (design decision 6) cuando se
    /// omite, dentro de la misma transacción que el INSERT — igual criterio que
    /// <see cref="Clientes.ServicioDeClientes.CrearAsync"/>. Cuando se provee, se valida único
    /// por tenant antes de abrir la transacción (pre-chequeo best-effort, db-error-backstops:
    /// el backstop real es <c>ux_articulos_codigo_interno</c>).
    ///
    /// <para>Con <see cref="AltaArticulo.IdFamilia"/> el artículo entra a una familia (doc 10 §3), en este orden
    /// dentro de la transacción: lock de membresía EXCLUSIVO como primera sentencia, los 5 chequeos de
    /// catálogo, la familia viva y activa leída y bloqueada <c>FOR SHARE</c>, la referencia —el miembro vivo
    /// de menor id, una sola lectura bajo esos locks— y la comparación de los dieciséis campos compartidos del
    /// pedido; recién entonces el INSERT del artículo y la copia de su estado de precios
    /// (<see cref="ServicioDePrecios.CopiarEstadoDePreciosAlNuevoMiembroAsync"/>), todo en la misma
    /// transacción y con el mismo "ahora". Los rechazos de los chequeos de catálogo y de la familia ocurren
    /// antes de insertar. El 409 <c>codigo_proveedor_duplicado</c> y los respaldos de la base (índices únicos y
    /// claves foráneas) ocurren al insertar o después de haber insertado el artículo: la transacción se revierte
    /// entera y el contexto suelta lo que esta operación le agregó (<see cref="RastreoDeEntidades"/>). Sin
    /// <c>IdFamilia</c> no corren los pasos propios de la familia —el lock de membresía, la lectura y el bloqueo de
    /// la familia, la referencia, la comparación de los campos compartidos y la copia de precios—; los 5 chequeos
    /// de catálogo, el 409 <c>codigo_proveedor_duplicado</c> cuando el pedido trae <c>codigo_proveedor</c>, los
    /// respaldos de la base y el rollback con la suelta del contexto son los mismos en todo alta.</para>
    ///
    /// <para>Con <see cref="AltaArticulo.NombreFamiliaNueva"/> la misma transacción crea la familia y le da al artículo
    /// como único miembro: el lock de membresía exclusivo es la primera sentencia, igual que con
    /// <c>IdFamilia</c>, y la familia se inserta —con su propio <c>SaveChangesAsync</c>, que da el id— justo antes del
    /// artículo. No hay referencia, ni comparación de campos compartidos, ni copia de precios. El nombre se pre-chequea
    /// como en <see cref="ServicioDeAgrupacionDeFamilias.CrearAsync"/>; el respaldo real es <c>ux_familias_nombre</c>, que
    /// responde <c>409 familia_nombre_duplicado</c> y revierte el alta entera. Ni el alta de artículos ni la creación
    /// de una familia escriben auditoría propia, así que no hay filas de auditoría que agregar.</para></summary>
    public async Task<ArticuloListado> CrearAsync(AltaArticulo datos, CancellationToken ct = default)
    {
        // Un descuento sin cargar es 0, nunca nulo: el cálculo y lo guardado coinciden.
        datos = datos with { DescuentoProveedor = datos.DescuentoProveedor ?? 0m };

        var nombre = NormalizarRequerido(datos.Nombre, "nombre", 150);
        var descripcion = NormalizarOpcional(datos.Descripcion, "descripcion", null);
        var codigoInterno = NormalizarCodigoInternoOpcional(datos.CodigoInterno);
        var codigoProveedor = ReglaDeCodigoProveedor.NormalizarOpcional(datos.CodigoProveedor, "codigo_proveedor");
        ReglaDeCodigoProveedor.ExigirProveedor(codigoProveedor, datos.IdProveedorHabitual);
        var nombreFamiliaNueva = NormalizarNombreDeFamiliaNueva(datos);

        ExigirIdRequerido(datos.IdArea, "id_area");
        ExigirIdRequerido(datos.IdAlicuotaIva, "id_alicuota_iva");
        ExigirUnidadesPorBultoValida(datos.UnidadesPorBulto);
        ExigirCostoValido(datos.CostoLista, "costo_lista");
        ExigirDescuentoProveedorValido(datos.DescuentoProveedor);
        ExigirImpuestoInternoValido(datos.ImpuestoInterno);
        ExigirCostoListaPorDefinido(datos.CostoListaPor);
        ExigirUnidadesParaCostoPorBulto(datos.CostoListaPor, datos.UnidadesPorBulto);
        ExigirTeclaAtajoDefinida(datos.TeclaAtajo);
        ReglaDeArticulos.ValidarImporte(datos.UnidadVenta, AcumulaEnVentaDe(datos), datos.TeclaAtajo);

        var porcentajeIva = await ExigirAlicuotaIvaValidaAsync(datos.IdAlicuotaIva, ct);
        var costoNominal = CalcularCostoNominal(
            datos.CostoLista, datos.CostoListaPor, datos.CostoListaIncluyeIva, datos.UnidadesPorBulto, porcentajeIva,
            datos.DescuentoProveedor, datos.ImpuestoInterno);

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

        if (datos.TeclaAtajo is { } teclaAtajo)
        {
            await ExigirTeclaAtajoDisponibleAsync(teclaAtajo, excluirId: null, ct);
        }

        if (nombreFamiliaNueva is not null)
        {
            await NombreDeFamilia.ExigirDisponibleAsync(db, nombreFamiliaNueva, excluirId: null, ct);
        }

        var idTenant = ExigirTenantDeLaSesion();

        // Sin reintento: el INSERT no es idempotente y no hay clave de idempotencia — el
        // codigo_interno autogenerado se resuelve DENTRO de la transacción, así que un reintento
        // tomaría uno nuevo y daría de alta un segundo artículo.
        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        return await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            var conexion = await ObtenerConexionAbiertaAsync(ct);
            var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

            // Entrar a una familia es un cambio de PERTENENCIA: el lock de membresía EXCLUSIVO es la primera
            // sentencia de la transacción, antes que los locks de los catálogos. Excluye a los demás escritores
            // que toman ese lock —la escritura de precios y la edición de artículos, compartido salvo con "solo
            // este", y la confirmación de compras, compartido—, así que ninguno está a mitad de camino mientras
            // se lee la referencia y se copian sus precios. Un alta sin familia no cambia ninguna pertenencia y
            // no lo toma. Crear una familia nueva con el artículo como único miembro también cambia la pertenencia
            // y lo toma igual.
            if (datos.IdFamilia is not null || nombreFamiliaNueva is not null)
            {
                await LockDeMembresiaDeFamilias.TomarExclusivoAsync(conexion, transaccionCruda, idTenant, ct);
            }

            // fix/articulos-lock-referencias: primeras sentencias del lambda —después del lock de membresía,
            // si hay familia— bajo el lock de GuardaDeReferencias.BloquearSiEstaVivaAsync, no el pre-chequeo
            // AnyAsync filtrado de antes (ver el doc-comment de la clase). Mismo orden área → categoría →
            // marca → grupo → proveedor que tenían los pre-chequeos, mismo código/mensaje de error.
            await ExigirAreaValidaAsync(datos.IdArea, ct);
            await ExigirCategoriaValidaAsync(datos.IdCategoria, ct);
            await ExigirMarcaValidaAsync(datos.IdMarca, ct);
            await ExigirGrupoValidoAsync(datos.IdGrupo, ct);
            await ExigirProveedorHabitualValidoAsync(datos.IdProveedorHabitual, ct);

            // La familia se valida DESPUÉS de los catálogos del pedido —una referencia inexistente es un 400
            // aunque además el pedido difiera de la familia— y ANTES de asignar el código interno y de insertar
            // nada: un rechazo no deja ni el artículo ni el contador consumido.
            int? idDeLaReferencia = null;
            var costoNominalAGuardar = costoNominal;

            if (datos.IdFamilia is { } idFamilia)
            {
                var referencia = await ExigirIngresoALaFamiliaAsync(
                    conexion, transaccionCruda, idFamilia, idTenant, datos, costoNominal, ct);
                idDeLaReferencia = referencia.Id;
                costoNominalAGuardar = referencia.CostoNominal;
            }

            var codigoFinal = codigoInterno;
            if (codigoFinal is null)
            {
                await AsignadorDeCodigoInternoArticulo.AsegurarContadorAsync(db, idTenant, ct);
                var numero = await AsignadorDeCodigoInternoArticulo.AsignarSiguienteAsync(db, idTenant, ct);
                codigoFinal = numero.ToString(CultureInfo.InvariantCulture);
            }

            // Desde acá se escribe. Cualquier fallo —también el de un guardado— revierte la transacción pero
            // deja rastreado en el contexto lo que esta operación agregó: el artículo, sus filas hijas, las de
            // precio y las de auditoría, en el estado en que hayan quedado. El contexto vive todo el request,
            // así que se suelta antes de propagar el error (solo eso, no lo que el llamador ya tenía
            // rastreado): un guardado posterior sobre el mismo contexto no puede volver a escribirlo.
            var yaRastreadas = RastreoDeEntidades.Instantanea(db);

            try
            {
                var ahora = reloj.Ahora;
                var idFamiliaDelArticulo = datos.IdFamilia;

                // La familia nueva se inserta antes que el artículo, que la referencia por su id, y en la misma
                // transacción: un nombre tomado en una carrera la rechaza acá con el 23505 de ux_familias_nombre
                // y no queda ni la familia ni el artículo. El artículo es su único miembro: no hay referencia
                // contra la que comparar los campos compartidos ni precios que copiar.
                if (nombreFamiliaNueva is not null)
                {
                    var familia = new Familia { Nombre = nombreFamiliaNueva, Activo = true, CreatedAt = ahora, UpdatedAt = ahora };
                    db.Familias.Add(familia);
                    await db.SaveChangesAsync(ct);
                    idFamiliaDelArticulo = familia.Id;
                }

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
                    CostoListaPor = datos.CostoListaPor,
                    CostoListaIncluyeIva = datos.CostoListaIncluyeIva,
                    DescuentoProveedor = datos.DescuentoProveedor,
                    ImpuestoInterno = datos.ImpuestoInterno,
                    CostoNominal = costoNominalAGuardar,
                    DisponibleParaTodas = datos.DisponibleParaTodas,
                    Activo = datos.Activo,
                    ControlaLote = datos.ControlaLote,
                    AcumulaEnVenta = AcumulaEnVentaDe(datos),
                    TeclaAtajo = datos.TeclaAtajo,
                    IdFamilia = idFamiliaDelArticulo,
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

                // El miembro nuevo nace con el estado de precios de la referencia, en esta misma transacción y con
                // el mismo "ahora" que el artículo. ServicioDePrecios es el único escritor de precios: acá solo se
                // le pide la copia, y las filas se guardan con este SaveChangesAsync.
                if (idDeLaReferencia is { } idArticuloDeReferencia)
                {
                    await servicioDePrecios.CopiarEstadoDePreciosAlNuevoMiembroAsync(
                        articulo.Id, idArticuloDeReferencia, idTenant, ahora, ct);
                    await db.SaveChangesAsync(ct);
                }

                await transaccion.CommitAsync(ct);

                // Sin reconciliación acá (a diferencia de ActualizarAsync): un artículo recién creado
                // no puede tener stock preexistente, así que cualquier corrida sería un no-op —
                // design decisión 13, el residuo de un par sin fila de stock siempre es cero.
                return Proyectar(articulo, datos.DisponibleParaTodas ? Array.Empty<int>() : (IReadOnlyList<int>?)idsEmpresas ?? Array.Empty<int>());
            }
            catch
            {
                RastreoDeEntidades.SoltarLoAgregadoDesde(db, yaRastreadas);
                throw;
            }
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

        // alcance_invalido (400): un ordinal fuera del enum no cae en silencio en ningún destino. Es una
        // validación del payload como las de abajo: no abre transacción ni lee nada.
        var modo = ServicioDePrecios.ModoDeLaSolicitud(datos.Alcance);

        var nombre = NormalizarRequerido(datos.Nombre, "nombre", 150);
        var descripcion = NormalizarOpcional(datos.Descripcion, "descripcion", null);

        // Un descuento sin cargar es 0, nunca nulo: el cálculo y lo guardado coinciden.
        datos = datos with { DescuentoProveedor = datos.DescuentoProveedor ?? 0m };

        ExigirIdRequerido(datos.IdArea, "id_area");
        ExigirIdRequerido(datos.IdAlicuotaIva, "id_alicuota_iva");
        ExigirUnidadesPorBultoValida(datos.UnidadesPorBulto);
        ExigirCostoValido(datos.CostoLista, "costo_lista");
        ExigirDescuentoProveedorValido(datos.DescuentoProveedor);
        ExigirImpuestoInternoValido(datos.ImpuestoInterno);

        if (datos.CostoListaPor is { } costoListaPorPedido)
        {
            ExigirCostoListaPorDefinido(costoListaPorPedido);
        }

        ExigirTeclaAtajoDefinida(datos.TeclaAtajo);

        var porcentajeIva = await ExigirAlicuotaIvaValidaAsync(datos.IdAlicuotaIva, ct);

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
        List<int> idsConControlDeLoteActivado = [];

        await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            var conexion = await ObtenerConexionAbiertaAsync(ct);
            var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

            // PRIMERA sentencia de la transacción: el lock de membresía de familias, exclusivo solo cuando
            // el modo puede sacar al artículo de su familia ("solo este") y compartido en cualquier otro
            // caso, incluido el artículo sin familia. Se elige antes de leer nada y no se promueve después.
            //
            // Orden global de locks de las familias: membresía → filas de articulos ascendentes → pares de
            // precios. Los 5 chequeos de catálogo de más abajo (FOR KEY SHARE) van DESPUÉS de las filas de
            // articulos, como siempre: la baja de un catálogo toma el orden inverso —FOR UPDATE sobre el
            // catálogo y después LEE articulos— pero lo lee SIN lock (InspectorDeUso es read-only), así que
            // nunca espera por estas filas y no hay ciclo.
            if (ReglaDeFamilias.RequiereLockExclusivo(modo))
            {
                await LockDeMembresiaDeFamilias.TomarExclusivoAsync(conexion, transaccionCruda, idTenant, ct);
            }
            else
            {
                await LockDeMembresiaDeFamilias.TomarCompartidoAsync(conexion, transaccionCruda, idTenant, ct);
            }

            // Pertenencia bajo el lock, filas bloqueadas y LA ÚNICA lectura de las entidades, en ese orden
            // (ver BloquearYLeerAsync). Una segunda lectura no arreglaría nada: con la entidad ya
            // trackeada, la relectura resuelve contra el identity map y devuelve la MISMA instancia vieja.
            var bloqueados = await BloquearYLeerAsync(conexion, transaccionCruda, id, idTenant, modo, ct);
            var editado = bloqueados.Editado;
            articulo = editado;

            // La base del costo de lista y su IVA, si el pedido no los trae, son los guardados: salen de la misma
            // lectura única bajo los locks, igual que acumula_en_venta. El costo nominal no se recibe del cliente: se
            // recalcula solo si cambió alguna entrada del cálculo respecto de lo guardado; si no, se conserva el
            // guardado, que puede ser el costo efectivo que dejó una compra.
            var costoListaPor = datos.CostoListaPor ?? editado.CostoListaPor;
            var costoListaIncluyeIva = datos.CostoListaIncluyeIva ?? editado.CostoListaIncluyeIva;
            var impuestoInterno = datos.ImpuestoInterno ?? editado.ImpuestoInterno;
            ExigirUnidadesParaCostoPorBulto(costoListaPor, datos.UnidadesPorBulto);

            var porcentajeIvaGuardado = editado.IdAlicuotaIva == datos.IdAlicuotaIva
                ? porcentajeIva
                : await LeerPorcentajeDeAlicuotaIvaAsync(editado.IdAlicuotaIva, ct) ?? porcentajeIva;
            var cambiaElCalculo = CalculadorDeCostoNominal.CambiaAlgunaEntrada(
                new CalculadorDeCostoNominal.Entradas(
                    editado.CostoLista, editado.CostoListaPor, editado.CostoListaIncluyeIva, editado.UnidadesPorBulto,
                    porcentajeIvaGuardado, editado.DescuentoProveedor, editado.ImpuestoInterno),
                new CalculadorDeCostoNominal.Entradas(
                    datos.CostoLista, costoListaPor, costoListaIncluyeIva, datos.UnidadesPorBulto, porcentajeIva,
                    datos.DescuentoProveedor, impuestoInterno));
            var costoNominal = cambiaElCalculo
                ? CalcularCostoNominal(
                    datos.CostoLista, costoListaPor, costoListaIncluyeIva, datos.UnidadesPorBulto, porcentajeIva,
                    datos.DescuentoProveedor, impuestoInterno)
                : editado.CostoNominal;

            await ExigirAreaValidaAsync(datos.IdArea, ct);
            await ExigirCategoriaValidaAsync(datos.IdCategoria, ct);
            await ExigirMarcaValidaAsync(datos.IdMarca, ct);
            await ExigirGrupoValidoAsync(datos.IdGrupo, ct);
            await ExigirProveedorHabitualValidoAsync(datos.IdProveedorHabitual, ct);

            // Hasta acá todo fue validar (los 5 chequeos de catálogo) y desde acá se decide: ninguna entidad se
            // muta antes de la última validación, porque un rechazo no puede dejar una mutación rastreada en
            // el contexto del request — un guardado posterior sobre ese mismo contexto la escribiría por
            // detrás. Los dieciséis campos compartidos del pedido se comparan con los ACTUALES del artículo,
            // leídos bajo los locks: leídos antes, la comparación sería sobre un estado que otro escritor
            // puede haber cambiado. Lo mismo vale para acumula_en_venta, el único campo compartido cuyo nulo en
            // el pedido significa "conservar el guardado": su valor sale de esa misma lectura, no de otra.
            var pedido = ValoresCompartidosDe(
                datos, editado.AcumulaEnVenta, costoListaPor, costoListaIncluyeIva, impuestoInterno, costoNominal);
            // Con la marca de desglose de costo, la composición del costo es propia del artículo y no cuenta como campo
            // compartido: una composición nueva que da el mismo costo nominal que la familia se guarda sin pedir alcance.
            var composicionPropia = bloqueados.PermiteDesglosarCosto;
            var resolucion = bloqueados.IdFamilia is null
                ? ResolucionDeAlcanceDeFamilia.SoloElArticulo
                : ReglaDeFamilias.ResolverEdicionDeMiembro(
                    modo,
                    cambiaCamposCompartidos:
                        ValoresCompartidosDeFamilia.De(editado).CamposDistintos(pedido, composicionPropia).Count > 0);

            if (resolucion == ResolucionDeAlcanceDeFamilia.AlcanceRequerido)
            {
                throw await ErrorDeAlcanceRequeridoDeLaEdicionAsync(
                    conexion, transaccionCruda, bloqueados.IdFamilia!.Value, idTenant, composicionPropia,
                    editado.CostoNominal, pedido.CostoNominal, ct);
            }

            // Artículo por importe (sobre el estado resultante, con lo leído bajo los locks): ninguna de estas
            // validaciones muta nada. Los CHECK de la tabla quedan de respaldo, no de contrato.
            ReglaDeArticulos.ValidarImporte(pedido.UnidadVenta, pedido.AcumulaEnVenta, datos.TeclaAtajo);

            if (datos.TeclaAtajo is { } teclaPedida && teclaPedida != editado.TeclaAtajo)
            {
                await ExigirTeclaAtajoDisponibleAsync(teclaPedida, id, ct);
            }

            if (resolucion == ResolucionDeAlcanceDeFamilia.TodaLaFamilia && pedido.UnidadVenta != UnidadVenta.Importe)
            {
                var conTecla = bloqueados.Miembros.FirstOrDefault(m => m.Id != editado.Id && m.TeclaAtajo is not null);

                if (conTecla is not null)
                {
                    throw ErrorDominio.Conflicto(
                        "familia_miembro_con_tecla_atajo",
                        $"El artículo {conTecla.CodigoInterno} de la familia tiene la tecla de atajo {conTecla.TeclaAtajo}: " +
                        "la unidad de venta de la familia no puede dejar de ser por importe mientras tenga una.");
                }
            }

            // Desde acá solo se escribe. Un solo "ahora" para todos los artículos que se escriben.
            var ahora = reloj.Ahora;

            // Task 4.2 (design: Reconciliation triggers): el valor de controla_lote de cada artículo se
            // captura ANTES de sobrescribirlo —es el único momento en que "antes" y "después" conviven en
            // memoria— y sale de la lectura bajo el lock: leído de una foto pre-lock, una edición
            // concurrente que flipeara controla_lote entre esa foto y el commit hacía que esta detección
            // de transición viera el "antes" equivocado y saltara (o disparara de más) la reconciliación
            // de lotes. Con una familia pueden ser varios los artículos que cambian.
            //
            // Honestidad sobre la cobertura (judgment-day, los dos jueces): esta detección NO tiene test
            // de carrera propio. Sale de la MISMA lectura única que sí está probada por la carrera de
            // `activo`, así que el mecanismo está cubierto; lo que no está afirmado por ningún test de
            // carrera es la consecuencia observable —que la reconciliación de lotes dispare o no—, porque
            // el andamiaje de lotes vive en ReconciliacionTests y el de carreras en
            // ArticulosReferenciasVivasTests, sin colocar. Es deuda declarada, no cobertura supuesta.
            var conCamposCompartidos = resolucion == ResolucionDeAlcanceDeFamilia.TodaLaFamilia
                ? bloqueados.Miembros
                : [editado];

            foreach (var objetivo in conCamposCompartidos)
            {
                if (!objetivo.ControlaLote && pedido.ControlaLote)
                {
                    idsConControlDeLoteActivado.Add(objetivo.Id);
                }

                // A los demás miembros de una familia con composición propia solo les llegan los once campos que siguen
                // compartidos; el editado recibe siempre su composición.
                pedido.AplicarA(objetivo, conservarComposicion: composicionPropia && objetivo.Id != editado.Id);
                objetivo.UpdatedAt = ahora;
            }

            // Los campos propios son siempre del artículo editado: los demás miembros no los tocan.
            editado.Nombre = nombre;
            editado.Descripcion = descripcion;
            editado.IdMarca = datos.IdMarca;
            editado.DisponibleParaTodas = datos.DisponibleParaTodas;
            editado.Activo = datos.Activo;
            editado.TeclaAtajo = datos.TeclaAtajo;

            if (resolucion == ResolucionDeAlcanceDeFamilia.SalirDeLaFamilia)
            {
                editado.IdFamilia = null;
            }

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
        // true"): alcance = cada artículo que flipeó, todas las PV (ReconciliarAsync ya filtra a las de
        // empresas con lotes_habilitado efectivo). Un flip a false no reconcilia nada (spec: "the
        // false → true transition... a flip to false reconciles nothing").
        //
        // Contrato de fallo parcial (diseño aceptado, sin transacción ambiente entre el commit de
        // arriba y estas reconciliaciones): el flip de controla_lote ya quedó COMMITEADO antes de
        // este punto. Si ReconciliarAsync falla a mitad de camino (algunos pares (artículo, PV) ya
        // reconciliados, otros no), el request devuelve un 500 genérico y el flip queda commiteado
        // con reconciliación incompleta. No hay rollback del flip ni señal adicional que distinga
        // "reconciliación completa" de "parcial" — la recuperación es el self-healing por
        // construcción de ReconciliarParAsync (residuo recomputado desde el estado actual en la
        // próxima corrida) o un POST manual a /api/stock/lotes/reconciliacion sobre el mismo
        // alcance. Cuando el flip alcanza a una familia entera hay una reconciliación por miembro, en
        // orden ascendente de id: si una falla, los miembros que le siguen quedan con el flip commiteado y
        // sin reconciliar, con la misma recuperación.
        foreach (var idConControlDeLote in idsConControlDeLoteActivado)
        {
            await servicioDeLotes.ReconciliarAsync(idConControlDeLote, idPuntoVenta: null, ct);
        }

        return Proyectar(articulo!, datos.DisponibleParaTodas ? Array.Empty<int>() : (IReadOnlyList<int>?)idsEmpresas ?? Array.Empty<int>())
            with { TieneImagen = await db.ImagenesArticulo.AnyAsync(i => i.IdArticulo == id, ct) };
    }

    /// <summary>Los artículos que una edición tiene bloqueados y leídos: el editado, todos los que ese
    /// pedido puede escribir (<see cref="Miembros"/>, ascendentes por id; es solo el editado cuando no se
    /// escribe a la familia) y la familia de la que es miembro (<c>null</c> ⇒ sin familia).</summary>
    private sealed record ArticulosBloqueados(
        Articulo Editado, IReadOnlyList<Articulo> Miembros, int? IdFamilia, bool PermiteDesglosarCosto = false);

    /// <summary>
    /// Paso (2) del protocolo de locks de familias —las filas de <c>articulos</c>—, que sigue al lock de
    /// membresía: lee la pertenencia del artículo (una sola lectura, ya bajo ese lock), bloquea las filas
    /// que corresponden y recién entonces lee las entidades, UNA sola vez. La edición no escribe precios y
    /// no toma los locks de par del paso (3).
    ///
    /// <list type="bullet">
    /// <item>Sin familia: la decisión sobre un alcance explícito se toma ya (<c>familia_cambio</c>, sin
    /// bloquear ninguna fila) y la fila se bloquea como siempre, <c>FOR UPDATE</c> sobre la fila del propio
    /// artículo (<see cref="GuardaDeReferencias.BloquearFilaAsync{T}"/>), el lock que serializa dos
    /// ediciones del mismo artículo ANTES de leer. Ver la skill <c>single-read-under-lock</c>, regla 4.</item>
    /// <item>Miembro y "solo este": solo la fila del propio artículo, <c>FOR NO KEY UPDATE</c> guardada por la
    /// familia leída y por la baja lógica.</item>
    /// <item>Miembro con cualquier otro modo: todos los miembros vivos, ascendentes por id y
    /// <c>FOR NO KEY UPDATE</c>, el editado incluido. Se bloquean todos —y no solo el editado— porque el
    /// pedido puede terminar escribiendo a la familia entera, y la decisión depende de valores que solo se
    /// pueden leer DESPUÉS de bloquear: tomar primero la fila propia y recién después las de los demás
    /// rompería el orden ascendente que evita el ciclo entre dos escritores de la misma familia.</item>
    /// </list>
    /// </summary>
    private async Task<ArticulosBloqueados> BloquearYLeerAsync(
        DbConnection conexion, DbTransaction? transaccion, int id, int idTenant, ModoDeAlcanceDeFamilia modo,
        CancellationToken ct)
    {
        var idFamilia = await MembresiaDeFamilias.LeerIdFamiliaAsync(conexion, transaccion, id, idTenant, ct);

        if (idFamilia is not { } familia)
        {
            if (ReglaDeFamilias.ResolverAlcance(modo, esMiembro: false) == ResolucionDeAlcanceDeFamilia.FamiliaCambio)
            {
                throw MembresiaDeFamilias.ErrorDeFamiliaCambio();
            }

            await guarda.BloquearFilaAsync<Articulo>(id, ct);

            // La ÚNICA lectura del artículo, nacida bajo el lock.
            var suelto = await BuscarAsync(id, ct);

            return new ArticulosBloqueados(suelto, [suelto], IdFamilia: null);
        }

        // La marca de la familia, bajo el lock de membresía (compartido o exclusivo): apagarla toma el exclusivo, así que
        // no puede cambiar de true a false hasta el commit. Una lectura escalar, no una entidad.
        var permiteDesglosarCosto = await MembresiaDeFamilias.LeerPermiteDesglosarCostoAsync(
            conexion, transaccion, familia, idTenant, ct);

        List<int> idsBloqueados;

        if (ReglaDeFamilias.ResolverAlcance(modo, esMiembro: true) == ResolucionDeAlcanceDeFamilia.SalirDeLaFamilia)
        {
            await MembresiaDeFamilias.BloquearFilaDelArticuloAsync(conexion, transaccion, id, familia, idTenant, ct);
            idsBloqueados = [id];
        }
        else
        {
            idsBloqueados = await MembresiaDeFamilias.BloquearMiembrosAsync(conexion, transaccion, familia, idTenant, ct);

            // Los escritores de pertenencia toman el lock de membresía exclusivo, así que bajo el lock este
            // artículo es miembro. Si no aparece, alguien cambió su pertenencia (o lo dio de baja) sin
            // respetar el protocolo: se rechaza en vez de escribirle a la familia un pedido que no lo incluye.
            if (!idsBloqueados.Contains(id))
            {
                throw MembresiaDeFamilias.ErrorDeFamiliaCambio();
            }
        }

        // La ÚNICA lectura de las entidades, nacida bajo los locks. Las filas que devuelve son exactamente
        // las que quedaron bloqueadas: la baja lógica no pudo comitear después de ese bloqueo.
        var filas = await db.Articulos.Where(a => idsBloqueados.Contains(a.Id)).OrderBy(a => a.Id).ToListAsync(ct);

        return new ArticulosBloqueados(filas.Single(a => a.Id == id), filas, familia, permiteDesglosarCosto);
    }

    /// <summary>Las validaciones del alta dentro de una familia, bajo el lock de membresía exclusivo que el
    /// llamador ya tomó: la familia —viva, de este tenant, activa— leída y bloqueada <c>FOR SHARE</c>
    /// (<see cref="MembresiaDeFamilias.LeerFamiliaParaIngresarAsync"/>), la referencia —el miembro vivo de menor
    /// id, UNA lectura sin rastreo: no se muta— y la decisión de <see cref="ReglaDeFamilias.ResolverIngreso"/>.
    /// Devuelve el id de la referencia, de la que se copian los precios, y su costo nominal, que el miembro nuevo hereda.
    ///
    /// <para>Los rechazos son <c>404</c> si la familia no existe o está dada de baja (también la de otro
    /// tenant), <c>familia_inactiva</c>, <c>familia_sin_articulos</c> y <c>familia_valores_distintos</c>, que
    /// nombra las columnas que difieren, todos 409.</para></summary>
    private async Task<(int Id, decimal? CostoNominal)> ExigirIngresoALaFamiliaAsync(
        DbConnection conexion, DbTransaction? transaccion, int idFamilia, int idTenant, AltaArticulo datos,
        decimal? costoNominalDelPedido, CancellationToken ct)
    {
        var familia = await MembresiaDeFamilias.LeerFamiliaParaIngresarAsync(conexion, transaccion, idFamilia, idTenant, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe la familia {idFamilia}.");

        var referencia = await db.Articulos
            .AsNoTracking()
            .Where(a => a.IdFamilia == idFamilia)
            .OrderBy(a => a.Id)
            .FirstOrDefaultAsync(ct);

        var valoresDeLaReferencia = referencia is null ? null : ValoresCompartidosDeFamilia.De(referencia);

        // El costo nominal no es un campo del pedido y por sí solo nunca rechaza el alta: se compara con el de la
        // referencia, y si el resto de los campos compartidos coincide, el miembro nuevo hereda ese costo (el que haya
        // dejado una compra).
        //
        // Con la marca de desglose de costo, en cambio, el pedido con una composición (costo de lista cargado) trae su
        // propio costo nominal, y una composición que da otro que el de la familia se rechaza: un pedido sin costo de
        // lista no trae composición y hereda el de la familia, como siempre.
        var nominalDelPedido = familia.PermiteDesglosarCosto && datos.CostoLista is not null
            ? costoNominalDelPedido
            : referencia?.CostoNominal;
        var pedidos = ValoresCompartidosDe(datos, nominalDelPedido);

        switch (ReglaDeFamilias.ResolverIngreso(
            familia.Activa, valoresDeLaReferencia, pedidos, familia.PermiteDesglosarCosto))
        {
            case ResolucionDeIngresoAFamilia.Permitido:
                return (referencia!.Id, referencia.CostoNominal);

            case ResolucionDeIngresoAFamilia.FamiliaInactiva:
                throw ErrorDominio.Conflicto(
                    "familia_inactiva",
                    $"La familia \"{familia.Nombre}\" está inactiva: no se le pueden agregar artículos.");

            case ResolucionDeIngresoAFamilia.FamiliaSinArticulos:
                throw ErrorDominio.Conflicto(
                    "familia_sin_articulos",
                    $"La familia \"{familia.Nombre}\" no tiene artículos vivos: no hay valores de referencia " +
                    "ni precios que copiar.");

            case ResolucionDeIngresoAFamilia.ValoresDistintos:
                throw ErrorDominio.Conflicto(
                    "familia_valores_distintos",
                    $"Los campos compartidos del artículo tienen que ser idénticos a los de la familia \"{familia.Nombre}\": " +
                    $"difieren {string.Join(", ", valoresDeLaReferencia!.CamposDistintos(pedidos, familia.PermiteDesglosarCosto))}.");

            case ResolucionDeIngresoAFamilia.CostoNominalDistinto:
                throw ErrorDominioConDatos.Conflicto(
                    "costo_nominal_distinto_de_familia",
                    $"La composición del costo da un costo nominal de {FormatearNominal(pedidos.CostoNominal)} y el de la familia " +
                    $"\"{familia.Nombre}\" es {FormatearNominal(referencia!.CostoNominal)}: el artículo hereda el de la familia, " +
                    "así que la composición tiene que dar el mismo.",
                    new Dictionary<string, object?>
                    {
                        ["costoNominalDeLaFamilia"] = referencia.CostoNominal,
                        ["costoNominalDelPedido"] = pedidos.CostoNominal
                    });

            default:
                throw new InvalidOperationException("Resolución de ingreso a la familia desconocida.");
        }
    }

    /// <summary>El <c>409 alcance_requerido</c> de la edición de un miembro. En una familia que permite desglosar el
    /// costo, el error lleva además los dos costos nominales (<c>datos.costoNominalAnterior</c> y
    /// <c>datos.costoNominalNuevo</c>, iguales si lo que cambia es otro campo compartido) y el mensaje los nombra: es lo
    /// que el cliente muestra al preguntar el alcance. Sin la marca, el error es el de siempre.</summary>
    private static async Task<ErrorDominio> ErrorDeAlcanceRequeridoDeLaEdicionAsync(
        DbConnection conexion, DbTransaction? transaccion, int idFamilia, int idTenant, bool permiteDesglosarCosto,
        decimal? costoNominalAnterior, decimal? costoNominalNuevo, CancellationToken ct)
    {
        const string queHayQueIndicar = "tiene que indicar si se aplican a toda la familia o solo a este artículo.";

        if (!permiteDesglosarCosto)
        {
            return await MembresiaDeFamilias.ErrorDeAlcanceRequeridoAsync(
                conexion, transaccion, idFamilia, idTenant, $"la edición cambia campos compartidos y {queHayQueIndicar}", ct);
        }

        var cambioElNominal = costoNominalAnterior != costoNominalNuevo;
        var queCambia = cambioElNominal
            ? $"la edición cambia campos compartidos (el costo nominal pasa de {FormatearNominal(costoNominalAnterior)} " +
              $"a {FormatearNominal(costoNominalNuevo)}) y "
            : "la edición cambia campos compartidos y ";

        return await MembresiaDeFamilias.ErrorDeAlcanceRequeridoAsync(
            conexion, transaccion, idFamilia, idTenant, queCambia + queHayQueIndicar, ct,
            new Dictionary<string, object?>
            {
                ["costoNominalAnterior"] = costoNominalAnterior,
                ["costoNominalNuevo"] = costoNominalNuevo
            });
    }

    private static string FormatearNominal(decimal? costoNominal) =>
        costoNominal is { } valor
            ? valor.ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ',')
            : "sin costo";

    /// <summary>Los dieciséis campos compartidos (<see cref="ValoresCompartidosDeFamilia"/>) del pedido de
    /// alta. Un campo compartido nuevo no compila hasta que se lo pasa acá: el record es posicional.</summary>
    private static ValoresCompartidosDeFamilia ValoresCompartidosDe(AltaArticulo datos, decimal? costoNominal) => new(
        datos.IdArea, datos.IdCategoria, datos.IdGrupo, datos.IdProveedorHabitual, datos.IdAlicuotaIva,
        datos.UnidadVenta, datos.UnidadesPorBulto, datos.EsProducto, datos.ControlaLote, AcumulaEnVentaDe(datos),
        datos.CostoLista, datos.CostoListaPor, datos.CostoListaIncluyeIva, datos.DescuentoProveedor,
        datos.ImpuestoInterno, costoNominal);

    /// <summary>El <c>acumula_en_venta</c> con el que nace el artículo: el del pedido, o, si no lo trae, el único válido
    /// para su unidad de venta (un artículo por importe nunca acumula).</summary>
    private static bool AcumulaEnVentaDe(AltaArticulo datos) =>
        datos.AcumulaEnVenta ?? datos.UnidadVenta != UnidadVenta.Importe;

    /// <summary>Los dieciséis campos compartidos (<see cref="ValoresCompartidosDeFamilia"/>) del pedido de
    /// edición. <see cref="EdicionArticulo.AcumulaEnVenta"/> puede venir <c>null</c> (conservar el valor
    /// guardado): <paramref name="acumulaEnVentaGuardado"/> es el del artículo editado, leído bajo los locks, y
    /// es el valor que ese campo toma en el pedido; <paramref name="costoListaPor"/> y
    /// <paramref name="costoListaIncluyeIva"/> ya vienen resueltos de la misma manera, y
    /// <paramref name="costoNominal"/> es el calculado por el servidor. Un campo compartido nuevo no compila hasta que se lo pasa
    /// acá: el record es posicional.</summary>
    private static ValoresCompartidosDeFamilia ValoresCompartidosDe(
        EdicionArticulo datos, bool acumulaEnVentaGuardado, CostoListaPor costoListaPor, bool costoListaIncluyeIva,
        decimal impuestoInterno, decimal? costoNominal) => new(
        datos.IdArea, datos.IdCategoria, datos.IdGrupo, datos.IdProveedorHabitual, datos.IdAlicuotaIva,
        datos.UnidadVenta, datos.UnidadesPorBulto, datos.EsProducto, datos.ControlaLote,
        datos.AcumulaEnVenta ?? acumulaEnVentaGuardado, datos.CostoLista, costoListaPor, costoListaIncluyeIva,
        datos.DescuentoProveedor, impuestoInterno, costoNominal);

    private async Task<DbConnection> ObtenerConexionAbiertaAsync(CancellationToken ct)
    {
        var conexion = db.Database.GetDbConnection();

        if (conexion.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        return conexion;
    }

    /// <summary>Baja lógica: escribe <c>deleted_at</c>, no borra la fila. Los
    /// <see cref="CodigoBarra"/>/<see cref="ArticuloEmpresa"/> asociados quedan como están —
    /// sin cascada, mismo criterio que <see cref="Proveedores.ServicioDeProveedores.EliminarAsync"/>
    /// (sin guard de fila protegida a diferencia de clientes: artículos no tiene un equivalente
    /// al Consumidor Final). La única excepción son los <see cref="CodigoProveedor"/> vivos del
    /// artículo: se dan de baja en la misma transacción para liberar el par (proveedor, código)
    /// del índice único parcial. La imagen de la tienda, en cambio, se borra de verdad en esa misma
    /// transacción (ver <see cref="CatalogoOnline.ServicioDeCatalogoWeb.QuitarImagenAsync"/>).
    ///
    /// La paridad con esa baja de proveedores era declarativa y ahora es real: allá el lock de fila
    /// se toma ANTES de cargar la entidad y acá no se tomaba ninguno, así que dos bajas concurrentes
    /// del mismo artículo leían las dos la fila viva y la segunda re-estampaba un <c>deleted_at</c>
    /// NUEVO sobre el de la primera en vez de rendir 404. Mismo shape sin reintento + lock antes de
    /// la carga que <c>ServicioDeCatalogo{T,TListado,TAlta}.EliminarAsync</c>, con <c>FOR UPDATE</c> sobre
    /// la fila del artículo. Editar y dar de baja el mismo artículo también se serializan entre sí:
    /// <see cref="ActualizarAsync"/> toma sobre esa fila el mismo <c>FOR UPDATE</c> si el artículo no tiene
    /// familia y <c>FOR NO KEY UPDATE</c> si es miembro, y los dos modos chocan con el de esta baja.</summary>
    public async Task EliminarAsync(int id, CancellationToken ct = default)
    {
        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            await guarda.BloquearFilaAsync<Articulo>(id, ct);

            var articulo = await BuscarAsync(id, ct);

            // Los componentes de un combo dado de baja se borran con él: toda fila que exista es de un combo vivo.
            if (await db.CombosComponentes.AnyAsync(c => c.IdArticulo == id, ct))
            {
                throw ErrorDominio.Conflicto(
                    "articulo_en_uso", "No se puede dar de baja el artículo porque tiene combos.");
            }

            var ahora = reloj.Ahora;
            articulo.DeletedAt = ahora;
            articulo.UpdatedAt = ahora;

            var codigosProveedor = await db.CodigosProveedor.Where(c => c.IdArticulo == id).ToListAsync(ct);
            foreach (var codigoProveedor in codigosProveedor)
            {
                codigoProveedor.DeletedAt = ahora;
                codigoProveedor.UpdatedAt = ahora;
            }

            // La imagen de la tienda es un derivado sin historia: se borra la fila, no se deja dada de baja con
            // sus bytes. Va bajo el mismo lock del artículo que toma ServicioDeCatalogoWeb al subir o quitarla.
            db.ImagenesArticulo.RemoveRange(await db.ImagenesArticulo.Where(i => i.IdArticulo == id).ToListAsync(ct));

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
    /// 8: "called but never auto-applied"). El costo base es el <c>costo_nominal</c> guardado o, si no
    /// lo tiene, el que sale de normalizar el costo de lista (ver <see cref="CalcularCostoBaseAsync"/>).
    /// Con <paramref name="idEmpresa"/> el múltiplo de redondeo es el de esa empresa
    /// (<c>redondeo_sugerencia_precio</c>); sin ella, el default del parámetro. Una empresa que no
    /// existe o no es visible para el tenant es un 404, igual que en la tesorería y los reportes.</summary>
    public async Task<SugerenciaDePrecio> SugerirPrecioAsync(
        int idArticulo, int? idEmpresa = null, CancellationToken ct = default)
    {
        var articulo = await BuscarAsync(idArticulo, ct);

        var multiploDeRedondeo = decimal.Parse(ParametroConocido.RedondeoSugerenciaPrecio.ValorPorDefecto, CultureInfo.InvariantCulture);
        if (idEmpresa is { } empresa)
        {
            if (!await db.Empresas.AnyAsync(e => e.Id == empresa, ct))
            {
                throw ErrorDominio.NoEncontrado($"No existe la empresa {empresa}.");
            }

            multiploDeRedondeo = await servicioDeParametros.ResolverRedondeoDeSugerenciaAsync(empresa, ct);
        }

        var margenGrupo = articulo.IdGrupo is { } idGrupo
            ? await db.Grupos.Where(g => g.Id == idGrupo).Select(g => g.Margen).FirstOrDefaultAsync(ct)
            : null;

        var margenProveedor = articulo.IdProveedorHabitual is { } idProveedor
            ? await db.Proveedores.Where(p => p.Id == idProveedor).Select(p => p.Margen).FirstOrDefaultAsync(ct)
            : null;

        var costoBase = await CalcularCostoBaseAsync(articulo, ct);
        var precioSugerido = SugeridorDePrecio.Sugerir(costoBase, margenGrupo, margenProveedor, multiploDeRedondeo);

        return new SugerenciaDePrecio(precioSugerido);
    }

    /// <summary><c>costo_nominal</c> guardado o, si el artículo no lo tiene, el costo normalizado del costo de
    /// lista (por unidad, con IVA y neto del descuento): el costo de lista puede estar por bulto o sin IVA y no
    /// sirve crudo. <c>null</c> si tampoco hay costo de lista, si es por bulto sin unidades por bulto o si la
    /// alícuota del artículo ya no existe.</summary>
    private async Task<decimal?> CalcularCostoBaseAsync(Articulo articulo, CancellationToken ct)
    {
        if (articulo.CostoNominal is { } nominal)
        {
            return nominal;
        }

        if (articulo.CostoLista is null
            || CalculadorDeCostoNominal.BultoSinUnidades(articulo.CostoListaPor, articulo.UnidadesPorBulto))
        {
            return null;
        }

        if (await LeerPorcentajeDeAlicuotaIvaAsync(articulo.IdAlicuotaIva, ct) is not { } porcentajeIva)
        {
            return null;
        }

        return CalculadorDeCostoNominal.Calcular(
            articulo.CostoLista, articulo.CostoListaPor, articulo.CostoListaIncluyeIva, articulo.UnidadesPorBulto,
            porcentajeIva, articulo.DescuentoProveedor, articulo.ImpuestoInterno);
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

    /// <summary>El JSON del servidor acepta el ordinal además del nombre: un ordinal fuera del enum se rechaza acá
    /// y no llega al CHECK <c>ck_articulos_costo_lista_por</c>.</summary>
    private static void ExigirCostoListaPorDefinido(CostoListaPor costoListaPor)
    {
        if (!Enum.IsDefined(costoListaPor))
        {
            throw new ErrorDominio(
                "costo_lista_por_invalido", "El campo costo_lista_por debe ser Unidad o Bulto.", 400);
        }
    }

    /// <summary>El JSON del servidor acepta el ordinal además del nombre: un ordinal fuera del enum se rechaza acá
    /// y no llega al CHECK <c>ck_articulos_tecla_atajo</c>.</summary>
    private static void ExigirTeclaAtajoDefinida(TeclaAtajo? teclaAtajo)
    {
        if (teclaAtajo is { } tecla && !Enum.IsDefined(tecla))
        {
            throw new ErrorDominio(
                "tecla_atajo_invalida", "El campo tecla_atajo debe ser F5, F6, F7 o F8.", 400);
        }
    }

    /// <summary>db-error-backstops: pre-chequeo best-effort — el backstop real es <c>ux_articulos_tecla_atajo</c>
    /// (23505 → 409 <c>tecla_atajo_duplicada</c>, <c>ManejadorDeErrores</c>). Ve solo los artículos vivos del tenant,
    /// igual que el índice parcial; <paramref name="excluirId"/> es el artículo que se edita.</summary>
    private async Task ExigirTeclaAtajoDisponibleAsync(TeclaAtajo teclaAtajo, int? excluirId, CancellationToken ct)
    {
        var tomada = await db.Articulos.AnyAsync(a => a.TeclaAtajo == teclaAtajo && a.Id != excluirId, ct);

        if (tomada)
        {
            throw ErrorDominio.Conflicto(
                "tecla_atajo_duplicada", $"Ya hay un artículo con la tecla de atajo {teclaAtajo} en este tenant.");
        }
    }

    /// <summary>Un costo de lista por bulto no se puede llevar a costo por unidad sin las unidades del bulto.</summary>
    private static void ExigirUnidadesParaCostoPorBulto(CostoListaPor costoListaPor, decimal? unidadesPorBulto)
    {
        if (CalculadorDeCostoNominal.BultoSinUnidades(costoListaPor, unidadesPorBulto))
        {
            throw new ErrorDominio(
                "unidades_por_bulto_requerido",
                "Un costo de lista por bulto exige unidades_por_bulto mayor que cero.",
                400);
        }
    }

    /// <summary>El costo nominal calculado también es una columna <c>numeric(14,2)</c>: con IVA puede superar el
    /// tope que sí cumple el costo de lista, y ese caso es un 400 y no un desborde de la base.</summary>
    private static decimal? CalcularCostoNominal(
        decimal? costoLista, CostoListaPor costoListaPor, bool costoListaIncluyeIva, decimal? unidadesPorBulto,
        decimal porcentajeIva, decimal? descuentoProveedor, decimal impuestoInterno)
    {
        var costoNominal = CalculadorDeCostoNominal.Calcular(
            costoLista, costoListaPor, costoListaIncluyeIva, unidadesPorBulto, porcentajeIva, descuentoProveedor,
            impuestoInterno);
        ExigirCostoValido(costoNominal, "costo_nominal");

        return costoNominal;
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

    /// <summary>Columna <c>numeric(14,2)</c>: importe de 0 a 999999999999,99 con hasta dos decimales.</summary>
    private static void ExigirImpuestoInternoValido(decimal? valor)
    {
        if (valor is not { } v)
        {
            return;
        }

        if (v < 0m || v >= 1_000_000_000_000m || decimal.Round(v, 2) != v)
        {
            throw new ErrorDominio(
                "impuesto_interno_articulo_invalido",
                "El campo impuesto_interno debe estar entre 0 y 999999999999.99, con hasta dos decimales.",
                400);
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
    /// <c>ServicioDeClientes.ExigirCondicionFiscalValidaAsync</c>. Devuelve el porcentaje, que el cálculo del costo
    /// nominal necesita.</summary>
    private async Task<decimal> ExigirAlicuotaIvaValidaAsync(int id, CancellationToken ct) =>
        await LeerPorcentajeDeAlicuotaIvaAsync(id, ct)
            ?? throw new ErrorDominio("referencia_invalida", $"No existe la alícuota de IVA {id}.", 400);

    private async Task<decimal?> LeerPorcentajeDeAlicuotaIvaAsync(int id, CancellationToken ct) =>
        await db.AlicuotasIva
            .Where(a => a.Id == id)
            .Select(a => (decimal?)a.Porcentaje)
            .SingleOrDefaultAsync(ct);

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

    /// <summary>El nombre de la familia a crear con el alta, o <c>null</c> si el pedido no trae
    /// <see cref="AltaArticulo.NombreFamiliaNueva"/>. Con <see cref="AltaArticulo.IdFamilia"/> a la vez es
    /// <c>400 familia_existente_y_nueva</c>; si no, se normaliza como cualquier nombre de familia
    /// (<see cref="NombreDeFamilia.Normalizar"/>): un nombre presente pero vacío se rechaza, no se descarta.</summary>
    private static string? NormalizarNombreDeFamiliaNueva(AltaArticulo datos)
    {
        if (datos.NombreFamiliaNueva is null)
        {
            return null;
        }

        if (datos.IdFamilia is not null)
        {
            throw new ErrorDominio(
                "familia_existente_y_nueva",
                "Los campos idFamilia y nombreFamiliaNueva son excluyentes: el artículo entra a una familia existente o crea una nueva.",
                400);
        }

        return NombreDeFamilia.Normalizar(datos.NombreFamiliaNueva);
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
        a.CostoLista, a.CostoListaPor, a.CostoListaIncluyeIva, a.DescuentoProveedor, a.CostoNominal,
        a.DisponibleParaTodas, idsEmpresas, a.Activo,
        a.ControlaLote, a.AcumulaEnVenta, a.IdFamilia, CodigoProveedor: null, a.DisponibleOnline, a.DisponibleVenta,
        TeclaAtajo: a.TeclaAtajo, ImpuestoInterno: a.ImpuestoInterno);
}
