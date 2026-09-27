using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Npgsql;
using RedAJP.Globales;
using RedAJP.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RedAJP.Controllers
{
    [Authorize]
    public class CuponesController : GlobalController
    {
        private readonly string _cadenaConexion;
        private Parametros.Modulo Modulo = Parametros.Modulos.Cupones;

        public CuponesController(IConfiguration configuration)
        {
            _cadenaConexion = configuration.GetConnectionString("MiConexion");
        }

        public async Task<IActionResult> Index()
        {
            if (!User.TienePermiso(Modulo, PermisoLeer)) {
                MostrarMensaje("Error", "No tiene permisos de lectura en ésta página", TipoMensaje.Alerta);
                return RedirectToAction("Index", "Home"); 
            }

            var lista = new List<CuponViewModel>();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // AGREGAMOS LA SUB-CONSULTA DEL CONTEO:
                    string sql = @"
                SELECT c.*, 
                       u1.""NombreCompleto"" as ""Creador"",
                       u2.""NombreCompleto"" as ""Editor"",
                       (SELECT COUNT(*) FROM ""Sist_Cupones_Usuarios"" cu WHERE cu.""Id_Cupon"" = c.""Id_Cupon"") as ""TotalUsuarios""
                FROM ""Sist_Cupones"" c
                JOIN ""Sist_Usuarios"" u1 ON c.""Id_Usuario_Creador"" = u1.""Id_Usuario""
                LEFT JOIN ""Sist_Usuarios"" u2 ON c.""Id_Usuario_Edicion"" = u2.""Id_Usuario""
                ORDER BY c.""Activo"" DESC, c.""Fecha_Fin"" DESC";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    using (var r = await cmd.ExecuteReaderAsync())
                    {
                        while (await r.ReadAsync())
                        {
                            lista.Add(new CuponViewModel
                            {
                                Id_Cupon = (int)r["Id_Cupon"],
                                Codigo = r["Codigo"].ToString(),
                                Descripcion = r["Descripcion"]?.ToString(),
                                Tipo_Descuento = (int)r["Tipo_Descuento"],
                                Valor = (decimal)r["Valor"],
                                Limite_Usos = (int)r["Limite_Usos"],
                                Conteo_Usados = (int)r["Conteo_Usados"],
                                Fecha_Inicio = (DateTime)r["Fecha_Inicio"],
                                Fecha_Fin = (DateTime)r["Fecha_Fin"],
                                Activo = (bool)r["Activo"],
                                Aplica_Eventos = (bool)r["Aplica_Eventos"],
                                Aplica_Tienda = (bool)r["Aplica_Tienda"],
                                CreadoPor = r["Creador"].ToString(),
                                EditadoPor = r["Editor"]?.ToString(),

                                ConteoUsuariosRestringidos = Convert.ToInt32(r["TotalUsuarios"])
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
            }

            return View(lista);
        }

        // GET: Cupones/Editor/5
        public async Task<IActionResult> Editor(int id)
        {
            if (!User.TienePermiso(Modulo, PermisoEditar)) {
                MostrarMensaje("Error", "No tiene permisos de edición en ésta página", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            var modelo = new CuponViewModel();
            // Valores por defecto
            modelo.Fecha_Inicio = DateTime.Now;
            modelo.Fecha_Fin = DateTime.Now.AddMonths(1);
            modelo.Activo = true;
            modelo.Aplica_Eventos = true;

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // 1. Cargar Catálogo de Eventos (Para el select de restricción por evento)
                    var eventos = new List<dynamic>();
                    string sqlEv = @"SELECT ""Id_Evento"", ""Titulo"" FROM ""Eventos_Catalogo"" WHERE ""Activo"" = TRUE ORDER BY ""Fecha_Inicio"" DESC";
                    using (var cmdE = new NpgsqlCommand(sqlEv, conexion))
                    using (var r = await cmdE.ExecuteReaderAsync())
                    {
                        while (await r.ReadAsync()) eventos.Add(new { Id = (int)r["Id_Evento"], Titulo = r["Titulo"].ToString() });
                    }
                    ViewBag.ListaEventos = eventos;

                    // 2. Cargar Catálogo de Usuarios (Para el buscador del cupón personal)
                    var usuarios = new List<dynamic>();
                    string sqlUsers = @"SELECT ""Id_Usuario"", ""NombreCompleto"", ""Email"", ""Email_Verificado"" 
                                FROM ""Sist_Usuarios"" WHERE ""Activo"" = TRUE ORDER BY ""NombreCompleto"" ASC";
                    using (var cmdU = new NpgsqlCommand(sqlUsers, conexion))
                    using (var r = await cmdU.ExecuteReaderAsync())
                    {
                        while (await r.ReadAsync())
                        {
                            usuarios.Add(new
                            {
                                Id = (int)r["Id_Usuario"],
                                Nombre = r["NombreCompleto"].ToString(),
                                Email = r["Email"].ToString(),
                                Verificado = r["Email_Verificado"] != DBNull.Value && (bool)r["Email_Verificado"]
                            });
                        }
                    }
                    ViewBag.TodosUsuarios = usuarios;

                    // 3. Cargar Datos del Cupón (Si es edición)
                    if (id > 0)
                    {
                        string sql = @"SELECT * FROM ""Sist_Cupones"" WHERE ""Id_Cupon"" = @id";
                        using (var cmd = new NpgsqlCommand(sql, conexion))
                        {
                            cmd.Parameters.AddWithValue("@id", id);
                            using (var r = await cmd.ExecuteReaderAsync())
                            {
                                if (await r.ReadAsync())
                                {
                                    modelo.Id_Cupon = (int)r["Id_Cupon"];
                                    modelo.Codigo = r["Codigo"].ToString();
                                    modelo.Descripcion = r["Descripcion"]?.ToString();
                                    modelo.Tipo_Descuento = (int)r["Tipo_Descuento"];
                                    modelo.Valor = (decimal)r["Valor"];
                                    modelo.Tope_Maximo_Descuento = r["Tope_Maximo_Descuento"] as decimal?;
                                    modelo.Monto_Minimo_Compra = (decimal)r["Monto_Minimo_Compra"];
                                    modelo.Limite_Usos = (int)r["Limite_Usos"];
                                    modelo.Conteo_Usados = (int)r["Conteo_Usados"];
                                    modelo.Fecha_Inicio = (DateTime)r["Fecha_Inicio"];
                                    modelo.Fecha_Fin = (DateTime)r["Fecha_Fin"];
                                    modelo.Activo = (bool)r["Activo"];
                                    modelo.Aplica_Eventos = (bool)r["Aplica_Eventos"];
                                    modelo.Aplica_Tienda = (bool)r["Aplica_Tienda"];
                                    modelo.Id_Evento_Restringido = r["Id_Evento_Restringido"] as int?;
                                    // NOTA: Ya no leemos Email_Restringido
                                }
                                else return RedirectToAction("Index");
                            }
                        }

                        // 4. [NUEVO] Cargar la lista de Usuarios Autorizados directamente al Modelo
                        string sqlRel = @"SELECT ""Id_Usuario"" FROM ""Sist_Cupones_Usuarios"" WHERE ""Id_Cupon"" = @id";
                        using (var cmdRel = new NpgsqlCommand(sqlRel, conexion))
                        {
                            cmdRel.Parameters.AddWithValue("@id", modelo.Id_Cupon);
                            using (var r = await cmdRel.ExecuteReaderAsync())
                            {
                                while (await r.ReadAsync())
                                {
                                    modelo.IdsUsuariosAutorizados.Add((int)r["Id_Usuario"]);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            return View(modelo);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Guardar(CuponViewModel modelo)
        {
            // 1. VALIDACIÓN DE PERMISOS
            if (!User.TienePermiso(Modulo, modelo.Id_Cupon == 0 ? PermisoCrear : PermisoEditar))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permisos suficientes.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            // 2. VALIDACIÓN DE ALCANCE 
            // Si no aplica en ningún lado, el cupón es basura.
            if (!modelo.Aplica_Tienda && !modelo.Aplica_Eventos)
            {
                MostrarMensaje("Error de Alcance", "El cupón debe estar habilitado para Tienda, Eventos o ambos.", TipoMensaje.Error);
                return View("Editor", modelo);
            }

            // 3. LIMPIEZA Y REGLAS DE LISTA DE USUARIOS
            if (modelo.IdsUsuariosAutorizados != null && modelo.IdsUsuariosAutorizados.Any())
            {
                // A. Eliminar duplicados enviados por error o malicia
                modelo.IdsUsuariosAutorizados = modelo.IdsUsuariosAutorizados.Distinct().ToList();

                // B. REGLA DE ORO: Si hay lista, el límite ES la cantidad de personas.
                // Sobrescribimos cualquier valor que haya mandado el usuario en el input de límite.
                modelo.Limite_Usos = modelo.IdsUsuariosAutorizados.Count;
            }
            else
            {
                // Si NO hay lista, es un cupón público/abierto. Validamos el límite manual.
                if (modelo.Limite_Usos < 1)
                {
                    MostrarMensaje("Error de Inventario", "Debes definir un Límite de Usos mayor a 0.", TipoMensaje.Error);
                    return View("Editor", modelo);
                }
                // Aseguramos que la lista no sea null para evitar excepciones
                modelo.IdsUsuariosAutorizados = new List<int>();
            }

            // 4. VALIDACIONES MATEMÁTICAS
            if (modelo.Tipo_Descuento == 1) // Porcentaje
            {
                if (modelo.Valor <= 0 || modelo.Valor > 100)
                {
                    MostrarMensaje("Valor Inválido", "El porcentaje debe ser mayor a 0 y máximo 100.", TipoMensaje.Error);
                    return View("Editor", modelo);
                }
            }
            else // Monto Fijo
            {
                if (modelo.Valor <= 0)
                {
                    MostrarMensaje("Valor Inválido", "El monto de descuento debe ser mayor a 0.", TipoMensaje.Error);
                    return View("Editor", modelo);
                }
                // Opcional: Validar que el descuento no supere el mínimo de compra (si aplica)
                if (modelo.Monto_Minimo_Compra > 0 && modelo.Valor > modelo.Monto_Minimo_Compra)
                {
                    MostrarMensaje("Advertencia Lógica", "El descuento es mayor que el monto mínimo de compra. Esto podría generar totales negativos o cero.", TipoMensaje.Alerta);
                    // No retornamos, solo avisamos (o bloqueas si prefieres).
                }
            }

            // 5. VALIDACIÓN DE FECHAS
            if (modelo.Fecha_Fin <= modelo.Fecha_Inicio)
            {
                MostrarMensaje("Error en Vigencia", "La fecha final debe ser posterior a la fecha de inicio.", TipoMensaje.Error);
                return View("Editor", modelo);
            }

            // Validaciones Básicas del Modelo
            if (!ModelState.IsValid)
            {
                // DEBUG: Recopilar todos los errores del modelo en un solo texto
                // Esto te dirá exactamente qué campo (Required, Range, Email, etc.) está fallando.
                var listaErrores = string.Join(" | ", ModelState.Values
                                                    .SelectMany(v => v.Errors)
                                                    .Select(e => e.ErrorMessage));

                MostrarMensaje("Datos Inválidos", $"Verifica la información: {listaErrores}", TipoMensaje.Error);
                return View("Editor", modelo);
            }
            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // 6. VALIDACIONES CONTRA BASE DE DATOS (LECTURA ANTES DE TRANSACCIÓN)

                    // A. Duplicidad de Código
                    if (modelo.Activo)
                    {
                        string sqlValidar = @"SELECT COUNT(*) FROM ""Sist_Cupones"" 
                                      WHERE UPPER(""Codigo"") = UPPER(@cod) 
                                      AND ""Activo"" = TRUE 
                                      AND ""Id_Cupon"" != @id"; // Excluirse a sí mismo
                        using (var cmd = new NpgsqlCommand(sqlValidar, conexion))
                        {
                            cmd.Parameters.AddWithValue("@cod", modelo.Codigo.Trim());
                            cmd.Parameters.AddWithValue("@id", modelo.Id_Cupon);
                            if ((long)await cmd.ExecuteScalarAsync() > 0)
                            {
                                MostrarMensaje("Código Duplicado", "Ya existe otro cupón ACTIVO con este código.", TipoMensaje.Error);
                                return View("Editor", modelo);
                            }
                        }
                    }

                    // B. VALIDACIÓN DE USUARIOS VERIFICADOS (SEGURIDAD DE BACKEND)
                    // Evita que alguien inyecte IDs de usuarios "fantasma" o no verificados editando el HTML.
                    if (modelo.IdsUsuariosAutorizados.Any())
                    {
                        string sqlCheckVerif = @"SELECT COUNT(*) FROM ""Sist_Usuarios"" 
                                         WHERE ""Id_Usuario"" = ANY(@ids) 
                                         AND (""Email_Verificado"" IS NULL OR ""Email_Verificado"" = FALSE)";
                        using (var cmd = new NpgsqlCommand(sqlCheckVerif, conexion))
                        {
                            cmd.Parameters.AddWithValue("@ids", modelo.IdsUsuariosAutorizados);
                            long invalidos = (long)await cmd.ExecuteScalarAsync();

                            if (invalidos > 0)
                            {
                                MostrarMensaje("Usuarios No Válidos",
                                    $"Se detectaron {invalidos} usuario(s) en la lista que no tienen correo verificado. La operación ha sido cancelada por seguridad.",
                                    TipoMensaje.Error);
                                return View("Editor", modelo);
                            }
                        }
                    }

                    // C. PROTECCIÓN DE INTEGRIDAD (NO BORRAR USUARIOS QUE YA USARON EL CUPÓN)
                    if (modelo.Id_Cupon > 0 && modelo.IdsUsuariosAutorizados.Any())
                    {
                        // 1. Obtenemos quiénes YA lo usaron históricamente
                        var idsUsados = new List<int>();
                        string sqlUsados = @"SELECT DISTINCT ""Id_Usuario"" FROM ""Sist_Cupones_Uso"" WHERE ""Id_Cupon"" = @id";
                        using (var cmd = new NpgsqlCommand(sqlUsados, conexion))
                        {
                            cmd.Parameters.AddWithValue("@id", modelo.Id_Cupon);
                            using (var r = await cmd.ExecuteReaderAsync())
                                while (await r.ReadAsync()) idsUsados.Add((int)r[0]);
                        }

                        // 2. Verificamos: ¿Hay alguien que lo usó y NO está en la nueva lista propuesta?
                        // (Es decir, ¿estamos intentando quitarle el permiso retroactivamente a alguien que ya lo gastó?)
                        var conflictos = idsUsados.Except(modelo.IdsUsuariosAutorizados).ToList();

                        if (conflictos.Any())
                        {
                            MostrarMensaje("Conflicto de Integridad",
                                $"No puedes eliminar de la lista a usuarios que YA utilizaron este cupón. {conflictos.Count} usuario(s) en conflicto.",
                                TipoMensaje.Error);
                            return View("Editor", modelo);
                        }
                    }

                    // 7. TRANSACCIÓN DE ESCRITURA
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);
                            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";

                            // I. GUARDAR CUPÓN (INSERT/UPDATE)
                            if (modelo.Id_Cupon == 0)
                            {
                                string sqlInsert = @"INSERT INTO ""Sist_Cupones"" 
                        (""Codigo"", ""Descripcion"", ""Tipo_Descuento"", ""Valor"", 
                         ""Fecha_Inicio"", ""Fecha_Fin"", ""Limite_Usos"", ""Monto_Minimo_Compra"", 
                         ""Tope_Maximo_Descuento"", ""Activo"", ""Aplica_Eventos"", ""Aplica_Tienda"",
                         ""Id_Evento_Restringido"", ""Fecha_Creacion"", ""Id_Usuario_Creador"")
                        VALUES 
                        (@cod, @desc, @tipo, @val, 
                         @ini, @fin, @lim, @min, 
                         @tope, @act, @evt, @shop,
                         @idEvt, NOW(), @uid)
                        RETURNING ""Id_Cupon""";

                                using (var cmd = new NpgsqlCommand(sqlInsert, conexion, trans))
                                {
                                    SetParamsCupon(cmd, modelo);
                                    cmd.Parameters.AddWithValue("@uid", idUsuario);
                                    modelo.Id_Cupon = (int)await cmd.ExecuteScalarAsync();
                                }

                                await Funciones.RegistrarBitacora(conexion, idUsuario, Modulo, Parametros.AccionesBitacora.Crear, $"Creó cupón: {modelo.Codigo}", ip, trans);
                            }
                            else
                            {
                                string sqlUpdate = @"UPDATE ""Sist_Cupones"" SET 
                        ""Codigo""=@cod, ""Descripcion""=@desc, ""Tipo_Descuento""=@tipo, ""Valor""=@val, 
                        ""Fecha_Inicio""=@ini, ""Fecha_Fin""=@fin, ""Limite_Usos""=@lim, 
                        ""Monto_Minimo_Compra""=@min, ""Tope_Maximo_Descuento""=@tope, ""Activo""=@act,
                        ""Aplica_Eventos""=@evt, ""Aplica_Tienda""=@shop, 
                        ""Id_Evento_Restringido""=@idEvt,
                        ""Fecha_Edicion""=NOW(), ""Id_Usuario_Edicion""=@uid
                        WHERE ""Id_Cupon""=@id";

                                using (var cmd = new NpgsqlCommand(sqlUpdate, conexion, trans))
                                {
                                    SetParamsCupon(cmd, modelo);
                                    cmd.Parameters.AddWithValue("@uid", idUsuario);
                                    cmd.Parameters.AddWithValue("@id", modelo.Id_Cupon);
                                    await cmd.ExecuteNonQueryAsync();
                                }

                                await Funciones.RegistrarBitacora(conexion, idUsuario, Modulo, Parametros.AccionesBitacora.Editar, $"Editó cupón ID {modelo.Id_Cupon}", ip, trans);
                            }

                            // II. ACTUALIZAR RELACIÓN DE USUARIOS
                            // 1. Limpieza total (segura porque ya validamos integridad arriba)
                            string sqlDel = @"DELETE FROM ""Sist_Cupones_Usuarios"" WHERE ""Id_Cupon"" = @id";
                            using (var cmdDel = new NpgsqlCommand(sqlDel, conexion, trans))
                            {
                                cmdDel.Parameters.AddWithValue("@id", modelo.Id_Cupon);
                                await cmdDel.ExecuteNonQueryAsync();
                            }

                            // 2. Inserción masiva
                            if (modelo.IdsUsuariosAutorizados.Any())
                            {
                                string sqlIns = @"INSERT INTO ""Sist_Cupones_Usuarios"" (""Id_Cupon"", ""Id_Usuario"") VALUES (@id, @uid)";
                                foreach (var uid in modelo.IdsUsuariosAutorizados)
                                {
                                    using (var cmdIns = new NpgsqlCommand(sqlIns, conexion, trans))
                                    {
                                        cmdIns.Parameters.AddWithValue("@id", modelo.Id_Cupon);
                                        cmdIns.Parameters.AddWithValue("@uid", uid);
                                        await cmdIns.ExecuteNonQueryAsync();
                                    }
                                }
                            }

                            await trans.CommitAsync();
                        }
                        catch { await trans.RollbackAsync(); throw; }
                    }
                }

                MostrarMensaje("Éxito", "Cupón guardado correctamente.", TipoMensaje.Exito);
                return RedirectToAction("Index");
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error Crítico", "Ocurrió un error inesperado: " + ex.Message, TipoMensaje.Error);
                return View("Editor", modelo);
            }
        }

        // Método Auxiliar para mapear parámetros sin repetir código
        private void SetParamsCupon(NpgsqlCommand cmd, CuponViewModel m)
        {
            cmd.Parameters.AddWithValue("@cod", m.Codigo.Trim().ToUpper());
            cmd.Parameters.AddWithValue("@desc", (object)m.Descripcion ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@tipo", m.Tipo_Descuento);
            cmd.Parameters.AddWithValue("@val", m.Valor);
            cmd.Parameters.AddWithValue("@ini", m.Fecha_Inicio);
            cmd.Parameters.AddWithValue("@fin", m.Fecha_Fin);
            cmd.Parameters.AddWithValue("@lim", m.Limite_Usos);
            cmd.Parameters.AddWithValue("@min", m.Monto_Minimo_Compra);
            cmd.Parameters.AddWithValue("@tope", (object)m.Tope_Maximo_Descuento ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@act", m.Activo);
            cmd.Parameters.AddWithValue("@evt", m.Aplica_Eventos);
            cmd.Parameters.AddWithValue("@shop", m.Aplica_Tienda);
            cmd.Parameters.AddWithValue("@idEvt", (object)m.Id_Evento_Restringido ?? DBNull.Value);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Eliminar(int id)
        {
            if (!User.TienePermiso(Modulo, PermisoBorrar)) {
                MostrarMensaje("Error", "No tiene permisos para eliminar en ésta página", TipoMensaje.Alerta);
                return RedirectToAction("Index"); 
            }
            int idUser = int.Parse(User.FindFirst("IdUsuario").Value);

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // 1. Verificación Inicial (Lectura)
                    string sqlCheck = @"SELECT ""Conteo_Usados"", ""Codigo"" FROM ""Sist_Cupones"" WHERE ""Id_Cupon"" = @id";
                    int usados = 0;
                    string codigo = "";

                    using (var cmd = new NpgsqlCommand(sqlCheck, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                usados = (int)r["Conteo_Usados"];
                                codigo = r["Codigo"].ToString();
                            }
                            else
                            {
                                MostrarMensaje("Error", "No se ha encontrado el cupón a eliminar", TipoMensaje.Alerta);
                                return RedirectToAction("Index");
                            }
                        }
                    }

                    // 2. Operación de Escritura (Transaccional)
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            if (usados > 0)
                            {
                                // CASO A: YA FUE USADO -> SOFT DELETE (Solo desactivar)
                                // No borramos la lista de usuarios para mantener el historial.
                                var sqlSoft = @"UPDATE ""Sist_Cupones"" SET ""Activo"" = FALSE WHERE ""Id_Cupon"" = @id";
                                using (var cmd = new NpgsqlCommand(sqlSoft, conexion, trans))
                                {
                                    cmd.Parameters.AddWithValue("@id", id);
                                    await cmd.ExecuteNonQueryAsync();
                                }

                                // Opcional: Registrar en bitácora que se desactivó
                                await Funciones.RegistrarBitacora(conexion, idUser, Modulo,
                                   Parametros.AccionesBitacora.Editar,
                                   $"Desactivó cupón '{codigo}' (ya tenía usos).",
                                   HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1", trans);

                                await trans.CommitAsync();
                                MostrarMensaje("Desactivado", "El cupón ya tiene usos registrados, por integridad solo se ha desactivado.", TipoMensaje.Info);
                            }
                            else
                            {
                                // CASO B: NO HA SIDO USADO -> HARD DELETE (Borrado físico total)

                                // 1. Eliminar relaciones de usuarios (Tabla Hija)
                                var sqlDelRel = @"DELETE FROM ""Sist_Cupones_Usuarios"" WHERE ""Id_Cupon"" = @id";
                                using (var cmdRel = new NpgsqlCommand(sqlDelRel, conexion, trans))
                                {
                                    cmdRel.Parameters.AddWithValue("@id", id);
                                    await cmdRel.ExecuteNonQueryAsync();
                                }

                                // 2. Eliminar el cupón (Tabla Padre)
                                var sqlDelMain = @"DELETE FROM ""Sist_Cupones"" WHERE ""Id_Cupon"" = @id";
                                using (var cmdMain = new NpgsqlCommand(sqlDelMain, conexion, trans))
                                {
                                    cmdMain.Parameters.AddWithValue("@id", id);
                                    await cmdMain.ExecuteNonQueryAsync();
                                }

                                // 3. Bitácora
                                await Funciones.RegistrarBitacora(conexion, idUser, Modulo,
                                   Parametros.AccionesBitacora.Borrar,
                                   $"Eliminó cupón '{codigo}' permanentemente.",
                                   HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1", trans);

                                await trans.CommitAsync();
                                MostrarMensaje("Eliminado", "Cupón borrado correctamente.", TipoMensaje.Exito);
                            }
                        }
                        catch (Exception)
                        {
                            await trans.RollbackAsync();
                            throw; // Re-lanzar para que lo atrape el catch externo y muestre el mensaje
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Index");
        }
        private void SetParams(NpgsqlCommand cmd, CuponViewModel m, object idEv, object tope, object email)
        {
            cmd.Parameters.AddWithValue("@cod", m.Codigo.Trim().ToUpper());
            cmd.Parameters.AddWithValue("@desc", m.Descripcion ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@tipo", m.Tipo_Descuento);
            cmd.Parameters.AddWithValue("@val", m.Valor);
            cmd.Parameters.AddWithValue("@tope", tope);
            cmd.Parameters.AddWithValue("@min", m.Monto_Minimo_Compra);
            cmd.Parameters.AddWithValue("@lim", m.Limite_Usos);
            cmd.Parameters.AddWithValue("@ini", m.Fecha_Inicio);
            cmd.Parameters.AddWithValue("@fin", m.Fecha_Fin);
            cmd.Parameters.AddWithValue("@act", m.Activo);
            cmd.Parameters.AddWithValue("@ev", m.Aplica_Eventos);
            cmd.Parameters.AddWithValue("@tienda", m.Aplica_Tienda);
            cmd.Parameters.AddWithValue("@idEv", idEv);
            cmd.Parameters.AddWithValue("@email", email);
        }
    }
}