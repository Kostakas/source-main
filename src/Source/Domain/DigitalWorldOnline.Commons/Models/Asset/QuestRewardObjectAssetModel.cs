﻿namespace DigitalWorldOnline.Commons.Models.Asset
{
    public sealed class QuestRewardObjectAssetModel
    {
        /// <summary>
        /// Sequencial unique identifier.
        /// </summary>
        public long Id { get; set; }

        /// <summary>
        /// Reward identifier.
        /// </summary>
        public int Reward { get; set; }

        /// <summary>
        /// Quest reward amount.
        /// </summary>
        public int Amount { get; set; }

        /// <summary>
        /// Parent object.
        /// </summary>
        public long QuestRewardId { get; set; }
        public QuestRewardAssetModel QuestReward { get; set; }
    }
}