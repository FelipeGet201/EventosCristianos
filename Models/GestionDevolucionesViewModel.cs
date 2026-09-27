using System.Collections.Generic;

namespace RedAJP.Models
{
    public class GestionDevolucionesViewModel
    {
        public string IdEventoEncriptado { get; set; }
        public List<DevolucionItemViewModel> Devoluciones { get; set; } = new List<DevolucionItemViewModel>();
    }

    public class DevolucionItemViewModel
    {
        public int IdDevolucion { get; set; }
        public string NombrePersona { get; set; }
        public string Evento { get; set; } 
        public string Motivo { get; set; }
        public decimal MontoTotal { get; set; }
        public decimal SaldoPendiente { get; set; }
        public string Estado { get; set; }
    }
}