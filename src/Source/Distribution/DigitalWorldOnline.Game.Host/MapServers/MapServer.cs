using AutoMapper;
using DigitalWorldOnline.Application;
using DigitalWorldOnline.Commons.Models.Map;
using DigitalWorldOnline.Game.Managers;
using MediatR;
using Serilog;
using Microsoft.Extensions.Configuration;
using DigitalWorldOnline.Game.Models.Configuration;

namespace DigitalWorldOnline.GameHost
{
    public sealed partial class MapServer
    {
        private readonly PartyManager _partyManager;
        private readonly StatusManager _statusManager;
        private readonly ExpManager _expManager;
        private readonly DropManager _dropManager;
        private readonly AssetsLoader _assets;
        private readonly ConfigsLoader _configs;
        private readonly ILogger _logger;
        private readonly ISender _sender;
        private readonly IMapper _mapper;
        private readonly IConfiguration _configuration;
        private readonly TimeRewardService _timeRewardService;

        public List<GameMap> Maps { get; set; }

        public MapServer(
            PartyManager partyManager,
            AssetsLoader assets,
            ConfigsLoader configs,
            StatusManager statusManager,
            ExpManager expManager,
            DropManager dropManager,
            ILogger logger,
            ISender sender,
            IMapper mapper,
            IConfiguration configuration,
            TimeRewardService timeRewardService)
        {
            _partyManager = partyManager;
            _statusManager = statusManager;
            _expManager = expManager;
            _dropManager = dropManager;
            _assets = assets.Load();
            _configs = configs.Load();
            _logger = logger;
            _sender = sender;
            _mapper = mapper;
            _configuration = configuration;
            _timeRewardService = timeRewardService;

            Maps = new List<GameMap>();
        }
    }
}