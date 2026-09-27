using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Npgsql;
using RedAJP.Globales;
using RedAJP.Models;
using System;
using System.Threading.Tasks;

namespace RedAJP.Controllers
{
    public class DevolucionesController : GlobalController
    {
        private readonly string _cadenaConexion;
        // Asumo que mantienes los permisos ligados al módulo de Eventos
        private Parametros.Modulo Modulo = Parametros.Modulos.Eventos;

        public DevolucionesController(IConfiguration configuration)
        {
            _cadenaConexion = configuration.GetConnectionString("MiConexion");
        }

        /// <summary>
        /// GET: Muestra la lista de devoluciones adaptado a tu esquema binario (Entregado true/false).
        /// </summary>
        [Authorize]
        [HttpGet]
        public async Task<IActionResult> Index(string sid = null)
        {
            if (!User.TienePermiso(Modulo, PermisoAdmin))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permisos de administrador.", TipoMensaje.Alerta);
                return RedirectToAction("Index", "Eventos");
            }

            int idEvento = 0;
            if (!string.IsNullOrEmpty(sid)) idEvento = Funciones.DesencriptarId(sid);

            var modelo = new GestionDevolucionesViewModel { IdEventoEncriptado = sid };

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    string filtroEvento = idEvento > 0 ? "AND r.\"Id_Evento\" = @idEv" : "";

                    // AQUÍ ESTÁ LA MAGIA: Subconsulta que suma los abonos de la tabla hija
                    string sql = $@"
                        SELECT 
                            d.""Id_Devolucion"", 
                            d.""Monto"", 
                            d.""Entregado"", 
                            d.""Motivo"", 
                            b.""Nombre_Completo"",
                            e.""Titulo"" as ""Nombre_Evento"",
                            (SELECT COALESCE(SUM(""Monto_Pagado""), 0) FROM ""Devoluciones_Pagos"" dp WHERE dp.""Id_Devolucion"" = d.""Id_Devolucion"") as ""Monto_Entregado""
                        FROM ""Devoluciones"" d
                        JOIN ""Eventos_B_Asistentes"" b ON d.""Id_Referencia_Origen"" = b.""Id_Asistente""
                        JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro""
                        JOIN ""Eventos_Catalogo"" e ON r.""Id_Evento"" = e.""Id_Evento""
                        WHERE TRIM(d.""Modulo_Origen"") ILIKE 'Eventos%' 
                        {filtroEvento}
                        ORDER BY d.""Entregado"" ASC, d.""Fecha_Generacion"" DESC";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        if (idEvento > 0) cmd.Parameters.AddWithValue("@idEv", idEvento);

                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                decimal montoTotal = (decimal)r["Monto"];
                                decimal montoEntregado = (decimal)r["Monto_Entregado"]; // Ya viene calculado de la DB
                                bool completada = (bool)r["Entregado"];

                                decimal saldoPendiente = montoTotal - montoEntregado;
                                if (saldoPendiente < 0) saldoPendiente = 0;

                                string estado = completada ? "Completada" : (montoEntregado > 0 ? "Parcial" : "Pendiente");

                                modelo.Devoluciones.Add(new DevolucionItemViewModel
                                {
                                    IdDevolucion = (int)r["Id_Devolucion"],
                                    NombrePersona = r["Nombre_Completo"].ToString(),
                                    Evento = r["Nombre_Evento"].ToString(),
                                    Motivo = r["Motivo"]?.ToString() ?? "Saldo a favor",
                                    MontoTotal = montoTotal,
                                    SaldoPendiente = saldoPendiente,
                                    Estado = estado
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
                return RedirectToAction("Index", "Eventos");
            }

            return View(modelo);
        }

        /// <summary>
        /// AJAX: Obtiene el historial de abonos de una devolución específica.
        /// </summary>
        [Authorize]
        [HttpGet]
        public async Task<IActionResult> ApiHistorialPagos(int idDevolucion)
        {
            if (!User.TienePermiso(Modulo, PermisoAdmin))
                return Json(new { exito = false, mensaje = "No tienes permisos de administrador." });

            var historial = new List<dynamic>();
            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    string sql = @"
                        SELECT dp.""Monto_Pagado"", dp.""Referencia"", dp.""Fecha_Pago"", u.""NombreCompleto"" as ""Admin""
                        FROM ""Devoluciones_Pagos"" dp
                        JOIN ""Sist_Usuarios"" u ON dp.""Id_Usuario_Pago"" = u.""Id_Usuario""
                        WHERE dp.""Id_Devolucion"" = @id
                        ORDER BY dp.""Fecha_Pago"" DESC";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idDevolucion);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                historial.Add(new
                                {
                                    monto = ((decimal)r["Monto_Pagado"]).ToString("C2", new System.Globalization.CultureInfo("es-MX")),
                                    referencia = r["Referencia"]?.ToString() ?? "Sin referencia",
                                    fecha = ((DateTime)r["Fecha_Pago"]).ToString("dd MMM yyyy hh:mm tt"),
                                    admin = r["Admin"].ToString()
                                });
                            }
                        }
                    }
                }
                return Json(new { exito = true, pagos = historial });
            }
            catch (Exception ex)
            {
                return Json(new { exito = false, mensaje = ex.Message });
            }
        }

        /// <summary>
        /// POST: Procesa el pago TOTAL de una devolución asegurando integridad de datos.
        /// </summary>
        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ProcesarDevolucion(string sid, int idDevolucion, decimal montoAplicar, string referenciaPago)
        {
            if (!User.TienePermiso(Modulo, PermisoEditar))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permisos suficientes.", TipoMensaje.Error);
                return RedirectToAction("Index", new { sid = sid });
            }

            if (montoAplicar <= 0)
            {
                MostrarMensaje("Error", "El monto a abonar debe ser mayor a cero.", TipoMensaje.Alerta);
                return RedirectToAction("Index", new { sid = sid });
            }

            int idUserAdmin = int.Parse(User.FindFirst("IdUsuario").Value);
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            // 1. BLOQUEO DE FILA MAESTRA (Evita pagos paralelos)
                            string sqlLock = @"SELECT ""Monto"", ""Entregado"" FROM ""Devoluciones"" WHERE ""Id_Devolucion"" = @idDev FOR UPDATE";
                            decimal montoTotalDevolucion = 0;
                            bool yaEntregado = false;

                            using (var cmdLock = new NpgsqlCommand(sqlLock, conexion, trans))
                            {
                                cmdLock.Parameters.AddWithValue("@idDev", idDevolucion);
                                using (var r = await cmdLock.ExecuteReaderAsync())
                                {
                                    if (await r.ReadAsync())
                                    {
                                        montoTotalDevolucion = (decimal)r["Monto"];
                                        yaEntregado = (bool)r["Entregado"];
                                    }
                                    else throw new Exception("La devolución no existe.");
                                }
                            }

                            if (yaEntregado) throw new Exception("Esta devolución ya fue liquidada en su totalidad.");

                            // 2. SUMAR LOS ABONOS EXISTENTES EN LA NUEVA TABLA
                            decimal totalAbonado = 0;
                            using (var cmdSum = new NpgsqlCommand(@"SELECT COALESCE(SUM(""Monto_Pagado""), 0) FROM ""Devoluciones_Pagos"" WHERE ""Id_Devolucion"" = @idDev", conexion, trans))
                            {
                                cmdSum.Parameters.AddWithValue("@idDev", idDevolucion);
                                totalAbonado = (decimal)await cmdSum.ExecuteScalarAsync();
                            }

                            decimal saldoPendiente = montoTotalDevolucion - totalAbonado;

                            // 3. VALIDAR QUE EL NUEVO ABONO NO EXCEDA LA DEUDA
                            if (montoAplicar > saldoPendiente)
                            {
                                throw new Exception($"El abono de {montoAplicar:C2} supera el saldo pendiente de {saldoPendiente:C2}.");
                            }

                            // 4. INSERTAR EL NUEVO ABONO (TU NUEVA TABLA)
                            string sqlAbono = @"
                                INSERT INTO ""Devoluciones_Pagos"" (""Id_Devolucion"", ""Monto_Pagado"", ""Referencia"", ""Id_Usuario_Pago"", ""Fecha_Pago"") 
                                VALUES (@idDev, @monto, @ref, @idAdmin, NOW())";

                            using (var cmdAbono = new NpgsqlCommand(sqlAbono, conexion, trans))
                            {
                                cmdAbono.Parameters.AddWithValue("@idDev", idDevolucion);
                                cmdAbono.Parameters.AddWithValue("@monto", montoAplicar);
                                cmdAbono.Parameters.AddWithValue("@ref", (object)referenciaPago ?? DBNull.Value);
                                cmdAbono.Parameters.AddWithValue("@idAdmin", idUserAdmin);
                                await cmdAbono.ExecuteNonQueryAsync();
                            }

                            // 5. ¿SE LIQUIDÓ POR COMPLETO CON ESTE PAGO?
                            bool seLiquidoCompleto = (totalAbonado + montoAplicar) >= montoTotalDevolucion;

                            if (seLiquidoCompleto)
                            {
                                string sqlCierre = @"
                                    UPDATE ""Devoluciones"" 
                                    SET ""Entregado"" = TRUE, ""Fecha_Entrega"" = NOW(), ""Id_Usuario_Entrega"" = @idAdmin 
                                    WHERE ""Id_Devolucion"" = @idDev";

                                using (var cmdCierre = new NpgsqlCommand(sqlCierre, conexion, trans))
                                {
                                    cmdCierre.Parameters.AddWithValue("@idAdmin", idUserAdmin);
                                    cmdCierre.Parameters.AddWithValue("@idDev", idDevolucion);
                                    await cmdCierre.ExecuteNonQueryAsync();
                                }
                            }

                            // 6. BITÁCORA
                            string tipoPago = seLiquidoCompleto ? "LIQUIDACIÓN COMPLETA" : "ABONO PARCIAL";
                            string nota = $"Aplicó {tipoPago} de {montoAplicar:C2} a la Devolución #{idDevolucion}. Ref: {referenciaPago}";
                            await Funciones.RegistrarBitacora(conexion, idUserAdmin, Modulo, Parametros.AccionesBitacora.Editar, nota, ip, trans);

                            await trans.CommitAsync();
                            MostrarMensaje("Pago Registrado", seLiquidoCompleto ? "Devolución liquidada por completo." : $"Abono de {montoAplicar:C2} registrado con éxito.", TipoMensaje.Exito);
                        }
                        catch (Exception ex)
                        {
                            await trans.RollbackAsync();
                            throw;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Operación Abortada", ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Index", new { sid = sid });
        }
    }
}