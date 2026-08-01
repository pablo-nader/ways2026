namespace Ways.IntegrationTests;

/// <summary>
/// <see cref="WaysApiFixture"/> setea <c>ConnectionStrings__Ways</c> como variable de
/// entorno del PROCESO (ver <see cref="WaysApiFixture.InitializeAsync"/>): es la única forma
/// encontrada de que <c>Program.cs</c> (hosting mínimo, lee configuración de forma síncrona
/// antes de <c>Build()</c>) vea la cadena de conexión del contenedor de cada instancia.
///
/// Por default xUnit corre clases de test SIN <c>[Collection]</c> explícito en colecciones
/// distintas, en paralelo entre sí. Dos <see cref="WaysApiFixture"/> de clases distintas
/// pisándose esa variable de entorno en paralelo produce una carrera real: una clase termina
/// arrancando su host contra el contenedor (ya destruido) de la otra. Todas las clases que
/// arrancan un <see cref="WaysApiFixture"/> van en esta colección para forzar que corran
/// secuencialmente — cada instancia de fixture sigue siendo la suya (<c>IClassFixture</c>,
/// no <c>ICollectionFixture</c>), solo se serializa la ejecución entre clases.
/// </summary>
[CollectionDefinition("Ways.IntegrationTests secuencial", DisableParallelization = true)]
public class ColeccionSecuencial;
