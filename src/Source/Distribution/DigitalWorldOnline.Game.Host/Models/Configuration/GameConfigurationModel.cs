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
    }
}

