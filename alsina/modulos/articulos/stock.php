<?php
$consulta=mysqli_query($conexion,"SELECT SUM(existencia) AS existencias, SUM(precio*existencia) AS precioExistencia, SUM(precioOferta*existencia) AS precioOferta, SUM(precioCant*existencia) AS precioCant, SUM(costo*existencia) AS precioCosto, SUM(lista*existencia) AS precioLista FROM articulos WHERE producto=1 AND activo=1");
$resultado=mysqli_fetch_assoc($consulta);

if(isset($_GET['proveedor']) && $_GET['proveedor'] != '*') { 
    $proveedor='AND id_proveedor="'.$_GET['proveedor'].'"'; 
} else { $proveedor = ''; }
$consultaSinStock_="SELECT * FROM articulos WHERE existencia<=0 $proveedor AND producto=1 AND activo=1 LIMIT 150";
$consultaSinStock=mysqli_query($conexion,$consultaSinStock_);
$consultaBajoStock_="SELECT * FROM articulos WHERE existencia < existenciaMinima AND existencia > 0 $proveedor  AND producto=1 AND activo=1 LIMIT 150";
$consultaBajoStock=mysqli_query($conexion,$consultaBajoStock_);
$contenido.='
<div class="col-lg-12">
    <table class="table table-striped responsive-table table-hover table-bordered">
        <tr>
            <td style="font-weight:bold;">Existencias Totales</td>
            <td>'.$resultado['existencias'].' articulos</td>
            <td style="font-weight:bold;">Total Precio Nominal</td>
            <td>$ '.$resultado['precioCosto'].'</td>
            <td style="font-weight:bold;">Total Precio Oferta</td>
            <td>$ '.$resultado['precioOferta'].'</td>
        </tr>
        <tr>
            <td style="font-weight:bold;">Total Precio Lista</td>
            <td>$ '.$resultado['precioLista'].'</td>
            <td style="font-weight:bold;">Total Precio Venta</td>
            <td>$ '.$resultado['precioExistencia'].'</td>
            <td style="font-weight:bold;">Total Precio Cantidad</td>
            <td>$ '.$resultado['precioCant'].'</td>
        </tr>
    </table>		
</div>
<div class="col-lg-6">
    <table class="table table-striped responsive-table table-hover table-bordered">
        <tr>
            <th style="text-decoration:italic">Productos sin Stock</th>
            <th colspan="2">
                <form method="get" action="" name="filtrarSinStock" id="filtrarSinStock">
                    <input type="hidden" name="menu" value="articulos">
                    <input type="hidden" name="opc" value="stock">
                    <select name="proveedor" id="proveedor" class="form-control chzn-select" onchange="this.form.submit()">
                    <option value="*">Todos</option>';
                                $obtenerProveedor=mysqli_query($conexion,"SELECT id,nombre FROM proveedores ORDER BY nombre");
                                while($mostrarProveedor=mysqli_fetch_assoc($obtenerProveedor)) {
                                    if(@$_GET['proveedor']==$mostrarProveedor['id']) {
                                        $contenido.='
                                <option selected value="'.$mostrarProveedor['id'].'">'.$mostrarProveedor['nombre'].'</option>';
                                    }
                                    else {
                                        $contenido.='
                                <option value="'.$mostrarProveedor['id'].'">'.$mostrarProveedor['nombre'].'</option>';
                                    }	
                                }
                                if(isset($_GET['proveedor'])) { 
                                    $proveedor=$proveedor=$_GET['proveedor']; 
                                    $imprimir='onclick="imprimirArticulos(\'1\','.$proveedor.')"';
                                }
                                else { $imprimir=''; }			
                       $contenido.='
                    </select>
                </form>
            </th>
        </tr>
        <tr>
            <th colspan="2">Acciones: </th>
            <th><a title="Imprimir Lista" '.$imprimir.'><i class="fa fa-print"></i></a></th>
        </tr>
        <tr>
            <th>Producto</th>
            <th>Minimo</th>
            <th>Reposicion</th>
        </tr>';
        while($resultadoSinStock=mysqli_fetch_assoc($consultaSinStock)) {
            $contenido.='
        <tr>
            <td>'.$resultadoSinStock['nombre'].'</td>
            <td>'.$resultadoSinStock['existenciaMinima'].'</td>
            <td>'.$resultadoSinStock['reposicion'].'</td>
        </tr>';
        }
$contenido.='
    </table>
</div>
<div class="col-lg-6">
    <table class="table table-striped responsive-table table-hover table-bordered">
        <tr>
            <th style="text-decoration:italic">Productos por debajo del Minimo</th>
            <th colspan="2">
                <form method="get" action="" name="filtrarSinStock" id="filtrarSinStock">
                    <input type="hidden" name="menu" value="articulos">
                    <input type="hidden" name="opc" value="stock">
                    <select name="proveedor" id="proveedor" class="form-control chzn-select" onchange="this.form.submit()">
                    <option value="*">Todos</option>';
                                $obtenerProveedor=mysqli_query($conexion,"SELECT id,nombre FROM proveedores ORDER BY nombre");
                                while($mostrarProveedor=mysqli_fetch_assoc($obtenerProveedor)) {
                                    if(@$_GET['proveedor']==$mostrarProveedor['id']) {
                                        $contenido.='
                                <option selected value="'.$mostrarProveedor['id'].'">'.$mostrarProveedor['nombre'].'</option>';
                                    }
                                    else {
                                        $contenido.='
                                <option value="'.$mostrarProveedor['id'].'">'.$mostrarProveedor['nombre'].'</option>';
                                    }	
                                }
                                if(isset($_GET['proveedor'])) { 
                                    $proveedor=$proveedor=$_GET['proveedor']; 
                                    $imprimir='onclick="imprimirArticulos(\'2\','.$proveedor.')"';
                                }
                                else { $imprimir=''; }	
                       $contenido.='
                    </select>
                </form>
            </th>
        </tr>
        <tr>
            <th colspan="2">Acciones: </th>
            <th><a title="Imprimir Lista" '.$imprimir.'><i class="fa fa-print"></i></a></th>
        </tr>
        <tr>
            <th>Producto</th>
            <th>Stock</th>
            <th>Minimo</th>
        </tr>';
        while($resultadoBajoStock=mysqli_fetch_assoc($consultaBajoStock)) {
            $contenido.='
        <tr>
            <td>'.$resultadoBajoStock['nombre'].'</td>
            <td>'.$resultadoBajoStock['existencia'].'</td>
            <td>'.$resultadoBajoStock['existenciaMinima'].'</td>
        </tr>';
        }
$contenido.='
    </table>
</div>


';