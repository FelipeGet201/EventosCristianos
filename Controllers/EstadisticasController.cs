using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using RedAJP.Globales;
using System.Text.Json;
using ClosedXML.Excel;

namespace RedAJP.Controllers
{
    [Authorize]
    public class EstadisticasController : GlobalController
    {
        private readonly string _cadenaConexion;
        private Parametros.Modulo Modulo = Parametros.Modulos.Estadisticas;

        public EstadisticasController(IConfiguration configuration)
        {
            _cadenaConexion = configuration.GetConnectionString("MiConexion");
        }

        public async Task<IActionResult> Index()
        {
            if (!User.TienePermiso(Modulo, PermisoLeer))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permiso para ver las estadísticas", TipoMensaje.Alerta);
                return RedirectToAction("Index", "Home");
            }

            var idAdmin = int.Parse(User.FindFirst("IdUsuario").Value);
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";

            var statsIglesias = new List<object>();
            var statsLocalidades = new List<object>();
            var statsEdades = new List<object>();
            var statsPromedioEdades = new List<object>();
            var statsSectores = new List<object>();
            var statsZonas = new List<object>();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // 1. Integrantes por Iglesia
                    string sqlIglesias = @"
                        SELECT i.nombre AS ""Iglesia"", COUNT(u.""Id_Usuario"") AS ""Total""
                        FROM iciar_iglesias i
                        INNER JOIN ""Sist_Usuarios"" u ON i.id = u.""Id_Iglesia_Asignada""
                        WHERE u.""Activo"" = TRUE
                        GROUP BY i.nombre
                        ORDER BY ""Total"" DESC LIMIT 10";
                    using (var cmd = new NpgsqlCommand(sqlIglesias, conexion))
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                            statsIglesias.Add(new { Nombre = reader["Iglesia"].ToString(), Total = Convert.ToInt32(reader["Total"]) });
                    }

                    // 2. Integrantes por Localidad
                    string sqlLocalidades = @"
                        SELECT i.localidad AS ""Localidad"", COUNT(u.""Id_Usuario"") AS ""Total""
                        FROM iciar_iglesias i
                        INNER JOIN ""Sist_Usuarios"" u ON i.id = u.""Id_Iglesia_Asignada""
                        WHERE u.""Activo"" = TRUE AND i.localidad IS NOT NULL
                        GROUP BY i.localidad
                        ORDER BY ""Total"" DESC LIMIT 10";
                    using (var cmd = new NpgsqlCommand(sqlLocalidades, conexion))
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                            statsLocalidades.Add(new { Nombre = reader["Localidad"].ToString(), Total = Convert.ToInt32(reader["Total"]) });
                    }

                    // 3. Distribución por Edades
                    string sqlEdades = @"
                        SELECT 
                            CASE 
                                WHEN EXTRACT(YEAR FROM age(current_date, u.""Fecha_Nacimiento"")) < 18 THEN 'Menores de 18'
                                WHEN EXTRACT(YEAR FROM age(current_date, u.""Fecha_Nacimiento"")) BETWEEN 18 AND 35 THEN '18 a 35 años'
                                WHEN EXTRACT(YEAR FROM age(current_date, u.""Fecha_Nacimiento"")) BETWEEN 36 AND 50 THEN '36 a 50 años'
                                ELSE 'Mayores de 50'
                            END AS ""RangoEdad"",
                            COUNT(u.""Id_Usuario"") AS ""Total""
                        FROM ""Sist_Usuarios"" u
                        WHERE u.""Activo"" = TRUE AND u.""Fecha_Nacimiento"" IS NOT NULL
                        GROUP BY ""RangoEdad""
                        ORDER BY ""RangoEdad""";
                    using (var cmd = new NpgsqlCommand(sqlEdades, conexion))
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                            statsEdades.Add(new { Rango = reader["RangoEdad"].ToString(), Total = Convert.ToInt32(reader["Total"]) });
                    }

                    // 4. Edad Promedio por Iglesia
                    string sqlPromedio = @"
                        SELECT i.nombre AS ""Iglesia"", ROUND(AVG(EXTRACT(YEAR FROM age(current_date, u.""Fecha_Nacimiento"")))::numeric, 1) AS ""EdadPromedio""
                        FROM iciar_iglesias i
                        INNER JOIN ""Sist_Usuarios"" u ON i.id = u.""Id_Iglesia_Asignada""
                        WHERE u.""Activo"" = TRUE AND u.""Fecha_Nacimiento"" IS NOT NULL
                        GROUP BY i.nombre
                        HAVING COUNT(u.""Id_Usuario"") > 5
                        ORDER BY ""EdadPromedio"" ASC";
                    using (var cmd = new NpgsqlCommand(sqlPromedio, conexion))
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                            statsPromedioEdades.Add(new { Iglesia = reader["Iglesia"].ToString(), Promedio = Convert.ToDouble(reader["EdadPromedio"]) });
                    }

                    // 5. Integrantes por Sector
                    string sqlSectores = @"
                        SELECT i.sector, COUNT(u.""Id_Usuario"") AS ""Total""
                        FROM iciar_iglesias i
                        INNER JOIN ""Sist_Usuarios"" u ON i.id = u.""Id_Iglesia_Asignada""
                        WHERE u.""Activo"" = TRUE
                        GROUP BY i.sector
                        ORDER BY i.sector ASC";
                    using (var cmd = new NpgsqlCommand(sqlSectores, conexion))
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            int val = Convert.ToInt32(reader["sector"]);
                            string lbl = val == 0 ? "Sin Configurar" : "Sector " + val;
                            statsSectores.Add(new { Nombre = lbl, Total = Convert.ToInt32(reader["Total"]) });
                        }
                    }

                    // 6. Integrantes por Zona
                    string sqlZonas = @"
                        SELECT i.sector, i.zona, COUNT(u.""Id_Usuario"") AS ""Total""
                        FROM iciar_iglesias i
                        INNER JOIN ""Sist_Usuarios"" u ON i.id = u.""Id_Iglesia_Asignada""
                        WHERE u.""Activo"" = TRUE
                        GROUP BY i.sector, i.zona
                        ORDER BY i.sector ASC, i.zona ASC";
                    using (var cmd = new NpgsqlCommand(sqlZonas, conexion))
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            int sec = Convert.ToInt32(reader["sector"]);
                            int zon = Convert.ToInt32(reader["zona"]);
                            string lblSector = sec == 0 ? "Sec S/C" : "Sector " + sec;
                            string lblZona = zon == 0 ? "Zona S/C" : "Zona " + zon;
                            statsZonas.Add(new { Nombre = $"{lblSector}, {lblZona}", Total = Convert.ToInt32(reader["Total"]) });
                        }
                    }

                    // Bitácora
                    using (var transaccion = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            await Funciones.RegistrarBitacora(conexion, idAdmin, Modulo, Parametros.AccionesBitacora.Leer, "Consultó panel de estadísticas", ip, transaccion);
                            await transaccion.CommitAsync();
                        }
                        catch { await transaccion.RollbackAsync(); }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
            }

            ViewBag.JsonIglesias = JsonSerializer.Serialize(statsIglesias);
            ViewBag.JsonLocalidades = JsonSerializer.Serialize(statsLocalidades);
            ViewBag.JsonEdades = JsonSerializer.Serialize(statsEdades);
            ViewBag.JsonPromedios = JsonSerializer.Serialize(statsPromedioEdades);
            ViewBag.JsonSectores = JsonSerializer.Serialize(statsSectores);
            ViewBag.JsonZonas = JsonSerializer.Serialize(statsZonas);

            return View();
        }

        // ========================================================
        // NUEVA FUNCIÓN: REPORTE MAESTRO (2 HOJAS EN 1 EXCEL)
        // ========================================================
        [HttpGet]
        public async Task<IActionResult> ExportarReporteMaestro()
        {
            if (!User.TienePermiso(Modulo, PermisoLeer)) return Forbid();

            string idAdminStr = User.FindFirst("IdUsuario")?.Value;
            int idAdmin = string.IsNullOrEmpty(idAdminStr) ? 0 : int.Parse(idAdminStr);
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";

            string nombreArchivo = $"Reporte_Directorio_Iglesias_{DateTime.Now:yyyyMMdd_HHmm}.xlsx";

            try
            {
                using (var workbook = new XLWorkbook())
                {
                    using (var conexion = new NpgsqlConnection(_cadenaConexion))
                    {
                        await conexion.OpenAsync();

                        // -----------------------------------------------------
                        // HOJA 1: DATOS COMPLETOS DE LAS IGLESIAS
                        // -----------------------------------------------------
                        var wsIglesias = workbook.Worksheets.Add("1. Datos de Congregaciones");

                        string sqlIglesias = @"
                            SELECT 
                                i.id AS ""ID Sistema"",
                                CASE WHEN i.zona = 0 THEN 'Sin Configurar' ELSE i.zona::text END AS ""Zona"",
                                CASE WHEN i.sector = 0 THEN 'Sin Configurar' ELSE i.sector::text END AS ""Sector"",
                                i.nombre AS ""Nombre de Iglesia"",
                                m.nombre AS ""Municipio"",
                                i.localidad AS ""Localidad"",
                                i.colonia AS ""Colonia"",
                                i.calle AS ""Calle"",
                                i.numero AS ""Número"",
                                i.referencia AS ""Referencia"",
                                i.horarios AS ""Horarios de Reunión"",
                                i.mapa_url AS ""Link Google Maps"",
                                i.facebook_url AS ""Link Facebook"",
                                i.latitud AS ""Latitud GPS"",
                                i.longitud AS ""Longitud GPS"",
                                (SELECT COUNT(*) FROM ""Sist_Usuarios"" u WHERE u.""Id_Iglesia_Asignada"" = i.id AND u.""Activo"" = TRUE) AS ""Total Integrantes""
                            FROM iciar_iglesias i
                            LEFT JOIN iciar_municipios m ON i.municipio_id = m.id
                            ORDER BY i.zona ASC, i.sector ASC, i.nombre ASC";

                        using (var cmd = new NpgsqlCommand(sqlIglesias, conexion))
                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            // Encabezados
                            for (int i = 0; i < reader.FieldCount; i++)
                            {
                                var cell = wsIglesias.Cell(1, i + 1);
                                cell.Value = reader.GetName(i);
                                cell.Style.Font.Bold = true;
                                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#005a66"); // Azul Náutico
                                cell.Style.Font.FontColor = XLColor.White;
                            }

                            // Filas
                            int row = 2;
                            while (await reader.ReadAsync())
                            {
                                for (int i = 0; i < reader.FieldCount; i++)
                                    wsIglesias.Cell(row, i + 1).Value = reader[i].ToString();
                                row++;
                            }
                            wsIglesias.Columns().AdjustToContents();
                        }

                        // -----------------------------------------------------
                        // HOJA 2: INTEGRANTES ANONIMIZADOS
                        // -----------------------------------------------------
                        var wsIntegrantes = workbook.Worksheets.Add("2. Demografía Integrantes");

                        string sqlIntegrantes = @"
                            SELECT 
                                i.id AS ""Clave Iglesia"",
                                CASE WHEN i.zona = 0 THEN 'S/C' ELSE i.zona::text END AS ""Zona"",
                                CASE WHEN i.sector = 0 THEN 'S/C' ELSE i.sector::text END AS ""Sector"",
                                i.nombre AS ""Nombre Congregación"",
                                i.localidad AS ""Localidad"",
                                CASE 
                                    WHEN u.""Fecha_Nacimiento"" IS NOT NULL THEN 
                                        EXTRACT(YEAR FROM age(current_date, u.""Fecha_Nacimiento""))::text
                                    ELSE 'Edad No Registrada'
                                END AS ""Edad del Miembro""
                            FROM ""Sist_Usuarios"" u
                            INNER JOIN iciar_iglesias i ON u.""Id_Iglesia_Asignada"" = i.id
                            WHERE u.""Activo"" = TRUE
                            ORDER BY i.zona ASC, i.sector ASC, i.nombre ASC, ""Edad del Miembro"" DESC";

                        using (var cmd = new NpgsqlCommand(sqlIntegrantes, conexion))
                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            // Encabezados
                            for (int i = 0; i < reader.FieldCount; i++)
                            {
                                var cell = wsIntegrantes.Cell(1, i + 1);
                                cell.Value = reader.GetName(i);
                                cell.Style.Font.Bold = true;
                                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#198754"); // Verde éxito
                                cell.Style.Font.FontColor = XLColor.White;
                            }

                            // Filas
                            int row = 2;
                            while (await reader.ReadAsync())
                            {
                                for (int i = 0; i < reader.FieldCount; i++)
                                    wsIntegrantes.Cell(row, i + 1).Value = reader[i].ToString();
                                row++;
                            }
                            wsIntegrantes.Columns().AdjustToContents();
                        }

                        // Registro en bitácora de la exportación
                        using (var transaccion = await conexion.BeginTransactionAsync())
                        {
                            try
                            {
                                await Funciones.RegistrarBitacora(conexion, idAdmin, Modulo, Parametros.AccionesBitacora.Exportar, "Exportó reporte maestro de Excel (Iglesias e Integrantes)", ip, transaccion);
                                await transaccion.CommitAsync();
                            }
                            catch { await transaccion.RollbackAsync(); }
                        }
                    }

                    // Guardar y retornar el archivo Excel
                    using (var stream = new MemoryStream())
                    {
                        workbook.SaveAs(stream);
                        var content = stream.ToArray();
                        return File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", nombreArchivo);
                    }
                }
            }
            catch (Exception ex)
            {
                return BadRequest("Ocurrió un error al generar el archivo Excel: " + ex.Message);
            }
        }
    }
}