using DigitalWorldOnline.Commons.Models;
using DigitalWorldOnline.Commons.Writers;

namespace DigitalWorldOnline.Commons.Packets.GameServer
{
    public class TimeRewardPacket :PacketWriter
    {
        private const int PacketNumber = 3106;

        /// <summary>
        /// Load the time reward current streak.
        /// </summary>
        public TimeRewardPacket(TimeReward timeReward,byte finish)
        {
            int totalTime = timeReward.CurrentTime + timeReward.RemainingTime;

            Type(PacketNumber);
            WriteInt(timeReward.RewardIndex.GetHashCode());
            WriteInt(timeReward.RemainingTime);
            WriteInt(totalTime);
            WriteByte(finish);
        }
    }
}