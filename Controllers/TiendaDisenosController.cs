using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using RedAJP.Models;
using RedAJP.Globales;
using static RedAJP.Globales.Parametros;

namespace RedAJP.Controllers
{
    [Authorize]
    public class TiendaDisenosController : GlobalController
    {
        private readonly string _cadenaConexion;
        private readonly IConfiguration _configuration; // 1. AGREGAR AQUÍ
        private Parametros.Modulo Modulo = Parametros.Modulos.TiendaConfig;

        public TiendaDisenosController(IConfiguration configuration)
        {
            _cadenaConexion = configuration.GetConnectionString("MiConexion");
            _configuration = configuration; // 2. ASIGNAR AQUÍ
        }

        // ==========================================
        // 1. LISTADO DE SOLICITUDES
        // ==========================================
        public async Task<IActionResult> Solicitudes(string filtro = "pendientes")
        {
            if (!User.TienePermiso(Modulo, PermisoLeer))
            {
                MostrarMensaje("Error", "No tienes permiso de lectura en ésta ventana", TipoMensaje.Alerta);
                return RedirectToAction("Index", "Home");
            }

            var lista = new List<GestionSolicitudItem>();
            ViewBag.FiltroActual = filtro;

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // --- CORRECCIÓN AQUÍ ---
                    // Usamos u."NombreCompleto" tal como indicaste
                    string sql = @"SELECT s.*, u.""NombreCompleto"" as ""Usuario"",
                                          p.""Nombre_Comercial"" as ""Producto"",
                                          s.""Imagen_Original_Url"", s.""Imagen_Previo_Url"", 
                                          s.""Fecha_Creacion"", s.""Id_Estatus_Diseno"", s.""Comentarios_Admin""
                                   FROM ""Tienda_Solicitudes_Diseno"" s
                                   JOIN ""Sist_Usuarios"" u ON s.""Id_Usuario"" = u.""Id_Usuario""
                                   JOIN ""Tienda_Productos_Venta"" p ON s.""Id_Producto_Base"" = p.""Id_Producto""
                                   WHERE 1=1 ";

                    if (filtro == "pendientes") sql += " AND s.\"Id_Estatus_Diseno\" = 1";
                    else if (filtro == "historial") sql += " AND s.\"Id_Estatus_Diseno\" IN (2,3,4)";

                    sql += " ORDER BY s.\"Fecha_Creacion\" DESC";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    using (var r = await cmd.ExecuteReaderAsync())
                    {
                        while (r.Read())
                        {
                            lista.Add(new GestionSolicitudItem
                            {
                                Id_Solicitud = (int)r["Id_Solicitud"],
                                Usuario = r["Usuario"].ToString(), // Ahora lee NombreCompleto
                                Producto = r["Producto"].ToString(),
                                Fecha = (DateTime)r["Fecha_Creacion"],
                                Estatus = (int)r["Id_Estatus_Diseno"],
                                Imagen_Original_Url = r["Imagen_Original_Url"]?.ToString(),
                                Imagen_Previo_Url = r["Imagen_Previo_Url"]?.ToString(),
                                Comentarios_Admin = r["Comentarios_Admin"]?.ToString()
                            });
                        }
                    }
                }
            }
            catch (Exception ex) { TempData["Error"] = ex.Message; }

            return View(lista);
        }

        // ==========================================
        // 2. APROBAR
        // ==========================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Aprobar(int id)
        {
            if (!User.TienePermiso(Modulo, PermisoEditar))
            {
                MostrarMensaje("Error", "No tienes permiso de edición en ésta ventana", TipoMensaje.Alerta);
                return RedirectToAction("Solicitudes");
            }

            await ProcesarCambioEstatus(id, true, "Aprobado por administración.");

            MostrarMensaje("Aprobado", "Diseño aprobado correctamente.", TipoMensaje.Exito);
            return RedirectToAction("Solicitudes");
        }

        // ==========================================
        // 3. RECHAZAR
        // ==========================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Rechazar(int id, string motivo)
        {
            if (!User.TienePermiso(Modulo, PermisoEditar))
            {
                MostrarMensaje("Error", "No tienes permiso de edición en ésta ventana", TipoMensaje.Alerta);
                return RedirectToAction("Solicitudes");
            }

            await ProcesarCambioEstatus(id, false, motivo);

            MostrarMensaje("Rechazado", "Haz rechazado el diseño.", TipoMensaje.Exito);
            return RedirectToAction("Solicitudes");
        }

        // ==========================================
        // HELPER PRIVADO (Transaccional + Bitácora)
        // ==========================================
        private async Task ProcesarCambioEstatus(int idSolicitud, bool bAprobado, string comentario)
        {
            var idUser = int.Parse(User.FindFirst("IdUsuario").Value);
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString();
            int nStatus = bAprobado ? 2 : 3; // 2 = Aprobado, 3 = Rechazado
            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            // 1. Actualizar Estatus
                            string sql = @"UPDATE ""Tienda_Solicitudes_Diseno"" 
                                           SET ""Id_Estatus_Diseno"" = @st, ""Comentarios_Admin"" = @com
                                           WHERE ""Id_Solicitud"" = @id";

                            using (var cmd = new NpgsqlCommand(sql, conexion, trans))
                            {
                                cmd.Parameters.AddWithValue("@st", nStatus);
                                cmd.Parameters.AddWithValue("@com", comentario ?? "");
                                cmd.Parameters.AddWithValue("@id", idSolicitud);
                                await cmd.ExecuteNonQueryAsync();
                            }

                            // 2. Registrar en Bitácora
                            string detalle = $"Solicitud #{idSolicitud} -> Estatus {nStatus}. Nota: {comentario}";
                            await Funciones.RegistrarBitacora(conexion, idUser, Modulo, Parametros.AccionesBitacora.SolicitudDiseño, detalle, ip, trans);

                            await trans.CommitAsync();

                            // ========================================================================
                            // --- ALERTA POR CORREO: DISEÑO DICTAMINADO (AL CLIENTE) ---
                            // ========================================================================
                            try
                            {
                                string emailCliente = "";
                                string nombreCliente = "";
                                string nombreProducto = "";

                                // Obtener los datos del cliente y del producto usando una conexión rápida (fuera de la transacción)
                                string sqlDatos = @"
                                    SELECT u.""Email"", u.""NombreCompleto"", p.""Nombre_Comercial""
                                    FROM ""Tienda_Solicitudes_Diseno"" s
                                    JOIN ""Sist_Usuarios"" u ON s.""Id_Usuario"" = u.""Id_Usuario""
                                    JOIN ""Tienda_Productos_Venta"" p ON s.""Id_Producto_Base"" = p.""Id_Producto""
                                    WHERE s.""Id_Solicitud"" = @id";

                                using (var cmdD = new NpgsqlCommand(sqlDatos, conexion))
                                {
                                    cmdD.Parameters.AddWithValue("@id", idSolicitud);
                                    using (var r = await cmdD.ExecuteReaderAsync())
                                    {
                                        if (await r.ReadAsync())
                                        {
                                            emailCliente = r["Email"]?.ToString();
                                            nombreCliente = r["NombreCompleto"]?.ToString();
                                            nombreProducto = r["Nombre_Comercial"]?.ToString();
                                        }
                                    }
                                }

                                if (!string.IsNullOrEmpty(emailCliente))
                                {
                                    string estadoTexto = bAprobado ? "APROBADO" : "RECHAZADO";
                                    string colorHeader = bAprobado ? "#198754" : "#dc3545"; // Verde o Rojo
                                    string accionTexto = bAprobado
                                        ? "Tu diseño cumple con nuestros lineamientos y ya puedes agregarlo a tu carrito desde tu perfil para realizar la compra."
                                        : "Lamentablemente, tu diseño no cumple con los lineamientos y no puede ser impreso.";

                                    string htmlDictamen = $@"
                                    <div style='font-family: Arial, Helvetica, sans-serif; max-width: 600px; margin: 0 auto; border: 1px solid #e0e0e0; border-radius: 8px; overflow: hidden; box-shadow: 0 4px 6px rgba(0,0,0,0.05);'>
                                        <div style='background-color: {colorHeader}; padding: 20px; text-align: center; color: #ffffff;'>
                                            <h2 style='margin: 0; font-size: 22px; font-weight: 600;'>Resolución de tu Diseño</h2>
                                        </div>
                                        <div style='padding: 30px; background-color: #ffffff; color: #333333;'>
                                            <p style='font-size: 16px; margin-top: 0;'>Hola, <strong>{nombreCliente}</strong>:</p>
                                            <p style='font-size: 16px; line-height: 1.6;'>El equipo ha revisado la propuesta gráfica que nos enviaste para <strong>{nombreProducto}</strong> (Folio #{idSolicitud}).</p>
                                            
                                            <div style='background-color: #f8f9fa; border-left: 5px solid {colorHeader}; padding: 18px; margin: 25px 0; border-radius: 4px;'>
                                                <p style='margin: 0 0 10px 0; font-size: 15px;'><strong>Estatus Final: <span style='color:{colorHeader};'>{estadoTexto}</span></strong></p>
                                                <p style='margin: 0; line-height: 1.6;'>{accionTexto}</p>
                                            </div>

                                            <p style='font-size: 15px; color: #555555; line-height: 1.5;'>
                                                <strong>Notas del Revisor:</strong> <em>""{comentario}""</em>
                                            </p>
                                        </div>
                                    </div>";

                                    await Funciones.EnviarCorreo(_configuration, emailCliente, $"Aviso sobre tu diseño #{idSolicitud}", htmlDictamen);
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Error enviando correo de dictamen al cliente: {ex.Message}");
                            }
                        }
                        catch
                        {
                            await trans.RollbackAsync();
                            throw;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception("Error al procesar: " + ex.Message);
            }
        }
    }
}