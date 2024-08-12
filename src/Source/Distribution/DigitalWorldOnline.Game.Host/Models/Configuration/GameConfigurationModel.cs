using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DigitalWorldOnline.Game.Models.Configuration
{
    public class GameConfigurationModel
    {
        public int? BaseCriticalDamage { get; set; }
        public AttributeConfig Attribute { get; set; }
        public ElementConfig Element { get; set; }
        public ItemDropCountConfig ItemDropCount { get; set; }

        public BitDropCountConfig BitDropCount { get; set; }

        public class AttributeConfig
        {
            public bool ApplyDamage { get; set; }
            public double AdvantageMultiplier { get; set; }
            public double DisAdvantageMultiplier { get; set; }
        }

        public class ElementConfig
        {
            public bool ApplyDamage { get; set; }
            public double AdvantageMultiplier { get; set; }
            public double DisAdvantageMultiplier { get; set; }
        }

        public class ItemDropCountConfig
        {
            public bool ApplyDropAddition { get; set; }
            public int MultiplyDropCount { get; set; }
        }

        public class BitDropCountConfig
        {
            public bool ApplyDropAddition { get; set; }
            public int MultiplyDropCount { get; set; }
        }
    }
}

