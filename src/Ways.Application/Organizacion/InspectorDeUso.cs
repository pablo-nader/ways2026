using System.Data;
using System.Data.Common;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;

namespace Ways.Application.Organizacion;

/// <summary>
/// Contesta una sola pregunta: ¿el cliente ya operó sobre esta entidad de organización?
/// Devuelve la ETIQUETA de la primera RAMA que bloquea —la tabla hoja, y el puente detrás de
/// <c>" via "</c> cuando la rama es puenteada (<see cref="RamaDeUso.Etiqueta"/>)—, o <c>null</c>
/// si la entidad está prístina (stage-20 design D5, D6). Devolver la rama y no la hoja pelada es
/// judgment-day ronda 2, hallazgo R2-6: una hoja a la que se llega por dos caminos dejaba al
/// llamador redactando el bloqueo a ciegas.
///
/// UNA sola ida y vuelta: un <c>UNION ALL</c> de ramas <c>SELECT '&lt;tabla&gt;' WHERE
/// EXISTS (...)</c> con un <c>LIMIT 1</c> afuera, así el nodo <c>Append</c> de Postgres corta
/// en la primera rama que devuelve fila. La alternativa (unas 40 <c>AnyAsync</c> secuenciales)
/// se rechazó por round trips; el costo de planificar ~40 ramas se paga una vez por intento de
/// baja, sobre una acción de plataforma que se hace un puñado de veces al año.
///
/// Una rama con PUENTE (<see cref="PuenteDeUso"/>) agrega un <c>JOIN</c> contra la tabla
/// estructural intermedia y pone los parámetros del ancla sobre ella: es cómo el uso sube por la
/// jerarquía sin abrir N consultas, una por hijo. Sigue siendo el mismo statement único.
///
/// ADO crudo sobre la conexión/transacción del llamador, nunca <c>Database.SqlQuery&lt;T&gt;</c>
/// ni <c>FromSqlRaw</c>: confirmado en stage-1 slice 2 que esos dos revientan con
/// <c>IndexOutOfRangeException</c> contra este modelo. La conexión se abre con
/// <c>Database.OpenConnectionAsync</c> y nunca con <c>conexion.OpenAsync()</c> directo, porque
/// ese segundo camino no dispara <c>InterceptorDeContextoDeTenant</c> y la conexión quedaría sin
/// los GUC que RLS necesita — mismo idioma que
/// <see cref="Ways.Application.Clientes.AsignadorDeNumeroCliente"/>.
///
/// SUPERFICIE DE INYECCIÓN CERRADA: los identificadores salen únicamente de la metadata de EF
/// (<c>IEntityType</c>/<c>IProperty</c>), se validan contra <c>\A[a-z_][a-z0-9_]*\z</c> y se emiten
/// calificados por esquema y entre comillas dobles; cada valor de clave del ancla y el instante
/// del ancla viajan como parámetro ligado. Ninguna cadena de origen externo llega al statement,
/// y el statement es de solo lectura (<c>SELECT</c>/<c>EXISTS</c>).
///
/// OD4 — UNA FILA DADA DE BAJA LÓGICAMENTE IGUAL BLOQUEA, y es una perilla reversible de una
/// línea. Ninguna rama emite <c>AND d."deleted_at" IS NULL</c>: SQL crudo no aplica el query
/// filter <c>"BajaLogica"</c> de EF, así que la semántica sale sola. El cliente operó ahí;
/// borrar la fila después no rebobina esa historia, y es además la dirección que falla del lado
/// seguro. REVERTIRLO cuesta: agregar ese conjunto por rama, dar vuelta el test de la tarea
/// 4.11 y regenerar el golden N3. Registrado, no implementado.
///
/// MODO REFERENCIA (fix/bajas-catalogos-guarda-de-uso) — <see cref="TablasQueReferencianAsync"/>
/// reutiliza el mismo inventario y el mismo renderizador para una pregunta distinta: ¿existe AL
/// MENOS UNA fila, sin importar cuándo se creó? Las bajas de catálogos de tenant y proveedores
/// necesitan esa pregunta porque sus FKs son MUTABLES —un artículo creado en enero se puede
/// reasignar a una marca creada en febrero—, así que el corte <c>created_at &gt; ancla</c> del
/// modo de organización dejaría pasar esa reasignación sin bloquear. <see cref="RenderizarReferencias"/>
/// es el mismo <c>UNION ALL</c> sin ese conjunto y sin <c>LIMIT 1</c>: devuelve TODAS las ramas
/// que dispararon, no la primera, así que el guard puede nombrar cada tabla en uso.
///
/// EFECTO LATERAL B — RLS sigue aplicando porque vive en la conexión, así que NINGUNA rama
/// agrega un conjunto de tenant POR ENCIMA de lo que ya declara su FK. La redacción anterior
/// ("no se agrega ningún conjunto <c>id_tenant</c>") se leía como que el statement no menciona
/// <c>id_tenant</c>, y eso es falso a simple vista: la mayoría de las FKs de este modelo son
/// compuestas <c>(id_x, id_tenant)</c>, así que el SQL emitido muestra conjuntos de
/// <c>id_tenant</c> por todos lados — salen de la metadata de la FK, no de una defensa que el
/// inspector agregue por su cuenta. Lo que NO se agrega es un conjunto EXTRA, y el motivo es
/// direccional: un conjunto de más solo puede ANGOSTAR el resultado, y un bug que angosta
/// sub-bloquea — la única dirección que esta etapa no acepta. La salida entera del inspector es
/// una etiqueta de rama (o <c>null</c>): nombres de tabla del propio esquema, sin filas, sin ids
/// y sin conteos, así que tampoco hay canal por donde se filtre dato de otro tenant.
///
/// <c>db-error-backstops</c>: ESTRUCTURALMENTE N/A. Toda FK del modelo es
/// <c>DeleteBehavior.Restrict</c>, pero acá no hay borrado físico en ningún camino: la baja es
/// un <c>UPDATE ... SET deleted_at</c>, contra el que <c>Restrict</c> no aporta absolutamente
/// nada. Ninguna constraint de Postgres puede dispararse detrás de este guard, no hay SQLSTATE
/// que clasificar y <c>ManejadorDeErrores.cs</c> queda intacto. La consecuencia hay que decirla
/// en voz alta: ESTE GUARD DE APLICACIÓN ES LA ÚNICA LÍNEA DE DEFENSA, sin red de base atrás.
/// Por eso se entregó INERTE en la slice 3 — sin ningún llamador en <c>src/</c> — para que
/// pudiera revisarse por sus propios méritos antes de que algo pudiera invocarlo. Desde la slice
/// 4 tiene exactamente cuatro llamadores: las tres bajas de
/// <c>ServicioDeOrganizacion</c> y la de <c>ServicioDeUsuarios</c>.
/// </summary>
public sealed class InspectorDeUso(IWaysDbContext db)
{
    /// <summary>
    /// <c>\A</c>/<c>\z</c> y NUNCA <c>^</c>/<c>$</c>: en .NET <c>$</c> matchea también ANTES de un
    /// <c>\n</c> final, así que <c>^...$</c> acepta <c>"stock\n"</c> y todo lo que venga después
    /// del salto de línea entra al statement. <c>\z</c> es el fin de cadena absoluto, y es lo que
    /// cierra esa superficie.
    /// </summary>
    private const string PatronDeIdentificador = @"\A[a-z_][a-z0-9_]*\z";

    private static readonly Regex IdentificadorValido =
        new(PatronDeIdentificador, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Alias de la tabla puente en las ramas que llegan al ancla por la jerarquía
    /// estructural. Es una constante del renderizador, nunca un identificador de la metadata.</summary>
    private const string AliasDelPuente = "pv";

    /// <summary>
    /// <paramref name="valoresDeClave"/> es posicional contra
    /// <see cref="InventarioDeDependientes.PropiedadesDeAncla"/>: <c>valoresDeClave[i]</c> es el
    /// valor de la propiedad <c>i</c> de esa lista, y termina en el parámetro <c>$(i+1)</c>.
    /// <paramref name="ancla"/> es el <c>CreatedAt</c> de la entidad ancla y va al último
    /// parámetro, solo si alguna rama lo usa.
    ///
    /// Lo que devuelve es la <see cref="RamaDeUso.Etiqueta"/> de la rama que disparó, no el nombre
    /// pelado de la tabla: <c>EtiquetasDeTablas.DescribirBloqueo</c> la parsea para nombrar el
    /// puente solo cuando el hit VINO por el puente.
    /// </summary>
    public async Task<string?> PrimeraDependenciaEnUsoAsync(
        Type tipoAncla,
        IReadOnlyList<object> valoresDeClave,
        DateTimeOffset ancla,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tipoAncla);
        ArgumentNullException.ThrowIfNull(valoresDeClave);

        var ramas = InventarioDeDependientes.Construir(db.Model, tipoAncla);
        var propiedades = InventarioDeDependientes.PropiedadesDeAncla(db.Model, tipoAncla);

        ValidarValoresDeClave(tipoAncla, ramas, propiedades, valoresDeClave);

        var conexion = await ObtenerConexionAbiertaAsync(ct);

        await using var comando = conexion.CreateCommand();
        comando.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        comando.CommandText = Renderizar(ramas, propiedades);

        foreach (var valor in valoresDeClave)
        {
            ParametrosDeComando.Agregar(comando, valor);
        }

        if (ramas.Any(rama => rama.UsaAncla))
        {
            ParametrosDeComando.Agregar(comando, ancla);
        }

        return await comando.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>
    /// MODO REFERENCIA (ver el doc-comment de la clase): existencia pura, sin el conjunto de
    /// <c>created_at</c> y sin <c>LIMIT 1</c> — devuelve TODAS las ramas que dispararon, no la
    /// primera, deduplicadas y en el orden del inventario (<see cref="RenderizarReferencias"/> no
    /// ordena el statement: el orden lo impone este método en C#, leyendo el resultado completo y
    /// filtrando la lista YA ORDENADA de etiquetas ejecutables por las que aparecieron en el
    /// resultset — así el SQL emitido no depende de que Postgres preserve el orden de un
    /// <c>UNION ALL</c> sin <c>ORDER BY</c>, que no es una garantía del motor).
    /// </summary>
    public async Task<IReadOnlyList<string>> TablasQueReferencianAsync(
        Type tipoAncla,
        IReadOnlyList<object> valoresDeClave,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tipoAncla);
        ArgumentNullException.ThrowIfNull(valoresDeClave);

        var ramas = InventarioDeDependientes.Construir(db.Model, tipoAncla);
        var propiedades = InventarioDeDependientes.PropiedadesDeAncla(db.Model, tipoAncla);

        ValidarValoresDeClave(tipoAncla, ramas, propiedades, valoresDeClave);

        var conexion = await ObtenerConexionAbiertaAsync(ct);

        await using var comando = conexion.CreateCommand();
        comando.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        comando.CommandText = RenderizarReferencias(ramas, propiedades);

        foreach (var valor in valoresDeClave)
        {
            ParametrosDeComando.Agregar(comando, valor);
        }

        var etiquetasEnOrden = ramas
            .Where(rama => rama.Clasificacion is not ClasificacionDeDependiente.Excluido)
            .Select(rama => rama.Etiqueta)
            .ToList();

        var encontradas = new HashSet<string>(StringComparer.Ordinal);

        await using var lector = await comando.ExecuteReaderAsync(ct);

        while (await lector.ReadAsync(ct))
        {
            encontradas.Add(lector.GetString(0));
        }

        return [.. etiquetasEnOrden.Where(encontradas.Contains)];
    }

    /// <summary>
    /// La validación compartida por los dos métodos de EJECUCIÓN (<see cref="PrimeraDependenciaEnUsoAsync"/>
    /// y <see cref="TablasQueReferencianAsync"/>): falla cerrado si el ancla no tiene ninguna rama
    /// ejecutable, si <paramref name="valoresDeClave"/> no trae la cuenta exacta que
    /// <paramref name="propiedades"/> exige, o si algún valor posicional es <c>null</c> (llegaría
    /// sin normalizar a <see cref="ParametrosDeComando.Agregar"/> y reventaría con un error opaco
    /// de Npgsql).
    /// </summary>
    private static void ValidarValoresDeClave(
        Type tipoAncla,
        IReadOnlyList<RamaDeUso> ramas,
        IReadOnlyList<string> propiedades,
        IReadOnlyList<object> valoresDeClave)
    {
        // FALLA CERRADO, igual que Renderizar: un conjunto ejecutable vacío significa que el
        // inventario no sabe nada de esta ancla, no que la entidad esté prístina. Devolver null
        // (o una lista vacía) acá sería afirmar lo segundo sin haber preguntado nada — la
        // dirección que esta etapa no acepta.
        if (ramas.Count == 0)
        {
            throw new InvalidOperationException(
                $"El ancla {tipoAncla.Name} no tiene ninguna rama ejecutable, así que el " +
                "inspector no puede afirmar que la entidad esté sin uso.");
        }

        if (valoresDeClave.Count != propiedades.Count)
        {
            throw new ArgumentException(
                $"El ancla {tipoAncla.Name} necesita {propiedades.Count} valor(es) de clave " +
                $"({string.Join(", ", propiedades)}) y se recibieron {valoresDeClave.Count}.",
                nameof(valoresDeClave));
        }

        for (var i = 0; i < valoresDeClave.Count; i++)
        {
            if (valoresDeClave[i] is null)
            {
                throw new ArgumentException(
                    $"El valor de clave en la posición {i} ({propiedades[i]}) del ancla " +
                    $"{tipoAncla.Name} es null: el inspector no liga parámetros nulos.",
                    nameof(valoresDeClave));
            }
        }
    }

    /// <summary>
    /// Renderiza el statement. Público y estático porque es la superficie que testea la unidad
    /// de rendering sin base — el repo no usa <c>InternalsVisibleTo</c> en ningún lado (mismo
    /// criterio que <c>DesactivadorDeCertificadoFiscal</c>).
    /// </summary>
    public static string Renderizar(IReadOnlyList<RamaDeUso> ramas, IReadOnlyList<string> propiedadesDeAncla) =>
        RenderizarUnion(ramas, propiedadesDeAncla, incluirConjuntoDeAncla: true, incluirLimite: true);

    /// <summary>MODO REFERENCIA (ver el doc-comment de la clase): mismo <c>UNION ALL</c>, sin el
    /// conjunto <c>created_at &gt; $n</c> de ninguna rama —ni siquiera las <c>Marcado</c>— y sin
    /// <c>LIMIT 1</c> externo, así que <see cref="TablasQueReferencianAsync"/> puede leer TODAS las
    /// ramas que dispararon.</summary>
    public static string RenderizarReferencias(
        IReadOnlyList<RamaDeUso> ramas, IReadOnlyList<string> propiedadesDeAncla) =>
        RenderizarUnion(ramas, propiedadesDeAncla, incluirConjuntoDeAncla: false, incluirLimite: false);

    private static string RenderizarUnion(
        IReadOnlyList<RamaDeUso> ramas,
        IReadOnlyList<string> propiedadesDeAncla,
        bool incluirConjuntoDeAncla,
        bool incluirLimite)
    {
        ArgumentNullException.ThrowIfNull(ramas);
        ArgumentNullException.ThrowIfNull(propiedadesDeAncla);

        var ejecutables = ramas
            .Where(rama => rama.Clasificacion is not ClasificacionDeDependiente.Excluido)
            .ToList();

        if (ejecutables.Count == 0)
        {
            throw new InvalidOperationException(
                "No hay ninguna rama ejecutable que renderizar para el ancla pedida.");
        }

        var indiceDelAncla = propiedadesDeAncla.Count + 1;

        var sql = new StringBuilder("SELECT tabla FROM (");
        var primera = true;

        foreach (var rama in ejecutables)
        {
            if (!primera)
            {
                sql.Append(" UNION ALL ");
            }

            primera = false;
            sql.Append(RenderizarRama(rama, propiedadesDeAncla, indiceDelAncla, incluirConjuntoDeAncla));
        }

        sql.Append(") AS ramas");

        if (incluirLimite)
        {
            sql.Append(" LIMIT 1");
        }

        return sql.ToString();
    }

    private static string RenderizarRama(
        RamaDeUso rama, IReadOnlyList<string> propiedadesDeAncla, int indiceDelAncla, bool incluirConjuntoDeAncla)
    {
        var esquema = Identificador(rama.Esquema);
        var tabla = Identificador(rama.Tabla);

        var origen = $"\"{esquema}\".\"{tabla}\" d";
        List<string> conjuntos;

        if (rama.Puente is { } puente)
        {
            // El uso sube por la jerarquía: la hoja se une al puente por las mismas columnas con
            // las que ya lo referenciaba, y los parámetros del ancla van sobre el PUENTE. Una sola
            // consulta cubre todos los puntos de venta de la empresa; el conjunto de tenant sobre
            // el puente es lo que impide que el id de otro tenant bloquee.
            var union = rama.Columnas
                .Zip(puente.ColumnasDeUnion, (columna, columnaDelPuente) =>
                    $"d.\"{Identificador(columna)}\" = " +
                    $"{AliasDelPuente}.\"{Identificador(columnaDelPuente)}\"");

            origen += $" JOIN \"{Identificador(puente.Esquema)}\".\"{Identificador(puente.Tabla)}\" " +
                $"{AliasDelPuente} ON {string.Join(" AND ", union)}";

            conjuntos = puente.ColumnasHaciaElAncla
                .Zip(rama.PropiedadesDelPrincipal, (columna, propiedad) =>
                    $"{AliasDelPuente}.\"{Identificador(columna)}\" = " +
                    $"${IndiceDeParametro(propiedad, propiedadesDeAncla)}")
                .ToList();
        }
        else
        {
            conjuntos = rama.Columnas
                .Zip(rama.PropiedadesDelPrincipal, (columna, propiedad) =>
                    $"d.\"{Identificador(columna)}\" = ${IndiceDeParametro(propiedad, propiedadesDeAncla)}")
                .ToList();
        }

        if (incluirConjuntoDeAncla && rama.UsaAncla)
        {
            // ">" ESTRICTO: lo que creó el aprovisionamiento comparte el instante del ancla
            // (ServicioDeAprovisionamiento lee el reloj una sola vez) y no debe bloquear. Va
            // siempre sobre la HOJA, incluso con puente: es la fila que el cliente cargó.
            // MODO REFERENCIA: incluirConjuntoDeAncla es SIEMPRE false — una FK mutable no tiene
            // ningún instante contra el que cortar (ver el doc-comment de la clase).
            conjuntos.Add(
                $"d.\"{InventarioDeDependientes.ColumnaDeMarcaTemporal}\" > ${indiceDelAncla}");
        }

        // La proyección es la ETIQUETA DE LA RAMA, no la tabla hoja pelada (judgment-day ronda 2,
        // hallazgo R2-6): con la hoja sola, una tabla que llega al ancla por DOS caminos —hoy
        // `parametros`, directa desde la empresa y puenteada desde sus puntos de venta— dejaba al
        // llamador sin saber cuál de los dos disparó, y la copia del 409 afirmaba "en sus puntos de
        // venta" incluso sobre una fila de nivel empresa. Se compone de los identificadores YA
        // VALIDADOS, nunca de `rama.Etiqueta` en crudo: la superficie de inyección sigue cerrada.
        var etiqueta = rama.Puente is null
            ? tabla
            : $"{tabla}{RamaDeUso.SeparadorDePuente}{Identificador(rama.Puente.Tabla)}";

        return $"SELECT '{etiqueta}' AS tabla WHERE EXISTS (SELECT 1 FROM {origen} " +
            $"WHERE {string.Join(" AND ", conjuntos)})";
    }

    private static int IndiceDeParametro(string propiedad, IReadOnlyList<string> propiedadesDeAncla)
    {
        var indice = propiedadesDeAncla.ToList().IndexOf(propiedad);

        return indice >= 0
            ? indice + 1
            : throw new InvalidOperationException(
                $"La propiedad principal {propiedad} no está en la lista de propiedades del " +
                $"ancla ({string.Join(", ", propiedadesDeAncla)}).");
    }

    /// <summary><c>internal</c> y no <c>private</c> desde fix/bajas-catalogos-guarda-de-uso:
    /// <c>GuardaDeReferencias.BloquearFilaAsync</c> valida con el MISMO patrón los identificadores
    /// (esquema/tabla/columna) que arma a mano para su <c>SELECT ... FOR UPDATE</c> — una segunda
    /// regex sería una segunda superficie que mantener sincronizada con esta.</summary>
    internal static string Identificador(string valor) =>
        IdentificadorValido.IsMatch(valor)
            ? valor
            : throw new InvalidOperationException(
                $"El identificador '{valor}' no cumple {PatronDeIdentificador}: el inspector de " +
                "uso solo emite identificadores que salen de la metadata de EF.");

    private async Task<DbConnection> ObtenerConexionAbiertaAsync(CancellationToken ct)
    {
        var conexion = db.Database.GetDbConnection();

        if (conexion.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        return conexion;
    }
}
