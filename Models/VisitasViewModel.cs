using System;
using System.Collections.Generic;

namespace RedAJP.Models
{
    public class VisitasViewModel
    {
        // FILTROS (Nuevos)
        public DateTime FechaInicio { get; set; }
        public DateTime FechaFin { get; set; }

        // KPIs (Se mantienen)
        public long TotalEnRango { get; set; } // Renombrado para ser más exacto
        public long VisitasHoy { get; set; }
        public string DispositivoTop { get; set; }

        // Datos Gráficas
        public string[] LabelsDias { get; set; }
        public int[] DataDias { get; set; }
        public int[] DataHoras { get; set; }
        public int PcCount { get; set; }
        public int MovilCount { get; set; }

        // Lista
        public List<VisitaDetalle> UltimasVisitas { get; set; } = new List<VisitaDetalle>();
        public Dictionary<string, int> VisitasPorModulo { get; set; } = new Dictionary<string, int>();
        public List<BitacoraItem> LogBitacora { get; set; } = new List<BitacoraItem>();
    }
    public class BitacoraItem
    {
        public string Fecha { get; set; }
        public string Modulo { get; set; }
        public string Accion { get; set; }
        public string Detalle { get; set; }
        public string Usuario { get; set; }
        public string Ip { get; set; }
    }

    public class VisitaDetalle
    {
        public string Ip { get; set; }
        public string Fecha { get; set; }
        public string Usuario { get; set; }
        public string Pagina { get; set; }
        public string Dispositivo { get; set; }
    }
}