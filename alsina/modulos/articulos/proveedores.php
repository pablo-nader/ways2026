<?php
    if (isset($_POST['id'])) {
        if(@$_POST['crear'] == 'crear') {
            $nombre = $_POST['nombre'];
            $razonSocial = $_POST['razonSocial'];
            $cuit = $_POST['cuit'];

            if (mysqli_query($conexion, "INSERT INTO proveedores (nombre, razonSocial, cuit) VALUES ('$nombre', '$razonSocial', '$cuit')")) {
                $id = mysqli_insert_id($conexion);
                $mensaje = '
                <div class="alert alert-success rounded-0">
                    El Proveedor '.$nombre.' (ID: '.str_pad($id, 4, "0", STR_PAD_LEFT).') ha sido creado correctamente.
                </div>';
            } else {
                $mensaje = '
                <div class="alert alert-danger rounded-0">
                    Ocurrió un error al crear el Proveedor.
                </div>';
            }
        } elseif (@$_POST['accion'] == 'Editar Proveedor') {
            $id = $_POST['id'];
            $nombre = $_POST['nombre'];
            $razonSocial = $_POST['razonSocial'];
            $cuit = $_POST['cuit'];
            if ($editarProveedor = mysqli_query($conexion, "UPDATE  proveedores 
                                                            SET     nombre = '$nombre', 
                                                                    razonSocial = '$razonSocial', 
                                                                    cuit = '$cuit' 
                                                            WHERE   id = '$id'")) {
                $mensaje = '
                <div class="alert alert-success rounded-0">
                    El Proveedor ID: '.str_pad($id, 4, "0", STR_PAD_LEFT).' ('.$nombre.') ha sido editado correctamente.
                </div>';
            } else {
                $mensaje='
                <div class="alert alert-danger rounded-0">
                    Ocurrió un error al editar el Proveedor ID: '.str_pad($id, 4, "0", STR_PAD_LEFT).' ('.$nombre.').
                </div>';
            }
        }
    }
    if(isset($_GET['id'])) {
        $id = $_GET['id'];
        $buscarMarca = mysqli_query($conexion, "SELECT id, nombre, razonSocial, cuit FROM proveedores WHERE id = '$id'");
        if (mysqli_num_rows($buscarMarca) == 1) {
            if(@$_POST['accion'] == 'Editar Proveedor') {
                $id = $_POST['id'];
                $nombre = $_POST['nombre'];
                $razonSocial = $_POST['razonSocial'];
                $cuit = $_POST['cuit'];
                if ($editarProveedor = mysqli_query($conexion, "UPDATE  proveedores 
                                                                SET     nombre = '$nombre', 
                                                                        razonSocial = '$razonSocial', 
                                                                        cuit = '$cuit' 
                                                                WHERE   id = '$id'")) {
                    $mensaje = '
                    <div class="alert alert-success rounded-0">
                        El Proveedor ID: '.str_pad($id, 4, "0", STR_PAD_LEFT).' ('.$nombre.') ha sido editada correctamente.
                    </div>';
                } else {
                    $mensaje = '
                    <div class="alert alert-danger rounded-0">
                        Ocurrió un error al editar el Proveedor ID: '.str_pad($id,4,"0",STR_PAD_LEFT).' ('.$nombre.').
                    </div>';
                }
            }
            $mostrarProveedor = mysqli_fetch_assoc($buscarMarca);
            $boton = '
                <div class="col-lg-12">
                    <input name="accion" id="accion" type="submit" class="form-control rounded-0 btn btn-success" value="Editar Proveedor">
                </div>';
            $editar = true;
            $mensaje = '
                <div class="alert alert-warning rounded-0">
                    Estás a punto de editar el Proveedor '.$mostrarProveedor['nombre'].' ('.str_pad($mostrarProveedor['id'],4,"0",STR_PAD_LEFT).').
                </div>';
        }
    }
    if (!$editar) { 
        $boton = '
            <div class="col-lg-12">
                <input name="crear" id="crear" type="hidden" class="form-control rounded-0" value="crear">
                <input name="accion" id="accion" type="submit" class="form-control rounded-0 btn btn-success" value="Crear Proveedor">
            </div>';
    }

    $contenido .= '
    <div class="col-lg-6">
        '.$mensaje.'
    
        <form class="row" method="post" action="" autocomplete="off">
            <label for="id" class="control-label col-lg-4">ID</label>
            <div class="col-lg-8">
                <input type="text" id="id" name="id" value="'.($mostrarProveedor['id'] ?? "").'" readonly class="form-control mb-3 rounded-0">
            </div>

            <label for="nombre" class="control-label col-lg-4">Nombre</label>
            <div class="col-lg-8">
                <input class="form-control mb-3 rounded-0" type="text" value="'.($mostrarProveedor['nombre'] ?? "").'" id="nombre" name="nombre" autofocus required>
            </div>

            <label class="control-label col-lg-4">Razón Social</label>
            <div class="col-lg-8">
                <input class="form-control mb-3 rounded-0" type="text" value="'.($mostrarProveedor['razonSocial'] ?? "").'" id="razonSocial" name="razonSocial">
            </div>

            <label class="control-label col-lg-4">CUIT</label>
            <div class="col-lg-8">
                <input class="form-control mb-3 rounded-0" type="text" value="'.($mostrarProveedor['cuit'] ?? "").'" id="cuit" name="cuit" maxlength="11">
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
                    <th>Razón Social</th>
                    <th>CUIT</th>
                </tr>
            </thead>
            <tbody>';
    $buscarProveedores = mysqli_query($conexion, "SELECT id, nombre, razonSocial, cuit
                                                  FROM   proveedores
                                                  ORDER BY nombre");
    while ($mostrarProveedores = mysqli_fetch_assoc($buscarProveedores)) {
        $contenido .= '
                <tr>
                    <td><a href="index.php?menu=articulos&opc=proveedores&id='.$mostrarProveedores['id'].'">'.str_pad($mostrarProveedores['id'], 4, "0", STR_PAD_LEFT).'</a></td>
                    <td>'.$mostrarProveedores['nombre'].'</td>
                    <td>'.$mostrarProveedores['razonSocial'].'</td>
                    <td>'.$mostrarProveedores['cuit'].'</td>
                </tr>';
    }
    $contenido .= '
            </tbody>
        </table>
    </div>';