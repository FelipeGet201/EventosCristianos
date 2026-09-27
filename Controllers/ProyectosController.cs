using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using RedAJP.Globales;
using RedAJP.Models;
using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;

namespace RedAJP.Controllers
{
    [Authorize]
    public class ProyectosController : GlobalController
    {
        private readonly string _cadenaConexion;
        private Parametros.Modulo Modulo = Parametros.Modulos.Caja;

        public ProyectosController(IConfiguration configuration)
        {
            _cadenaConexion = configuration.GetConnectionString("MiConexion");
        }

        // ========================================================================
        // 1. DASHBOARD GENERAL DE PROYECTOS (VISTA PRINCIPAL)
        // ========================================================================
        public async Task<IActionResult> Index()
        {
            if (!User.TienePermiso(Modulo, PermisoLeer))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permiso para visualizar este módulo.", TipoMensaje.Error);
                return RedirectToAction("Index", "Home");
            }

            var listaProyectos = new List<dynamic>();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    string sql = @"
                        SELECT p.*,
                               COALESCE((SELECT SUM(""Monto"") FROM ""Fin_Proyectos_Caja"" WHERE ""Id_Proyecto"" = p.""Id_Proyecto"" AND ""Tipo"" = 'Ingreso'), 0) as ""Recaudado"",
                               COALESCE((SELECT SUM(""Monto"") FROM ""Fin_Proyectos_Caja"" WHERE ""Id_Proyecto"" = p.""Id_Proyecto"" AND ""Tipo"" = 'Egreso'), 0) as ""Gastado""
                        FROM ""Fin_Proyectos"" p
                        ORDER BY p.""Activo"" DESC, p.""Fecha_Limite"" ASC";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    using (var r = await cmd.ExecuteReaderAsync())
                    {
                        while (await r.ReadAsync())
                        {
                            decimal recaudado = (decimal)r["Recaudado"];
                            decimal gastado = (decimal)r["Gastado"];

                            dynamic p = new System.Dynamic.ExpandoObject();
                            p.Id_Proyecto = (int)r["Id_Proyecto"];
                            p.Titulo = r["Titulo"].ToString();
                            p.Descripcion = r["Descripcion"].ToString();
                            p.Fecha_Limite = (DateTime)r["Fecha_Limite"];
                            p.Activo = (bool)r["Activo"];
                            p.Es_Permanente = (bool)r["Es_Permanente"];
                            p.Recaudado = recaudado;
                            p.Gastado = gastado;
                            p.Disponible = recaudado - gastado;

                            listaProyectos.Add(p);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", "No se pudieron cargar los proyectos: " + ex.Message, TipoMensaje.Error);
            }

            ViewBag.EsAdmin = User.TienePermiso(Modulo, Parametros.Permisos.Admin);
            ViewBag.PuedeCrear = User.TienePermiso(Modulo, Parametros.Permisos.Crear);
            ViewBag.PuedeEditar = User.TienePermiso(Modulo, Parametros.Permisos.Editar);

            return View(listaProyectos);
        }

        // ========================================================================
        // 2. DETALLE / LIBRO DIARIO EXCLUSIVO DE BANCO
        // ========================================================================
        public async Task<IActionResult> Detalle(int id)
        {
            if (!User.TienePermiso(Modulo, PermisoLeer))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permiso para realizar esta acción.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            dynamic proyectoInfo = new System.Dynamic.ExpandoObject();
            var historialMovimientos = new List<dynamic>();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    string sqlProyecto = @"
                        SELECT p.*,
                               COALESCE((SELECT SUM(""Monto"") FROM ""Fin_Proyectos_Caja"" WHERE ""Id_Proyecto"" = p.""Id_Proyecto"" AND ""Tipo"" = 'Ingreso'), 0) as ""Recaudado"",
                               COALESCE((SELECT SUM(""Monto"") FROM ""Fin_Proyectos_Caja"" WHERE ""Id_Proyecto"" = p.""Id_Proyecto"" AND ""Tipo"" = 'Egreso'), 0) as ""Gastado""
                        FROM ""Fin_Proyectos"" p WHERE p.""Id_Proyecto"" = @id";

                    using (var cmdP = new NpgsqlCommand(sqlProyecto, conexion))
                    {
                        cmdP.Parameters.AddWithValue("@id", id);
                        using (var r = await cmdP.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                decimal recaudado = (decimal)r["Recaudado"];
                                decimal gastado = (decimal)r["Gastado"];

                                proyectoInfo.Id_Proyecto = (int)r["Id_Proyecto"];
                                proyectoInfo.Titulo = r["Titulo"].ToString();
                                proyectoInfo.Descripcion = r["Descripcion"].ToString();
                                proyectoInfo.Fecha_Limite = (DateTime)r["Fecha_Limite"];
                                proyectoInfo.Activo = (bool)r["Activo"];
                                proyectoInfo.Es_Permanente = (bool)r["Es_Permanente"];
                                proyectoInfo.Recaudado = recaudado;
                                proyectoInfo.Gastado = gastado;
                                proyectoInfo.Disponible = recaudado - gastado;
                            }
                            else
                            {
                                MostrarMensaje("Aviso", "El proyecto solicitado no existe.", TipoMensaje.Alerta);
                                return RedirectToAction("Index");
                            }
                        }
                    }

                    string sqlMovs = @"
                        SELECT pc.*, u.""NombreCompleto"" 
                        FROM ""Fin_Proyectos_Caja"" pc
                        JOIN ""Sist_Usuarios"" u ON pc.""Id_Usuario"" = u.""Id_Usuario""
                        WHERE pc.""Id_Proyecto"" = @id
                        ORDER BY pc.""Fecha"" DESC";

                    using (var cmdM = new NpgsqlCommand(sqlMovs, conexion))
                    {
                        cmdM.Parameters.AddWithValue("@id", id);
                        using (var rM = await cmdM.ExecuteReaderAsync())
                        {
                            while (await rM.ReadAsync())
                            {
                                dynamic m = new System.Dynamic.ExpandoObject();
                                m.Id_Movimiento_Proyecto = (int)rM["Id_Movimiento_Proyecto"];
                                m.Concepto = rM["Concepto"].ToString();
                                m.Monto = (decimal)rM["Monto"];
                                m.Tipo = rM["Tipo"].ToString();
                                m.Fecha = (DateTime)rM["Fecha"];
                                m.Fecha_Registro = rM["Fecha_Registro"] != DBNull.Value ? (DateTime)rM["Fecha_Registro"] : (DateTime)rM["Fecha"];
                                m.Id_Usuario = (int)rM["Id_Usuario"];
                                m.Usuario = rM["NombreCompleto"].ToString();
                                m.FolioBancario = rM["Folio_Bancario"] != DBNull.Value ? rM["Folio_Bancario"].ToString() : "-";
                                m.EsTraspasoGeneral = (bool)rM["Es_Traspaso_General"];

                                historialMovimientos.Add(m);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error Contable", ex.Message, TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            ViewBag.EsAdmin = User.TienePermiso(Modulo, Parametros.Permisos.Admin);
            ViewBag.PuedeCrear = User.TienePermiso(Modulo, Parametros.Permisos.Crear);
            ViewBag.PuedeEditar = User.TienePermiso(Modulo, Parametros.Permisos.Editar);
            ViewBag.PuedeBorrar = User.TienePermiso(Modulo, Parametros.Permisos.Borrar);
            ViewBag.UsuarioActual = int.Parse(User.FindFirst("IdUsuario")?.Value ?? "0");

            ViewBag.Proyecto = proyectoInfo;
            return View(historialMovimientos);
        }

        // ========================================================================
        // 3. CREAR O EDITAR PROYECTO 
        // ========================================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Guardar(int Id_Proyecto, string Titulo, string Descripcion, DateTime? Fecha_Limite, bool Es_Permanente)
        {
            if (!User.TienePermiso(Modulo, Parametros.Permisos.Editar))
            {
                MostrarMensaje("Denegado", "No tienes permisos de escritura.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            if (string.IsNullOrWhiteSpace(Titulo) || string.IsNullOrWhiteSpace(Descripcion))
            {
                MostrarMensaje("Campos Vacíos", "El título y la descripción son obligatorios.", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            int idAdmin = int.Parse(User.FindFirst("IdUsuario")?.Value ?? "1");
            bool esNuevo = Id_Proyecto == 0;

            DateTime fechaFinal = Es_Permanente ? DateTime.Now.AddYears(50) : (Fecha_Limite ?? DateTime.Now);

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    string sql = esNuevo ?
                        @"INSERT INTO ""Fin_Proyectos"" (""Titulo"", ""Descripcion"", ""Fecha_Limite"", ""Es_Permanente"", ""Activo"", ""Id_Usuario_Creador"") VALUES (@t, @d, @f, @p, TRUE, @u)" :
                        @"UPDATE ""Fin_Proyectos"" SET ""Titulo"" = @t, ""Descripcion"" = @d, ""Fecha_Limite"" = @f, ""Es_Permanente"" = @p WHERE ""Id_Proyecto"" = @id";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@t", Titulo.Trim());
                        cmd.Parameters.AddWithValue("@d", Descripcion.Trim());
                        cmd.Parameters.AddWithValue("@f", fechaFinal);
                        cmd.Parameters.AddWithValue("@p", Es_Permanente);
                        if (esNuevo) cmd.Parameters.AddWithValue("@u", idAdmin);
                        else cmd.Parameters.AddWithValue("@id", Id_Proyecto);

                        await cmd.ExecuteNonQueryAsync();
                    }

                    string logText = esNuevo ? $"Creó causa: {Titulo}" : $"Modificó causa ID #{Id_Proyecto}";
                    await Funciones.RegistrarBitacora(conexion, idAdmin, Modulo, esNuevo ? Parametros.AccionesBitacora.Crear : Parametros.AccionesBitacora.Editar, logText, HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1");
                }

                MostrarMensaje("Éxito", esNuevo ? "Proyecto publicado." : "Proyecto actualizado.", TipoMensaje.Exito);
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Index");
        }

        // ========================================================================
        // 4. REGISTRAR MOVIMIENTO (SIEMPRE EN BANCO)
        // ========================================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GuardarMovimiento(int Id_Proyecto, string Concepto, decimal Monto, string Tipo, string Folio_Bancario, DateTime Fecha)
        {
            if (!User.TienePermiso(Modulo, Parametros.Permisos.Crear))
            {
                MostrarMensaje("Denegado", "No tienes permisos de creación.", TipoMensaje.Error);
                return RedirectToAction("Detalle", new { id = Id_Proyecto });
            }

            if (Fecha > DateTime.Now)
            {
                MostrarMensaje("Fecha Inválida", "La fecha contable del movimiento no puede ser posterior al instante actual.", TipoMensaje.Error);
                return RedirectToAction("Detalle", new { id = Id_Proyecto });
            }

            if (string.IsNullOrWhiteSpace(Folio_Bancario))
            {
                MostrarMensaje("Folio Requerido", "Para movimientos bancarios es estrictamente necesario capturar el Folio o Rastreabilidad de la transacción.", TipoMensaje.Error);
                return RedirectToAction("Detalle", new { id = Id_Proyecto });
            }

            int idAdmin = int.Parse(User.FindFirst("IdUsuario")?.Value ?? "1");

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    if (Tipo == "Egreso")
                    {
                        string sqlSaldo = @"
                            SELECT COALESCE(SUM(CASE WHEN ""Tipo"" = 'Ingreso' THEN ""Monto"" ELSE 0 END), 0) -
                                   COALESCE(SUM(CASE WHEN ""Tipo"" = 'Egreso' THEN ""Monto"" ELSE 0 END), 0)
                            FROM ""Fin_Proyectos_Caja"" WHERE ""Id_Proyecto"" = @id";

                        using (var cmdS = new NpgsqlCommand(sqlSaldo, conexion))
                        {
                            cmdS.Parameters.AddWithValue("@id", Id_Proyecto);
                            decimal saldoDisponible = Convert.ToDecimal(await cmdS.ExecuteScalarAsync());

                            if (Monto > saldoDisponible)
                            {
                                MostrarMensaje("Fondos Insuficientes", $"El proyecto solo cuenta con {saldoDisponible:C} en el banco. No puedes gastar {Monto:C}.", TipoMensaje.Error);
                                return RedirectToAction("Detalle", new { id = Id_Proyecto });
                            }
                        }
                    }

                    // Se fuerza a TRUE Movimiento_En_Banco internamente por integridad contable.
                    string sqlIns = @"INSERT INTO ""Fin_Proyectos_Caja"" 
                        (""Id_Proyecto"", ""Concepto"", ""Monto"", ""Tipo"", ""Fecha"", ""Id_Usuario"", ""Movimiento_En_Banco"", ""Folio_Bancario"") 
                        VALUES (@id, @c, @m, @t, @fecha, @u, TRUE, @f)";

                    using (var cmd = new NpgsqlCommand(sqlIns, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", Id_Proyecto);
                        cmd.Parameters.AddWithValue("@c", Concepto.Trim());
                        cmd.Parameters.AddWithValue("@m", Monto);
                        cmd.Parameters.AddWithValue("@t", Tipo);
                        cmd.Parameters.AddWithValue("@fecha", Fecha);
                        cmd.Parameters.AddWithValue("@u", idAdmin);
                        cmd.Parameters.AddWithValue("@f", Folio_Bancario.Trim());

                        await cmd.ExecuteNonQueryAsync();
                    }

                    await Funciones.RegistrarBitacora(conexion, idAdmin, Modulo, Parametros.AccionesBitacora.Crear, $"Registró {Tipo} de ${Monto} (Banco) en Proyecto #{Id_Proyecto}", HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1");
                }

                MostrarMensaje("Éxito", "Movimiento anexado con éxito.", TipoMensaje.Exito);
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Detalle", new { id = Id_Proyecto });
        }

        // ========================================================================
        // 5. TRASPASO DIRECCIONADO (BANCO A BANCO)
        // ========================================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> TransferirACajaGeneral(int Id_Proyecto_Transferir, decimal MontoTransferir, string ComentariosTransferencia)
        {
            if (!User.TienePermiso(Modulo, Parametros.Permisos.Admin))
            {
                MostrarMensaje("Acceso Denegado", "Solo administradores pueden vaciar fondos.", TipoMensaje.Error);
                return RedirectToAction("Detalle", new { id = Id_Proyecto_Transferir });
            }

            int idAdmin = int.Parse(User.FindFirst("IdUsuario")?.Value ?? "1");
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var transaccion = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            string sqlCheck = @"
                                SELECT (COALESCE(SUM(CASE WHEN ""Tipo"" = 'Ingreso' THEN ""Monto"" ELSE 0 END), 0) -
                                        COALESCE(SUM(CASE WHEN ""Tipo"" = 'Egreso' THEN ""Monto"" ELSE 0 END), 0)) AS ""Saldo""
                                FROM ""Fin_Proyectos_Caja"" WHERE ""Id_Proyecto"" = @id";

                            decimal saldoProyecto = 0;
                            using (var cmdC = new NpgsqlCommand(sqlCheck, conexion, transaccion))
                            {
                                cmdC.Parameters.AddWithValue("@id", Id_Proyecto_Transferir);
                                saldoProyecto = Convert.ToDecimal(await cmdC.ExecuteScalarAsync());
                            }

                            if (MontoTransferir > saldoProyecto)
                            {
                                throw new InvalidOperationException($"El proyecto solo cuenta con {saldoProyecto:C} disponibles en banco.");
                            }

                            string tituloProyecto = "Proyecto";
                            using (var cmdN = new NpgsqlCommand(@"SELECT ""Titulo"" FROM ""Fin_Proyectos"" WHERE ""Id_Proyecto"" = @id", conexion, transaccion))
                            {
                                cmdN.Parameters.AddWithValue("@id", Id_Proyecto_Transferir);
                                tituloProyecto = (await cmdN.ExecuteScalarAsync())?.ToString() ?? "Proyecto";
                            }

                            // Registramos Egreso en Proyecto (Siempre es de banco)
                            string sqlEgresoProj = @"INSERT INTO ""Fin_Proyectos_Caja"" 
                                (""Id_Proyecto"", ""Concepto"", ""Monto"", ""Tipo"", ""Fecha"", ""Id_Usuario"", ""Movimiento_En_Banco"", ""Es_Traspaso_General"") 
                                VALUES (@id, @c, @m, 'Egreso', NOW(), @u, TRUE, TRUE)";

                            using (var cmdE = new NpgsqlCommand(sqlEgresoProj, conexion, transaccion))
                            {
                                cmdE.Parameters.AddWithValue("@id", Id_Proyecto_Transferir);
                                cmdE.Parameters.AddWithValue("@c", $"RETIRO TRASPASO: Centralización a Tesorería General. Nota: " + ComentariosTransferencia);
                                cmdE.Parameters.AddWithValue("@m", MontoTransferir);
                                cmdE.Parameters.AddWithValue("@u", idAdmin);
                                await cmdE.ExecuteNonQueryAsync();
                            }

                            // Registramos Ingreso en Caja General (Siempre en banco)
                            string folioRastreo = $"TR_PROY_{Id_Proyecto_Transferir}_" + DateTime.Now.ToString("yymmdd");
                            string sqlIngresoGeneral = @"INSERT INTO ""Fin_Caja"" 
                                (""Concepto"", ""Monto"", ""Tipo"", ""Fecha"", ""Id_Usuario"", ""Movimiento_En_Banco"", ""Folio_Bancario"")
                                VALUES (@concepto, @monto, 'Ingreso', NOW(), @uid, TRUE, @folio)";

                            using (var cmdI = new NpgsqlCommand(sqlIngresoGeneral, conexion, transaccion))
                            {
                                cmdI.Parameters.AddWithValue("@concepto", $"TRASPASO DE FONDOS - Causa: {tituloProyecto}");
                                cmdI.Parameters.AddWithValue("@monto", MontoTransferir);
                                cmdI.Parameters.AddWithValue("@uid", idAdmin);
                                cmdI.Parameters.AddWithValue("@folio", folioRastreo);
                                await cmdI.ExecuteNonQueryAsync();
                            }

                            await Funciones.RegistrarBitacora(conexion, idAdmin, Modulo, Parametros.AccionesBitacora.Crear, $"Traspasó ${MontoTransferir:N2} desde Proyecto #{Id_Proyecto_Transferir} hacia Banco Central.", ip, transaccion);
                            await transaccion.CommitAsync();

                            MostrarMensaje("Traspaso Exitoso", "Los fondos fueron liquidados del subproyecto y depositados en la cuenta de banco central.", TipoMensaje.Exito);
                        }
                        catch (Exception)
                        {
                            await transaccion.RollbackAsync();
                            throw;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error en Traspaso", ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Detalle", new { id = Id_Proyecto_Transferir });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EliminarMovimiento(int idEliminar, int idProyecto)
        {
            if (!User.TienePermiso(Modulo, Parametros.Permisos.Borrar))
            {
                MostrarMensaje("Permiso Insuficiente", "No tienes nivel para eliminar registros.", TipoMensaje.Error);
                return RedirectToAction("Detalle", new { id = idProyecto });
            }

            int idUsuario = int.Parse(User.FindFirst("IdUsuario")?.Value ?? "0");
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    DateTime fechaMovimiento;
                    decimal montoBorrado = 0;
                    int idCreador = 0;
                    string tipoMovimiento = "";
                    string folioBancario = "";
                    string concepto = "";

                    // SE SELECCIONA TAMBIÉN EL CONCEPTO PARA VALIDARLO
                    string sqlCheck = "SELECT \"Fecha\", \"Monto\", \"Id_Usuario\", \"Tipo\", \"Folio_Bancario\", \"Concepto\" FROM \"Fin_Proyectos_Caja\" WHERE \"Id_Movimiento_Proyecto\" = @id";
                    using (var cmdCheck = new NpgsqlCommand(sqlCheck, conexion))
                    {
                        cmdCheck.Parameters.AddWithValue("@id", idEliminar);
                        using (var reader = await cmdCheck.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                fechaMovimiento = (DateTime)reader["Fecha"];
                                montoBorrado = (decimal)reader["Monto"];
                                idCreador = (int)reader["Id_Usuario"];
                                tipoMovimiento = reader["Tipo"].ToString();
                                folioBancario = reader["Folio_Bancario"] != DBNull.Value ? reader["Folio_Bancario"].ToString() : "";
                                concepto = reader["Concepto"] != DBNull.Value ? reader["Concepto"].ToString() : "";
                            }
                            else
                            {
                                MostrarMensaje("No Encontrado", "El movimiento ya no existe.", TipoMensaje.Alerta);
                                return RedirectToAction("Detalle", new { id = idProyecto });
                            }
                        }
                    }

                    // BLOQUEO: EVITA STRIPE O DONACIONES MANUALES YA VERIFICADAS
                    bool esStripe = !string.IsNullOrEmpty(folioBancario) && folioBancario.StartsWith("pi_");
                    bool esAportacionVerificada = concepto.Contains("Donación Verificada", StringComparison.OrdinalIgnoreCase);

                    if (esStripe || esAportacionVerificada)
                    {
                        MostrarMensaje("Bloqueo de Sistema", "No se pueden eliminar movimientos automáticos de Stripe ni donaciones ya verificadas.", TipoMensaje.Error);
                        return RedirectToAction("Detalle", new { id = idProyecto });
                    }

                    if (tipoMovimiento == "Ingreso")
                    {
                        string sqlCheckRetiros = @"                     SELECT COUNT(*) FROM ""Fin_Proyectos_Caja""                      WHERE ""Id_Proyecto"" = @idProj                        AND ""Tipo"" = 'Egreso'                        AND ""Fecha"" > @fechaMov";

                        using (var cmdRet = new NpgsqlCommand(sqlCheckRetiros, conexion))
                        {
                            cmdRet.Parameters.AddWithValue("@idProj", idProyecto);
                            cmdRet.Parameters.AddWithValue("@fechaMov", fechaMovimiento);
                            long retirosPosteriores = (long)await cmdRet.ExecuteScalarAsync();

                            if (retirosPosteriores > 0)
                            {
                                MostrarMensaje("Bloqueo Contable", "No puedes eliminar este ingreso porque ya existen retiros registrados en una fecha posterior.", TipoMensaje.Error);
                                return RedirectToAction("Detalle", new { id = idProyecto });
                            }
                        }
                    }

                    if (idCreador != idUsuario && !User.TienePermiso(Modulo, Parametros.Permisos.Admin))
                    {
                        MostrarMensaje("Acceso Denegado", "Solo puedes eliminar registros creados por ti.", TipoMensaje.Error);
                        return RedirectToAction("Detalle", new { id = idProyecto });
                    }

                    double diasDiferencia = (DateTime.Now - fechaMovimiento).TotalDays;
                    if (diasDiferencia > 7)
                    {
                        MostrarMensaje("Restricción de Tiempo", "No se pueden borrar movimientos contables con más de 7 días de antigüedad.", TipoMensaje.Alerta);
                        return RedirectToAction("Detalle", new { id = idProyecto });
                    }

                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            using (var cmdDel = new NpgsqlCommand(@"DELETE FROM ""Fin_Proyectos_Caja"" WHERE ""Id_Movimiento_Proyecto"" = @id", conexion, trans))
                            {
                                cmdDel.Parameters.AddWithValue("@id", idEliminar);
                                await cmdDel.ExecuteNonQueryAsync();
                            }

                            await Funciones.RegistrarBitacora(conexion, idUsuario, Modulo, Parametros.AccionesBitacora.Borrar, $"Eliminó movimiento manual #{idEliminar} por ${montoBorrado} del proyecto #{idProyecto}", ip, trans);
                            await trans.CommitAsync();

                            MostrarMensaje("Eliminado", "Movimiento revocado del libro diario.", TipoMensaje.Exito);
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
                MostrarMensaje("Error Crítico", ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Detalle", new { id = idProyecto });
        }

        [HttpPost]
        public async Task<IActionResult> ToggleActivo(int id, bool activo)
        {
            if (!User.TienePermiso(Modulo, Parametros.Permisos.Editar)) return Forbid();
            try
            {
                using (var con = new NpgsqlConnection(_cadenaConexion))
                {
                    await con.OpenAsync();
                    using (var cmd = new NpgsqlCommand(@"UPDATE ""Fin_Proyectos"" SET ""Activo"" = @act WHERE ""Id_Proyecto"" = @id", con))
                    {
                        cmd.Parameters.AddWithValue("@act", activo);
                        cmd.Parameters.AddWithValue("@id", id);
                        await cmd.ExecuteNonQueryAsync();
                    }
                }
                return Json(new { exito = true });
            }
            catch { return Json(new { exito = false }); }
        }
    }
}