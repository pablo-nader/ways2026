"""Reparte la suite de integración en fragmentos y verifica que se hayan ejecutado completos."""

import argparse
import json
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path

MAXIMO_NOMBRES_IMPRESOS = 20
ESPACIO_TRX = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
CARACTERES_ESPECIALES_DEL_FILTRO = r"\()&|=!~"


class ErrorDeFragmentacion(Exception):
    pass


@dataclass(frozen=True)
class Plan:
    filtro: str
    clases_fragmento: int
    clases_totales: int
    metodos_fragmento: int
    metodos_totales: int


def clase_de(nombre_completo: str) -> str:
    return nombre_completo.rsplit(".", 1)[0]


def leer_lineas(texto: str) -> list[str]:
    return [linea.strip() for linea in texto.splitlines() if linea.strip()]


def clases_de(lista: list[str]) -> list[str]:
    return sorted({clase_de(nombre) for nombre in lista})


def pesos_por_clase(clases: list[str], lista: list[str], pesos: dict[str, float]) -> dict[str, float]:
    medidos = [pesos[c] for c in clases if c in pesos]
    metodos_medidos = sum(1 for n in lista if clase_de(n) in pesos)
    por_metodo = sum(medidos) / metodos_medidos if metodos_medidos else 1.0
    metodos = {c: 0 for c in clases}
    for nombre in lista:
        metodos[clase_de(nombre)] += 1
    return {c: pesos[c] if c in pesos else max(metodos[c], 1) * por_metodo for c in clases}


def clases_del_fragmento(
    clases: list[str],
    fragmento: int,
    total: int,
    pesos: dict[str, float] | None = None,
) -> list[str]:
    if pesos is None:
        return [clase for indice, clase in enumerate(clases) if indice % total == fragmento - 1]
    cargas = [0.0] * total
    asignadas: list[list[str]] = [[] for _ in range(total)]
    for clase in sorted(clases, key=lambda c: (-pesos[c], c)):
        destino = min(range(total), key=lambda i: (cargas[i], i))
        cargas[destino] += pesos[clase]
        asignadas[destino].append(clase)
    return sorted(asignadas[fragmento - 1])


def escapar_filtro(texto: str) -> str:
    return "".join(
        "\\" + caracter if caracter in CARACTERES_ESPECIALES_DEL_FILTRO else caracter
        for caracter in texto
    )


def construir_filtro(clases: list[str]) -> str:
    return "|".join(f"FullyQualifiedName~{escapar_filtro(clase)}." for clase in clases)


def planificar(
    lista: list[str], fragmento: int, total: int, pesos: dict[str, float] | None = None
) -> Plan:
    lista = [nombre.strip() for nombre in lista if nombre.strip()]
    if not lista:
        raise ErrorDeFragmentacion("La lista de tests está vacía: no hay nada que repartir.")
    if total < 1:
        raise ErrorDeFragmentacion(f"El total de fragmentos debe ser al menos 1 (recibido {total}).")
    if not 1 <= fragmento <= total:
        raise ErrorDeFragmentacion(
            f"El fragmento {fragmento} está fuera del rango 1..{total}."
        )
    clases = clases_de(lista)
    ponderados = pesos_por_clase(clases, lista, pesos) if pesos else None
    propias = clases_del_fragmento(clases, fragmento, total, ponderados)
    if not propias:
        raise ErrorDeFragmentacion(
            f"El fragmento {fragmento} de {total} queda sin clases "
            f"({len(clases)} clases en total): un filtro vacío correría toda la suite."
        )
    conjunto = set(propias)
    metodos_fragmento = sum(1 for nombre in lista if clase_de(nombre) in conjunto)
    return Plan(
        filtro=construir_filtro(propias),
        clases_fragmento=len(propias),
        clases_totales=len(clases),
        metodos_fragmento=metodos_fragmento,
        metodos_totales=len(lista),
    )


def ejecutados_en(carpeta: Path) -> set[str]:
    nombres = set()
    for trx in sorted(carpeta.rglob("*.trx")):
        for metodo in ET.parse(trx).getroot().iter(f"{ESPACIO_TRX}TestMethod"):
            nombres.add(f"{metodo.get('className')}.{metodo.get('name')}")
    return nombres


def describir(titulo: str, nombres) -> str:
    ordenados = sorted(nombres)
    visibles = ordenados[:MAXIMO_NOMBRES_IMPRESOS]
    resto = len(ordenados) - len(visibles)
    texto = f"{titulo} ({len(ordenados)}): " + ", ".join(visibles)
    return texto + f" y {resto} más" if resto else texto


def verificar(carpetas: list[Path]) -> tuple[list[str], str]:
    errores: list[str] = []
    listas: list[tuple[Path, set[str]]] = []
    ejecutados: list[set[str]] = []

    for carpeta in carpetas:
        archivo_lista = carpeta / "lista.txt"
        if archivo_lista.is_file():
            listas.append((carpeta, set(leer_lineas(archivo_lista.read_text(encoding="utf-8")))))
        else:
            errores.append(f"{carpeta}: falta lista.txt.")
        if any(carpeta.rglob("*.trx")):
            ejecutados.append(ejecutados_en(carpeta))
        else:
            errores.append(f"{carpeta}: no hay ningún archivo trx.")

    referencia = None
    for carpeta, lista in listas:
        if referencia is None:
            referencia = (carpeta, lista)
        elif lista != referencia[1]:
            errores.append(
                f"{carpeta}: el lista.txt difiere del de {referencia[0]} "
                f"({len(lista ^ referencia[1])} líneas distintas)."
            )

    repetidos: set[str] = set()
    vistos: set[str] = set()
    for conjunto in ejecutados:
        repetidos |= vistos & conjunto
        vistos |= conjunto
    if repetidos:
        errores.append(describir("Tests ejecutados por más de un fragmento", repetidos))

    if referencia is not None and len(ejecutados) == len(carpetas):
        faltantes = referencia[1] - vistos
        sobrantes = vistos - referencia[1]
        if faltantes:
            errores.append(describir("Tests no ejecutados", faltantes))
        if sobrantes:
            errores.append(describir("Tests ejecutados fuera de la lista", sobrantes))

    por_fragmento = ", ".join(str(len(conjunto)) for conjunto in ejecutados)
    resumen = f"{len(carpetas)} fragmentos verificados; métodos por fragmento: {por_fragmento}."
    return errores, resumen


def leer_pesos(ruta: str | None) -> dict[str, float] | None:
    if not ruta:
        return None
    return {clase: float(segundos) for clase, segundos in json.loads(Path(ruta).read_text(encoding="utf-8")).items()}


def duracion_en_segundos(texto: str) -> float:
    horas, minutos, segundos = texto.split(":")
    return int(horas) * 3600 + int(minutos) * 60 + float(segundos)


def medir(carpetas: list[Path]) -> dict[str, float]:
    pesos: dict[str, float] = {}
    for carpeta in carpetas:
        for trx in sorted(carpeta.rglob("*.trx")):
            raiz = ET.parse(trx).getroot()
            clase_por_test = {
                prueba.get("id"): prueba.find(f"{ESPACIO_TRX}TestMethod").get("className")
                for prueba in raiz.iter(f"{ESPACIO_TRX}UnitTest")
            }
            for resultado in raiz.iter(f"{ESPACIO_TRX}UnitTestResult"):
                clase = clase_por_test[resultado.get("testId")]
                pesos[clase] = pesos.get(clase, 0.0) + duracion_en_segundos(resultado.get("duration"))
    return {clase: round(segundos, 1) for clase, segundos in sorted(pesos.items())}


def comando_medir(args: argparse.Namespace) -> int:
    pesos = medir([Path(d) for d in args.directorios])
    Path(args.salida).write_text(json.dumps(pesos, indent=1) + "\n", encoding="utf-8", newline="")
    print(f"{len(pesos)} clases medidas, {sum(pesos.values()) / 60:.1f} min de tests en total.")
    return 0


def comando_plan(args: argparse.Namespace) -> int:
    lista = leer_lineas(Path(args.lista).read_text(encoding="utf-8"))
    plan = planificar(lista, args.fragmento, args.total, leer_pesos(args.pesos))
    Path(args.salida).write_text(plan.filtro, encoding="utf-8", newline="")
    print(
        f"Fragmento {args.fragmento}/{args.total}: {plan.clases_fragmento} de "
        f"{plan.clases_totales} clases y {plan.metodos_fragmento} de "
        f"{plan.metodos_totales} métodos."
    )
    return 0


def comando_verificar(args: argparse.Namespace) -> int:
    errores, resumen = verificar([Path(d) for d in args.directorios])
    if errores:
        for error in errores:
            print(f"ERROR: {error}", file=sys.stderr)
        return 1
    print(resumen)
    return 0


def crear_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    subcomandos = parser.add_subparsers(dest="comando", required=True)

    plan = subcomandos.add_parser("plan", help="Genera el filtro de VSTest de un fragmento.")
    plan.add_argument("--lista", required=True)
    plan.add_argument("--fragmento", type=int, required=True)
    plan.add_argument("--total", type=int, required=True)
    plan.add_argument("--salida", required=True)
    plan.add_argument("--pesos", help="JSON clase -> segundos medidos; sin él se reparte por orden.")
    plan.set_defaults(ejecutar=comando_plan)

    medida = subcomandos.add_parser("medir", help="Genera el JSON de pesos desde los trx de una corrida.")
    medida.add_argument("directorios", nargs="+")
    medida.add_argument("--salida", required=True)
    medida.set_defaults(ejecutar=comando_medir)

    comprobar = subcomandos.add_parser("verificar", help="Comprueba que los fragmentos cubran la suite.")
    comprobar.add_argument("directorios", nargs="+")
    comprobar.set_defaults(ejecutar=comando_verificar)
    return parser


def main(argumentos: list[str] | None = None) -> int:
    args = crear_parser().parse_args(argumentos)
    try:
        return args.ejecutar(args)
    except ErrorDeFragmentacion as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
