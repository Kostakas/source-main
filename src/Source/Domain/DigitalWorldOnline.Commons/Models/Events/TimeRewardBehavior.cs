using DigitalWorldOnline.Commons.Enums;
using DigitalWorldOnline.Commons.Utils;

namespace DigitalWorldOnline.Commons.Models
{
    public sealed partial class TimeReward
    {
        public DateTime LastTimeRewardUpdate = DateTime.Now;

        private int _currentTime = 0;

        public int CurrentTime
        {
            get
            {
                return _currentTime;
            }

            set
            {
                _currentTime = value;
            }
        }

        public int RemainingTime
        {
            get
            {
                switch (RewardIndex)
                {
                    default: return -1;

                    case TimeRewardIndexEnum.First:
                        return (int)(TimeRewardDurationEnum.First - CurrentTime);
                    case TimeRewardIndexEnum.Second:
                        return (int)(TimeRewardDurationEnum.Second - CurrentTime);
                    case TimeRewardIndexEnum.Third:
                        return (int)(TimeRewardDurationEnum.Third - CurrentTime);
                    case TimeRewardIndexEnum.Fourth:
                        return (int)(TimeRewardDurationEnum.Fourth - CurrentTime);
                }
            }
        }

        public void SetLastTimeRewardDate()
        {
            LastTimeRewardUpdate = DateTime.Now;
        }

        public bool TimeCompleted()
        {
            return RewardIndex switch
            {
                TimeRewardIndexEnum.First => CurrentTime >= (int)TimeRewardDurationEnum.First,
                TimeRewardIndexEnum.Second => CurrentTime >= (int)TimeRewardDurationEnum.Second,
                TimeRewardIndexEnum.Third => CurrentTime >= (int)TimeRewardDurationEnum.Third,
                TimeRewardIndexEnum.Fourth => CurrentTime >= (int)TimeRewardDurationEnum.Fourth,
                _ => false,
            };
        }
    }
}