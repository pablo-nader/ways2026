<?php
    if (isset($_POST['id'])) {
        if(@$_POST['crear'] == 'crear') {
            $nombre = ucwords($_POST['nombre']);
            $proveedor = $_POST['proveedor'];
            $grupo = $_POST['grupo'];

            if ($crearMarca = mysqli_query($conexion, "INSERT INTO marcas (nombre, proveedor, grupo) VALUES ('$nombre', '$proveedor', '$grupo')")) {
                $id = mysqli_insert_id($conexion);
                $mensaje = '
                <div class="alert alert-success rounded-0">
                    La Marca '.$nombre.' (ID: '.str_pad($id, 4, "0", STR_PAD_LEFT).') ha sido creado correctamente.
                </div>';
            }else {
                $mensaje = '
                <div class="alert alert-danger rounded-0">
                    Ocurrió un error al crear la Marca.
                </div>';
            }
        } elseif (@$_POST['accion'] == 'Editar Marca') {
            $id = $_POST['id'];
            $nombre = ucwords($_POST['nombre']);
            $proveedor = $_POST['proveedor'];
            $grupo = $_POST['grupo'];
            if ($editarMarca = mysqli_query($conexion, "UPDATE  marcas 
                                                        SET     nombre = '$nombre', 
                                                                proveedor = '$proveedor', 
                                                                grupo = '$grupo' 
                                                        WHERE   id = '$id'")) {
                $mensaje = '
                <div class="alert alert-success rounded-0">
                    La Marca ID: '.str_pad($id, 4, "0", STR_PAD_LEFT).' ('.$nombre.') ha sido editado correctamente.
                </div>';
            } else {
                $mensaje='
                <div class="alert alert-danger rounded-0">
                    Ocurrió un error al editar la Marca ID: '.str_pad($id, 4, "0", STR_PAD_LEFT).' ('.$nombre.').
                </div>';
            }
        }
    }
    if(isset($_GET['id'])) {
        $id = $_GET['id'];
        $buscarMarca = mysqli_query($conexion, "SELECT * FROM marcas WHERE id = '$id'");
        if (mysqli_num_rows($buscarMarca) == 1) {
            if(@$_POST['accion'] == 'Editar Marca') {
                $id = $_POST['id'];
                $nombre = $_POST['nombre'];
                $proveedor = $_POST['proveedor'];
                $grupo = $_POST['grupo'];
                if ($editarMarca = mysqli_query($conexion, "UPDATE  marcas 
                                                            SET     nombre = '$nombre', 
                                                                    proveedor = '$proveedor', 
                                                                    grupo = '$grupo' 
                                                            WHERE   id = '$id'")) {
                    $mensaje = '
                    <div class="alert alert-success rounded-0">
                        La Marca ID: '.str_pad($id, 4, "0", STR_PAD_LEFT).' ('.$nombre.') ha sido editada correctamente.
                    </div>';
                } else {
                    $mensaje = '
                    <div class="alert alert-danger rounded-0">
                        Ocurrió un error al editar la Marca ID: '.str_pad($id,4,"0",STR_PAD_LEFT).' ('.$nombre.').
                    </div>';
                }
            }
            $mostrarMarca = mysqli_fetch_assoc($buscarMarca);
            $boton = '
                <div class="col-lg-12">
                    <input name="accion" id="accion" type="submit" class="form-control rounded-0 btn btn-success" value="Editar Marca">
                </div>';
            $editar = true;
            $mensaje = '
                <div class="alert alert-warning rounded-0">
                    Estás a punto de editar la Marca '.$mostrarMarca['nombre'].' ('.str_pad($mostrarMarca['id'],4,"0",STR_PAD_LEFT).').
                </div>';
        }
    }
    if (!$editar) { 
        $boton = '
            <div class="col-lg-12">
                <input name="crear" id="crear" type="hidden" class="form-control rounded-0" value="crear">
                <input name="accion" id="accion" type="submit" class="form-control rounded-0 btn btn-success" value="Crear Marca">
            </div>';
    }

    $contenido .= '
    <div class="col-lg-6">
        '.$mensaje.'
    
        <form class="row" method="post" action="" autocomplete="off">
            <label for="id" class="control-label col-lg-4">ID</label>
            <div class="col-lg-8">
                <input type="text" id="id" name="id" value="'.($mostrarMarca['id'] ?? "").'" readonly class="form-control mb-3 rounded-0">
            </div>

            <label for="nombre" class="control-label col-lg-4">Nombre</label>
            <div class="col-lg-8">
                <input class="form-control mb-3 rounded-0" type="text" value="'.($mostrarMarca['nombre'] ?? "").'" id="nombre" name="nombre" autofocus required>
            </div>

            <label class="control-label col-lg-4">Proveedor</label>
            <div class="col-lg-8">
                <select name="proveedor" id="proveedor" class="form-select rounded-0 mb-3" required>';
                    $obtenerProveedor=mysqli_query($conexion,"SELECT id, nombre FROM proveedores ORDER BY nombre");
                    while ($mostrarProveedor = mysqli_fetch_assoc($obtenerProveedor)) {
                        if(@$mostrarMarca['proveedor'] == $mostrarProveedor['id']) {
                            $contenido .= '<option selected value="'.$mostrarProveedor['id'].'">'.$mostrarProveedor['nombre'].'</option>';
                        } else {
                            $contenido .= '<option value="'.$mostrarProveedor['id'].'">'.$mostrarProveedor['nombre'].'</option>';
                        }	
                    }
            $contenido.='
                </select>
            </div>

            <label class="control-label col-lg-4">Grupo</label>
            <div class="col-lg-8">
                <select name="grupo" id="grupo" class="form-select rounded-0 mb-3" required>';
                    $obtenerGrupo = mysqli_query($conexion,"SELECT id, nombre FROM grupos ORDER BY nombre");
                    while ($mostrarGrupo = mysqli_fetch_assoc($obtenerGrupo)) {
                        if (@$mostrarMarca['grupo'] == $mostrarGrupo['id']) {
                            $contenido .= '<option selected value="'.$mostrarGrupo['id'].'">'.$mostrarGrupo['nombre'].'</option>';
                        } else {
                            $contenido .= '<option value="'.$mostrarGrupo['id'].'">'.$mostrarGrupo['nombre'].'</option>';
                        }	
                    }
        $contenido .= '
                </select>
            </div>
            <div class="form-group">
                '.$boton.'
            </div>
        </form>
    </div>
    <div class="col-lg-6">
        <table class="table table-striped responsive-table table-hover table-bordered">
            <thead>
                <tr>
                    <th>ID</th>
                    <th>Nombre</th>
                    <th>Proveedor</th>
                    <th>Grupo</th>
                </tr>
            </thead>
            <tbody>';
    $buscarMarcas = mysqli_query($conexion, "SELECT m.id, 
                                                    m.Nombre AS nombre,
                                                    p.nombre AS proveedor,
                                                    g.nombre AS grupo
                                             FROM   marcas m 
                                                JOIN proveedores p ON p.id = m.proveedor
                                                JOIN grupos g ON g.id = m.grupo
                                             ORDER BY id DESC");
    while ($mostrarMarcas=mysqli_fetch_assoc($buscarMarcas)) {
        $contenido.= '
                <tr>
                    <td><a href="index.php?menu=articulos&opc=marcas&id='.$mostrarMarcas['id'].'">'.str_pad($mostrarMarcas['id'],4,"0",STR_PAD_LEFT).'</a></td>
                    <td>'.$mostrarMarcas['nombre'].'</td>
                    <td>'.$mostrarMarcas['proveedor'].'</td>
                    <td>'.$mostrarMarcas['grupo'].'</td>
                </tr>';
    }
    $contenido.='
            </tbody>
        </table>
    </div>';