<?php
if(isset($_POST['id'])){
    $nuevoCodigo=$_POST['nuevoCodigo'];
    $consultarCodigo=mysqli_num_rows(mysqli_query($conexion,"SELECT * FROM articulos WHERE barra='$nuevoCodigo'"));
    if($consultarCodigo!=0) {
        //Si el Codigo de Barras existe, mostramos error
        $editar=FALSE;
        $articulo=mysqli_fetch_array(mysqli_query($conexion,"SELECT nombre FROM articulos WHERE barra='$nuevoCodigo'"));
        $mensaje='
            <div class="alert alert-danger">
                El Código nuevo ya existe para otro artículo: <strong><a href="index.php?menu=articulos&opc=nuevo&id='.$nuevoCodigo.'">'.$articulo[0].'</a></strong>. <a href="javascript:window.history.go(-1)">Volver.</a>
            </div>';
    }
    else {
        //Obtenemos Datos
        $id=$_POST['id'];
        $codigo=$_POST['codigo'];
        $nombre=$_POST['nombre'];
        $nombreOferta=$_POST['nombreOferta'];
        $lista=$_POST['lista'];
        $dtoGral=$_POST['dtoGral'];
        $costo=$_POST['costo'];
        $costoOferta=$_POST['costoOferta'];
        $precio=$_POST['precio'];
        $precioOferta=$_POST['precioOferta'];
        $precioCant=$_POST['precioCant'];
        $precioEmp=$_POST['precioEmp'];
        $existencia=$_POST['existencia'];
        $existenciaMinima=$_POST['existenciaMinima'];
        $reposicion=$_POST['reposicion'];
        $caja=$_POST['caja'];
        $proveedor=$_POST['proveedor'];
        $marca=$_POST['marca'];
        $grupo=$_POST['grupo'];
        $OfertaDia=$_POST['OfertaDia'];
        $OfertaDiaDesde=$_POST['OfertaDiaDesde'];
        $OfertaDiaHasta=$_POST['OfertaDiaHasta'];
        $OfertaHora=$_POST['OfertaHora'];
        $OfertaHoraDesde=$_POST['OfertaHoraDesde'];
        $OfertaHoraHasta=$_POST['OfertaHoraHasta'];
        $OfertaCant=$_POST['OfertaCant'];
        $OfertaCantN=$_POST['OfertaCantN'];
        $OfertaCant=$_POST['OfertaCant'];
        $producto=$_POST['producto'];
        $activo=$_POST['activo'];
        $uBulto=$_POST['uBulto'];
        //Generamos las consultas para crear un nuevo articulo y eliminar el anterior
        $crearArticulo="INSERT INTO articulos 
        (barra,codigo,nombre,nombreOferta,lista,dtoGral,costo,costoOferta,precio,precioOferta,precioCant,precioEmp,existencia,existenciaMinima,reposicion,caja,proveedor,marca,grupo,OfertaDia,OfertaDiaDesde,OfertaDiaHasta,OfertaHora,OfertaHoraHasta,OfertaHoraDesde,OfertaCant,OfertaCantN,activo,producto,uBulto) 
        VALUES 
        ('$nuevoCodigo','$codigo','$nombre','$nombreOferta','$lista','$dtoGral','$costo','$costoOferta','$precio','$precioOferta','$precioCant','$precioEmp','$existencia','$existenciaMinima','$reposicion','$caja','$proveedor','$marca','$grupo','$OfertaDia','$OfertaDiaDesde','$OfertaDiaHasta','$OfertaHora','$OfertaHoraHasta','$OfertaHoraDesde','$OfertaCant','$OfertaCantN','$activo','$producto','$uBulto')";
        $eliminarArticulo="UPDATE articulos SET activo='0', existencia='0' WHERE ID='$id'";
        if(mysqli_query($conexion,$crearArticulo)) {
            if(mysqli_query($conexion,$eliminarArticulo)) {
                $editar=FALSE;
                $mensaje='
                    <div class="alert alert-success">
                        El código del artículo <strong>'.$nombre.'</strong> se modificó correctamente. <a href="javascript:window.history.go(-2)">Volver.</a>
                    </div>';				
            }
            else {
                $editar=FALSE;
                $mensaje='
                    <div class="alert alert-danger">
                        Ocurrió un error al intentar cambiar el código, la consulta ejecutada fue :<br>
                    <strong>'.$eliminarArticulo.'</strong>. <a href="javascript:window.history.go(-1)">Volver.</a>
                    </div>';
            }
        }
        else {
            $editar=FALSE;
            $mensaje='
                <div class="alert alert-danger">
                    Ocurrió un error al intentar cambiar el código, la consulta ejecutada fue :<br>
                <strong>'.$crearArticulo.'</strong>. <a href="javascript:window.history.go(-1)">Volver.</a>
                </div>';
        }
    }
}
elseif(isset($_GET['id'])){
    $id=$_GET['id'];
    $buscarArticulo=mysqli_query($conexion,"SELECT * FROM articulos WHERE ID='$id'");
    if(mysqli_num_rows($buscarArticulo)==1) {
        $mostrarArticulo=mysqli_fetch_assoc($buscarArticulo);
        $editar=TRUE;
        $mensaje='
            <div class="alert alert-danger">
                Estás a punto de cambiar el código de barras del artículo '.$mostrarArticulo['nombre'].' ('.str_pad($mostrarArticulo['ID'],4,"0",STR_PAD_LEFT).'). <br>
                Esto creará un artículo idéntico con un nuevo ID, pero conservando todas las propiedades. El cambio es <strong>irreversible</strong>.
            </div>';
    }
    else { 
        $mensaje='
            <div class="alert alert-danger">
                El numero de Articulo no se encuentra en la Base de Datos. <a href="javascript:window.history.go(-2)">Volver.</a>
            </div>';
        $editar=FALSE;
    }
}
else {
        $mensaje='
            <div class="alert alert-danger">
                No se ha definido un ID para modificar. <a href="javascript:window.history.go(-2)">Volver.</a>
            </div>';
        $editar=FALSE;
}
if (@$editar==TRUE) { 
$contenido.='<div class="col-lg-12">'.$mensaje.'</div>
<form class="form-horizontal" name="articulos" id="articulos" method="post" action="index.php?menu=articulos&opc=cambiarCodigo&id='.$id.'" autocomplete="off">
<input type="hidden" name="id" id="id" value="'.@$mostrarArticulo['ID'].'">
<input type="hidden" name="barra" id="barra" value="'.@$mostrarArticulo['barra'].'">
<input type="hidden" name="codigo" id="codigo" value="'.@$mostrarArticulo['codigo'].'">
<input type="hidden" name="nombre" id="nombre" value="'.@$mostrarArticulo['nombre'].'">
<input type="hidden" name="nombreOferta" id="nombreOferta" value="'.@$mostrarArticulo['nombreOferta'].'">
<input type="hidden" name="lista" id="lista" value="'.@$mostrarArticulo['lista'].'">
<input type="hidden" name="dtoGral" id="dtoGral" value="'.@$mostrarArticulo['dtoGral'].'">
<input type="hidden" name="costo" id="costo" value="'.@$mostrarArticulo['costo'].'">
<input type="hidden" name="costoOferta" id="costoOferta" value="'.@$mostrarArticulo['costoOferta'].'">
<input type="hidden" name="precio" id="precio" value="'.@$mostrarArticulo['precio'].'">
<input type="hidden" name="precioOferta" id="precioOferta" value="'.@$mostrarArticulo['precioOferta'].'">
<input type="hidden" name="precioCant" id="precioCant" value="'.@$mostrarArticulo['precioCant'].'">
<input type="hidden" name="precioEmp" id="precioEmp" value="'.@$mostrarArticulo['precioEmp'].'">
<input type="hidden" name="existencia" id="existencia" value="'.@$mostrarArticulo['existencia'].'">
<input type="hidden" name="existenciaMinima" id="existenciaMinima" value="'.@$mostrarArticulo['existenciaMinima'].'">
<input type="hidden" name="reposicion" id="reposicion" value="'.@$mostrarArticulo['reposicion'].'">
<input type="hidden" name="caja" id="caja" value="'.@$mostrarArticulo['caja'].'">
<input type="hidden" name="proveedor" id="proveedor" value="'.@$mostrarArticulo['proveedor'].'">
<input type="hidden" name="marca" id="marca" value="'.@$mostrarArticulo['marca'].'">
<input type="hidden" name="grupo" id="grupo" value="'.@$mostrarArticulo['grupo'].'">
<input type="hidden" name="OfertaDia" id="OfertaDia" value="'.@$mostrarArticulo['OfertaDia'].'">
<input type="hidden" name="OfertaDiaDesde" id="OfertaDiaDesde" value="'.@$mostrarArticulo['OfertaDiaDesde'].'">
<input type="hidden" name="OfertaDiaHasta" id="OfertaDiaHasta" value="'.@$mostrarArticulo['OfertaDiaHasta'].'">
<input type="hidden" name="OfertaHora" id="OfertaHora" value="'.@$mostrarArticulo['OfertaHora'].'">
<input type="hidden" name="OfertaHoraDesde" id="OfertaHoraDesde" value="'.@$mostrarArticulo['OfertaHoraDesde'].'">
<input type="hidden" name="OfertaHoraHasta" id="OfertaHoraHasta" value="'.@$mostrarArticulo['OfertaHoraHasta'].'">
<input type="hidden" name="OfertaCant" id="OfertaCant" value="'.@$mostrarArticulo['OfertaCant'].'">
<input type="hidden" name="OfertaCantN" id="OfertaCantN" value="'.@$mostrarArticulo['OfertaCantN'].'">
<input type="hidden" name="producto" id="producto" value="'.@$mostrarArticulo['producto'].'">
<input type="hidden" name="activo" id="activo" value="'.@$mostrarArticulo['activo'].'">
<input type="hidden" name="uBulto" id="uBulto" value="'.@$mostrarArticulo['uBulto'].'">
<div class="col-lg-6">	
    <div class="form-group">
        <label for="id" class="control-label col-lg-4">ID</label>
        <div class="col-lg-8">
            <input type="text" value="'.@$mostrarArticulo['ID'].'" class="form-control" disabled>
        </div>
    </div>
    <div class="form-group">
        <label for="barra" class="control-label col-lg-4">Codigo</label>
        <div class="col-lg-8">
            <input type="text" value="'.@$mostrarArticulo['barra'].'" class="form-control" disabled>
        </div>
    </div>
    <div class="form-group">
        <label for="codigo" class="control-label col-lg-4">Codigo Interno</label>
        <div class="col-lg-8">
            <input type="text" value="'.@$mostrarArticulo['codigo'].'" class="form-control" disabled>
        </div>
    </div>
    <div class="form-group">
        <label for="nombre" class="control-label col-lg-4">Nombre</label>
        <div class="col-lg-8">
            <input class="form-control" type="text" value="'.@$mostrarArticulo['nombre'].'" disabled>
        </div>
    </div>
    <div class="form-group">
        <label for="nombreOferta" class="control-label col-lg-4">Nombre Oferta</label>
        <div class="col-lg-8">
            <input class="form-control" type="text" value="'.@$mostrarArticulo['nombreOferta'].'" disabled>
        </div>
    </div>
    <hr>
    <div class="form-group">
        <label for="lista" class="control-label col-lg-4">Precio Costo (Lista)</label>
        <div class="col-lg-8">
            <div class="input-group">
                <input class="form-control" type="text" value="'.@$mostrarArticulo['lista'].'" disabled>
                <span class="input-group-addon"><span class="glyphicon glyphicon-usd"></span></span>
            </div>
        </div>
    </div>
    <div class="form-group">
        <label for="dtoGral" class="control-label col-lg-4">Descuento</label>
        <div class="col-lg-8">
            <div class="input-group">
                <input class="form-control" type="text" value="'.@$mostrarArticulo['dtoGral'].'" disabled>
                <span class="input-group-addon"><span style="font-weight:bold;">%</span></span>
            </div>
        </div>
    </div>
    <div class="form-group">
        <label for="costo" class="control-label col-lg-4">Precio Costo (Nominal)</label>
        <div class="col-lg-8">
            <div class="input-group">
                <input class="form-control" type="text" value="'.@$mostrarArticulo['costo'].'" disabled>
                <span class="input-group-addon"><span class="glyphicon glyphicon-usd"></span></span>
            </div>
        </div>
    </div>
    <div class="form-group">
        <label for="costoOferta" class="control-label col-lg-4">Precio Costo (Oferta)</label>
        <div class="col-lg-8">
            <div class="input-group">
                <input class="form-control" type="text" value="'.@$mostrarArticulo['costoOferta'].'" disabled>
                <span class="input-group-addon"><span class="glyphicon glyphicon-usd"></span></span>
            </div>
        </div>
    </div>
    <hr>
    <div class="form-group">
        <label for="precio" class="control-label col-lg-4">Precio Venta (Lista)</label>
        <div class="col-lg-8">
            <div class="input-group">
                <input class="form-control" type="text" value="'.@$mostrarArticulo['precio'].'" disabled>
                <span class="input-group-addon"><span class="glyphicon glyphicon-usd"></span></span>
            </div>
        </div>
    </div>
    <div class="form-group">
        <label for="precioOferta" class="control-label col-lg-4">Precio Venta (Oferta)</label>
        <div class="col-lg-8">
            <div class="input-group">
                <input class="form-control" type="text" value="'.@$mostrarArticulo['precioOferta'].'" disabled>
                <span class="input-group-addon"><span class="glyphicon glyphicon-usd"></span></span>
            </div>
        </div>
    </div>
    <div class="form-group">
        <label for="precioCant" class="control-label col-lg-4">Precio Venta (Cant)</label>
        <div class="col-lg-8">
            <div class="input-group">
                <input class="form-control" type="text" value="'.@$mostrarArticulo['precioCant'].'" disabled>
                <span class="input-group-addon"><span class="glyphicon glyphicon-usd"></span></span>
            </div>
        </div>
    </div>
    <div class="form-group">
        <label for="precioEmp" class="control-label col-lg-4">Precio Venta (Empl)</label>
        <div class="col-lg-8">
            <div class="input-group">
                <input class="form-control" type="text" value="'.@$mostrarArticulo['precioEmp'].'" disabled>
                <span class="input-group-addon"><span class="glyphicon glyphicon-usd"></span></span>
            </div>
        </div>
    </div>
    <hr>
    <div class="form-group">
        <label for="existencia" class="control-label col-lg-4">Existencias</label>
        <div class="col-lg-8">
            <div class="input-group">
                <input class="form-control" type="text" value="'.@$mostrarArticulo['existencia'].'" disabled>
                <span class="input-group-addon"><span class="glyphicon glyphicon-usd"></span></span>
            </div>
        </div>
    </div>
    <div class="form-group">
        <label for="existenciaMinima" class="control-label col-lg-4">Existencia Minima</label>
        <div class="col-lg-8">
            <div class="input-group">
                <input class="form-control" type="text" value="'.@$mostrarArticulo['existenciaMinima'].'" disabled>
                <span class="input-group-addon"><span class="glyphicon glyphicon-usd"></span></span>
            </div>
        </div>
    </div>
    <div class="form-group">
        <label for="reposicion" class="control-label col-lg-4">Reposicion</label>
        <div class="col-lg-8">
            <div class="input-group">
                <input class="form-control" type="text" value="'.@$mostrarArticulo['reposicion'].'" disabled>
                <span class="input-group-addon"><span class="glyphicon glyphicon-usd"></span></span>
            </div>
        </div>
    </div>
</div>
<div class="col-lg-6">	
    <div class="form-group">
        <label for="reposicion" class="control-label col-lg-4">Nuevo Codigo</label>
        <div class="col-lg-8">
            <div class="input-group">
                <input class="form-control" type="text" value="" name="nuevoCodigo" id="nuevoCodigo" autofocus>
                <span class="input-group-addon"><i class="fa fa-barcode"></i></span>
            </div>
        </div>
    </div>
    <div class="form-group">
        <label class="control-label col-lg-4">Caja</label>
        <div class="col-lg-8">
            <select class="form-control chzn-select" disabled>';
                $obtenerCaja=mysqli_query($conexion,"SELECT id,nombre FROM caja ORDER BY nombre");
                while($mostrarCaja=mysqli_fetch_assoc($obtenerCaja)) {
                    if(@$mostrarArticulo['caja']==$mostrarCaja['id']) {
                        $contenido.='
                <option selected value="'.$mostrarCaja['id'].'">'.$mostrarCaja['nombre'].'</option>';
                    }
                    else {
                        $contenido.='
                <option value="'.$mostrarCaja['id'].'">'.$mostrarCaja['nombre'].'</option>';
                    }	
                }
       $contenido.='
            </select>
        </div>
    </div>
    <div class="form-group">
        <label class="control-label col-lg-4">Proveedor</label>
        <div class="col-lg-8">
            <select class="form-control chzn-select" disabled>';
                $obtenerProveedor=mysqli_query($conexion,"SELECT id,nombre FROM proveedores ORDER BY nombre");
                while($mostrarProveedor=mysqli_fetch_assoc($obtenerProveedor)) {
                    if(@$mostrarArticulo['proveedor']==$mostrarProveedor['id']) {
                        $contenido.='
                <option selected value="'.$mostrarProveedor['id'].'">'.$mostrarProveedor['nombre'].'</option>';
                    }
                    else {
                        $contenido.='
                <option value="'.$mostrarProveedor['id'].'">'.$mostrarProveedor['nombre'].'</option>';
                    }	
                }
       $contenido.='
            </select>
        </div>
    </div>
    <div class="form-group">
        <label class="control-label col-lg-4">Marca</label>
        <div class="col-lg-8">
            <select class="form-control chzn-select" disabled>';
                $obtenerMarca=mysqli_query($conexion,"SELECT id,nombre FROM marcas ORDER BY nombre");
                while($mostrarMarca=mysqli_fetch_assoc($obtenerMarca)) {
                    if(@$mostrarArticulo['marca']==$mostrarMarca['id']) {
                        $contenido.='
                <option selected value="'.$mostrarMarca['id'].'">'.$mostrarMarca['nombre'].'</option>';
                    }
                    else {
                        $contenido.='
                <option value="'.$mostrarMarca['id'].'">'.$mostrarMarca['nombre'].'</option>';
                    }	
                }
       $contenido.='
            </select>
        </div>
    </div>
    <div class="form-group">
        <label class="control-label col-lg-4">Grupo</label>
        <div class="col-lg-8">
            <select class="form-control chzn-select" disabled>';
                $obtenerGrupo=mysqli_query($conexion,"SELECT id,nombre FROM grupos ORDER BY nombre");
                while($mostrarGrupo=mysqli_fetch_assoc($obtenerGrupo)) {
                    if(@$mostrarArticulo['grupo']==$mostrarGrupo['id']) {
                        $contenido.='
                <option selected value="'.$mostrarGrupo['id'].'">'.$mostrarGrupo['nombre'].'</option>';
                    }
                    else {
                        $contenido.='
                <option value="'.$mostrarGrupo['id'].'">'.$mostrarGrupo['nombre'].'</option>';
                    }	
                }
                if(@$mostrarArticulo['OfertaDia']==1) { 
                    $diaChecked1='checked'; 
                    $diaDesde=explode("-",$mostrarArticulo['OfertaDiaDesde']); // aaaa-mm-dd
                    $diaDesde=$diaDesde[2].'/'.$diaDesde[1].'/'.$diaDesde[0]; // dd/mm/aaaa
                    $diaHasta=explode("-",$mostrarArticulo['OfertaDiaHasta']); // aaaa-mm-dd
                    $diaHasta=$diaHasta[2].'/'.$diaHasta[1].'/'.$diaHasta[0]; // dd/mm/aaaa
                    $dia=$diaDesde.' - '.$diaHasta;
                }
                elseif(@$mostrarArticulo['OfertaDia']==0) { $diaChecked2='checked'; }
                else { $diaChecked2='checked'; }
                if(@$mostrarArticulo['OfertaHora']==1) { 
                    $horaChecked1='checked'; 
                    $horaDesde=explode(":",$mostrarArticulo['OfertaHoraDesde']); // hh:mm:ss
                    $horaDesde=$horaDesde[0].':'.$horaDesde[1].' hs'; //  hh:mm:ss
                    $horaHasta=explode(":",$mostrarArticulo['OfertaHoraHasta']); // hh:mm:ss
                    $horaHasta=$horaHasta[0].':'.$horaHasta[1].' hs'; // hh:mm:ss
                    $hora=$horaDesde.' - '.$horaHasta;
                }
                elseif(@$mostrarArticulo['OfertaHora']==0) { $horaChecked2='checked'; }
                else { $horaChecked2='checked'; }
                if(@$mostrarArticulo['OfertaCant']==1) { $cantChecked1='checked'; }
                elseif(@$mostrarArticulo['OfertaCant']==0) { $cantChecked2='checked'; }
                else { $cantChecked2='checked'; }
                if(@$mostrarArticulo['producto']==1) { $productoChecked1='checked'; }
                elseif(@$mostrarArticulo['producto']==0) { $productoChecked2='checked'; }
                else { $productoChecked1='checked'; }
                if(@$mostrarArticulo['activo']==1) { $activoChecked1='checked'; }
                elseif(@$mostrarArticulo['activo']==0) { $activoChecked2='checked'; }
                else { $activoChecked1='checked'; }
       $contenido.='
            </select>
        </div>
    </div>
    <hr>
    <div class="form-group">
        <label class="control-label col-lg-4">Oferta por Dias</label>
            <div class="col-lg-8">
                <div class="checkbox">
                    <div class="col-lg-4">
                    
                        <input type="radio" name="ofertaDia" value="1" '.@$diaChecked1.' disabled> Si
                    </div>
                    <div class="col-lg-4">
                        <input type="radio" name="ofertaDia" value="0" '.@$diaChecked2.' disabled> No
                    </div>
                </div>
            </div>
        <label>
    </div>
    <div class="form-group">
        <label class="control-label col-lg-4">Fecha Oferta</label>
        <div class="col-lg-8">
            <div class="input-group">
                <input class="form-control" type="text" data-mask="99/99/9999 - 99/99/9999" value="'.@$dia.'" disabled>
                <span class="input-group-addon"><span class="glyphicon glyphicon-calendar"></span></span>
            </div>
        </div>
    </div>
    <div class="form-group">
        <label class="control-label col-lg-4">Oferta por Horas</label>
            <div class="col-lg-8">
                <div class="checkbox">
                    <div class="col-lg-4">
                        <input type="radio" value="1" '.@$horaChecked1.' disabled> Si
                    </div>
                    <div class="col-lg-4">
                        <input type="radio" value="0" '.@$horaChecked2.' disabled> No
                    </div>
                </div>
            </div>
        <label>
    </div>
    <div class="form-group">
        <label class="control-label col-lg-4">Horario Oferta</label>
        <div class="col-lg-8">
            <div class="input-group">
                <input class="form-control" type="text" value="'.@$hora.'" data-mask="99:99 hs - 99:99 hs" disabled>
                <span class="input-group-addon"><span class="glyphicon glyphicon-time"></span></span>
            </div>
        </div>
    </div>
    <div class="form-group">
        <label class="control-label col-lg-4">Oferta por Cantidad</label>
            <div class="col-lg-8">
                <div class="checkbox">
                    <div class="col-lg-4">
                        <input type="radio" value="1" '.@$cantChecked1.' disabled> Si
                    </div>
                    <div class="col-lg-4">
                        <input type="radio" value="0" '.@$cantChecked2.' disabled> No
                    </div>
                </div>
            </div>
        <label>
    </div>
    <div class="form-group">
        <label class="control-label col-lg-4">Cantidad</label>
        <div class="col-lg-8">
            <input class="form-control" value="'.@$mostrarArticulo['OfertaCantN'].'" type="text" disabled>
        </div>
    </div>
    <hr>
    <div class="form-group">
        <label class="control-label col-lg-4">Unidades por Bulto</label>
        <div class="col-lg-8">
            <input class="form-control" value="'.@$mostrarArticulo['uBulto'].'" type="text" disabled>
        </div>
    </div>
    <hr>
    <div class="form-group">
        <label class="control-label col-lg-4">Producto/Servicio</label>
            <div class="col-lg-8">
                <div class="checkbox">
                    <div class="col-lg-6">
                        <input type="radio" value="1" disabled '.@$productoChecked1.'> Producto
                    </div>
                    <div class="col-lg-6">
                        <input type="radio" value="0" disabled '.@$productoChecked2.'> Servicio
                    </div>
                </div>
            </div>
        <label>
    </div>
    <div class="form-group">
        <label class="control-label col-lg-4">Activo</label>
            <div class="col-lg-8">
                <div class="checkbox">
                    <div class="col-lg-4">
                        <input  type="radio" value="1" disabled '.@$activoChecked1.'> Si
                    </div>
                    <div class="col-lg-4">
                        <input type="radio" value="0" disabled '.@$activoChecked2.'> No
                    </div>
                </div>
            </div>
        <label>
    </div>
    <br>
    <br>
    <div class="form-group">
        <label class="control-label col-lg-4"></label>
        <div class="col-lg-4">
            <input name="deshacer" id="deshacer" type="reset" class="form-control btn btn-default" value="Deshacer">
        </div>
        <div class="col-lg-4">
            <input name="accion" id="accion" type="submit" class="form-control btn btn-success" value="Cambiar Codigo" tabindex="18">
        </div>
    </div>
</div>
</form>
';	
}
else { 
    $contenido.='<div class="col-lg-12">'.$mensaje.'</div>';
}