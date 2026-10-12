import tempfile
import unittest
from pathlib import Path

import fragmentar_integracion as frag

LISTA = [
    "Ways.IntegrationTests.AaaTests.Uno",
    "Ways.IntegrationTests.AaaTests.Dos",
    "Ways.IntegrationTests.BbbTests.Uno",
    "Ways.IntegrationTests.CccTests.Uno",
    "Ways.IntegrationTests.CccTests.Dos",
    "Ways.IntegrationTests.CccTests.Tres",
    "Ways.IntegrationTests.DddTests.Uno",
]

TRX = """<?xml version="1.0" encoding="utf-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <TestDefinitions>
{definiciones}
  </TestDefinitions>
</TestRun>
"""


def escribir_trx(carpeta: Path, nombres: list[str]) -> None:
    definiciones = []
    for nombre in nombres:
        clase, metodo = nombre.rsplit(".", 1)
        definiciones.append(
            f'    <UnitTest name="{metodo}"><TestMethod className="{clase}" name="{metodo}" /></UnitTest>'
        )
    (carpeta / "r.trx").write_text(
        TRX.format(definiciones="\n".join(definiciones)), encoding="utf-8"
    )


class ReparteClasesTests(unittest.TestCase):
    def test_la_clase_es_el_nombre_sin_el_ultimo_segmento(self):
        self.assertEqual(
            "Ways.IntegrationTests.AaaTests",
            frag.clase_de("Ways.IntegrationTests.AaaTests.Uno"),
        )

    def test_reparto_en_dos_es_disjunto_y_cubre_todas_las_clases(self):
        clases = frag.clases_de(LISTA)
        partes = [frag.clases_del_fragmento(clases, k, 2) for k in (1, 2)]
        self.assertEqual(set(), set(partes[0]) & set(partes[1]))
        self.assertEqual(set(clases), set(partes[0]) | set(partes[1]))

    def test_reparto_en_tres_es_disjunto_y_cubre_todas_las_clases(self):
        clases = frag.clases_de(LISTA)
        partes = [frag.clases_del_fragmento(clases, k, 3) for k in (1, 2, 3)]
        union = set()
        for parte in partes:
            self.assertEqual(set(), union & set(parte))
            union |= set(parte)
        self.assertEqual(set(clases), union)

    def test_asigna_por_indice_ordinal_modulo_n(self):
        clases = frag.clases_de(LISTA)
        self.assertEqual(
            ["Ways.IntegrationTests.AaaTests", "Ways.IntegrationTests.CccTests"],
            frag.clases_del_fragmento(clases, 1, 2),
        )
        self.assertEqual(
            ["Ways.IntegrationTests.BbbTests", "Ways.IntegrationTests.DddTests"],
            frag.clases_del_fragmento(clases, 2, 2),
        )

    def test_el_orden_de_entrada_no_cambia_el_reparto(self):
        al_reves = list(reversed(LISTA))
        self.assertEqual(
            frag.clases_del_fragmento(frag.clases_de(LISTA), 1, 2),
            frag.clases_del_fragmento(frag.clases_de(al_reves), 1, 2),
        )

    def test_todos_los_metodos_de_una_clase_caen_en_el_mismo_fragmento(self):
        for total in (2, 3):
            fragmentos = {}
            for k in range(1, total + 1):
                clases = frag.clases_del_fragmento(frag.clases_de(LISTA), k, total)
                for linea in LISTA:
                    if frag.clase_de(linea) in clases:
                        fragmentos.setdefault(frag.clase_de(linea), set()).add(k)
            for clase, ks in fragmentos.items():
                self.assertEqual(1, len(ks), clase)


class FiltroTests(unittest.TestCase):
    def test_el_filtro_une_las_clases_con_barra_vertical_y_punto_final(self):
        self.assertEqual(
            "FullyQualifiedName~A.B.|FullyQualifiedName~A.C.",
            frag.construir_filtro(["A.B", "A.C"]),
        )

    def test_escapa_los_caracteres_especiales_del_filtro(self):
        self.assertEqual(
            r"A\(\)\&\|\=\!\~B",
            frag.escapar_filtro("A()&|=!~B"),
        )

    def test_el_filtro_escapa_el_nombre_de_clase(self):
        self.assertEqual(
            r"FullyQualifiedName~A.B\(x\).",
            frag.construir_filtro(["A.B(x)"]),
        )

    def test_no_deja_salto_de_linea_final(self):
        self.assertFalse(frag.construir_filtro(["A.B"]).endswith("\n"))


class PlanificarTests(unittest.TestCase):
    def test_devuelve_filtro_y_resumen_del_fragmento(self):
        plan = frag.planificar(LISTA, 1, 2)
        self.assertEqual(
            "FullyQualifiedName~Ways.IntegrationTests.AaaTests.|"
            "FullyQualifiedName~Ways.IntegrationTests.CccTests.",
            plan.filtro,
        )
        self.assertEqual(2, plan.clases_fragmento)
        self.assertEqual(4, plan.clases_totales)
        self.assertEqual(5, plan.metodos_fragmento)
        self.assertEqual(7, plan.metodos_totales)

    def test_rechaza_lista_vacia(self):
        with self.assertRaisesRegex(frag.ErrorDeFragmentacion, "vac"):
            frag.planificar([], 1, 2)

    def test_rechaza_fragmento_fuera_de_rango(self):
        for k in (0, 3, -1):
            with self.assertRaisesRegex(frag.ErrorDeFragmentacion, "fragmento"):
                frag.planificar(LISTA, k, 2)

    def test_rechaza_total_menor_a_uno(self):
        with self.assertRaisesRegex(frag.ErrorDeFragmentacion, "total"):
            frag.planificar(LISTA, 1, 0)

    def test_rechaza_fragmento_sin_clases(self):
        with self.assertRaisesRegex(frag.ErrorDeFragmentacion, "sin clases"):
            frag.planificar(["A.B.M"], 2, 2)

    def test_ignora_lineas_en_blanco(self):
        plan = frag.planificar(LISTA + ["", "  "], 1, 2)
        self.assertEqual(7, plan.metodos_totales)


class CliPlanTests(unittest.TestCase):
    def test_escribe_el_filtro_sin_salto_final_y_devuelve_cero(self):
        with tempfile.TemporaryDirectory() as tmp:
            lista = Path(tmp) / "lista.txt"
            salida = Path(tmp) / "filtro.txt"
            lista.write_text("\n".join(LISTA) + "\n", encoding="utf-8")
            codigo = frag.main(
                ["plan", "--lista", str(lista), "--fragmento", "2",
                 "--total", "2", "--salida", str(salida)]
            )
            self.assertEqual(0, codigo)
            self.assertEqual(
                "FullyQualifiedName~Ways.IntegrationTests.BbbTests.|"
                "FullyQualifiedName~Ways.IntegrationTests.DddTests.",
                salida.read_text(encoding="utf-8"),
            )

    def test_devuelve_distinto_de_cero_con_lista_vacia(self):
        with tempfile.TemporaryDirectory() as tmp:
            lista = Path(tmp) / "lista.txt"
            salida = Path(tmp) / "filtro.txt"
            lista.write_text("", encoding="utf-8")
            codigo = frag.main(
                ["plan", "--lista", str(lista), "--fragmento", "1",
                 "--total", "2", "--salida", str(salida)]
            )
            self.assertNotEqual(0, codigo)
            self.assertFalse(salida.exists())


class VerificarTests(unittest.TestCase):
    def armar(self, tmp: str, fragmentos: dict[str, tuple[list[str], list[str] | None]]):
        carpetas = []
        for nombre, (lista, ejecutados) in fragmentos.items():
            carpeta = Path(tmp) / nombre
            carpeta.mkdir()
            if lista is not None:
                (carpeta / "lista.txt").write_text("\n".join(lista) + "\n", encoding="utf-8")
            if ejecutados is not None:
                escribir_trx(carpeta, ejecutados)
            carpetas.append(carpeta)
        return carpetas

    def partes(self):
        f1 = frag.clases_del_fragmento(frag.clases_de(LISTA), 1, 2)
        a = [x for x in LISTA if frag.clase_de(x) in f1]
        b = [x for x in LISTA if frag.clase_de(x) not in f1]
        return a, b

    def test_pasa_con_fragmentos_consistentes(self):
        a, b = self.partes()
        with tempfile.TemporaryDirectory() as tmp:
            carpetas = self.armar(tmp, {"f1": (LISTA, a), "f2": (LISTA, b)})
            errores, resumen = frag.verificar(carpetas)
        self.assertEqual([], errores)
        self.assertIn("2 fragmentos", resumen)

    def test_un_theory_con_filas_repetidas_cuenta_una_vez(self):
        a, b = self.partes()
        with tempfile.TemporaryDirectory() as tmp:
            carpetas = self.armar(tmp, {"f1": (LISTA, a + a), "f2": (LISTA, b)})
            errores, _ = frag.verificar(carpetas)
        self.assertEqual([], errores)

    def test_falla_si_falta_un_metodo(self):
        a, b = self.partes()
        with tempfile.TemporaryDirectory() as tmp:
            carpetas = self.armar(tmp, {"f1": (LISTA, a), "f2": (LISTA, b[1:])})
            errores, _ = frag.verificar(carpetas)
        self.assertTrue(any("no ejecutados" in e and b[0] in e for e in errores), errores)

    def test_falla_si_un_metodo_lo_ejecutan_dos_fragmentos(self):
        a, b = self.partes()
        with tempfile.TemporaryDirectory() as tmp:
            carpetas = self.armar(tmp, {"f1": (LISTA, a + [b[0]]), "f2": (LISTA, b)})
            errores, _ = frag.verificar(carpetas)
        self.assertTrue(any("más de un fragmento" in e and b[0] in e for e in errores), errores)

    def test_falla_si_se_pasa_dos_veces_el_mismo_directorio(self):
        a, b = self.partes()
        with tempfile.TemporaryDirectory() as tmp:
            carpetas = self.armar(tmp, {"f1": (LISTA, a + b)})
            errores, _ = frag.verificar([carpetas[0], carpetas[0]])
        self.assertTrue(any("más de un fragmento" in e for e in errores), errores)

    def test_falla_si_se_ejecuta_un_metodo_fuera_de_la_lista(self):
        a, b = self.partes()
        with tempfile.TemporaryDirectory() as tmp:
            carpetas = self.armar(
                tmp, {"f1": (LISTA, a + ["X.Y.Z"]), "f2": (LISTA, b)}
            )
            errores, _ = frag.verificar(carpetas)
        self.assertTrue(any("fuera de la lista" in e and "X.Y.Z" in e for e in errores), errores)

    def test_falla_si_las_listas_difieren(self):
        a, b = self.partes()
        with tempfile.TemporaryDirectory() as tmp:
            carpetas = self.armar(tmp, {"f1": (LISTA, a), "f2": (LISTA[:-1], b)})
            errores, _ = frag.verificar(carpetas)
        self.assertTrue(any("lista.txt" in e and "difiere" in e for e in errores), errores)

    def test_falla_si_un_directorio_no_tiene_trx(self):
        a, _ = self.partes()
        with tempfile.TemporaryDirectory() as tmp:
            carpetas = self.armar(tmp, {"f1": (LISTA, a), "f2": (LISTA, None)})
            errores, _ = frag.verificar(carpetas)
        self.assertTrue(any("trx" in e for e in errores), errores)

    def test_falla_si_un_directorio_no_tiene_lista(self):
        a, b = self.partes()
        with tempfile.TemporaryDirectory() as tmp:
            carpetas = self.armar(tmp, {"f1": (LISTA, a), "f2": (None, b)})
            errores, _ = frag.verificar(carpetas)
        self.assertTrue(any("lista.txt" in e for e in errores), errores)

    def test_limita_los_nombres_impresos_a_veinte(self):
        grande = [f"N.C{i:03d}.M" for i in range(30)]
        with tempfile.TemporaryDirectory() as tmp:
            carpetas = self.armar(tmp, {"f1": (grande, [])})
            errores, _ = frag.verificar(carpetas)
        faltantes = [e for e in errores if "no ejecutados" in e][0]
        self.assertIn("30", faltantes)
        self.assertEqual(20, faltantes.count("N.C"))

    def test_cli_devuelve_distinto_de_cero_con_errores(self):
        a, b = self.partes()
        with tempfile.TemporaryDirectory() as tmp:
            carpetas = self.armar(tmp, {"f1": (LISTA, a), "f2": (LISTA, b[1:])})
            self.assertNotEqual(0, frag.main(["verificar", *map(str, carpetas)]))

    def test_cli_devuelve_cero_cuando_todo_coincide(self):
        a, b = self.partes()
        with tempfile.TemporaryDirectory() as tmp:
            carpetas = self.armar(tmp, {"f1": (LISTA, a), "f2": (LISTA, b)})
            self.assertEqual(0, frag.main(["verificar", *map(str, carpetas)]))


if __name__ == "__main__":
    unittest.main()
