<?php
    if(isset($_POST['id'])){
        if($_POST['accion'] == 'Editar Articulo') {
            //Datos del articulo
            $id = $_POST['id'];
            $codigo = $_POST['codigo'];
            $nombre = ucwords($_POST['nombre']);
            $nombreOferta = ucwords($_POST['nombreOferta']);

            $lista = number_format(!empty($_POST['lista']) ? $_POST['lista'] : 0, 2, ".", "");
            $dtoGral = number_format(!empty($_POST['dtoGral']) ? $_POST['dtoGral'] : 0, 2, ".", "");
            $costo = number_format(!empty($_POST['costo']) ? $_POST['costo'] : 0, 2, ".", "");
            $costoOferta = number_format(!empty($_POST['costoOferta']) ? $_POST['costoOferta'] : 0, 2, ".", "");
            $precio = number_format(!empty($_POST['precio']) ? $_POST['precio'] : 0, 2, ".", "");
            $precioOferta = number_format(!empty($_POST['precioOferta']) ? $_POST['precioOferta'] : 0, 2, ".", "");
            $precioCant = number_format(!empty($_POST['precioCant']) ? $_POST['precioCant'] : 0, 2, ".", "");
            $precioEmp = number_format(!empty($_POST['precioEmp']) ? $_POST['precioEmp'] : 0, 2, ".", "");

            $existencia = !empty($_POST['existencia']) ? $_POST['existencia'] : 0;
            $existenciaMinima = !empty($_POST['existenciaMinima']) ? $_POST['existenciaMinima'] : 0;
            $reposicion = !empty($_POST['reposicion']) ? $_POST['reposicion'] : 0;
            $uBulto = !empty($_POST['uBulto']) ? $_POST['uBulto'] : 0;
            
            $activo = $_POST['activo'] ?? "" == "on" ? 1 : 0;
            $producto = $_POST['producto'] ?? "" == "on" ? 1 : 0;
            $tolerancia = 0.00;
            $id_area = $_POST['id_area'];
            $id_proveedor = $_POST['id_proveedor'];
            $id_marca = $_POST['id_marca'];
            $id_grupo = $_POST['id_grupo'];
            
            $ofertaDia = $_POST['OfertaDia'] ?? "" == "on" ? 1 : 0;
            $ofertaDiaDesde = $_POST['OfertaDiaDesde'];
            $ofertaDiaHasta = $_POST['OfertaDiaHasta'];

            $ofertaHora = $_POST['OfertaHora'] ?? "" == "on" ? 1 : 0;
            $ofertaHoraDesde = $_POST['OfertaHoraDesde'];
            $ofertaHoraHasta = $_POST['OfertaHoraHasta'];

            $ofertaCant = $_POST['OfertaCant'] ?? "" == "on" ? 1 : 0;
            $ofertaCantN = $_POST['OfertaCantN'];                           

            $consultaArticulo = "UPDATE articulos 
                                 SET    codigo = '$codigo',
                                        nombre = '$nombre', 
                                        nombreOferta = '$nombreOferta', 
                                        lista = '$lista',
                                        dtoGral = '$dtoGral',
                                        costo = '$costo',
                                        costoOferta = '$costoOferta',
                                        precio = '$precio',
                                        precioOferta = '$precioOferta',
                                        precioCant = '$precioCant',
                                        precioEmp = '$precioEmp',
                                        tolerancia = '$tolerancia',
                                        existencia = '$existencia',
                                        existenciaMinima = '$existenciaMinima',
                                        reposicion = '$reposicion',
                                        uBulto = '$uBulto',
                                        id_area = '$id_area', 
                                        id_proveedor = '$id_proveedor', 
                                        id_marca = '$id_marca', 
                                        id_grupo = '$id_grupo', 
                                        activo = '$activo',
                                        producto = '$producto',
                                        OfertaDia = '$ofertaDia',
                                        OfertaDiaDesde = '$ofertaDiaDesde',
                                        OfertaDiaHasta = '$ofertaDiaHasta',
                                        OfertaHora = '$ofertaHora',
                                        OfertaHoraDesde = '$ofertaHoraDesde',
                                        OfertaHoraHasta = '$ofertaHoraHasta',
                                        ofertaCant = '$ofertaCant',
                                        ofertaCantN = '$ofertaCantN'
                                 WHERE  id = '$id'";        

            if ($editarArticulo = mysqli_query($conexion, $consultaArticulo)) {
                $mensaje = '
                <div class="alert alert-success">
                    El Articulo ID: '.str_pad($id, 4, "0", STR_PAD_LEFT).' ('.$nombre.') ha sido editado correctamente. <a href="javascript:window.history.go(-2)">Volver.</a><br>
                </div>';
            } else {
                $mensaje = '
                <div class="alert alert-danger">
                    Ocurrió un error al editar el Articulo ID: '.str_pad($id, 4, "0", STR_PAD_LEFT).' ('.$nombre.').  <a href="javascript:window.history.go(-2)">Volver.</a><br>
                    '.$consultaArticulo.'
                </div>';
            }
        }
    } elseif(isset($_GET['id'])){
        $id = $_GET['id'];
        $buscarArticulo = mysqli_query($conexion, "SELECT * FROM articulos WHERE id = '$id'");
        $buscar_codigos = mysqli_query($conexion, "SELECT * FROM codigos_barra WHERE id_articulo = '$id'");
        if(mysqli_num_rows($buscarArticulo) == 1) {
            $mostrarArticulo = mysqli_fetch_assoc($buscarArticulo);              
            $codigos_barra = "";
            while ($codigo = mysqli_fetch_assoc($buscar_codigos))
            {
                $codigos_barra .= "<option value='".$codigo['id']."'>".$codigo['codigo']."</option>";
            }
            $editar = true;
        } else { 
            $mensaje = '
                <div class="alert alert-danger">
                    El numero de Articulo no se encuentra en la Base de Datos. <a href="javascript:window.history.go(-2)">Volver.</a>
                </div>';
            $editar = false;
        }
    } else {
            $mensaje='
                <div class="alert alert-danger"> 
                    No se ha definido un ID para editar. <a href="javascript:window.history.go(-2)">Volver.</a>
                </div>';
            $editar = false;
    }

    $contenido .= '<div class="col-lg-12">'.$mensaje.'</div>';

    if ($editar) { 
        $contenido .= '    
        <form class="row p-3" name="articulos" id="articulos" method="post" action="index.php?menu=articulos&opc=editar" autocomplete="off">
            <div class="col-lg-6">	
                <div class="row mb-3">
                    <label for="id" class="control-label col-lg-4">ID</label>
                    <div class="col-lg-8">
                        <input type="text" id="id" name="id" value="'.$mostrarArticulo['ID'].'" readonly class="form-control rounded-0">
                    </div>
                </div>
                <div class="row mb-3">
                    <label for="barra" class="control-label col-lg-4">Codigo</label>
                    <div class="col-lg-7">
                        <select id="barra" name="barra" readonly class="form-select rounded-0" multiple size="1">
                            '.$codigos_barra.'
                        </select>
                    </div>
                    <div class="col-lg-1">
                        <button type="button" class="btn btn-success rounded-0" data-bs-toggle="modal" data-bs-target="#add-code">
                            <i class="fa fa-plus"></i>
                        </button>
                    </div>
                </div>
                <div class="row mb-3">
                    <label for="codigo" class="control-label col-lg-4">Codigo Interno</label>
                    <div class="col-lg-8">
                        <input type="text" id="codigo" name="codigo" value="'.$mostrarArticulo['codigo'].'" class="form-control rounded-0" tabindex="1">
                    </div>
                </div>
                <div class="row mb-3">
                    <label for="nombre" class="control-label col-lg-4">Nombre</label>
                    <div class="col-lg-8">
                        <input class="form-control rounded-0" type="text" value="'.$mostrarArticulo['nombre'].'" id="nombre" name="nombre" autofocus tabindex="2">
                    </div>
                </div>
                <div class="row mb-3">
                    <label for="nombreOferta" class="control-label col-lg-4">Nombre Oferta</label>
                    <div class="col-lg-8">
                        <input class="form-control rounded-0" type="text" value="'.$mostrarArticulo['nombreOferta'].'" id="nombreOferta" name="nombreOferta" tabindex="3">
                    </div>
                </div>
                <hr>
                <div class="row mb-3">
                    <label for="lista" class="control-label col-lg-4">Precio Costo (Lista)</label>
                    <div class="col-lg-8">
                        <div class="input-group">
                            <input id="lista" name="lista" class="form-control rounded-0" type="text" value="'.$mostrarArticulo['lista'].'" tabindex="4">
                            <span class="input-group-text rounded-0"><span style="font-weight:bold;">$</span></span>
                        </div>
                    </div>
                </div>
                <div class="row mb-3">
                    <label for="dtoGral" class="control-label col-lg-4">Descuento</label>
                    <div class="col-lg-8">
                        <div class="input-group">
                            <input id="dtoGral" name="dtoGral" class="form-control rounded-0" type="text" value="'.$mostrarArticulo['dtoGral'].'" tabindex="5">
                            <span class="input-group-text rounded-0"><span style="font-weight:bold;">%</span></span>
                        </div>
                    </div>
                </div>
                <div class="row mb-3">
                    <label for="costo" class="control-label col-lg-4">Precio Costo (Nominal)</label>
                    <div class="col-lg-8">
                        <div class="input-group">
                            <input id="costo" name="costo" class="form-control rounded-0" type="text" value="'.$mostrarArticulo['costo'].'" tabindex="6">
                            <span class="input-group-text rounded-0"><span style="font-weight:bold;">$</span></span>
                        </div>
                    </div>
                </div>
                <div class="row mb-3">
                    <label for="costoOferta" class="control-label col-lg-4">Precio Costo (Oferta)</label>
                    <div class="col-lg-8">
                        <div class="input-group">
                            <input id="costoOferta" name="costoOferta" class="form-control rounded-0" type="text" value="'.$mostrarArticulo['costoOferta'].'" tabindex="7">
                            <span class="input-group-text rounded-0"><span style="font-weight:bold;">$</span></span>
                        </div>
                    </div>
                </div>
                <hr>
                <div class="row mb-3">
                    <label for="precio" class="control-label col-lg-4">Precio Venta (Lista)</label>
                    <div class="col-lg-8">
                        <div class="input-group">
                            <input id="precio" name="precio" class="form-control rounded-0" type="text" value="'.$mostrarArticulo['precio'].'" tabindex="8">
                            <span class="input-group-text rounded-0"><span style="font-weight:bold;">$</span></span>
                        </div>
                    </div>
                </div>
                <div class="row mb-3">
                    <label for="precioOferta" class="control-label col-lg-4">Precio Venta (Oferta)</label>
                    <div class="col-lg-8">
                        <div class="input-group">
                            <input id="precioOferta" name="precioOferta" class="form-control rounded-0" type="text" value="'.$mostrarArticulo['precioOferta'].'" tabindex="9">
                            <span class="input-group-text rounded-0"><span style="font-weight:bold;">$</span></span>
                        </div>
                    </div>
                </div>
                <div class="row mb-3">
                    <label for="precioCant" class="control-label col-lg-4">Precio Venta (Cant)</label>
                    <div class="col-lg-8">
                        <div class="input-group">
                            <input id="precioCant" name="precioCant" class="form-control rounded-0" type="text" value="'.$mostrarArticulo['precioCant'].'" tabindex="10">
                            <span class="input-group-text rounded-0"><span style="font-weight:bold;">$</span></span>
                        </div>
                    </div>
                </div>
                <div class="row mb-3">
                    <label for="precioEmp" class="control-label col-lg-4">Precio Venta (Empl)</label>
                    <div class="col-lg-8">
                        <div class="input-group">
                            <input id="precioEmp" name="precioEmp" class="form-control rounded-0" type="text" value="'.$mostrarArticulo['precioEmp'].'" tabindex="11">
                            <span class="input-group-text rounded-0"><span style="font-weight:bold;">$</span></span>
                        </div>
                    </div>
                </div>
                <hr>
                <div class="row mb-3">
                    <label for="existencia" class="control-label col-lg-4">Existencias</label>
                    <div class="col-lg-8">
                        <div class="input-group">
                            <input id="existencia" name="existencia" class="form-control rounded-0" type="text" value="'.$mostrarArticulo['existencia'].'" tabindex="12">
                            <span class="input-group-text rounded-0"><span style="font-weight:bold;">$</span></span>
                        </div>
                    </div>
                </div>
                <div class="row mb-3">
                    <label for="existenciaMinima" class="control-label col-lg-4">Existencia Minima</label>
                    <div class="col-lg-8">
                        <div class="input-group">
                            <input id="existenciaMinima" name="existenciaMinima" class="form-control rounded-0" type="text" value="'.$mostrarArticulo['existenciaMinima'].'" tabindex="13">
                            <span class="input-group-text rounded-0"><span style="font-weight:bold;">$</span></span>
                        </div>
                    </div>
                </div>
                <div class="row mb-3">
                    <label for="reposicion" class="control-label col-lg-4">Reposicion</label>
                    <div class="col-lg-8">
                        <div class="input-group">
                            <input id="reposicion" name="reposicion" class="form-control rounded-0" type="text" value="'.$mostrarArticulo['reposicion'].'" tabindex="14">
                            <span class="input-group-text rounded-0"><span style="font-weight:bold;">$</span></span>
                        </div>
                    </div>
                </div>
            </div>
            <div class="col-lg-6">                
                <div class="row mb-3">
                    <label class="control-label col-lg-4">Área</label>
                    <div class="col-lg-8">
                        <select name="id_area" id="id_area" class="form-select rounded-0" tabindex="15">';
                            $obtenerArea = mysqli_query($conexion, "SELECT id, nombre FROM areas ORDER BY nombre");
                            while($area = mysqli_fetch_assoc($obtenerArea)) {
                                if($mostrarArticulo['id_area'] == $area['id']) {
                                    $contenido .= '<option selected value="'.$area['id'].'">'.$area['nombre'].'</option>';
                                } else {
                                    $contenido .= '<option value="'.$area['id'].'">'.$area['nombre'].'</option>';
                                }	
                            }
                $contenido.='
                        </select>
                    </div>
                </div>
                <div class="row mb-3">
                    <label class="control-label col-lg-4">Proveedor</label>
                    <div class="col-lg-8">
                        <select name="id_proveedor" id="id_proveedor" class="form-select rounded-0" tabindex="16">';
                            $obtenerProveedor = mysqli_query($conexion, "SELECT id, nombre FROM proveedores ORDER BY nombre");
                            while($mostrarProveedor = mysqli_fetch_assoc($obtenerProveedor)) {
                                if($mostrarArticulo['id_proveedor'] == $mostrarProveedor['id']) {
                                    $contenido .= '<option selected value="'.$mostrarProveedor['id'].'">'.$mostrarProveedor['nombre'].'</option>';
                                } else {
                                    $contenido .= '<option value="'.$mostrarProveedor['id'].'">'.$mostrarProveedor['nombre'].'</option>';
                                }	
                            }
                $contenido.='
                        </select>
                    </div>
                </div>
                <div class="row mb-3">
                    <label class="control-label col-lg-4">Marca</label>
                    <div class="col-lg-8">
                        <select name="id_marca" id="id_marca" class="form-select rounded-0" tabindex="17">';
                            $obtenerMarca = mysqli_query($conexion, "SELECT id, nombre FROM marcas ORDER BY nombre");
                            while($mostrarMarca=mysqli_fetch_assoc($obtenerMarca)) {
                                if($mostrarArticulo['id_marca'] == $mostrarMarca['id']) {
                                    $contenido .= '<option selected value="'.$mostrarMarca['id'].'">'.$mostrarMarca['nombre'].'</option>';
                                } else {
                                    $contenido .= '<option value="'.$mostrarMarca['id'].'">'.$mostrarMarca['nombre'].'</option>';
                                }	
                            }
                $contenido.='
                        </select>
                    </div>
                </div>
                <div class="row mb-3">
                    <label class="control-label col-lg-4">Grupo</label>
                    <div class="col-lg-8">
                        <select name="id_grupo" id="id_grupo" class="form-select rounded-0" tabindex="18">';
                            $obtenerGrupo = mysqli_query($conexion, "SELECT id, nombre FROM grupos ORDER BY nombre");
                            while($mostrarGrupo = mysqli_fetch_assoc($obtenerGrupo)) {
                                if($mostrarArticulo['id_grupo'] == $mostrarGrupo['id']) {
                                    $contenido .= '<option selected value="'.$mostrarGrupo['id'].'">'.$mostrarGrupo['nombre'].'</option>';
                                } else {
                                    $contenido .= '<option value="'.$mostrarGrupo['id'].'">'.$mostrarGrupo['nombre'].'</option>';
                                }	
                            }
                $contenido.='
                        </select>
                    </div>
                </div>
                <hr>
                <div class="row mb-3">
                    <label class="control-label col-lg-4">Oferta por Dias</label>
                        <div class="col-lg-8">
                            <input class="form-check-input" type="checkbox" id="OfertaDia" name="OfertaDia"  '.($mostrarArticulo['OfertaDia'] ? "checked" : "").' tabindex="19">
                        </div>
                    <label>
                </div>
                <div class="row mb-3">
                    <label class="control-label col-lg-4">Fecha Oferta</label>
                    <div class="col-lg-4">
                        <input name="OfertaDiaDesde" id="OfertaDiaDesde" class="form-control rounded-0" type="date" value="'.$mostrarArticulo['OfertaDiaDesde'].'" tabindex="20">
                    </div>
                    <div class="col-lg-4">
                    <input name="OfertaDiaHasta" id="OfertaDiaHasta" class="form-control rounded-0" type="date" value="'.$mostrarArticulo['OfertaDiaHasta'].'" tabindex="21">
                </div>
                </div>
                <div class="row mb-3">
                    <label class="control-label col-lg-4">Oferta por Horas</label>
                        <div class="col-lg-8">
                            <input class="form-check-input" type="checkbox" id="OfertaHora" name="OfertaHora"  '.($mostrarArticulo['OfertaHora'] ? "checked" : "").' tabindex="22">
                        </div>
                    <label>
                </div>
                <div class="row mb-3">
                    <label class="control-label col-lg-4">Horario Oferta</label>
                    <div class="col-lg-4">
                        <input name="OfertaHoraDesde" id="OfertaHoraDesde" class="form-control rounded-0" type="time" value="'.$mostrarArticulo['OfertaHoraDesde'].'" tabindex="23">
                    </div>
                    <div class="col-lg-4">
                    <input name="OfertaHoraHasta" id="OfertaHoraHasta" class="form-control rounded-0" type="time" value="'.$mostrarArticulo['OfertaHoraHasta'].'" tabindex="24">
                </div>
                </div>
                <div class="row mb-3">
                    <label class="control-label col-lg-4">Oferta por Cantidad</label>
                        <div class="col-lg-8">
                            <input class="form-check-input" type="checkbox" id="OfertaCant" name="OfertaCant" '.($mostrarArticulo['OfertaCant'] ? "checked" : "").' tabindex="25">
                        </div>
                    <label>
                </div>
                <div class="row mb-3">
                    <label class="control-label col-lg-4">Cantidad</label>
                    <div class="col-lg-8">
                        <input name="OfertaCantN" id="OfertaCantN" class="form-control rounded-0" value="'.$mostrarArticulo['OfertaCantN'].'" type="text" tabindex="26">
                    </div>
                </div>
                <hr>
                <div class="row mb-3">
                    <label class="control-label col-lg-4">Unidades por Bulto</label>
                    <div class="col-lg-8">
                        <input name="uBulto" id="uBulto" class="form-control rounded-0" value="'.$mostrarArticulo['uBulto'].'" type="text" tabindex="27">
                    </div>
                </div>
                <hr>
                <div class="row mb-3">
                    <label class="control-label col-lg-4">Producto/Servicio</label>
                    <div class="col-lg-8">
                        <input class="form-check-input" type="checkbox" id="producto" name="producto" '.($mostrarArticulo['producto'] ? "checked" : "").' tabindex="28">
                    </div>
                <label>
            </div>
            <div class="row mb-3">
                <label class="control-label col-lg-4">Activo</label>
                <div class="col-lg-8">
                    <input class="form-check-input" type="checkbox" id="activo" name="activo" '.($mostrarArticulo['activo'] ? "checked" : "").' tabindex="29">
                </div>
                <label>
            </div>
            <br>
            <br>
            <div class="row mb-3">
                <label class="control-label col-lg-4"></label>
                <div class="col-lg-4">
                    <input name="deshacer" id="deshacer" type="reset" class="form-control rounded-0 btn btn-default" value="Deshacer">
                </div>
                <div class="col-lg-4">
                    <input name="accion" id="accion" type="submit" class="form-control rounded-0 btn btn-success" value="Editar Articulo" tabindex="30">
                </div>
            </div>
        </div>
        </form>';	
    }