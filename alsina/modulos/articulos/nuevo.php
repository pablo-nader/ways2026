<?php
    // Crear Articulo
    if(isset($_POST['crear']) && $_POST['crear'] == 'crear') {
        //Datos del articulo
        $barra = $_POST['barra'];
        $nombre = ucwords($_POST['nombre']);

        $query = "  SELECT a.ID 
                    FROM articulos a
                        JOIN codigos_barra cb ON a.ID = cb.id_articulo
                    WHERE cb.codigo = '$barra'";
        $checkArticulo = mysqli_query($conexion, $query);
        $consultaArticulo = "INSERT INTO articulos (barra, nombre) VALUES ('$barra', '$nombre')";

        if (!is_numeric($barra)) {
            $mensaje = '
            <div class="alert alert-danger">
               El código de barras DEBE ser numérico.
            </div>';
        } elseif (mysqli_num_rows($checkArticulo) != 0) {
            $id = mysqli_fetch_assoc($checkArticulo)['ID'];
            echo '<script>window.location = "index.php?menu=articulos&opc=editar&id='.$id.'"</script>';
        } elseif ($crearArticulo = mysqli_query($conexion, $consultaArticulo)) {
            $id = mysqli_insert_id($conexion);

            echo '<script>window.location = "index.php?menu=articulos&opc=editar&id='.$id.'"</script>';
        } else {
            $mensaje = '
            <div class="alert alert-danger">
                Ocurrió un error al crear el Articulo.<br>
                '.$consultaArticulo.'
            </div>';
        }
    }

    $contenido .= '
    <div class="col-lg-12">'.$mensaje.'</div>
    <form class="row p-3" name="articulos" method="post" action="" autocomplete="off">
        <div class="col-lg-3"></div>
        <div class="col-lg-6">	
            <div class="row">
                <label for="barra" class="control-label col-lg-4 mb-3">Codigo</label>
                <div class="col-lg-8 mb-3">
                    <input type="text" id="barra" name="barra" class="form-control rounded-0" autofocus required>
                </div>
            </div>
            <div class="row">
                <label for="nombre" class="control-label col-lg-4 mb-3">Nombre</label>
                <div class="col-lg-8 mb-3">
                    <input class="form-control rounded-0" type="text" id="nombre" name="nombre" required>
                </div>
            </div>
            <div class="form-group">
                <div class="col-lg-12">
                    <input name="crear" id="crear" type="hidden" class="form-control" value="crear">
                    <input name="accion" id="accion" type="submit" class="form-control btn btn-success rounded-0" value="Crear Articulo">
                </div>
            </div>
        </div>
    </form>';	