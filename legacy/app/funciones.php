<?php
// FUNCION PARA COMPROBAR OFERTAS
function comprobarOferta($articulo,$cantidad,$conexion) {
	$busqueda="SELECT * FROM articulos a JOIN codigos_barra cb ON a.ID = cb.id_articulo WHERE cb.codigo='$articulo'";
	$busqueda=mysqli_query($conexion,$busqueda);
	$oferta=mysqli_fetch_assoc($busqueda);
	$of=FALSE;
	$break=FALSE;
	$id_area=@$oferta['id_area'];
	if(@$oferta['OfertaHora']==1) {
		$horaActual=date("H:i:s");
		if (($horaActual<$oferta['OfertaHoraDesde']) || ($horaActual>$oferta['OfertaHoraHasta'])) {
			$of=FALSE; $break=TRUE;
		}
	}
	if(@$oferta['OfertaDia']==1) {
		if($break==FALSE) {
			$fechaActual=date("Y-m-d");
			if (($fechaActual<$oferta['OfertaDiaDesde']) || ($fechaActual>$oferta['OfertaDiaHasta'])) {
				$of=FALSE; 
			}
			else {
				$descuento=(($oferta['precio']-$oferta['precioOferta'])*$cantidad)*(-1);
				$descuento=number_format($descuento,2,'.','');
				if((!isset($oferta['nombreOferta'])) || ($oferta['nombreOferta']=='')) {
					$descuentoDetalle='DESCUENTO POR CANTIDAD';
				}
				else {
					$descuentoDetalle=$oferta['nombreOferta'];
				}
				$of=TRUE;
			}
		}
	}
	if(@$oferta['OfertaCant']==1) {
		if($break==FALSE) {
			if($oferta['OfertaCantN']<=$cantidad) {
				$unidadDescuento=($oferta['precio']-$oferta['precioCant'])*$oferta['OfertaCantN'];
				$multiplicante=$cantidad/$oferta['OfertaCantN'];
				$multiplicante=explode('.',$multiplicante);
				$descuento=($unidadDescuento*$multiplicante[0])*(-1);
				$descuento=number_format($descuento,2,'.','');
				$descuento=explode('.',$descuento);
				if($descuento[1]!=50) { $descuento[1]='00'; }
				$descuento=$descuento[0].'.'.$descuento[1];
				if((!isset($oferta['nombreOferta'])) || ($oferta['nombreOferta']=='')) {
					$descuentoDetalle='DESCUENTO POR CANTIDAD';
				}
				else {
					$descuentoDetalle=$oferta['nombreOferta'];
				}
				$of=TRUE;
			}
			$cantidad2=$cantidad*(-1);
			if($oferta['OfertaCantN']<=$cantidad2) {
				$unidadDescuento=($oferta['precio']-$oferta['precioCant'])*$oferta['OfertaCantN'];
				$multiplicante=$cantidad2/$oferta['OfertaCantN'];
				$multiplicante=explode('.',$multiplicante);
				$descuento=($unidadDescuento*$multiplicante[0]);
				$descuento=number_format($descuento,2,'.','');
				$descuento=explode('.',$descuento);
				if($descuento[1]!=50) { $descuento[1]='00'; }
				$descuento=$descuento[0].'.'.$descuento[1];
				if((!isset($oferta['nombreOferta'])) || ($oferta['nombreOferta']=='')) {
					$descuentoDetalle='DESCUENTO POR CANTIDAD';
				}
				else {
					$descuentoDetalle=$oferta['nombreOferta'];
				}
				$of=TRUE;
			}
		}
	}
	
	if ($of==TRUE) {
		$barra='OF'.$articulo;
		$contenidoOf = array("mostrarID" => 'OF'.$oferta['ID'], "barra" => $barra, "cantidad" => '-', "descripcion" => $descuentoDetalle, "precio" => '-', "total" => $descuento, "id_area" => $id_area);  
		
		if(isset($_SESSION['ticket'][$barra])) {
			@$_SESSION['descuento'] = $_SESSION['descuento']-$_SESSION['ticket'][$barra]['total'];
		}
		$_SESSION['ticket'][$barra] = $contenidoOf;  		
		@$_SESSION['descuento'] = $_SESSION['descuento']+$_SESSION['ticket'][$barra]['total'];
	}
}
// FUNCION PARA COMPROBAR OFERTAS DE GRUPO
function comprobarOfertaGrupo($grupo,$cantidad,$conexion) {
	$busqueda="SELECT * FROM grupos WHERE id='$grupo'";
	$busqueda=mysqli_query($conexion,$busqueda);
	$oferta=mysqli_fetch_assoc($busqueda);
	$of=TRUE;
	//Comprobamos que si la oferta tiene restriccion horaria y si esta dentro del rango
	if(@$oferta['horas']==1) {
		$horaActual=date("H:i:s");
		if (($horaActual<$oferta['hDesde']) || ($horaActual>$oferta['hHasta'])) {
			$of=FALSE;
		}
	}
	//Comprobamos que si la oferta tiene restriccion de dias y si esta dentro del rango
	if(@$oferta['dias']==1) {
		$fechaActual=date("Y-m-d");
		if (($fechaActual<$oferta['dDesde']) || ($fechaActual>$oferta['dHasta'])) {
			$of=FALSE; 
		}
	}
	//Si no hay restricciones o se encuentra dentro del rango seguimos
	if($of==TRUE) {
		//Si la oferta es por cantidad, comprobamos que sea superior y cuantas 
		if(@$oferta['ofertaCantidad']==1) {
			if($cantidad['cantidad']>=$oferta['cantidad']){
				$cantOfertas=$cantidad['cantidad']/$oferta['cantidad'];
				$multiplicante=explode('.',$cantOfertas);
				$unitario=$cantidad['importe']/$cantidad['cantidad'];
				//$unitario=ceil($unitario);
				$descuento=(($unitario*$oferta['cantidad']) - $oferta['precio']) * ($multiplicante[0]) * (-1);
				$descuento=ceil(number_format($descuento,2,'.',''));
				if((!isset($oferta['descripcion'])) || ($oferta['descripcion']=='')) {
					$descuentoDetalle='DESCUENTO POR CANTIDAD';
				}
				else {
					$descuentoDetalle=$oferta['descripcion'];
				}
				$barra='OF'.$grupo;
				$contenidoOf = array("mostrarID" => 'OF'.$grupo, "barra" => $barra, "cantidad" => '-', "descripcion" => $descuentoDetalle, "precio" => '-', "total" => $descuento, "id_area" => '1');  
				
				if(isset($_SESSION['ticket'][$barra])) {
					@$_SESSION['descuento'] = $_SESSION['descuento']-$_SESSION['ticket'][$barra]['total'];
				}
				$_SESSION['ticket'][$barra] = $contenidoOf;  		
				@$_SESSION['descuento'] = $_SESSION['descuento']+$_SESSION['ticket'][$barra]['total'];
			}
		}
		elseif(@$oferta['ofertaDirecta']==1) {
			$descuento=ceil((($cantidad['importe']*$oferta['descuento'])/100)*(-1));
			$descuento=number_format($descuento,2,".","");
			if((!isset($oferta['descripcion'])) || ($oferta['descripcion']=='')) {
				$descuentoDetalle='DESCUENTO DIRECTO';
			}
			else {
				$descuentoDetalle=$oferta['descripcion'];
			}
			$barra='OF'.$grupo;
			$contenidoOf = array("mostrarID" => 'OF'.$grupo, "barra" => $barra, "cantidad" => '-', "descripcion" => $descuentoDetalle, "precio" => '-', "total" => $descuento, "id_area" => '1');  
			
			if(isset($_SESSION['ticket'][$barra])) {
				@$_SESSION['descuento'] = $_SESSION['descuento']-$_SESSION['ticket'][$barra]['total'];
			}
			$_SESSION['ticket'][$barra] = $contenidoOf;  		
			@$_SESSION['descuento'] = $_SESSION['descuento']+$_SESSION['ticket'][$barra]['total'];
		}
	}
}
