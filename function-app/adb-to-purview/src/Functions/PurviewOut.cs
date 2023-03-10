// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Function.Domain.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Function.Domain.Helpers;
using Microsoft.Extensions.Configuration;

namespace AdbToPurview.Function
{
    public class PurviewOut
    {
        private readonly ILogger<PurviewOut> _logger; 
        private readonly ILoggerFactory _loggerFactory;
        private readonly IOlConsolodateEnrich _olConsolodateEnrich;       
        private readonly IOlToPurviewParsingService _olToPurviewParsingService;
        private readonly IPurviewIngestion _purviewIngestion;
        private readonly IConfiguration _configuration;

        public PurviewOut(ILogger<PurviewOut> logger, IOlToPurviewParsingService olToPurviewParsingService, IPurviewIngestion purviewIngestion, IOlConsolodateEnrich olConsolodateEnrich, ILoggerFactory loggerFactory, IConfiguration configuration)
        {
            logger.LogInformation("Enter PurviewOut");
            _logger = logger; 
            _loggerFactory = loggerFactory;
            _olConsolodateEnrich = olConsolodateEnrich;           
            _olToPurviewParsingService = olToPurviewParsingService;
            _purviewIngestion = purviewIngestion;
            _configuration = configuration;
        }

        [Function("PurviewOut")]
        // V2 May want to implement batch processing of events by making "input" an array and setting IsBatched
        // see https://docs.microsoft.com/en-us/azure/azure-functions/functions-bindings-event-hubs-trigger?tabs=csharp#scaling
        public async Task<string> Run(
            [EventHubTrigger("%EventHubName%", IsBatched = false ,Connection = "ListenToMessagesFromEventHub", ConsumerGroup = "%EventHubConsumerGroup%")] string input)
        {
            try{
                var enrichedEvent = await _olConsolodateEnrich.ProcessOlMessage(input);
                if (enrichedEvent == null)
                {
                    _logger.LogInformation($"Start event, duplicate event, or no context found - eventData: {input}");
                    return "";
                }

                IDatabricksToPurviewParser parser = new DatabricksToPurviewParser(_loggerFactory, _configuration, enrichedEvent);
                var purviewEvent = _olToPurviewParsingService.GetPurviewFromOlEvent(enrichedEvent, parser);
                if (purviewEvent == null)
                {
                    _logger.LogWarning("No Purview Event found");
                    return "unable to parse purview event";
                }

                _logger.LogInformation($"PurviewOut-ParserService: {purviewEvent}");
                var jObjectPurviewEvent = JsonConvert.DeserializeObject<JObject>(purviewEvent) ?? new JObject();
                _logger.LogInformation("Calling SendToPurview");
                await _purviewIngestion.SendToPurview(jObjectPurviewEvent, parser.GetColumnParser());

                return $"Output message created at {DateTime.Now}";
            }
            catch(Exception e){
                var message = $"Error in PurviewOut function: {e.Message}";
                _logger.LogError(message);
                return message;
            }
        }
    }
}