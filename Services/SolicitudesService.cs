using Npgsql;
using Microsoft.Extensions.Configuration;
using System;
using System.Threading.Tasks;

namespace RedAJP.Services
{
    // =========================================================
    // 1. LA INTERFAZ (El contrato actualizado)
    // =========================================================
    public interface ISolicitudesService
    {
        // Contador 1: Solicitudes de diseño pendientes (Admin)
        Task<int> ObtenerContadorPendientesAsync();

        // Contador 2: Objetos en el carrito (Por Usuario)
        Task<int> ObtenerContadorCarritoAsync(int idUsuario);

        // Contador 3: Pedidos pagados listos para entregar (Admin)
        Task<int> ObtenerContadorEntregasAsync();

        // Contador 4: Notificaciones para el cliente
        Task<int> ObtenerContadorAlertasClienteAsync(int idUsuario);

        // Contador 5: Transferencias pendientes de revisión (Admin)
        Task<int> ObtenerContadorTransferenciasPendAsync();

        // Contador 6: Total de Publicaciones Activas en el Tianguis (Global)
        Task<int> ObtenerContadorTianguisPublicacionesAsync();

        // Contador 7: Alertas del Tianguis (Mis ofertas ganadas o mis ventas por confirmar)
        Task<int> ObtenerContadorTianguisAlertasAsync(int idUsuario);

        // Donaciones pendientes de autorizar
        Task<int> ObtenerContadorDonacionesPendientesAsync(int idUsuario);

        Task<int> ObtenerContadorVinculacionesPendientesAsync();

        // Contador: Retorna 1 si el usuario tiene boletos asignados en alguna rifa activa (Vendedor)
        Task<int> ObtenerContadorRifasAsignadasAsync(int idUsuario);

        // Contador: Retorna la cantidad de comprobantes pendientes de validar (Administrador)
        Task<int> ObtenerContadorRifasPagosPendientesAsync(int idUsuario);

        // Contador: Mensajes no leídos en la Comunidad
        Task<int> ObtenerContadorMensajesComunidadAsync(int idUsuario, bool esAdmin);
    }

    // =========================================================
    // 2. LA CLASE (La implementación)
    // =========================================================
    public class SolicitudesService : ISolicitudesService
    {
        private readonly IConfiguration _config;
        private readonly string _connectionString;

        public SolicitudesService(IConfiguration config)
        {
            _config = config;
            _connectionString = _config.GetConnectionString("MiConexion");
        }

        // --- 1. SOLICITUDES DE DISEÑO PENDIENTES (ADMIN) ---
        public async Task<int> ObtenerContadorPendientesAsync()
        {
            // Estatus 1 = Pendiente de revisión
            const string sql = @"SELECT COUNT(*) FROM ""Tienda_Solicitudes_Diseno"" WHERE ""Id_Estatus_Diseno"" = 1";
            return await EjecutarEscalarAsync(sql);
        }

        // --- 2. OBJETOS EN EL CARRITO (POR USUARIO) ---
        public async Task<int> ObtenerContadorCarritoAsync(int idUsuario)
        {
            // AHORA: Sumamos directo de la tabla Tienda_Carrito
            const string sql = @"SELECT COALESCE(SUM(""Cantidad""), 0) 
                         FROM ""Tienda_Carrito"" 
                         WHERE ""Id_Usuario"" = @uid";

            return await EjecutarEscalarAsync(sql, cmd =>
            {
                cmd.Parameters.AddWithValue("@uid", idUsuario);
            });
        }

        // --- 3. NUEVAS COMPRAS POR ENTREGAR (ADMIN) ---
        public async Task<int> ObtenerContadorEntregasAsync()
        {
            // Contamos los pedidos listos para entregar (Estatus 30) 
            // y le sumamos cualquier mensaje de cliente que los administradores no hayan leído.
            const string sql = @"
        SELECT 
            (SELECT COUNT(*) FROM ""Tienda_Pedidos"" WHERE ""Id_Estatus"" = 30) +
            (SELECT COUNT(*) FROM ""Tienda_Pedidos_Mensajes"" WHERE ""Es_Admin"" = FALSE AND ""Leido"" = FALSE)";

            return await EjecutarEscalarAsync(sql);
        }
        public async Task<int> ObtenerContadorAlertasClienteAsync(int idUsuario)
        {
            // Sumamos los pedidos listos para recoger (Estatus 30) de este usuario
            // MÁS los mensajes que le enviaron los administradores y que aún no lee.
            const string sql = @"
        SELECT 
            (SELECT COUNT(*) FROM ""Tienda_Pedidos"" 
             WHERE ""Id_Usuario_Solicita"" = @idUsuario AND ""Id_Estatus"" = 30) 
            +
            (SELECT COUNT(*) FROM ""Tienda_Pedidos_Mensajes"" m 
             INNER JOIN ""Tienda_Pedidos"" p ON m.""Id_Pedido"" = p.""Id_Pedido"" 
             WHERE p.""Id_Usuario_Solicita"" = @idUsuario AND m.""Es_Admin"" = TRUE AND m.""Leido"" = FALSE)";

            // Usamos una expresión lambda para agregar el parámetro al comando
            return await EjecutarEscalarAsync(sql, cmd =>
            {
                cmd.Parameters.AddWithValue("@idUsuario", idUsuario);
            });
        }

        // --- 4. TRANSFERENCIAS PENDIENTES DE REVISIÓN (ADMIN) ---
        public async Task<int> ObtenerContadorTransferenciasPendAsync()
        {
            // 'review' = El usuario ya subió foto y espera aprobación del Admin
            const string sql = @"SELECT COUNT(*) FROM ""Eventos_D_Transacciones"" WHERE ""Estatus_Pago"" = 'review'";

            // Convertimos a Int32 porque COUNT devuelve Int64 (long) en PostgreSQL
            return Convert.ToInt32(await EjecutarEscalarAsync(sql));
        }

        // --- 5. TOTAL DE PUBLICACIONES TIANGUIS (GLOBAL) ---
        public async Task<int> ObtenerContadorTianguisPublicacionesAsync()
        {
            // Cuenta todas las publicaciones que estén Activas ('ACT')
            const string sql = @"SELECT COUNT(*) FROM ""Tianguis_Publicaciones"" WHERE ""IdEstado"" = 'ACT' AND ""Activo"" = TRUE;";

            return Convert.ToInt32(await EjecutarEscalarAsync(sql));
        }

        // --- 6. ALERTAS TIANGUIS (POR USUARIO) ---
        public async Task<int> ObtenerContadorTianguisAlertasAsync(int idUsuario)
        {
            // Una "Alerta" para un usuario es:
            // 1. Alguien le hizo una oferta en SU publicación (Estado INT) Y la publicación sigue activa/reservada.
            // 2. Alguien le apartó/reservó un artículo que ÉL quiere comprar (Estado RES) Y la publicación no se ha cerrado.
            const string sql = @"
        SELECT COUNT(*) FROM (
            -- Mis publicaciones que tienen interesados activos
            SELECT i.""IdInteresado"" 
            FROM ""Tianguis_Interesados"" i
            JOIN ""Tianguis_Publicaciones"" p ON i.""IdPublicacion"" = p.""IdPublicacion""
            WHERE p.""IdUsuarioVendedor"" = @uid 
              AND i.""IdEstadoTrato"" = 'INT'
              AND p.""IdEstado"" IN ('ACT', 'RES')
            
            UNION ALL
            
            -- Mis ofertas donde el vendedor ya me reservó el artículo
            SELECT i.""IdInteresado"" 
            FROM ""Tianguis_Interesados"" i
            JOIN ""Tianguis_Publicaciones"" p ON i.""IdPublicacion"" = p.""IdPublicacion""
            WHERE i.""IdUsuarioComprador"" = @uid 
              AND i.""IdEstadoTrato"" = 'RES'
              AND p.""IdEstado"" IN ('ACT', 'RES')
        ) as Alertas";

            return Convert.ToInt32(await EjecutarEscalarAsync(sql, cmd =>
            {
                cmd.Parameters.AddWithValue("@uid", idUsuario);
            }));
        }

        // --- 6. ALERTAS DONACIONES ---
        public async Task<int> ObtenerContadorDonacionesPendientesAsync(int idUsuario)
        {
            // Cuenta los comprobantes pendientes (Nuevos + 2da Validación de OTROS administradores)
            const string sql = @"
        SELECT COUNT(""Id_Donacion"") 
        FROM ""Sist_Aportaciones"" 
        WHERE ""Estatus"" = 'Pendiente' 
          AND (""Id_Usuario_Pre_Revisor"" IS NULL OR ""Id_Usuario_Pre_Revisor"" != @uid)";

            return Convert.ToInt32(await EjecutarEscalarAsync(sql, cmd =>
            {
                cmd.Parameters.AddWithValue("@uid", idUsuario);
            }));
        }

        // --- 7. BOLETOS ASIGNADOS O DEUDAS EN RIFAS ACTIVAS (VENDEDOR) ---
        public async Task<int> ObtenerContadorRifasAsignadasAsync(int idUsuario)
        {
            // Devuelve 1 SOLO SI el usuario tiene acciones pendientes en alguna rifa activa:
            // - Le sobran boletos por vender (Estado = 'Asignado')
            // - O ya vendió boletos pero no ha reportado/pagado el total de esa deuda.
            const string sql = @"
                SELECT CASE WHEN EXISTS (
                    SELECT b.""IdRifa""
                    FROM ""RifasPersonales_Boletos"" b
                    INNER JOIN ""RifasPersonales_Rifas"" r ON b.""IdRifa"" = r.""IdRifa""
                    WHERE b.""IdUsuarioAsignado"" = @uid 
                      AND r.""Estado"" = 'Activa'
                    GROUP BY b.""IdRifa"", r.""CostoBoleto""
                    HAVING 
                        -- Condición A: Tiene boletos que aún no vende
                        SUM(CASE WHEN b.""Estado"" = 'Asignado' THEN 1 ELSE 0 END) > 0
                        OR 
                        -- Condición B: Lo que ha vendido supera a lo que ha pagado/reportado
                        (
                            (SUM(CASE WHEN b.""Estado"" = 'Vendido' THEN 1 ELSE 0 END) * r.""CostoBoleto"") 
                            - 
                            COALESCE((
                                SELECT SUM(p.""Monto"") 
                                FROM ""RifasPersonales_Pagos"" p 
                                WHERE p.""IdRifa"" = b.""IdRifa"" 
                                  AND p.""IdUsuarioVendedor"" = @uid 
                                  AND p.""Estado"" IN ('Aprobado', 'Pendiente')
                            ), 0)
                        ) > 0.01
                ) THEN 1 ELSE 0 END;";

            return await EjecutarEscalarAsync(sql, cmd =>
            {
                cmd.Parameters.AddWithValue("@uid", idUsuario);
            });
        }

        // --- 8. COMPROBANTES DE PAGO PENDIENTES DE VALIDAR (ADMINISTRADOR) ---
        public async Task<int> ObtenerContadorRifasPagosPendientesAsync(int idUsuario)
        {
            // Cuenta estrictamente cuántos pagos tienen estado 'Pendiente' 
            // en las rifas donde este usuario es el creador.
            const string sql = @"
                SELECT COUNT(p.""IdPago"") 
                FROM ""RifasPersonales_Pagos"" p
                INNER JOIN ""RifasPersonales_Rifas"" r ON p.""IdRifa"" = r.""IdRifa""
                WHERE r.""IdUsuarioCreador"" = @uid 
                  AND p.""Estado"" = 'Pendiente';";

            return Convert.ToInt32(await EjecutarEscalarAsync(sql, cmd =>
            {
                cmd.Parameters.AddWithValue("@uid", idUsuario);
            }));
        }

        // --- 9. MENSAJES DE COMUNIDAD NO LEÍDOS ---
        public async Task<int> ObtenerContadorMensajesComunidadAsync(int idUsuario, bool esAdmin)
        {
            // Cuenta mensajes no expirados, no cancelados, que no existan en la tabla de leídos 
            // y aplicamos la excepción para los mensajes con autor NULL o dirigidos a la directiva.
            const string sql = @"
                SELECT COUNT(*) 
                FROM ""Sist_Comunidad_Mensajes"" m
                WHERE m.""Fecha_Expiracion"" >= NOW() 
                  AND m.""Estado"" != 'CAN'
                  AND (m.""Id_Usuario_Autor"" != @idUsuario OR m.""Id_Usuario_Autor"" IS NULL OR m.""Destinatario"" = 'Directiva')
                  AND NOT EXISTS (
                      SELECT 1 FROM ""Sist_Comunidad_Mensajes_Leidos"" l 
                      WHERE l.""Id_Mensaje"" = m.""Id_Mensaje"" AND l.""Id_Usuario"" = @idUsuario
                  )
                  AND (
                      (m.""Estado"" = 'APR' AND m.""Destinatario"" = 'Todos') 
                      OR 
                      (@esAdmin = TRUE)
                      OR 
                      (EXISTS (
                          SELECT 1 FROM ""Sist_Grupos_Miembros"" gm 
                          JOIN ""Sist_Comunidad_Configuracion"" cc ON gm.""Id_Grupo"" = cc.""Id_Grupo_Directiva"" 
                          WHERE gm.""Id_Usuario"" = @idUsuario AND cc.""Id_Config"" = 1
                      ))
                  )";

            return Convert.ToInt32(await EjecutarEscalarAsync(sql, cmd =>
            {
                cmd.Parameters.AddWithValue("@idUsuario", idUsuario);
                cmd.Parameters.AddWithValue("@esAdmin", esAdmin);
            }));
        }

        public async Task<int> ObtenerContadorVinculacionesPendientesAsync()
        {
            // Cuenta las solicitudes de vinculación de iglesias que están en estado Pendiente
            const string sql = @"
        SELECT COUNT(id) 
        FROM iciar_iglesias_solicitudes 
        WHERE estado = 'PEN'";

            return Convert.ToInt32(await EjecutarEscalarAsync(sql, cmd => { }));
        }

        // =========================================================
        // MÉTODO PRIVADO AUXILIAR (Para no repetir código de conexión)
        // =========================================================
        private async Task<int> EjecutarEscalarAsync(string sql, Action<NpgsqlCommand> agregarParametros = null)
        {
            try
            {
                using (var conexion = new NpgsqlConnection(_connectionString))
                {
                    await conexion.OpenAsync();
                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        // Si hay parámetros (como el ID de usuario), los agregamos aquí
                        agregarParametros?.Invoke(cmd);

                        var resultado = await cmd.ExecuteScalarAsync();

                        if (resultado != null && resultado != DBNull.Value)
                        {
                            return Convert.ToInt32(resultado);
                        }
                    }
                }
            }
            catch
            {
                // Si falla la BD, devolvemos 0 para que la página siga funcionando
                return 0;
            }
            return 0;
        }
    }
}