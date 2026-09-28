using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using NpgsqlTypes;
using RedAJP.Globales;
using RedAJP.Models;
using System.Drawing;
using ClosedXML.Excel;
using Stripe;

namespace RedAJP.Controllers
{
    [Authorize]
    public class CajaController : GlobalController
    {
        private readonly string _cadenaConexion;
        private readonly IConfiguration _configuration; 
        private Parametros.Modulo Modulo = Parametros.Modulos.Caja;

        public CajaController(IConfiguration configuration)
        {
            _configuration = configuration; 
            _cadenaConexion = configuration.GetConnectionString("MiConexion");
        }


        public async Task<IActionResult> Index(int? mes, int? anio)
        {
            // 1. VERIFICACIÓN DE PERMISOS
            if (!User.TienePermiso(Modulo, PermisoLeer))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permiso para acceder a esta opción.", TipoMensaje.Error);
                return RedirectToAction("Index", "Home");
            }

            // 2. CONFIGURACIÓN DE FECHAS
            int anioActual = anio ?? DateTime.Now.Year;
            int mesActual = mes ?? DateTime.Now.Month;

            ViewBag.MesSeleccionado = mesActual;
            ViewBag.AnioSeleccionado = anioActual;

            // 3. INICIALIZACIÓN DE VARIABLES
            var listaMovimientos = new List<MovimientoCaja>();
            var listaAnios = new List<int>();

            decimal ingresosManualesMes = 0;
            decimal egresosManualesMes = 0;
            decimal egresosBancoMes = 0;
            decimal egresosCajaMes = 0;

            decimal eventosMes = 0, rifasMes = 0, tiendaMes = 0;

            decimal saldoCajaFisica = 0;
            decimal saldoBancoTotal = 0;

            decimal saldoBancoEventos = 0;
            decimal saldoBancoRifas = 0;
            decimal saldoBancoTienda = 0;

            decimal totalProyectos = 0;
            decimal totalPendienteSincronizar = 0; // NUEVO: Para el dinero en tránsito

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // PASO 1: OBTENER LISTA DE AÑOS DISPONIBLES
                    string sqlAnios = "SELECT DISTINCT \"Anio\" FROM \"Fin_Caja\" ORDER BY \"Anio\" DESC";
                    using (var cmdA = new NpgsqlCommand(sqlAnios, conexion))
                    using (var rA = await cmdA.ExecuteReaderAsync())
                    {
                        while (await rA.ReadAsync()) listaAnios.Add((int)rA["Anio"]);
                    }
                    if (listaAnios.Count == 0) listaAnios.Add(DateTime.Now.Year);
                    if (!listaAnios.Contains(DateTime.Now.Year)) listaAnios.Insert(0, DateTime.Now.Year);

                    // PASO 1.5: OBTENER TOTAL DE FONDOS EN PROYECTOS
                    string sqlTotProj = @"SELECT COALESCE(SUM(CASE WHEN ""Tipo"" = 'Ingreso' THEN ""Monto"" ELSE 0 END), 0) -
                                                 COALESCE(SUM(CASE WHEN ""Tipo"" = 'Egreso' THEN ""Monto"" ELSE 0 END), 0)
                                          FROM ""Fin_Proyectos_Caja""";
                    totalProyectos = Convert.ToDecimal(await new NpgsqlCommand(sqlTotProj, conexion).ExecuteScalarAsync());


                    // PASO 2: CÁLCULO DE SALDOS HISTÓRICOS (REALES AL DÍA DE HOY)
                    string sqlSaldoFisico = @"SELECT 
                COALESCE(SUM(CASE WHEN ""Tipo"" = 'Ingreso' THEN ""Monto"" ELSE 0 END), 0) -
                COALESCE(SUM(CASE WHEN ""Tipo"" = 'Egreso' THEN ""Monto"" ELSE 0 END), 0)
                FROM ""Fin_Caja"" WHERE ""Movimiento_En_Banco"" = FALSE";
                    saldoCajaFisica = Convert.ToDecimal(await new NpgsqlCommand(sqlSaldoFisico, conexion).ExecuteScalarAsync());

                    // Solo se suma el dinero validado y que pertenece a la cuenta principal
                    saldoBancoEventos = Convert.ToDecimal(await new NpgsqlCommand("SELECT COALESCE(SUM(\"Total_Neto\"), 0.00) FROM \"Eventos_D_Transacciones\" WHERE \"Estatus_Pago\" = 'paid' AND \"ValidadoXCaja\" = TRUE AND \"CuentaPrincipalBanco\" = TRUE", conexion).ExecuteScalarAsync());
                    saldoBancoRifas = Convert.ToDecimal(await new NpgsqlCommand("SELECT COALESCE(SUM(\"Total_Neto\"), 0.00) FROM \"Rifas_Ventas\" WHERE \"Estado\" = 'Pagado' AND \"ValidadoXCaja\" = TRUE AND \"CuentaPrincipalBanco\" = TRUE", conexion).ExecuteScalarAsync());
                    saldoBancoTienda = Convert.ToDecimal(await new NpgsqlCommand("SELECT COALESCE(SUM(\"Total_Neto\"), 0.00) FROM \"Tienda_Pedidos\" WHERE \"Id_Estatus\" >= 30 AND \"ValidadoXCaja\" = TRUE AND \"CuentaPrincipalBanco\" = TRUE", conexion).ExecuteScalarAsync());

                    decimal totalIngresosWeb = saldoBancoEventos + saldoBancoRifas + saldoBancoTienda;

                    string sqlIngresosBancoManual = @"SELECT COALESCE(SUM(""Monto""), 0) FROM ""Fin_Caja"" WHERE ""Tipo"" = 'Ingreso' AND ""Movimiento_En_Banco"" = TRUE";
                    decimal ingresosBancoManual = Convert.ToDecimal(await new NpgsqlCommand(sqlIngresosBancoManual, conexion).ExecuteScalarAsync());

                    string sqlEgresosBancoManual = @"SELECT COALESCE(SUM(""Monto""), 0) FROM ""Fin_Caja"" WHERE ""Tipo"" = 'Egreso' AND ""Movimiento_En_Banco"" = TRUE";
                    decimal egresosBancoManual = Convert.ToDecimal(await new NpgsqlCommand(sqlEgresosBancoManual, conexion).ExecuteScalarAsync());

                    saldoBancoTotal = (totalIngresosWeb + ingresosBancoManual) - egresosBancoManual;

                    // CÁLCULO DE DINERO PENDIENTE DE SINCRONIZAR (En tránsito)
                    // Se excluyen de este conteo las transferencias directas a cuentas personalizadas, ya que no son Stripe
                    string sqlPendienteEventos = @"
                        SELECT COALESCE(SUM(t.""Total_Neto""), 0.00) 
                        FROM ""Eventos_D_Transacciones"" t
                        JOIN ""Eventos_Catalogo"" ec ON t.""Id_Evento"" = ec.""Id_Evento""
                        WHERE t.""Estatus_Pago"" = 'paid' 
                        AND t.""ValidadoXCaja"" = FALSE 
                        AND NOT (t.""EsTransferencia"" = TRUE AND ec.""Usa_Cuenta_Personalizada"" = TRUE)";

                    decimal pendienteEventos = Convert.ToDecimal(await new NpgsqlCommand(sqlPendienteEventos, conexion).ExecuteScalarAsync());
                    decimal pendienteRifas = Convert.ToDecimal(await new NpgsqlCommand("SELECT COALESCE(SUM(\"Total_Neto\"), 0.00) FROM \"Rifas_Ventas\" WHERE \"Estado\" = 'Pagado' AND \"ValidadoXCaja\" = FALSE", conexion).ExecuteScalarAsync());
                    decimal pendienteTienda = Convert.ToDecimal(await new NpgsqlCommand("SELECT COALESCE(SUM(\"Total_Neto\"), 0.00) FROM \"Tienda_Pedidos\" WHERE \"Id_Estatus\" >= 30 AND \"ValidadoXCaja\" = FALSE", conexion).ExecuteScalarAsync());

                    totalPendienteSincronizar = pendienteEventos + pendienteRifas + pendienteTienda;


                    // PASO 3: MOVIMIENTOS DEL PERIODO SELECCIONADO
                    string filtroMesCaja = (mesActual == 0) ? "" : "AND c.\"Mes\" = @mes";

                    string sqlMovs = $@"
    SELECT c.*, u.""NombreCompleto"", ec.""Titulo"" AS ""NombreEvento""
    FROM ""Fin_Caja"" c
    LEFT JOIN ""Sist_Usuarios"" u ON c.""Id_Usuario"" = u.""Id_Usuario""
    LEFT JOIN ""Eventos_Catalogo"" ec ON c.""Id_Evento"" = ec.""Id_Evento""
    WHERE c.""Anio"" = @anio {filtroMesCaja}
    ORDER BY c.""Fecha"" DESC";

                    using (var cmd = new NpgsqlCommand(sqlMovs, conexion))
                    {
                        cmd.Parameters.AddWithValue("@anio", anioActual);
                        if (mesActual != 0) cmd.Parameters.AddWithValue("@mes", mesActual);

                        using (var lector = await cmd.ExecuteReaderAsync())
                        {
                            while (await lector.ReadAsync())
                            {
                                var m = new MovimientoCaja
                                {
                                    Id_Movimiento = (int)lector["Id_Movimiento"],
                                    Concepto = lector["Concepto"].ToString(),
                                    Monto = (decimal)lector["Monto"],
                                    Tipo = lector["Tipo"].ToString(),
                                    Fecha = (DateTime)lector["Fecha"],
                                    NombreUsuario = lector["NombreCompleto"].ToString(),
                                    Id_Usuario = (int)lector["Id_Usuario"],
                                    Id_Archivo = lector["Id_Archivo"] == DBNull.Value ? null : (int)lector["Id_Archivo"],
                                    Movimiento_En_Banco = (bool)lector["Movimiento_En_Banco"],
                                    FolioBancario = lector["Folio_Bancario"] != DBNull.Value ? lector["Folio_Bancario"].ToString() : "",
                                    Id_Evento = lector["Id_Evento"] == DBNull.Value ? null : (int?)lector["Id_Evento"],
                                    NombreEvento = lector["NombreEvento"] != DBNull.Value ? lector["NombreEvento"].ToString() : null
                                };
                                listaMovimientos.Add(m);

                                if (m.Tipo == "Ingreso") { ingresosManualesMes += m.Monto; }
                                else
                                {
                                    egresosManualesMes += m.Monto;
                                    if (m.Movimiento_En_Banco) egresosBancoMes += m.Monto;
                                    else egresosCajaMes += m.Monto;
                                }
                            }
                        }
                    }

                    // PASO 4: INGRESOS WEB DEL PERIODO (MODIFICADO: Solo sumamos lo oficial validado)
                    string fFecha = (mesActual == 0) ? "EXTRACT(YEAR FROM {0}) = @a" : "EXTRACT(YEAR FROM {0}) = @a AND EXTRACT(MONTH FROM {0}) = @m";

                    var cmdE = new NpgsqlCommand($"SELECT COALESCE(SUM(\"Total_Neto\"), 0) FROM \"Eventos_D_Transacciones\" WHERE \"Estatus_Pago\" = 'paid' AND \"ValidadoXCaja\" = TRUE AND \"CuentaPrincipalBanco\" = TRUE AND " + string.Format(fFecha, "\"Fecha_Intento\""), conexion);
                    cmdE.Parameters.AddWithValue("@a", anioActual); if (mesActual != 0) cmdE.Parameters.AddWithValue("@m", mesActual);
                    eventosMes = Convert.ToDecimal(await cmdE.ExecuteScalarAsync());

                    var cmdR = new NpgsqlCommand($"SELECT COALESCE(SUM(\"Total_Neto\"), 0) FROM \"Rifas_Ventas\" WHERE \"Estado\" = 'Pagado' AND \"ValidadoXCaja\" = TRUE AND \"CuentaPrincipalBanco\" = TRUE AND " + string.Format(fFecha, "\"Fecha_Pago\""), conexion);
                    cmdR.Parameters.AddWithValue("@a", anioActual); if (mesActual != 0) cmdR.Parameters.AddWithValue("@m", mesActual);
                    rifasMes = Convert.ToDecimal(await cmdR.ExecuteScalarAsync());

                    var cmdT = new NpgsqlCommand($"SELECT COALESCE(SUM(\"Total_Neto\"), 0) FROM \"Tienda_Pedidos\" WHERE \"Id_Estatus\" >= 30 AND \"ValidadoXCaja\" = TRUE AND \"CuentaPrincipalBanco\" = TRUE AND " + string.Format(fFecha, "\"Fecha_Solicitud\""), conexion);
                    cmdT.Parameters.AddWithValue("@a", anioActual); if (mesActual != 0) cmdT.Parameters.AddWithValue("@m", mesActual);
                    tiendaMes = Convert.ToDecimal(await cmdT.ExecuteScalarAsync());

                    // --- OBTENER LISTA DE EVENTOS PARA EL SELECT DEL MODAL ---
                    var listaEventosCaja = new List<dynamic>();
                    string sqlEvts = @"SELECT ""Id_Evento"", ""Titulo"" FROM ""Eventos_Catalogo"" ORDER BY ""Fecha_Inicio"" DESC";
                    using (var cmdEv = new NpgsqlCommand(sqlEvts, conexion))
                    using (var rEv = await cmdEv.ExecuteReaderAsync())
                    {
                        while (await rEv.ReadAsync())
                        {
                            listaEventosCaja.Add(new { Id = (int)rEv["Id_Evento"], Titulo = rEv["Titulo"].ToString() });
                        }
                    }
                    ViewBag.EventosCaja = listaEventosCaja;
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }

            ViewBag.TotalPendienteSincronizar = totalPendienteSincronizar; // <--- NUEVO: ENVIADO A LA VISTA
            ViewBag.TotalProyectos = totalProyectos;
            ViewBag.AniosDisponibles = listaAnios;
            ViewBag.SaldoCajaFisica = saldoCajaFisica;
            ViewBag.SaldoBancoTotal = saldoBancoTotal;
            ViewBag.CapitalTotal = saldoCajaFisica + saldoBancoTotal;
            ViewBag.SaldoBancoEventos = saldoBancoEventos;
            ViewBag.SaldoBancoRifas = saldoBancoRifas;
            ViewBag.SaldoBancoTienda = saldoBancoTienda;
            ViewBag.IngresosManualesMes = ingresosManualesMes;
            ViewBag.EventosMes = eventosMes;
            ViewBag.RifasMes = rifasMes;
            ViewBag.TiendaMes = tiendaMes;
            ViewBag.EgresosManualesMes = egresosManualesMes;
            ViewBag.EgresosBancoMes = egresosBancoMes;
            ViewBag.EgresosCajaMes = egresosCajaMes;

            // --- CÁLCULO DE SALDOS POR USUARIO Y TRASPASOS PENDIENTES ---
            int idUsuarioLogueado = int.Parse(User.FindFirst("IdUsuario")?.Value ?? "0");
            var listaSaldosUsuarios = new List<dynamic>();
            var listaTraspasosMePiden = new List<dynamic>();
            var listaTraspasosPedi = new List<dynamic>();
            var listaUsuariosCombo = new List<dynamic>();
            var listaMiHistorial = new List<dynamic>();
            decimal miSaldoFisico = 0;

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    string sqlMiSaldo = @"
            SELECT (
                COALESCE((SELECT SUM(""Monto"") FROM ""Fin_Caja"" WHERE ""Id_Usuario"" = @uid AND ""Tipo"" = 'Ingreso' AND ""Movimiento_En_Banco"" = FALSE), 0) -
                COALESCE((SELECT SUM(""Monto"") FROM ""Fin_Caja"" WHERE ""Id_Usuario"" = @uid AND ""Tipo"" = 'Egreso' AND ""Movimiento_En_Banco"" = FALSE), 0) +
                COALESCE((SELECT SUM(""Monto"") FROM ""Fin_Traspasos"" WHERE ""Id_Usuario_Destino"" = @uid AND ""Estado"" = 'ACE'), 0) -
                COALESCE((SELECT SUM(""Monto"") FROM ""Fin_Traspasos"" WHERE ""Id_Usuario_Origen"" = @uid AND ""Estado"" = 'ACE'), 0)
            )";
                    using (var cmdMS = new NpgsqlCommand(sqlMiSaldo, conexion))
                    {
                        cmdMS.Parameters.AddWithValue("@uid", idUsuarioLogueado);
                        miSaldoFisico = Convert.ToDecimal(await cmdMS.ExecuteScalarAsync());
                    }

                    string sqlSaldos = @"
            WITH UsuariosCaja AS (
                SELECT u.""Id_Usuario"", u.""NombreCompleto""
                FROM ""Sist_Usuarios"" u
                WHERE u.""Activo"" = TRUE
                  AND (
                      EXISTS (SELECT 1 FROM ""Fin_Caja"" c WHERE c.""Id_Usuario"" = u.""Id_Usuario"")
                      OR EXISTS (SELECT 1 FROM ""Fin_Traspasos"" t WHERE t.""Id_Usuario_Origen"" = u.""Id_Usuario"" OR t.""Id_Usuario_Destino"" = u.""Id_Usuario"")
                  )
            ),
            SaldosCalculados AS (
                SELECT 
                    uc.""Id_Usuario"", 
                    uc.""NombreCompleto"",
                    (
                        COALESCE((SELECT SUM(""Monto"") FROM ""Fin_Caja"" WHERE ""Id_Usuario"" = uc.""Id_Usuario"" AND ""Tipo"" = 'Ingreso' AND ""Movimiento_En_Banco"" = FALSE), 0) -
                        COALESCE((SELECT SUM(""Monto"") FROM ""Fin_Caja"" WHERE ""Id_Usuario"" = uc.""Id_Usuario"" AND ""Tipo"" = 'Egreso' AND ""Movimiento_En_Banco"" = FALSE), 0) +
                        COALESCE((SELECT SUM(""Monto"") FROM ""Fin_Traspasos"" WHERE ""Id_Usuario_Destino"" = uc.""Id_Usuario"" AND ""Estado"" = 'ACE'), 0) -
                        COALESCE((SELECT SUM(""Monto"") FROM ""Fin_Traspasos"" WHERE ""Id_Usuario_Origen"" = uc.""Id_Usuario"" AND ""Estado"" = 'ACE'), 0)
                    ) AS ""Saldo_Actual""
                FROM UsuariosCaja uc
            )
            SELECT * FROM SaldosCalculados WHERE ""Saldo_Actual"" > 0;";

                    using (var cmdS = new NpgsqlCommand(sqlSaldos, conexion))
                    using (var rS = await cmdS.ExecuteReaderAsync())
                    {
                        while (await rS.ReadAsync())
                        {
                            int idU = (int)rS["Id_Usuario"];
                            string nombre = rS["NombreCompleto"].ToString();
                            decimal saldo = (decimal)rS["Saldo_Actual"];

                            dynamic objResult = new System.Dynamic.ExpandoObject();
                            objResult.Id = idU;
                            objResult.Nombre = nombre;
                            objResult.Saldo = saldo;

                            listaSaldosUsuarios.Add(objResult);

                            if (idU != idUsuarioLogueado)
                            {
                                listaUsuariosCombo.Add(objResult);
                            }
                        }
                    }

                    string sqlMePiden = @"SELECT t.*, u.""NombreCompleto"" AS ""Solicitante"" FROM ""Fin_Traspasos"" t JOIN ""Sist_Usuarios"" u ON t.""Id_Usuario_Destino"" = u.""Id_Usuario"" WHERE t.""Id_Usuario_Origen"" = @uid AND t.""Estado"" = 'PEN'";
                    using (var cmdP = new NpgsqlCommand(sqlMePiden, conexion))
                    {
                        cmdP.Parameters.AddWithValue("@uid", idUsuarioLogueado);
                        using (var rP = await cmdP.ExecuteReaderAsync())
                        {
                            while (await rP.ReadAsync()) listaTraspasosMePiden.Add(new { Id = (int)rP["Id_Traspaso"], Nombre = rP["Solicitante"].ToString(), Monto = (decimal)rP["Monto"], Concepto = rP["Concepto"].ToString(), Fecha = (DateTime)rP["Fecha_Solicitud"] });
                        }
                    }

                    string sqlPedi = @"SELECT t.*, u.""NombreCompleto"" AS ""A_Quien_Pedi"" FROM ""Fin_Traspasos"" t JOIN ""Sist_Usuarios"" u ON t.""Id_Usuario_Origen"" = u.""Id_Usuario"" WHERE t.""Id_Usuario_Destino"" = @uid AND t.""Estado"" = 'PEN'";
                    using (var cmdM = new NpgsqlCommand(sqlPedi, conexion))
                    {
                        cmdM.Parameters.AddWithValue("@uid", idUsuarioLogueado);
                        using (var rM = await cmdM.ExecuteReaderAsync())
                        {
                            while (await rM.ReadAsync()) listaTraspasosPedi.Add(new { Id = (int)rM["Id_Traspaso"], Nombre = rM["A_Quien_Pedi"].ToString(), Monto = (decimal)rM["Monto"], Concepto = rM["Concepto"].ToString(), Fecha = (DateTime)rM["Fecha_Solicitud"] });
                        }
                    }

                    string sqlHistorial = @"
    SELECT 'Movimiento' AS ""TipoRegistro"", ""Id_Movimiento"" AS ""Id"", ""Concepto"", ""Monto"", ""Tipo"", ""Fecha""
    FROM ""Fin_Caja""
    WHERE ""Id_Usuario"" = @uid AND ""Movimiento_En_Banco"" = FALSE
    UNION ALL
    SELECT 'Traspaso Entrante' AS ""TipoRegistro"", ""Id_Traspaso"" AS ""Id"", ""Concepto"", ""Monto"", 'Ingreso' AS ""Tipo"", ""Fecha_Resolucion"" AS ""Fecha""
    FROM ""Fin_Traspasos""
    WHERE ""Id_Usuario_Destino"" = @uid AND ""Estado"" = 'ACE'
    UNION ALL
    SELECT 'Traspaso Saliente' AS ""TipoRegistro"", ""Id_Traspaso"" AS ""Id"", ""Concepto"", ""Monto"", 'Egreso' AS ""Tipo"", ""Fecha_Resolucion"" AS ""Fecha""
    FROM ""Fin_Traspasos""
    WHERE ""Id_Usuario_Origen"" = @uid AND ""Estado"" = 'ACE'
    ORDER BY ""Fecha"" DESC";

                    using (var cmdH = new NpgsqlCommand(sqlHistorial, conexion))
                    {
                        cmdH.Parameters.AddWithValue("@uid", idUsuarioLogueado);
                        using (var rH = await cmdH.ExecuteReaderAsync())
                        {
                            while (await rH.ReadAsync())
                            {
                                listaMiHistorial.Add(new
                                {
                                    TipoRegistro = rH["TipoRegistro"].ToString(),
                                    Concepto = rH["Concepto"].ToString(),
                                    Monto = (decimal)rH["Monto"],
                                    Tipo = rH["Tipo"].ToString(),
                                    Fecha = (DateTime)rH["Fecha"]
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { /* Manejo silencioso o log */ }

            ViewBag.MiSaldo = miSaldoFisico;
            ViewBag.SaldosUsuarios = listaSaldosUsuarios;
            ViewBag.TraspasosMePiden = listaTraspasosMePiden;
            ViewBag.TraspasosPedi = listaTraspasosPedi;
            ViewBag.UsuariosCombo = listaUsuariosCombo;
            ViewBag.MiHistorial = listaMiHistorial;

            return View(listaMovimientos);
        }

        [Authorize]
        public IActionResult DescargarReporteExcel(int? mes, int? anio)
        {
            if (!User.TienePermiso(Modulo, PermisoLeer))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permiso para acceder a esta opción.", TipoMensaje.Error);
                return RedirectToAction("Index", "Home");
            }

            int anioActual = anio ?? DateTime.Now.Year;
            int mesActual = mes ?? DateTime.Now.Month;

            var cultura = new System.Globalization.CultureInfo("es-MX");
            string nombreMes = mesActual == 0 ? "Consolidado_Anual" : cultura.DateTimeFormat.GetMonthName(mesActual);
            string textoPeriodo = mesActual == 0 ? $"Año {anioActual}" : $"{cultura.TextInfo.ToTitleCase(nombreMes)} {anioActual}";

            using (var workbook = new XLWorkbook())
            {
                // ========================================================================
                // 1. OBTENCIÓN DE DATOS DE LA BASE DE DATOS
                // ========================================================================

                decimal saldoBancoReal = 0;
                decimal saldoCajaReal = 0;

                decimal pWebEventos = 0, pWebRifas = 0, pWebTienda = 0;
                decimal pBancoIng = 0, pBancoEgr = 0;
                decimal pCajaIng = 0, pCajaEgr = 0;

                // Listas sencillas para guardar los movimientos de banco y de caja física
                var listaBanco = new List<(DateTime Fecha, string Concepto, string Origen, string Asociacion, string Folio, string Usuario, decimal Monto, string Tipo, string TipoIngreso, string Comprobante)>();
                var listaCaja = new List<(DateTime Fecha, string Concepto, string Origen, string Asociacion, string Folio, string Usuario, decimal Monto, string Tipo, string TipoIngreso, string Comprobante)>();

                using (var con = new NpgsqlConnection(_cadenaConexion))
                {
                    con.Open();

                    // Calcular saldos totales históricos hasta el día de hoy
                    string sqlWebHist = "SELECT COALESCE(SUM(\"Total_Neto\"), 0) FROM {0} WHERE {1}";
                    decimal hEventos = Convert.ToDecimal(new NpgsqlCommand(string.Format(sqlWebHist, "\"Eventos_D_Transacciones\"", "\"Estatus_Pago\"='paid' AND \"ValidadoXCaja\" = TRUE AND \"CuentaPrincipalBanco\" = TRUE"), con).ExecuteScalar());
                    decimal hRifas = Convert.ToDecimal(new NpgsqlCommand(string.Format(sqlWebHist, "\"Rifas_Ventas\"", "\"Estado\"='Pagado' AND \"ValidadoXCaja\" = TRUE AND \"CuentaPrincipalBanco\" = TRUE"), con).ExecuteScalar());
                    decimal hTienda = Convert.ToDecimal(new NpgsqlCommand(string.Format(sqlWebHist, "\"Tienda_Pedidos\"", "\"Id_Estatus\">=30 AND \"ValidadoXCaja\" = TRUE AND \"CuentaPrincipalBanco\" = TRUE"), con).ExecuteScalar());

                    string sqlManBancoHist = @"SELECT COALESCE(SUM(CASE WHEN ""Tipo"" = 'Ingreso' THEN ""Monto"" ELSE 0 END), 0) - COALESCE(SUM(CASE WHEN ""Tipo"" = 'Egreso' THEN ""Monto"" ELSE 0 END), 0) FROM ""Fin_Caja"" WHERE ""Movimiento_En_Banco"" = TRUE";
                    decimal hManualBanco = Convert.ToDecimal(new NpgsqlCommand(sqlManBancoHist, con).ExecuteScalar());
                    saldoBancoReal = (hEventos + hRifas + hTienda) + hManualBanco;

                    string sqlManCajaHist = @"SELECT COALESCE(SUM(CASE WHEN ""Tipo"" = 'Ingreso' THEN ""Monto"" ELSE 0 END), 0) - COALESCE(SUM(CASE WHEN ""Tipo"" = 'Egreso' THEN ""Monto"" ELSE 0 END), 0) FROM ""Fin_Caja"" WHERE ""Movimiento_En_Banco"" = FALSE";
                    saldoCajaReal = Convert.ToDecimal(new NpgsqlCommand(sqlManCajaHist, con).ExecuteScalar());

                    // Filtro para saber si buscamos por año o por mes específico
                    string fFecha = (mesActual == 0) ? "EXTRACT(YEAR FROM {0}) = @a" : "EXTRACT(YEAR FROM {0}) = @a AND EXTRACT(MONTH FROM {0}) = @m";

                    // Obtener dinero de eventos, rifas y tienda del periodo seleccionado
                    var cmdE = new NpgsqlCommand($"SELECT COALESCE(SUM(\"Total_Neto\"),0) FROM \"Eventos_D_Transacciones\" WHERE \"Estatus_Pago\"='paid' AND \"ValidadoXCaja\"=TRUE AND \"CuentaPrincipalBanco\"=TRUE AND " + string.Format(fFecha, "\"Fecha_Intento\""), con);
                    cmdE.Parameters.AddWithValue("@a", anioActual); if (mesActual != 0) cmdE.Parameters.AddWithValue("@m", mesActual);
                    pWebEventos = Convert.ToDecimal(cmdE.ExecuteScalar());

                    var cmdR = new NpgsqlCommand($"SELECT COALESCE(SUM(\"Total_Neto\"),0) FROM \"Rifas_Ventas\" WHERE \"Estado\"='Pagado' AND \"ValidadoXCaja\"=TRUE AND \"CuentaPrincipalBanco\"=TRUE AND " + string.Format(fFecha, "\"Fecha_Pago\""), con);
                    cmdR.Parameters.AddWithValue("@a", anioActual); if (mesActual != 0) cmdR.Parameters.AddWithValue("@m", mesActual);
                    pWebRifas = Convert.ToDecimal(cmdR.ExecuteScalar());

                    var cmdT = new NpgsqlCommand($"SELECT COALESCE(SUM(\"Total_Neto\"),0) FROM \"Tienda_Pedidos\" WHERE \"Id_Estatus\">=30 AND \"ValidadoXCaja\"=TRUE AND \"CuentaPrincipalBanco\"=TRUE AND " + string.Format(fFecha, "\"Fecha_Solicitud\""), con);
                    cmdT.Parameters.AddWithValue("@a", anioActual); if (mesActual != 0) cmdT.Parameters.AddWithValue("@m", mesActual);
                    pWebTienda = Convert.ToDecimal(cmdT.ExecuteScalar());

                    // Cargar movimientos manuales de la caja general
                    string sqlManuales = $@"
                SELECT c.""Fecha"", c.""Tipo"", c.""Concepto"", c.""Monto"", c.""Movimiento_En_Banco"", c.""Folio_Bancario"", 
                       u.""NombreCompleto"", ec.""Titulo"" AS ""NombreEvento"", ra.""Titulo"" AS ""NombreArchivo""
                FROM ""Fin_Caja"" c 
                LEFT JOIN ""Sist_Usuarios"" u ON c.""Id_Usuario"" = u.""Id_Usuario"" 
                LEFT JOIN ""Eventos_Catalogo"" ec ON c.""Id_Evento"" = ec.""Id_Evento"" 
                LEFT JOIN ""Rec_Archivos"" ra ON c.""Id_Archivo"" = ra.""Id_Archivo""
                WHERE " + string.Format(fFecha, "c.\"Fecha\"");
                    using (var cmd = new NpgsqlCommand(sqlManuales, con))
                    {
                        cmd.Parameters.AddWithValue("@a", anioActual); if (mesActual != 0) cmd.Parameters.AddWithValue("@m", mesActual);
                        using (var r = cmd.ExecuteReader())
                        {
                            while (r.Read())
                            {
                                bool esBanco = (bool)r["Movimiento_En_Banco"];
                                string tipo = r["Tipo"].ToString();
                                decimal monto = (decimal)r["Monto"];
                                string nomEvento = r["NombreEvento"] != DBNull.Value ? r["NombreEvento"].ToString() : "N/A";
                                string usuario = r["NombreCompleto"] != DBNull.Value ? r["NombreCompleto"].ToString() : "Desconocido";
                                string folio = r["Folio_Bancario"] != DBNull.Value ? r["Folio_Bancario"].ToString() : "N/A";
                                string concepto = r["Concepto"].ToString();

                                string tipoIngreso = "Efectivo";
                                if (esBanco)
                                {
                                    if (folio.StartsWith("pi_") || folio.StartsWith("cs_") || folio.StartsWith("ch_") || concepto.Contains("Stripe", StringComparison.OrdinalIgnoreCase))
                                        tipoIngreso = "Stripe";
                                    else
                                        tipoIngreso = "Transferencia";
                                }

                                string archivo = r["NombreArchivo"] != DBNull.Value ? r["NombreArchivo"].ToString() : "N/A";

                                if (esBanco)
                                {
                                    if (tipo == "Ingreso") pBancoIng += monto; else pBancoEgr += monto;
                                    listaBanco.Add(((DateTime)r["Fecha"], r["Concepto"].ToString(), "Caja", nomEvento, folio, usuario, monto, tipo, tipoIngreso, archivo));
                                }
                                else
                                {
                                    if (tipo == "Ingreso") pCajaIng += monto; else pCajaEgr += monto;
                                    listaCaja.Add(((DateTime)r["Fecha"], r["Concepto"].ToString(), "Caja", nomEvento, folio, usuario, monto, tipo, tipoIngreso, archivo));
                                }
                            }
                        }
                    }

                    // Cargar movimientos de causas/proyectos especiales
                    string sqlProyectos = $@"
                SELECT c.""Fecha"", c.""Tipo"", c.""Concepto"", c.""Monto"", c.""Movimiento_En_Banco"", c.""Folio_Bancario"", 
                       u.""NombreCompleto"", p.""Titulo"" AS ""NombreProyecto"" 
                FROM ""Fin_Proyectos_Caja"" c 
                LEFT JOIN ""Sist_Usuarios"" u ON c.""Id_Usuario"" = u.""Id_Usuario"" 
                LEFT JOIN ""Fin_Proyectos"" p ON c.""Id_Proyecto"" = p.""Id_Proyecto"" 
                WHERE " + string.Format(fFecha, "c.\"Fecha\"");
                    using (var cmd = new NpgsqlCommand(sqlProyectos, con))
                    {
                        cmd.Parameters.AddWithValue("@a", anioActual); if (mesActual != 0) cmd.Parameters.AddWithValue("@m", mesActual);
                        using (var r = cmd.ExecuteReader())
                        {
                            while (r.Read())
                            {
                                bool esBanco = (bool)r["Movimiento_En_Banco"];
                                string tipo = r["Tipo"].ToString();
                                decimal monto = (decimal)r["Monto"];
                                string nomProyecto = r["NombreProyecto"] != DBNull.Value ? "Causa: " + r["NombreProyecto"].ToString() : "Donación Proyecto";
                                string usuario = r["NombreCompleto"] != DBNull.Value ? r["NombreCompleto"].ToString() : "Desconocido";
                                string folio = r["Folio_Bancario"] != DBNull.Value ? r["Folio_Bancario"].ToString() : "N/A";
                                string concepto = r["Concepto"].ToString();

                                string tipoIngreso = "Efectivo";
                                if (esBanco)
                                {
                                    if (folio.StartsWith("pi_") || folio.StartsWith("cs_") || folio.StartsWith("ch_") || concepto.Contains("Stripe", StringComparison.OrdinalIgnoreCase))
                                        tipoIngreso = "Stripe";
                                    else
                                        tipoIngreso = "Transferencia";
                                }

                                string archivo = "N/A";

                                if (esBanco)
                                {
                                    if (tipo == "Ingreso") pBancoIng += monto; else pBancoEgr += monto;
                                    listaBanco.Add(((DateTime)r["Fecha"], r["Concepto"].ToString(), "Proyectos", nomProyecto, folio, usuario, monto, tipo, tipoIngreso, archivo));
                                }
                                else
                                {
                                    if (tipo == "Ingreso") pCajaIng += monto; else pCajaEgr += monto;
                                    listaCaja.Add(((DateTime)r["Fecha"], r["Concepto"].ToString(), "Proyectos", nomProyecto, folio, usuario, monto, tipo, tipoIngreso, archivo));
                                }
                            }
                        }
                    }

                    // Cargar inscripciones web de eventos
                    string sqlEvtWeb = $@"
                SELECT t.""Fecha_Intento"", t.""Total_Neto"", COALESCE(t.""Ref_Pasarela"", t.""External_Reference"") as ""Folio"", 
                       ec.""Titulo"" AS ""NombreEvento"", u.""NombreCompleto"", t.""EsTransferencia"", ra.""Titulo"" AS ""NombreArchivo""
                FROM ""Eventos_D_Transacciones"" t 
                JOIN ""Eventos_Catalogo"" ec ON t.""Id_Evento"" = ec.""Id_Evento"" 
                LEFT JOIN ""Sist_Usuarios"" u ON t.""Id_Usuario"" = u.""Id_Usuario""
                LEFT JOIN ""Rec_Archivos"" ra ON t.""Id_Archivo_Comprobante"" = ra.""Id_Archivo""
                WHERE t.""Estatus_Pago""='paid' AND t.""ValidadoXCaja""=TRUE AND t.""CuentaPrincipalBanco""=TRUE AND " + string.Format(fFecha, "t.\"Fecha_Intento\"");
                    using (var cmd = new NpgsqlCommand(sqlEvtWeb, con))
                    {
                        cmd.Parameters.AddWithValue("@a", anioActual); if (mesActual != 0) cmd.Parameters.AddWithValue("@m", mesActual);
                        using (var r = cmd.ExecuteReader())
                        {
                            while (r.Read())
                            {
                                string usuario = r["NombreCompleto"] != DBNull.Value ? r["NombreCompleto"].ToString() : "Sistema Web";
                                string folio = r["Folio"] != DBNull.Value ? r["Folio"].ToString() : "N/A";
                                string tipoIngreso = (bool)r["EsTransferencia"] ? "Transferencia" : "Stripe";
                                string archivo = r["NombreArchivo"] != DBNull.Value ? r["NombreArchivo"].ToString() : "N/A";

                                listaBanco.Add(((DateTime)r["Fecha_Intento"], "Inscripción Web", "Eventos", r["NombreEvento"].ToString(), folio, usuario, (decimal)r["Total_Neto"], "Ingreso", tipoIngreso, archivo));
                            }
                        }
                    }

                    // Cargar compras de boletos de rifas web
                    string sqlRifWeb = $@"
                SELECT r.""Fecha_Pago"", r.""Total_Neto"", COALESCE(r.""Ref_Pasarela_Id"", r.""Ref_Stripe"") as ""Folio"", 
                       ri.""Titulo"" AS ""NombreRifa"", u.""NombreCompleto"" 
                FROM ""Rifas_Ventas"" r 
                JOIN ""Rifas"" ri ON r.""Id_Rifa"" = ri.""Id_Rifa"" 
                LEFT JOIN ""Sist_Usuarios"" u ON r.""Id_Usuario_Comprador"" = u.""Id_Usuario""
                WHERE r.""Estado""='Pagado' AND r.""ValidadoXCaja""=TRUE AND r.""CuentaPrincipalBanco""=TRUE AND " + string.Format(fFecha, "r.\"Fecha_Pago\"");
                    using (var cmd = new NpgsqlCommand(sqlRifWeb, con))
                    {
                        cmd.Parameters.AddWithValue("@a", anioActual); if (mesActual != 0) cmd.Parameters.AddWithValue("@m", mesActual);
                        using (var r = cmd.ExecuteReader())
                        {
                            while (r.Read())
                            {
                                string usuario = r["NombreCompleto"] != DBNull.Value ? r["NombreCompleto"].ToString() : "Sistema Web";
                                string folio = r["Folio"] != DBNull.Value ? r["Folio"].ToString() : "N/A";
                                listaBanco.Add(((DateTime)r["Fecha_Pago"], "Boleto Web", "Rifas", "Sorteo: " + r["NombreRifa"].ToString(), folio, usuario, (decimal)r["Total_Neto"], "Ingreso", "Stripe", "N/A"));
                            }
                        }
                    }

                    // Cargar pedidos completados de la tienda oficial
                    string sqlTndWeb = $@"
                SELECT t.""Fecha_Solicitud"", t.""Total_Neto"", COALESCE(t.""Ref_Pasarela"", t.""External_Reference"") as ""Folio"",
                       u.""NombreCompleto"" 
                FROM ""Tienda_Pedidos"" t 
                LEFT JOIN ""Sist_Usuarios"" u ON t.""Id_Usuario_Solicita"" = u.""Id_Usuario""
                WHERE t.""Id_Estatus"" >= 30 AND t.""ValidadoXCaja""=TRUE AND t.""CuentaPrincipalBanco""=TRUE AND " + string.Format(fFecha, "t.\"Fecha_Solicitud\"");
                    using (var cmd = new NpgsqlCommand(sqlTndWeb, con))
                    {
                        cmd.Parameters.AddWithValue("@a", anioActual); if (mesActual != 0) cmd.Parameters.AddWithValue("@m", mesActual);
                        using (var r = cmd.ExecuteReader())
                        {
                            while (r.Read())
                            {
                                string usuario = r["NombreCompleto"] != DBNull.Value ? r["NombreCompleto"].ToString() : "Sistema Web";
                                string folio = r["Folio"] != DBNull.Value ? r["Folio"].ToString() : "N/A";
                                listaBanco.Add(((DateTime)r["Fecha_Solicitud"], "Pedido Tienda", "Tienda", "Tienda Oficial", folio, usuario, (decimal)r["Total_Neto"], "Ingreso", "Stripe", "N/A"));
                            }
                        }
                    }
                }

                // ========================================================================
                // 2. CREACIÓN DE LAS PESTAÑAS DEL ARCHIVO EXCEL
                // ========================================================================

                // ---------------- PESTAÑA 1: RESUMEN GENERAL ----------------
                var wsResumen = workbook.Worksheets.Add("Resumen");
                wsResumen.ShowGridLines = false;
                wsResumen.Style.Font.FontName = "Calibri";
                wsResumen.Style.Font.FontSize = 11;

                wsResumen.Cell("B2").Value = "REPORTE DE TESORERÍA INTEGRAL";
                wsResumen.Cell("B2").Style.Font.FontSize = 16;
                wsResumen.Cell("B2").Style.Font.Bold = true;
                wsResumen.Cell("B2").Style.Font.FontColor = XLColor.PrussianBlue;
                wsResumen.Cell("B3").Value = $"Periodo Reportado: {textoPeriodo}";
                wsResumen.Cell("B4").Value = $"Generado: {DateTime.Now:dd/MM/yyyy HH:mm}";

                int rowResumen = 6;

                var titleBanco = wsResumen.Range(rowResumen, 2, rowResumen, 6);
                titleBanco.Merge().Value = "RESUMEN CUENTAS BANCARIAS Y DIGITALES";
                titleBanco.Style.Font.Bold = true;
                titleBanco.Style.Font.FontColor = XLColor.White;
                titleBanco.Style.Fill.BackgroundColor = XLColor.FromHtml("#1565C0");
                titleBanco.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                rowResumen += 2;

                wsResumen.Cell(rowResumen, 2).Value = "SALDO REAL DISPONIBLE EN BANCO (HOY):";
                wsResumen.Cell(rowResumen, 2).Style.Font.Bold = true;
                wsResumen.Range(rowResumen, 2, rowResumen, 4).Merge();
                wsResumen.Cell(rowResumen, 6).Value = saldoBancoReal;
                wsResumen.Cell(rowResumen, 6).Style.NumberFormat.Format = "$ #,##0.00";
                wsResumen.Cell(rowResumen, 6).Style.Font.Bold = true;
                wsResumen.Cell(rowResumen, 6).Style.Fill.BackgroundColor = XLColor.FromHtml("#E3F2FD");
                rowResumen += 2;

                void AddRowResumen(string desc, decimal monto, bool esResta = false)
                {
                    wsResumen.Cell(rowResumen, 2).Value = desc;
                    wsResumen.Range(rowResumen, 2, rowResumen, 4).Merge();
                    wsResumen.Cell(rowResumen, 6).Value = monto;
                    wsResumen.Cell(rowResumen, 6).Style.NumberFormat.Format = "$ #,##0.00";
                    if (esResta) wsResumen.Cell(rowResumen, 6).Style.Font.FontColor = XLColor.Red;
                    rowResumen++;
                }

                AddRowResumen("+ Ingresos por Eventos (Web)", pWebEventos);
                AddRowResumen("+ Ingresos por Rifas (Web)", pWebRifas);
                AddRowResumen("+ Ingresos por Tienda (Web)", pWebTienda);
                AddRowResumen("+ Depósitos Manuales / Proyectos", pBancoIng);
                AddRowResumen("- Gastos / Egresos (Transferencias/Tarjeta)", pBancoEgr, true);

                wsResumen.Cell(rowResumen, 5).Value = "Flujo Neto Banco Periodo:";
                wsResumen.Cell(rowResumen, 5).Style.Font.Bold = true;
                wsResumen.Cell(rowResumen, 6).Value = (pWebEventos + pWebRifas + pWebTienda + pBancoIng) - pBancoEgr;
                wsResumen.Cell(rowResumen, 6).Style.NumberFormat.Format = "$ #,##0.00";
                wsResumen.Cell(rowResumen, 6).Style.Border.TopBorder = XLBorderStyleValues.Thin;
                rowResumen += 3;

                var titleCaja = wsResumen.Range(rowResumen, 2, rowResumen, 6);
                titleCaja.Merge().Value = "RESUMEN CAJA CHICA (EFECTIVO FÍSICO)";
                titleCaja.Style.Font.Bold = true;
                titleCaja.Style.Font.FontColor = XLColor.White;
                titleCaja.Style.Fill.BackgroundColor = XLColor.FromHtml("#2E7D32");
                titleCaja.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                rowResumen += 2;

                wsResumen.Cell(rowResumen, 2).Value = "DINERO FÍSICO EN CAJÓN (HOY):";
                wsResumen.Cell(rowResumen, 2).Style.Font.Bold = true;
                wsResumen.Range(rowResumen, 2, rowResumen, 4).Merge();
                wsResumen.Cell(rowResumen, 6).Value = saldoCajaReal;
                wsResumen.Cell(rowResumen, 6).Style.NumberFormat.Format = "$ #,##0.00";
                wsResumen.Cell(rowResumen, 6).Style.Font.Bold = true;
                wsResumen.Cell(rowResumen, 6).Style.Fill.BackgroundColor = XLColor.FromHtml("#E8F5E9");
                rowResumen += 2;

                AddRowResumen("+ Entradas de Efectivo (Manuales / Proyectos)", pCajaIng);
                AddRowResumen("- Salidas de Efectivo (Gastos/Compras)", pCajaEgr, true);

                wsResumen.Cell(rowResumen, 5).Value = "Flujo Neto Caja Periodo:";
                wsResumen.Cell(rowResumen, 5).Style.Font.Bold = true;
                wsResumen.Cell(rowResumen, 6).Value = pCajaIng - pCajaEgr;
                wsResumen.Cell(rowResumen, 6).Style.NumberFormat.Format = "$ #,##0.00";
                wsResumen.Cell(rowResumen, 6).Style.Border.TopBorder = XLBorderStyleValues.Thin;

                wsResumen.Column(2).Width = 20;
                wsResumen.Column(3).Width = 20;
                wsResumen.Column(4).Width = 20;
                wsResumen.Column(5).Width = 25;
                wsResumen.Column(6).Width = 18;

                // ---------------- PESTAÑA 2: ENTRADAS BANCO ----------------
                var wsEntradasBanco = workbook.Worksheets.Add("Entradas Banco");
                wsEntradasBanco.ShowGridLines = false;
                wsEntradasBanco.Style.Font.FontName = "Calibri";
                wsEntradasBanco.Style.Font.FontSize = 11;

                wsEntradasBanco.Cell("A1").Value = "INGRESOS BANCARIOS Y DIGITALES";
                wsEntradasBanco.Cell("A1").Style.Font.Bold = true;
                wsEntradasBanco.Cell("A1").Style.Font.FontSize = 14;
                wsEntradasBanco.Cell("A2").Value = $"Periodo Detallado: {textoPeriodo}";

                int rEB = 4;
                string[] headersEB = { "Fecha", "Concepto", "Origen", "Asociación (Evento / Proyecto / Causa)", "Folio / Stripe Key", "Usuario / Comprador", "Monto", "Tipo de Ingreso", "Comprobante" };
                for (int i = 0; i < headersEB.Length; i++)
                {
                    wsEntradasBanco.Cell(rEB, i + 1).Value = headersEB[i];
                    wsEntradasBanco.Cell(rEB, i + 1).Style.Font.Bold = true;
                    wsEntradasBanco.Cell(rEB, i + 1).Style.Font.FontColor = XLColor.White;
                    wsEntradasBanco.Cell(rEB, i + 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#1565C0");
                }
                rEB++;

                foreach (var item in listaBanco.Where(x => x.Tipo == "Ingreso").OrderByDescending(x => x.Fecha))
                {
                    wsEntradasBanco.Cell(rEB, 1).Value = item.Fecha.ToString("dd/MM/yyyy HH:mm");
                    wsEntradasBanco.Cell(rEB, 2).Value = item.Concepto;
                    wsEntradasBanco.Cell(rEB, 3).Value = item.Origen;
                    wsEntradasBanco.Cell(rEB, 4).Value = item.Asociacion;
                    if (item.Asociacion != "N/A") wsEntradasBanco.Cell(rEB, 4).Style.Font.Italic = true;
                    wsEntradasBanco.Cell(rEB, 5).Value = item.Folio ?? "N/A";
                    wsEntradasBanco.Cell(rEB, 6).Value = item.Usuario;
                    wsEntradasBanco.Cell(rEB, 7).Value = item.Monto;
                    wsEntradasBanco.Cell(rEB, 7).Style.NumberFormat.Format = "$ #,##0.00";
                    wsEntradasBanco.Cell(rEB, 8).Value = item.TipoIngreso;
                    wsEntradasBanco.Cell(rEB, 9).Value = item.Comprobante;
                    rEB++;
                }

                wsEntradasBanco.Column(1).Width = 18;
                wsEntradasBanco.Column(2).Width = 35;
                wsEntradasBanco.Column(3).Width = 15;
                wsEntradasBanco.Column(4).Width = 35;
                wsEntradasBanco.Column(5).Width = 35;
                wsEntradasBanco.Column(6).Width = 25;
                wsEntradasBanco.Column(7).Width = 15;
                wsEntradasBanco.Column(8).Width = 20;
                wsEntradasBanco.Column(9).Width = 35;
                wsEntradasBanco.SheetView.FreezeRows(4);

                // ---------------- PESTAÑA 3: SALIDAS BANCO ----------------
                var wsSalidasBanco = workbook.Worksheets.Add("Salidas Banco");
                wsSalidasBanco.ShowGridLines = false;
                wsSalidasBanco.Style.Font.FontName = "Calibri";
                wsSalidasBanco.Style.Font.FontSize = 11;

                wsSalidasBanco.Cell("A1").Value = "EGRESOS BANCARIOS Y DIGITALES";
                wsSalidasBanco.Cell("A1").Style.Font.Bold = true;
                wsSalidasBanco.Cell("A1").Style.Font.FontSize = 14;
                wsSalidasBanco.Cell("A2").Value = $"Periodo Detallado: {textoPeriodo}";

                int rSB = 4;
                string[] headersSB = { "Fecha", "Concepto", "Origen", "Asociación (Evento / Proyecto / Causa)", "Folio / Stripe Key", "Usuario / Comprador", "Monto" };
                for (int i = 0; i < headersSB.Length; i++)
                {
                    wsSalidasBanco.Cell(rSB, i + 1).Value = headersSB[i];
                    wsSalidasBanco.Cell(rSB, i + 1).Style.Font.Bold = true;
                    wsSalidasBanco.Cell(rSB, i + 1).Style.Font.FontColor = XLColor.White;
                    wsSalidasBanco.Cell(rSB, i + 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#B71C1C");
                }
                rSB++;

                foreach (var item in listaBanco.Where(x => x.Tipo == "Egreso").OrderByDescending(x => x.Fecha))
                {
                    wsSalidasBanco.Cell(rSB, 1).Value = item.Fecha.ToString("dd/MM/yyyy HH:mm");
                    wsSalidasBanco.Cell(rSB, 2).Value = item.Concepto;
                    wsSalidasBanco.Cell(rSB, 3).Value = item.Origen;
                    wsSalidasBanco.Cell(rSB, 4).Value = item.Asociacion;
                    if (item.Asociacion != "N/A") wsSalidasBanco.Cell(rSB, 4).Style.Font.Italic = true;
                    wsSalidasBanco.Cell(rSB, 5).Value = item.Folio ?? "N/A";
                    wsSalidasBanco.Cell(rSB, 6).Value = item.Usuario;
                    wsSalidasBanco.Cell(rSB, 7).Value = item.Monto;
                    wsSalidasBanco.Cell(rSB, 7).Style.NumberFormat.Format = "$ #,##0.00";
                    rSB++;
                }

                wsSalidasBanco.Column(1).Width = 18;
                wsSalidasBanco.Column(2).Width = 35;
                wsSalidasBanco.Column(3).Width = 15;
                wsSalidasBanco.Column(4).Width = 35;
                wsSalidasBanco.Column(5).Width = 35;
                wsSalidasBanco.Column(6).Width = 25;
                wsSalidasBanco.Column(7).Width = 15;
                wsSalidasBanco.SheetView.FreezeRows(4);

                // ---------------- PESTAÑA 4: ENTRADAS CAJA CHICA ----------------
                var wsEntradasCaja = workbook.Worksheets.Add("Entradas Caja Chica");
                wsEntradasCaja.ShowGridLines = false;
                wsEntradasCaja.Style.Font.FontName = "Calibri";
                wsEntradasCaja.Style.Font.FontSize = 11;

                wsEntradasCaja.Cell("A1").Value = "INGRESOS CAJA CHICA (EFECTIVO)";
                wsEntradasCaja.Cell("A1").Style.Font.Bold = true;
                wsEntradasCaja.Cell("A1").Style.Font.FontSize = 14;
                wsEntradasCaja.Cell("A2").Value = $"Periodo Detallado: {textoPeriodo}";

                int rEC = 4;
                string[] headersEC = { "Fecha", "Concepto", "Origen", "Asociación (Evento / Proyecto / Causa)", "Folio / Stripe Key", "Usuario / Comprador", "Monto" };
                for (int i = 0; i < headersEC.Length; i++)
                {
                    wsEntradasCaja.Cell(rEC, i + 1).Value = headersEC[i];
                    wsEntradasCaja.Cell(rEC, i + 1).Style.Font.Bold = true;
                    wsEntradasCaja.Cell(rEC, i + 1).Style.Font.FontColor = XLColor.White;
                    wsEntradasCaja.Cell(rEC, i + 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#2E7D32");
                }
                rEC++;

                foreach (var item in listaCaja.Where(x => x.Tipo == "Ingreso").OrderByDescending(x => x.Fecha))
                {
                    wsEntradasCaja.Cell(rEC, 1).Value = item.Fecha.ToString("dd/MM/yyyy HH:mm");
                    wsEntradasCaja.Cell(rEC, 2).Value = item.Concepto;
                    wsEntradasCaja.Cell(rEC, 3).Value = item.Origen;
                    wsEntradasCaja.Cell(rEC, 4).Value = item.Asociacion;
                    if (item.Asociacion != "N/A") wsEntradasCaja.Cell(rEC, 4).Style.Font.Italic = true;
                    wsEntradasCaja.Cell(rEC, 5).Value = item.Folio ?? "N/A";
                    wsEntradasCaja.Cell(rEC, 6).Value = item.Usuario;
                    wsEntradasCaja.Cell(rEC, 7).Value = item.Monto;
                    wsEntradasCaja.Cell(rEC, 7).Style.NumberFormat.Format = "$ #,##0.00";
                    rEC++;
                }

                wsEntradasCaja.Column(1).Width = 18;
                wsEntradasCaja.Column(2).Width = 35;
                wsEntradasCaja.Column(3).Width = 15;
                wsEntradasCaja.Column(4).Width = 35;
                wsEntradasCaja.Column(5).Width = 35;
                wsEntradasCaja.Column(6).Width = 25;
                wsEntradasCaja.Column(7).Width = 15;
                wsEntradasCaja.SheetView.FreezeRows(4);

                // ---------------- PESTAÑA 5: SALIDAS CAJA CHICA ----------------
                var wsSalidasCaja = workbook.Worksheets.Add("Salidas Caja Chica");
                wsSalidasCaja.ShowGridLines = false;
                wsSalidasCaja.Style.Font.FontName = "Calibri";
                wsSalidasCaja.Style.Font.FontSize = 11;

                wsSalidasCaja.Cell("A1").Value = "EGRESOS CAJA CHICA (EFECTIVO)";
                wsSalidasCaja.Cell("A1").Style.Font.Bold = true;
                wsSalidasCaja.Cell("A1").Style.Font.FontSize = 14;
                wsSalidasCaja.Cell("A2").Value = $"Periodo Detallado: {textoPeriodo}";

                int rSC = 4;
                string[] headersSC = { "Fecha", "Concepto", "Origen", "Asociación (Evento / Proyecto / Causa)", "Folio / Stripe Key", "Usuario / Comprador", "Monto" };
                for (int i = 0; i < headersSC.Length; i++)
                {
                    wsSalidasCaja.Cell(rSC, i + 1).Value = headersSC[i];
                    wsSalidasCaja.Cell(rSC, i + 1).Style.Font.Bold = true;
                    wsSalidasCaja.Cell(rSC, i + 1).Style.Font.FontColor = XLColor.White;
                    wsSalidasCaja.Cell(rSC, i + 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#B71C1C");
                }
                rSC++;

                foreach (var item in listaCaja.Where(x => x.Tipo == "Egreso").OrderByDescending(x => x.Fecha))
                {
                    wsSalidasCaja.Cell(rSC, 1).Value = item.Fecha.ToString("dd/MM/yyyy HH:mm");
                    wsSalidasCaja.Cell(rSC, 2).Value = item.Concepto;
                    wsSalidasCaja.Cell(rSC, 3).Value = item.Origen;
                    wsSalidasCaja.Cell(rSC, 4).Value = item.Asociacion;
                    if (item.Asociacion != "N/A") wsSalidasCaja.Cell(rSC, 4).Style.Font.Italic = true;
                    wsSalidasCaja.Cell(rSC, 5).Value = item.Folio ?? "N/A";
                    wsSalidasCaja.Cell(rSC, 6).Value = item.Usuario;
                    wsSalidasCaja.Cell(rSC, 7).Value = item.Monto;
                    wsSalidasCaja.Cell(rSC, 7).Style.NumberFormat.Format = "$ #,##0.00";
                    rSC++;
                }

                wsSalidasCaja.Column(1).Width = 18;
                wsSalidasCaja.Column(2).Width = 35;
                wsSalidasCaja.Column(3).Width = 15;
                wsSalidasCaja.Column(4).Width = 35;
                wsSalidasCaja.Column(5).Width = 35;
                wsSalidasCaja.Column(6).Width = 25;
                wsSalidasCaja.Column(7).Width = 15;
                wsSalidasCaja.SheetView.FreezeRows(4);

                // ========================================================================
                // 3. ENVÍO DEL ARCHIVO EXCEL TERMINADO
                // ========================================================================
                using (var stream = new MemoryStream())
                {
                    workbook.SaveAs(stream);
                    return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"Reporte_Tesoreria_Estructurado_{nombreMes}_{anioActual}.xlsx");
                }
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ActualizarEventoGasto(int idMovimientoDetalle, int? idEventoDetalle)
        {
            if (!User.TienePermiso(Modulo, PermisoEditar))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permiso para editar movimientos.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // Validar que el movimiento exista y cumpla las reglas (Egreso + Banco)
                    string sqlCheck = @"SELECT ""Tipo"", ""Movimiento_En_Banco"" FROM ""Fin_Caja"" WHERE ""Id_Movimiento"" = @id";
                    using (var cmdCheck = new NpgsqlCommand(sqlCheck, conexion))
                    {
                        cmdCheck.Parameters.AddWithValue("@id", idMovimientoDetalle);
                        using (var reader = await cmdCheck.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                string tipo = reader["Tipo"].ToString();
                                bool esBanco = (bool)reader["Movimiento_En_Banco"];

                                if (tipo != "Egreso" || !esBanco)
                                {
                                    MostrarMensaje("Restricción", "Solo los gastos bancarios pueden asociarse a eventos.", TipoMensaje.Error);
                                    return RedirectToAction("Index");
                                }
                            }
                            else
                            {
                                MostrarMensaje("Error", "Movimiento no encontrado.", TipoMensaje.Error);
                                return RedirectToAction("Index");
                            }
                        }
                    }

                    // Actualizar solo el evento
                    string sqlUpdate = @"UPDATE ""Fin_Caja"" SET ""Id_Evento"" = @idEv WHERE ""Id_Movimiento"" = @id";
                    using (var cmdUpd = new NpgsqlCommand(sqlUpdate, conexion))
                    {
                        cmdUpd.Parameters.AddWithValue("@id", idMovimientoDetalle);
                        cmdUpd.Parameters.AddWithValue("@idEv", idEventoDetalle ?? (object)DBNull.Value);
                        await cmdUpd.ExecuteNonQueryAsync();
                    }

                    int idLogueado = int.Parse(User.FindFirst("IdUsuario")?.Value ?? "0");
                    string ipUsuario = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";
                    await Funciones.RegistrarBitacora(conexion, idLogueado, Modulo, Parametros.AccionesBitacora.Editar, $"Actualizó el evento asociado del movimiento #{idMovimientoDetalle}", ipUsuario);

                    MostrarMensaje("Actualizado", "La vinculación con el evento se guardó correctamente.", TipoMensaje.Exito);
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Index");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Guardar(MovimientoCaja mov)
        {
            bool bNuevo = mov.Id_Movimiento == 0;
            var idClaim = User.FindFirst("IdUsuario");
            if (idClaim == null) return RedirectToAction("Index", "Login");

            int idUsuario = int.Parse(idClaim.Value);
            string ipUsuario = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";

            if (mov.Id_Evento.HasValue && (mov.Tipo != "Egreso" || !mov.Movimiento_En_Banco))
            {
                MostrarMensaje("Operación Inválida", "Solamente los gastos provenientes de banco pueden asociarse a un evento.", TipoMensaje.Error);
                return RedirectToAction("Index", new { mes = mov.Fecha.Month, anio = mov.Fecha.Year });
            }
            // 1. VALIDACIÓN DE PERMISOS
            if (bNuevo)
            {
                if (!User.TienePermiso(Modulo, PermisoCrear))
                {
                    MostrarMensaje("Acceso Denegado", "No tienes permiso para registrar movimientos.", TipoMensaje.Error);
                    return RedirectToAction("Index");
                }
            }
            else
            {
                if (!User.TienePermiso(Modulo, PermisoEditar))
                {
                    MostrarMensaje("Acceso Denegado", "No tienes permiso para modificar movimientos.", TipoMensaje.Error);
                    return RedirectToAction("Index");
                }

                using (var conCheck = new NpgsqlConnection(_cadenaConexion))
                {
                    await conCheck.OpenAsync();
                    var cmdCheck = new NpgsqlCommand("SELECT \"Id_Usuario\" FROM \"Fin_Caja\" WHERE \"Id_Movimiento\" = @id", conCheck);
                    cmdCheck.Parameters.AddWithValue("@id", mov.Id_Movimiento);
                    var idCreadorObj = await cmdCheck.ExecuteScalarAsync();

                    if (idCreadorObj != null && (int)idCreadorObj != idUsuario)
                    {
                        MostrarMensaje("Acceso Denegado", "Solo puedes editar registros creados por ti.", TipoMensaje.Error);
                        return RedirectToAction("Index");
                    }
                }
            }

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // --- VALIDACIÓN DE SALDO PERSONAL ---
                    if (!mov.Movimiento_En_Banco && mov.Tipo == "Egreso")
                    {
                        string sqlSaldo = @"
                SELECT 
                (COALESCE((SELECT SUM(""Monto"") FROM ""Fin_Caja"" WHERE ""Id_Usuario"" = @uid AND ""Tipo"" = 'Ingreso' AND ""Movimiento_En_Banco"" = FALSE), 0)
                - COALESCE((SELECT SUM(""Monto"") FROM ""Fin_Caja"" WHERE ""Id_Usuario"" = @uid AND ""Tipo"" = 'Egreso' AND ""Movimiento_En_Banco"" = FALSE AND ""Id_Movimiento"" != @idEdit), 0)
                + COALESCE((SELECT SUM(""Monto"") FROM ""Fin_Traspasos"" WHERE ""Id_Usuario_Destino"" = @uid AND ""Estado"" = 'ACE'), 0)
                - COALESCE((SELECT SUM(""Monto"") FROM ""Fin_Traspasos"" WHERE ""Id_Usuario_Origen"" = @uid AND ""Estado"" = 'ACE'), 0))";

                        using (var cmdS = new NpgsqlCommand(sqlSaldo, conexion))
                        {
                            cmdS.Parameters.AddWithValue("@uid", idUsuario);
                            cmdS.Parameters.AddWithValue("@idEdit", mov.Id_Movimiento);
                            decimal saldoDisp = Convert.ToDecimal(await cmdS.ExecuteScalarAsync());

                            if (mov.Monto > saldoDisp)
                            {
                                MostrarMensaje("Fondos Insuficientes", $"Tu saldo físico actual es de {saldoDisp:C}. No puedes registrar un gasto de {mov.Monto:C}.", TipoMensaje.Error);
                                return RedirectToAction("Index");
                            }
                        }
                    }
                    // --- VALIDACIÓN DE SALDO EN BANCO ---
                    else if (mov.Movimiento_En_Banco && mov.Tipo == "Egreso")
                    {
                        string sqlSaldoBanco = @"
                SELECT (
                    COALESCE((SELECT SUM(""Total_Neto"") FROM ""Eventos_D_Transacciones"" WHERE ""Estatus_Pago"" = 'paid'), 0) +
                    COALESCE((SELECT SUM(""Total_Neto"") FROM ""Rifas_Ventas"" WHERE ""Estado"" = 'Pagado'), 0) +
                    COALESCE((SELECT SUM(""Total_Neto"") FROM ""Tienda_Pedidos"" WHERE ""Id_Estatus"" >= 30), 0) +
                    COALESCE((SELECT SUM(""Monto"") FROM ""Fin_Caja"" WHERE ""Tipo"" = 'Ingreso' AND ""Movimiento_En_Banco"" = TRUE), 0) -
                    COALESCE((SELECT SUM(""Monto"") FROM ""Fin_Caja"" WHERE ""Tipo"" = 'Egreso' AND ""Movimiento_En_Banco"" = TRUE AND ""Id_Movimiento"" != @idEdit), 0)
                )";

                        using (var cmdB = new NpgsqlCommand(sqlSaldoBanco, conexion))
                        {
                            cmdB.Parameters.AddWithValue("@idEdit", mov.Id_Movimiento);
                            decimal saldoBancoDisp = Convert.ToDecimal(await cmdB.ExecuteScalarAsync());

                            if (mov.Monto > saldoBancoDisp)
                            {
                                MostrarMensaje("Fondos Insuficientes en Banco", $"El saldo actual disponible en el banco es de {saldoBancoDisp:C}. No puedes registrar un gasto bancario de {mov.Monto:C}.", TipoMensaje.Error);
                                return RedirectToAction("Index");
                            }
                        }
                    }

                    using (var transaccion = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            int? idArchivoParaBorrar = null;

                            if (!bNuevo && mov.ArchivoComprobante != null && mov.ArchivoComprobante.Length > 0)
                            {
                                string sqlGetOld = @"SELECT ""Id_Archivo"" FROM ""Fin_Caja"" WHERE ""Id_Movimiento"" = @id";
                                using (var cmdOld = new NpgsqlCommand(sqlGetOld, conexion, transaccion))
                                {
                                    cmdOld.Parameters.AddWithValue("@id", mov.Id_Movimiento);
                                    var result = await cmdOld.ExecuteScalarAsync();
                                    if (result != null && result != DBNull.Value)
                                    {
                                        idArchivoParaBorrar = (int)result;
                                    }
                                }
                            }

                            if (mov.ArchivoComprobante != null && mov.ArchivoComprobante.Length > 0)
                            {
                                using (var memoryStream = new MemoryStream())
                                {
                                    await mov.ArchivoComprobante.CopyToAsync(memoryStream);
                                    byte[] bytes = memoryStream.ToArray();

                                    string sqlArchivo = @"INSERT INTO ""Rec_Archivos"" 
                            (""Titulo"", ""Descripcion"", ""Tipo"", ""Contenido_Binario"", ""Descargas"", ""Origen"", ""Fecha_Creacion"", ""Id_Usuario_Carga"")
                            VALUES (@titulo, 'Comprobante de Caja', @mime, @bytes, 0, 'Caja', NOW(), @uid)
                            RETURNING ""Id_Archivo""";

                                    using (var cmdFile = new NpgsqlCommand(sqlArchivo, conexion, transaccion))
                                    {
                                        cmdFile.Parameters.AddWithValue("@titulo", mov.ArchivoComprobante.FileName);
                                        cmdFile.Parameters.AddWithValue("@mime", mov.ArchivoComprobante.ContentType);
                                        cmdFile.Parameters.AddWithValue("@bytes", bytes);
                                        cmdFile.Parameters.AddWithValue("@uid", idUsuario);
                                        mov.Id_Archivo = (int)await cmdFile.ExecuteScalarAsync();
                                    }
                                }
                            }

                            string sql = "";
                            if (bNuevo)
                            {
                                sql = @"INSERT INTO ""Fin_Caja"" (""Concepto"", ""Monto"", ""Tipo"", ""Fecha"", ""Id_Usuario"", ""Id_Archivo"", ""Movimiento_En_Banco"", ""Folio_Bancario"", ""Id_Evento"")
                                VALUES (@c, @m, @t, @f, @u, @a, @banco, @folio, @ev) 
                                RETURNING ""Id_Movimiento""";
                            }
                            else
                            {
                                decimal montoAnt = 0;
                                string conceptoAnt = ""; 
                                string tipoAnt = "";

                                string sqlGetOld = @"SELECT ""Monto"", ""Concepto"", ""Tipo"" FROM ""Fin_Caja"" WHERE ""Id_Movimiento"" = @id";
                                using (var cmdOld = new NpgsqlCommand(sqlGetOld, conexion, transaccion))
                                {
                                    cmdOld.Parameters.AddWithValue("@id", mov.Id_Movimiento);
                                    using (var rOld = await cmdOld.ExecuteReaderAsync())
                                    {
                                        if (await rOld.ReadAsync())
                                        {
                                            montoAnt = (decimal)rOld["Monto"];
                                            conceptoAnt = rOld["Concepto"].ToString();
                                            tipoAnt = rOld["Tipo"].ToString();
                                        }
                                    }
                                }

                                bool huboCambio = (montoAnt != mov.Monto || conceptoAnt != mov.Concepto || tipoAnt != mov.Tipo);

                                if (huboCambio)
                                {
                                    string sqlHist = @"INSERT INTO ""Fin_Caja_Historial"" 
        (""Id_Movimiento"", ""Monto_Anterior"", ""Monto_Nuevo"", 
         ""Concepto_Anterior"", ""Concepto_Nuevo"", 
         ""Tipo_Anterior"", ""Tipo_Nuevo"", ""Id_Usuario_Editor"")
        VALUES (@id, @ma, @mn, @ca, @cn, @ta, @tn, @ue)";

                                    using (var cmdH = new NpgsqlCommand(sqlHist, conexion, transaccion))
                                    {
                                        cmdH.Parameters.AddWithValue("@id", mov.Id_Movimiento);
                                        cmdH.Parameters.AddWithValue("@ma", montoAnt);
                                        cmdH.Parameters.AddWithValue("@mn", mov.Monto);
                                        cmdH.Parameters.AddWithValue("@ca", conceptoAnt);
                                        cmdH.Parameters.AddWithValue("@cn", mov.Concepto);
                                        cmdH.Parameters.AddWithValue("@ta", tipoAnt);
                                        cmdH.Parameters.AddWithValue("@tn", mov.Tipo);
                                        cmdH.Parameters.AddWithValue("@ue", idUsuario);
                                        await cmdH.ExecuteNonQueryAsync();
                                    }
                                }

                                sql = @"UPDATE ""Fin_Caja"" 
                                SET ""Concepto""=@c, ""Monto""=@m, ""Tipo""=@t, ""Fecha""=@f, ""Movimiento_En_Banco""=@banco, ""Editado""=TRUE, ""Folio_Bancario""=@folio,
                                    ""Id_Archivo"" = CASE WHEN @a IS NULL THEN ""Id_Archivo"" ELSE @a END,
                                    ""Id_Evento"" = @ev
                                WHERE ""Id_Movimiento""=@id";
                            }

                            using (var cmd = new NpgsqlCommand(sql, conexion, transaccion))
                            {
                                cmd.Parameters.AddWithValue("@c", mov.Concepto);
                                cmd.Parameters.AddWithValue("@m", mov.Monto);
                                cmd.Parameters.AddWithValue("@t", mov.Tipo);
                                cmd.Parameters.AddWithValue("@f", mov.Fecha);
                                cmd.Parameters.AddWithValue("@u", idUsuario);
                                cmd.Parameters.AddWithValue("@banco", mov.Movimiento_En_Banco);
                                cmd.Parameters.AddWithValue("@folio", string.IsNullOrWhiteSpace(mov.FolioBancario) ? (object)DBNull.Value : mov.FolioBancario);
                                cmd.Parameters.AddWithValue("@ev", mov.Id_Evento ?? (object)DBNull.Value); // <--- NUEVO CAMPO

                                var paramArchivo = new NpgsqlParameter("@a", NpgsqlTypes.NpgsqlDbType.Integer);
                                if (mov.Id_Archivo != null) paramArchivo.Value = mov.Id_Archivo;
                                else paramArchivo.Value = DBNull.Value;
                                cmd.Parameters.Add(paramArchivo);

                                if (!bNuevo) 
                                {
                                    cmd.Parameters.AddWithValue("@id", mov.Id_Movimiento);
                                    await cmd.ExecuteNonQueryAsync(); 
                                }
                                else 
                                { 
                                    mov.Id_Movimiento = (int)await cmd.ExecuteScalarAsync();
                                }
                            }

                            // --- PASO D: ELIMINAR FISICAMENTE EL ARCHIVO ANTERIOR ---
                            // Lo hacemos después del UPDATE para evitar errores de llave foránea
                            if (idArchivoParaBorrar.HasValue)
                            {
                                // Verificamos que el ID nuevo no sea igual al viejo (por seguridad)
                                if (mov.Id_Archivo != idArchivoParaBorrar.Value)
                                {
                                    string sqlDelFile = @"DELETE FROM ""Rec_Archivos"" WHERE ""Id_Archivo"" = @oldId";
                                    using (var cmdDel = new NpgsqlCommand(sqlDelFile, conexion, transaccion))
                                    {
                                        cmdDel.Parameters.AddWithValue("@oldId", idArchivoParaBorrar.Value);
                                        await cmdDel.ExecuteNonQueryAsync();
                                    }
                                }
                            }

                            string metodoTexto = mov.Movimiento_En_Banco ? "BANCO" : "EFECTIVO";
                            string detalle = $"#{mov.Id_Movimiento} ({metodoTexto}): {mov.Tipo} de ${mov.Monto} - {mov.Concepto}";
                            await Funciones.RegistrarBitacora(conexion, idUsuario, Modulo, bNuevo ? Parametros.AccionesBitacora.Crear : Parametros.AccionesBitacora.Editar, detalle, ipUsuario, transaccion);

                            await transaccion.CommitAsync();
                            MostrarMensaje("Operación Exitosa", "El movimiento ha sido guardado correctamente.", TipoMensaje.Exito);
                        }
                        catch (Exception)
                        {
                            await transaccion.RollbackAsync(); 
                            throw;
                        }
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error del Sistema", "No se pudo guardar: " + ex.Message, TipoMensaje.Error); }

            return RedirectToAction("Index", new { mes = mov.Fecha.Month, anio = mov.Fecha.Year });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Eliminar(int idEliminar)
        {
            var idClaim = User.FindFirst("IdUsuario");
            int idUsuario = int.Parse(idClaim.Value);
            string ipUsuario = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";

            bool esAdmin = User.TienePermiso(Modulo, PermisoAdmin);
            bool puedeBorrar = User.TienePermiso(Modulo, PermisoBorrar);

            if (!esAdmin && !puedeBorrar)
            {
                MostrarMensaje("Permiso Insuficiente", "No tienes nivel para eliminar registros.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    DateTime fechaMovimiento;
                    string conceptoBorrardo = "";
                    decimal montoBorrado = 0;
                    int? idArchivoEliminar = null;
                    int idCreador = 0;

                    string sqlCheck = "SELECT \"Fecha\", \"Concepto\", \"Monto\", \"Id_Archivo\", \"Id_Usuario\" FROM \"Fin_Caja\" WHERE \"Id_Movimiento\" = @id";
                    using (var cmdCheck = new NpgsqlCommand(sqlCheck, conexion))
                    {
                        cmdCheck.Parameters.AddWithValue("@id", idEliminar);
                        using (var reader = await cmdCheck.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                fechaMovimiento = (DateTime)reader["Fecha"];
                                conceptoBorrardo = reader["Concepto"].ToString();
                                montoBorrado = (decimal)reader["Monto"];
                                idCreador = (int)reader["Id_Usuario"];

                                if (reader["Id_Archivo"] != DBNull.Value)
                                {
                                    idArchivoEliminar = (int)reader["Id_Archivo"];
                                }
                            }
                            else
                            {
                                MostrarMensaje("No Encontrado", "El movimiento que intentas borrar ya no existe.", TipoMensaje.Alerta);
                                return RedirectToAction("Index");
                            }
                        }
                    }

                    // --- Bloqueo de Donaciones Y Traspasos ---
                    if (conceptoBorrardo.StartsWith(sNombreConceptoDonacion, StringComparison.OrdinalIgnoreCase) ||
                        conceptoBorrardo.Contains("TRASPASO", StringComparison.OrdinalIgnoreCase))
                    {
                        MostrarMensaje("Acción Bloqueada", "Los registros automatizados de Donaciones o Traspasos no pueden ser eliminados manualmente desde Tesorería.", TipoMensaje.Error);
                        return RedirectToAction("Index");
                    }

                    if (idCreador != idUsuario && !esAdmin)
                    {
                        MostrarMensaje("Acceso Denegado", "Solo puedes eliminar registros creados por ti.", TipoMensaje.Error);
                        return RedirectToAction("Index");
                    }

                    double diasDiferencia = (DateTime.Now - fechaMovimiento).TotalDays;
                    if (diasDiferencia > 7)
                    {
                        MostrarMensaje("Restricción de Tiempo", "No se pueden borrar movimientos con más de 7 días de antigüedad.", TipoMensaje.Alerta);
                        return RedirectToAction("Index");
                    }

                    using (var transaccion = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            using (var cmdDel = new NpgsqlCommand("DELETE FROM \"Fin_Caja\" WHERE \"Id_Movimiento\" = @id", conexion, transaccion))
                            {
                                cmdDel.Parameters.AddWithValue("@id", idEliminar);
                                await cmdDel.ExecuteNonQueryAsync();
                            }

                            if (idArchivoEliminar != null)
                            {
                                using (var cmdDelFile = new NpgsqlCommand("DELETE FROM \"Rec_Archivos\" WHERE \"Id_Archivo\" = @idFile", conexion, transaccion))
                                {
                                    cmdDelFile.Parameters.AddWithValue("@idFile", idArchivoEliminar);
                                    await cmdDelFile.ExecuteNonQueryAsync();
                                }
                            }

                            string detalle = $"Eliminó movimiento #{idEliminar}: ${montoBorrado} ({conceptoBorrardo})" +
                                             (idArchivoEliminar != null ? " [Archivo adjunto eliminado]" : "");

                            await Funciones.RegistrarBitacora(conexion, idUsuario, Modulo, Parametros.AccionesBitacora.Borrar, detalle, ipUsuario, transaccion);

                            await transaccion.CommitAsync();

                            MostrarMensaje("Eliminado", "El movimiento se eliminó correctamente.", TipoMensaje.Exito);
                        }
                        catch
                        {
                            await transaccion.RollbackAsync();
                            throw;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error Crítico", "No se pudo eliminar: " + ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Index");
        }

        public async Task<IActionResult> VerComprobante(int id, bool descargar = false)
        {
            if (!User.TienePermiso(Modulo, PermisoLeer))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permiso para acceder a esta opción.", TipoMensaje.Error);
                return RedirectToAction("Index", "Home");
            }
            if (id == 0) return NotFound();

            using (var conexion = new NpgsqlConnection(_cadenaConexion))
            {
                await conexion.OpenAsync();
                var cmd = new NpgsqlCommand("SELECT \"Contenido_Binario\", \"Tipo\", \"Titulo\" FROM \"Rec_Archivos\" WHERE \"Id_Archivo\" = @id AND \"Origen\" = 'Caja'", conexion);
                cmd.Parameters.AddWithValue("@id", id);

                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        if (reader["Contenido_Binario"] != DBNull.Value)
                        {
                            byte[] bytes = (byte[])reader["Contenido_Binario"];
                            string mime = reader["Tipo"].ToString();
                            string nombre = reader["Titulo"].ToString();

                            if (descargar)
                                return File(bytes, mime, nombre); // Fuerza la descarga en el navegador
                            else
                                return File(bytes, mime); // Lo muestra "inline" para el modal
                        }
                    }
                }
            }
            return NotFound();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SolicitarTraspaso(int IdUsuarioDestino, decimal MontoTraspaso, string ConceptoTraspaso, bool SolicitarTodo = false)
        {
            if (!User.TienePermiso(Modulo, PermisoCrear))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permiso para acceder a esta opción.", TipoMensaje.Error);
                return RedirectToAction("Index", "Home");
            }
            int idLogueado = int.Parse(User.FindFirst("IdUsuario")?.Value ?? "0");

            // 1. VALIDACIONES BÁSICAS DE ENTRADA (Sin validar el monto todavía)
            if (string.IsNullOrWhiteSpace(ConceptoTraspaso))
            {
                MostrarMensaje("Concepto Vacío", "Debes especificar el motivo o concepto del traspaso.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }
            if (IdUsuarioDestino <= 0 || IdUsuarioDestino == idLogueado)
            {
                MostrarMensaje("Destino Inválido", "Debes seleccionar a un encargado válido distinto a ti.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // 2. OBTENER SALDO REAL DIRECTO DE BASE DE DATOS
                    string sqlSaldoDestino = @"
        SELECT (
            COALESCE((SELECT SUM(""Monto"") FROM ""Fin_Caja"" WHERE ""Id_Usuario"" = @uid AND ""Tipo"" = 'Ingreso' AND ""Movimiento_En_Banco"" = FALSE), 0) -
            COALESCE((SELECT SUM(""Monto"") FROM ""Fin_Caja"" WHERE ""Id_Usuario"" = @uid AND ""Tipo"" = 'Egreso' AND ""Movimiento_En_Banco"" = FALSE), 0) +
            COALESCE((SELECT SUM(""Monto"") FROM ""Fin_Traspasos"" WHERE ""Id_Usuario_Destino"" = @uid AND ""Estado"" = 'ACE'), 0) -
            COALESCE((SELECT SUM(""Monto"") FROM ""Fin_Traspasos"" WHERE ""Id_Usuario_Origen"" = @uid AND ""Estado"" = 'ACE'), 0)
        )";

                    decimal saldoDestino = 0;
                    using (var cmdS = new NpgsqlCommand(sqlSaldoDestino, conexion))
                    {
                        cmdS.Parameters.AddWithValue("@uid", IdUsuarioDestino);
                        saldoDestino = Convert.ToDecimal(await cmdS.ExecuteScalarAsync());
                    }

                    // 3. LÓGICA DE SEGURIDAD: SOBRESCRIBIR MONTO SI MARCÓ "TODO"
                    if (SolicitarTodo)
                    {
                        MontoTraspaso = saldoDestino; // Ignoramos lo que mandó el HTML y usamos el de BD
                    }

                    // 4. VALIDACIÓN DE MONTOS
                    if (MontoTraspaso <= 0)
                    {
                        MostrarMensaje("Monto Inválido", "El monto a solicitar debe ser mayor a cero (el encargado podría no tener fondos).", TipoMensaje.Error);
                        return RedirectToAction("Index");
                    }

                    if (MontoTraspaso > saldoDestino)
                    {
                        MostrarMensaje("Fondos Insuficientes del Encargado", $"No puedes solicitar {MontoTraspaso:C}. El encargado seleccionado solo dispone de {saldoDestino:C} en este momento.", TipoMensaje.Error);
                        return RedirectToAction("Index");
                    }

                    // 5. REGISTRO DE SOLICITUD
                    string sql = @"INSERT INTO ""Fin_Traspasos"" (""Id_Usuario_Origen"", ""Id_Usuario_Destino"", ""Monto"", ""Estado"", ""Concepto"", ""Fecha_Solicitud"") 
                   VALUES (@origen, @destino, @monto, 'PEN', @concepto, NOW())";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@origen", IdUsuarioDestino);
                        cmd.Parameters.AddWithValue("@destino", idLogueado);
                        cmd.Parameters.AddWithValue("@monto", MontoTraspaso);
                        cmd.Parameters.AddWithValue("@concepto", ConceptoTraspaso.Trim());
                        await cmd.ExecuteNonQueryAsync();
                    }
                }
                MostrarMensaje("Solicitud Enviada", "El encargado ha recibido la solicitud y deberá confirmarla para que los fondos pasen a tu nombre.", TipoMensaje.Exito);
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error del Sistema", ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Index");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResponderTraspaso(int idTraspaso, string respuesta)
        {
            if (!User.TienePermiso(Modulo, PermisoCrear))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permiso para acceder a esta opción.", TipoMensaje.Error);
                return RedirectToAction("Index", "Home");
            }
            int idLogueado = int.Parse(User.FindFirst("IdUsuario")?.Value ?? "0");

            // Validar seguridad de los botones
            if (respuesta != "Aceptar" && respuesta != "Rechazar")
            {
                MostrarMensaje("Acción Inválida", "La respuesta proporcionada no es válida.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            string estado = respuesta == "Aceptar" ? "ACE" : "REC";

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // 1. VALIDAR QUE EL TRASPASO EXISTA, SEA MÍO Y SIGA PENDIENTE
                    string sqlVerificar = @"SELECT ""Monto"" FROM ""Fin_Traspasos"" WHERE ""Id_Traspaso"" = @id AND ""Id_Usuario_Origen"" = @uid AND ""Estado"" = 'PEN'";
                    decimal montoSolicitado = 0;

                    using (var cmdV = new NpgsqlCommand(sqlVerificar, conexion))
                    {
                        cmdV.Parameters.AddWithValue("@id", idTraspaso);
                        cmdV.Parameters.AddWithValue("@uid", idLogueado);
                        var res = await cmdV.ExecuteScalarAsync();

                        if (res == null)
                        {
                            MostrarMensaje("Solicitud no válida", "El traspaso no existe, no te corresponde o ya fue resuelto previamente.", TipoMensaje.Alerta);
                            return RedirectToAction("Index");
                        }
                        montoSolicitado = (decimal)res;
                    }

                    // 2. SI VOY A ENTREGAR (ACEPTAR), VALIDAR MI SALDO ACTUAL
                    // (Es posible que haya gastado el dinero desde que me hicieron la solicitud original)
                    if (estado == "ACE")
                    {
                        string sqlSaldo = @"
                    SELECT (
                        COALESCE((SELECT SUM(""Monto"") FROM ""Fin_Caja"" WHERE ""Id_Usuario"" = @uid AND ""Tipo"" = 'Ingreso' AND ""Movimiento_En_Banco"" = FALSE), 0) -
                        COALESCE((SELECT SUM(""Monto"") FROM ""Fin_Caja"" WHERE ""Id_Usuario"" = @uid AND ""Tipo"" = 'Egreso' AND ""Movimiento_En_Banco"" = FALSE), 0) +
                        COALESCE((SELECT SUM(""Monto"") FROM ""Fin_Traspasos"" WHERE ""Id_Usuario_Destino"" = @uid AND ""Estado"" = 'ACE'), 0) -
                        COALESCE((SELECT SUM(""Monto"") FROM ""Fin_Traspasos"" WHERE ""Id_Usuario_Origen"" = @uid AND ""Estado"" = 'ACE'), 0)
                    )";

                        using (var cmdS = new NpgsqlCommand(sqlSaldo, conexion))
                        {
                            cmdS.Parameters.AddWithValue("@uid", idLogueado);
                            decimal miSaldo = Convert.ToDecimal(await cmdS.ExecuteScalarAsync());

                            if (montoSolicitado > miSaldo)
                            {
                                MostrarMensaje("Sin Fondos", $"No puedes entregar {montoSolicitado:C}. Tu saldo actual cayó a {miSaldo:C}. Rechaza la solicitud o registra ingresos primero.", TipoMensaje.Error);
                                return RedirectToAction("Index");
                            }
                        }
                    }

                    // 3. EFECTUAR EL CAMBIO
                    string sqlUpdate = @"UPDATE ""Fin_Traspasos"" SET ""Estado"" = @est, ""Fecha_Resolucion"" = NOW() WHERE ""Id_Traspaso"" = @id AND ""Id_Usuario_Origen"" = @uid AND ""Estado"" = 'PEN'";
                    using (var cmdU = new NpgsqlCommand(sqlUpdate, conexion))
                    {
                        cmdU.Parameters.AddWithValue("@est", estado);
                        cmdU.Parameters.AddWithValue("@id", idTraspaso);
                        cmdU.Parameters.AddWithValue("@uid", idLogueado);
                        await cmdU.ExecuteNonQueryAsync();
                    }
                }

                string palabraExito = estado == "ACE" ? "Aceptado (Fondos transferidos)" : "Rechazado (Solicitud anulada)";
                MostrarMensaje("Traspaso Actualizado", $"El traspaso ha sido {palabraExito}.", TipoMensaje.Exito);
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error del Sistema", ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Index");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SincronizarFlujosStripe()
        {
            if (!User.TienePermiso(Modulo, Parametros.Permisos.Admin))
                return Json(new { exito = false, mensaje = "Acceso denegado. Privilegios insuficientes." });

            // =====================================================================
            // CONFIGURACIÓN: Nombre exacto del titular de la cuenta oficial
            // =====================================================================
            string titularOficialOrganizacion = "Ministerio Corbán";
            int totalProcesados = 0;

            var configuracionesStripe = new Dictionary<string, string> {
                { "Eventos", _configuration["StripeEventos:SecretKey"] },
                { "Tienda", _configuration["StripeTienda:SecretKey"] },
                { "Rifas", _configuration["StripeRifas:SecretKey"] }
            };

            try
            {
                using (var con = new NpgsqlConnection(_cadenaConexion))
                {
                    await con.OpenAsync();

                    // ===================================================================================
                    // FASE 1: DESCARGAR TODAS LAS TRANSACCIONES SIN VALIDAR A MEMORIA LOCAL
                    // ===================================================================================
                    var transaccionesPendientes = new List<dynamic>();

                    // Extraer Eventos (Excluir las transferencias a cuentas personalizadas)
                    string qEventos = @"SELECT t.""Id_Transaccion"" AS ""Id"", t.""Ref_Pasarela"" AS ""Ref"", t.""EsTransferencia"", ec.""Clave_Cuenta_Bancaria"" 
                        FROM ""Eventos_D_Transacciones"" t
                        JOIN ""Eventos_Catalogo"" ec ON t.""Id_Evento"" = ec.""Id_Evento""
                        WHERE t.""Estatus_Pago"" = 'paid' 
                        AND t.""ValidadoXCaja"" = FALSE
                        AND NOT (t.""EsTransferencia"" = TRUE AND ec.""Usa_Cuenta_Personalizada"" = TRUE)";
                    using (var cmd = new NpgsqlCommand(qEventos, con))
                    using (var r = await cmd.ExecuteReaderAsync())
                        while (await r.ReadAsync())
                            transaccionesPendientes.Add(new
                            {
                                Modulo = "Eventos",
                                Id = (int)r["Id"],
                                Ref = r["Ref"]?.ToString(),
                                EsTransferencia = (bool)r["EsTransferencia"],
                                ClaveCuenta = r["Clave_Cuenta_Bancaria"]?.ToString()
                            });

                    // Extraer Rifas
                    string qRifas = @"SELECT ""Id_Venta"" AS ""Id"", ""Ref_Pasarela_Id"" AS ""Ref"", FALSE AS ""EsTransferencia"" 
                      FROM ""Rifas_Ventas"" WHERE ""Estado"" = 'Pagado' AND ""ValidadoXCaja"" = FALSE";
                    using (var cmd = new NpgsqlCommand(qRifas, con))
                    using (var r = await cmd.ExecuteReaderAsync())
                        while (await r.ReadAsync())
                            transaccionesPendientes.Add(new { Modulo = "Rifas", Id = (int)r["Id"], Ref = r["Ref"]?.ToString(), EsTransferencia = (bool)r["EsTransferencia"], ClaveCuenta = "" });

                    // Extraer Tienda
                    string qTienda = @"SELECT ""Id_Pedido"" AS ""Id"", ""Ref_Pasarela"" AS ""Ref"", FALSE AS ""EsTransferencia"" 
                       FROM ""Tienda_Pedidos"" WHERE ""Id_Estatus"" >= 30 AND ""ValidadoXCaja"" = FALSE";
                    using (var cmd = new NpgsqlCommand(qTienda, con))
                    using (var r = await cmd.ExecuteReaderAsync())
                        while (await r.ReadAsync())
                            transaccionesPendientes.Add(new { Modulo = "Tienda", Id = (int)r["Id"], Ref = r["Ref"]?.ToString(), EsTransferencia = (bool)r["EsTransferencia"], ClaveCuenta = "" });


                    // ===================================================================================
                    // FASE 2: VALIDAR CADA TRANSACCIÓN INDIVIDUAL Y EVALUAR EL TITULAR CON FALLBACKS
                    // ===================================================================================
                    foreach (var trx in transaccionesPendientes)
                    {
                        if (string.IsNullOrEmpty(trx.Ref)) continue;

                        string moduloTrx = trx.Modulo;
                        bool esOficialFinal = false;

                        // Si corresponde a una transferencia, validamos la cuenta asignada en el evento
                        if (trx.EsTransferencia)
                        {
                            // Si el campo tiene el valor DEFAULT la transferencia es válida para el banco de la empresa
                            esOficialFinal = string.Equals(trx.ClaveCuenta, "DEFAULT", StringComparison.OrdinalIgnoreCase);
                        }
                        else
                        {
                            bool pagoConfirmadoEnStripe = false;
                            StripeClient clienteStripeExitoso = null;

                            // Ordenamos las llaves para probar PRIMERO la del módulo original.
                            // Si esa falla, probará con las demás del diccionario.
                            var llavesAProbar = configuracionesStripe.Keys
                                                .OrderByDescending(k => k == moduloTrx)
                                                .ToList();

                            // 1. Buscar la transacción en las cuentas disponibles
                            foreach (var keyName in llavesAProbar)
                            {
                                string secretKey = configuracionesStripe[keyName];
                                if (string.IsNullOrEmpty(secretKey)) continue;

                                var clienteStripeTest = new StripeClient(secretKey);

                                try
                                {
                                    if (trx.Ref.StartsWith("cs_"))
                                    {
                                        var sessionService = new Stripe.Checkout.SessionService(clienteStripeTest);
                                        var session = await sessionService.GetAsync(trx.Ref);
                                        if (session.PaymentStatus == "paid")
                                        {
                                            pagoConfirmadoEnStripe = true;
                                            clienteStripeExitoso = clienteStripeTest;
                                            break; // ¡Encontrado! Salimos del ciclo de búsqueda de llaves.
                                        }
                                    }
                                    else if (trx.Ref.StartsWith("pi_"))
                                    {
                                        var piService = new Stripe.PaymentIntentService(clienteStripeTest);
                                        var intent = await piService.GetAsync(trx.Ref);
                                        if (intent.Status == "succeeded")
                                        {
                                            pagoConfirmadoEnStripe = true;
                                            clienteStripeExitoso = clienteStripeTest;
                                            break; // ¡Encontrado! Salimos del ciclo de búsqueda de llaves.
                                        }
                                    }
                                }
                                catch
                                {
                                    // Si la transacción no existe en esta cuenta, la API lanza excepción.
                                    // La atrapamos silenciosamente y el ciclo continúa con la siguiente llave.
                                }
                            }

                            // Si no se encontró en NINGUNA de las cuentas o no está pagada, saltamos.
                            if (!pagoConfirmadoEnStripe || clienteStripeExitoso == null) continue;

                            // 2. Obtener y validar el Titular de la cuenta exitosa para esta transacción
                            try
                            {
                                var accountService = new AccountService(clienteStripeExitoso);
                                var cuentaStripe = await accountService.GetAsync("self");

                                string titularDeLaCuentaStripe = cuentaStripe.Settings?.Dashboard?.DisplayName
                                                                 ?? cuentaStripe.BusinessProfile?.Name;

                                if (string.IsNullOrWhiteSpace(titularDeLaCuentaStripe))
                                {
                                    continue;
                                }

                                // Comparación del titular individual (admite tanto Ministerio Corbán como Alianza Juvenil Los Pescadores por compatibilidad)
                                esOficialFinal = titularDeLaCuentaStripe.Equals(titularOficialOrganizacion, StringComparison.OrdinalIgnoreCase)
                                              || titularDeLaCuentaStripe.Equals("Alianza Juvenil Los Pescadores", StringComparison.OrdinalIgnoreCase);
                            }
                            catch
                            {
                                // Falla en la validación del titular, se omite.
                                continue;
                            }
                        }

                        // ===================================================================================
                        // FASE 3: ACTUALIZACIÓN TRANSACCIONAL EN BD LOCAL
                        // ===================================================================================
                        using (var trans = await con.BeginTransactionAsync())
                        {
                            try
                            {
                                string sqlUpdateOrigen = "";
                                if (moduloTrx == "Eventos")
                                    sqlUpdateOrigen = @"UPDATE ""Eventos_D_Transacciones"" SET ""CuentaPrincipalBanco"" = @cpb, ""ValidadoXCaja"" = TRUE WHERE ""Id_Transaccion"" = @id";
                                else if (moduloTrx == "Rifas")
                                    sqlUpdateOrigen = @"UPDATE ""Rifas_Ventas"" SET ""CuentaPrincipalBanco"" = @cpb, ""ValidadoXCaja"" = TRUE WHERE ""Id_Venta"" = @id";
                                else if (moduloTrx == "Tienda")
                                    sqlUpdateOrigen = @"UPDATE ""Tienda_Pedidos"" SET ""CuentaPrincipalBanco"" = @cpb, ""ValidadoXCaja"" = TRUE WHERE ""Id_Pedido"" = @id";

                                using (var cmdUpd = new NpgsqlCommand(sqlUpdateOrigen, con, trans))
                                {
                                    cmdUpd.Parameters.AddWithValue("@cpb", esOficialFinal);
                                    cmdUpd.Parameters.AddWithValue("@id", trx.Id);
                                    await cmdUpd.ExecuteNonQueryAsync();
                                }

                                await trans.CommitAsync();
                                totalProcesados++;
                            }
                            catch
                            {
                                await trans.RollbackAsync();
                            }
                        }
                    }

                    return Json(new { exito = true, mensaje = $"Sincronización exitosa. Se validaron y actualizaron {totalProcesados} transacciones pendientes." });
                }
            }
            catch (Exception ex)
            {
                return Json(new { exito = false, mensaje = "Falla crítica en el proceso de base de datos: " + ex.Message });
            }
        }
    }
}