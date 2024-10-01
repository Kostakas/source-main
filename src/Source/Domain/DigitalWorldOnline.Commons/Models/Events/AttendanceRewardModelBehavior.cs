namespace DigitalWorldOnline.Commons.Models.Events
{
    public sealed partial class AttendanceRewardModel
    {
        public bool ReedemRewards => LastRewardDate.Date < DateTime.UtcNow.Date;

        public void SetLastRewardDate()
        {
            LastRewardDate = DateTime.UtcNow;  // Usar UTC para mantener la coherencia
        }

        public void IncreaseTotalDays(byte amount = 1)
        {
            // Verificar que TotalDays no exceda el rango permitido
            if (TotalDays + amount < TotalDays)
            {
                // Manejar el caso de desbordamiento si es necesario
                TotalDays = byte.MaxValue;
            }
            else
            {
                TotalDays += amount;
            }
        }
    }
}
