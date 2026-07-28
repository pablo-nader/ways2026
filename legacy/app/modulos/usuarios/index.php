<?php
	$consulta = mysqli_query($conexion,"SELECT * FROM usuarios WHERE id<>'1'");
	$contenido.= '
	<div class="col-lg-12">
		<table id="dataTable" class="table table-bordered table-condensed table-hover table-striped">
			<thead>
				<tr>
					<tr>
					<th>Cliente</th>
					<th>Nombre</th>
					<th>Direccion</th>
					<th>Telefono</th>
					<th>Saldo</th>
					<th>Acuerdo</th>
					<th style="text-align:center;"><i class="fa fa-user"></i></th>
					<th style="text-align:center;"><i class="fa fa-search"></i></th>
				</tr>
				</tr>
			</thead>
			<tbody>';
	while($usuarios = mysqli_fetch_assoc($consulta)) {
		if($usuarios['cel']==0) { 
			if($usuarios['tel']==0) { 
				$telefono='';		
				$iconTel='';					
			}
			else {
				$telefono=$usuarios['tel'];
				$iconTel='<i class="fa fa-home"></i>';
			}
		}
		else {
			$telefono=$usuarios['cel'];
			$iconTel='<i class="fa fa-phone"></i>';
		}
		if($usuarios['tipoUser']==2 || $usuarios['tipoUser']==3 || $usuarios['tipoUser']==4) { 
			$privilegios='<i class="fa fa-user-cog"></i>';
		}
		else {
			$privilegios='';
		}
		$contenido.= '
				<tr>
					<td>'.str_pad($usuarios['id'],4,"0", STR_PAD_LEFT).'</td>
					<td title="'.$usuarios['user'].'"><span style="float:right;">'.$privilegios.'</span>'.$usuarios['nombre'].' '.$usuarios['apellido'].'</td>
					<td>'.$usuarios['domicilio'].'</td>
					<td style="text-align:right;">'.$telefono.'<span style="float:left;">'.$iconTel.'</span></td>
					<td style="text-align:right;">'.number_format($usuarios['saldo'],2,".",",").'<span style="float:left;">$</span></td>
					<td style="text-align:right;">'.number_format($usuarios['acuerdo'],2,".",",").'<span style="float:left;">$</span></td>
					<td style="text-align:center;"><a href="index.php?menu=usuarios&opc=editar&usuario='.$usuarios['id'].'"><i class="fa fa-user-edit"></i></a></td>
					<td style="text-align:center;"><a href="index.php?menu=usuarios&opc=cc&usuario='.$usuarios['id'].'"><i class="fa fa-search"></i></a></td>
				</tr>';
	}
	$contenido.= '	</tbody>
		</table>
	</div>';